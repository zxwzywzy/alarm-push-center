using System.Diagnostics;

namespace AlarmPushCenter;

public sealed record TelemetryPoint(DateTime Time, bool Running, int Connected, int Enabled, int ActiveAlarms,
    long NewAlarms, long Recoveries, double ReadsPerSecond, double AverageReadMs, double ReadSuccessPercent,
    double CpuPercent, double MemoryMb, int QueueDepth, long SentDeliveries, long FailedDeliveries);

// One bounded series is shared by the web console and Windows client. No remote polling or database writes.
public sealed class RuntimeTelemetry
{
    private readonly object _gate = new();
    private readonly Queue<TelemetryPoint> _points = new();
    private AlarmMonitor? _previousMonitor;
    private AlarmMonitor.CollectionMetrics? _previous;
    private long _lastSample = Stopwatch.GetTimestamp();
    private TimeSpan _lastCpu = ReadCpu();
    private static TimeSpan ReadCpu() { using var process = Process.GetCurrentProcess(); return process.TotalProcessorTime; }

    public void Sample(AlarmMonitor? monitor, int enabled)
    {
        lock (_gate)
        {
            var seconds = Math.Max(.001, Stopwatch.GetElapsedTime(_lastSample).TotalSeconds);
            if (_points.Count > 0 && seconds < 5) return;
            var cpu = 0d; var memory = 0d;
            using (var process = Process.GetCurrentProcess())
            {
                var totalCpu = process.TotalProcessorTime;
                cpu = Math.Clamp((totalCpu - _lastCpu).TotalSeconds / seconds / Environment.ProcessorCount * 100, 0, 100);
                memory = process.WorkingSet64 / 1048576d;
                _lastCpu = totalCpu;
            }
            var now = monitor?.Metrics();
            var previous = ReferenceEquals(monitor, _previousMonitor) ? _previous : null;
            long Delta(long value, long old) => Math.Max(0, value - old);
            var reads = Delta(now?.Reads ?? 0, previous?.Reads ?? 0);
            var failed = Delta(now?.FailedReads ?? 0, previous?.FailedReads ?? 0);
            var microseconds = Delta(now?.ReadMicroseconds ?? 0, previous?.ReadMicroseconds ?? 0);
            var running = monitor is not null;
            _points.Enqueue(new(DateTime.UtcNow, running, running ? now?.Connected ?? 0 : 0, enabled,
                running ? now?.ActiveAlarms ?? 0 : 0, Delta(now?.AlarmEvents ?? 0, previous?.AlarmEvents ?? 0),
                Delta(now?.RecoveryEvents ?? 0, previous?.RecoveryEvents ?? 0), Math.Round(reads / seconds, 2),
                reads == 0 ? 0 : Math.Round(microseconds / 1000d / reads, 2),
                reads == 0 ? 0 : Math.Round(Math.Clamp((reads - failed) * 100d / reads, 0, 100), 1),
                Math.Round(cpu, 2), Math.Round(memory, 1), now?.QueueDepth ?? 0,
                Delta(now?.SentDeliveries ?? 0, previous?.SentDeliveries ?? 0), Delta(now?.FailedDeliveries ?? 0, previous?.FailedDeliveries ?? 0)));
            while (_points.Count > 721) _points.Dequeue();
            _previousMonitor = monitor; _previous = now; _lastSample = Stopwatch.GetTimestamp();
        }
    }
    public IReadOnlyList<TelemetryPoint> Read(int minutes = 15)
    {
        lock (_gate)
        {
            var since = DateTime.UtcNow.AddMinutes(-Math.Clamp(minutes, 1, 60));
            return _points.Where(x => x.Time >= since).ToArray();
        }
    }
}
