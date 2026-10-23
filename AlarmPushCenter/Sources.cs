using System.IO.Ports;
using System.Globalization;
using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Opc.UaFx;
using Opc.UaFx.Client;

namespace AlarmPushCenter;

public interface ISourceAdapter : IAsyncDisposable
{
    SourceConfig Config { get; }
    Task<Signal> ReadAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<Signal>> ReadSignalsAsync(CancellationToken cancellationToken);
}

public abstract class SourceAdapter(SourceConfig config) : ISourceAdapter
{
    public SourceConfig Config { get; } = config;
    public abstract Task<Signal> ReadAsync(CancellationToken cancellationToken);
    public virtual async Task<IReadOnlyList<Signal>> ReadSignalsAsync(CancellationToken cancellationToken)
        => [await ReadAsync(cancellationToken).ConfigureAwait(false)];
    public virtual ValueTask DisposeAsync() => ValueTask.CompletedTask;

    protected CancellationTokenSource RequestTimeout(CancellationToken token)
    {
        var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(Config.Params.GetDouble("timeout", 5), 0.1, 120)));
        return timeout;
    }

    protected static bool Matches(object? value, string expected)
    {
        var actual = Convert.ToString(value)?.Trim().ToLowerInvariant() ?? "";
        expected = expected.Trim().ToLowerInvariant();
        return expected switch
        {
            "true" or "1" or "on" or "yes" => actual is "true" or "1" or "on" or "yes",
            "false" or "0" or "off" or "no" => actual is "false" or "0" or "off" or "no",
            _ => actual == expected
        };
    }

    protected static Signal JsonSignal(JsonElement payload, IReadOnlyDictionary<string, JsonElement> parameters, string defaultCode)
    {
        var activeKey = parameters.Get("active_key", "active");
        var codeKey = parameters.Get("code_key", "code");
        var textKey = parameters.Get("text_key", "text");
        var dictionary = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        if (payload.ValueKind == JsonValueKind.Object)
            foreach (var item in payload.EnumerateObject()) dictionary[item.Name] = JsonValue(item.Value);
        dictionary.TryGetValue(activeKey, out var active);
        dictionary.TryGetValue(codeKey, out var code);
        dictionary.TryGetValue(textKey, out var text);
        return new Signal(ToBoolean(active), code is null ? defaultCode : Convert.ToString(code)!, text is null ? "JSON 报警" : Convert.ToString(text)!, dictionary);
    }

    protected static object? JsonValue(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Number when value.TryGetInt64(out var integer) => integer,
        JsonValueKind.Number when value.TryGetDouble(out var number) => number,
        JsonValueKind.Null => null,
        _ => value.ToString()
    };

    protected static bool ToBoolean(object? value) => value switch
    {
        bool boolean => boolean,
        byte number => number != 0,
        short number => number != 0,
        int number => number != 0,
        long number => number != 0,
        ushort number => number != 0,
        uint number => number != 0,
        ulong number => number != 0,
        float number => number != 0,
        double number => number != 0,
        decimal number => number != 0,
        _ => Matches(value, "true")
    };
}

public static class SourceFactory
{
    public static ISourceAdapter Create(SourceConfig config) => config.Protocol.ToLowerInvariant() switch
    {
        "mock" => new MockSource(config),
        "http_json" => new HttpJsonSource(config),
        "tcp_json" => new TcpJsonSource(config),
        "udp_json" => new UdpJsonSource(config),
        "serial_text" => new SerialTextSource(config),
        "opcua" => new OpcUaSource(config),
        "opcua_prodiag" => new OpcUaProDiagSource(config),
        "s7" => new S7Source(config),
        "modbus_tcp" or "modbus_rtu" => new ModbusSource(config),
        "mqtt" => new MqttSource(config),
        "ads" => new AdsSource(config),
        "ethernet_ip" => new EthernetIpSource(config),
        _ => throw new NotSupportedException($"不支持的采集协议：{config.Protocol}")
    };
}

public sealed class OpcUaProDiagSource : SourceAdapter
{
    private const string SimaticNamespace = "http://www.siemens.com/simatic-s7-opcua";
    private static readonly Regex AssociatedValuePlaceholder = new("@(?<number>10|[1-9])%(?<format>[^@]*)@", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private readonly OpcClient _client;
    private readonly object _stateLock = new();
    private readonly Dictionary<string, Signal> _active = new(StringComparer.OrdinalIgnoreCase);
    private OpcSubscription? _subscription;
    private bool _connected;

    public OpcUaProDiagSource(SourceConfig config) : base(config)
    {
        _client = new OpcClient(config.Params.Get("endpoint"));
        _client.OperationTimeout = (int)(Math.Clamp(config.Params.GetDouble("timeout", 5), 0.1, 120) * 1000);
        if (!string.IsNullOrWhiteSpace(config.Params.Get("username")))
            _client.Security.UserIdentity = new OpcClientIdentity(config.Params.Get("username"), config.Params.Get("password"));
    }

    public override async Task<Signal> ReadAsync(CancellationToken cancellationToken)
    {
        var signals = await ReadSignalsAsync(cancellationToken).ConfigureAwait(false);
        return signals.Count > 0 ? signals[0] : new Signal(false, "0", "当前无 ProDiag 报警", new Dictionary<string, object?>());
    }

    public override Task<IReadOnlyList<Signal>> ReadSignalsAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureSubscribed();
        if (_subscription is not { IsPublishing: true }) throw new IOException("OPC UA ProDiag 事件订阅未处于发布状态。");
        lock (_stateLock) return Task.FromResult<IReadOnlyList<Signal>>(_active.Values.ToArray());
    }

    private void EnsureSubscribed()
    {
        if (_subscription is not null) return;
        if (!_connected) { _client.Connect(); _connected = true; }
        var eventNode = ResolveEventNode();
        var standardFilter = OpcFilter.Using(_client).FromEvents(OpcEventTypes.AlarmCondition).Select();
        var namespaceIndex = _client.SystemContext.Namespaces.ResolveIndex(SimaticNamespace);
        var idOperand = new OpcSimpleAttributeOperand(OpcEventTypes.AlarmCondition, OpcAttribute.Value,
            new OpcName("ID", namespaceIndex));
        var associatedOperands = Enumerable.Range(1, 10).Select(index =>
            new OpcSimpleAttributeOperand(OpcEventTypes.AlarmCondition, OpcAttribute.Value,
                new OpcName($"AssociatedValue_{index:00}", namespaceIndex)));
        var filter = new OpcEventFilter(standardFilter.WhereClause, standardFilter.SelectClause.Concat([idOperand]).Concat(associatedOperands));
        var item = new OpcMonitoredItem(eventNode, OpcAttribute.EventNotifier, filter)
        {
            QueueSize = Math.Clamp(Config.Params.GetInt("event_queue_size", 1000), 10, 10000),
            DisplayName = "SIMATIC ProDiag alarms"
        };
        item.EventReceived += OnEventReceived;
        _subscription = _client.CreateSubscription(new OpcSubscriptionSetup(new[] { item })
        {
            DisplayName = "AlarmPushCenter ProDiag",
            PublishingInterval = Math.Clamp(Config.Params.GetInt("publishing_interval_ms", 250), 50, 60000),
            KeepAliveCount = 10,
            LifetimeCount = 100
        });
        if (_subscription is not { IsPublishing: true }) throw new IOException("OPC UA ProDiag 事件订阅创建失败。");
        if (Config.Params.Get("condition_refresh", "是").Equals("是", StringComparison.OrdinalIgnoreCase))
            _subscription.RefreshConditions();
    }

    private OpcNodeId ResolveEventNode()
    {
        var configured = Config.Params.Get("event_node_id").Trim();
        if (configured.Length > 0) return OpcNodeId.Parse(configured);
        var root = _client.BrowseNode(OpcObjectTypes.Server);
        var pending = new Queue<(OpcNodeInfo Node, int Depth)>();
        pending.Enqueue((root, 0));
        while (pending.Count > 0)
        {
            var (node, depth) = pending.Dequeue();
            if (string.Equals(node.Name.Value, "ProcessDiagnostics", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(Convert.ToString(node.DisplayName), "ProcessDiagnostics", StringComparison.OrdinalIgnoreCase))
                return node.NodeId;
            if (depth >= 3) continue;
            foreach (var child in node.Children()) pending.Enqueue((child, depth + 1));
        }
        throw new IOException("未找到 ProcessDiagnostics 事件节点。请确认 CPU 已启用 OPC UA Alarms & Conditions，或手动填写事件节点 ID。");
    }

    private void OnEventReceived(object? sender, OpcEventReceivedEventArgs e)
    {
        if (e.Event is not OpcAlarmCondition alarm) return;
        var code = TryReadSimaticEventFields(e, out var associatedValues) ?? alarm.ConditionName;
        if (string.IsNullOrWhiteSpace(code) || code == "0") code = alarm.NodeId?.ToString();
        if (string.IsNullOrWhiteSpace(code)) code = alarm.SourceName;
        if (string.IsNullOrWhiteSpace(code)) code = Convert.ToHexString(alarm.EventId.ToArray());
        var signal = BuildSignal(code, Convert.ToString(alarm.Message) ?? "ProDiag 报警", alarm.IsActive,
            Convert.ToInt32(alarm.Severity, CultureInfo.InvariantCulture), alarm.SourceName, alarm.ConditionName,
            alarm.Time, alarm.IsAcked, alarm.IsRetained, alarm.EventId.ToString(), associatedValues);
        lock (_stateLock)
        {
            if (signal.Active) _active[signal.Code] = signal;
            else _active.Remove(signal.Code);
        }
    }

    private string? TryReadSimaticEventFields(OpcEventReceivedEventArgs e, out Dictionary<int, object?> associatedValues)
    {
        associatedValues = [];
        try
        {
            var data = e.MonitoredItem.LastEvent;
            if (data is null || e.MonitoredItem.Filter is not OpcEventFilter filter) return null;
            var values = data.Fields.Select(field => field.Value).ToArray();
            var view = OpcEventDataView.Create(_client.SystemContext, filter, values);
            var namespaceIndex = _client.SystemContext.Namespaces.ResolveIndex(SimaticNamespace);
            for (var index = 1; index <= 10; index++)
            {
                try
                {
                    object? raw;
                    try { raw = view.Get<object>(new OpcName($"AssociatedValue_{index:00}", namespaceIndex)); }
                    catch { raw = values.Length >= 10 ? values[values.Length - 10 + index - 1] : null; }
                    var value = UnwrapAssociatedValue(raw);
                    if (value is not null) associatedValues[index] = value;
                }
                catch { }
            }
            uint id;
            try { id = view.Get<uint>(new OpcName("ID", namespaceIndex)); }
            catch { id = view.Get<ushort>(new OpcName("ID", namespaceIndex)); }
            return id == 0 ? null : id.ToString("X8", CultureInfo.InvariantCulture);
        }
        catch { return null; }
    }

    internal static Signal BuildSignal(string code, string text, bool active, int severity, string sourceName,
        string conditionName, DateTime time, bool acknowledged, bool retained, string eventId,
        IReadOnlyDictionary<int, object?>? associatedValues = null)
    {
        var values = new Dictionary<string, object?>
        {
            ["value"] = active,
            ["severity"] = severity,
            ["source_name"] = sourceName,
            ["condition_name"] = conditionName,
            ["event_time"] = time,
            ["acknowledged"] = acknowledged,
            ["retained"] = retained,
            ["event_id"] = eventId
        };
        if (associatedValues is not null)
            foreach (var (index, value) in associatedValues)
                values[$"associated_value_{index}"] = value;
        if (TryDecodeSimaticConditionNodeId(code, out var fbNumber, out var supervisionId))
        {
            values["fb_number"] = fbNumber;
            values["supervision_id"] = supervisionId;
            values["runtime_match_key"] = $"FB{fbNumber}:{supervisionId}";
        }
        var resolvedText = ResolveAssociatedValuePlaceholders(string.IsNullOrWhiteSpace(text) ? "ProDiag 报警" : text, values);
        return new Signal(active, code, resolvedText, values);
    }

    internal static string ResolveAssociatedValuePlaceholders(string text, IReadOnlyDictionary<string, object?> values,
        IReadOnlyList<AlarmTextListEntry>? textLists = null, string? fixedTextListName = null, string? fixedTextListIndex = null)
        => AssociatedValuePlaceholder.Replace(text ?? "", match =>
        {
            var index = int.Parse(match.Groups["number"].Value, CultureInfo.InvariantCulture);
            var format = match.Groups["format"].Value;
            if (format.StartsWith("t#", StringComparison.OrdinalIgnoreCase))
            {
                var listName = format[2..].Trim();
                object? lookupValue = null;
                if (!string.IsNullOrWhiteSpace(fixedTextListName) && listName.Equals(fixedTextListName, StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(fixedTextListIndex)) lookupValue = fixedTextListIndex;
                else if (values.TryGetValue($"associated_value_{index}", out var eventValue)) lookupValue = eventValue;
                if (lookupValue is null) return match.Value;
                var resolved = ResolveTextListValue(textLists, listName, lookupValue);
                return resolved ?? match.Value;
            }
            if (!values.TryGetValue($"associated_value_{index}", out var value) || value is null) return match.Value;
            return FormatAssociatedValue(value, format);
        });

    private static string? ResolveTextListValue(IReadOnlyList<AlarmTextListEntry>? entries, string listName, object value)
    {
        if (entries is null || entries.Count == 0 || string.IsNullOrWhiteSpace(listName) ||
            !decimal.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Any, CultureInfo.InvariantCulture, out var number)) return null;
        var matching = entries.Where(entry => entry.ListName.Equals(listName, StringComparison.Ordinal)).ToList();
        if (matching.Count == 0)
        {
            var caseInsensitive = entries.Where(entry => entry.ListName.Equals(listName, StringComparison.OrdinalIgnoreCase)).ToList();
            if (caseInsensitive.Select(entry => entry.ListName).Distinct(StringComparer.Ordinal).Count() != 1) return null;
            matching = caseInsensitive;
        }
        foreach (var entry in matching)
        {
            if (!decimal.TryParse(entry.From, NumberStyles.Any, CultureInfo.InvariantCulture, out var from)) continue;
            var to = decimal.TryParse(entry.To, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsedTo) ? parsedTo : from;
            if (number >= from && number <= to && !string.IsNullOrWhiteSpace(entry.Text)) return entry.Text.Trim();
        }
        return null;
    }

    internal static string DescribeAssociatedValuePlaceholders(string text)
        => AssociatedValuePlaceholder.Replace(text ?? "", match => $"〈运行时关联值 SD_{match.Groups["number"].Value}〉");

    private static string FormatAssociatedValue(object value, string format)
    {
        value = UnwrapAssociatedValue(value) ?? value;
        var spec = format.Trim();
        try
        {
            if (spec.StartsWith("t#", StringComparison.OrdinalIgnoreCase)) return Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
            var widthText = new string(spec.TakeWhile(char.IsDigit).ToArray());
            var width = int.TryParse(widthText, NumberStyles.None, CultureInfo.InvariantCulture, out var parsedWidth) ? parsedWidth : 0;
            if (spec.EndsWith("X", StringComparison.Ordinal)) return Convert.ToUInt64(value, CultureInfo.InvariantCulture).ToString(width > 0 ? $"X{width}" : "X", CultureInfo.InvariantCulture);
            if (spec.EndsWith("b", StringComparison.OrdinalIgnoreCase))
            {
                var binary = Convert.ToString(Convert.ToInt64(value, CultureInfo.InvariantCulture), 2);
                return width > 0 ? binary.PadLeft(width, '0') : binary;
            }
            if (spec.EndsWith("d", StringComparison.OrdinalIgnoreCase)) return Convert.ToInt64(value, CultureInfo.InvariantCulture).ToString(width > 0 ? $"D{width}" : "D", CultureInfo.InvariantCulture);
            if (spec.EndsWith("u", StringComparison.OrdinalIgnoreCase)) return Convert.ToUInt64(value, CultureInfo.InvariantCulture).ToString(width > 0 ? $"D{width}" : "D", CultureInfo.InvariantCulture);
            if (spec.EndsWith("f", StringComparison.OrdinalIgnoreCase))
            {
                var dot = spec.IndexOf('.');
                var decimals = dot >= 0 && int.TryParse(new string(spec.Skip(dot + 1).TakeWhile(char.IsDigit).ToArray()), out var parsedDecimals) ? parsedDecimals : -1;
                return Convert.ToDouble(value, CultureInfo.InvariantCulture).ToString(decimals >= 0 ? $"F{decimals}" : "G", CultureInfo.InvariantCulture);
            }
            return Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
        }
        catch { return Convert.ToString(value, CultureInfo.InvariantCulture) ?? ""; }
    }

    private static object? UnwrapAssociatedValue(object? value, int depth = 0)
    {
        if (value is null || depth > 5) return value;
        if (value is Opc.Ua.Variant variant) return UnwrapAssociatedValue(variant.Value, depth + 1);
        if (value is Opc.Ua.ExtensionObject extension) return UnwrapAssociatedValue(extension.Body, depth + 1);
        if (value is OpcDataObject data)
        {
            var selected = data.GetFields().FirstOrDefault(field => field.Exists && field.HasValue && field.Value is not null &&
                !field.Name.Contains("Switch", StringComparison.OrdinalIgnoreCase));
            return selected is null ? null : UnwrapAssociatedValue(selected.Value, depth + 1);
        }
        var type = value.GetType();
        if (type.IsPrimitive || value is string or decimal or DateTime or Guid) return value;
        var property = type.GetProperties().FirstOrDefault(item => item.CanRead && item.GetIndexParameters().Length == 0 &&
            item.Name is not ("TypeId" or "BinaryEncodingId" or "XmlEncodingId" or "JsonEncodingId" or "SwitchValue"));
        if (property is not null)
        {
            try { return UnwrapAssociatedValue(property.GetValue(value), depth + 1); } catch { }
        }
        return value;
    }

    internal static bool TryDecodeSimaticConditionNodeId(string nodeId, out int fbNumber, out string supervisionId)
    {
        fbNumber = 0;
        supervisionId = "";
        var marker = nodeId.IndexOf(";b=", StringComparison.OrdinalIgnoreCase);
        if (marker < 0) return false;
        try
        {
            var bytes = Convert.FromBase64String(nodeId[(marker + 3)..]);
            if (bytes.Length < 6) return false;
            var supervision = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(0, 2));
            fbNumber = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(4, 2));
            supervisionId = supervision.ToString("X4", CultureInfo.InvariantCulture);
            return fbNumber > 0;
        }
        catch { return false; }
    }

    public override ValueTask DisposeAsync()
    {
        _subscription = null;
        if (_connected) _client.Disconnect();
        _client.Dispose();
        return ValueTask.CompletedTask;
    }
}

public sealed class MockSource(SourceConfig config) : SourceAdapter(config)
{
    private int _counter;
    private bool _active;

    public override Task<Signal> ReadAsync(CancellationToken cancellationToken)
    {
        _counter++;
        if (_counter % Math.Max(1, Config.Params.GetInt("toggle_every", 8)) == 0) _active = !_active;
        var parameters = Config.Params;
        return Task.FromResult(new Signal(_active, parameters.Get("code", "1001"), parameters.Get("text", "模拟报警"), new Dictionary<string, object?> { ["value"] = _active ? 1 : 0, ["active"] = _active }));
    }
}

public sealed class HttpJsonSource(SourceConfig config) : SourceAdapter(config)
{
    private static readonly HttpClient Client = new() { Timeout = Timeout.InfiniteTimeSpan };

    public override async Task<Signal> ReadAsync(CancellationToken cancellationToken)
    {
        var parameters = Config.Params;
        using var timeout = RequestTimeout(cancellationToken);
        cancellationToken = timeout.Token;
        using var request = new HttpRequestMessage(new HttpMethod(parameters.Get("method", "GET")), parameters.Get("url"));
        var headerJson = parameters.Get("headers_json", "{}");
        using var headerDocument = JsonDocument.Parse(headerJson);
        if (headerDocument.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidOperationException("请求头 JSON 必须是对象。");
        foreach (var header in headerDocument.RootElement.EnumerateObject()) request.Headers.TryAddWithoutValidation(header.Name, header.Value.ToString());
        if (request.Method != HttpMethod.Get) request.Content = new StringContent(parameters.Get("body_json", "{}"), Encoding.UTF8, "application/json");
        using var response = await Client.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        return JsonSignal(document.RootElement, parameters, "http");
    }
}

public sealed class TcpJsonSource(SourceConfig config) : SourceAdapter(config)
{
    public override async Task<Signal> ReadAsync(CancellationToken cancellationToken)
    {
        var p = Config.Params;
        using var client = new TcpClient();
        using var timeout = RequestTimeout(cancellationToken);
        cancellationToken = timeout.Token;
        await client.ConnectAsync(p.Get("host"), p.GetInt("port", 9000), cancellationToken);
        await using var stream = client.GetStream();
        var request = Encoding.UTF8.GetBytes(Unescape(p.Get("request_text", "read\\n")));
        await stream.WriteAsync(request, cancellationToken);
        var delimiter = Unescape(p.Get("delimiter", "\\n"));
        var response = await ReadFrameAsync(stream, delimiter, p.GetInt("max_response_bytes", 1048576), cancellationToken);
        using var document = JsonDocument.Parse(response);
        return JsonSignal(document.RootElement, p, "tcp");
    }

    private static string Unescape(string value) => value.Replace("\\r", "\r").Replace("\\n", "\n");

    internal static async Task<string> ReadFrameAsync(Stream stream, string delimiter, int maxBytes, CancellationToken token)
    {
        if (maxBytes < 1 || maxBytes > 16 * 1024 * 1024) throw new InvalidOperationException("响应长度限制必须在 1～16777216 字节之间。");
        var marker = Encoding.UTF8.GetBytes(delimiter);
        using var data = new MemoryStream();
        var buffer = new byte[4096];
        var scanned = 0;
        while (true)
        {
            var count = await stream.ReadAsync(buffer, token);
            if (count == 0)
            {
                if (marker.Length > 0) throw new IOException("TCP 连接在消息分隔符到达之前关闭。");
                return Encoding.UTF8.GetString(data.ToArray());
            }
            data.Write(buffer, 0, count);
            var bytes = data.GetBuffer();
            if (marker.Length > 0)
                for (; scanned <= data.Length - marker.Length; scanned++)
                    if (bytes.AsSpan(scanned, marker.Length).SequenceEqual(marker))
                    {
                        if (scanned > maxBytes) throw new IOException("TCP 响应超过长度限制。");
                        return Encoding.UTF8.GetString(bytes, 0, scanned);
                    }
            if (data.Length > (long)maxBytes + marker.Length) throw new IOException("TCP 响应超过长度限制。");
        }
    }
}

public sealed class UdpJsonSource(SourceConfig config) : SourceAdapter(config)
{
    public override async Task<Signal> ReadAsync(CancellationToken cancellationToken)
    {
        var p = Config.Params;
        using var timeout = RequestTimeout(cancellationToken);
        cancellationToken = timeout.Token;
        var addresses = await Dns.GetHostAddressesAsync(p.Get("host"), cancellationToken);
        var endpoint = new IPEndPoint(addresses[0], p.GetInt("port", 9001));
        using var client = new UdpClient(endpoint.AddressFamily);
        client.Connect(endpoint);
        var request = Encoding.UTF8.GetBytes(p.Get("request_text", "read"));
        await client.SendAsync(request, cancellationToken);
        var response = await client.ReceiveAsync(cancellationToken);
        using var document = JsonDocument.Parse(response.Buffer);
        return JsonSignal(document.RootElement, p, "udp");
    }
}

public sealed class SerialTextSource : SourceAdapter
{
    private readonly SerialPort _port;

    public SerialTextSource(SourceConfig config) : base(config)
    {
        var timeoutMs = (int)(Math.Clamp(config.Params.GetDouble("timeout", 5), 0.1, 120) * 1000);
        _port = new SerialPort(config.Params.Get("port", "COM3"), config.Params.GetInt("baudrate", 9600))
            { ReadTimeout = timeoutMs, WriteTimeout = timeoutMs, NewLine = "\n" };
    }

    public override Task<Signal> ReadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var p = Config.Params;
        if (!_port.IsOpen) _port.Open();
        using var timeout = RequestTimeout(cancellationToken);
        using var registration = timeout.Token.Register(() => _port.Dispose());
        var request = p.Get("request_text");
        if (!string.IsNullOrEmpty(request)) _port.Write(request.Replace("\\n", "\n"));
        var value = _port.ReadLine().Trim();
        var active = value.Contains(p.Get("active_text", "ALARM"), StringComparison.OrdinalIgnoreCase);
        return Task.FromResult(new Signal(active, p.Get("code", "serial"), p.Get("text", value.Length == 0 ? "串口报警" : value), new Dictionary<string, object?> { ["value"] = value }));
    }

    public override ValueTask DisposeAsync()
    {
        _port.Dispose();
        return ValueTask.CompletedTask;
    }
}

public sealed class OpcUaSource : SourceAdapter
{
    private readonly OpcClient _client;
    private readonly object _snapshotLock = new();
    private readonly TaskCompletionSource<bool> _initialSnapshot = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private OpcSubscription? _subscription;
    private bool _connected;
    private bool _countReceived, _listReceived;
    private object? _countValue, _listValue;
    private Exception? _countError, _listError;
    private DateTime _lastChangeUtc;

    public OpcUaSource(SourceConfig config) : base(config)
    {
        _client = new OpcClient(config.Params.Get("endpoint"));
        _client.OperationTimeout = (int)(Math.Clamp(config.Params.GetDouble("timeout", 5), 0.1, 120) * 1000);
        if (!string.IsNullOrWhiteSpace(config.Params.Get("username")))
            _client.Security.UserIdentity = new OpcClientIdentity(config.Params.Get("username"), config.Params.Get("password"));
    }

    public override async Task<Signal> ReadAsync(CancellationToken cancellationToken)
    {
        var signals = await ReadSignalsAsync(cancellationToken).ConfigureAwait(false);
        return signals.Count > 0 ? signals[0] : new Signal(false, "0", Config.Params.Get("text", "OPC UA 报警"),
            new Dictionary<string, object?> { ["active_count"] = 0 });
    }

    public override async Task<IReadOnlyList<Signal>> ReadSignalsAsync(CancellationToken cancellationToken)
    {
        using var timeout = RequestTimeout(cancellationToken);
        EnsureSubscribed();
        await _initialSnapshot.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
        while (true)
        {
            DateTime changed;
            lock (_snapshotLock) changed = _lastChangeUtc;
            var wait = TimeSpan.FromMilliseconds(Math.Clamp(Config.Params.GetInt("subscription_coalesce_ms", 50), 0, 1000)) - (DateTime.UtcNow - changed);
            if (wait <= TimeSpan.Zero) break;
            await Task.Delay(wait, timeout.Token).ConfigureAwait(false);
        }
        lock (_snapshotLock)
        {
            if (_countError is not null || _listError is not null) throw new IOException("OPC UA 订阅数据质量无效。", _countError ?? _listError);
            if (_subscription is not { IsPublishing: true }) throw new IOException("OPC UA 订阅未处于发布状态。");
            return BuildSignals(Config, _countValue, _listValue);
        }
    }

    private void EnsureSubscribed()
    {
        if (_subscription is not null) return;
        var p = Config.Params;
        if (!_connected) { _client.Connect(); _connected = true; }
        var sampling = Math.Clamp(p.GetInt("sampling_interval_ms", 250), 50, 60000);
        var countNode = OpcNodeId.Parse(p.Get("active_count_node_id"));
        var listNode = OpcNodeId.Parse(p.Get("alarm_code_list_node_id"));
        var countItem = new OpcMonitoredItem(countNode, OpcAttribute.Value) { SamplingInterval = sampling };
        var listItem = new OpcMonitoredItem(listNode, OpcAttribute.Value) { SamplingInterval = sampling };
        countItem.DataChangeReceived += (_, e) => UpdateSnapshot(true, e.Item.Value);
        listItem.DataChangeReceived += (_, e) => UpdateSnapshot(false, e.Item.Value);
        _subscription = _client.CreateSubscription(new OpcSubscriptionSetup(new[] { countItem, listItem })
        {
            DisplayName = "AlarmPushCenter",
            PublishingInterval = Math.Clamp(p.GetInt("publishing_interval_ms", 250), 50, 60000),
            KeepAliveCount = 10,
            LifetimeCount = 100
        });
        // Read once immediately as a deterministic connection check. The same two nodes remain
        // monitored by the subscription for all subsequent changes.
        var initial = _client.ReadNodes(OpcAttribute.Value, countNode, listNode).ToArray();
        UpdateSnapshot(true, initial[0]);
        UpdateSnapshot(false, initial[1]);
    }

    private void UpdateSnapshot(bool isCount, OpcValue value)
    {
        lock (_snapshotLock)
        {
            if (!value.Status.IsGood)
            {
                var error = new IOException($"{(isCount ? "有效报警数量" : "报警码数组")}节点质量：{value.Status}");
                if (isCount) _countError = error; else _listError = error;
            }
            else
            {
                if (isCount) { _countError = null; _countValue = value.Value; _countReceived = true; }
                else { _listError = null; _listValue = value.Value; _listReceived = true; }
                _lastChangeUtc = DateTime.UtcNow;
            }
            if (_countError is not null || _listError is not null || (_countReceived && _listReceived)) _initialSnapshot.TrySetResult(true);
        }
    }

    internal static IReadOnlyList<Signal> BuildSignals(SourceConfig config, object? countValue, object? listValue)
    {
        int count;
        try { count = Convert.ToInt32(Unwrap(countValue), CultureInfo.InvariantCulture); }
        catch (Exception ex) { throw new IOException("有效报警数量节点不是有效整数。", ex); }
        var maximum = Math.Clamp(config.Params.GetInt("max_alarm_count", 1000), 1, 10000);
        if (count is < 0 || count > maximum) throw new IOException($"有效报警数量 {count} 超出 0～{maximum} 范围。");
        var values = ArrayValues(Unwrap(listValue)).ToArray();
        if (count > values.Length) throw new IOException($"有效报警数量为 {count}，但报警码数组仅有 {values.Length} 项。");
        var inactive = config.Params.Get("inactive_code", "0");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var signals = new List<Signal>(count);
        for (var index = 0; index < count; index++)
        {
            var raw = ExtractAlarmCode(Unwrap(values[index]));
            var code = Convert.ToString(raw, CultureInfo.InvariantCulture)?.Trim() ?? "";
            if (code.Length == 0 || string.Equals(code, inactive, StringComparison.OrdinalIgnoreCase) || !seen.Add(code)) continue;
            signals.Add(new Signal(true, code, config.Params.Get("text", "OPC UA 报警"), new Dictionary<string, object?>
            {
                ["value"] = raw, ["array_index"] = index, ["active_count"] = count
            }));
        }
        return signals;
    }

    private static object? Unwrap(object? value) => value is OpcValue opcValue ? opcValue.Value : value;
    private static object? ExtractAlarmCode(object? value)
    {
        if (value is byte[] bytes)
        {
            if (bytes.Length < sizeof(int)) throw new IOException("报警列表结构项少于 4 字节，无法读取 DInt 报警码。");
            return BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(0, sizeof(int)));
        }
        if (value is IReadOnlyDictionary<string, object?> fields && fields.TryGetValue("AlarmCode", out var code)) return Unwrap(code);
        var property = value?.GetType().GetProperty("AlarmCode");
        return property is null ? value : Unwrap(property.GetValue(value));
    }
    private static IEnumerable<object?> ArrayValues(object? value)
    {
        if (value is null) return [];
        if (value is string || value is not System.Collections.IEnumerable items) throw new IOException("报警码列表节点不是数组。请在 PLC 中配置 DInt 数组。");
        return items.Cast<object?>();
    }

    public async Task WriteCommandAsync(BotCommandDefinition command, CancellationToken cancellationToken)
    {
        ValidateWriteCommand(command);
        using var timeout = RequestTimeout(cancellationToken);
        if (!_connected) { _client.Connect(); _connected = true; }
        var nodeId = OpcNodeId.Parse(command.OpcNodeId);
        timeout.Token.ThrowIfCancellationRequested();
        WriteChecked(nodeId, ParseWriteValue(command.OpcDataType, command.OpcValue));
        if (command.PulseMs <= 0) return;
        try { await Task.Delay(command.PulseMs, timeout.Token).ConfigureAwait(false); }
        finally { WriteChecked(nodeId, ParseWriteValue(command.OpcDataType, command.OpcResetValue)); }
    }

    private void WriteChecked(OpcNodeId nodeId, object value)
    {
        var status = _client.WriteNode(nodeId, value);
        if (!status.IsGood) throw new IOException($"OPC UA 节点写入失败：{status}");
    }

    internal static void ValidateWriteCommand(BotCommandDefinition command)
    {
        if (string.IsNullOrWhiteSpace(command.OpcNodeId)) throw new InvalidOperationException($"[{command.Command}] 请填写 OPC UA 写入节点 ID。");
        try { _ = OpcNodeId.Parse(command.OpcNodeId); }
        catch (Exception ex) { throw new InvalidOperationException($"[{command.Command}] OPC UA 写入节点 ID 格式无效。", ex); }
        _ = ParseWriteValue(command.OpcDataType, command.OpcValue);
        if (command.PulseMs > 0) _ = ParseWriteValue(command.OpcDataType, command.OpcResetValue);
    }

    internal static object ParseWriteValue(string dataType, string text)
    {
        try
        {
            return dataType.Trim().ToLowerInvariant() switch
            {
                "boolean" or "bool" => text.Trim().ToLowerInvariant() switch { "true" or "1" or "是" => true, "false" or "0" or "否" => false, _ => throw new FormatException("布尔值应为 true/false 或 1/0") },
                "int16" => short.Parse(text, NumberStyles.Integer, CultureInfo.InvariantCulture),
                "uint16" => ushort.Parse(text, NumberStyles.Integer, CultureInfo.InvariantCulture),
                "int32" or "dint" => int.Parse(text, NumberStyles.Integer, CultureInfo.InvariantCulture),
                "uint32" => uint.Parse(text, NumberStyles.Integer, CultureInfo.InvariantCulture),
                "int64" => long.Parse(text, NumberStyles.Integer, CultureInfo.InvariantCulture),
                "uint64" => ulong.Parse(text, NumberStyles.Integer, CultureInfo.InvariantCulture),
                "float" => float.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture),
                "double" => double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture),
                "string" => text,
                _ => throw new FormatException("支持 Boolean、Int16、UInt16、Int32、UInt32、Int64、UInt64、Float、Double、String")
            };
        }
        catch (Exception ex) when (ex is FormatException or OverflowException)
        { throw new InvalidOperationException($"OPC UA 写入值“{text}”与数据类型 {dataType} 不匹配：{ex.Message}", ex); }
    }

    public override ValueTask DisposeAsync()
    {
        _subscription = null;
        _client.Disconnect();
        _client.Dispose();
        return ValueTask.CompletedTask;
    }

}

// Protocol adapters implemented in IndustrialSources.cs.
