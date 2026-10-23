using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace AlarmPushCenter;

public sealed record BotButton(string Text, string Command, string? Url = null);
public sealed record BotReply(string Text, IReadOnlyList<BotButton> Buttons);
public sealed record BotHistoryPage(string Text, int Page, int PageCount, int Total);

public sealed class BotCommandEngine(NotificationTarget target, IReadOnlyList<SourceConfig> sources, Func<string> status,
    Func<SourceConfig, BotCommandDefinition, CancellationToken, Task>? write = null, Func<string>? history = null,
    Func<string, int, BotHistoryPage>? historyPage = null, Func<IReadOnlySet<string>>? connectedSources = null)
{
    private readonly ConcurrentDictionary<string, PendingWrite> _pending = new();
    private readonly ConcurrentDictionary<string, DateTime> _seen = new();
    private readonly Func<SourceConfig, BotCommandDefinition, CancellationToken, Task> _write = write ?? PlcCommandWriter.WriteAsync;
    private readonly Func<string> _history = history ?? (() => "暂无历史报警。");
    private readonly Func<string, int, BotHistoryPage>? _historyPage = historyPage;
    private readonly Func<IReadOnlySet<string>> _connectedSources = connectedSources ?? (() => sources.Where(x => x.Enabled).Select(x => x.Id).ToHashSet(StringComparer.OrdinalIgnoreCase));
    private sealed record PendingWrite(string UserId, string ChatId, BotCommandDefinition Command, DateTime Expires);

    public IReadOnlyList<BotButton> Menu(int page = 0)
    {
        var buttons = BotInteraction.Commands(target).Where(x => x.Enabled && x.Button.Length > 0)
            .Select(x => new BotButton(x.Button, x.Command, x.Action == "link" ? x.Reply : null)).ToArray();
        if (buttons.Length <= 6) return buttons;
        page = Math.Clamp(page, 0, (buttons.Length - 1) / 4);
        var result = buttons.Skip(page * 4).Take(4).ToList();
        if (page > 0) result.Add(new BotButton("上一页", "/page " + (page - 1)));
        if ((page + 1) * 4 < buttons.Length) result.Add(new BotButton("下一页", "/page " + (page + 1)));
        return result;
    }

    private static IReadOnlyList<BotButton> BackToMenu() => [new BotButton("返回菜单", "/menu")];

    public async Task<BotReply?> ExecuteAsync(string userId, string userName, string chatId, string text, string eventId, CancellationToken token)
    {
        if (!BotInteraction.Admins(target).Contains(userId)) return null;
        var now = DateTime.UtcNow;
        foreach (var pair in _pending.Where(x => x.Value.Expires < now)) _pending.TryRemove(pair.Key, out _);
        foreach (var pair in _seen.Where(x => now - x.Value > TimeSpan.FromHours(1))) _seen.TryRemove(pair.Key, out _);
        if (eventId.Length > 0 && !_seen.TryAdd(eventId, now)) return null;
        if (_seen.Count > 10000) return new BotReply("请求过多，请稍后再试。", []);
        var parts = text.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return null;
        var commandName = parts[0].ToLowerInvariant();
        if (commandName == "/page") return new BotReply("请选择功能。", Menu(parts.Length > 1 && int.TryParse(parts[1], out var page) ? page : 0));
        if (commandName == "/history_device")
        {
            var arguments = parts.Length > 1 ? parts[1].Split(' ', StringSplitOptions.RemoveEmptyEntries) : [];
            var source = arguments.Length > 0 ? sources.FirstOrDefault(x => x.Id.Equals(arguments[0], StringComparison.OrdinalIgnoreCase)) : null;
            if (source is null) return new BotReply("设备不存在，请重新选择。", HistoryDeviceButtons());
            var requestedPage = arguments.Length > 1 && int.TryParse(arguments[1], out var parsedPage) ? parsedPage : 0;
            if (_historyPage is null) return new BotReply(_history(), BackToMenu());
            var result = _historyPage(source.Name, requestedPage);
            var historyButtons = new List<BotButton>();
            if (result.Page > 0) historyButtons.Add(new BotButton("上一页", $"/history_device {source.Id} {result.Page - 1}"));
            if (result.Page + 1 < result.PageCount) historyButtons.Add(new BotButton("下一页", $"/history_device {source.Id} {result.Page + 1}"));
            historyButtons.Add(new BotButton("重新选择设备", "/history_devices"));
            historyButtons.Add(new BotButton("返回菜单", "/menu"));
            return new BotReply(result.Text, historyButtons);
        }
        if (commandName == "/history_devices") return new BotReply("请从当前已连接设备中选择一台，确认后再读取它的历史报警。", HistoryDeviceButtons());
        if (commandName is "/confirm" or "/cancel")
        {
            if (parts.Length < 2 || !_pending.TryGetValue(parts[1], out var pending) || pending.UserId != userId || pending.ChatId != chatId)
                return new BotReply("确认已失效，或不属于当前用户/会话。请重新选择动作。", BackToMenu());
            if (!_pending.TryRemove(parts[1], out pending)) return new BotReply("此操作已经处理。", BackToMenu());
            if (commandName == "/cancel") return new BotReply("操作已取消。", BackToMenu());
            var source = sources.FirstOrDefault(x => x.Id == pending.Command.SourceId);
            if (source is null || !source.Enabled) return new BotReply("设备不存在或已禁用。", BackToMenu());
            try
            {
                await _write(source, pending.Command, token);
                var content = string.IsNullOrWhiteSpace(pending.Command.Reply) ? "PLC 写入完成。" : pending.Command.Reply;
                return new BotReply(BotInteraction.Render(content, userName, "", status()), BackToMenu());
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !token.IsCancellationRequested)
            { return new BotReply($"PLC 写入失败或结果未确认：{ex.Message}。请核对设备状态后再操作。", BackToMenu()); }
        }
        var definition = BotInteraction.Commands(target).FirstOrDefault(x => x.Enabled && x.Command == commandName);
        if (definition?.Action == "plc_write")
        {
            if (target.Params.Get("allow_plc_write") != "是") return new BotReply("当前机器人未启用 PLC 控制权限。", BackToMenu());
            var source = sources.FirstOrDefault(x => x.Id == definition.SourceId);
            if (source is null || !source.Enabled) return new BotReply("设备不存在或已禁用。", BackToMenu());
            var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
            // Keep one outstanding operation per user and conversation.
            foreach (var item in _pending.Where(x => x.Value.UserId == userId && x.Value.ChatId == chatId)) _pending.TryRemove(item.Key, out _);
            _pending[nonce] = new PendingWrite(userId, chatId, definition, now.AddSeconds(60));
            var address = source.Protocol switch
            {
                "s7" => $"DB{definition.DbNumber}.DBX{definition.Address}.{definition.Bit} → {definition.Value}",
                "opcua" => $"节点 {definition.OpcNodeId}\n{definition.OpcDataType} → {definition.OpcValue}",
                _ => $"{definition.WriteType} 地址 {definition.Address} → {definition.Value}"
            };
            var reset = source.Protocol == "opcua" && definition.PulseMs > 0 ? $"，恢复值 {definition.OpcResetValue}" : "";
            return new BotReply($"确认控制设备「{source.Name}」\n{address}\n脉冲：{definition.PulseMs}ms{reset}\n60 秒内确认，过期需重新发起。",
                [new BotButton("确认执行", "/confirm " + nonce), new BotButton("取消", "/cancel " + nonce)]);
        }
        if (definition?.Action == "history")
            return _historyPage is null ? new BotReply(_history(), BackToMenu())
                : new BotReply("请从当前已连接设备中选择一台，确认后再读取它的历史报警。", HistoryDeviceButtons());
        var template = definition?.Reply ?? (commandName is "/start" or "/menu" ? target.Params.Get("welcome_text", "你好，{user}。请选择下方功能。")
            : target.Params.Get("unknown_reply", "暂不支持该指令，请选择菜单。"));
        if (definition?.Action == "status" && !template.Contains("{status}")) template += "\n{status}";
        var reply = BotInteraction.Render(template, userName, parts.Length > 1 ? parts[1] : "", status());
        var buttons = commandName is "/start" or "/menu" || definition?.Action == "menu" ? Menu() : BackToMenu();
        return new BotReply(reply.Length == 0 ? "请选择下方功能。" : reply[..Math.Min(reply.Length, 4000)], buttons);
    }

    private IReadOnlyList<BotButton> HistoryDeviceButtons()
    {
        var connected = _connectedSources();
        var buttons = sources.Where(x => x.Enabled && connected.Contains(x.Id))
            .Select(x => new BotButton("🟢 " + x.Name, "/history_device " + x.Id + " 0")).ToList();
        if (buttons.Count == 0) buttons.Add(new BotButton("刷新连接设备", "/history_devices"));
        buttons.Add(new BotButton("返回菜单", "/menu"));
        return buttons;
    }
}
