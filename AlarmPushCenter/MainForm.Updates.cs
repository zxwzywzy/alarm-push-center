using System.Diagnostics;
using System.Text.Json;

namespace AlarmPushCenter;

public sealed partial class MainForm
{
    private readonly Button _updateButton = ActionButton("版本更新", false);
    private readonly AppUpdateClient _updateClient = new();
    private readonly System.Windows.Forms.Timer _updateTimer = new() { Interval = 6 * 60 * 60 * 1000 };
    private UpdateStatus? _desktopUpdate;
    private bool _checkingUpdate, _checkUpdatesAutomatically = true;
    private string UpdatePreferencesPath => Path.Combine(Path.GetDirectoryName(_store.Path)!, "desktop-updates.json");
    private void InitializeUpdates()
    {
        try { if (File.Exists(UpdatePreferencesPath)) _checkUpdatesAutomatically = JsonDocument.Parse(File.ReadAllText(UpdatePreferencesPath)).RootElement.GetProperty("check_automatically").GetBoolean(); }
        catch { }
        _updateButton.Click += async (_, _) => await ShowUpdatesAsync();
        _updateTimer.Tick += async (_, _) => { if (_checkUpdatesAutomatically) await CheckDesktopUpdateAsync(); };
        _updateTimer.Start();
        Shown += async (_, _) =>
        {
            if (Environment.GetCommandLineArgs().Any(x => x.StartsWith("--render", StringComparison.Ordinal) || x == "--self-test")) return;
            if (UpdateResumeState.Consume(_store.Path) == true) await ToggleMonitorAsync();
            if (_checkUpdatesAutomatically) await CheckDesktopUpdateAsync();
        };
        FormClosed += (_, _) => { _updateTimer.Dispose(); _updateClient.Dispose(); };
    }
    private async Task CheckDesktopUpdateAsync()
    {
        if (_checkingUpdate) return;
        _checkingUpdate = true;
        try
        {
            _desktopUpdate = await _updateClient.CheckAsync("windows", AppUpdateClient.CurrentVersion);
            if (!IsDisposed) _updateButton.Text = _desktopUpdate.Available ? "有新版可用" : "版本更新";
        }
        catch { _desktopUpdate = new(AppUpdateClient.CurrentVersion, false, null, DateTime.UtcNow, "检查失败，请稍后重试；不影响当前采集。"); }
        finally { _checkingUpdate = false; }
    }
    private async Task ShowUpdatesAsync()
    {
        await CheckDesktopUpdateAsync();
        using var dialog = new Form { Text = "版本更新", ClientSize = new Size(600, 310), StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog, MinimizeBox = false, MaximizeBox = false, BackColor = AppBackground, Font = Font };
        var status = new Label { Text = $"当前版本 {AppUpdateClient.CurrentVersion}\n\n{_desktopUpdate?.Message ?? "正在检查更新"}", Location = new(24, 22), Size = new(550, 80), ForeColor = VisualTheme.Ink };
        var auto = new CheckBox { Text = "启动时及每 6 小时自动检查更新", Checked = _checkUpdatesAutomatically, AutoSize = true, Location = new(24, 116) };
        auto.CheckedChanged += (_, _) =>
        {
            _checkUpdatesAutomatically = auto.Checked;
            try { File.WriteAllText(UpdatePreferencesPath, JsonSerializer.Serialize(new { check_automatically = auto.Checked })); }
            catch { status.Text = "更新偏好未能保存，请检查配置目录权限。"; }
        };
        var detail = new Label { Text = "更新会先保存并备份配置，关闭客户端后使用 Inno Setup 更新，再自动打开并恢复原采集状态。正在报警、发送消息或通话时会暂缓。", Location = new(24, 157), Size = new(550, 66), ForeColor = VisualTheme.Muted };
        var install = ActionButton("下载并更新", true); install.Location = new(24, 242);
        install.Enabled = _desktopUpdate?.Available == true;
        var notes = ActionButton("查看更新说明", false); notes.Location = new(205, 242);
        notes.Click += (_, _) => Process.Start(new ProcessStartInfo(_desktopUpdate?.Release?.NotesUrl ?? $"https://github.com/{AppUpdateClient.Repository}/releases") { UseShellExecute = true });
        install.Click += async (_, _) =>
        {
            if (_desktopUpdate?.Release is not AppRelease release) return;
            if (!File.Exists(Path.Combine(AppContext.BaseDirectory, "unins000.exe"))) { status.Text = "源码/开发预览不能直接覆盖安装。请从发布页安装正式客户端。"; return; }
            if (!DesktopUpdateIdle()) { status.Text = "当前有激活报警、待发送消息或电话操作，请空闲后更新。"; return; }
            if (MessageBox.Show(dialog, $"更新到 {release.Version}？客户端将短暂重启，配置及采集状态会保留。", "确认更新", MessageBoxButtons.OKCancel, MessageBoxIcon.Information) != DialogResult.OK) return;
            install.Enabled = false; auto.Enabled = false; status.Text = "正在下载并核对 SHA256，请保持网络连接…";
            try
            {
                var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AlarmPushCenterUpdates", release.Version);
                var installer = await _updateClient.DownloadAsync(release, "windows", folder);
                if (!DesktopUpdateIdle()) throw new InvalidOperationException("下载已完成，当前出现报警或通话，请空闲后再更新。");
                if (!SaveFromUi()) throw new InvalidOperationException("配置有错误，更新尚未开始。");
                var script = DesktopUpdateScript(installer, _store.Path, AppContext.BaseDirectory, Environment.ProcessId);
                var scriptPath = Path.Combine(folder, "install-and-restart.ps1"); File.WriteAllText(scriptPath, script);
                UpdateResumeState.Write(_store.Path, _monitor is not null);
                var process = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
                foreach (var argument in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", scriptPath }) process.ArgumentList.Add(argument);
                Process.Start(process); dialog.Close(); Close();
            }
            catch (Exception ex) { status.Text = ex.Message; install.Enabled = true; auto.Enabled = true; }
        };
        dialog.Controls.AddRange([status, auto, detail, install, notes]); dialog.ShowDialog(this);
    }
    private bool DesktopUpdateIdle() => _monitor?.Metrics() is not { ActiveAlarms: > 0 } && _monitor?.Metrics() is not { QueueDepth: > 0 } && !_phoneController.OperationBusy;
    internal static string DesktopUpdateScript(string installer, string config, string applicationDirectory, int processId)
    {
        static string Q(string value) => "'" + value.Replace("'", "''") + "'";
        var exe = Path.Combine(applicationDirectory, "AlarmPushCenter.exe");
        var backup = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AlarmPushCenterUpdates", "Backups", DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..8]);
        return $$"""
        $ErrorActionPreference='Stop'
        Wait-Process -Id {{processId}} -ErrorAction SilentlyContinue
        $backup={{Q(backup)}}
        New-Item -ItemType Directory -Force -Path $backup | Out-Null
        try {
            $config={{Q(config)}}
            $folder=Split-Path -Parent $config
            $baseName=Split-Path -Leaf $config
            Get-ChildItem -LiteralPath $folder -File | Where-Object { $_.Name.StartsWith($baseName) -or $_.Name -in @('alarm-history.jsonl','phone-gateway.json','desktop-updates.json','.update-resume-once') } | Copy-Item -Destination $backup
            $result=Start-Process -FilePath {{Q(installer)}} -ArgumentList {{Q("/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP- /DIR=\"" + applicationDirectory.TrimEnd(Path.DirectorySeparatorChar) + "\"")}} -Wait -PassThru -WindowStyle Hidden
            if($result.ExitCode -notin @(0,3010)){ throw "Installer exit code $($result.ExitCode)" }
        } catch { $_ | Out-File -LiteralPath (Join-Path $backup 'update-error.log') }
        if(Test-Path -LiteralPath {{Q(exe)}}) { Start-Process -FilePath {{Q(exe)}} -ArgumentList {{Q("--config \"" + config + "\"")}} }
        """;
    }
}
