namespace AlarmPushCenter;

public sealed partial class MainForm
{
    private readonly ComboBox _historySource = Combo();
    private readonly DataGridView _historyGrid = CreateHistoryGrid();
    private readonly Label _historyPageLabel = new() { AutoSize = true, ForeColor = Color.FromArgb(71, 85, 105), Font = new Font("Microsoft YaHei UI", 9, FontStyle.Bold) };
    private readonly Label _historySummary = new() { AutoSize = true, ForeColor = Color.FromArgb(71, 85, 105), Font = new Font("Microsoft YaHei UI", 9) };
    private readonly Button _historyPrevious = SmallButton("上一页");
    private readonly Button _historyNext = SmallButton("下一页");
    private int _historyPage;
    private const int HistoryPageSize = 10;
    private const int HistoryMaximum = 20;

    private Panel BuildHistoryPage(bool embedded = false)
    {
        var (page, content) = ScrollablePage();
        if (!embedded) AddBlock(content, PageBanner("ALARM ARCHIVE", "历史报警清晰可查", "先选择当前设备，再查询最近 20 条记录；每页固定显示 10 条。", "06", Color.FromArgb(14, 116, 144)));

        var card = Surface("选择设备", "设备列表会标出当前连接状态。选择设备后点击查询，避免误看其他产线记录。", 138);
        _historySource.Width = 380;
        var shell = new InputShell(_historySource) { Location = new Point(24, 68), Width = 430 };
        var query = ActionButton("查询历史", true); query.Location = new Point(474, 69);
        var refresh = ActionButton("刷新设备", false); refresh.Location = new Point(604, 69);
        query.Click += (_, _) => { _historyPage = 0; RefreshHistoryGrid(true); };
        refresh.Click += (_, _) => RefreshHistorySources();
        card.Controls.AddRange([shell, query, refresh]);
        AddBlock(content, card);

        var history = Surface("报警记录", "仅展示所选设备最近 20 条；每页 10 条。", 600);
        _historySummary.Location = new Point(24, 66);
        _historyGrid.Location = new Point(24, 98);
        _historyGrid.Size = new Size(900, 400);
        _historyPrevious.Location = new Point(24, 520);
        _historyPageLabel.Location = new Point(132, 530);
        _historyNext.Location = new Point(240, 520);
        var clear = SmallButton("清理当前设备历史");
        clear.ForeColor = Color.FromArgb(190, 52, 67);
        clear.Location = new Point(370, 520);
        _historyPrevious.Click += (_, _) => { if (_historyPage > 0) { _historyPage--; RefreshHistoryGrid(); } };
        _historyNext.Click += (_, _) => { _historyPage++; RefreshHistoryGrid(); };
        clear.Click += (_, _) => ClearSelectedHistory();
        history.Resize += (_, _) => _historyGrid.Size = new Size(Math.Max(300, history.ClientSize.Width - 48), 400);
        history.Controls.AddRange([_historySummary, _historyGrid, _historyPrevious, _historyPageLabel, _historyNext, clear]);
        AddBlock(content, history);
        return page;
    }

    private void RefreshHistorySources()
    {
        var selected = (_historySource.SelectedItem as Option)?.Key;
        _historySource.Items.Clear();
        _historySource.Items.Add(new Option("", "请选择当前设备…"));
        foreach (var source in _config.Sources.Where(x => x.Enabled))
        {
            var connected = _sourceHealth.TryGetValue(source.Id, out var health) && health.Connected &&
                health.LastSuccess is DateTime last && DateTime.Now - last <= TimeSpan.FromSeconds(Math.Max(15, source.PollSeconds * 3 + 5));
            _historySource.Items.Add(new Option(source.Name, $"{(connected ? "● 已连接" : "○ 未连接")}  {source.Name} · {ProtocolLabel(source.Protocol)}"));
        }
        _historySource.SelectedIndex = 0;
        if (!string.IsNullOrWhiteSpace(selected))
            for (var i = 1; i < _historySource.Items.Count; i++)
                if ((_historySource.Items[i] as Option)?.Key == selected) { _historySource.SelectedIndex = i; break; }
        if (_historySource.Items.Count == 1) _historySummary.Text = "当前没有已启用的采集设备。";
    }

    private void RefreshHistoryGrid(bool requireSelection = false)
    {
        var sourceName = (_historySource.SelectedItem as Option)?.Key ?? "";
        if (sourceName.Length == 0)
        {
            _historyGrid.Rows.Clear();
            _historySummary.Text = "请先选择设备，再查询历史报警。";
            _historyPageLabel.Text = "第 0/0 页";
            _historyPrevious.Enabled = _historyNext.Enabled = false;
            if (requireSelection) MessageBox.Show(this, "请先从当前设备列表中选择一台设备。", "请选择设备", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        var entries = _historyStore.ReadRecent(sourceName, HistoryMaximum);
        var pageCount = Math.Max(1, (entries.Count + HistoryPageSize - 1) / HistoryPageSize);
        _historyPage = Math.Clamp(_historyPage, 0, pageCount - 1);
        _historyGrid.Rows.Clear();
        foreach (var entry in entries.Skip(_historyPage * HistoryPageSize).Take(HistoryPageSize))
            _historyGrid.Rows.Add(entry.Time.ToString("yyyy-MM-dd HH:mm:ss"), entry.State == "recovery" ? "恢复" : "报警", entry.Severity, entry.Code, entry.Text);
        _historySummary.Text = $"{sourceName} · 最近 {entries.Count} 条记录 · 每页 {HistoryPageSize} 条";
        _historyPageLabel.Text = $"第 {_historyPage + 1}/{pageCount} 页";
        _historyPrevious.Enabled = _historyPage > 0;
        _historyNext.Enabled = _historyPage + 1 < pageCount;
    }

    private BotHistoryPage GetBotHistoryPage(string sourceName, int page)
    {
        var total = _historyStore.ReadRecent(sourceName, HistoryMaximum).Count;
        var pageCount = Math.Max(1, (total + HistoryPageSize - 1) / HistoryPageSize);
        page = Math.Clamp(page, 0, pageCount - 1);
        return new BotHistoryPage(_historyStore.FormatPage(sourceName, page, HistoryPageSize, HistoryMaximum), page, pageCount, total);
    }

    private void ClearSelectedHistory()
    {
        var sourceName = (_historySource.SelectedItem as Option)?.Key ?? "";
        if (sourceName.Length == 0) { MessageBox.Show(this, "请先选择要清理历史的设备。"); return; }
        if (MessageBox.Show(this, $"准备清理设备「{sourceName}」的全部历史报警。\n\n是否继续？", "第一次确认", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        if (MessageBox.Show(this, $"此操作无法撤销。请再次确认删除「{sourceName}」的历史报警。", "第二次确认", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        var deleted = _historyStore.DeleteForSource(sourceName);
        _historyPage = 0;
        RefreshHistoryGrid();
        MessageBox.Show(this, $"已清理 {deleted} 条历史报警。", "清理完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private static DataGridView CreateHistoryGrid()
    {
        var grid = ParamsGrid();
        grid.ReadOnly = true;
        grid.AllowUserToAddRows = false;
        grid.AllowUserToDeleteRows = false;
        grid.MultiSelect = false;
        grid.Columns.Clear();
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "时间", Width = 175 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "状态", Width = 80 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "级别", Width = 95 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "报警编号", Width = 155 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "报警内容", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill });
        return grid;
    }
}
