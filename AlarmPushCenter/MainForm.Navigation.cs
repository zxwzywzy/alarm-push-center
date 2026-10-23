namespace AlarmPushCenter;

public sealed partial class MainForm
{
    private Control BuildNavigationHeader()
    {
        var bar = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, Margin = Padding.Empty,
            Padding = new Padding(26, 0, 24, 0), BackColor = Color.White };
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 236));
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 128));
        var brand = new Panel { Dock = DockStyle.Fill, BackColor = Color.White };
        var logo = new PictureBox { Image = LoadProductImage(), SizeMode = PictureBoxSizeMode.Zoom,
            Location = new Point(0, 18), Size = new Size(36, 36), AccessibleName = "应用图标" };
        logo.Disposed += (_, _) => logo.Image?.Dispose();
        brand.Controls.Add(logo);
        brand.Controls.Add(new Label { Text = "报警推送中心", AutoSize = true, ForeColor = VisualTheme.Ink,
            Font = new Font("Microsoft YaHei UI", 12, FontStyle.Bold), Location = new Point(48, 14) });
        brand.Controls.Add(new Label { Text = "设备 · 报警 · 通知", AutoSize = true, ForeColor = VisualTheme.Muted,
            Font = new Font("Microsoft YaHei UI", 8), Location = new Point(49, 42) });
        var navigation = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false,
            FlowDirection = FlowDirection.LeftToRight, Padding = new Padding(0, 2, 0, 0), BackColor = Color.White };
        AddNavigation(navigation, "运行总览", 0);
        AddNavigation(navigation, "设备工作区", 1);
        AddNavigation(navigation, "报警规则", 2);
        AddNavigation(navigation, "通知与交互", 3);
        AddNavigation(navigation, "运行记录", 4);
        _updateButton.Anchor = AnchorStyles.None;
        bar.Controls.Add(brand, 0, 0); bar.Controls.Add(navigation, 1, 0); bar.Controls.Add(_updateButton, 2, 0);
        bar.Paint += (_, e) => { using var line = new Pen(Color.FromArgb(223, 229, 223)); e.Graphics.DrawLine(line, 0, bar.Height - 1, bar.Width, bar.Height - 1); };
        return bar;
    }
}
