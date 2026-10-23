using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AlarmPushCenter;

public sealed record UfiGatewayOptions
{
    public bool Enabled { get; init; }
    public string Address { get; init; } = "http://192.168.0.1:2333";
    public string Token { get; init; } = "";
    public int Slot { get; init; }
    public int RingSeconds { get; init; } = 25;
    public int MinimumIntervalSeconds { get; init; } = 300;

    public Uri Validate()
    {
        if (!Uri.TryCreate(Address, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") ||
            uri.AbsolutePath != "/" || uri.Query != "" || uri.Fragment != "" || uri.UserInfo != "" ||
            !IPAddress.TryParse(uri.Host, out var ip) || ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            throw new InvalidOperationException("随身 WiFi 地址需为内网 IPv4 地址和端口，例如 http://192.168.0.1:2333。");
        var bytes = ip.GetAddressBytes();
        if (!(bytes[0] == 10 || bytes[0] == 192 && bytes[1] == 168 || bytes[0] == 172 && bytes[1] is >= 16 and <= 31))
            throw new InvalidOperationException("电话网关仅允许当前设备可达的内网随身 WiFi。");
        if (Slot is < 0 or > 1 || RingSeconds is < 5 or > 60 || MinimumIntervalSeconds is < 60 or > 3600 || Token.Length > 256)
            throw new InvalidOperationException("AT 槽位为 0/1，最长呼叫为 5～60 秒，两次呼叫间隔为 60～3600 秒。");
        return uri;
    }
}

public sealed class UfiGatewaySettings
{
    private readonly object _gate = new();
    private readonly string _path;
    private UfiGatewayOptions _value;
    private CancellationTokenSource _changed = new();
    public UfiGatewaySettings(string? configPath = null)
    {
        var config = configPath ?? Environment.GetEnvironmentVariable("ALARM_CONFIG") ?? "/etc/alarm-push-center/config.json";
        _path = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(config))!, "phone-gateway.json");
        _value = File.Exists(_path) ? JsonSerializer.Deserialize<UfiGatewayOptions>(File.ReadAllText(_path)) ?? new() : new();
    }
    public UfiGatewayOptions Value { get { lock (_gate) return _value; } }
    public CancellationToken Changed { get { lock (_gate) return _changed.Token; } }
    public (UfiGatewayOptions Options, CancellationToken Changed) Snapshot() { lock (_gate) return (_value, _changed.Token); }
    public object PublicView() => new { settings = Value with { Token = "" }, token_configured = Value.Token.Length > 0,
        router_supported = UfiPhoneController.IsOpenWrt };
    public void Save(UfiGatewayOptions input)
    {
        lock (_gate)
        {
            input = input with { Token = string.IsNullOrEmpty(input.Token) ? _value.Token : input.Token.Trim(), Address = input.Address.Trim().TrimEnd('/') };
            input.Validate();
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temp = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temp, JsonSerializer.Serialize(input));
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                File.Move(temp, _path, true);
                _value = input;
                var previous = _changed;
                _changed = new();
                previous.Cancel();
                previous.Dispose();
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
    }
}

// Protocol interoperability based on UFI-TOOLS modules/at/atModule.kt and modules/auth/KanoAuth.kt.
// https://github.com/kanoqwq/UFI-TOOLS (reference revision cf83b815).
// No generic command endpoint is exposed by the alarm console.
public sealed class UfiPhoneClient(HttpClient http, UfiGatewayOptions options)
{
    public async Task<string> ProbeAsync(CancellationToken token)
    {
        var json = await RequestAsync("/api/version_info", token);
        if (!json.TryGetProperty("model", out var model) || !json.TryGetProperty("app_ver", out _) ||
            string.IsNullOrWhiteSpace(model.GetString()))
            throw new InvalidOperationException("该地址未识别为 UFI-TOOLS 随身 WiFi。");
        var ready = await AtAsync("AT+CPIN?", token);
        if (!Regex.IsMatch(ready, @"\+CPIN:\s*READY\b", RegexOptions.IgnoreCase))
            throw new InvalidOperationException("随身 WiFi 已连接，但当前 AT 槽位的 SIM 未就绪，请检查卡和槽位。");
        await EnsureIdleAsync(token);
        return model.GetString()!;
    }
    public async Task EnsureIdleAsync(CancellationToken token)
    {
        var calls = await AtAsync("AT+CLCC", token);
        if (Regex.IsMatch(calls, @"\+CLCC:\s*\d+,"))
            throw new InvalidOperationException("随身 WiFi 正在通话，本次提醒等待空闲。");
    }
    public async Task<string> AtAsync(string command, CancellationToken token)
    {
        var json = await RequestAsync($"/api/AT?slot={options.Slot}&command={Uri.EscapeDataString(command)}", token);
        var result = json.TryGetProperty("result", out var value) ? value.GetString() ?? "" : "";
        if (!Regex.IsMatch(result, @"\bOK\s*$", RegexOptions.IgnoreCase) ||
            Regex.IsMatch(result, @"ERROR|NO CARRIER|NO ANSWER|BUSY|NO DIAL", RegexOptions.IgnoreCase))
            throw new InvalidOperationException("随身 WiFi 未接受电话指令，请检查 SIM 语音业务、VoLTE 和固件支持。");
        return result;
    }
    private async Task<JsonElement> RequestAsync(string path, CancellationToken token)
    {
        var address = options.Validate();
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(address, path));
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
        // The UI accepts a plain login passphrase; UFI-TOOLS expects its lowercase SHA-256 digest.
        request.Headers.TryAddWithoutValidation("authorization", Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(options.Token.Trim()))));
        request.Headers.TryAddWithoutValidation("kano-t", timestamp);
        request.Headers.TryAddWithoutValidation("kano-sign", Signature("GET", request.RequestUri!.AbsolutePath, timestamp));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new InvalidOperationException("随身 WiFi 口令验证失败，请填写 UFI-TOOLS 登录口令。");
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException("随身 WiFi 电话接口不可用，请检查地址、端口和 UFI-TOOLS 版本。");
        await response.Content.LoadIntoBufferAsync(32768, timeout.Token);
        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
        using (json)
        {
            if (json.RootElement.TryGetProperty("error", out _)) throw new InvalidOperationException("随身 WiFi 执行失败，请检查 UFI-TOOLS 的 AT 功能。");
            return json.RootElement.Clone();
        }
    }
    public static string Signature(string method, string path, string timestamp)
    {
        var digest = HMACMD5.HashData(Encoding.UTF8.GetBytes("minikano_kOyXz0Ciz4V7wR0IeKmJFYFQ20jd"),
            Encoding.UTF8.GetBytes("minikano" + method + path + timestamp));
        return Convert.ToHexStringLower(SHA256.HashData(SHA256.HashData(digest[..8]).Concat(SHA256.HashData(digest[8..])).ToArray()));
    }
}

public sealed class UfiPhoneController : IDisposable
{
    private readonly UfiGatewaySettings settings;
    private readonly AlarmCallTracker calls;
    private readonly Action<RuntimeEvent> _log;
    private readonly Func<bool> _isSupported;
    private readonly SemaphoreSlim _operation = new(1, 1);
    private readonly HttpClient _http;
    public UfiPhoneController(UfiGatewaySettings settings, AlarmCallTracker calls, Action<RuntimeEvent> log,
        HttpClient? http = null, Func<bool>? isSupported = null)
    {
        this.settings = settings; this.calls = calls; _log = log;
        _isSupported = isSupported ?? (() => true);
        _http = http ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false })
            { Timeout = Timeout.InfiniteTimeSpan };
    }
    public string StatusMessage => _status;
    private long? _lastDial;
    private string _status = "尚未检测随身 WiFi";
    public static bool IsOpenWrt => OperatingSystem.IsLinux() && File.Exists("/etc/openwrt_release");
    public bool GatewayEnabled => settings.Value.Enabled;
    public bool OperationBusy => _operation.CurrentCount == 0;
    public object Status() => new { router_supported = IsOpenWrt, enabled = settings.Value.Enabled, message = _status,
        cooldown_seconds = RemainingCooldown(settings.Value) };
    public async Task<object> ProbeAsync(CancellationToken token)
    {
        RequireSupported();
        if (!await _operation.WaitAsync(0, token)) throw new InvalidOperationException("电话网关正在使用，请稍后检测。");
        try
        {
            var options = settings.Value;
            var model = await new UfiPhoneClient(_http, options).ProbeAsync(token);
            _status = $"已连接 {model}，SIM 就绪，当前无通话";
            return new { ok = true, message = _status };
        }
        catch { _status = "随身 WiFi 不可用，请重新检测连接"; throw; }
        finally { _operation.Release(); }
    }
    private void RequireSupported()
    {
        if (!_isSupported()) throw new InvalidOperationException("电话提醒仅支持连接了随身 WiFi 的 OpenWrt 路由器。");
    }
    private int RemainingCooldown(UfiGatewayOptions options) => _lastDial is long last
        ? Math.Max(0, options.MinimumIntervalSeconds - (int)Stopwatch.GetElapsedTime(last).TotalSeconds) : 0;

    public async Task<object> CallAsync(SourceConfig source, CancellationToken token, AlarmCallTracker.PendingCall? pending = null)
    {
        RequireSupported();
        var reminder = source.PhoneReminder ?? new();
        ConfigValidator.ValidatePhoneReminder(reminder);
        if (!reminder.Enabled) throw new InvalidOperationException("请先启用当前设备的电话提醒并填写号码。");
        if (!await _operation.WaitAsync(0, token)) throw new InvalidOperationException("随身 WiFi 正在执行另一次检测或通话。");
        try
        {
            var snapshot = settings.Snapshot();
            var options = snapshot.Options;
            using var changed = CancellationTokenSource.CreateLinkedTokenSource(token, snapshot.Changed);
            token = changed.Token;
            if (!options.Enabled) throw new InvalidOperationException("请先在系统设置启用随身 WiFi 电话网关。");
            if (RemainingCooldown(options) > 0) throw new InvalidOperationException($"电话提醒处于间隔保护中，请 {RemainingCooldown(options)} 秒后重试。");
            var client = new UfiPhoneClient(_http, options);
            await client.ProbeAsync(token); // Connection, authentication, SIM and idle state before every dial.
            token.ThrowIfCancellationRequested();
            if (pending is not null && !calls.Claim(pending)) throw new OperationCanceledException(token);
            _lastDial = Stopwatch.GetTimestamp(); // Reserve even if ATD times out: never repeat an uncertain dial.
            var observed = "已发起呼叫，尚未确认响铃";
            var accepted = false;
            using var ringTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            ringTimeout.CancelAfter(TimeSpan.FromSeconds(options.RingSeconds));
            try
            {
                await client.AtAsync("ATD" + reminder.Number + ";", ringTimeout.Token);
                accepted = true;
                RecordPhoneEvent("info", $"[{source.Name}] 已发起电话提醒，接听号码尾号 {reminder.Number[^4..]}。", source.Id);
                var started = Stopwatch.GetTimestamp();
                while (Stopwatch.GetElapsedTime(started).TotalSeconds < options.RingSeconds)
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), ringTimeout.Token);
                    var state = await client.AtAsync("AT+CLCC", ringTimeout.Token);
                    if (!state.Contains("+CLCC:", StringComparison.OrdinalIgnoreCase)) break;
                    if (Regex.IsMatch(state, @"\+CLCC:\s*\d+,0,3,")) observed = "设备报告对方响铃";
                    if (Regex.IsMatch(state, @"\+CLCC:\s*\d+,0,0,")) { observed = "设备报告已接通（未播放语音）"; break; }
                }
            }
            catch (OperationCanceledException) when (ringTimeout.IsCancellationRequested && !token.IsCancellationRequested)
            {
                if (!accepted) throw new InvalidOperationException("拨号请求超时，结果未确认；已尝试挂断，不会自动重拨。");
                observed += "，已达到呼叫等待上限";
            }
            finally
            {
                // Recovery, stop and request cancellation must still hang up our outgoing call.
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                try { await client.AtAsync("ATH", cleanup.Token); }
                catch { RecordPhoneEvent("error", "挂断指令未确认，请检查随身 WiFi 当前通话状态。", source.Id); throw; }
            }
            _status = observed + "，已发送挂断指令";
            RecordPhoneEvent("info", $"[{source.Name}] {_status}。", source.Id);
            return new { ok = true, message = _status };
        }
        finally { _operation.Release(); }
    }

    public async Task RunAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
                if (!_isSupported() || !settings.Value.Enabled || RemainingCooldown(settings.Value) > 0) continue;
                var pending = calls.Due();
                if (pending is null) continue;
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, pending.Cancellation.Token);
                try { await CallAsync(pending.Source, linked.Token, pending); }
                catch (OperationCanceledException) when (linked.IsCancellationRequested) { }
                catch (Exception ex)
                {
                    _status = ex is InvalidOperationException ? ex.Message : "随身 WiFi 连接失败，请检查当前设备与随身 WiFi 的连接。";
                    RecordPhoneEvent("warning", "电话提醒暂不可用：" + _status, pending.Source.Id);
                    await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
    private void RecordPhoneEvent(string level, string message, string sourceId)
        => _log(new RuntimeEvent(level, message, sourceId));
    public void Dispose() => _http.Dispose();
}
