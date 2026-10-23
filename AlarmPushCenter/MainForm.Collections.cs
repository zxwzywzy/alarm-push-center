namespace AlarmPushCenter;

public sealed partial class MainForm
{
    private readonly ComboBox _sourceSelector = Combo();
    private readonly ComboBox _ruleSelector = Combo();
    private readonly ComboBox _targetSelector = Combo();
    private readonly ComboBox _ruleSource = Combo();
    private int _sourceIndex, _ruleIndex, _targetIndex;
    private bool _refreshingSelectors, _closing, _closeReady;

    private Control BuildCollectionToolbar(string kind)
    {
        var selector = Selector(kind);
        selector.Width = 340;
        var add = ActionButton("＋  新增", true);
        var delete = ActionButton("删除当前项", false);
        delete.ForeColor = Color.FromArgb(190, 52, 67);
        delete.FlatAppearance.BorderColor = Color.FromArgb(248, 190, 196);
        var card = new SurfacePanel { Height = 80, BackColor = Color.White };
        var kindName = kind switch { "source" => "采集设备", "rule" => "报警规则", _ => "通知目标" };
        card.Controls.Add(new Label { Text = $"当前{kindName}", AutoSize = true, Location = new Point(20, 12), ForeColor = Color.FromArgb(35, 48, 75), Font = new Font("Microsoft YaHei UI", 10, FontStyle.Bold) });
        card.Controls.Add(new Label { Text = "切换时保存当前编辑", AutoSize = true, Location = new Point(20, 42), ForeColor = VisualTheme.Muted, Font = new Font("Microsoft YaHei UI", 8) });
        var shell = new InputShell(selector) { Location = new Point(190, 20), Width = 340 };
        add.Location = new Point(544, 21);
        delete.Location = new Point(670, 21);
        card.Resize += (_, _) =>
        {
            shell.Width = Math.Clamp(card.ClientSize.Width - 470, 200, 340);
            add.Left = shell.Right + 14;
            delete.Left = add.Right + 10;
        };
        card.Controls.AddRange([shell, add, delete]);
        selector.SelectedIndexChanged += (_, _) =>
        {
            if (_refreshingSelectors || selector.SelectedIndex < 0) return;
            var index = selector.SelectedIndex;
            if (index == Index(kind)) return;
            if (!SaveFromUi(false)) { RefreshSelectors(); return; }
            SetIndex(kind, index);
            LoadConfigToUi();
        };
        add.Click += (_, _) =>
        {
            if (!SaveFromUi(false)) return;
            switch (kind)
            {
                case "source":
                    var source = ConfigDefaults.NewSource();
                    source.Name = $"设备 {_config.Sources.Count + 1}";
                    _config.Sources.Add(source); _sourceIndex = _config.Sources.Count - 1;
                    break;
                case "rule":
                    var rule = ConfigDefaults.NewRule(_config.Sources[_sourceIndex].Id);
                    rule.Name = $"规则 {_config.Rules.Count + 1}";
                    _config.Rules.Add(rule); _ruleIndex = _config.Rules.Count - 1;
                    break;
                case "target":
                    var target = ConfigDefaults.NewTarget();
                    target.Name = $"通知目标 {_config.Targets.Count + 1}";
                    _config.Targets.Add(target); _targetIndex = _config.Targets.Count - 1;
                    break;
            }
            LoadConfigToUi();
        };
        delete.Click += (_, _) =>
        {
            if (!SaveFromUi(false)) return;
            var count = kind switch { "source" => _config.Sources.Count, "rule" => _config.Rules.Count, _ => _config.Targets.Count };
            if (count <= 1) { MessageBox.Show(this, "至少保留一项；不使用时可取消启用。"); return; }
            if (kind == "source" && _config.Rules.Any(x => x.SourceId == _config.Sources[_sourceIndex].Id))
            {
                MessageBox.Show(this, "请先删除或重新关联该设备的报警规则。"); return;
            }
            switch (kind)
            {
                case "source": _config.Sources.RemoveAt(_sourceIndex); break;
                case "rule": _config.Rules.RemoveAt(_ruleIndex); break;
                case "target": _config.Targets.RemoveAt(_targetIndex); break;
            }
            SetIndex(kind, Math.Min(Index(kind), count - 2));
            LoadConfigToUi();
        };
        return card;
    }

    private ComboBox Selector(string kind) => kind switch { "source" => _sourceSelector, "rule" => _ruleSelector, _ => _targetSelector };
    private int Index(string kind) => kind switch { "source" => _sourceIndex, "rule" => _ruleIndex, _ => _targetIndex };
    private void SetIndex(string kind, int index)
    {
        switch (kind) { case "source": _sourceIndex = index; break; case "rule": _ruleIndex = index; break; default: _targetIndex = index; break; }
    }

    private void RefreshSelectors()
    {
        _refreshingSelectors = true;
        try
        {
            Fill(_sourceSelector, _config.Sources.Select(x => new Option(x.Id, x.Name)), _sourceIndex);
            Fill(_ruleSelector, _config.Rules.Select(x => new Option(x.Id, x.Name)), _ruleIndex);
            Fill(_targetSelector, _config.Targets.Select(x => new Option(x.Id, x.Name)), _targetIndex);
            Fill(_ruleSource, _config.Sources.Select(x => new Option(x.Id, x.Name)), 0);
        }
        finally { _refreshingSelectors = false; }
    }

    private static void Fill(ComboBox combo, IEnumerable<Option> options, int index)
    {
        combo.Items.Clear();
        combo.Items.AddRange(options.Cast<object>().ToArray());
        combo.SelectedIndex = index;
    }

    private async void OnClosingAsync(object? sender, FormClosingEventArgs e)
    {
        if (_closeReady) return;
        e.Cancel = true;
        if (_closing) return;
        _closing = true;
        Enabled = false;
        await Task.Yield();
        try
        {
            _phoneLifetime.Cancel();
            await StopPhoneMonitorAsync();
            if (_botRuntime is not null) { await _botRuntime.DisposeAsync(); _botRuntime = null; }
            if (_monitor is not null) await _monitor.StopAsync();
        }
        catch (Exception ex) { Log(new RuntimeEvent("error", $"停止监控失败：{ex.Message}")); }
        finally { _monitor = null; _closeReady = true; Close(); }
    }
}
