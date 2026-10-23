using System.Text.Json;
using System.Text.Json.Serialization;

namespace AlarmPushCenter;

public sealed class AppConfig
{
    public int Version { get; set; } = 7;
    public List<SourceConfig> Sources { get; set; } = [];
    public List<AlarmRule> Rules { get; set; } = [];
    public List<NotificationTarget> Targets { get; set; } = [];

    public AppConfig Normalize()
    {
        Version = Math.Max(Version, 7);
        Sources ??= [];
        Rules ??= [];
        Targets ??= [];
        if (Sources.Count == 0) Sources.Add(ConfigDefaults.NewSource());
        if (Rules.Count == 0) Rules.Add(ConfigDefaults.NewRule(Sources[0].Id));
        if (Targets.Count == 0) Targets.Add(ConfigDefaults.NewTarget());
        foreach (var source in Sources)
        {
            source.Id = string.IsNullOrWhiteSpace(source.Id) ? Ids.New("src") : source.Id;
            source.Name = string.IsNullOrWhiteSpace(source.Name) ? "演示设备" : source.Name;
            source.Params ??= [];
            source.PhoneReminder ??= new();
            source.Params = ConfigDefaults.MergeSourceParams(source.Protocol, source.Params);
        }
        foreach (var rule in Rules)
        {
            rule.Id = string.IsNullOrWhiteSpace(rule.Id) ? Ids.New("rule") : rule.Id;
            if (string.IsNullOrWhiteSpace(rule.SourceId)) rule.SourceId = Sources[0].Id;
            rule.AlarmTexts ??= [];
            rule.AlarmTextLists ??= [];
            while (rule.AlarmTexts.Count < 30) rule.AlarmTexts.Add(new AlarmText());
        }
        foreach (var target in Targets)
        {
            target.Id = string.IsNullOrWhiteSpace(target.Id) ? Ids.New("target") : target.Id;
            target.Name = string.IsNullOrWhiteSpace(target.Name) ? "通知目标" : target.Name;
            target.Recipients ??= [];
            target.Params ??= [];
            target.Params = ConfigDefaults.MergeTargetParams(target.Channel, target.Params);
        }
        return this;
    }
}

public sealed class SourceConfig
{
    public string Id { get; set; } = Ids.New("src");
    public string Name { get; set; } = "演示设备";
    public string Protocol { get; set; } = "mock";
    public bool Enabled { get; set; } = true;
    public double PollSeconds { get; set; } = 2;
    public Dictionary<string, JsonElement> Params { get; set; } = [];
    public PhoneReminder PhoneReminder { get; set; } = new();
}

public sealed class PhoneReminder
{
    public bool Enabled { get; set; }
    public int DelaySeconds { get; set; } = 300;
    public string Number { get; set; } = "";
}

public sealed class AlarmRule
{
    public string Id { get; set; } = Ids.New("rule");
    public string Name { get; set; } = "默认报警";
    public string SourceId { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public string Severity { get; set; } = "warning";
    public string MatchCode { get; set; } = "";
    public string MatchKey { get; set; } = "";
    public string MatchValue { get; set; } = "";
    public double CooldownSeconds { get; set; }
    public List<AlarmText> AlarmTexts { get; set; } = [];
    public List<AlarmTextListEntry> AlarmTextLists { get; set; } = [];
    public string MessageTemplate { get; set; } = "【{severity}】{source_name}\n报警编号：{code}\n报警内容：{text}\n时间：{time}";
    public string RecoveryTemplate { get; set; } = "【恢复】{source_name}\n报警编号：{code}\n时间：{time}";
    public string Note { get; set; } = "";
}

public sealed class AlarmText
{
    public string Category { get; set; } = "默认";
    public string Code { get; set; } = "";
    public string InstanceName { get; set; } = "";
    public string FbNumber { get; set; } = "";
    public string TextListName { get; set; } = "";
    public string TextListIndex { get; set; } = "";
    public string Text { get; set; } = "";
}

public sealed class AlarmTextListEntry
{
    public string PlcName { get; set; } = "";
    public string ListName { get; set; } = "";
    public string From { get; set; } = "";
    public string To { get; set; } = "";
    public string Text { get; set; } = "";
}

public sealed class NotificationTarget
{
    public string Id { get; set; } = Ids.New("target");
    public string Name { get; set; } = "Telegram 报警群";
    public string Channel { get; set; } = "telegram";
    public bool Enabled { get; set; }
    public string Token { get; set; } = "";
    public string WebhookUrl { get; set; } = "";
    public List<string> Recipients { get; set; } = [];
    public Dictionary<string, JsonElement> Params { get; set; } = [];
}

public sealed record Signal(bool Active, string Code, string Text, IReadOnlyDictionary<string, object?> Values);
public sealed record RuntimeEvent(string Level, string Message, string SourceId = "", string RuleId = "");

public static class Ids
{
    public static string New(string prefix) => $"{prefix}_{Guid.NewGuid():N}"[..(prefix.Length + 9)];
}

public static class JsonValues
{
    public static string Get(this IReadOnlyDictionary<string, JsonElement> values, string key, string fallback = "")
        => values.TryGetValue(key, out var value) ? value.ToString() : fallback;

    public static int GetInt(this IReadOnlyDictionary<string, JsonElement> values, string key, int fallback = 0)
        => int.TryParse(values.Get(key), out var number) ? number : fallback;

    public static double GetDouble(this IReadOnlyDictionary<string, JsonElement> values, string key, double fallback = 0)
        => double.TryParse(values.Get(key), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var number) ? number : fallback;

    public static JsonElement Element(string value) => JsonSerializer.SerializeToElement(value);
}

public static class ConfigDefaults
{
    private static readonly Dictionary<string, Dictionary<string, string>> SourceDefaults = new(StringComparer.OrdinalIgnoreCase)
    {
        ["mock"] = new() { ["toggle_every"] = "8", ["code"] = "1001", ["text"] = "模拟温度过高" },
        ["s7"] = new() { ["host"] = "192.168.0.10", ["rack"] = "0", ["slot"] = "1", ["db_number"] = "10", ["trigger_byte"] = "0", ["trigger_bit"] = "0", ["code_offset"] = "2", ["text_offset"] = "4", ["text_length"] = "32", ["encoding"] = "utf-8" },
        ["modbus_tcp"] = new() { ["host"] = "192.168.0.10", ["port"] = "502", ["unit_id"] = "1", ["table"] = "coil", ["address"] = "0", ["active_value"] = "1", ["code"] = "modbus-1", ["text"] = "Modbus TCP 报警" },
        ["modbus_rtu"] = new() { ["port"] = "COM3", ["baudrate"] = "9600", ["parity"] = "N", ["stopbits"] = "1", ["bytesize"] = "8", ["unit_id"] = "1", ["table"] = "coil", ["address"] = "0", ["active_value"] = "1", ["code"] = "modbus-rtu-1", ["text"] = "Modbus RTU 报警" },
        ["mqtt"] = new() { ["host"] = "127.0.0.1", ["port"] = "1883", ["topic"] = "factory/alarm", ["qos"] = "0", ["username"] = "", ["password"] = "", ["active_key"] = "active", ["code_key"] = "code", ["text_key"] = "text", ["expected_active"] = "true" },
        ["opcua"] = new() { ["endpoint"] = "opc.tcp://127.0.0.1:4840", ["active_count_node_id"] = "ns=4;s=Alarm.ActiveAlarmCount", ["alarm_code_list_node_id"] = "ns=4;s=Alarm.AlarmCodeList", ["username"] = "", ["password"] = "", ["publishing_interval_ms"] = "250", ["sampling_interval_ms"] = "250", ["subscription_coalesce_ms"] = "50", ["max_alarm_count"] = "1000", ["inactive_code"] = "0", ["text"] = "未配置的 OPC UA 报警码" },
        ["opcua_prodiag"] = new() { ["endpoint"] = "opc.tcp://127.0.0.1:4840", ["event_node_id"] = "", ["username"] = "", ["password"] = "", ["publishing_interval_ms"] = "250", ["event_queue_size"] = "1000", ["condition_refresh"] = "是" },
        ["http_json"] = new() { ["url"] = "http://127.0.0.1:8000/alarm", ["method"] = "GET", ["headers_json"] = "{}", ["active_key"] = "active", ["code_key"] = "code", ["text_key"] = "text" },
        ["tcp_json"] = new() { ["host"] = "127.0.0.1", ["port"] = "9000", ["request_text"] = "read\\n", ["delimiter"] = "\\n", ["active_key"] = "active", ["code_key"] = "code", ["text_key"] = "text" },
        ["udp_json"] = new() { ["host"] = "127.0.0.1", ["port"] = "9001", ["request_text"] = "read", ["active_key"] = "active", ["code_key"] = "code", ["text_key"] = "text" },
        ["serial_text"] = new() { ["port"] = "COM3", ["baudrate"] = "9600", ["request_text"] = "read\\n", ["active_text"] = "ALARM", ["code"] = "serial-1", ["text"] = "串口报警" },
        ["ethernet_ip"] = new() { ["host"] = "192.168.0.10", ["tag"] = "AlarmActive", ["timeout"] = "3", ["code"] = "ethernet-ip-1", ["text"] = "EtherNet/IP 报警" },
        ["ads"] = new() { ["net_id"] = "127.0.0.1.1.1", ["port"] = "851", ["symbol"] = "MAIN.bAlarm", ["code"] = "ads-1", ["text"] = "ADS 报警" }
    };

    private static readonly Dictionary<string, Dictionary<string, string>> TargetDefaults = new(StringComparer.OrdinalIgnoreCase)
    {
        ["telegram"] = InteractionDefaults(),
        ["wecom"] = InteractionDefaults(),
        ["dingtalk"] = InteractionDefaults(),
        ["custom_webhook"] = new() { ["payload_template"] = "{\"text\":\"{message}\"}" }
    };

    private static Dictionary<string, string> InteractionDefaults() => new()
    {
        ["receive_enabled"] = "否", ["allow_plc_write"] = "否", ["alarm_show_buttons"] = "否", ["admin_ids"] = "", ["command_defs"] = "[]",
        ["bot_client_id"] = "", ["bot_client_secret"] = "", ["card_template_id"] = "", ["ding_conversation_id"] = "", ["button_columns"] = "2",
        ["welcome_text"] = "你好，{user}。请选择下方功能。", ["unknown_reply"] = "暂不支持该指令，请选择菜单。"
    };

    public static readonly string[] Protocols = ["mock", "s7", "modbus_tcp", "modbus_rtu", "mqtt", "opcua", "opcua_prodiag", "http_json", "tcp_json", "udp_json", "serial_text", "ethernet_ip", "ads"];
    public static readonly string[] Channels = ["telegram", "dingtalk", "wecom", "feishu", "custom_webhook"];

    public static SourceConfig NewSource() => new() { Params = MergeSourceParams("mock", new Dictionary<string, JsonElement>()) };
    public static AlarmRule NewRule(string sourceId) => new() { SourceId = sourceId, AlarmTexts = Enumerable.Range(0, 30).Select(_ => new AlarmText()).ToList() };
    public static NotificationTarget NewTarget() => new() { Params = MergeTargetParams("telegram", new Dictionary<string, JsonElement>()) };

    public static Dictionary<string, JsonElement> MergeSourceParams(string protocol, IDictionary<string, JsonElement>? values)
    {
        var result = Merge(SourceDefaults.TryGetValue(protocol, out var defaults) ? defaults : new Dictionary<string, string>(), values);
        if (protocol.Equals("opcua", StringComparison.OrdinalIgnoreCase))
            foreach (var obsolete in new[] { "node_id", "expected_active", "value_as_code", "inactive_value", "code" }) result.Remove(obsolete);
        result.TryAdd("timeout", JsonValues.Element("5"));
        return result;
    }

    public static Dictionary<string, JsonElement> MergeTargetParams(string channel, IDictionary<string, JsonElement>? values)
        => Merge(TargetDefaults.TryGetValue(channel, out var defaults) ? defaults : new Dictionary<string, string>(), values);

    private static Dictionary<string, JsonElement> Merge(IDictionary<string, string> defaults, IDictionary<string, JsonElement>? values)
    {
        var result = defaults.ToDictionary(x => x.Key, x => JsonValues.Element(x.Value), StringComparer.OrdinalIgnoreCase);
        if (values is not null) foreach (var (key, value) in values) result[key] = value;
        return result;
    }
}
