namespace AlarmPushCenter;

public sealed partial class MainForm
{
    private readonly AlarmCallTracker _phoneCalls = new();
    private readonly CancellationTokenSource _phoneLifetime = new();
    private UfiGatewaySettings _phoneGateway = null!;
    private UfiPhoneController _phoneController = null!;
    private CancellationTokenSource? _phoneLoopStop, _phoneUiStop;
    private Task _phoneLoop = Task.CompletedTask, _phoneUiTask = Task.CompletedTask;
    private bool _phoneUiBusy;
    private readonly CheckBox _phoneEnabled = Toggle("启用当前设备电话提醒");
    private readonly TextBox _phoneNumber = Input();
    private readonly NumericUpDown _phoneDelay = Numeric(1, 86400, 1, 0);
    private readonly Label _phoneState = new() { AutoSize = false, Height = 54, ForeColor = VisualTheme.Muted };
    private readonly Button _phoneProbe = ActionButton("检查随身 WiFi", false);
    private readonly Button _phoneTest = ActionButton("测试拨号", true);
    private readonly Button _phoneCancel = ActionButton("取消测试", false);
    private readonly TabControl _sourceTabs = new() { Height = 930, Font = new Font("Microsoft YaHei UI", 10, FontStyle.Bold), Padding = new Point(22, 7) };

    private void InitializePhone()
    {
        _phoneGateway = new UfiGatewaySettings(_store.Path);
        _phoneController = new UfiPhoneController(_phoneGateway, _phoneCalls, Log);
        FormClosed += (_, _) =>
        {
            _phoneLifetime.Cancel(); _phoneLoopStop?.Cancel();
            _phoneController.Dispose(); _phoneCalls.Dispose();
        };
    }

    private Control BuildSourceWorkspace()
    {
        var connection = new TabPage("采集连接") { BackColor = AppBackground, AutoScroll = true };
        var phone = new TabPage("电话提醒") { BackColor = AppBackground, AutoScroll = true };
        var connectionContent = SourceTabContent(connection);
        var phoneContent = SourceTabContent(phone);
        foreach (var protocol in ConfigDefaults.Protocols) _protocol.Items.Add(new Option(protocol, ProtocolLabel(protocol)));
        _protocol.SelectedIndexChanged += (_, _) =>
        {
            LoadParameterGrid(_sourceParams, ConfigDefaults.MergeSourceParams(SelectedKey(_protocol), null));
            RefreshSourceParameterEditor();
        };
        AddBlock(connectionContent, FormCard("采集源基础配置", "设置设备连接方式与轮询节奏。", [
            ("设备名称", (Control)_sourceName), ("采集协议", _protocol), ("采集处理间隔（秒）", _pollSeconds), ("运行状态", _sourceEnabled)
        ]));
        AddBlock(connectionContent, _sourceParameterHost);
        AddBlock(connectionContent, HintCard("协议说明", "OPC UA 数组模式读取数量节点 + DInt 数组；ProDiag 模式订阅事件。S7 绝对地址要求 DB 关闭优化访问。"));

        AddBlock(phoneContent, FormCard("当前设备电话提醒", "报警持续达到设定时间后，由本机连接的随身 WiFi 拨打电话。", [
            ("提醒开关", (Control)_phoneEnabled), ("接听号码", _phoneNumber), ("报警持续时间（秒）", _phoneDelay)
        ]));
        _phoneNumber.PlaceholderText = "例如 13800138000";
        var service = Surface("随身 WiFi 电话服务", "全部设备共用一个电话网关；每台设备独立设置时间和接听号码。", 235);
        _phoneState.Location = new Point(26, 78); _phoneState.Width = 600;
        _phoneState.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        service.Controls.Add(_phoneState);
        var settings = ActionButton("网关设置", false);
        var actions = new FlowLayoutPanel { Location = new Point(22, 151), Height = 60, Width = 700,
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right, WrapContents = true };
        actions.Controls.AddRange([settings, _phoneProbe, _phoneTest, _phoneCancel]);
        _phoneCancel.Enabled = false;
        settings.Click += (_, _) => ShowPhoneGatewaySettings();
        _phoneProbe.Click += async (_, _) => await StartPhoneUiOperationAsync(false);
        _phoneTest.Click += async (_, _) => await StartPhoneUiOperationAsync(true);
        _phoneCancel.Click += (_, _) => _phoneUiStop?.Cancel();
        service.Controls.Add(actions); AddBlock(phoneContent, service);
        AddBlock(phoneContent, HintCard("使用条件", "电脑须能访问安装了 UFI-TOOLS 的随身 WiFi，且 SIM 支持语音。检查连接不会拨号；测试拨号需要确认。保存后重新启动采集服务，设备提醒设置才会生效。"));
        AddBlock(phoneContent, HintCard("提醒规则", "同一轮持续报警只拨打一次；报警恢复、采集断线或停止服务会取消待拨号。自动提醒仅在客户端运行且采集已启动时生效；电话接通后不播放语音。"));
        _sourceTabs.TabPages.AddRange([connection, phone]);
        return _sourceTabs;
    }

    private static TableLayoutPanel SourceTabContent(TabPage page)
    {
        var content = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1,
            Padding = new Padding(12), BackColor = AppBackground };
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); page.Controls.Add(content);
        return content;
    }

    private void LoadPhoneToUi(SourceConfig source)
    {
        var phone = source.PhoneReminder ?? new();
        _phoneEnabled.Checked = phone.Enabled; _phoneNumber.Text = phone.Number;
        _phoneDelay.Value = Clamp(phone.DelaySeconds, _phoneDelay.Minimum, _phoneDelay.Maximum);
        RefreshPhoneState();
    }

    private PhoneReminder ReadPhoneFromUi()
    {
        var phone = new PhoneReminder { Enabled = _phoneEnabled.Checked, Number = _phoneNumber.Text.Trim(), DelaySeconds = (int)_phoneDelay.Value };
        ConfigValidator.ValidatePhoneReminder(phone); return phone;
    }

    private void RefreshPhoneState()
    {
        var enabled = _phoneGateway.Value.Enabled;
        _phoneState.Text = enabled ? _phoneController.StatusMessage : "电话网关未启用，请先打开“网关设置”。";
        _phoneState.ForeColor = enabled ? Color.FromArgb(14, 116, 144) : VisualTheme.Muted;
    }

    private void ShowPhoneGatewaySettings()
    {
        if (_phoneUiBusy) { MessageBox.Show(this, "请先完成或取消当前电话检测 / 测试。"); return; }
        var value = _phoneGateway.Value;
        using var dialog = new Form { Text = "随身 WiFi · 本机电话网关", StartPosition = FormStartPosition.CenterParent,
            ClientSize = new Size(780, 570), MinimumSize = new Size(700, 600), BackColor = AppBackground, Font = Font,
            ShowInTaskbar = false, MaximizeBox = false, MinimizeBox = false };
        var enabled = Toggle("启用本机电话网关"); enabled.Checked = value.Enabled;
        var address = Input(); address.Text = value.Address;
        var passphrase = Input(true); passphrase.PlaceholderText = value.Token.Length > 0 ? "已保存口令；留空保持不变" : "填写 UFI-TOOLS 登录口令";
        var slot = Numeric(0, 1, 1, 0); slot.Value = value.Slot;
        var ring = Numeric(5, 60, 1, 0); ring.Value = value.RingSeconds;
        var interval = Numeric(60, 3600, 1, 0); interval.Value = value.MinimumIntervalSeconds;
        var card = FormCard("随身 WiFi 连接", "电脑通过 USB、网线或 Wi-Fi 访问该地址。口令是 UFI-TOOLS 登录口令。", [
            ("电话网关", (Control)enabled), ("随身 WiFi 地址", address), ("登录口令（留空保留）", passphrase),
            ("AT 槽位", slot), ("最长呼叫等待（秒）", ring), ("两次呼叫最短间隔（秒）", interval)
        ]);
        card.Dock = DockStyle.Top;
        var body = new Panel { Dock = DockStyle.Fill, Padding = new Padding(18), AutoScroll = true }; body.Controls.Add(card);
        var footer = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 72, Padding = new Padding(20, 12, 0, 0) };
        var save = ActionButton("保存网关设置", true); var cancel = ActionButton("取消", false);
        footer.Controls.AddRange([save, cancel]); dialog.Controls.Add(body); dialog.Controls.Add(footer);
        cancel.Click += (_, _) => dialog.Close();
        save.Click += (_, _) =>
        {
            try
            {
                _phoneGateway.Save(value with { Enabled = enabled.Checked, Address = address.Text, Token = passphrase.Text,
                    Slot = (int)slot.Value, RingSeconds = (int)ring.Value, MinimumIntervalSeconds = (int)interval.Value });
                RefreshPhoneState(); Log(new("info", "本机随身 WiFi 电话网关设置已保存。")); dialog.Close();
            }
            catch (Exception ex) { MessageBox.Show(dialog, ex.Message, "网关配置错误", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        };
        dialog.ShowDialog(this);
    }

    private async Task StartPhoneUiOperationAsync(bool dial)
    {
        if (_phoneUiBusy || _closing) return;
        SourceConfig source;
        try
        {
            source = new SourceConfig { Id = _config.Sources[_sourceIndex].Id, Name = _sourceName.Text.Trim(), PhoneReminder = dial ? ReadPhoneFromUi() : new() };
            if (!_phoneGateway.Value.Enabled) throw new InvalidOperationException("请先在网关设置中启用本机电话网关。");
            if (dial && !source.PhoneReminder.Enabled) throw new InvalidOperationException("请先启用当前设备电话提醒并填写接听号码。");
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "电话提醒", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
        if (dial && MessageBox.Show(this, $"将通过随身 WiFi 拨打 {source.PhoneReminder.Number}。\n设备：{source.Name}\n可能产生运营商通话费用，最长等待 {_phoneGateway.Value.RingSeconds} 秒。\n\n确认拨打？", "确认测试拨号", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        _phoneUiBusy = true; _phoneProbe.Enabled = _phoneTest.Enabled = false; _phoneCancel.Enabled = true;
        _phoneUiStop = CancellationTokenSource.CreateLinkedTokenSource(_phoneLifetime.Token);
        _phoneUiTask = ExecutePhoneUiOperationAsync(source, dial, _phoneUiStop.Token);
        await _phoneUiTask;
    }

    private async Task ExecutePhoneUiOperationAsync(SourceConfig source, bool dial, CancellationToken token)
    {
        try
        {
            _phoneState.Text = dial ? "正在发起测试呼叫…" : "正在检查连接、口令和 SIM…";
            if (dial) await _phoneController.CallAsync(source, token); else await _phoneController.ProbeAsync(token);
            RefreshPhoneState();
            Log(new("info", dial ? _phoneController.StatusMessage : "随身 WiFi 检查通过，未拨打电话。", source.Id));
        }
        catch (OperationCanceledException) { _phoneState.Text = "已取消；如已发起呼叫，已尝试挂断，请查看运行日志。"; }
        catch (Exception ex)
        {
            _phoneState.Text = ex is InvalidOperationException ? ex.Message : "随身 WiFi 连接失败，请检查地址、网络和登录口令。";
            Log(new("warning", _phoneState.Text, source.Id));
        }
        finally
        {
            _phoneUiBusy = false; _phoneProbe.Enabled = _phoneTest.Enabled = true; _phoneCancel.Enabled = false;
            _phoneUiStop?.Dispose(); _phoneUiStop = null;
        }
    }

    private void StartPhoneMonitor()
    {
        _phoneLoopStop = CancellationTokenSource.CreateLinkedTokenSource(_phoneLifetime.Token);
        var token = _phoneLoopStop.Token;
        _phoneLoop = Task.Run(() => _phoneController.RunAsync(token));
    }

    private async Task StopPhoneMonitorAsync()
    {
        _phoneCalls.End(); _phoneLoopStop?.Cancel(); _phoneUiStop?.Cancel();
        await _phoneLoop;
        await _phoneUiTask;
        _phoneLoopStop?.Dispose(); _phoneLoopStop = null;
    }
}
