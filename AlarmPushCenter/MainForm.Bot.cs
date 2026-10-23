namespace AlarmPushCenter;

public sealed partial class MainForm
{
    private BotRuntime? _botRuntime;
    private readonly ComboBox _botTarget = Combo();
    private readonly Label _botCurrentTarget = new() { AutoSize = true, ForeColor = VisualTheme.Ink, Font = new Font("Microsoft YaHei UI", 11, FontStyle.Bold) };
    private readonly CheckBox _botEnabled = Toggle("启用双向交互");
    private readonly CheckBox _botAllowWrite = Toggle("允许 PLC 控制");
    private readonly CheckBox _botAlarmButtons = Toggle("报警推送显示操作按钮");
    private readonly TextBox _botClientId = Input();
    private readonly TextBox _botSecret = Input(true);
    private readonly TextBox _botAdmins = Multiline(65);
    private readonly TextBox _botWelcome = Multiline(65);
    private readonly TextBox _botUnknown = Input();
    private readonly TextBox _botCardTemplate = Input();
    private readonly NumericUpDown _botColumns = Numeric(1, 3, 1, 0);
    private readonly ListBox _commandList = new() { Dock = DockStyle.Fill, BorderStyle = BorderStyle.None, DrawMode = DrawMode.OwnerDrawFixed, ItemHeight = 64, IntegralHeight = false, BackColor = Color.White };
    private readonly TextBox _commandName = Input();
    private readonly TextBox _commandButton = Input();
    private readonly ComboBox _commandAction = Combo();
    private readonly CheckBox _commandEnabled = Toggle("启用此动作");
    private readonly ComboBox _commandSource = Combo();
    private readonly ComboBox _commandWriteType = Combo("bit", "register");
    private readonly NumericUpDown _commandDb = Numeric(1, 65535, 1, 0);
    private readonly NumericUpDown _commandAddress = Numeric(0, 65535, 1, 0);
    private readonly NumericUpDown _commandBit = Numeric(0, 7, 1, 0);
    private readonly NumericUpDown _commandValue = Numeric(0, 65535, 1, 0);
    private readonly NumericUpDown _commandPulse = Numeric(0, 5000, 100, 0);
    private readonly TextBox _commandOpcNode = Input();
    private readonly ComboBox _commandOpcType = Combo("Boolean", "Int16", "UInt16", "Int32", "UInt32", "Int64", "UInt64", "Float", "Double", "String");
    private readonly TextBox _commandOpcValue = Input();
    private readonly TextBox _commandOpcResetValue = Input();
    private readonly TextBox _commandReply = Multiline(95);
    private readonly BotPreview _botPreview = new() { Height = 350 };
    private List<BotCommandDefinition> _botDefinitions = [];
    private int _commandIndex = -1;
    private bool _loadingBot;
    private string? _legacyBotError;

    private Panel BuildBotPage(bool embedded = false)
    {
        var (page, content) = ScrollablePage();
        if (!embedded) AddBlock(content, PageBanner("CONVERSATIONAL CONTROL", "让机器人真正参与现场处置", "统一配置菜单、状态查询、历史报警和需要二次确认的 PLC 控制。", "04", Color.FromArgb(139, 92, 246)));
        var top = new SurfacePanel { Height = 112, BackColor = Color.FromArgb(252, 253, 255) };
        top.Controls.Add(new Label { Text = "交互服务", AutoSize = true, Location = new Point(26, 18), ForeColor = Color.FromArgb(35, 48, 75), Font = new Font("Microsoft YaHei UI", 10, FontStyle.Bold) });
        top.Controls.Add(new Label { Text = "选择通知目标并控制双向交互服务", AutoSize = true, Location = new Point(26, 44), ForeColor = VisualTheme.Muted, Font = new Font("Microsoft YaHei UI", 8) });
        _botTarget.Width = 360;
        var targetShell = new InputShell(_botTarget) { Location = new Point(26, 64), Width = 390, Visible = !embedded };
        _botCurrentTarget.Location = new Point(28, 69);
        _botCurrentTarget.Visible = embedded;
        _botEnabled.Location = new Point(440, 64);
        var service = ActionButton("启动 / 停止服务", true);
        service.Location = new Point(650, 65);
        service.Click += async (_, _) => await ToggleMonitorAsync();
        top.Controls.AddRange([targetShell, _botCurrentTarget, _botEnabled, service]);
        AddBlock(content, top);
        if (!embedded) AddBlock(content, HintCard("机器人配置说明", "Telegram：通知页填写机器人令牌，交互页填写管理员数字 ID。企业微信：填写智能机器人标识和密钥。钉钉：填写应用标识和密钥；使用按钮还需互动卡片模板 ID。"));
        _botTarget.SelectedIndexChanged += (_, _) =>
        {
            if (_loadingBot || _refreshingSelectors || _botTarget.SelectedIndex < 0 || _botTarget.SelectedIndex == _targetIndex) return;
            var index = _botTarget.SelectedIndex;
            if (!SaveFromUi(false)) { LoadBotToUi(); return; }
            _targetIndex = index; LoadConfigToUi();
        };
        var sections = new TabControl { Height = 1080, Dock = DockStyle.Top, Font = new Font("Microsoft YaHei UI", 10, FontStyle.Bold), Padding = new Point(20, 7) };
        var settingsTab = new TabPage("连接与对话") { BackColor = AppBackground, AutoScroll = true };
        var actionsTab = new TabPage("按钮与PLC动作") { BackColor = AppBackground, AutoScroll = true };
        var settingsContent = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1, Padding = new Padding(14), BackColor = AppBackground };
        settingsContent.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var actionsContent = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1, Padding = new Padding(14), BackColor = AppBackground };
        actionsContent.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        settingsTab.Controls.Add(settingsContent); actionsTab.Controls.Add(actionsContent); sections.TabPages.AddRange([settingsTab, actionsTab]);
        AddBlock(settingsContent, FormCard("连接与权限", "按所选平台填写连接凭据，并设置允许操作机器人的用户。", [
            ("机器人标识 / 应用标识", (Control)_botClientId), ("机器人密钥", _botSecret),
            ("允许交互的用户 ID（每行一个）", _botAdmins), ("PLC 写入权限", _botAllowWrite),
            ("报警消息按钮", _botAlarmButtons),
            ("钉钉互动卡片模板标识", _botCardTemplate), ("Telegram 按钮列数", _botColumns)
        ]));
        foreach (var (key, label) in new[] { ("reply", "发送自定义回复"), ("status", "查询运行状态"), ("history", "查看历史报警"), ("menu", "显示菜单"), ("link", "打开链接"), ("plc_write", "写入 PLC") }) _commandAction.Items.Add(new Option(key, label));
        var workspace = new TableLayoutPanel { Height = 970, ColumnCount = 2, BackColor = Color.Transparent };
        workspace.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 256)); workspace.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var library = Surface("02  交互动作", "每个按钮对应一条可配置命令", 970);
        library.Dock = DockStyle.Fill; library.Margin = new Padding(0, 0, 18, 0);
        var commandHost = new Panel { Location = new Point(16, 112), Size = new Size(218, 530), Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom };
        commandHost.Controls.Add(_commandList); library.Controls.Add(commandHost);
        var actions = new FlowLayoutPanel { Location = new Point(14, 64), AutoSize = true };
        var add = SmallButton("＋ 新增"); add.MinimumSize = new Size(80, 36);
        var remove = SmallButton("删除"); remove.MinimumSize = new Size(70, 36);
        add.Click += (_, _) => { CaptureCommand(); _botDefinitions.Add(new BotCommandDefinition { Command = "/action_" + (_botDefinitions.Count + 1), Button = "新动作", Reply = "请输入回复内容" }); _commandIndex = _botDefinitions.Count - 1; RefreshCommandList(); LoadCommand(); };
        remove.Click += (_, _) => { if (_commandIndex < 0) return; _botDefinitions.RemoveAt(_commandIndex); _commandIndex = Math.Min(_commandIndex, _botDefinitions.Count - 1); RefreshCommandList(); LoadCommand(); };
        actions.Controls.AddRange([add, remove]); library.Controls.Add(actions);
        var examples = SmallButton("添加示例菜单"); examples.Location = new Point(18, 910); examples.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
        examples.Click += (_, _) =>
        {
            CaptureCommand();
            foreach (var item in new[] { new BotCommandDefinition { Command = "/status", Button = "设备状态", Action = "status", Reply = "{status}" }, new BotCommandDefinition { Command = "/history", Button = "历史报警", Action = "history", Reply = "" }, new BotCommandDefinition { Command = "/help", Button = "联系值班员", Reply = "请联系值班室：请在这里填写联系方式。" }, new BotCommandDefinition { Command = "/menu", Button = "返回菜单", Action = "menu", Reply = "请选择需要的操作。" } })
                if (!_botDefinitions.Any(x => x.Command == item.Command)) _botDefinitions.Add(item);
            _legacyBotError = null; _commandIndex = 0; RefreshCommandList(); LoadCommand();
        };
        library.Controls.Add(examples);
        var editor = FormCard("动作编辑器", "修改后点击保存。PLC 控制始终先展示地址并要求一次性确认。", [
            ("命令，例如 /status", (Control)_commandName), ("按钮文字", _commandButton), ("执行动作", _commandAction), ("状态", _commandEnabled),
            ("目标设备", _commandSource), ("写入类型", _commandWriteType), ("S7 DB 编号", _commandDb), ("字节 / 寄存器地址", _commandAddress),
            ("S7 位编号", _commandBit), ("S7 / Modbus 写入值", _commandValue),
            ("OPC UA 写入节点 ID", _commandOpcNode), ("OPC UA 数据类型", _commandOpcType),
            ("OPC UA 写入值", _commandOpcValue), ("OPC UA 脉冲恢复值", _commandOpcResetValue),
            ("脉冲毫秒（0 为保持）", _commandPulse), ("回复 / 成功提示 / 链接", _commandReply)
        ]);
        editor.Dock = DockStyle.Fill;
        workspace.Controls.Add(library, 0, 0); workspace.Controls.Add(editor, 1, 0); AddBlock(actionsContent, workspace);
        _commandList.DrawItem += DrawCommand;
        _commandList.SelectedIndexChanged += (_, _) => { if (_loadingBot) return; CaptureCommand(); _commandIndex = _commandList.SelectedIndex; LoadCommand(); };
        _commandAction.SelectedIndexChanged += (_, _) => UpdatePlcFields();
        _commandSource.SelectedIndexChanged += (_, _) => UpdatePlcFields();
        _commandWriteType.SelectedIndexChanged += (_, _) => UpdatePlcFields();
        AddBlock(settingsContent, FormCard("对话内容", "回复可用 {user}、{args}、{time}、{status}。", [("欢迎消息", (Control)_botWelcome), ("未知命令回复", _botUnknown)]));
        var preview = ActionButton("预览当前配置", false);
        preview.Click += (_, _) => { CaptureCommand(); UpdateBotPreview(); };
        AddBlock(settingsContent, preview); AddBlock(settingsContent, _botPreview);
        AddBlock(content, sections);
        return page;
    }

    private void LoadBotToUi()
    {
        _loadingBot = true;
        try
        {
            var target = _config.Targets[_targetIndex];
            _botCurrentTarget.Text = $"当前目标：{target.Name}  ·  {ChannelLabel(target.Channel)}";
            Fill(_botTarget, _config.Targets.Select(x => new Option(x.Id, x.Name + " · " + ChannelLabel(x.Channel))), _targetIndex);
            _botEnabled.Checked = BotInteraction.Enabled(target); _botAllowWrite.Checked = target.Params.Get("allow_plc_write") == "是";
            _botAlarmButtons.Checked = BotInteraction.ShowButtonsOnAlarm(target);
            _botClientId.Text = target.Params.Get("bot_client_id"); _botSecret.Text = target.Params.Get("bot_client_secret");
            _botAdmins.Text = target.Params.Get("admin_ids"); _botCardTemplate.Text = target.Params.Get("card_template_id");
            _botWelcome.Text = target.Params.Get("welcome_text", "你好，{user}。请选择下方功能。");
            _botUnknown.Text = target.Params.Get("unknown_reply", "暂不支持该指令，请选择菜单。");
            _botColumns.Value = Math.Clamp(target.Params.GetInt("button_columns", 2), 1, 3);
            _legacyBotError = null;
            try { _botDefinitions = BotInteraction.Commands(target); } catch (InvalidOperationException ex) { _botDefinitions = []; _legacyBotError = ex.Message; }
            Fill(_commandSource, _config.Sources.Select(x => new Option(x.Id, $"{x.Name} · {ProtocolLabel(x.Protocol)}")), 0);
            _commandIndex = _botDefinitions.Count > 0 ? 0 : -1;
            RefreshCommandList(); LoadCommand();
        }
        finally { _loadingBot = false; }
        UpdateBotPreview();
    }

    private void SaveBotFields(NotificationTarget target)
    {
        CaptureCommand();
        target.Params["receive_enabled"] = JsonValues.Element(_botEnabled.Checked ? "是" : "否");
        target.Params["allow_plc_write"] = JsonValues.Element(_botAllowWrite.Checked ? "是" : "否");
        target.Params["alarm_show_buttons"] = JsonValues.Element(_botAlarmButtons.Checked ? "是" : "否");
        target.Params["admin_ids"] = JsonValues.Element(_botAdmins.Text.Trim());
        target.Params["bot_client_id"] = JsonValues.Element(_botClientId.Text.Trim());
        target.Params["bot_client_secret"] = JsonValues.Element(_botSecret.Text.Trim());
        target.Params["card_template_id"] = JsonValues.Element(_botCardTemplate.Text.Trim());
        target.Params["welcome_text"] = JsonValues.Element(_botWelcome.Text);
        target.Params["unknown_reply"] = JsonValues.Element(_botUnknown.Text);
        target.Params["button_columns"] = JsonValues.Element(_botColumns.Value.ToString());
        if (_legacyBotError is null) target.Params["command_defs"] = JsonValues.Element(BotInteraction.Serialize(_botDefinitions));
    }

    private void CaptureCommand()
    {
        if (_loadingBot || _commandIndex < 0 || _commandIndex >= _botDefinitions.Count) return;
        _botDefinitions[_commandIndex] = new BotCommandDefinition
        {
            Command = _commandName.Text.Trim(), Button = _commandButton.Text.Trim(), Action = SelectedKey(_commandAction), Enabled = _commandEnabled.Checked,
            Reply = _commandReply.Text, SourceId = SelectedKey(_commandSource), WriteType = _commandWriteType.Text,
            DbNumber = (int)_commandDb.Value, Address = (int)_commandAddress.Value, Bit = (int)_commandBit.Value, Value = (int)_commandValue.Value, PulseMs = (int)_commandPulse.Value,
            OpcNodeId = _commandOpcNode.Text.Trim(), OpcDataType = _commandOpcType.Text, OpcValue = _commandOpcValue.Text.Trim(), OpcResetValue = _commandOpcResetValue.Text.Trim()
        };
        _commandList.Invalidate();
    }

    private void LoadCommand()
    {
        var wasLoading = _loadingBot; _loadingBot = true;
        var item = _commandIndex >= 0 ? _botDefinitions[_commandIndex] : new BotCommandDefinition { Command = "", Button = "", Reply = "" };
        _commandName.Text = item.Command; _commandButton.Text = item.Button; SelectKey(_commandAction, item.Action);
        _commandEnabled.Checked = item.Enabled; _commandReply.Text = item.Reply; SelectKey(_commandSource, item.SourceId);
        _commandWriteType.SelectedItem = item.WriteType; _commandDb.Value = Math.Clamp(item.DbNumber, 1, 65535);
        _commandAddress.Value = Math.Clamp(item.Address, 0, 65535); _commandBit.Value = Math.Clamp(item.Bit, 0, 7);
        _commandValue.Value = Math.Clamp(item.Value, 0, 65535); _commandPulse.Value = Math.Clamp(item.PulseMs, 0, 5000);
        _commandOpcNode.Text = item.OpcNodeId; _commandOpcType.SelectedItem = item.OpcDataType;
        if (_commandOpcType.SelectedIndex < 0) _commandOpcType.SelectedItem = "Boolean";
        _commandOpcValue.Text = item.OpcValue; _commandOpcResetValue.Text = item.OpcResetValue;
        _loadingBot = wasLoading; UpdatePlcFields();
    }
    private void UpdatePlcFields()
    {
        var enabled = SelectedKey(_commandAction) == "plc_write";
        var source = _config.Sources.FirstOrDefault(x => x.Id == SelectedKey(_commandSource));
        var opc = enabled && source?.Protocol == "opcua";
        var s7 = enabled && source?.Protocol == "s7";
        var modbus = enabled && source?.Protocol is "modbus_tcp" or "modbus_rtu";
        _commandSource.Enabled = enabled;
        _commandWriteType.Enabled = modbus;
        _commandDb.Enabled = s7; _commandBit.Enabled = s7;
        _commandAddress.Enabled = s7 || modbus; _commandValue.Enabled = s7 || modbus;
        _commandOpcNode.Enabled = opc; _commandOpcType.Enabled = opc; _commandOpcValue.Enabled = opc; _commandOpcResetValue.Enabled = opc;
        _commandPulse.Enabled = s7 || opc || (modbus && _commandWriteType.Text == "bit");
    }
    private void RefreshCommandList()
    {
        var old = _loadingBot; _loadingBot = true;
        _commandList.Items.Clear(); for (var i = 0; i < _botDefinitions.Count; i++) _commandList.Items.Add(i);
        _commandList.SelectedIndex = _commandIndex; _loadingBot = old;
    }
    private void DrawCommand(object? sender, DrawItemEventArgs e)
    {
        if (e.Index < 0 || e.Index >= _botDefinitions.Count) return;
        var item = _botDefinitions[e.Index]; var selected = e.Index == _commandList.SelectedIndex;
        using var brush = new SolidBrush(selected ? Color.FromArgb(240, 236, 255) : Color.White); e.Graphics.FillRectangle(brush, e.Bounds);
        TextRenderer.DrawText(e.Graphics, item.Button.Length == 0 ? "未命名动作" : item.Button, new Font("Microsoft YaHei UI", 10, FontStyle.Bold), new Rectangle(e.Bounds.X + 14, e.Bounds.Y + 8, e.Bounds.Width - 24, 24), selected ? PrimaryColor : VisualTheme.Ink, TextFormatFlags.EndEllipsis);
        TextRenderer.DrawText(e.Graphics, item.Command + (item.Action == "plc_write" ? "  ·  PLC" : ""), Font, new Rectangle(e.Bounds.X + 14, e.Bounds.Y + 34, e.Bounds.Width - 24, 22), VisualTheme.Muted, TextFormatFlags.EndEllipsis);
    }
    private void UpdateBotPreview()
    {
        var text = _legacyBotError ?? (_commandIndex >= 0 ? _botDefinitions[_commandIndex].Reply : _botWelcome.Text);
        _botPreview.SetPreview(BotInteraction.Render(text, "值班员", "示例参数", "运行正常 · 3 台设备在线"), _botDefinitions.Where(x => x.Enabled).Select(x => x.Button).Where(x => x.Length > 0).Take(6).ToArray());
    }
}

public sealed class BotPreview : SurfacePanel
{
    private readonly Label _message = new() { AutoSize = false, BackColor = Color.White, ForeColor = VisualTheme.Ink, Font = new Font("Microsoft YaHei UI", 10), Padding = new Padding(18), Location = new Point(30, 60), Size = new Size(530, 125) };
    private readonly FlowLayoutPanel _buttons = new() { Location = new Point(30, 190), Size = new Size(620, 105), WrapContents = true, BackColor = Color.Transparent };
    public BotPreview()
    {
        BackColor = Color.FromArgb(233, 239, 248);
        Controls.Add(new Label { Text = "对话预览   /   本地模拟，不发送消息或写入 PLC", AutoSize = true, ForeColor = VisualTheme.Muted, Location = new Point(30, 23), Font = new Font("Microsoft YaHei UI", 9) });
        Controls.AddRange([_message, _buttons]);
        Resize += (_, _) => { _message.Width = Math.Max(200, Width - 60); _buttons.Width = Math.Max(200, Width - 60); };
    }
    public void SetPreview(string message, string[] buttons)
    {
        _message.Text = message;
        foreach (Control control in _buttons.Controls.Cast<Control>().ToArray()) control.Dispose();
        foreach (var name in buttons)
        {
            var button = new ModernButton { Text = name, Width = 190, Height = 40, BackColor = Color.White, ForeColor = VisualTheme.Accent, Margin = new Padding(0, 0, 10, 10) };
            button.FlatAppearance.BorderColor = Color.FromArgb(210, 218, 234);
            button.Click += (_, _) => _message.Text = $"已点击「{name}」\n这是本地外观预览，未执行任何设备操作。";
            _buttons.Controls.Add(button);
        }
    }
}
