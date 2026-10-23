using System.Net.Http.Json;
using System.Collections.Concurrent;
using System.Text.Json;

namespace AlarmPushCenter;

public interface INotifier
{
    Task<int> SendAsync(string message, CancellationToken cancellationToken);
}

public static class NotifierFactory
{
    public static INotifier Create(NotificationTarget target) => target.Channel.ToLowerInvariant() switch
    {
        "telegram" => new TelegramNotifier(target),
        "dingtalk" => new DingTalkNotifier(target),
        "wecom" => new WeComNotifier(target),
        "feishu" => new FeishuNotifier(target),
        "custom_webhook" => new CustomWebhookNotifier(target),
        _ => throw new NotSupportedException($"不支持的通知渠道：{target.Channel}")
    };
}

public abstract class Notifier(NotificationTarget target) : INotifier
{
    protected static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(15) };
    protected NotificationTarget Target { get; } = target;
    public abstract Task<int> SendAsync(string message, CancellationToken cancellationToken);

    protected async Task PostJsonAsync(string url, object body, CancellationToken cancellationToken)
    {
        using var response = await Client.PostAsJsonAsync(url, body, cancellationToken);
        response.EnsureSuccessStatusCode();
        if (Target.Channel != "custom_webhook")
        {
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            ValidateResponse(Target.Channel, document.RootElement);
        }
    }

    internal static void ValidateResponse(string channel, JsonElement response)
    {
        var key = channel.ToLowerInvariant() switch
        {
            "telegram" => "ok", "dingtalk" or "wecom" => "errcode", "feishu" => "code", _ => ""
        };
        if (key.Length == 0) return;
        if (channel == "feishu" && !response.TryGetProperty(key, out _)) key = "StatusCode";
        if (!response.TryGetProperty(key, out var status) ||
            (key == "ok" ? status.ValueKind != JsonValueKind.True : status.ToString() != "0"))
            throw new InvalidOperationException($"{channel} 拒绝了消息（状态：{(status.ValueKind == JsonValueKind.Undefined ? "缺失" : status.ToString())}）。请检查凭据、群机器人限制与消息格式。");
    }

    protected void EnsureWebhook()
    {
        if (string.IsNullOrWhiteSpace(Target.WebhookUrl)) throw new InvalidOperationException($"{Target.Name} 未配置 Webhook URL。");
    }
}

public sealed class TelegramNotifier(NotificationTarget target) : Notifier(target)
{
    public override async Task<int> SendAsync(string message, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(Target.Token)) throw new InvalidOperationException("Telegram 未配置 Bot Token。");
        var recipients = Target.Recipients.Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
        if (recipients.Count == 0) throw new InvalidOperationException("Telegram 未配置目标 Chat ID。");
        foreach (var recipient in recipients)
        {
            var url = $"https://api.telegram.org/bot{Target.Token}/sendMessage";
            if (BotInteraction.ShowButtonsOnAlarm(Target))
                await PostJsonAsync(url, new { chat_id = recipient, text = message, reply_markup = BotInteraction.Keyboard(Target) }, cancellationToken);
            else
                await PostJsonAsync(url, new { chat_id = recipient, text = message }, cancellationToken);
        }
        return recipients.Count;
    }
}

public sealed class DingTalkNotifier : Notifier
{
    private readonly HttpClient _client;
    public DingTalkNotifier(NotificationTarget target) : this(target, Client) { }
    internal DingTalkNotifier(NotificationTarget target, HttpClient client) : base(target) { _client = client; }
    private static readonly ConcurrentDictionary<string, (string Token, DateTime Expires)> Tokens = new();
    private static readonly SemaphoreSlim TokenGate = new(1, 1);

    public override async Task<int> SendAsync(string message, CancellationToken cancellationToken)
    {
        if (Target.Params.Get("ding_message_target") == "users")
        {
            var users = PersonalRecipients();
            if (BotInteraction.ShowButtonsOnAlarm(Target) && !string.IsNullOrWhiteSpace(Target.Params.Get("card_template_id")))
                foreach (var userId in users) await SendCardAsync(message, cancellationToken, userId);
            else await SendApplicationTextAsync(message, cancellationToken);
            return users.Length;
        }
        if (BotInteraction.ShowButtonsOnAlarm(Target))
        {
            await SendCardAsync(message, cancellationToken);
            return 1;
        }
        if (HasApplicationCredentials())
        {
            await SendApplicationTextAsync(message, cancellationToken);
            return 1;
        }
        EnsureWebhook();
        await PostJsonAsync(Target.WebhookUrl, new { msgtype = "text", text = new { content = message } }, cancellationToken);
        return 1;
    }

    private async Task SendCardAsync(string message, CancellationToken token, string? userId = null)
    {
        var chatId = Target.Params.Get("ding_conversation_id");
        if (string.IsNullOrWhiteSpace(Target.Params.Get("card_template_id")) || string.IsNullOrWhiteSpace(Target.Params.Get("bot_client_id")) ||
            string.IsNullOrWhiteSpace(Target.Params.Get("bot_client_secret")) || (userId is null && string.IsNullOrWhiteSpace(chatId)))
        {
            if (HasApplicationCredentials())
            {
                await SendApplicationTextAsync(message + "\n\n提示：未配置互动卡片模板，当前使用普通文本消息。", token);
                return;
            }
            EnsureWebhook();
            await PostJsonAsync(Target.WebhookUrl, new { msgtype = "text", text = new { content = message + "\n\n提示：钉钉群会话 ID 尚未记录，机器人在目标群收到一条消息后会自动切换为互动卡片。" } }, token);
            return;
        }
        var accessToken = await GetAccessTokenAsync(token);
        var id = Guid.NewGuid().ToString("N");
        var buttons = BotInteraction.Commands(Target).Where(x => x.Enabled && !string.IsNullOrWhiteSpace(x.Button)).Take(6)
            .Select(x => new BotButton(x.Button, x.Command, x.Action == "link" ? x.Reply : null)).ToArray();
        var body = new Dictionary<string, object>
        {
            ["cardTemplateId"] = Target.Params.Get("card_template_id"), ["outTrackId"] = id,
            ["cardData"] = new { cardParamMap = EnterpriseBotReceiver.DingCardData(new BotReply(message, buttons)) },
            ["callbackType"] = "STREAM", ["userIdType"] = 1,
            ["openSpaceId"] = userId is null ? "dtv1.card//IM_GROUP." + chatId : "dtv1.card//IM_ROBOT." + userId
        };
        if (userId is null)
        {
            body["imGroupOpenSpaceModel"] = new { supportForward = false };
            body["imGroupOpenDeliverModel"] = new { robotCode = Target.Params.Get("bot_client_id") };
        }
        else
        {
            body["imRobotOpenSpaceModel"] = new { supportForward = false };
            body["imRobotOpenDeliverModel"] = new { spaceType = "IM_ROBOT" };
        }
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.dingtalk.com/v1.0/card/instances/createAndDeliver") { Content = JsonContent.Create(body) };
        request.Headers.Add("x-acs-dingtalk-access-token", accessToken);
        using var response = await _client.SendAsync(request, token);
        if (!response.IsSuccessStatusCode)
            throw new IOException($"钉钉互动卡片发送失败，HTTP {(int)response.StatusCode}。请检查应用权限、卡片模板和群会话 ID。");
        DingCardRegistry.Register(id, new DingCardContext(userId is null ? chatId : "", userId ?? "", userId is null ? "2" : "1", DateTime.UtcNow));
    }

    private bool HasApplicationCredentials() => !string.IsNullOrWhiteSpace(Target.Params.Get("bot_client_id")) ||
        !string.IsNullOrWhiteSpace(Target.Params.Get("bot_client_secret"));

    private string[] PersonalRecipients()
    {
        var users = Target.Recipients.Select(x => x.Trim()).Where(x => x.Length > 0).Distinct().ToArray();
        if (users.Length == 0) throw new InvalidOperationException("请填写接收通知的企业成员 userId（每行一个），不是手机号或 unionId。");
        return users;
    }

    private async Task SendApplicationTextAsync(string message, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(Target.Params.Get("bot_client_id")) || string.IsNullOrWhiteSpace(Target.Params.Get("bot_client_secret")))
            throw new InvalidOperationException("企业应用机器人需要同时填写 Client ID 和 Client Secret。");
        var chatId = Target.Params.Get("ding_conversation_id");
        var personal = Target.Params.Get("ding_message_target") == "users";
        var users = personal ? PersonalRecipients() : [];
        if (!personal && string.IsNullOrWhiteSpace(chatId))
            throw new InvalidOperationException("尚未配置钉钉目标群会话 ID。请填写 openConversationId；或保存配置并启用命令接收后，在目标群 @机器人发送 /status，自动记录群会话 ID，再刷新页面测试。");
        var body = new Dictionary<string, object> { ["robotCode"] = Target.Params.Get("bot_client_id"),
            ["msgKey"] = "sampleText", ["msgParam"] = JsonSerializer.Serialize(new { content = message }) };
        if (personal) body["userIds"] = users;
        else body["openConversationId"] = chatId;
        using var request = new HttpRequestMessage(HttpMethod.Post, personal ? "https://api.dingtalk.com/v1.0/robot/oToMessages/batchSend" : "https://api.dingtalk.com/v1.0/robot/groupMessages/send")
        {
            Content = JsonContent.Create(body)
        };
        request.Headers.Add("x-acs-dingtalk-access-token", await GetAccessTokenAsync(token));
        using var response = await _client.SendAsync(request, token);
        if (!response.IsSuccessStatusCode)
            throw new IOException($"钉钉企业机器人发送失败，HTTP {(int)response.StatusCode}。请检查企业内机器人发送消息权限、应用可见范围，以及 userId 或群会话 ID。");
        using var result = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
        foreach (var field in new[] { "invalidStaffIdList", "flowControlledStaffIdList" })
            if (result.RootElement.TryGetProperty(field, out var failures) && failures.ValueKind == JsonValueKind.Array && failures.GetArrayLength() > 0)
                throw new IOException(field == "invalidStaffIdList" ? "部分 userId 无效或不在应用可见范围内，请核对接收人员。" : "部分接收人员触发钉钉发送限流，请稍后重试。");
        if (!result.RootElement.TryGetProperty("processQueryKey", out var key) || string.IsNullOrWhiteSpace(key.GetString()))
            throw new IOException("钉钉未返回消息发送凭证，请检查应用权限及群会话配置。");
    }

    private async Task<string> GetAccessTokenAsync(CancellationToken token)
    {
        var cacheKey = Target.Params.Get("bot_client_id") + ":" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Target.Params.Get("bot_client_secret"))));
        if (Tokens.TryGetValue(cacheKey, out var cached) && DateTime.UtcNow < cached.Expires) return cached.Token;
        await TokenGate.WaitAsync(token);
        try
        {
            if (Tokens.TryGetValue(cacheKey, out cached) && DateTime.UtcNow < cached.Expires) return cached.Token;
            using var response = await _client.PostAsJsonAsync("https://api.dingtalk.com/v1.0/oauth2/accessToken", new
            { appKey = Target.Params.Get("bot_client_id"), appSecret = Target.Params.Get("bot_client_secret") }, token);
            response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
            var root = document.RootElement;
            var value = root.GetProperty("accessToken").GetString() ?? throw new IOException("钉钉未返回访问令牌。");
            var seconds = root.TryGetProperty("expireIn", out var expiry) && expiry.TryGetInt32(out var parsed) ? parsed : 7200;
            Tokens[cacheKey] = (value, DateTime.UtcNow.AddSeconds(Math.Max(30, seconds - 120)));
            return value;
        }
        finally { TokenGate.Release(); }
    }
}

public sealed class WeComNotifier(NotificationTarget target) : Notifier(target)
{
    public override async Task<int> SendAsync(string message, CancellationToken cancellationToken)
    {
        EnsureWebhook();
        await PostJsonAsync(Target.WebhookUrl, new { msgtype = "text", text = new { content = message } }, cancellationToken);
        return 1;
    }
}

public sealed class FeishuNotifier(NotificationTarget target) : Notifier(target)
{
    public override async Task<int> SendAsync(string message, CancellationToken cancellationToken)
    {
        EnsureWebhook();
        await PostJsonAsync(Target.WebhookUrl, new { msg_type = "text", content = new { text = message } }, cancellationToken);
        return 1;
    }
}

public sealed class CustomWebhookNotifier(NotificationTarget target) : Notifier(target)
{
    public override async Task<int> SendAsync(string message, CancellationToken cancellationToken)
    {
        EnsureWebhook();
        var template = Target.Params.Get("payload_template", "{\"text\":\"{message}\"}");
        var payload = template.Replace("{message}", JsonSerializer.Serialize(message)[1..^1]);
        using var document = JsonDocument.Parse(payload);
        await PostJsonAsync(Target.WebhookUrl, document.RootElement.Clone(), cancellationToken);
        return 1;
    }
}
