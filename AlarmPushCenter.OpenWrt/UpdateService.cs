using System.Diagnostics;
using System.Text.Json;

namespace AlarmPushCenter;

public sealed record UpdatePreferences
{
    public bool CheckAutomatically { get; init; } = true;
    public bool InstallAutomatically { get; init; }
    public int MaintenanceStartHour { get; init; } = 3;
    public int MaintenanceEndHour { get; init; } = 5;
}

public sealed class UpdateService(RuntimeHost runtime, UfiPhoneService phone) : BackgroundService
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly AppUpdateClient _client = new();
    private readonly string _config = Environment.GetEnvironmentVariable("ALARM_CONFIG") ?? "/etc/alarm-push-center/config.json";
    private string SettingsPath => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(_config))!, "update-settings.json");
    private UpdateStatus _status = new(AppUpdateClient.CurrentVersion, false, null, DateTime.MinValue, "尚未检查更新。");
    private UpdatePreferences _preferences = new();
    private bool _applying;
    private string? _jobId;
    private bool PreviousAttemptFailed => ReadOperation() is JsonElement value && value.TryGetProperty("phase", out var phase) &&
        phase.GetString() is "failed" or "rolled_back" && value.TryGetProperty("version", out var version) && version.GetString() == _status.Release?.Version;
    public object PublicView()
    {
        var operation = ReadOperation();
        if (_applying && operation is JsonElement value && value.TryGetProperty("job_id", out var id) && id.GetString() == _jobId &&
            value.TryGetProperty("phase", out var phase) && phase.GetString() is "completed" or "failed" or "rolled_back") _applying = false;
        return new { status = _status, preferences = _preferences, applying = _applying, supported = UfiPhoneController.IsOpenWrt, operation };
    }
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, PropertyNameCaseInsensitive = true };

    public void SavePreferences(UpdatePreferences value)
    {
        if (value is null) throw new InvalidOperationException("更新偏好不能为空。");
        if (value.MaintenanceStartHour is < 0 or > 23 || value.MaintenanceEndHour is < 0 or > 23 || value.MaintenanceStartHour == value.MaintenanceEndHour)
            throw new InvalidOperationException("维护时间需为 0～23 点且起止时间不同。");
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        var temp = SettingsPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(value, Json));
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.Move(temp, SettingsPath, true); _preferences = value;
    }
    public async Task<object> CheckAsync(CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try
        {
            _status = await _client.CheckAsync("openwrt", AppUpdateClient.CurrentVersion, token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !token.IsCancellationRequested)
        {
            _status = new(AppUpdateClient.CurrentVersion, false, null, DateTime.UtcNow, "检查失败，当前版本继续运行。请检查路由器外网与 GitHub 连接。");
        }
        finally { _gate.Release(); }
        return PublicView();
    }
    public async Task<object> ApplyAsync(string version, CancellationToken token)
    {
        if (!UfiPhoneController.IsOpenWrt) throw new InvalidOperationException("安装更新仅支持路由器上的正式插件。");
        await _gate.WaitAsync(token);
        string? staging = null;
        var launched = false;
        try
        {
            if (_applying) throw new InvalidOperationException("更新已在进行中。");
            var fresh = await _client.CheckAsync("openwrt", AppUpdateClient.CurrentVersion, token);
            if (!fresh.Available || fresh.Release?.Version != version) throw new InvalidOperationException("版本信息已变化，请重新检查更新。");
            if (!runtime.UpdateIdle || phone.OperationBusy) throw new InvalidOperationException("当前有激活报警、待发送消息或电话操作，请结束后再更新。");
            var opkg = await CommandAsync("/bin/opkg", ["print-architecture"], token);
            if (!opkg.Contains("aarch64_cortex-a53", StringComparison.Ordinal)) throw new InvalidOperationException("当前设备架构不匹配，未安装。");
            var id = Guid.NewGuid().ToString("N");
            var root = staging = "/tmp/alarm-push-center-updates/" + id;
            Directory.CreateDirectory(root);
            if (OperatingSystem.IsLinux()) File.SetUnixFileMode(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var package = await _client.DownloadAsync(fresh.Release, "openwrt", root, token);
            // Recheck after download: a new alarm may have arrived while the network was busy.
            if (!runtime.UpdateIdle || phone.OperationBusy) throw new InvalidOperationException("下载已完成，但设备正在报警或通话；待空闲后再更新。");
            var jobPath = Path.Combine(root, "job.json");
            File.WriteAllText(jobPath, JsonSerializer.Serialize(new { fresh.Release.Version, fresh.Release.Sha256, resume_running = runtime.CollectionRunning }, Json));
            if (OperatingSystem.IsLinux()) File.SetUnixFileMode(jobPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            var job = JsonSerializer.Serialize(new { name = "alarm-push-center-upgrade", instances = new { job = new {
                command = new[] { "/bin/sh", "/usr/libexec/alarm-push-center-upgrade", root }, stdout = true, stderr = true } } });
            await CommandAsync("/bin/ubus", ["call", "service", "set", job], token);
            launched = true; _jobId = id; _applying = true;
            return new { message = "校验完成，正在备份并更新。服务会短暂重启，采集状态将自动恢复。", version = fresh.Release.Version };
        }
        finally
        {
            if (!launched && staging is not null && Directory.Exists(staging)) Directory.Delete(staging, true);
            _gate.Release();
        }
    }
    private static object? ReadOperation()
    {
        const string path = "/etc/alarm-push-center-updates/state.json";
        try { return File.Exists(path) ? JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(path)) : null; }
        catch { return null; }
    }
    internal static bool IsMaintenanceHour(int hour, int start, int end) => start < end ? hour >= start && hour < end : hour >= start || hour < end;
    protected override async Task ExecuteAsync(CancellationToken token)
    {
        try
        {
            if (File.Exists(SettingsPath)) _preferences = JsonSerializer.Deserialize<UpdatePreferences>(File.ReadAllText(SettingsPath), Json) ?? new();
        }
        catch { _preferences = new(); }
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(20), token);
            while (!token.IsCancellationRequested)
            {
                if (_preferences.CheckAutomatically && DateTime.UtcNow - _status.CheckedAt >= (_status.Release is null ? TimeSpan.FromMinutes(10) : TimeSpan.FromHours(6))) await CheckAsync(token);
                if (_preferences.InstallAutomatically && _status.Available && !_applying && !PreviousAttemptFailed && runtime.UpdateIdle && !phone.OperationBusy &&
                    IsMaintenanceHour(DateTime.Now.Hour, _preferences.MaintenanceStartHour, _preferences.MaintenanceEndHour))
                {
                    try { await ApplyAsync(_status.Release!.Version, token); }
                    catch (Exception ex) when (ex is not OperationCanceledException) { /* Retry next interval; current service keeps running. */ }
                }
                await Task.Delay(TimeSpan.FromMinutes(10), token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }
    private static async Task<string> CommandAsync(string program, string[] arguments, CancellationToken token)
    {
        using var process = new Process { StartInfo = new(program) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false } };
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        process.Start();
        var output = process.StandardOutput.ReadToEndAsync(token); var error = process.StandardError.ReadToEndAsync(token);
        await process.WaitForExitAsync(token);
        if (process.ExitCode != 0) throw new InvalidOperationException("更新任务启动失败，原服务保持运行。");
        await error; return await output;
    }
    public override void Dispose() { _client.Dispose(); _gate.Dispose(); base.Dispose(); }
}

public sealed record ApplyUpdateRequest(string Version, string CurrentPassword);
public sealed record UpdateSettingsRequest(UpdatePreferences Preferences, string CurrentPassword);
