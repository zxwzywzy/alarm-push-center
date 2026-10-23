namespace AlarmPushCenter;

public sealed partial class MainForm
{
    private readonly RuntimeTelemetry _telemetry = new();
    private readonly TelemetryTrendControl _alarmTrend = new() { ChartTitle = "报警与连接趋势" };
    private readonly TelemetryTrendControl _resourceTrend = new() { ChartTitle = "采集耗时与程序资源", ResourceChart = true };
    private readonly ComboBox _trendRange = Combo("最近 5 分钟", "最近 15 分钟", "最近 1 小时");
    private readonly Label _telemetrySummary = new() { Dock = DockStyle.Fill, ForeColor = VisualTheme.Muted,
        BackColor = Color.FromArgb(246, 248, 244), Padding = new Padding(18, 10, 10, 0), Font = new Font("Microsoft YaHei UI", 9) };
    private DateTime _lastTelemetryPoint;

    private Control BuildTelemetryPanel()
    {
        var panel = new TableLayoutPanel { Height = 330, ColumnCount = 2, RowCount = 3, BackColor = Color.White,
            Margin = new Padding(0, 0, 0, 18), Padding = new Padding(6), AccessibleName = "运行趋势与程序资源" };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 82));
        var heading = new Panel { Dock = DockStyle.Fill, BackColor = panel.BackColor };
        heading.Controls.Add(new Label { AutoSize = true, Text = "实时采样 · 每 5 秒 · 最多保留 1 小时", ForeColor = VisualTheme.Muted, Location = new Point(18, 12) });
        _trendRange.Width = 155; _trendRange.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        heading.Controls.Add(_trendRange); heading.Resize += (_, _) => _trendRange.Location = new Point(Math.Max(300, heading.Width - 173), 4);
        _trendRange.SelectedIndex = 1;
        _trendRange.SelectedIndexChanged += (_, _) => RefreshTelemetry(true);
        panel.Controls.Add(heading, 0, 0); panel.SetColumnSpan(heading, 2);
        panel.Controls.Add(_alarmTrend, 0, 1); panel.Controls.Add(_resourceTrend, 1, 1);
        panel.Controls.Add(_telemetrySummary, 0, 2); panel.SetColumnSpan(_telemetrySummary, 2);
        return panel;
    }
    private void RefreshTelemetry(bool force = false)
    {
        if (IsDisposed) return;
        RefreshMetrics();
        _telemetry.Sample(_monitor, _config.Sources.Count(x => x.Enabled));
        var points = _telemetry.Read(_trendRange.SelectedIndex switch { 0 => 5, 2 => 60, _ => 15 });
        var latest = points.LastOrDefault();
        if (latest is null || (!force && latest.Time == _lastTelemetryPoint)) return;
        _lastTelemetryPoint = latest.Time;
        _alarmTrend.Points = points; _resourceTrend.Points = points;
        _alarmTrend.Invalidate(); _resourceTrend.Invalidate();
        _telemetrySummary.Text = $"连接 {latest.Connected}/{latest.Enabled} 台    激活报警 {latest.ActiveAlarms}    采集 {latest.ReadsPerSecond:0.##} 次/秒    平均耗时 {latest.AverageReadMs:0.##} ms\n" +
            $"程序 CPU {latest.CpuPercent:0.##}%    程序内存 {latest.MemoryMb:0.#} MB    待发送 {latest.QueueDepth}    本周期投递失败 {latest.FailedDeliveries}\n" +
            "报警按规则实际触发计数；CPU 按整机算力归一化。程序退出后清空趋势。";
        _alarmTrend.AccessibleDescription = $"当前激活报警 {latest.ActiveAlarms}，连接 {latest.Connected}/{latest.Enabled} 台。";
        _resourceTrend.AccessibleDescription = $"平均采集耗时 {latest.AverageReadMs} 毫秒，CPU {latest.CpuPercent}%，内存 {latest.MemoryMb} MB。";
    }
}
