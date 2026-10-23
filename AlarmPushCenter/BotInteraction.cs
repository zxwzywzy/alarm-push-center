using System.Globalization;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AlarmPushCenter;

public sealed class BotCommandDefinition
{
    public bool Enabled { get; set; } = true;
    public string Command { get; set; } = "/help";
    public string Button { get; set; } = "帮助";
    public string Action { get; set; } = "reply";
    public string Reply { get; set; } = "你好，{user}。";
    public string SourceId { get; set; } = "";
    public int DbNumber { get; set; } = 10;
    public int Address { get; set; }
    public int Bit { get; set; }
    public int Value { get; set; } = 1;
    public string WriteType { get; set; } = "bit";
    public int PulseMs { get; set; }
    public string OpcNodeId { get; set; } = "";
    public string OpcDataType { get; set; } = "Boolean";
    public string OpcValue { get; set; } = "true";
    public string OpcResetValue { get; set; } = "false";
}

public static class BotInteraction
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, PropertyNameCaseInsensitive = true };
    public static bool Enabled(NotificationTarget target) => target.Channel.ToLowerInvariant() is "telegram" or "wecom" or "dingtalk"
        && target.Params.Get("receive_enabled").ToLowerInvariant() is "是" or "true" or "1";
    public static bool ShowButtonsOnAlarm(NotificationTarget target) => target.Params.Get("alarm_show_buttons").ToLowerInvariant() is "是" or "true" or "1";

    public static List<BotCommandDefinition> Commands(NotificationTarget target)
    {
        var raw = target.Params.Get("command_defs").Trim();
        if (raw.Length == 0) return [];
        try
        {
            if (raw.StartsWith('[')) return JsonSerializer.Deserialize<List<BotCommandDefinition>>(raw, Options) ?? [];
            return raw.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line =>
            {
                using var item = JsonDocument.Parse(line);
                if (item.RootElement.TryGetProperty("db_number", out _))
                    throw new InvalidOperationException("检测到旧 PLC 写入指令，请在交互编辑器中重新配置，不能按普通回复执行。");
                return JsonSerializer.Deserialize<BotCommandDefinition>(line, Options)!;
            }).ToList();
        }
        catch (JsonException ex) { throw new InvalidOperationException("机器人命令配置格式无效，请在交互页面检查。", ex); }
    }

    public static string Serialize(IEnumerable<BotCommandDefinition> commands) => JsonSerializer.Serialize(commands, Options);
    public static HashSet<string> Admins(NotificationTarget target) => target.Params.Get("admin_ids")
        .Split(['\r', '\n', ',', '，', ' ', ';'], StringSplitOptions.RemoveEmptyEntries).ToHashSet();

    public static void Validate(NotificationTarget target)
    {
        if (!Enabled(target)) return;
        if (target.Channel == "telegram" && !ValidTelegramToken(target.Token))
            throw new InvalidOperationException($"[{target.Name}] Telegram 机器人令牌格式无效，应为“数字机器人ID:密钥”，请从 BotFather 重新复制完整令牌。");
        if (target.Channel != "telegram" && (string.IsNullOrWhiteSpace(target.Params.Get("bot_client_id")) || string.IsNullOrWhiteSpace(target.Params.Get("bot_client_secret"))))
            throw new InvalidOperationException($"[{target.Name}] 请填写机器人标识和密钥。");
        var admins = Admins(target);
        if (admins.Count == 0 && target.Channel != "dingtalk")
            throw new InvalidOperationException($"[{target.Name}] 请填写允许交互的用户 ID，每行一个；Telegram 使用数字 ID。");
        if (target.Channel == "telegram" && admins.Any(x => !long.TryParse(x, out var id) || id <= 0))
            throw new InvalidOperationException($"[{target.Name}] 请填写允许交互的用户 ID，每行一个；Telegram 使用数字 ID。");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var commands = Commands(target);
        if (!commands.Any(x => x.Enabled)) throw new InvalidOperationException($"[{target.Name}] 双向交互至少需要一个已启用动作。请点击“添加示例菜单”或新增动作。");
        if (commands.Count > 80) throw new InvalidOperationException("每个机器人最多配置 80 条命令。");
        foreach (var command in commands)
        {
            if (!Regex.IsMatch(command.Command, "^/[a-z0-9_]{1,32}$") || !seen.Add(command.Command))
                throw new InvalidOperationException($"命令 {command.Command} 无效或重复。使用 / 加小写英文、数字、下划线。");
            if (command.Action is not ("reply" or "status" or "history" or "menu" or "link" or "plc_write")) throw new InvalidOperationException("不支持的交互动作：" + command.Action);
            if (command.Action == "plc_write" && (string.IsNullOrEmpty(command.SourceId) || command.PulseMs is < 0 or > 5000))
                throw new InvalidOperationException("PLC 写入参数无效：请选择目标设备，脉冲时间应为 0～5000ms。");
            if (command.Reply.Length > 3500 || command.Button.Length > 64) throw new InvalidOperationException("回复最多 3500 字符，按钮标题最多 64 字符。");
            if (command.Action == "link" && (!Uri.TryCreate(command.Reply, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")))
                throw new InvalidOperationException("打开链接动作需要有效的 HTTP/HTTPS 地址。");
        }
    }

    public static bool ValidTelegramToken(string token) => Regex.IsMatch(token.Trim(), "^[0-9]{5,}:[A-Za-z0-9_-]{20,}$");

    public static object? Keyboard(NotificationTarget target)
    {
        if (!Enabled(target)) return null;
        var buttons = Commands(target).Where(x => x.Enabled && !string.IsNullOrWhiteSpace(x.Button)).Select(x =>
            x.Action == "link" ? new Dictionary<string, string> { ["text"] = x.Button, ["url"] = x.Reply }
                : new Dictionary<string, string> { ["text"] = x.Button, ["callback_data"] = x.Command }).ToArray();
        var columns = Math.Clamp(target.Params.GetInt("button_columns", 2), 1, 3);
        return buttons.Length == 0 ? null : new { inline_keyboard = buttons.Chunk(columns).ToArray() };
    }

    public static string Render(string template, string user, string args, string status) => Regex.Replace(template,
        "\\{(user|args|time|status)\\}", match => match.Groups[1].Value switch
        {
            "user" => user, "args" => args, "time" => DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture), _ => status
        });
}

public interface ITelegramApi
{
    Task<JsonElement> CallAsync(string method, object payload, CancellationToken token);
}

public sealed class TelegramApi(string botToken) : ITelegramApi
{
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(35) };
    public async Task<JsonElement> CallAsync(string method, object payload, CancellationToken token)
    {
        using var response = await Client.PostAsJsonAsync($"https://api.telegram.org/bot{botToken}/{method}", payload, token);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"Telegram {method} 请求失败，HTTP {(int)response.StatusCode}。");
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
        Notifier.ValidateResponse("telegram", document.RootElement);
        return document.RootElement.GetProperty("result").Clone();
    }
}

public sealed class TelegramReceiver
{
    private readonly NotificationTarget _target;
    private readonly ITelegramApi _api;
    private readonly Func<string> _status;
    private readonly BotCommandEngine _engine;
    private readonly Action<RuntimeEvent> _log;
    private readonly Dictionary<string, DateTime> _lastRequest = [];
    private string _username = "";
    private long _offset;

    public TelegramReceiver(NotificationTarget target, Func<string> status, Action<RuntimeEvent> log, ITelegramApi? api = null,
        IReadOnlyList<SourceConfig>? sources = null, Func<string>? history = null, Func<string, int, BotHistoryPage>? historyPage = null,
        Func<IReadOnlySet<string>>? connectedSources = null)
    { _target = target; _status = status; _log = log; _api = api ?? new TelegramApi(target.Token); _engine = new BotCommandEngine(target, sources ?? [], status, history: history, historyPage: historyPage, connectedSources: connectedSources); }

    public async Task RunAsync(CancellationToken token)
    {
        BotInteraction.Validate(_target);
        var initialized = false;
        while (!token.IsCancellationRequested)
        {
            try
            {
                if (!initialized)
                {
                    var webhook = await _api.CallAsync("getWebhookInfo", new { }, token);
                    if (webhook.TryGetProperty("url", out var url) && !string.IsNullOrEmpty(url.GetString()))
                        throw new InvalidOperationException("机器人已配置 Webhook，无法同时使用桌面长轮询；请先在原服务移除 Webhook。");
                    var me = await _api.CallAsync("getMe", new { }, token);
                    _username = me.GetProperty("username").GetString() ?? "";
                    // Ignore old pending commands on startup rather than replaying them.
                    var pending = await _api.CallAsync("getUpdates", new { offset = -1, limit = 1, timeout = 0, allowed_updates = new[] { "message", "callback_query" } }, token);
                    foreach (var update in pending.EnumerateArray()) _offset = update.GetProperty("update_id").GetInt64() + 1;
                    initialized = true;
                    _log(new RuntimeEvent("info", $"机器人交互已连接 [{_target.Name}]，命令与按钮可用。"));
                }
                var updates = await _api.CallAsync("getUpdates", new { offset = _offset, timeout = 20, allowed_updates = new[] { "message", "callback_query" } }, token);
                foreach (var update in updates.EnumerateArray())
                {
                    var id = update.GetProperty("update_id").GetInt64();
                    if (id < _offset) continue;
                    _offset = id + 1;
                    try { await HandleUpdateAsync(update, token); }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                    catch (Exception ex) { _log(new RuntimeEvent("error", $"机器人回复失败 [{_target.Name}]：{ex.Message}")); }
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                _log(new RuntimeEvent("error", $"机器人连接失败 [{_target.Name}]：{ex.Message}"));
                await Task.Delay(5000, token);
            }
        }
    }

    internal async Task HandleUpdateAsync(JsonElement update, CancellationToken token)
    {
        var callback = update.TryGetProperty("callback_query", out var query);
        var message = callback ? (query.TryGetProperty("message", out var attached) ? attached : default)
            : (update.TryGetProperty("message", out var normal) ? normal : default);
        var origin = callback ? query : message;
        if (origin.ValueKind != JsonValueKind.Object || !origin.TryGetProperty("from", out var sender)) return;
        var senderId = sender.GetProperty("id").ToString();
        if (!BotInteraction.Admins(_target).Contains(senderId))
        {
            if (callback) await AnswerAsync(query, "你没有此机器人的交互权限。", token);
            return;
        }
        if (message.ValueKind != JsonValueKind.Object || !message.TryGetProperty("chat", out var chat)) return;
        var text = callback ? query.GetProperty("data").GetString() ?? ""
            : message.TryGetProperty("text", out var content) ? content.GetString() ?? "" : "";
        if (string.IsNullOrWhiteSpace(text)) return;
        var parts = text.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var nameParts = parts[0].Split('@', 2);
        if (nameParts.Length == 2 && !string.Equals(nameParts[1], _username, StringComparison.OrdinalIgnoreCase)) return;
        if (_lastRequest.TryGetValue(senderId, out var last) && DateTime.UtcNow - last < TimeSpan.FromMilliseconds(500))
        { if (callback) await AnswerAsync(query, "操作太快，请稍后再试。", token); return; }
        _lastRequest[senderId] = DateTime.UtcNow;
        if (callback) await AnswerAsync(query, "正在处理…", token);
        var user = sender.TryGetProperty("first_name", out var first) ? first.GetString() ?? senderId : senderId;
        var eventId = callback ? query.GetProperty("id").GetString() ?? "" : update.GetProperty("update_id").ToString();
        var reply = await _engine.ExecuteAsync(senderId, user, chat.GetProperty("id").ToString(),
            nameParts[0] + (parts.Length > 1 ? " " + parts[1] : ""), eventId, token);
        if (reply is null) return;
        var buttons = reply.Buttons.Select(x => x.Url is null
            ? new Dictionary<string, string> { ["text"] = x.Text, ["callback_data"] = x.Command }
            : new Dictionary<string, string> { ["text"] = x.Text, ["url"] = x.Url });
        object? keyboard = reply.Buttons.Count == 0 ? null : new { inline_keyboard = buttons.Chunk(Math.Clamp(_target.Params.GetInt("button_columns", 2), 1, 3)).ToArray() };
        await _api.CallAsync("sendMessage", new { chat_id = chat.GetProperty("id").ToString(), text = reply.Text, reply_markup = keyboard }, token);
        _log(new RuntimeEvent("info", $"机器人交互 [{_target.Name}] 用户 {senderId} 执行 {nameParts[0]}。"));
    }

    private Task<JsonElement> AnswerAsync(JsonElement query, string text, CancellationToken token) => _api.CallAsync("answerCallbackQuery",
        new { callback_query_id = query.GetProperty("id").GetString(), text }, token);
}

