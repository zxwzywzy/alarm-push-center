namespace AlarmPushCenter;

public sealed partial class MainForm
{
    private void ShowDisclaimer()
    {
        using var dialog = new Form { Text = "免责声明与使用风险", StartPosition = FormStartPosition.CenterParent,
            Size = new Size(780, 660), MinimumSize = new Size(540, 420), Font = Font, ShowInTaskbar = false, Icon = Icon };
        var text = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill,
            Text = ProductDisclaimer.Text, Font = new Font("Microsoft YaHei UI", 10), BackColor = Color.White, BorderStyle = BorderStyle.None };
        var close = new Button { Text = "关闭", Dock = DockStyle.Bottom, Height = 44, DialogResult = DialogResult.Cancel };
        dialog.Padding = new Padding(20); dialog.Controls.Add(text); dialog.Controls.Add(close); dialog.CancelButton = close;
        dialog.ShowDialog(this);
    }

    private static Icon LoadProductIcon()
    {
        using var stream = typeof(MainForm).Assembly.GetManifestResourceStream("AlarmPushCenter.AppIcon.ico")!;
        using var icon = new Icon(stream);
        return (Icon)icon.Clone();
    }
    private static Image LoadProductImage()
    {
        using var stream = typeof(MainForm).Assembly.GetManifestResourceStream("AlarmPushCenter.AppIcon.png")!;
        using var image = Image.FromStream(stream);
        return new Bitmap(image);
    }
}
