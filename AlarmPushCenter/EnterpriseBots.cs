using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace AlarmPushCenter;

internal static class BotJson
{
    public static string Text(this JsonElement value, string name) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var item) ? item.ToString() : "";
    public static JsonElement Item(this JsonElement value, string name) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var item) ? item : default;
    public static JsonElement Parse(string value) { using var doc = JsonDocument.Parse(value); return doc.RootElement.Clone(); }
}

internal sealed record DingCardContext(string ChatId, string UserId, string ConversationType, DateTime Created);

internal static class DingCardRegistry
{
    private static readonly ConcurrentDictionary<string, DingCardContext> Cards = new();

    public static void Register(string id, DingCardContext context)
    {
        Cards[id] = context;
        foreach (var item in Cards.Where(x => DateTime.UtcNow - x.Value.Created > TimeSpan.FromHours(24)).ToArray())
            Cards.TryRemove(item.Key, out _);
    }

    public static bool TryGet(string id, out DingCardContext context) => Cards.TryGetValue(id, out context!);
}

internal sealed class BotSocket : IDisposable
{
    private readonly ClientWebSocket _socket = new();
    private readonly SemaphoreSlim _send = new(1, 1);
    public async Task ConnectAsync(Uri uri, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        _socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        await _socket.ConnectAsync(uri, timeout.Token);
    }
    public async Task SendAsync(object message, CancellationToken token)
    {
        await _send.WaitAsync(token);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            await _socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(message).AsMemory(), WebSocketMessageType.Text, true, timeout.Token);
        }
        finally { _send.Release(); }
    }
    public async Task<JsonElement> ReceiveAsync(CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(70));
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        while (true)
        {
            var result = await _socket.ReceiveAsync(chunk.AsMemory(), timeout.Token);
            if (result.MessageType == WebSocketMessageType.Close) throw new IOException("机器人连接已关闭。");
            buffer.Write(chunk, 0, result.Count);
            if (buffer.Length > 4 * 1024 * 1024) throw new IOException("机器人消息超过 4MB 限制。");
            if (result.EndOfMessage) return BotJson.Parse(Encoding.UTF8.GetString(buffer.ToArray()));
        }
    }
    public void Dispose() { _socket.Abort(); _socket.Dispose(); _send.Dispose(); }
}

public sealed class EnterpriseBotReceiver
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private readonly NotificationTarget _target;
    private readonly BotCommandEngine _engine;
    private readonly Action<RuntimeEvent> _log;
    private readonly Action<string, string, string>? _dingContextDiscovered;
    private string _accessToken = "";
    private DateTime _tokenExpires;

    public EnterpriseBotReceiver(NotificationTarget target, IReadOnlyList<SourceConfig> sources, Func<string> status, Func<string> history,
        Action<RuntimeEvent> log, Action<string, string, string>? dingContextDiscovered = null,
        Func<string, int, BotHistoryPage>? historyPage = null, Func<IReadOnlySet<string>>? connectedSources = null)
    { _target = target; _engine = new BotCommandEngine(target, sources, status, history: history, historyPage: historyPage, connectedSources: connectedSources); _log = log; _dingContextDiscovered = dingContextDiscovered; }

    public async Task RunAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try { await RunConnectionAsync(token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                _log(new RuntimeEvent("error", $"机器人连接异常 [{_target.Name}]：{ex.Message}；5 秒后重连。"));
                await Task.Delay(5000, token);
            }
        }
    }

    private async Task RunConnectionAsync(CancellationToken token)
    {
        using var socket = new BotSocket();
        using var session = CancellationTokenSource.CreateLinkedTokenSource(token);
        var ct = session.Token;
        if (_target.Channel == "wecom")
        {
            await socket.ConnectAsync(new Uri("wss://openws.work.weixin.qq.com"), ct);
            var reqId = Guid.NewGuid().ToString("N");
            await socket.SendAsync(new { cmd = "aibot_subscribe", headers = new { req_id = reqId }, body = new { bot_id = _target.Params.Get("bot_client_id"), secret = _target.Params.Get("bot_client_secret") } }, ct);
            var auth = await socket.ReceiveAsync(ct);
            if (auth.Text("errcode") != "0" || auth.Item("headers").Text("req_id") != reqId) throw new IOException("企业微信机器人认证失败，请检查 Bot ID / Secret。");
        }
        else
        {
            var connection = await PostAsync("https://api.dingtalk.com/v1.0/gateway/connections/open", new
            {
                clientId = _target.Params.Get("bot_client_id"), clientSecret = _target.Params.Get("bot_client_secret"),
                subscriptions = new[] { new { type = "CALLBACK", topic = "/v1.0/im/bot/messages/get" }, new { type = "CALLBACK", topic = "/v1.0/card/instances/callback" } }, ua = "AlarmPushCenter/2.0"
            }, ct);
            var endpoint = new Uri(connection.Text("endpoint"));
            if (endpoint.Scheme != "wss" || !endpoint.Host.EndsWith(".dingtalk.com", StringComparison.OrdinalIgnoreCase)) throw new IOException("钉钉返回了无效连接地址。");
            await socket.ConnectAsync(new Uri(endpoint + (endpoint.Query.Length == 0 ? "?" : "&") + "ticket=" + Uri.EscapeDataString(connection.Text("ticket"))), ct);
        }
        _log(new RuntimeEvent("info", $"机器人交互已连接 [{_target.Name}]。"));
        var queue = Channel.CreateBounded<JsonElement>(100);
        async Task Receive()
        {
            while (!ct.IsCancellationRequested)
            {
                var frame = await socket.ReceiveAsync(ct);
                if (_target.Channel == "wecom")
                {
                    if (frame.TryGetProperty("errcode", out var error))
                    {
                        if (error.ToString() != "0") _log(new RuntimeEvent("error", $"企业微信返回错误码 {error}。"));
                        continue;
                    }
                    if (frame.Text("cmd") is "aibot_msg_callback" or "aibot_event_callback") await queue.Writer.WriteAsync(frame, ct);
                }
                else
                {
                    var headers = frame.Item("headers");
                    if (frame.Text("type") == "SYSTEM" && headers.Text("topic") == "disconnect") throw new IOException("钉钉要求重新连接。");
                    await socket.SendAsync(DingAck(headers.Text("messageId"), frame.Text("type") == "SYSTEM" ? frame.Text("data") : "{\"response\":null}"), ct);
                    if (frame.Text("type") == "CALLBACK") await queue.Writer.WriteAsync(frame, ct);
                }
            }
        }
        async Task Process()
        {
            await foreach (var frame in queue.Reader.ReadAllAsync(ct))
            {
                try
                {
                    if (_target.Channel == "wecom") await HandleWecomAsync(socket, frame, ct);
                    else await HandleDingAsync(frame, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex) { _log(new RuntimeEvent("error", $"交互处理失败 [{_target.Name}]：{ex.Message}")); }
            }
        }
        async Task Heartbeat()
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(25000, ct);
                if (_target.Channel == "wecom") await socket.SendAsync(new { cmd = "ping", headers = new { req_id = Guid.NewGuid().ToString("N") } }, ct);
            }
        }
        var jobs = new[] { Receive(), Process(), Heartbeat() };
        try { await await Task.WhenAny(jobs); }
        finally { session.Cancel(); try { await Task.WhenAll(jobs); } catch (OperationCanceledException) { } }
    }

    private async Task HandleWecomAsync(BotSocket socket, JsonElement frame, CancellationToken token)
    {
        var body = frame.Item("body");
        var userId = body.Item("from").Text("userid");
        if (!BotInteraction.Admins(_target).Contains(userId)) return;
        var chatId = body.Text("chatid"); if (chatId.Length == 0) chatId = userId;
        var kind = body.Item("event").Text("eventtype");
        var text = body.Item("text").Text("content");
        if (kind == "enter_chat") text = "/start";
        if (kind == "template_card_event") text = body.Item("event").Text("event_key");
        var reqId = frame.Item("headers").Text("req_id");
        if (kind == "template_card_event")
            await socket.SendAsync(new { cmd = "aibot_respond_update_msg", headers = new { req_id = reqId }, body = new
            { response_type = "update_template_card", userids = new[] { userId }, template_card = new { card_type = "text_notice", task_id = body.Item("event").Text("task_id"), main_title = new { title = "正在处理操作…" } } } }, token);
        var result = await _engine.ExecuteAsync(userId, userId, chatId, text, body.Text("msgid"), token);
        if (result is null) return;
        if (kind == "template_card_event")
        {
            await socket.SendAsync(new { cmd = "aibot_send_msg", headers = new { req_id = Guid.NewGuid().ToString("N") }, body = new { chatid = chatId, msgtype = "markdown", markdown = new { content = result.Text } } }, token);
            await socket.SendAsync(new { cmd = "aibot_send_msg", headers = new { req_id = Guid.NewGuid().ToString("N") }, body = new { chatid = chatId, msgtype = "template_card", template_card = WecomCard(result) } }, token);
        }
        else
            await socket.SendAsync(new { cmd = kind == "enter_chat" ? "aibot_respond_welcome_msg" : "aibot_respond_msg", headers = new { req_id = reqId }, body = new
            { msgtype = "stream_with_template_card", stream = new { id = Guid.NewGuid().ToString("N"), finish = true, content = result.Text }, template_card = WecomCard(result) } }, token);
        _log(new RuntimeEvent("info", $"企业微信交互：用户 {userId}，{text.Split(' ')[0]}。"));
    }

    internal static object WecomCard(BotReply reply) => new
    {
        card_type = "button_interaction", task_id = Guid.NewGuid().ToString("N"), main_title = new { title = "设备操作中心", desc = "选择下面的功能" },
        button_list = reply.Buttons.Take(6).Select(x => new { text = x.Text[..Math.Min(x.Text.Length, 20)], key = x.Command, style = x.Command.StartsWith("/confirm") ? 1 : 2 }).ToArray()
    };
    internal static object DingAck(string messageId, string data) => new { code = 200, message = "OK", headers = new { messageId, contentType = "application/json" }, data };

    private async Task HandleDingAsync(JsonElement frame, CancellationToken token)
    {
        var body = BotJson.Parse(frame.Text("data"));
        var topic = frame.Item("headers").Text("topic");
        var eventId = frame.Item("headers").Text("messageId");
        DingCardContext context;
        string userId, text;
        if (topic == "/v1.0/card/instances/callback")
        {
            if (!DingCardRegistry.TryGet(body.Text("outTrackId"), out context!)) return;
            userId = body.Text("userId");
            var content = body.Item("content");
            if (content.ValueKind == JsonValueKind.String) content = BotJson.Parse(content.GetString()!);
            text = FindCommand(content);
        }
        else
        {
            userId = body.Text("senderStaffId");
            if (userId.Length == 0) userId = body.Text("senderId");
            text = body.Item("text").Text("content").Trim();
            context = new DingCardContext(body.Text("conversationId"), userId, body.Text("conversationType"), DateTime.UtcNow);
            if (context.ConversationType == "2" && !string.IsNullOrWhiteSpace(context.ChatId))
            {
                if (string.IsNullOrWhiteSpace(_target.Params.Get("ding_conversation_id")))
                    _target.Params["ding_conversation_id"] = JsonValues.Element(context.ChatId);
                if (string.IsNullOrWhiteSpace(_target.Params.Get("admin_ids")) && !string.IsNullOrWhiteSpace(userId))
                    _target.Params["admin_ids"] = JsonValues.Element(userId);
                _dingContextDiscovered?.Invoke(_target.Id, context.ChatId, userId);
            }
        }
        var result = await _engine.ExecuteAsync(userId, body.Text("senderNick"), context.ChatId, text, eventId, token);
        if (result is null) return;
        if (string.IsNullOrWhiteSpace(_target.Params.Get("card_template_id")))
        {
            var webhook = body.Text("sessionWebhook");
            if (!Uri.TryCreate(webhook, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.Host != "oapi.dingtalk.com") throw new IOException("缺少有效的会话 Webhook，请配置卡片模板。");
            var plain = result.Text + "\n\n" + string.Join("\n", result.Buttons.Select(x => $"{x.Text}：{x.Command}"));
            var response = await PostAsync(webhook, new { msgtype = "text", text = new { content = plain } }, token);
            Notifier.ValidateResponse("dingtalk", response);
        }
        else await SendDingCardAsync(context, result, token);
        _log(new RuntimeEvent("info", $"钉钉交互：用户 {userId}，{text.Split(' ')[0]}。"));
    }

    internal static string FindCommand(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            if (value.TryGetProperty("command", out var command)) return command.ToString();
            foreach (var item in value.EnumerateObject()) { var result = FindCommand(item.Value); if (result.Length > 0) return result; }
        }
        return "";
    }

    internal static Dictionary<string, string> DingCardData(BotReply reply)
    {
        var data = new Dictionary<string, string> { ["title"] = "设备操作中心", ["content"] = reply.Text };
        for (var i = 0; i < 6; i++)
        {
            data["button_" + (i + 1)] = i < reply.Buttons.Count ? reply.Buttons[i].Text : "";
            data["command_" + (i + 1)] = i < reply.Buttons.Count ? reply.Buttons[i].Command : "";
        }
        return data;
    }

    private async Task SendDingCardAsync(DingCardContext context, BotReply reply, CancellationToken token)
    {
        if (DateTime.UtcNow >= _tokenExpires)
        {
            var result = await PostAsync("https://api.dingtalk.com/v1.0/oauth2/accessToken", new { appKey = _target.Params.Get("bot_client_id"), appSecret = _target.Params.Get("bot_client_secret") }, token);
            _accessToken = result.Text("accessToken");
            _tokenExpires = DateTime.UtcNow.AddSeconds(Math.Max(30, int.Parse(result.Text("expireIn")) - 120));
        }
        var id = Guid.NewGuid().ToString("N");
        var body = new Dictionary<string, object>
        {
            ["cardTemplateId"] = _target.Params.Get("card_template_id"), ["outTrackId"] = id,
            ["cardData"] = new { cardParamMap = DingCardData(reply) }, ["callbackType"] = "STREAM", ["userIdType"] = 1,
            ["imGroupOpenSpaceModel"] = new { supportForward = false }, ["imRobotOpenSpaceModel"] = new { supportForward = false },
            ["openSpaceId"] = context.ConversationType == "2" ? "dtv1.card//IM_GROUP." + context.ChatId : "dtv1.card//IM_ROBOT." + context.UserId
        };
        if (context.ConversationType == "2") body["imGroupOpenDeliverModel"] = new { robotCode = _target.Params.Get("bot_client_id") };
        else body["imRobotOpenDeliverModel"] = new { spaceType = "IM_ROBOT" };
        await PostAsync("https://api.dingtalk.com/v1.0/card/instances/createAndDeliver", body, token, _accessToken);
        DingCardRegistry.Register(id, context with { Created = DateTime.UtcNow });
    }

    private static async Task<JsonElement> PostAsync(string url, object body, CancellationToken token, string? accessToken = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body) };
        if (accessToken is not null) request.Headers.Add("x-acs-dingtalk-access-token", accessToken);
        using var response = await Http.SendAsync(request, token);
        if (!response.IsSuccessStatusCode) throw new IOException($"平台接口调用失败，HTTP {(int)response.StatusCode}。请检查机器人凭据和应用权限。");
        return BotJson.Parse(await response.Content.ReadAsStringAsync(token));
    }
}
