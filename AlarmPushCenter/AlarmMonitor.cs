using System.Diagnostics;
using System.Collections.Concurrent;
using System.Threading.Channels;

namespace AlarmPushCenter;

public sealed class AlarmMonitor : IAsyncDisposable
{
    private readonly AppConfig _config;
    private readonly Action<RuntimeEvent> _onEvent;
    private readonly Func<SourceConfig, ISourceAdapter> _buildSource;
    private readonly Func<NotificationTarget, INotifier> _buildNotifier;
    private CancellationTokenSource? _stopSource;
    private Task? _runTask;
    private List<NotificationLane> _lanes = [];
    private readonly SemaphoreSlim _stopGate = new(1, 1);
    private readonly AlarmHistoryStore? _history;
    private readonly AlarmCallTracker? _calls;
    private readonly ConcurrentDictionary<string, ConnectionState> _connections = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _startedAt;
    private long _reads, _failedReads, _readMicroseconds, _alarmEvents, _recoveryEvents, _sentDeliveries, _failedDeliveries;

    public AlarmMonitor(AppConfig config, Action<RuntimeEvent> onEvent,
        Func<SourceConfig, ISourceAdapter>? buildSource = null,
        Func<NotificationTarget, INotifier>? buildNotifier = null,
        AlarmHistoryStore? history = null, AlarmCallTracker? calls = null)
    {
        _config = ConfigStore.Clone(config);
        _config.Targets = _config.Targets.SelectMany(target =>
        {
            if (target.Channel != "telegram") return new[] { target };
            return target.Recipients.Distinct().Select(recipient => new NotificationTarget
            {
                Id = target.Id, Name = target.Name, Channel = target.Channel, Enabled = target.Enabled,
                Token = target.Token, Recipients = [recipient], Params = target.Params
            });
        }).ToList();
        _onEvent = onEvent;
        _buildSource = buildSource ?? SourceFactory.Create;
        _buildNotifier = buildNotifier ?? NotifierFactory.Create;
        _history = history;
        _calls = calls;
    }

    public bool IsRunning => _runTask is { IsCompleted: false };

    public void Start()
    {
        if (_runTask is not null) return;
        _stopSource = new CancellationTokenSource();
        _startedAt = DateTime.Now;
        _calls?.Begin();
        _connections.Clear();
        foreach (var source in _config.Sources.Where(x => x.Enabled)) _connections[source.Id] = new ConnectionState();
        var token = _stopSource.Token;
        _lanes = _config.Targets.Where(x => x.Enabled).Select(x => new NotificationLane(x)).ToList();
        var tasks = _config.Sources.Where(x => x.Enabled)
            .Select(source => Task.Run(() => RunSourceAsync(source, token)))
            .Concat(_lanes.Select(lane => Task.Run(() => RunNotificationsAsync(lane, token))));
        _runTask = Task.WhenAll(tasks);
    }

    public async Task StopAsync()
    {
        await _stopGate.WaitAsync().ConfigureAwait(false);
        try
        {
        if (_stopSource is null) return;
        _stopSource.Cancel();
        _calls?.End();
        try { if (_runTask is not null) await _runTask.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        finally
        {
            _stopSource.Dispose();
            _stopSource = null;
            _runTask = null;
        }
        }
        finally { _stopGate.Release(); }
    }

    private async Task RunSourceAsync(SourceConfig source, CancellationToken token)
    {
        ISourceAdapter? adapter = null;
        var rules = _config.Rules.Where(x => x.Enabled && x.SourceId == source.Id).ToList();
        var states = rules.ToDictionary(x => x.Id, _ => new RuleState());
        var interval = TimeSpan.FromSeconds(Math.Clamp(source.PollSeconds, 0.2, 3600));
        var connectionUp = false;
        long lastHeartbeat = 0, lastFailureLog = 0;
        string? lastFailure = null;
        try
        {
            while (!token.IsCancellationRequested)
            {
                var started = Stopwatch.GetTimestamp();
                var readSucceeded = false;
                try
                {
                    IReadOnlyList<Signal> signals;
                    var gate = PlcCommandWriter.Gate(source.Id);
                    await gate.WaitAsync(token).ConfigureAwait(false);
                    try
                    {
                        adapter ??= _buildSource(source);
                        PlcCommandWriter.Register(source.Id, adapter);
                        signals = await adapter.ReadSignalsAsync(token).ConfigureAwait(false);
                    }
                    finally { gate.Release(); }
                    readSucceeded = true;
                    ObserveRead(started, false);
                    if (!connectionUp)
                    {
                        Log("connected", source.Protocol == "opcua"
                            ? $"OPC UA 连接成功 [{source.Name}]，订阅已发布且两个报警节点数据质量正常。"
                            : source.Protocol == "opcua_prodiag"
                            ? $"OPC UA ProDiag 连接成功 [{source.Name}]，Alarms & Conditions 事件订阅已发布。"
                            : $"采集连接成功 [{source.Name}]。", source.Id);
                    }
                    connectionUp = true;
                    lastFailure = null;
                    var connection = _connections.GetOrAdd(source.Id, _ => new ConnectionState());
                    lock (connection)
                    {
                        if (!connection.Connected) connection.ConnectedSince = DateTime.Now;
                        connection.Connected = true; connection.LastSuccess = DateTime.Now; connection.ActiveAlarmCount = signals.Count(x => x.Active);
                        connection.Detail = "心跳正常";
                    }
                    if (lastHeartbeat == 0 || Stopwatch.GetElapsedTime(lastHeartbeat).TotalSeconds >= 5)
                    {
                        Log("heartbeat", $"[{source.Name}] 心跳正常，最近采集成功；当前有效报警 {signals.Count} 条。", source.Id);
                        lastHeartbeat = Stopwatch.GetTimestamp();
                    }
                    foreach (var rule in rules)
                        Process(source, rule, states[rule.Id], signals);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
                catch (Exception ex)
                {
                    if (!readSucceeded) ObserveRead(started, true);
                    var failure = ex.GetBaseException().Message;
                    if (connectionUp || !string.Equals(lastFailure, failure, StringComparison.Ordinal) || lastFailureLog == 0 || Stopwatch.GetElapsedTime(lastFailureLog).TotalSeconds >= 30)
                    {
                        Log("disconnected", $"连接中断 [{source.Name}] {failure}；稍后重连。", source.Id);
                        lastFailureLog = Stopwatch.GetTimestamp();
                    }
                    connectionUp = false;
                    _calls?.Clear(source.Id);
                    lastFailure = failure;
                    var connection = _connections.GetOrAdd(source.Id, _ => new ConnectionState());
                    lock (connection) { connection.Connected = false; connection.Detail = failure; }
                    await CloseAdapterAsync(adapter, source.Id).ConfigureAwait(false);
                    adapter = null;
                }
                var remaining = interval - Stopwatch.GetElapsedTime(started);
                await Task.Delay(remaining > TimeSpan.Zero ? remaining : TimeSpan.FromMilliseconds(20), token).ConfigureAwait(false);
            }
        }
        finally { await CloseAdapterAsync(adapter, source.Id).ConfigureAwait(false); }
    }

    private async Task CloseAdapterAsync(ISourceAdapter? adapter, string sourceId)
    {
        if (adapter is null) return;
        var gate = PlcCommandWriter.Gate(sourceId);
        await gate.WaitAsync().ConfigureAwait(false);
        try { PlcCommandWriter.Unregister(sourceId); await adapter.DisposeAsync().ConfigureAwait(false); }
        catch (Exception ex) { Log("error", $"关闭采集连接失败：{ex.GetBaseException().Message}", sourceId); }
        finally { gate.Release(); }
    }

    private void Process(SourceConfig source, AlarmRule rule, RuleState state, IReadOnlyList<Signal> signals)
    {
        var current = signals.Where(signal => signal.Active && Matches(rule, signal))
            .GroupBy(signal => signal.Code, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        _calls?.Observe(source, rule, current);
        foreach (var (code, previous) in state.ActiveSignals.Where(item => !current.ContainsKey(item.Key)).ToArray())
        {
            Interlocked.Increment(ref _recoveryEvents);
            Log("info", MessageRenderer.Render(rule.RecoveryTemplate, source, previous, rule), source.Id, rule.Id);
            _history?.Record(new AlarmHistoryEntry(DateTime.Now, "recovery", source.Name, MessageRenderer.ResolveAlarmCode(rule, previous), MessageRenderer.ResolveAlarmText(rule, previous), rule.Severity));
            state.ActiveSignals.Remove(code);
        }
        foreach (var (code, signal) in current)
        {
            if (state.ActiveSignals.ContainsKey(code)) { state.ActiveSignals[code] = signal; continue; }
            state.ActiveSignals[code] = signal;
            if (!state.LastSentByCode.TryGetValue(code, out var lastSent) || Stopwatch.GetElapsedTime(lastSent).TotalSeconds >= rule.CooldownSeconds)
            {
                var message = MessageRenderer.Render(rule.MessageTemplate, source, signal, rule);
                Interlocked.Increment(ref _alarmEvents);
                Log(rule.Severity, message, source.Id, rule.Id);
                _history?.Record(new AlarmHistoryEntry(DateTime.Now, "alarm", source.Name, MessageRenderer.ResolveAlarmCode(rule, signal), MessageRenderer.ResolveAlarmText(rule, signal), rule.Severity));
                state.LastSentByCode[code] = Stopwatch.GetTimestamp();
                foreach (var lane in _lanes)
                    if (!lane.Queue.Writer.TryWrite(new Delivery(message, source.Id, rule.Id)))
                    {
                        Interlocked.Increment(ref _failedDeliveries);
                        Log("error", $"通知队列已满 [{lane.Target.Name}]，本条报警未入队。", source.Id, rule.Id);
                    }
            }
        }
    }

    private async Task RunNotificationsAsync(NotificationLane lane, CancellationToken token)
    {
        // Each destination has an independent bounded queue. Network latency never blocks sampling.
        await foreach (var delivery in lane.Queue.Reader.ReadAllAsync(token).ConfigureAwait(false))
        {
            for (var attempt = 1; attempt <= 3; attempt++)
            {
                try
                {
                    var sent = await _buildNotifier(lane.Target).SendAsync(delivery.Message, token).ConfigureAwait(false);
                    Interlocked.Increment(ref _sentDeliveries);
                    Log("info", $"已发送 {sent} 个目标 [{lane.Target.Name}]", delivery.SourceId, delivery.RuleId);
                    break;
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    Log("error", $"通知发送失败 [{lane.Target.Name}]（{attempt}/3）：{ex.Message}", delivery.SourceId, delivery.RuleId);
                    if (attempt == 3) Interlocked.Increment(ref _failedDeliveries);
                    if (attempt < 3) await Task.Delay(TimeSpan.FromSeconds(5), token).ConfigureAwait(false);
                }
            }
        }
    }

    internal static bool Matches(AlarmRule rule, Signal signal)
    {
        if (!string.IsNullOrWhiteSpace(rule.MatchCode) && !string.Equals(rule.MatchCode, signal.Code, StringComparison.OrdinalIgnoreCase)) return false;
        if (string.IsNullOrWhiteSpace(rule.MatchKey)) return true;
        if (!signal.Values.TryGetValue(rule.MatchKey, out var value)) return false;
        return string.IsNullOrWhiteSpace(rule.MatchValue) || string.Equals(Convert.ToString(value), rule.MatchValue, StringComparison.OrdinalIgnoreCase);
    }

    private void ObserveRead(long started, bool failed)
    {
        Interlocked.Add(ref _readMicroseconds, (long)(Stopwatch.GetElapsedTime(started).TotalMilliseconds * 1000));
        if (failed) Interlocked.Increment(ref _failedReads);
        Interlocked.Increment(ref _reads);
    }

    public CollectionMetrics Metrics()
    {
        var states = ConnectionSnapshots().Values;
        return new(Interlocked.Read(ref _reads), Interlocked.Read(ref _failedReads), Interlocked.Read(ref _readMicroseconds),
            Interlocked.Read(ref _alarmEvents), Interlocked.Read(ref _recoveryEvents), Interlocked.Read(ref _sentDeliveries),
            Interlocked.Read(ref _failedDeliveries), states.Count(x => x.Connected),
            states.Where(x => x.Connected).Sum(x => x.ActiveAlarmCount), _lanes.Sum(x => x.Queue.Reader.Count));
    }
    public sealed record CollectionMetrics(long Reads, long FailedReads, long ReadMicroseconds, long AlarmEvents,
        long RecoveryEvents, long SentDeliveries, long FailedDeliveries, int Connected, int ActiveAlarms, int QueueDepth);

    public string StatusText()
    {
        var enabled = _config.Sources.Where(x => x.Enabled).ToArray();
        var connected = enabled.Count(source => _connections.TryGetValue(source.Id, out var state) && state.Connected);
        var uptime = _startedAt == default ? TimeSpan.Zero : DateTime.Now - _startedAt;
        var lines = new List<string>
        {
            "设备连接状态",
            $"当前连接：{connected}/{enabled.Length} 台",
            $"服务运行：{FormatDuration(uptime)}"
        };
        foreach (var source in enabled)
        {
            if (!_connections.TryGetValue(source.Id, out var state)) { lines.Add($"⚪ {source.Name} · {ProtocolLabel(source.Protocol)} · 等待连接"); continue; }
            lock (state)
            {
                if (state.Connected)
                {
                    var duration = state.ConnectedSince is DateTime since ? FormatDuration(DateTime.Now - since) : "刚刚";
                    var heartbeat = state.LastSuccess is DateTime last ? $"{Math.Max(0, (int)(DateTime.Now - last).TotalSeconds)} 秒前" : "等待中";
                    lines.Add($"🟢 {source.Name} · {ProtocolLabel(source.Protocol)}\n已连接 {duration} · 心跳 {heartbeat} · 有效报警 {state.ActiveAlarmCount}");
                }
                else lines.Add($"🔴 {source.Name} · {ProtocolLabel(source.Protocol)}\n连接失败：{state.Detail}");
            }
        }
        return string.Join("\n", lines);
    }

    public IReadOnlyDictionary<string, DeviceConnectionSnapshot> ConnectionSnapshots()
    {
        var result = new Dictionary<string, DeviceConnectionSnapshot>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in _connections)
            lock (pair.Value)
            {
                var state = pair.Value;
                result[pair.Key] = new(state.Connected, state.ConnectedSince, state.LastSuccess, state.ActiveAlarmCount, state.Detail);
            }
        return result;
    }

    public sealed record DeviceConnectionSnapshot(bool Connected, DateTime? ConnectedSince, DateTime? LastSuccess, int ActiveAlarmCount, string Detail);

    public IReadOnlySet<string> ConnectedSourceIds()
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in _config.Sources.Where(x => x.Enabled))
            if (_connections.TryGetValue(source.Id, out var state))
                lock (state) { if (state.Connected) result.Add(source.Id); }
        return result;
    }

    private static string FormatDuration(TimeSpan value) => value.TotalDays >= 1 ? $"{(int)value.TotalDays}天 {value:hh\\:mm\\:ss}" : value.ToString("hh\\:mm\\:ss");
    private static string ProtocolLabel(string protocol) => protocol switch { "opcua" => "OPC UA", "s7" => "S7", "modbus_tcp" => "Modbus TCP", "modbus_rtu" => "Modbus RTU", _ => protocol };

    private sealed class RuleState
    {
        public Dictionary<string, Signal> ActiveSignals { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, long> LastSentByCode { get; } = new(StringComparer.OrdinalIgnoreCase);
    }
    private sealed class ConnectionState
    {
        public bool Connected;
        public DateTime? ConnectedSince;
        public DateTime? LastSuccess;
        public int ActiveAlarmCount;
        public string Detail = "等待连接";
    }
    private sealed record Delivery(string Message, string SourceId, string RuleId);
    private sealed class NotificationLane(NotificationTarget target)
    {
        public NotificationTarget Target { get; } = target;
        public Channel<Delivery> Queue { get; } = Channel.CreateBounded<Delivery>(new BoundedChannelOptions(1000)
            { SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.Wait });
    }
    private void Log(string level, string message, string sourceId = "", string ruleId = "") => _onEvent(new RuntimeEvent(level, message, sourceId, ruleId));
    public async ValueTask DisposeAsync() => await StopAsync();
}
