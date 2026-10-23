using System.Text.Json;

namespace AlarmPushCenter;

public sealed partial class MainForm : Form
{
    private static readonly Color AppBackground = Color.FromArgb(245, 246, 243);
    private static readonly Color PrimaryColor = VisualTheme.Accent;
    private readonly ConfigStore _store;
    private readonly AlarmHistoryStore _historyStore;
    private AppConfig _config;
    private AlarmMonitor? _monitor;
    private readonly Panel _pageHost = new() { Dock = DockStyle.Fill, BackColor = AppBackground };
    private readonly Label _pageTitle = new() { AutoSize = true, Font = new Font("Microsoft YaHei UI", 20, FontStyle.Bold), ForeColor = Color.FromArgb(15, 23, 42) };
    private readonly Label _pageSubtitle = new() { AutoSize = true, Font = new Font("Microsoft YaHei UI", 9), ForeColor = Color.FromArgb(100, 116, 139) };
    private readonly List<Panel> _pages = [];
    private readonly List<Button> _navigationButtons = [];
    private readonly TabControl _messageTabs = new() { Dock = DockStyle.Fill, DrawMode = TabDrawMode.OwnerDrawFixed, ItemSize = new Size(180, 46), SizeMode = TabSizeMode.Fixed, Font = new Font("Microsoft YaHei UI", 10, FontStyle.Bold) };
    private readonly StatusPill _servicePill = new() { Font = new Font("Microsoft YaHei UI", 9, FontStyle.Bold) };

    private readonly Label _statusValue = MetricValue("未运行", PrimaryColor);
    private readonly Label _sourceCount = MetricValue("0", Color.FromArgb(14, 116, 144));
    private readonly Label _ruleCount = MetricValue("0", Color.FromArgb(234, 88, 12));
    private readonly Label _targetCount = MetricValue("0", Color.FromArgb(22, 163, 74));
    private readonly Button _monitorButton = ActionButton("启动服务", true);
    private readonly TextBox _log = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, BorderStyle = BorderStyle.None, BackColor = Color.FromArgb(15, 23, 42), ForeColor = Color.FromArgb(209, 250, 229), Font = new Font("Consolas", 10), Padding = new Padding(14) };
    private readonly DataGridView _connectionGrid = SourceHealthGrid();
    private readonly Dictionary<string, SourceHealth> _sourceHealth = new(StringComparer.OrdinalIgnoreCase);
    private readonly System.Windows.Forms.Timer _healthTimer = new() { Interval = 1000 };

    private readonly TextBox _sourceName = Input();
    private readonly ComboBox _protocol = Combo();
    private readonly NumericUpDown _pollSeconds = Numeric(0.2M, 3600, 0.5M, 1);
    private readonly CheckBox _sourceEnabled = Toggle("启用采集");
    private readonly DataGridView _sourceParams = ParamsGrid();

    private readonly TextBox _ruleName = Input();
    private readonly ComboBox _severity = Combo("info", "warning", "critical");
    private readonly TextBox _matchCode = Input();
    private readonly TextBox _matchKey = Input();
    private readonly TextBox _matchValue = Input();
    private readonly NumericUpDown _cooldown = Numeric(0, 86400, 1, 1);
    private readonly CheckBox _ruleEnabled = Toggle("启用规则");
    private readonly TextBox _messageTemplate = Multiline(118);
    private readonly TextBox _recoveryTemplate = Multiline(92);
    private readonly TextBox _note = Multiline(66);
    private readonly DataGridView _alarmTexts = AlarmTextGrid();

    private readonly TextBox _targetName = Input();
    private readonly ComboBox _channel = Combo();
    private readonly CheckBox _targetEnabled = Toggle("启用发送");
    private readonly TextBox _token = Input(true);
    private readonly TextBox _webhookUrl = Input();
    private readonly TextBox _recipients = Multiline(62);
    private readonly DataGridView _targetParams = ParamsGrid();

    public MainForm(string configPath)
    {
        _store = new ConfigStore(configPath);
        _historyStore = new AlarmHistoryStore(_store.Path);
        _config = _store.Load();
        var configFolder = Directory.GetParent(_store.Path)?.Name ?? "当前目录";
        Text = $"工业报警推送中心 · 配置目录：{configFolder}";
        Icon = LoadProductIcon();
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(1120, 720);
        Size = new Size(1440, 900);
        Font = new Font("Microsoft YaHei UI", 10);
        BackColor = AppBackground;
        InitializePhone();
        InitializeUpdates();
        BuildUi();
        LoadConfigToUi();
        _healthTimer.Tick += (_, _) => { RefreshConnectionGrid(); RefreshTelemetry(); if (!_phoneUiBusy) RefreshPhoneState(); };
        _healthTimer.Start();
        FormClosed += (_, _) => _healthTimer.Dispose();
        ShowPage(0);
        FormClosing += OnClosingAsync;
    }

    private void BuildUi()
    {
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, BackColor = AppBackground, Margin = Padding.Empty };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 78));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.Controls.Add(BuildNavigationHeader(), 0, 0);
        root.Controls.Add(BuildMainArea(), 0, 1);
        Controls.Add(root);
    }

    private Control BuildMainArea()
    {
        var main = new AppCanvasPanel { Dock = DockStyle.Fill, BackColor = AppBackground };
        var header = new HeaderPanel { Dock = DockStyle.Top, Height = 88, BackColor = Color.Transparent, Padding = new Padding(24, 12, 24, 12) };
        _pageTitle.Location = new Point(46, 26);
        _pageSubtitle.Location = new Point(48, 63);
        var save = ActionButton("保存配置", false);
        save.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        save.Location = new Point(0, 34);
        save.Click += (_, _) => SaveFromUi();
        _servicePill.Location = new Point(0, 34);
        _servicePill.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        header.Resize += (_, _) =>
        {
            save.Left = header.ClientSize.Width - save.Width - 46;
            _servicePill.Left = save.Left - _servicePill.Width - 12;
        };
        header.Controls.AddRange([_pageTitle, _pageSubtitle, _servicePill, save]);
        main.Controls.Add(_pageHost);
        main.Controls.Add(header);

        _pages.Add(BuildOverviewPage());
        _pages.Add(BuildSourcePage());
        _pages.Add(BuildRulePage());
        _pages.Add(BuildMessageCenterPage());
        _pages.Add(BuildRuntimeCenterPage());
        foreach (var page in _pages) _pageHost.Controls.Add(page);
        return main;
    }

    private Panel BuildOverviewPage()
    {
        var (page, content) = ScrollablePage();
        var controls = new SurfacePanel { Height = 70, BackColor = Color.White };
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(16, 12, 12, 8), WrapContents = false };
        _monitorButton.Text = "启动服务";
        _monitorButton.Click += async (_, _) => await ToggleMonitorAsync();
        var test = ActionButton("测试通知", false); test.Click += async (_, _) => await TestEnabledTargetsAsync();
        var devices = ActionButton("管理设备", false); devices.Click += (_, _) => ShowPage(1);
        var disclaimer = ActionButton("免责声明", false); disclaimer.Click += (_, _) => ShowDisclaimer();
        actions.Controls.AddRange([_monitorButton, devices, test, disclaimer]); controls.Controls.Add(actions); AddBlock(content, controls);
        var cards = new TableLayoutPanel { Height = 104, ColumnCount = 4, Margin = new Padding(0, 0, 0, 16) };
        for (var i = 0; i < 4; i++) cards.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
        cards.Controls.Add(MetricCard("服务状态", "采集与机器人", _statusValue, PrimaryColor), 0, 0);
        cards.Controls.Add(MetricCard("采集设备", "已连接 / 已启用", _sourceCount, Color.FromArgb(14, 154, 160)), 1, 0);
        cards.Controls.Add(MetricCard("报警规则", "按设备定制消息", _ruleCount, Color.FromArgb(239, 155, 64)), 2, 0);
        cards.Controls.Add(MetricCard("通知通道", "已启用的消息目标", _targetCount, Color.FromArgb(68, 153, 113)), 3, 0);
        AddBlock(content, cards);
        AddBlock(content, BuildTelemetryPanel());
        AddBlock(content, GridCard("设备连接与心跳", "OPC UA 订阅及所需节点都收到有效数据后，才计为连接成功。", _connectionGrid, 210));
        return page;
    }
    private Panel BuildSourcePage()
    {
        var page = new Panel { Dock = DockStyle.Fill, BackColor = AppBackground };
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2,
            Padding = new Padding(30, 20, 30, 20), BackColor = AppBackground };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 96));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var selector = BuildCollectionToolbar("source"); selector.Dock = DockStyle.Fill; selector.Margin = new Padding(0, 0, 0, 16);
        var workspace = BuildSourceWorkspace(); workspace.Dock = DockStyle.Fill; workspace.Margin = Padding.Empty;
        layout.Controls.Add(selector, 0, 0); layout.Controls.Add(workspace, 0, 1); page.Controls.Add(layout);
        return page;
    }

    private Panel BuildRulePage()
    {
        var (page, content) = ScrollablePage();
        AddBlock(content, PageBanner("ALARM AUTOMATION", "把报警变成清晰的行动", "匹配报警码、维护文本库，并预览最终发送给值班人员的内容。", "02", VisualTheme.Accent3));
        AddBlock(content, BuildCollectionToolbar("rule"));
        var tabs = new TabControl { Height = 940, Dock = DockStyle.Top, Font = new Font("Microsoft YaHei UI", 10, FontStyle.Bold), Padding = new Point(22, 7) };
        var logicTab = new TabPage("匹配与消息") { BackColor = AppBackground, AutoScroll = true };
        var libraryTab = new TabPage("报警文本库") { BackColor = AppBackground, AutoScroll = true };
        var logic = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1, Padding = new Padding(14), BackColor = AppBackground };
        logic.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var libraryContent = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1, Padding = new Padding(14), BackColor = AppBackground };
        libraryContent.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        logicTab.Controls.Add(logic); libraryTab.Controls.Add(libraryContent); tabs.TabPages.AddRange([logicTab, libraryTab]);
        AddBlock(logic, FormCard("规则匹配", "只有匹配到的激活信号才会被推送。", [
            ("规则名称", (Control)_ruleName), ("所属采集源", _ruleSource), ("报警级别", _severity), ("匹配报警码", _matchCode), ("匹配字段", _matchKey), ("匹配值", _matchValue), ("冷却秒数", _cooldown), ("运行状态", _ruleEnabled)
        ]));
        AddBlock(logic, EditorCard("报警消息模板", "可使用 {source_name}、{code}、{text}、{instance_name}、{severity}、{time}、{value}。", _messageTemplate));

        var alarmLibrary = GridCard("报警文本库", "优先按完整 AlarmId 匹配；ProDiag 运行时未提供完整 ID 时，按 FB 编号与监控编号匹配。支持手工 CSV/TSV，也可从 TIA ProDiag 一键生成总表。", _alarmTexts, 780);
        var actions = new FlowLayoutPanel { AutoSize = true, Height = 38, Padding = new Padding(0), Location = new Point(24, 66), Anchor = AnchorStyles.Top | AnchorStyles.Left };
        var addRows = SmallButton("新增一组（30 行）");
        addRows.Click += (_, _) => AddAlarmRows(30);
        var deleteRows = SmallButton("删除选中行");
        deleteRows.Click += (_, _) => DeleteAlarmRows();
        var selectAllRows = SmallButton("全选 / 取消全选");
        selectAllRows.Click += (_, _) => ToggleAllAlarmRows();
        var importRows = SmallButton("导入文本库");
        importRows.Click += (_, _) => ImportAlarmTexts();
        var importTia = SmallButton("从 TIA ProDiag 导入");
        importTia.Click += async (_, _) => await ImportTiaProDiagAsync(importTia, false);
        var importRunningTia = SmallButton("读取已打开 TIA");
        importRunningTia.Click += async (_, _) => await ImportTiaProDiagAsync(importRunningTia, true);
        var preview = SmallButton("预览消息");
        preview.Click += (_, _) => PreviewRule();
        actions.Controls.AddRange([addRows, selectAllRows, deleteRows, importRows, importTia, importRunningTia, preview]);
        _alarmTexts.Location = new Point(24, 110);
        alarmLibrary.Resize += (_, _) => _alarmTexts.Size = new Size(Math.Max(200, alarmLibrary.ClientSize.Width - 48), Math.Max(80, alarmLibrary.ClientSize.Height - 130));
        alarmLibrary.Controls.Add(actions);
        AddBlock(libraryContent, alarmLibrary);
        AddBlock(logic, EditorCard("恢复消息模板", "报警状态由激活切换为恢复时写入运行日志。", _recoveryTemplate));
        AddBlock(logic, EditorCard("备注", "记录值班、设备或工艺相关的说明。", _note));
        AddBlock(content, tabs);
        return page;
    }

    private Panel BuildTargetPage(bool embedded = false)
    {
        var (page, content) = ScrollablePage();
        if (!embedded) AddBlock(content, PageBanner("MESSAGE DELIVERY", "一个地方管理所有通知渠道", "Telegram、企业微信、钉钉及 Webhook 均使用统一的配置与测试流程。", "03", VisualTheme.Accent));
        if (!embedded) AddBlock(content, BuildCollectionToolbar("target"));
        foreach (var channel in ConfigDefaults.Channels) _channel.Items.Add(new Option(channel, ChannelLabel(channel)));
        _channel.SelectedIndexChanged += (_, _) => LoadParameterGrid(_targetParams, ConfigDefaults.MergeTargetParams(SelectedKey(_channel), null));
        AddBlock(content, FormCard("通知目标", "填写推送凭据与接收目标。敏感信息仅保存在本地配置文件。", [
            ("目标名称", (Control)_targetName), ("渠道类型", _channel), ("运行状态", _targetEnabled), ("Telegram 机器人令牌", _token), ("网络回调地址", _webhookUrl), ("Telegram 会话 ID（每行一个）", _recipients)
        ]));
        AddBlock(content, GridCard("渠道扩展参数", "Webhook 模板等。机器人命令、按钮及 PLC 控制请切换到“互动机器人”页签。", _targetParams, 255));
        var testPanel = Surface("连通性测试", "发送测试消息不会触发现场设备操作。", 100);
        var test = ActionButton("测试当前目标", false);
        test.Location = new Point(24, 48);
        test.Click += async (_, _) => await TestCurrentTargetAsync();
        testPanel.Controls.Add(test);
        AddBlock(content, testPanel);
        return page;
    }

    private Panel BuildMessageCenterPage()
    {
        var page = new Panel { Dock = DockStyle.Fill, BackColor = AppBackground, Padding = new Padding(28, 18, 28, 28) };
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1, BackColor = AppBackground };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 132));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var targetToolbar = BuildCollectionToolbar("target");
        targetToolbar.Dock = DockStyle.Fill;
        targetToolbar.Margin = new Padding(0, 0, 0, 14);
        _messageTabs.Appearance = TabAppearance.Normal;
        _messageTabs.Padding = new Point(24, 8);
        _messageTabs.DrawItem += (_, e) =>
        {
            var selected = e.Index == _messageTabs.SelectedIndex;
            using var background = new SolidBrush(selected ? PrimaryColor : Color.FromArgb(226, 233, 246));
            e.Graphics.FillRectangle(background, e.Bounds);
            TextRenderer.DrawText(e.Graphics, _messageTabs.TabPages[e.Index].Text, _messageTabs.Font, e.Bounds,
                selected ? Color.White : Color.FromArgb(55, 68, 95), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        };
        var delivery = new TabPage("通知通道") { BackColor = AppBackground, Padding = new Padding(0) };
        var interaction = new TabPage("互动机器人") { BackColor = AppBackground, Padding = new Padding(0) };
        delivery.Controls.Add(BuildTargetPage(true));
        interaction.Controls.Add(BuildBotPage(true));
        _messageTabs.TabPages.AddRange([delivery, interaction]);
        layout.Controls.Add(targetToolbar, 0, 0);
        layout.Controls.Add(_messageTabs, 0, 1);
        page.Controls.Add(layout);
        return page;
    }

    private void OpenMessageCenter(int tab)
    {
        ShowPage(3);
        _messageTabs.SelectedIndex = Math.Clamp(tab, 0, _messageTabs.TabCount - 1);
    }

    private Panel BuildLogPage()
    {
        var (page, content) = ScrollablePage();
        var card = Surface("运行日志", "采集失败、报警状态与通知结果会显示在这里。", 620);
        _log.Location = new Point(24, 74);
        _log.Size = new Size(860, 520);
        _log.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        card.Resize += (_, _) => _log.Size = new Size(Math.Max(100, card.ClientSize.Width - 48), Math.Max(100, card.ClientSize.Height - 98));
        card.Controls.Add(_log);
        AddBlock(content, card);
        return page;
    }

    private Panel BuildRuntimeCenterPage()
    {
        var page = new Panel { Dock = DockStyle.Fill, BackColor = AppBackground, Padding = new Padding(28, 18, 28, 28) };
        var tabs = new TabControl { Dock = DockStyle.Fill, Font = new Font("Microsoft YaHei UI", 10, FontStyle.Bold), Padding = new Point(24, 8) };
        var logTab = new TabPage("实时日志") { BackColor = AppBackground };
        var historyTab = new TabPage("历史报警") { BackColor = AppBackground };
        logTab.Controls.Add(BuildLogPage());
        historyTab.Controls.Add(BuildHistoryPage(true));
        tabs.TabPages.AddRange([logTab, historyTab]);
        page.Controls.Add(tabs);
        return page;
    }

    private void AddNavigation(FlowLayoutPanel navigation, string text, int pageIndex)
    {
        var button = new WorkspaceNavigationButton { Text = text, Width = 116, Height = 68, Margin = new Padding(0, 0, 6, 0), FlatStyle = FlatStyle.Flat, Font = new Font("Microsoft YaHei UI", 10), Cursor = Cursors.Hand };
        button.FlatAppearance.BorderSize = 0;
        button.FlatAppearance.MouseOverBackColor = Color.FromArgb(243, 248, 243);
        button.Click += (_, _) => ShowPage(pageIndex);
        _navigationButtons.Add(button);
        navigation.Controls.Add(button);
    }

    private void ShowPage(int pageIndex)
    {
        var titles = new[] { ("运行总览", "查看设备连接、报警与采集趋势"), ("设备工作区", "逐台配置连接、采集与电话提醒"), ("报警规则", "定义匹配条件、文本库与消息模板"), ("通知与交互", "配置通知通道、机器人按钮与设备操作"), ("运行记录", "查看实时日志、设备历史报警与执行记录") };
        _pageTitle.Text = titles[pageIndex].Item1;
        _pageSubtitle.Text = titles[pageIndex].Item2;
        for (var index = 0; index < _pages.Count; index++) _pages[index].Visible = index == pageIndex;
        for (var index = 0; index < _navigationButtons.Count; index++)
        {
            var current = index == pageIndex;
            ((WorkspaceNavigationButton)_navigationButtons[index]).IsActive = current;
            _navigationButtons[index].AccessibleDescription = current ? "当前工作区" : "切换工作区";
        }
        if (pageIndex == 4) RefreshHistorySources();
    }

    private void LoadConfigToUi()
    {
        _config.Normalize();
        RefreshSelectors();
        var source = _config.Sources[_sourceIndex];
        _sourceName.Text = source.Name;
        SelectKey(_protocol, source.Protocol);
        _sourceEnabled.Checked = source.Enabled;
        _pollSeconds.Value = Clamp((decimal)source.PollSeconds, _pollSeconds.Minimum, _pollSeconds.Maximum);
        LoadParameterGrid(_sourceParams, source.Params);
        RefreshSourceParameterEditor();
        LoadPhoneToUi(source);

        var rule = _config.Rules[_ruleIndex];
        SelectKey(_ruleSource, rule.SourceId);
        _ruleName.Text = rule.Name;
        _ruleEnabled.Checked = rule.Enabled;
        _severity.SelectedItem = rule.Severity;
        _matchCode.Text = rule.MatchCode;
        _matchKey.Text = rule.MatchKey;
        _matchValue.Text = rule.MatchValue;
        _cooldown.Value = Clamp((decimal)rule.CooldownSeconds, _cooldown.Minimum, _cooldown.Maximum);
        _messageTemplate.Text = rule.MessageTemplate;
        _recoveryTemplate.Text = rule.RecoveryTemplate;
        _note.Text = rule.Note;
        LoadAlarmTexts(rule.AlarmTexts);

        var target = _config.Targets[_targetIndex];
        _targetName.Text = target.Name;
        _targetEnabled.Checked = target.Enabled;
        SelectKey(_channel, target.Channel);
        _token.Text = target.Token;
        _webhookUrl.Text = target.WebhookUrl;
        _recipients.Text = string.Join(Environment.NewLine, target.Recipients);
        LoadParameterGrid(_targetParams, target.Params);
        RefreshMetrics();
        LoadBotToUi();
    }

    private bool SaveFromUi(bool persist = true)
    {
        try
        {
            _sourceParams.EndEdit(); _targetParams.EndEdit(); _alarmTexts.EndEdit();
            var updated = ConfigStore.Clone(_config);
            var oldSource = updated.Sources[_sourceIndex];
            updated.Sources[_sourceIndex] = new SourceConfig { Id = oldSource.Id, Name = Required(_sourceName.Text, "设备名称"), Protocol = SelectedKey(_protocol), Enabled = _sourceEnabled.Checked, PollSeconds = (double)_pollSeconds.Value, Params = ReadParameterGrid(_sourceParams), PhoneReminder = ReadPhoneFromUi() };
            var oldRule = updated.Rules[_ruleIndex];
            updated.Rules[_ruleIndex] = new AlarmRule { Id = oldRule.Id, Name = Required(_ruleName.Text, "规则名称"), SourceId = SelectedKey(_ruleSource), Enabled = _ruleEnabled.Checked, Severity = _severity.Text, MatchCode = _matchCode.Text.Trim(), MatchKey = _matchKey.Text.Trim(), MatchValue = _matchValue.Text.Trim(), CooldownSeconds = (double)_cooldown.Value, AlarmTexts = ReadAlarmTexts(), AlarmTextLists = oldRule.AlarmTextLists, MessageTemplate = _messageTemplate.Text.Trim(), RecoveryTemplate = _recoveryTemplate.Text.Trim(), Note = _note.Text.Trim() };
            var oldTarget = updated.Targets[_targetIndex];
            updated.Targets[_targetIndex] = new NotificationTarget { Id = oldTarget.Id, Name = Required(_targetName.Text, "目标名称"), Channel = SelectedKey(_channel), Enabled = _targetEnabled.Checked, Token = _token.Text.Trim(), WebhookUrl = _webhookUrl.Text.Trim(), Recipients = _recipients.Lines.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()).Distinct().ToList(), Params = ReadParameterGrid(_targetParams) };
            SaveBotFields(updated.Targets[_targetIndex]);
            if (persist) _store.Save(updated);
            _config = updated;
            RefreshMetrics();
            if (persist) Log(new RuntimeEvent("info", _monitor is null ? "配置已保存。" : "配置已保存，停止并重新启动监控后生效。"));
            return true;
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "配置错误", MessageBoxButtons.OK, MessageBoxIcon.Error); return false; }
    }

    private async Task ToggleMonitorAsync()
    {
        _monitorButton.Enabled = false;
        try
        {
        if (_monitor is not null)
        {
            if (_botRuntime is not null) { await _botRuntime.DisposeAsync(); _botRuntime = null; }
            await StopPhoneMonitorAsync();
            await _monitor.StopAsync();
            _monitor = null;
            _monitorButton.Text = "启动服务";
            _statusValue.Text = "已停止";
            _sourceHealth.Clear();
            RefreshConnectionGrid();
            Log(new RuntimeEvent("info", "监控已停止。"));
            return;
        }
        if (!SaveFromUi()) return;
        ConfigValidator.Validate(_config, forMonitoring: true);
        _monitor = new AlarmMonitor(_config, Log, history: _historyStore, calls: _phoneCalls);
        _sourceHealth.Clear();
        RefreshConnectionGrid();
        _monitor.Start();
        StartPhoneMonitor();
        _botRuntime = new BotRuntime(_config, () => _monitor?.StatusText() ?? "服务已停止。", () => _historyStore.FormatRecent(), Log,
            SaveDingContext, GetBotHistoryPage, () => _monitor?.ConnectedSourceIds() ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        _monitorButton.Text = "停止服务";
        _statusValue.Text = "运行中";
        Log(new RuntimeEvent("info", "监控已启动。"));
        }
        catch (Exception ex)
        {
            if (_botRuntime is not null) { try { await _botRuntime.DisposeAsync(); } catch { } _botRuntime = null; }
            try { await StopPhoneMonitorAsync(); } catch { }
            if (_monitor is not null) { try { await _monitor.StopAsync(); } catch { } _monitor = null; }
            _monitorButton.Text = "启动服务";
            _statusValue.Text = "已停止";
            MessageBox.Show(this, ex.Message, "服务错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally { if (!IsDisposed) _monitorButton.Enabled = true; }
    }

    private void SaveDingContext(string targetId, string conversationId, string userId)
    {
        if (string.IsNullOrWhiteSpace(conversationId)) return;
        lock (_store)
        {
            var updated = _store.Load();
            var target = updated.Targets.FirstOrDefault(x => x.Id == targetId);
            if (target is null) return;
            var changed = false;
            if (string.IsNullOrWhiteSpace(target.Params.Get("ding_conversation_id")))
            {
                target.Params["ding_conversation_id"] = JsonValues.Element(conversationId);
                changed = true;
            }
            if (string.IsNullOrWhiteSpace(target.Params.Get("admin_ids")) && !string.IsNullOrWhiteSpace(userId))
            {
                target.Params["admin_ids"] = JsonValues.Element(userId);
                changed = true;
            }
            if (!changed) return;
            _store.Save(updated);
            _config = updated;
        }
        BeginInvoke(() =>
        {
            LoadConfigToUi();
            Log(new RuntimeEvent("info", "已自动记录钉钉目标群会话 ID，正在刷新监控服务以启用报警互动卡片。"));
            _ = RestartMonitorWithCurrentConfigAsync();
        });
    }

    private async Task RestartMonitorWithCurrentConfigAsync()
    {
        if (_monitor is null) return;
        _monitorButton.Enabled = false;
        try
        {
            if (_botRuntime is not null) { await _botRuntime.DisposeAsync(); _botRuntime = null; }
            await StopPhoneMonitorAsync();
            await _monitor.StopAsync();
            _monitor = new AlarmMonitor(_config, Log, history: _historyStore, calls: _phoneCalls);
            _sourceHealth.Clear();
            RefreshConnectionGrid();
            _monitor.Start();
            StartPhoneMonitor();
            _botRuntime = new BotRuntime(_config, () => _monitor?.StatusText() ?? "服务已停止。", () => _historyStore.FormatRecent(), Log,
                SaveDingContext, GetBotHistoryPage, () => _monitor?.ConnectedSourceIds() ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase));
            _monitorButton.Text = "停止服务";
            _statusValue.Text = "运行中";
            Log(new RuntimeEvent("info", "监控服务已刷新，报警推送将使用最新钉钉互动卡片配置。"));
        }
        catch (Exception ex)
        {
            if (_botRuntime is not null) { try { await _botRuntime.DisposeAsync(); } catch { } _botRuntime = null; }
            try { await StopPhoneMonitorAsync(); } catch { }
            if (_monitor is not null) { try { await _monitor.StopAsync(); } catch { } _monitor = null; }
            _monitorButton.Text = "启动服务";
            _statusValue.Text = "已停止";
            MessageBox.Show(this, ex.Message, "服务刷新失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally { if (!IsDisposed) _monitorButton.Enabled = true; }
    }

    private async Task TestCurrentTargetAsync()
    {
        if (!SaveFromUi()) return;
        try
        {
            var result = await NotifierFactory.Create(_config.Targets[_targetIndex]).SendAsync("这是一条工业报警推送中心的测试消息。", CancellationToken.None);
            MessageBox.Show(this, $"已发送 {result} 个目标。", "测试成功", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "测试失败", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }

    private async Task TestEnabledTargetsAsync()
    {
        if (!SaveFromUi()) return;
        var results = new List<string>();
        foreach (var target in _config.Targets.Where(x => x.Enabled))
        {
            try { results.Add($"{target.Name}：已发送 {await NotifierFactory.Create(target).SendAsync("这是一条工业报警推送中心的测试消息。", CancellationToken.None)} 个目标"); }
            catch (Exception ex) { results.Add($"{target.Name}：失败，{ex.Message}"); }
        }
        MessageBox.Show(this, results.Count == 0 ? "没有启用的通知目标。" : string.Join(Environment.NewLine, results), "测试结果", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void PreviewRule()
    {
        var source = new SourceConfig { Id = "preview", Name = _sourceName.Text.Trim(), Protocol = SelectedKey(_protocol) };
        var rule = new AlarmRule { Name = _ruleName.Text.Trim(), Severity = _severity.Text, AlarmTexts = ReadAlarmTexts(), MessageTemplate = _messageTemplate.Text };
        var signal = new Signal(true, "1001", "客户端配置报警文本", new Dictionary<string, object?> { ["value"] = 1, ["active"] = true });
        MessageBox.Show(this, MessageRenderer.Render(rule.MessageTemplate, source, signal, rule), "报警文本预览", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void Log(RuntimeEvent runtimeEvent)
    {
        if (IsDisposed || Disposing || !IsHandleCreated) return;
        if (InvokeRequired)
        {
            try { BeginInvoke(() => Log(runtimeEvent)); } catch (InvalidOperationException) { }
            return;
        }
        if (runtimeEvent.SourceId.Length > 0 && runtimeEvent.Level is "connected" or "heartbeat" or "disconnected")
        {
            UpdateSourceHealth(runtimeEvent);
            if (runtimeEvent.Level == "heartbeat") return;
        }
        if (_log.TextLength > 250000) _log.Text = _log.Text[^150000..];
        var level = runtimeEvent.Level switch { "connected" => "连接", "disconnected" => "断线", _ => runtimeEvent.Level };
        _log.AppendText($"[{DateTime.Now:HH:mm:ss}] [{level}] {runtimeEvent.Message}{Environment.NewLine}");
    }

    private void UpdateSourceHealth(RuntimeEvent runtimeEvent)
    {
        if (!_sourceHealth.TryGetValue(runtimeEvent.SourceId, out var health))
            _sourceHealth[runtimeEvent.SourceId] = health = new SourceHealth();
        health.Detail = runtimeEvent.Message;
        if (runtimeEvent.Level is "connected" or "heartbeat")
        {
            health.Connected = true;
            health.LastSuccess = DateTime.Now;
        }
        else health.Connected = false;
        RefreshConnectionGrid();
    }

    private void RefreshConnectionGrid()
    {
        if (IsDisposed || _connectionGrid.IsDisposed) return;
        var selected = _connectionGrid.CurrentRow?.Tag as string;
        _connectionGrid.Rows.Clear();
        foreach (var source in _config.Sources)
        {
            var state = "等待启动";
            var last = "—";
            var detail = source.Enabled ? "启动服务后检测连接" : "采集源已禁用";
            var color = Color.FromArgb(100, 116, 139);
            if (!source.Enabled) state = "已禁用";
            else if (_monitor is not null)
            {
                state = "正在连接";
                detail = "等待首次有效采集";
                if (_sourceHealth.TryGetValue(source.Id, out var health))
                {
                    detail = health.Detail;
                    if (health.LastSuccess is DateTime success)
                    {
                        var age = Math.Max(0, (DateTime.Now - success).TotalSeconds);
                        last = $"{success:HH:mm:ss}（{age:0} 秒前）";
                        var staleAfter = Math.Max(15, source.PollSeconds * 3 + 5);
                        if (health.Connected && age <= staleAfter) { state = "● 连接正常"; color = Color.FromArgb(22, 163, 74); }
                        else if (health.Connected) { state = "● 心跳超时"; color = Color.FromArgb(234, 88, 12); }
                        else { state = "● 已断线"; color = Color.FromArgb(220, 38, 38); }
                    }
                    else if (!health.Connected) { state = "● 已断线"; color = Color.FromArgb(220, 38, 38); }
                }
            }
            var index = _connectionGrid.Rows.Add(source.Name, ProtocolLabel(source.Protocol), state, last, detail);
            var row = _connectionGrid.Rows[index]; row.Tag = source.Id; row.Cells[2].Style.ForeColor = color;
            if (source.Id == selected) _connectionGrid.CurrentCell = row.Cells[0];
        }
    }

    private sealed class SourceHealth
    {
        public bool Connected { get; set; }
        public DateTime? LastSuccess { get; set; }
        public string Detail { get; set; } = "";
    }

    private void RefreshMetrics()
    {
        _sourceCount.Text = $"{_monitor?.ConnectedSourceIds().Count ?? 0} / {_config.Sources.Count(x => x.Enabled)}";
        _ruleCount.Text = _config.Rules.Count.ToString();
        _targetCount.Text = _config.Targets.Count(x => x.Enabled).ToString();
        _servicePill.Active = _monitor is not null;
        _servicePill.Caption = _monitor is null ? "服务待机" : "服务运行中";
        _servicePill.Invalidate();
    }

    private void LoadAlarmTexts(IEnumerable<AlarmText> texts)
    {
        _alarmTexts.Rows.Clear();
        foreach (var item in texts) _alarmTexts.Rows.Add(NormalizeSeverity(item.Category), item.Code, item.InstanceName, item.FbNumber, item.Text, item.TextListName, item.TextListIndex, false);
        while (_alarmTexts.Rows.Count < 30) _alarmTexts.Rows.Add("默认", "", "", "", "");
    }

    private List<AlarmText> ReadAlarmTexts() => _alarmTexts.Rows.Cast<DataGridViewRow>().Where(x => !x.IsNewRow).Select(row => new AlarmText { Category = NormalizeSeverity(Cell(row, 0, "默认")), Code = Cell(row, 1), InstanceName = Cell(row, 2), FbNumber = Cell(row, 3), Text = Cell(row, 4), TextListName = Cell(row, 5), TextListIndex = Cell(row, 6) }).ToList();
    private void AddAlarmRows(int count) { for (var index = 0; index < count; index++) _alarmTexts.Rows.Add("默认", "", "", "", "", "", "", false); }
    private void ToggleAllAlarmRows()
    {
        _alarmTexts.EndEdit();
        var rows = _alarmTexts.Rows.Cast<DataGridViewRow>().Where(row => !row.IsNewRow).ToArray();
        var select = rows.Any(row => row.Cells[7].Value is not true);
        foreach (var row in rows) row.Cells[7].Value = select;
    }
    private void DeleteAlarmRows()
    {
        _alarmTexts.EndEdit();
        var rows = _alarmTexts.Rows.Cast<DataGridViewRow>().Where(row => !row.IsNewRow && (row.Selected || row.Cells[7].Value is true)).OrderByDescending(row => row.Index).ToArray();
        if (rows.Length == 0) { MessageBox.Show(this, "请勾选或选中要删除的报警文本。", "没有选择记录", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
        if (MessageBox.Show(this, $"确定删除选中的 {rows.Length} 条报警文本吗？", "确认删除", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        foreach (var row in rows) _alarmTexts.Rows.RemoveAt(row.Index);
        while (_alarmTexts.Rows.Count < 30) _alarmTexts.Rows.Add("默认", "", "", "", "", "", "", false);
    }

    private void ImportAlarmTexts()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "导入报警文本库",
            Filter = "报警文本文件 (*.csv;*.tsv;*.txt)|*.csv;*.tsv;*.txt|所有文件 (*.*)|*.*",
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            var imported = ParseAlarmTextFile(dialog.FileName);
            if (imported.Count == 0) { MessageBox.Show(this, "没有读取到有效报警文本。文件至少需要“编号、文本”两列。", "导入文本库", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
            MergeAlarmTexts(imported);
            MessageBox.Show(this, $"已导入 {imported.Count} 条报警文本。相同报警编号已自动覆盖。", "导入完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "导入失败", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }

    private List<AlarmText> ParseAlarmTextFile(string path)
    {
        var lines = File.ReadAllLines(path);
        var result = new List<AlarmText>();
        Dictionary<string, int>? columns = null;
        foreach (var raw in lines)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var cells = SplitDelimitedLine(raw);
            if (cells.Count < 2) continue;
            if (columns is null && IsHeaderRow(cells)) { columns = HeaderColumns(cells); continue; }
            var category = columns is null
                ? (cells.Count >= 3 ? NormalizeSeverity(cells[0]) : NormalizeSeverity(_severity.Text))
                : NormalizeSeverity(Column(cells, columns, "level", _severity.Text));
            var code = columns is null ? (cells.Count >= 3 ? cells[1].Trim() : cells[0].Trim()) : Column(cells, columns, "code");
            var instance = columns is null ? "" : Column(cells, columns, "instance");
            var fbNumber = columns is null ? "" : Column(cells, columns, "fb_number");
            var textListName = columns is null ? "" : Column(cells, columns, "text_list_name");
            var textListIndex = columns is null ? "" : Column(cells, columns, "text_list_index");
            var text = columns is null ? (cells.Count >= 3 ? string.Join(" ", cells.Skip(2)).Trim() : cells[1].Trim()) : Column(cells, columns, "text");
            if (code.Length == 0 || text.Length == 0) continue;
            result.Add(new AlarmText { Category = category, Code = code, InstanceName = instance, FbNumber = fbNumber, TextListName = textListName, TextListIndex = textListIndex, Text = text });
        }
        return result;
    }

    private void MergeAlarmTexts(IEnumerable<AlarmText> imported)
    {
        var current = ReadAlarmTexts().Where(x => !string.IsNullOrWhiteSpace(x.Code) || !string.IsNullOrWhiteSpace(x.Text)).ToList();
        var byCode = current.Where(x => !string.IsNullOrWhiteSpace(x.Code)).GroupBy(x => x.Code.Trim(), StringComparer.OrdinalIgnoreCase).ToDictionary(x => x.Key, x => x.First(), StringComparer.OrdinalIgnoreCase);
        foreach (var item in imported)
        {
            if (string.IsNullOrWhiteSpace(item.Code)) continue;
            if (byCode.TryGetValue(item.Code.Trim(), out var existing))
            {
                existing.Category = item.Category;
                existing.InstanceName = item.InstanceName;
                existing.FbNumber = item.FbNumber;
                existing.TextListName = item.TextListName;
                existing.TextListIndex = item.TextListIndex;
                existing.Text = item.Text;
            }
            else { current.Add(item); byCode[item.Code.Trim()] = item; }
        }
        LoadAlarmTexts(current);
    }

    private static Dictionary<string, int> HeaderColumns(IReadOnlyList<string> cells)
    {
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < cells.Count; index++)
        {
            var name = cells[index].Trim().TrimStart('\uFEFF').Replace("_", " ").ToLowerInvariant();
            if (!result.ContainsKey("level") && (name.Contains("级别") || name.Contains("severity") || name == "level" || name.Contains("category"))) result["level"] = index;
            if (!result.ContainsKey("code") && (name.Contains("alarmid") || name.Contains("alarm id") || name.Contains("报警编号") || name == "code" || name == "编号")) result["code"] = index;
            if (!result.ContainsKey("instance") && (name.Contains("实例") || name.Contains("instance") || name.Contains("对象"))) result["instance"] = index;
            if (!result.ContainsKey("fb_number") && (name.Contains("fb编号") || name.Contains("fb number") || name == "fb")) result["fb_number"] = index;
            if (!result.ContainsKey("text_list_name") && (name.Contains("文本列表") || name.Contains("text list"))) result["text_list_name"] = index;
            if (!result.ContainsKey("text_list_index") && (name.Contains("文本索引") || name.Contains("list index"))) result["text_list_index"] = index;
            if (!result.ContainsKey("text") && (name.Contains("详细文本") || name.Contains("报警文本") || name == "text" || name.Contains("message") || name.Contains("description"))) result["text"] = index;
        }
        return result;
    }

    private static string Column(IReadOnlyList<string> cells, IReadOnlyDictionary<string, int> columns, string name, string fallback = "")
        => columns.TryGetValue(name, out var index) && index >= 0 && index < cells.Count ? cells[index].Trim() : fallback;

    private static bool IsHeaderRow(IReadOnlyList<string> cells)
    {
        var joined = string.Join(",", cells).ToLowerInvariant();
        return joined.Contains("报警编号") || joined.Contains("alarm") || joined.Contains("code") || joined.Contains("文本") || joined.Contains("text");
    }

    private static List<string> SplitDelimitedLine(string line)
    {
        var delimiter = line.Contains('\t') ? '\t' : ',';
        var result = new List<string>();
        var current = "";
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (c == '"')
            {
                if (quoted && i + 1 < line.Length && line[i + 1] == '"') { current += '"'; i++; }
                else quoted = !quoted;
            }
            else if (c == delimiter && !quoted) { result.Add(current.Trim()); current = ""; }
            else current += c;
        }
        result.Add(current.Trim());
        return result;
    }

    private static string NormalizeSeverity(string value) => value.Trim().ToLowerInvariant() switch
    {
        "信息" or "提示" or "正常" or "info" => "info",
        "预警" or "警告" or "报警" or "warning" or "warn" => "warning",
        "严重" or "紧急" or "故障" or "critical" or "error" => "critical",
        "默认" or "" => "默认",
        _ => "默认"
    };

    private static void LoadParameterGrid(DataGridView grid, IReadOnlyDictionary<string, JsonElement> parameters)
    {
        grid.Rows.Clear();
        foreach (var (key, value) in parameters.OrderBy(x => x.Key))
        {
            var index = grid.Rows.Add(ParameterLabel(key), value.ToString());
            grid.Rows[index].Tag = key;
        }
    }

    private static Dictionary<string, JsonElement> ReadParameterGrid(DataGridView grid)
    {
        var result = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        foreach (DataGridViewRow row in grid.Rows)
        {
            if (row.IsNewRow) continue;
            var key = row.Tag as string ?? Cell(row, 0);
            if (!string.IsNullOrWhiteSpace(key)) result[key] = JsonValues.Element(Cell(row, 1));
        }
        return result;
    }

    private void EditSelectedParameter(DataGridView grid)
    {
        if (grid.CurrentCell is null || grid.CurrentCell.RowIndex < 0) return;
        var row = grid.Rows[grid.CurrentCell.RowIndex];
        var key = row.Tag as string ?? Cell(row, 0);
        if (string.IsNullOrWhiteSpace(key)) return;
        var label = Convert.ToString(row.Cells[0].Value) ?? key;
        var value = Cell(row, 1);
        using var dialog = new LargeTextEditDialog(label, key, value);
        if (dialog.ShowDialog(this) == DialogResult.OK) row.Cells[1].Value = dialog.Value;
    }

    private static (Panel Page, TableLayoutPanel Content) ScrollablePage()
    {
        var page = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = AppBackground };
        var content = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1, Padding = new Padding(30, 26, 30, 42), BackColor = AppBackground };
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        page.Controls.Add(content);
        return (page, content);
    }

    private static void AddBlock(TableLayoutPanel content, Control control)
    {
        var row = content.RowStyles.Count;
        content.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        control.Dock = DockStyle.Top;
        control.Margin = new Padding(0, 0, 0, 16);
        content.Controls.Add(control, 0, row);
    }

    private static SurfacePanel MetricCard(string title, string subtitle, Label value, Color accent)
    {
        var card = new SurfacePanel { Dock = DockStyle.Fill, Margin = new Padding(0, 0, 8, 0), Height = 100, BackColor = Color.White };
        card.Controls.Add(value); value.Location = new Point(18, 36); value.ForeColor = VisualTheme.Ink;
        card.Controls.Add(new Label { Text = title, AutoSize = true, ForeColor = VisualTheme.Muted, Font = new Font("Microsoft YaHei UI", 9), Location = new Point(18, 12) });
        card.Controls.Add(new Label { Text = subtitle, AutoSize = true, ForeColor = VisualTheme.Muted, Font = new Font("Microsoft YaHei UI", 8), Location = new Point(18, 77) });
        return card;
    }

    private static SurfacePanel FormCard(string title, string description, IEnumerable<(string Label, Control Control)> fields)
    {
        var items = fields.ToList();
        var rows = items.Chunk(2).ToArray();
        var heights = rows.Select(row => row.Max(x => x.Control is TextBox { Multiline: true } ? x.Control.Height + 22 : 46) + 46).ToArray();
        var card = Surface(title, description, 92 + heights.Sum());
        var form = new TableLayoutPanel { ColumnCount = 2, Location = new Point(18, 78), Size = new Size(600, heights.Sum()), Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right, BackColor = Color.Transparent };
        form.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50)); form.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        for (var row = 0; row < rows.Length; row++)
        {
            form.RowStyles.Add(new RowStyle(SizeType.Absolute, heights[row]));
            for (var column = 0; column < rows[row].Length; column++)
            {
                var (label, control) = rows[row][column];
                var field = new Panel { Dock = DockStyle.Fill, Margin = new Padding(8, 0, 14, 10), BackColor = Color.Transparent };
                field.Controls.Add(new Label { Text = label, Location = new Point(2, 0), Size = new Size(320, 25), Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top, AutoEllipsis = true, ForeColor = VisualTheme.Ink, Font = new Font("Microsoft YaHei UI", 9) });
                Control input = control is CheckBox ? control : new InputShell(control);
                input.Location = new Point(0, 29); input.Width = field.Width; input.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top;
                field.Controls.Add(input); form.Controls.Add(field, column, row);
            }
        }
        card.Resize += (_, _) => form.Width = Math.Max(200, card.Width - 40);
        card.Controls.Add(form); return card;
    }
    private static SurfacePanel EditorCard(string title, string description, Control editor)
    {
        var input = new InputShell(editor);
        var card = Surface(title, description, input.Height + 90);
        input.Location = new Point(24, 64);
        input.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        card.Resize += (_, _) => input.Width = Math.Max(200, card.ClientSize.Width - 48);
        card.Controls.Add(input);
        return card;
    }

    private static SurfacePanel GridCard(string title, string description, DataGridView grid, int height)
    {
        var card = Surface(title, description, height);
        grid.Location = new Point(24, 76);
        grid.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        card.Resize += (_, _) => grid.Size = new Size(Math.Max(200, card.ClientSize.Width - 48), Math.Max(80, card.ClientSize.Height - 96));
        card.Controls.Add(grid);
        return card;
    }

    private static void AddGridActions(Control parent, IEnumerable<(string Text, Action Action)> actions, int top)
    {
        var panel = new FlowLayoutPanel { AutoSize = true, Height = 34, Padding = new Padding(0), Location = new Point(24, top), Anchor = AnchorStyles.Top | AnchorStyles.Left };
        foreach (var (text, action) in actions)
        {
            var button = SmallButton(text);
            button.Click += (_, _) => action();
            panel.Controls.Add(button);
        }
        parent.Controls.Add(panel);
    }

    private static SurfacePanel HintCard(string title, string text)
    {
        var card = new SurfacePanel { Height = 112, BackColor = Color.FromArgb(237, 252, 250), BorderColor = Color.FromArgb(153, 246, 228) };
        card.Controls.Add(new Label { Text = title, AutoSize = true, Font = new Font("Microsoft YaHei UI", 10, FontStyle.Bold), ForeColor = Color.FromArgb(15, 118, 110), Location = new Point(24, 18) });
        card.Controls.Add(new Label { Text = text, AutoSize = false, ForeColor = Color.FromArgb(17, 94, 89), Font = new Font("Microsoft YaHei UI", 9), Location = new Point(24, 50), Size = new Size(800, 42), Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right });
        return card;
    }

    private static SurfacePanel Surface(string title, string description, int height)
    {
        var card = new SurfacePanel { Height = height, BackColor = Color.White };
        card.Controls.Add(new Label { Text = title, AutoSize = true, ForeColor = VisualTheme.Ink, Font = new Font("Microsoft YaHei UI", 11, FontStyle.Bold), Location = new Point(24, 17) });
        card.Controls.Add(new Label { Text = description, AutoSize = true, ForeColor = VisualTheme.Muted, Font = new Font("Microsoft YaHei UI", 9), Location = new Point(24, 43) });
        return card;
    }

    private static PageBannerPanel PageBanner(string eyebrow, string title, string description, string number, Color accent)
    {
        var banner = new PageBannerPanel { Height = 80 };
        banner.Controls.Add(new Label { Text = title, AutoSize = true, BackColor = Color.Transparent,
            ForeColor = VisualTheme.Ink, Font = new Font("Microsoft YaHei UI", 12, FontStyle.Bold), Location = new Point(22, 12) });
        banner.Controls.Add(new Label { Text = description, AutoSize = true, BackColor = Color.Transparent,
            ForeColor = VisualTheme.Muted, Font = new Font("Microsoft YaHei UI", 9), Location = new Point(23, 43) });
        return banner;
    }
    private static TextBox Input(bool password = false) => new() { Height = 30, BorderStyle = BorderStyle.None, BackColor = Color.White, UseSystemPasswordChar = password, Font = new Font("Microsoft YaHei UI", 10) };
    private static TextBox Multiline(int height) => new() { Multiline = true, ScrollBars = ScrollBars.Vertical, Height = height, BorderStyle = BorderStyle.None, BackColor = Color.White, Font = new Font("Microsoft YaHei UI", 10) };
    private static NumericUpDown Numeric(decimal minimum, decimal maximum, decimal increment, int decimals) => new() { Minimum = minimum, Maximum = maximum, Increment = increment, DecimalPlaces = decimals, Height = 32, BorderStyle = BorderStyle.None, BackColor = Color.White, Font = new Font("Microsoft YaHei UI", 10) };
    private static CheckBox Toggle(string text) => new ModernSwitch() { Text = text, AutoSize = false, ForeColor = Color.FromArgb(105, 80, 232), Font = new Font("Microsoft YaHei UI", 9, FontStyle.Bold), Padding = new Padding(0, 5, 0, 0) };
    private static ComboBox Combo(params string[] values) { var combo = new ModernComboBox(); combo.Items.AddRange(values); return combo; }
    private static Label MetricValue(string text, Color color) => new() { Text = text, AutoSize = true, Font = new Font("Microsoft YaHei UI", 20, FontStyle.Bold), ForeColor = color };
    private static Button ActionButton(string text, bool primary)
    {
        var button = new ModernButton { Text = text, AutoSize = true, Height = 42, Padding = new Padding(16, 0, 16, 0), FlatStyle = FlatStyle.Flat, Font = new Font("Microsoft YaHei UI", 9, FontStyle.Bold), Cursor = Cursors.Hand, BackColor = primary ? PrimaryColor : Color.White, ForeColor = primary ? Color.White : Color.FromArgb(51, 65, 85) };
        button.FlatAppearance.BorderSize = 1;
        button.FlatAppearance.BorderColor = primary ? PrimaryColor : Color.FromArgb(214, 222, 236);
        button.FlatAppearance.MouseOverBackColor = primary ? Color.FromArgb(49, 88, 230) : Color.FromArgb(248, 250, 252);
        return button;
    }
    private static Button SmallButton(string text) { var button = ActionButton(text, false); button.Height = 30; button.Font = new Font("Microsoft YaHei UI", 8); return button; }
    private static DataGridView ParamsGrid()
    {
        var grid = new DataGridView { AutoGenerateColumns = false, AllowUserToAddRows = true, AllowUserToDeleteRows = true, RowHeadersVisible = false, BackgroundColor = Color.FromArgb(252, 253, 255), BorderStyle = BorderStyle.None, CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal, GridColor = Color.FromArgb(231, 236, 247), EnableHeadersVisualStyles = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect, MultiSelect = true, ColumnHeadersHeight = 46, RowTemplate = { Height = 42 }, EditMode = DataGridViewEditMode.EditOnEnter, AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.None };
        grid.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle { BackColor = Color.FromArgb(235, 241, 255), ForeColor = Color.FromArgb(38, 54, 94), Font = new Font("Microsoft YaHei UI", 9, FontStyle.Bold), Alignment = DataGridViewContentAlignment.MiddleLeft, Padding = new Padding(8, 0, 0, 0) };
        grid.DefaultCellStyle = new DataGridViewCellStyle { BackColor = Color.FromArgb(252, 253, 255), SelectionBackColor = Color.FromArgb(222, 231, 255), SelectionForeColor = Color.FromArgb(15, 23, 42), ForeColor = Color.FromArgb(42, 53, 76), Font = new Font("Microsoft YaHei UI", 9), Padding = new Padding(8, 0, 0, 0) };
        grid.AlternatingRowsDefaultCellStyle = new DataGridViewCellStyle { BackColor = Color.FromArgb(247, 250, 255) };
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "配置项（中文）", Width = 320 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "参数值", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill });
        grid.CellToolTipTextNeeded += (_, e) => { if (e.RowIndex >= 0 && e.ColumnIndex >= 0) e.ToolTipText = Convert.ToString(grid.Rows[e.RowIndex].Cells[e.ColumnIndex].Value) ?? ""; };
        grid.CellDoubleClick += (_, e) =>
        {
            if (e.RowIndex < 0 || e.ColumnIndex != 1) return;
            var row = grid.Rows[e.RowIndex];
            using var dialog = new LargeTextEditDialog(Convert.ToString(row.Cells[0].Value) ?? "参数值", row.Tag as string ?? "", Convert.ToString(row.Cells[1].Value) ?? "");
            if (dialog.ShowDialog(grid.FindForm()) == DialogResult.OK) row.Cells[1].Value = dialog.Value;
        };
        return grid;
    }
    private static DataGridView SourceHealthGrid()
    {
        var grid = ParamsGrid();
        grid.ReadOnly = true; grid.AllowUserToAddRows = false; grid.AllowUserToDeleteRows = false; grid.MultiSelect = false;
        grid.Columns.Clear();
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "采集设备", Width = 190 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "协议", Width = 120 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "连接状态", Width = 135 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "最后成功心跳", Width = 190 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "连接详情", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill });
        return grid;
    }
    private static DataGridView AlarmTextGrid()
    {
        var grid = ParamsGrid();
        grid.AllowUserToAddRows = false;
        grid.Columns.Clear();
        grid.Columns.Add(new DataGridViewComboBoxColumn { HeaderText = "报警级别", Width = 150, FlatStyle = FlatStyle.Flat, Items = { "默认", "info", "warning", "critical" } });
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "AlarmId / 报警编号", Width = 190 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "实例名", Width = 220 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "FB 编号", Width = 105 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "详细报警文本（可编辑）", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill });
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "文本列表", Visible = false });
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "文本索引", Visible = false });
        var selected = new DataGridViewCheckBoxColumn { HeaderText = "☐ 全选", Width = 76, FalseValue = false, TrueValue = true };
        grid.Columns.Add(selected);
        selected.DisplayIndex = 0;
        grid.ColumnHeaderMouseClick += (_, e) =>
        {
            if (e.ColumnIndex != selected.Index) return;
            grid.EndEdit();
            var rows = grid.Rows.Cast<DataGridViewRow>().Where(row => !row.IsNewRow).ToArray();
            var value = rows.Any(row => row.Cells[selected.Index].Value is not true);
            foreach (var row in rows) row.Cells[selected.Index].Value = value;
            selected.HeaderText = value ? "☑ 全不选" : "☐ 全选";
        };
        grid.CellFormatting += (_, e) =>
        {
            if (e.RowIndex < 0 || e.ColumnIndex != 4 || e.Value is not string text || !text.Contains('@')) return;
            e.Value = OpcUaProDiagSource.DescribeAssociatedValuePlaceholders(text);
            e.FormattingApplied = true;
        };
        return grid;
    }
    private static decimal Clamp(decimal value, decimal minimum, decimal maximum) => Math.Min(maximum, Math.Max(minimum, value));
    private static string Required(string value, string name) => string.IsNullOrWhiteSpace(value) ? throw new InvalidOperationException($"请填写{name}。") : value.Trim();
    private static string Cell(DataGridViewRow row, int index, string fallback = "") => Convert.ToString(row.Cells[index].Value)?.Trim() is { Length: > 0 } value ? value : fallback;
    private static string SelectedKey(ComboBox combo) => (combo.SelectedItem as Option)?.Key ?? combo.Text;
    private static void SelectKey(ComboBox combo, string key) { foreach (var item in combo.Items.OfType<Option>()) if (item.Key.Equals(key, StringComparison.OrdinalIgnoreCase)) { combo.SelectedItem = item; return; } combo.SelectedIndex = 0; }
    private static string ProtocolLabel(string key) => key switch { "mock" => "Mock 仿真", "s7" => "Siemens S7", "modbus_tcp" => "Modbus TCP", "modbus_rtu" => "Modbus RTU", "mqtt" => "MQTT", "opcua" => "OPC UA（报警码数组）", "opcua_prodiag" => "OPC UA ProDiag（A&C）", "http_json" => "HTTP JSON", "tcp_json" => "TCP JSON", "udp_json" => "UDP JSON", "serial_text" => "串口文本", "ethernet_ip" => "EtherNet/IP", "ads" => "Beckhoff ADS", _ => key };
    private static string ChannelLabel(string key) => key switch { "telegram" => "Telegram Bot", "dingtalk" => "钉钉机器人", "wecom" => "企业微信机器人", "feishu" => "飞书机器人", "custom_webhook" => "自定义 Webhook", _ => key };
    private static string ParameterLabel(string key) => key switch
    {
        "host" => "设备地址 / 主机名", "port" => "端口", "timeout" => "通信超时（秒）", "username" => "用户名", "password" => "密码",
        "rack" => "S7 机架号", "slot" => "S7 插槽号", "cpu_type" => "S7 CPU 类型", "db_number" => "S7 DB 编号",
        "trigger_byte" => "触发位字节偏移", "trigger_bit" => "触发位编号（0～7）", "code_offset" => "报警码字节偏移",
        "text_offset" => "报警文本字节偏移", "text_length" => "报警文本长度", "encoding" => "文本编码",
        "endpoint" => "OPC UA 服务地址", "active_count_node_id" => "有效报警数量节点 ID", "alarm_code_list_node_id" => "报警码数组节点 ID",
        "event_node_id" => "ProDiag 事件节点 ID（留空自动发现）", "event_queue_size" => "ProDiag 事件队列容量", "condition_refresh" => "连接后同步当前报警（是/否）",
        "publishing_interval_ms" => "订阅发布间隔（毫秒）", "sampling_interval_ms" => "节点采样间隔（毫秒）",
        "subscription_coalesce_ms" => "双节点合并等待（毫秒）", "max_alarm_count" => "最大有效报警数量", "inactive_code" => "忽略的报警码",
        "unit_id" => "Modbus 从站号", "table" => "Modbus 数据区", "address" => "数据地址（零基）", "active_value" => "报警触发值",
        "baudrate" => "串口波特率", "parity" => "串口校验位", "stopbits" => "串口停止位", "bytesize" => "串口数据位",
        "topic" => "MQTT 主题", "qos" => "MQTT 服务质量等级", "active_key" => "状态字段名", "code_key" => "报警码字段名",
        "text_key" => "报警文本字段名", "method" => "HTTP 请求方法", "url" => "请求地址", "headers_json" => "请求头 JSON",
        "body_json" => "请求体 JSON", "request_text" => "请求文本", "delimiter" => "消息结束符", "max_response_bytes" => "最大响应字节数",
        "active_text" => "报警匹配文本", "net_id" => "ADS 网络 ID", "symbol" => "ADS 变量名", "tag" => "EtherNet/IP 标签名",
        "toggle_every" => "仿真切换间隔（读取次数）", "code" => "固定报警码", "text" => "默认报警文本",
        "receive_enabled" => "启用机器人交互（是/否）", "allow_plc_write" => "允许机器人写入 PLC（是/否）", "alarm_show_buttons" => "报警推送显示操作按钮（是/否）", "admin_ids" => "机器人管理员用户 ID",
        "command_defs" => "机器人动作配置", "bot_client_id" => "机器人 ID / Client ID", "bot_client_secret" => "机器人 Secret",
        "card_template_id" => "钉钉互动卡片模板 ID", "ding_conversation_id" => "钉钉目标群会话 ID", "button_columns" => "Telegram 按钮列数", "welcome_text" => "机器人欢迎消息",
        "unknown_reply" => "未知命令回复", "payload_template" => "Webhook 请求体模板",
        _ => key
    };
    private sealed record Option(string Key, string Label) { public override string ToString() => Label; }
}

public sealed class LargeTextEditDialog : Form
{
    private readonly TextBox _value;

    public LargeTextEditDialog(string label, string key, string value)
    {
        Text = "编辑参数值";
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = false;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        ClientSize = new Size(760, 430);
        Font = new Font("Microsoft YaHei UI", 10);
        BackColor = Color.FromArgb(245, 247, 252);

        var title = new Label
        {
            Text = label,
            Location = new Point(22, 18),
            Size = new Size(700, 24),
            Font = new Font("Microsoft YaHei UI", 11, FontStyle.Bold),
            ForeColor = Color.FromArgb(15, 23, 42)
        };
        var subtitle = new Label
        {
            Text = string.IsNullOrWhiteSpace(key) ? "参数值" : key,
            Location = new Point(22, 44),
            Size = new Size(700, 22),
            ForeColor = Color.FromArgb(100, 116, 139)
        };
        _value = new TextBox
        {
            Multiline = true,
            ScrollBars = ScrollBars.Vertical,
            WordWrap = false,
            Text = value,
            Location = new Point(22, 78),
            Size = new Size(716, 270),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom,
            Font = new Font("Consolas", 10),
            BorderStyle = BorderStyle.FixedSingle
        };
        var ok = new ModernButton { Text = "确定", DialogResult = DialogResult.OK, Location = new Point(584, 368), Size = new Size(74, 36), BackColor = Color.FromArgb(105, 80, 232), ForeColor = Color.White, FlatStyle = FlatStyle.Flat };
        var cancel = new ModernButton { Text = "取消", DialogResult = DialogResult.Cancel, Location = new Point(664, 368), Size = new Size(74, 36), BackColor = Color.White, ForeColor = Color.FromArgb(51, 65, 85), FlatStyle = FlatStyle.Flat };
        ok.FlatAppearance.BorderSize = 0;
        cancel.FlatAppearance.BorderColor = Color.FromArgb(203, 213, 225);
        AcceptButton = ok;
        CancelButton = cancel;
        Controls.AddRange([title, subtitle, _value, ok, cancel]);
    }

    public string Value => _value.Text.Trim();
}

public class SurfacePanel : Panel
{
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Color BorderColor { get; set; } = Color.FromArgb(223, 229, 223);
    public SurfacePanel()
    {
        BackColor = Color.White;
        DoubleBuffered = true;
        Padding = new Padding(0);
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        e.Graphics.Clear(VisualTheme.Canvas(this));
        using var path = VisualTheme.Round(new RectangleF(1, 1, Width - 3, Height - 3), 14);
        using var brush = new SolidBrush(BackColor);
        e.Graphics.FillPath(brush, path);
        using var pen = new Pen(BorderColor);
        e.Graphics.DrawPath(pen, path);
    }
}

