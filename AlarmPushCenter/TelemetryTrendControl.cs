using System.Drawing.Drawing2D;
using System.ComponentModel;

namespace AlarmPushCenter;

public sealed class TelemetryTrendControl : Control
{
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string ChartTitle { get; set; } = "";
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool ResourceChart { get; set; }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public IReadOnlyList<TelemetryPoint> Points { get; set; } = [];
    public TelemetryTrendControl()
    {
        DoubleBuffered = true;
        BackColor = Color.White;
        ForeColor = VisualTheme.Ink;
        Dock = DockStyle.Fill;
        AccessibleRole = AccessibleRole.Chart;
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
        using var titleFont = new Font("Microsoft YaHei UI", 11, FontStyle.Bold);
        using var font = new Font("Microsoft YaHei UI", 8);
        using var text = new SolidBrush(ForeColor);
        g.DrawString(ChartTitle, titleFont, text, 18, 12);
        var cyan = VisualTheme.Accent; var purple = Color.FromArgb(125, 130, 115); var green = Color.FromArgb(61, 139, 163);
        var legendX = 18f;
        void Legend(string label, Color color)
        {
            using var brush = new SolidBrush(color);
            g.FillEllipse(brush, legendX, 44, 7, 7);
            g.DrawString(label, font, text, legendX + 12, 40);
            legendX += g.MeasureString(label, font).Width + 35;
        }
        Legend(ResourceChart ? "采集耗时 ms（左轴）" : "新增报警", cyan);
        if (!ResourceChart) Legend("恢复事件", green);
        Legend(ResourceChart ? "程序 CPU %（右轴）" : "在线设备（右轴）", purple);
        if (Points.Count < 2)
        {
            g.DrawString("正在积累真实数据，约 5 秒后显示趋势。", font, text, 60, 130);
            return;
        }
        var area = new RectangleF(57, 72, Math.Max(10, Width - 120), Math.Max(10, Height - 109));
        var maxLeft = Math.Max(1, Points.Max(p => ResourceChart ? p.AverageReadMs : Math.Max(p.NewAlarms, p.Recoveries))) * 1.15;
        var maxRight = ResourceChart ? 100 : Math.Max(1, Points.Max(p => p.Enabled));
        using var grid = new Pen(Color.FromArgb(226, 233, 224));
        for (var i = 0; i <= 4; i++)
        {
            var y = area.Top + area.Height * i / 4;
            g.DrawLine(grid, area.Left, y, area.Right, y);
            g.DrawString((maxLeft * (1 - i / 4d)).ToString(maxLeft < 10 ? "0.0" : "0"), font, text, 4, y - 6);
            using var axis = new SolidBrush(purple);
            g.DrawString((maxRight * (1 - i / 4d)).ToString("0.#"), font, axis, area.Right + 8, y - 6);
        }
        var first = Points[0].Time; var last = Points[^1].Time;
        var seconds = Math.Max(.001, (last - first).TotalSeconds);
        for (var i = 0; i <= 4; i++)
        {
            var time = first.AddSeconds(seconds * i / 4).ToLocalTime().ToString("HH:mm:ss");
            var size = g.MeasureString(time, font);
            g.DrawString(time, font, text, area.Left + area.Width * i / 4 - size.Width / 2, area.Bottom + 12);
        }
        void Draw(Func<TelemetryPoint, double> value, double maximum, Color color, bool dashed = false)
        {
            var points = Points.Select(p => new PointF(area.Left + (float)((p.Time-first).TotalSeconds / seconds) * area.Width,
                area.Bottom - (float)Math.Clamp(value(p) / maximum, 0, 1) * area.Height)).ToArray();
            using var pen = new Pen(color, 2.5f) { LineJoin = LineJoin.Round, DashStyle = dashed ? DashStyle.Dash : DashStyle.Solid };
            g.DrawLines(pen, points);
            using var brush = new SolidBrush(color);
            for (var i = 0; i < points.Length; i += Math.Max(1, points.Length / 30))
                g.FillEllipse(brush, points[i].X - 3, points[i].Y - 3, 6, 6);
        }
        Draw(p => ResourceChart ? p.AverageReadMs : p.NewAlarms, maxLeft, cyan);
        if (!ResourceChart) Draw(p => p.Recoveries, maxLeft, green);
        Draw(p => ResourceChart ? p.CpuPercent : p.Connected, maxRight, purple, true);
    }
}
