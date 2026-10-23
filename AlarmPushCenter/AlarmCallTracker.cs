using System.Diagnostics;

namespace AlarmPushCenter;

// Shared by OpenWrt and Windows; the host owns the phone gateway and monitor lifecycle.
public sealed class AlarmCallTracker : IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<(string Source, string Rule, string Code), PendingCall> _active = [];
    private readonly Func<long> _clock;
    private readonly Func<long, TimeSpan> _elapsed;
    private bool _accepting = true;
    public AlarmCallTracker() : this(Stopwatch.GetTimestamp, Stopwatch.GetElapsedTime) { }
    public AlarmCallTracker(Func<long> clock, Func<long, TimeSpan> elapsed) { _clock = clock; _elapsed = elapsed; }

    public void Observe(SourceConfig source, AlarmRule rule, IReadOnlyDictionary<string, Signal> current)
    {
        lock (_gate)
        {
            if (!_accepting) return;
            foreach (var key in _active.Keys.Where(k => k.Source == source.Id && k.Rule == rule.Id &&
                (!source.PhoneReminder.Enabled || !current.ContainsKey(k.Code))).ToArray()) Remove(key);
            if (!source.PhoneReminder.Enabled) return;
            foreach (var (code, signal) in current)
            {
                var key = (source.Id, rule.Id, code.ToUpperInvariant());
                if (!_active.TryGetValue(key, out var pending))
                {
                    var alreadyAttempted = _active.Values.Any(p => p.Source.Id == source.Id &&
                        string.Equals(p.Code, signal.Code, StringComparison.OrdinalIgnoreCase) && p.Attempted);
                    _active[key] = pending = new(source, rule.Id, signal.Code, _clock()) { Attempted = alreadyAttempted };
                }
                pending.LastSeen = _clock();
            }
        }
    }

    public PendingCall? Due()
    {
        lock (_gate)
            return _active.Values.Where(p => !p.Attempted && !p.Cancellation.IsCancellationRequested &&
                _elapsed(p.Started).TotalSeconds >= p.Source.PhoneReminder.DelaySeconds &&
                _elapsed(p.LastSeen).TotalSeconds <= Math.Max(10, p.Source.PollSeconds * 3))
                .OrderBy(p => p.Started).FirstOrDefault();
    }
    public bool Claim(PendingCall pending)
    {
        lock (_gate)
        {
            if (pending.Attempted || pending.Cancellation.IsCancellationRequested ||
                _elapsed(pending.LastSeen).TotalSeconds > Math.Max(10, pending.Source.PollSeconds * 3)) return false;
            foreach (var entry in _active.Values.Where(p => p.Source.Id == pending.Source.Id &&
                string.Equals(p.Code, pending.Code, StringComparison.OrdinalIgnoreCase))) entry.Attempted = true;
            return true;
        }
    }
    public void Clear(string? sourceId = null)
    {
        lock (_gate)
            foreach (var key in _active.Keys.Where(k => sourceId is null || k.Source == sourceId).ToArray()) Remove(key);
    }
    public void Begin() { lock (_gate) { Clear(); _accepting = true; } }
    public void End() { lock (_gate) { _accepting = false; Clear(); } }
    private void Remove((string Source, string Rule, string Code) key)
    {
        if (_active.Remove(key, out var pending)) pending.Cancellation.Cancel();
    }
    public void Dispose() => End();
    public sealed class PendingCall(SourceConfig source, string ruleId, string code, long started)
    {
        public SourceConfig Source { get; } = source;
        public string RuleId { get; } = ruleId;
        public string Code { get; } = code;
        public long Started { get; } = started;
        public long LastSeen { get; set; } = started;
        public bool Attempted { get; set; }
        public CancellationTokenSource Cancellation { get; } = new();
    }
}
