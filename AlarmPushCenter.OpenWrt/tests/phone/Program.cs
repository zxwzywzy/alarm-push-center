using AlarmPushCenter;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Security.Cryptography;

var directory = Path.Combine(Path.GetTempPath(), "alarm-phone-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(directory);
Environment.SetEnvironmentVariable("ALARM_CONFIG", Path.Combine(directory, "config.json"));
var passed = 0;
void Assert(bool value, string name) { if (!value) throw new Exception(name); passed++; }
async Task Reject(Func<Task> action, string reason)
{
    try { await action(); throw new Exception("should reject: " + reason); }
    catch (InvalidOperationException) { passed++; }
}
try
{
    long now = 0;
    using var tracker = new AlarmCallTracker(() => now, since => TimeSpan.FromSeconds(now - since));
    var source = new SourceConfig { Id = "src", PollSeconds = 0.2,
        PhoneReminder = new() { Enabled = true, DelaySeconds = 10, Number = "13800138000" } };
    var rule = new AlarmRule { Id = "rule", SourceId = source.Id };
    Dictionary<string, Signal> Signals(string code = "a") => new(StringComparer.OrdinalIgnoreCase) { [code] = new(true, code, "test", new Dictionary<string, object?>()) };
    tracker.Observe(source, rule, Signals());
    now = 9; tracker.Observe(source, rule, Signals("A"));
    Assert(tracker.Due() is null, "not before threshold / case-insensitive code");
    now = 10; tracker.Observe(source, rule, Signals());
    var due = tracker.Due()!;
    Assert(due is not null, "continuous alarm becomes due");
    tracker.Observe(source, new AlarmRule { Id = "overlapping-rule", SourceId = source.Id }, Signals());
    Assert(tracker.Claim(due!), "single claimant");
    Assert(!tracker.Claim(due!), "cannot claim twice");
    Assert(tracker.Due() is null, "no repeated call while active");
    now = 20; tracker.Observe(source, rule, Signals());
    Assert(tracker.Due() is null, "overlapping rules cannot call the same device alarm twice");
    tracker.Observe(source, new AlarmRule { Id = "overlapping-rule", SourceId = source.Id }, new Dictionary<string, Signal>());
    tracker.Observe(source, rule, new Dictionary<string, Signal>());
    Assert(due!.Cancellation.IsCancellationRequested, "recovery cancels in-flight call");
    tracker.Observe(source, rule, Signals()); now = 29;
    Assert(tracker.Due() is null, "reappearance starts a new timer");
    tracker.Clear(source.Id); now = 35;
    Assert(tracker.Due() is null, "disconnection cancels pending timers");
    tracker.Observe(source, rule, Signals()); now = 50;
    Assert(tracker.Due() is null, "stale reads must not initiate calls");
    tracker.Observe(source, rule, Signals());
    Assert(tracker.Due() is not null, "fresh data resumes valid active alarm");
    tracker.Clear();
    Assert(tracker.Due() is null, "service stop removes pending alarms");
    tracker.End(); tracker.Observe(source, rule, Signals()); now = 70;
    Assert(tracker.Due() is null, "a late read cannot re-add timers after stop");
    tracker.Begin(); tracker.Observe(source, rule, Signals()); now = 80; tracker.Observe(source, rule, Signals());
    Assert(tracker.Due() is not null, "restart accepts a fresh continuous alarm");
    tracker.Clear();

    var gateway = new UfiGatewaySettings();
    gateway.Save(new() { Enabled = true, Token = "test-only-token", RingSeconds = 5, MinimumIntervalSeconds = 60 });
    gateway.Save(gateway.Value with { Token = "" });
    Assert(gateway.Value.Token == "test-only-token", "blank credentials preserve existing token");
    Assert(!JsonSerializer.Serialize(gateway.PublicView()).Contains("test-only-token"), "public view redacts credentials");
    foreach (var address in new[] { "http://8.8.8.8", "http://127.0.0.1:2333", "http://192.168.0.1:2333/other", "http://user:pass@192.168.0.1:2333", "http://example.com" })
        await Reject(() => { new UfiGatewayOptions { Address = address }.Validate(); return Task.CompletedTask; }, address);
    foreach (var number in new[] { "123'&&x", "13800138000;ATH", "13800138000\r\nAT", "" })
        await Reject(() => { ConfigValidator.ValidatePhoneReminder(new() { Enabled = true, Number = number }); return Task.CompletedTask; }, "unsafe recipient");

    using var host = new RuntimeHost(new ConsoleSettings(), tracker);
    var blockedTransport = new FakeUfi();
    using (var phone = new UfiPhoneService(gateway, tracker, host, new(blockedTransport), () => false))
        await Reject(() => phone.CallAsync(source, CancellationToken.None), "desktop / unsupported router");
    Assert(blockedTransport.Commands.Count == 0, "unsupported host never calls a modem");

    var transport = new FakeUfi();
    using var cancellation = new CancellationTokenSource();
    transport.OnDial = () => cancellation.Cancel();
    using (var phone = new UfiPhoneService(gateway, tracker, host, new(transport), () => true))
    {
        try { await phone.CallAsync(source, cancellation.Token); throw new Exception("expected cancellation"); }
        catch (OperationCanceledException) { passed++; }
        Assert(transport.Commands.SequenceEqual(new[] { "AT+CPIN?", "AT+CLCC", "ATD13800138000;", "ATH" }), "cancelled call still hangs up exactly once");
        Assert(transport.ValidAuth, "UFI auth and signature are included");
        await Reject(() => phone.CallAsync(source, CancellationToken.None), "global call interval");
        Assert(transport.Commands.Count == 4, "cooldown prevents another dial");
    }
    var busy = new FakeUfi { Busy = true };
    using (var phone = new UfiPhoneService(gateway, tracker, host, new(busy), () => true))
        await Reject(() => phone.CallAsync(source, CancellationToken.None), "existing call");
    Assert(!busy.Commands.Any(c => c.StartsWith("ATD") || c == "ATH"), "busy device is neither dialed nor hung up");

    var disconnected = new FakeUfi { Fail = true };
    using (var phone = new UfiPhoneService(gateway, tracker, host, new(disconnected), () => true))
    {
        try { await phone.CallAsync(source, CancellationToken.None); throw new Exception("expected disconnect"); }
        catch (HttpRequestException) { passed++; }
    }
    Assert(disconnected.Commands.Count == 0, "absent pocket WiFi never reaches dial");
    var disabled = new FakeUfi(); gateway.Save(gateway.Value with { Enabled = false });
    using (var phone = new UfiPhoneService(gateway, tracker, host, new(disabled), () => true))
        await Reject(() => phone.CallAsync(source, CancellationToken.None), "gateway disabled");
    Assert(disabled.Commands.Count == 0, "disabled gateway never calls");
    var adminTransport = new FakeUfi { ExpectedAuthorization = "8c6976e5b5410415bde908bd4dee15dfb167a9c873fc4bb8a81f6f2ab448a918" };
    using (var adminHttp = new HttpClient(adminTransport))
    {
        var adminClient = new UfiPhoneClient(adminHttp, new UfiGatewayOptions { Token = "admin" });
        Assert(await adminClient.ProbeAsync(CancellationToken.None) == "F50", "plain admin input is transformed to the real UFI header");
        Assert(adminTransport.ValidAuth, "admin authentication uses SHA256 rather than the raw passphrase");
        Assert(adminTransport.Commands.SequenceEqual(new[] { "AT+CPIN?", "AT+CLCC" }), "connection checks never dial");
    }
    Console.WriteLine($"phone checks passed: {passed}");
}
finally { Directory.Delete(directory, true); }

sealed class FakeUfi : HttpMessageHandler
{
    public List<string> Commands { get; } = [];
    public bool Busy, Fail;
    public bool ValidAuth = true;
    public string ExpectedAuthorization = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("test-only-token")));
    public Action? OnDial;
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        if (Fail) throw new HttpRequestException("not connected");
        var timestamp = request.Headers.GetValues("kano-t").Single();
        var authenticated = request.Headers.GetValues("authorization").Single() == ExpectedAuthorization &&
            request.Headers.GetValues("kano-sign").Single() == UfiPhoneClient.Signature("GET", request.RequestUri!.AbsolutePath, timestamp);
        ValidAuth &= authenticated;
        if (!authenticated) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
        if (request.RequestUri!.AbsolutePath == "/api/version_info") return Reply(new { model = "F50", app_ver = "4.0.5" });
        if (request.RequestUri.AbsolutePath != "/api/AT") throw new Exception("unexpected external request");
        var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(request.RequestUri.Query);
        var command = query["command"].ToString(); Commands.Add(command);
        if (command.StartsWith("ATD")) OnDial?.Invoke();
        return Reply(new { result = command == "AT+CPIN?" ? "+CPIN: READY OK" : Busy && command == "AT+CLCC" ? "+CLCC: 1,0,0,0,0 OK" : " OK" });
    }
    private static Task<HttpResponseMessage> Reply(object data) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent(JsonSerializer.Serialize(data), Encoding.UTF8, "application/json") });
}
