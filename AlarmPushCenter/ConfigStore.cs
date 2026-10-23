using System.Text.Json;
using System.Text.Json.Serialization;

namespace AlarmPushCenter;

public sealed class ConfigStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public ConfigStore(string path) => Path = path;

    public string Path { get; }

    public static AppConfig Clone(AppConfig config) => JsonSerializer.Deserialize<AppConfig>(JsonSerializer.Serialize(config, Options), Options)!;

    public AppConfig Load()
    {
        if (!File.Exists(Path)) return new AppConfig().Normalize();
        try
        {
            var config = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(Path), Options);
            return (config ?? new AppConfig()).Normalize();
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"配置文件格式无效：{ex.Message}", ex);
        }
    }

    public void Save(AppConfig config)
    {
        config = Clone(config).Normalize();
        ConfigValidator.Validate(config);
        var folder = System.IO.Path.GetDirectoryName(Path);
        if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);
        var temporaryPath = Path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, config, Options);
                stream.Flush(true);
            }
            if (File.Exists(Path)) File.Replace(temporaryPath, Path, Path + ".bak");
            else File.Move(temporaryPath, Path);
        }
        finally { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
    }
}

public static class MessageRenderer
{
    public static string Render(string template, SourceConfig source, Signal signal, AlarmRule? rule = null)
    {
        var alarmText = ResolveAlarmEntry(rule, signal);
        var text = OpcUaProDiagSource.ResolveAssociatedValuePlaceholders(alarmText?.Text.Trim() ?? signal.Text, signal.Values, rule?.AlarmTextLists, alarmText?.TextListName, alarmText?.TextListIndex);
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["source_name"] = source.Name,
            ["source_protocol"] = source.Protocol,
            ["source_id"] = source.Id,
            ["severity"] = rule?.Severity ?? "warning",
            ["rule_name"] = rule?.Name ?? "默认报警",
            ["code"] = alarmText?.Code?.Trim() is { Length: > 0 } resolvedCode ? resolvedCode : signal.Code,
            ["text"] = text,
            ["instance_name"] = alarmText?.InstanceName?.Trim() ?? "",
            ["time"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
        };
        foreach (var (key, value) in signal.Values) values.TryAdd(key, Convert.ToString(value) ?? "");

        return System.Text.RegularExpressions.Regex.Replace(template ?? "", "\\{(?<key>[A-Za-z0-9_]+)\\}", match =>
        {
            var key = match.Groups["key"].Value;
            return values.TryGetValue(key, out var value) ? value : match.Value;
        });
    }

    internal static string ResolveAlarmText(AlarmRule? rule, Signal signal)
    {
        var text = ResolveAlarmEntry(rule, signal)?.Text.Trim() ?? signal.Text;
        var alarmText = ResolveAlarmEntry(rule, signal);
        return OpcUaProDiagSource.ResolveAssociatedValuePlaceholders(text, signal.Values, rule?.AlarmTextLists, alarmText?.TextListName, alarmText?.TextListIndex);
    }

    internal static string ResolveAlarmCode(AlarmRule? rule, Signal signal)
        => ResolveAlarmEntry(rule, signal)?.Code?.Trim() is { Length: > 0 } code ? code : signal.Code;

    private static AlarmText? ResolveAlarmEntry(AlarmRule? rule, Signal signal)
    {
        var texts = rule?.AlarmTexts;
        var exact = texts?.FirstOrDefault(x =>
            string.Equals(x.Code?.Trim(), signal.Code, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(x.Text));
        if (exact is not null) return exact;
        if (texts is null || !signal.Values.TryGetValue("supervision_id", out var supervisionValue)) return null;
        var supervisionId = Convert.ToString(supervisionValue)?.Trim();
        if (string.IsNullOrWhiteSpace(supervisionId)) return null;
        var candidates = texts.Where(x => !string.IsNullOrWhiteSpace(x.Text) && AlarmIdEndsWith(x.Code, supervisionId)).ToList();
        if (signal.Values.TryGetValue("fb_number", out var fbValue))
        {
            var fbNumber = Convert.ToString(fbValue)?.Trim();
            var byBlock = candidates.Where(x => string.Equals(x.FbNumber?.Trim(), fbNumber, StringComparison.OrdinalIgnoreCase)).ToList();
            if (byBlock.Count == 1) return byBlock[0];
            if (byBlock.Count > 1) return null;
        }
        return candidates.Count == 1 ? candidates[0] : null;
    }

    private static bool AlarmIdEndsWith(string? alarmId, string supervisionId)
    {
        var normalized = new string((alarmId ?? "").Where(Uri.IsHexDigit).ToArray());
        return normalized.Length >= 4 && normalized.EndsWith(supervisionId.PadLeft(4, '0'), StringComparison.OrdinalIgnoreCase);
    }
}
