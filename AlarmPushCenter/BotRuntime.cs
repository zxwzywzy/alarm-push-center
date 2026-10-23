namespace AlarmPushCenter;

public sealed class BotRuntime : IAsyncDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _task;
    public BotRuntime(AppConfig config, Func<string> status, Func<string> history, Action<RuntimeEvent> log,
        Action<string, string, string>? dingContextDiscovered = null, Func<string, int, BotHistoryPage>? historyPage = null,
        Func<IReadOnlySet<string>>? connectedSources = null)
    {
        var snapshot = ConfigStore.Clone(config);
        _task = Task.WhenAll(snapshot.Targets.Where(BotInteraction.Enabled).Select(target => Task.Run(async () =>
        {
            if (target.Channel == "telegram") await new TelegramReceiver(target, status, log, sources: snapshot.Sources, history: history, historyPage: historyPage, connectedSources: connectedSources).RunAsync(_stop.Token);
            else await new EnterpriseBotReceiver(target, snapshot.Sources, status, history, log, dingContextDiscovered, historyPage, connectedSources).RunAsync(_stop.Token);
        })));
    }
    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        try { await _task; } catch (OperationCanceledException) { }
        finally { _stop.Dispose(); }
    }
}
