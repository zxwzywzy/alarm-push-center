using System.Collections.Concurrent;

namespace AlarmPushCenter;

public sealed class RuntimeHost(ConsoleSettings settings, AlarmCallTracker calls) : BackgroundService
{
    private readonly string _configPath = Environment.GetEnvironmentVariable("ALARM_CONFIG") ?? "/etc/alarm-push-center/config.json";
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentQueue<RuntimeEventView> _events = new();
    private AlarmMonitor? _monitor;
    private BotRuntime? _bots;
    private AlarmHistoryStore? _history;
    private DateTime _started = DateTime.Now;
    private volatile bool _requestedRunning;
    private readonly RuntimeTelemetry _telemetry = new();

    public AppConfig Config { get; private set; } = new AppConfig().Normalize();
    public bool CollectionRunning => _monitor is not null && _requestedRunning;
    public bool UpdateIdle => _monitor?.Metrics() is not { ActiveAlarms: > 0 } && _monitor?.Metrics() is not { QueueDepth: > 0 };

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await _gate.WaitAsync(stoppingToken);
            try
            {
                Config = new ConfigStore(_configPath).Load();
                _history = new AlarmHistoryStore(_configPath);
                if (UpdateResumeState.Consume(_configPath) ?? settings.Value.StartCollectionOnLaunch) await StartRuntimeCoreAsync();
            }
            finally { _gate.Release(); }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        catch (Exception ex) { Log(new RuntimeEvent("error", "服务启动失败：" + ex.GetBaseException().Message)); }
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                _telemetry.Sample(_monitor, Config.Sources.Count(x => x.Enabled));
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    public async Task SaveAndRestartAsync(AppConfig config)
    {
        config = ConfigStore.Clone(config).Normalize();
        ConfigValidator.Validate(config, true);
        await _gate.WaitAsync();
        try
        {
            new ConfigStore(_configPath).Save(config);
            Config = config;
            await StopRuntimeCoreAsync(false);
            if (_requestedRunning) await StartRuntimeCoreAsync();
        }
        finally { _gate.Release(); }
    }

    public async Task RestartAsync()
    {
        await _gate.WaitAsync();
        try
        {
            await StopRuntimeCoreAsync(false);
            await StartRuntimeCoreAsync();
        }
        finally { _gate.Release(); }
    }

    public async Task UpdateSourceAsync(string id, SourceConfig source)
    {
        await _gate.WaitAsync();
        try
        {
            var updated = ConfigStore.Clone(Config);
            var index = updated.Sources.FindIndex(x => x.Id == id);
            if (index < 0 || source.Id != id) throw new InvalidOperationException("采集设备不存在或设备标识不匹配。");
            source.PhoneReminder = updated.Sources[index].PhoneReminder;
            updated.Sources[index] = source;
            updated = updated.Normalize();
            ConfigValidator.Validate(updated, true);
            new ConfigStore(_configPath).Save(updated);
            Config = updated;
            await StopRuntimeCoreAsync(false);
            if (_requestedRunning) await StartRuntimeCoreAsync();
        }
        finally { _gate.Release(); }
    }

    public async Task StartRuntimeAsync()
    {
        await _gate.WaitAsync();
        try { await StartRuntimeCoreAsync(); }
        finally { _gate.Release(); }
    }

    private async Task StartRuntimeCoreAsync()
    {
        if (_monitor is not null) return;
        ConfigValidator.Validate(Config, true);
        _history ??= new AlarmHistoryStore(_configPath);
        calls.Clear();
        var monitor = new AlarmMonitor(Config, Log, history: _history, calls: calls);
        try
        {
            monitor.Start();
            var bots = new BotRuntime(Config, () => _monitor?.StatusText() ?? "服务未运行", () => _history.FormatRecent(10), Log,
                dingContextDiscovered: (targetId, chatId, userId) => { _ = RecordDingConversationAsync(targetId, chatId); },
                historyPage: (source, page) => HistoryPage(source, page), connectedSources: () => _monitor?.ConnectedSourceIds() ?? new HashSet<string>());
            _monitor = monitor;
            _bots = bots;
        }
        catch { await monitor.DisposeAsync(); throw; }
        _requestedRunning = true;
        _started = DateTime.Now;
        Log(new RuntimeEvent("info", "OpenWrt 报警采集服务已启动。"));
    }

    public Task StopRuntimeAsync() => StopRuntimeAsync(true);

    private async Task RecordDingConversationAsync(string targetId, string chatId)
    {
        if (string.IsNullOrWhiteSpace(chatId)) return;
        await _gate.WaitAsync();
        try
        {
            var updated = ConfigStore.Clone(Config);
            var target = updated.Targets.FirstOrDefault(x => x.Id == targetId && x.Channel == "dingtalk");
            if (target is null || !string.IsNullOrWhiteSpace(target.Params.Get("ding_conversation_id"))) return;
            target.Params["ding_conversation_id"] = JsonValues.Element(chatId);
            new ConfigStore(_configPath).Save(updated);
            Config = updated;
            await StopRuntimeCoreAsync(false);
            Log(new RuntimeEvent("info", "已记录钉钉目标群会话 ID，正在刷新报警推送服务。"));
            if (_requestedRunning) await StartRuntimeCoreAsync();
        }
        catch (Exception ex) { Log(new RuntimeEvent("error", "保存钉钉群会话失败：" + ex.GetBaseException().Message)); }
        finally { _gate.Release(); }
    }
    private async Task StopRuntimeAsync(bool explicitStop)
    {
        await _gate.WaitAsync();
        try { await StopRuntimeCoreAsync(explicitStop); }
        finally { _gate.Release(); }
    }

    private async Task StopRuntimeCoreAsync(bool explicitStop)
    {
        calls.End();
        if (explicitStop) _requestedRunning = false;
        var bots = _bots;
        var monitor = _monitor;
        _bots = null;
        _monitor = null;
        try { if (bots is not null) await bots.DisposeAsync(); }
        finally { if (monitor is not null) await monitor.DisposeAsync(); }
        Log(new RuntimeEvent("info", "报警采集服务已停止。"));
    }

    public object Overview()
    {
        var snapshots = _monitor?.ConnectionSnapshots();
        var devices = Config.Sources.Select(source => new
        {
            source.Id, source.Name, source.Protocol, source.Enabled,
            connected = source.Enabled && snapshots?.GetValueOrDefault(source.Id)?.Connected == true,
            state = !source.Enabled ? "disabled" : snapshots?.GetValueOrDefault(source.Id)?.Connected == true ? "online" : "offline",
            connection = snapshots?.GetValueOrDefault(source.Id)
        }).ToArray();
        return new
        {
            running = _monitor is not null && _requestedRunning,
            start_collection_on_launch = settings.Value.StartCollectionOnLaunch,
            uptime_seconds = _monitor is null ? 0 : Math.Max(0, (long)(DateTime.Now - _started).TotalSeconds),
            connected_count = devices.Count(x => x.connected), enabled_count = devices.Count(x => x.Enabled),
            alarm_count = _history?.ReadRecent("", 20).Count(x => x.State == "alarm") ?? 0,
            devices, events = _events.Reverse().Take(30).ToArray(), status_text = _monitor?.StatusText() ?? "服务未运行",
            telemetry = _telemetry.Read(60), sample_seconds = 5
        };
    }

    public async Task<object> TestSourceAsync(string id, CancellationToken token)
    {
        var source = Config.Sources.FirstOrDefault(x => x.Id == id) ?? throw new InvalidOperationException("采集源不存在。");
        return await TestSourceAsync(source, token);
    }

    public async Task<object> TestSourceAsync(SourceConfig source, CancellationToken token)
    {
        source = ConfigStore.Clone(new AppConfig { Sources = [source] }).Sources[0];
        source.Params = ConfigDefaults.MergeSourceParams(source.Protocol, source.Params ?? []);
        source.Enabled = true;
        ConfigValidator.Validate(new AppConfig { Sources = [source], Rules = [], Targets = [] }, true);
        await using var adapter = SourceFactory.Create(source);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        var signals = await adapter.ReadSignalsAsync(timeout.Token);
        return new { ok = true, message = $"连接成功，读取到 {signals.Count} 条有效信号。", signals = signals.Take(10) };
    }

    public async Task<object> TestTargetAsync(string id, CancellationToken token)
    {
        var target = Config.Targets.FirstOrDefault(x => x.Id == id) ?? throw new InvalidOperationException("通知目标不存在。");
        return await TestTargetAsync(target, token);
    }

    public async Task<object> TestTargetAsync(NotificationTarget target, CancellationToken token)
    {
        if (!ConfigDefaults.Channels.Contains(target.Channel, StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException("请选择有效的通知渠道。");
        var sent = await NotifierFactory.Create(target).SendAsync($"【测试消息】工业报警推送中心 OpenWrt\n时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}", token);
        return new { ok = true, message = $"测试消息已发送到 {sent} 个目标。" };
    }

    public object History(string source, int page, int pageSize)
    {
        pageSize = Math.Clamp(pageSize, 1, 20); page = Math.Max(1, page);
        var entries = _history?.ReadRecent(source, 20) ?? [];
        page = Math.Clamp(page, 1, Math.Max(1, (entries.Count + pageSize - 1) / pageSize));
        return new { total = entries.Count, page, page_size = pageSize, items = entries.Skip((page - 1) * pageSize).Take(pageSize) };
    }

    public int DeleteHistory(string source)
    {
        if (_history is null) return 0;
        if (string.IsNullOrWhiteSpace(source)) return _history.DeleteAll();
        return _history.DeleteForSource(source);
    }

    private BotHistoryPage HistoryPage(string source, int page)
    {
        var entries = _history?.ReadRecent(source, 20) ?? [];
        var pages = Math.Max(1, (entries.Count + 9) / 10); page = Math.Clamp(page, 0, pages - 1);
        return new BotHistoryPage(_history?.FormatPage(source, page, 10, 20) ?? "暂无历史报警。", page, pages, entries.Count);
    }

    private void Log(RuntimeEvent evt)
    {
        _events.Enqueue(new RuntimeEventView(DateTime.Now, evt.Level, evt.Message, evt.SourceId, evt.RuleId));
        while (_events.Count > 300) _events.TryDequeue(out _);
        Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{evt.Level}] {evt.Message.Replace('\n', ' ')}");
    }

    public void RecordPhoneEvent(string level, string message, string sourceId) => Log(new(level, message, sourceId));

    public async Task SaveDeviceAsync(string id, DeviceDraft draft)
    {
        if (draft.Source is null || draft.Rules is null) throw new InvalidOperationException("请提交当前设备与其所属报警规则。");
        await _gate.WaitAsync();
        try
        {
            var updated = ConfigStore.Clone(Config);
            var index = updated.Sources.FindIndex(x => x.Id == id);
            if (draft.Source.Id != id || draft.Rules.Any(r => r.SourceId != id))
                throw new InvalidOperationException("设备标识或所属报警规则不匹配。");
            if (index < 0) updated.Sources.Add(draft.Source);
            else updated.Sources[index] = draft.Source;
            updated.Rules.RemoveAll(r => r.SourceId == id);
            updated.Rules.AddRange(draft.Rules);
            updated.Normalize();
            ConfigValidator.Validate(updated, true);
            new ConfigStore(_configPath).Save(updated);
            Config = updated;
            await StopRuntimeCoreAsync(false);
            if (_requestedRunning) await StartRuntimeCoreAsync();
        }
        finally { _gate.Release(); }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        try { await StopRuntimeAsync(); }
        finally { await base.StopAsync(cancellationToken); }
    }
    private sealed record RuntimeEventView(DateTime Time, string Level, string Message, string SourceId, string RuleId);
}

public sealed record DeviceDraft(SourceConfig Source, List<AlarmRule> Rules);
