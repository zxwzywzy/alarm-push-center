using System.Text;
using System.Text.Json;

namespace AlarmPushCenter;

public sealed record AlarmHistoryEntry(DateTime Time, string State, string SourceName, string Code, string Text, string Severity);

public sealed class AlarmHistoryStore
{
    private readonly string _path;
    private readonly object _gate = new();
    private AlarmHistoryEntry[]? _cachedEntries;
    private long _cachedLength;
    private DateTime _cachedWriteTime;
    private static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    public AlarmHistoryStore(string configPath) => _path = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(configPath))!, "alarm-history.jsonl");

    public void Record(AlarmHistoryEntry entry)
    {
        try
        {
            lock (_gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                File.AppendAllText(_path, JsonSerializer.Serialize(entry, Options) + Environment.NewLine, new UTF8Encoding(false));
                _cachedEntries = null;
                TrimIfNeeded();
            }
        }
        catch { /* Alarm history failure must not interrupt PLC sampling. */ }
    }

    public string FormatRecent(int count = 10)
    {
        var entries = ReadRecent("", Math.Clamp(count, 1, 30));
        if (entries.Count == 0) return "暂无历史报警。";
        var lines = new List<string> { $"最近 {entries.Count} 条报警记录" };
        lines.AddRange(entries.Select(x =>
            $"{(x.State == "recovery" ? "✅ 恢复" : "⚠️ 报警")} {x.Time:MM-dd HH:mm:ss}\n{x.SourceName} · {x.Code} · {x.Text}"));
        return string.Join("\n\n", lines);
    }

    public IReadOnlyList<AlarmHistoryEntry> ReadRecent(string sourceName, int maximum = 20)
    {
        try
        {
            lock (_gate)
            {
                return LoadEntries().Where(item => string.IsNullOrWhiteSpace(sourceName) ||
                    string.Equals(item.SourceName, sourceName, StringComparison.OrdinalIgnoreCase))
                    .Take(Math.Clamp(maximum, 1, 5000)).ToArray();
            }
        }
        catch { return []; }
    }

    // Called under _gate. Repeated dashboard polls only stat the file; parse and sort
    // again after a local write or a change made by another process.
    private AlarmHistoryEntry[] LoadEntries()
    {
        var info = new FileInfo(_path);
        if (!info.Exists) { _cachedEntries = null; return []; }
        if (_cachedEntries is not null && info.Length == _cachedLength && info.LastWriteTimeUtc == _cachedWriteTime)
            return _cachedEntries;
        var entries = new List<AlarmHistoryEntry>();
        foreach (var line in File.ReadLines(_path))
        {
            try { if (JsonSerializer.Deserialize<AlarmHistoryEntry>(line, Options) is { } item) entries.Add(item); }
            catch (JsonException) { }
        }
        _cachedLength = info.Length;
        _cachedWriteTime = info.LastWriteTimeUtc;
        return _cachedEntries = entries.OrderByDescending(x => x.Time).ToArray();
    }

    public string FormatPage(string sourceName, int page, int pageSize = 10, int maximum = 20)
    {
        pageSize = Math.Clamp(pageSize, 1, 10);
        var entries = ReadRecent(sourceName, Math.Clamp(maximum, pageSize, 100));
        var pageCount = Math.Max(1, (entries.Count + pageSize - 1) / pageSize);
        page = Math.Clamp(page, 0, pageCount - 1);
        var current = entries.Skip(page * pageSize).Take(pageSize).ToArray();
        if (current.Length == 0) return $"设备「{sourceName}」暂无历史报警。";
        var lines = new List<string> { $"设备：{sourceName}\n历史报警 {entries.Count} 条 · 第 {page + 1}/{pageCount} 页" };
        lines.AddRange(current.Select(x => $"{(x.State == "recovery" ? "✅ 恢复" : "⚠️ 报警")} {x.Time:MM-dd HH:mm:ss}\n{x.Code} · {x.Text}"));
        return string.Join("\n\n", lines);
    }

    public int DeleteForSource(string sourceName)
    {
        lock (_gate)
        {
            if (!File.Exists(_path)) return 0;
            var kept = new List<string>();
            var deleted = 0;
            foreach (var line in File.ReadLines(_path))
            {
                try
                {
                    var item = JsonSerializer.Deserialize<AlarmHistoryEntry>(line, Options);
                    if (item is not null && string.Equals(item.SourceName, sourceName, StringComparison.OrdinalIgnoreCase)) { deleted++; continue; }
                }
                catch (JsonException) { }
                kept.Add(line);
            }
            Rewrite(kept);
            return deleted;
        }
    }

    public int DeleteAll()
    {
        lock (_gate)
        {
            var deleted = LoadEntries().Length;
            if (File.Exists(_path)) Rewrite([]);
            return deleted;
        }
    }

    private void Rewrite(IEnumerable<string> lines)
    {
        var temporaryPath = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                using (var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true))
                    foreach (var line in lines) writer.WriteLine(line);
                stream.Flush(true);
            }
            File.Move(temporaryPath, _path, true);
            _cachedEntries = null;
        }
        finally { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
    }

    private void TrimIfNeeded()
    {
        var info = new FileInfo(_path);
        if (info.Length <= 5 * 1024 * 1024) return;
        var lines = File.ReadLines(_path).TakeLast(5000).ToArray();
        Rewrite(lines);
    }
}
