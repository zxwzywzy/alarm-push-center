using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AlarmPushCenter;

internal static class DesktopPhoneChecks
{
    public static async Task RunAsync()
    {
        var folder = Path.Combine(Path.GetTempPath(), "desktop-phone-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var passed = 0;
        void Check(bool condition, string name) { if (!condition) throw new InvalidOperationException(name); passed++; }
        try
        {
            var configPath = Path.Combine(folder, "config.json");
            var gateway = new UfiGatewaySettings(configPath);
            gateway.Save(new() { Enabled = true, Token = "test-passphrase", RingSeconds = 5, MinimumIntervalSeconds = 60 });
            var reloaded = new UfiGatewaySettings(configPath);
            Check(reloaded.Value.Enabled && reloaded.Value.Token == "test-passphrase", "Desktop gateway is saved beside this configuration");
            gateway.Save(gateway.Value with { Token = "" });
            Check(gateway.Value.Token == "test-passphrase", "Blank desktop passphrase preserves saved credential");
            using var calls = new AlarmCallTracker();
            using var transport = new FakePocketWifi();
            var events = new List<RuntimeEvent>();
            using var phone = new UfiPhoneController(gateway, calls, e => { lock (events) events.Add(e); }, new HttpClient(transport));
            await phone.ProbeAsync(CancellationToken.None);
            Check(transport.Commands.SequenceEqual(["AT+CPIN?", "AT+CLCC"]), "Desktop connection check cannot dial");
            Check(transport.ValidAuth, "Desktop sends the SHA256 login passphrase");
            transport.Commands.Clear();

            var source = new SourceConfig { Id = "desktop", Name = "Desktop test", PollSeconds = .2,
                PhoneReminder = new() { Enabled = true, DelaySeconds = 1, Number = "13800138000" } };
            var config = new AppConfig { Sources = [source], Rules = [new AlarmRule { Id = "rule", SourceId = source.Id }], Targets = [] };
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var dialed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            transport.Dialed = () => { dialed.TrySetResult(); deadline.Cancel(); };
            await using var monitor = new AlarmMonitor(config, _ => { }, s => new ConstantAlarm(s), calls: calls);
            monitor.Start();
            var loop = phone.RunAsync(deadline.Token);
            await loop;
            await monitor.StopAsync();
            Check(dialed.Task.IsCompletedSuccessfully, "Desktop alarm monitor triggers the direct pocket WiFi controller after threshold");
            Check(transport.Commands.SequenceEqual(["AT+CPIN?", "AT+CLCC", "ATD13800138000;", "ATH"]), "Stopping a desktop dial sends hangup exactly once");
            Check(calls.Due() is null, "Desktop stop removes all pending reminder timers");
            lock (events) Check(events.All(e => !e.Message.Contains("test-passphrase") && !e.Message.Contains("13800138000")), "Desktop phone logs never expose the passphrase or full phone number");
            var before = transport.Commands.Count;
            try { await phone.CallAsync(source, CancellationToken.None); throw new InvalidOperationException("Cooldown was bypassed"); }
            catch (InvalidOperationException ex) when (ex.Message.Contains("间隔保护")) { passed++; }
            Check(transport.Commands.Count == before, "Cooldown rejects repeated desktop calls without sending AT");
            Console.WriteLine($"desktop phone checks passed: {passed}");
        }
        finally { Directory.Delete(folder, true); }
    }

    private sealed class ConstantAlarm(SourceConfig source) : SourceAdapter(source)
    {
        public override Task<Signal> ReadAsync(CancellationToken cancellationToken)
            => Task.FromResult(new Signal(true, "1001", "test", new Dictionary<string, object?>()));
    }

    private sealed class FakePocketWifi : HttpMessageHandler
    {
        public List<string> Commands { get; } = [];
        public bool ValidAuth { get; private set; } = true;
        public Action? Dialed { get; set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            ValidAuth &= request.Headers.GetValues("authorization").Single() ==
                Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("test-passphrase")));
            object body;
            if (request.RequestUri!.AbsolutePath == "/api/version_info") body = new { model = "F50", app_ver = "4.0.5" };
            else
            {
                var field = request.RequestUri.Query.TrimStart('?').Split('&').Single(x => x.StartsWith("command="));
                var command = Uri.UnescapeDataString(field["command=".Length..]);
                Commands.Add(command);
                if (command.StartsWith("ATD")) Dialed?.Invoke();
                body = new { result = command == "AT+CPIN?" ? "+CPIN: READY OK" : " OK" };
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") });
        }
    }
}
