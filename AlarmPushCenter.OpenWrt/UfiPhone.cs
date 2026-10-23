namespace AlarmPushCenter;

// The OpenWrt host retains its platform guard; the desktop uses the shared controller directly.
public sealed class UfiPhoneService : BackgroundService
{
    public bool OperationBusy => _controller.OperationBusy;
    private readonly UfiPhoneController _controller;
    public UfiPhoneService(UfiGatewaySettings settings, AlarmCallTracker calls, RuntimeHost runtime)
        : this(settings, calls, runtime, null, () => IsOpenWrt) { }
    public UfiPhoneService(UfiGatewaySettings settings, AlarmCallTracker calls, RuntimeHost runtime,
        HttpClient? http, Func<bool> isRouter)
    {
        _controller = new(settings, calls,
            e => runtime.RecordPhoneEvent(e.Level, e.Message, e.SourceId), http, isRouter);
    }
    public static bool IsOpenWrt => UfiPhoneController.IsOpenWrt;
    public bool GatewayEnabled => _controller.GatewayEnabled;
    public object Status() => _controller.Status();
    public Task<object> ProbeAsync(CancellationToken token) => _controller.ProbeAsync(token);
    public Task<object> CallAsync(SourceConfig source, CancellationToken token, AlarmCallTracker.PendingCall? pending = null)
        => _controller.CallAsync(source, token, pending);
    protected override Task ExecuteAsync(CancellationToken stoppingToken) => _controller.RunAsync(stoppingToken);
    public override void Dispose() { _controller.Dispose(); base.Dispose(); }
}
