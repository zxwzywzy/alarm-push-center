using System.IO.Ports;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Protocol;
using TwinCAT.Ads;
using S7.Net;
using NModbus;
using NModbus.IO;

namespace AlarmPushCenter;

public sealed class S7Source : SourceAdapter
{
    private readonly Plc _plc;
    public S7Source(SourceConfig config) : base(config)
    {
        var p = config.Params;
        _plc = new Plc(Enum.Parse<CpuType>(p.Get("cpu_type", "S71200"), true), p.Get("host"),
            (short)p.GetInt("rack"), (short)p.GetInt("slot", 1));
        _plc.ReadTimeout = _plc.WriteTimeout = (int)(Math.Clamp(p.GetDouble("timeout", 5), 0.1, 120) * 1000);
    }

    public override async Task<Signal> ReadAsync(CancellationToken cancellationToken)
    {
        using var timeout = RequestTimeout(cancellationToken);
        var p = Config.Params;
        if (!_plc.IsConnected) await _plc.OpenAsync(timeout.Token);
        var trigger = p.GetInt("trigger_byte");
        var bit = p.GetInt("trigger_bit");
        var offset = p.GetInt("code_offset", 2);
        var textOffset = p.GetInt("text_offset", 4);
        var textLength = p.GetInt("text_length", 32);
        if (trigger < 0 || bit is < 0 or > 7 || offset < 0 || textOffset < 0 || textLength < 0)
            throw new InvalidOperationException("S7 偏移不能为负，位编号必须为 0～7。");
        var length = checked(Math.Max(trigger + 1, Math.Max(textOffset + textLength, offset + 2)));
        if (length > 65536) throw new InvalidOperationException("S7 读取范围不能超过 65536 字节。");
        var bytes = await _plc.ReadBytesAsync(DataType.DataBlock, p.GetInt("db_number", 10), 0, length, timeout.Token);
        var active = (bytes[trigger] & (1 << bit)) != 0;
        var code = (bytes[offset] << 8 | bytes[offset + 1]).ToString();
        var textBytes = bytes.Skip(textOffset).Take(textLength).TakeWhile(x => x != 0).ToArray();
        var encoding = Encoding.GetEncoding(p.Get("encoding", "utf-8"));
        var text = encoding.GetString(textBytes).Trim();
        return new Signal(active, code, string.IsNullOrEmpty(text) ? code : text,
            new Dictionary<string, object?> { ["value"] = active ? 1 : 0, ["db"] = p.GetInt("db_number") });
    }

    internal async Task WriteCommandAsync(BotCommandDefinition command, CancellationToken token)
    {
        using var timeout = RequestTimeout(token);
        if (!_plc.IsConnected) await _plc.OpenAsync(timeout.Token);
        async Task WriteAsync(bool value, CancellationToken writeToken) =>
            await _plc.WriteBitAsync(DataType.DataBlock, command.DbNumber, command.Address, command.Bit, value, writeToken);
        if (command.PulseMs == 0)
        {
            await WriteAsync(command.Value != 0, timeout.Token);
            return;
        }
        try
        {
            await WriteAsync(command.Value != 0, timeout.Token);
            await Task.Delay(command.PulseMs, token);
        }
        finally
        {
            using var resetTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await WriteAsync(command.Value == 0, resetTimeout.Token);
        }
    }

    public override ValueTask DisposeAsync() { _plc.Close(); return ValueTask.CompletedTask; }
}

public sealed class ModbusSource : SourceAdapter
{
    private IModbusMaster? _master;
    private TcpClient? _tcpClient;
    private SerialPort? _serialPort;
    public ModbusSource(SourceConfig config) : base(config) { }

    internal static Parity ParseParity(string value) => value.Trim().ToUpperInvariant() switch
    {
        "N" => Parity.None, "E" => Parity.Even, "O" => Parity.Odd,
        "M" => Parity.Mark, "S" => Parity.Space,
        _ => Enum.Parse<Parity>(value, true)
    };
    internal static StopBits ParseStopBits(string value) => value.Trim() switch
    {
        "1" => StopBits.One, "1.5" => StopBits.OnePointFive, "2" => StopBits.Two,
        _ => Enum.Parse<StopBits>(value, true)
    };

    public override async Task<Signal> ReadAsync(CancellationToken cancellationToken)
    {
        using var timeout = RequestTimeout(cancellationToken);
        var p = Config.Params;
        await EnsureConnectedAsync(timeout.Token);
        // Close the transport to interrupt a synchronous driver read on stop or timeout.
        using var registration = timeout.Token.Register(() => { _tcpClient?.Dispose(); _serialPort?.Dispose(); });
        timeout.Token.ThrowIfCancellationRequested();
        var slave = checked((byte)p.GetInt("unit_id", 1));
        var address = checked((ushort)p.GetInt("address"));
        object value = p.Get("table", "coil").ToLowerInvariant() switch
        {
            "discrete_input" => _master!.ReadInputs(slave, address, 1)[0],
            "holding_register" => _master!.ReadHoldingRegisters(slave, address, 1)[0],
            "input_register" => _master!.ReadInputRegisters(slave, address, 1)[0],
            "coil" => _master!.ReadCoils(slave, address, 1)[0],
            _ => throw new InvalidOperationException("未知 Modbus 数据表。")
        };
        return new Signal(Matches(value, p.Get("active_value", "1")), p.Get("code", "modbus"), p.Get("text", "Modbus 报警"),
            new Dictionary<string, object?> { ["value"] = value, ["address"] = address });
    }

    private async Task EnsureConnectedAsync(CancellationToken token)
    {
        var p = Config.Params;
        var timeoutMs = (int)(Math.Clamp(p.GetDouble("timeout", 5), 0.1, 120) * 1000);
        if (_master is null)
        {
            var factory = new ModbusFactory();
            if (Config.Protocol.Equals("modbus_rtu", StringComparison.OrdinalIgnoreCase))
            {
                _serialPort = new SerialPort(p.Get("port", "COM3"), p.GetInt("baudrate", 9600), ParseParity(p.Get("parity", "N")), p.GetInt("bytesize", 8), ParseStopBits(p.Get("stopbits", "1")))
                    { ReadTimeout = timeoutMs, WriteTimeout = timeoutMs };
                _serialPort.Open();
                _master = factory.CreateRtuMaster(new SerialResource(_serialPort));
            }
            else
            {
                _tcpClient = new TcpClient();
                await _tcpClient.ConnectAsync(p.Get("host"), p.GetInt("port", 502), token);
                _master = factory.CreateMaster(_tcpClient);
            }
            _master.Transport.ReadTimeout = timeoutMs;
            _master.Transport.WriteTimeout = timeoutMs;
            _master.Transport.Retries = 0;
        }

    }

    internal async Task WriteCommandAsync(BotCommandDefinition command, CancellationToken token)
    {
        using var timeout = RequestTimeout(token);
        await EnsureConnectedAsync(timeout.Token);
        var slave = checked((byte)Config.Params.GetInt("unit_id", 1));
        var address = checked((ushort)command.Address);
        void Write(int value)
        {
            if (command.WriteType == "register") _master!.WriteSingleRegister(slave, address, checked((ushort)value));
            else _master!.WriteSingleCoil(slave, address, value != 0);
        }
        token.ThrowIfCancellationRequested();
        if (command.PulseMs == 0) { Write(command.Value); return; }
        try { Write(command.Value); await Task.Delay(command.PulseMs, token); }
        finally { Write(command.Value == 0 ? 1 : 0); }
    }
    public override ValueTask DisposeAsync()
    {
        try { _master?.Dispose(); }
        finally { _serialPort?.Dispose(); _tcpClient?.Dispose(); }
        return ValueTask.CompletedTask;
    }

    private sealed class SerialResource(SerialPort port) : IStreamResource
    {
        public int InfiniteTimeout => SerialPort.InfiniteTimeout;
        public int ReadTimeout { get => port.ReadTimeout; set => port.ReadTimeout = value; }
        public int WriteTimeout { get => port.WriteTimeout; set => port.WriteTimeout = value; }
        public void DiscardInBuffer() => port.DiscardInBuffer();
        public int Read(byte[] buffer, int offset, int count) => port.Read(buffer, offset, count);
        public void Write(byte[] buffer, int offset, int count) => port.Write(buffer, offset, count);
        public void Dispose() => port.Dispose();
    }
}
public sealed class MqttSource : SourceAdapter
{
    private readonly IMqttClient _client;
    private bool _subscribed;
    private bool _hasMessage;
    private readonly object _lock = new();
    private Dictionary<string, object?> _message = new(StringComparer.OrdinalIgnoreCase) { ["active"] = false };

    public MqttSource(SourceConfig config) : base(config)
    {
        var p = config.Params;
        _client = new MqttFactory().CreateMqttClient();
        _client.ApplicationMessageReceivedAsync += HandleMessageAsync;
    }

    private async Task ConnectAsync(IReadOnlyDictionary<string, JsonElement> p, CancellationToken token)
    {
        var builder = new MqttClientOptionsBuilder().WithTcpServer(p.Get("host"), p.GetInt("port", 1883));
        if (!string.IsNullOrWhiteSpace(p.Get("username"))) builder.WithCredentials(p.Get("username"), p.Get("password"));
        await _client.ConnectAsync(builder.Build(), token);
        var filter = new MqttTopicFilterBuilder().WithTopic(p.Get("topic")).WithQualityOfServiceLevel((MqttQualityOfServiceLevel)p.GetInt("qos")).Build();
        await _client.SubscribeAsync(new MqttClientSubscribeOptionsBuilder().WithTopicFilter(filter).Build(), token);
        _subscribed = true;
    }

    private Task HandleMessageAsync(MqttApplicationMessageReceivedEventArgs eventArgs)
    {
        var text = Encoding.UTF8.GetString(eventArgs.ApplicationMessage.PayloadSegment);
        Dictionary<string, object?> value;
        try
        {
            using var document = JsonDocument.Parse(text);
            value = document.RootElement.ValueKind == JsonValueKind.Object
                ? document.RootElement.EnumerateObject().ToDictionary(x => x.Name, x => JsonValue(x.Value), StringComparer.OrdinalIgnoreCase)
                : new(StringComparer.OrdinalIgnoreCase) { ["active"] = text, ["text"] = text };
        }
        catch (JsonException) { value = new(StringComparer.OrdinalIgnoreCase) { ["active"] = text, ["text"] = text }; }
        lock (_lock) { _message = value; _hasMessage = true; }
        return Task.CompletedTask;
    }

    public override async Task<Signal> ReadAsync(CancellationToken cancellationToken)
    {
        using var timeout = RequestTimeout(cancellationToken);
        if (!_client.IsConnected || !_subscribed)
        {
            _subscribed = false;
            lock (_lock) _hasMessage = false;
            await ConnectAsync(Config.Params, timeout.Token);
        }
        while (true)
        {
            lock (_lock) { if (_hasMessage) break; }
            await Task.Delay(50, timeout.Token);
        }
        Dictionary<string, object?> message;
        lock (_lock) message = new(_message, StringComparer.OrdinalIgnoreCase);
        var p = Config.Params;
        message.TryGetValue(p.Get("active_key", "active"), out var rawActive);
        message.TryGetValue(p.Get("code_key", "code"), out var code);
        message.TryGetValue(p.Get("text_key", "text"), out var text);
        return new Signal(Matches(rawActive, p.Get("expected_active", "true")), code is null ? "mqtt" : Convert.ToString(code)!, text is null ? "MQTT 报警" : Convert.ToString(text)!, message);
    }

    public override async ValueTask DisposeAsync()
    {
        if (_client.IsConnected) await _client.DisconnectAsync();
        _client.Dispose();
    }
}

public sealed class AdsSource : SourceAdapter
{
    private readonly AdsClient _client = new();

    public AdsSource(SourceConfig config) : base(config)
    {
        _client.Connect(config.Params.Get("net_id"), config.Params.GetInt("port", 851));
        _client.Timeout = (int)(Math.Clamp(config.Params.GetDouble("timeout", 5), 0.1, 120) * 1000);
    }

    public override Task<Signal> ReadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var p = Config.Params;
        var value = _client.ReadValue(p.Get("symbol"));
        return Task.FromResult(new Signal(ToBoolean(value), p.Get("code", "ads"), p.Get("text", "ADS 报警"), new Dictionary<string, object?> { ["value"] = value, ["symbol"] = p.Get("symbol") }));
    }

    public override ValueTask DisposeAsync()
    {
        _client.Dispose();
        return ValueTask.CompletedTask;
    }
}

public sealed class EthernetIpSource(SourceConfig config) : SourceAdapter(config)
{
    public override Task<Signal> ReadAsync(CancellationToken cancellationToken) => throw new NotSupportedException("EtherNet/IP 采集需要按控制器型号选择 CIP 驱动；当前 C# 版本保留了原有配置并会记录明确的适配器错误。");
}


