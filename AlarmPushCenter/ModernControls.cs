using System.Drawing.Drawing2D;

namespace AlarmPushCenter;

internal static class VisualTheme
{
    public static readonly Color Accent = Color.FromArgb(20, 125, 100);
    public static readonly Color Accent2 = Color.FromArgb(20, 184, 166);
    public static readonly Color Accent3 = Color.FromArgb(245, 158, 11);
    public static readonly Color Ink = Color.FromArgb(36, 53, 47);
    public static readonly Color Muted = Color.FromArgb(101, 117, 110);
    public static readonly Color Glass = Color.FromArgb(245, 248, 243);
    public static Color Canvas(Control control)
    {
        for (var parent = control.Parent; parent is not null; parent = parent.Parent)
            if (parent.BackColor.A == 255) return parent.BackColor;
        return Color.FromArgb(245, 247, 252);
    }
    public static GraphicsPath Round(RectangleF bounds, float radius)
    {
        var path = new GraphicsPath();
        var d = Math.Min(radius * 2, Math.Min(bounds.Width, bounds.Height));
        if (d <= 0) return path;
        path.AddArc(bounds.X, bounds.Y, d, d, 180, 90);
        path.AddArc(bounds.Right - d, bounds.Y, d, d, 270, 90);
        path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - d, d, d, 90, 90);
        path.CloseFigure(); return path;
    }
}

public sealed class AppCanvasPanel : Panel
{
    public AppCanvasPanel()
    {
        DoubleBuffered = true;
        BackColor = Color.FromArgb(244, 246, 250);
    }
    protected override void OnPaintBackground(PaintEventArgs e)
    {
        e.Graphics.Clear(BackColor);
    }
}

public sealed class WorkspaceNavigationButton : Button
{
    private bool _active, _hover;
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool IsActive { get => _active; set { _active = value; Invalidate(); } }
    public WorkspaceNavigationButton() { DoubleBuffered = true; BackColor = Color.White; AccessibleRole = AccessibleRole.PageTab; }
    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(_hover ? Color.FromArgb(243, 248, 243) : Color.White);
        TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle, _active ? VisualTheme.Accent : VisualTheme.Muted,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        if (_active) { using var pen = new Pen(VisualTheme.Accent, 3); e.Graphics.DrawLine(pen, 12, Height - 3, Width - 12, Height - 3); }
        if (Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(ClientRectangle, -5, -7));
    }
}

public sealed class HeaderPanel : Panel
{
    public HeaderPanel() { DoubleBuffered = true; }
    protected override void OnPaintBackground(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        e.Graphics.Clear(VisualTheme.Canvas(this));
        var rect = new RectangleF(18, 12, Width - 36, Height - 18);
        using var path = VisualTheme.Round(rect, 8);
        using var fill = new SolidBrush(Color.White);
        e.Graphics.FillPath(fill, path);
        using var border = new Pen(Color.FromArgb(232, 238, 248));
        e.Graphics.DrawPath(border, path);
    }
}

public sealed class PageBannerPanel : Panel
{
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Color AccentColor { get; set; } = VisualTheme.Accent;
    public PageBannerPanel() { DoubleBuffered = true; }
    protected override void OnPaintBackground(PaintEventArgs e)
    {
        e.Graphics.Clear(VisualTheme.Canvas(this));
        using var fill = new SolidBrush(Color.FromArgb(235, 243, 235));
        e.Graphics.FillRectangle(fill, ClientRectangle);
    }
}
public sealed class ModernComboBox : ComboBox
{
    public ModernComboBox()
    {
        DropDownStyle = ComboBoxStyle.DropDownList;
        DrawMode = DrawMode.OwnerDrawFixed;
        ItemHeight = 30;
        FlatStyle = FlatStyle.Flat;
        BackColor = Color.White;
        ForeColor = VisualTheme.Ink;
        Font = new Font("Microsoft YaHei UI", 10);
        IntegralHeight = false;
        DropDownHeight = 260;
    }
    protected override void OnDrawItem(DrawItemEventArgs e)
    {
        if (e.Index < 0) return;
        var selected = (e.State & DrawItemState.Selected) != 0;
        using var background = new SolidBrush(selected ? Color.FromArgb(224, 232, 255) : Color.White);
        e.Graphics.FillRectangle(background, e.Bounds);
        var text = GetItemText(Items[e.Index]);
        TextRenderer.DrawText(e.Graphics, text, Font, new Rectangle(e.Bounds.X + 10, e.Bounds.Y, e.Bounds.Width - 14, e.Bounds.Height), selected ? Color.FromArgb(36, 55, 110) : VisualTheme.Ink, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        if ((e.State & DrawItemState.Focus) != 0) e.DrawFocusRectangle();
    }
}

public sealed class StatusPill : Control
{
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 60 };
    private float _pulse;
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool Active { get; set; }
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public string Caption { get; set; } = "待机";
    public StatusPill()
    {
        DoubleBuffered = true;
        Size = new Size(136, 36);
        _timer.Tick += (_, _) => { _pulse = (_pulse + 0.08f) % 6.28f; Invalidate(); };
        _timer.Start();
        Disposed += (_, _) => _timer.Dispose();
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        e.Graphics.Clear(VisualTheme.Canvas(this));
        using var path = VisualTheme.Round(new RectangleF(1, 1, Width - 3, Height - 3), 18);
        using var fill = new SolidBrush(Active ? Color.FromArgb(235, 253, 247) : Color.FromArgb(246, 248, 252));
        e.Graphics.FillPath(fill, path);
        using var border = new Pen(Active ? Color.FromArgb(153, 246, 228) : Color.FromArgb(226, 232, 240));
        e.Graphics.DrawPath(border, path);
        var alpha = Active ? 120 + (int)(Math.Sin(_pulse) * 55) : 100;
        using var dot = new SolidBrush(Active ? Color.FromArgb(alpha, 16, 185, 129) : Color.FromArgb(alpha, 148, 163, 184));
        e.Graphics.FillEllipse(dot, 15, 12, 12, 12);
        TextRenderer.DrawText(e.Graphics, Caption, Font, new Rectangle(34, 0, Width - 40, Height), Active ? Color.FromArgb(5, 150, 105) : VisualTheme.Muted, TextFormatFlags.VerticalCenter | TextFormatFlags.Left);
    }
}

public sealed class ModernButton : Button
{
    private bool _hover, _pressed;
    public ModernButton()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw, true);
        FlatStyle = FlatStyle.Flat; FlatAppearance.BorderSize = 0;
        MinimumSize = new Size(110, 40); Padding = new Padding(18, 0, 18, 0); Cursor = Cursors.Hand;
    }
    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = _pressed = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { _pressed = true; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { _pressed = false; Invalidate(); base.OnMouseUp(e); }
    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        e.Graphics.Clear(VisualTheme.Canvas(this));
        var color = !Enabled ? Color.FromArgb(226, 229, 238) : _pressed ? ControlPaint.Dark(BackColor, 0.08f) : _hover ? ControlPaint.Light(BackColor, 0.12f) : BackColor;
        using var path = VisualTheme.Round(new RectangleF(1, 1, Width - 3, Height - 3), 10);
        using var brush = new SolidBrush(color); e.Graphics.FillPath(brush, path);
        using var border = new Pen(Focused ? VisualTheme.Accent : FlatAppearance.BorderColor, Focused ? 2 : 1);
        e.Graphics.DrawPath(border, path);
        TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle, Enabled ? ForeColor : VisualTheme.Muted,
            TextAlign == ContentAlignment.MiddleLeft ? TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix : TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
    }
}

public sealed class ModernSwitch : CheckBox
{
    public ModernSwitch() { AutoSize = false; Height = 40; Width = 190; Cursor = Cursors.Hand; SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true); }
    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        e.Graphics.Clear(VisualTheme.Canvas(this));
        using var path = VisualTheme.Round(new RectangleF(0, (Height - 24) / 2f, 44, 24), 12);
        using var brush = new SolidBrush(Checked ? VisualTheme.Accent : Color.FromArgb(206, 212, 225));
        e.Graphics.FillPath(brush, path);
        e.Graphics.FillEllipse(Brushes.White, Checked ? 23 : 3, (Height - 18) / 2f, 18, 18);
        TextRenderer.DrawText(e.Graphics, Text, Font, new Rectangle(55, 0, Width - 55, Height), VisualTheme.Ink, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
        if (Focused) ControlPaint.DrawFocusRectangle(e.Graphics, ClientRectangle);
    }
    protected override void OnCheckedChanged(EventArgs e) { Invalidate(); base.OnCheckedChanged(e); }
}

public sealed class InputShell : Panel
{
    private readonly Control _input;
    public InputShell(Control input)
    {
        _input = input; DoubleBuffered = true; BackColor = Color.White;
        Height = input is TextBox { Multiline: true } ? input.Height + 20 : 44;
        if (input is TextBox text) text.BorderStyle = BorderStyle.None;
        if (input is NumericUpDown number) number.BorderStyle = BorderStyle.None;
        input.Dock = DockStyle.None; input.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        Controls.Add(input);
        input.GotFocus += (_, _) => Invalidate(); input.LostFocus += (_, _) => Invalidate();
        Resize += (_, _) => Arrange(); Arrange();
    }
    private void Arrange()
    {
        _input.Width = Math.Max(20, Width - 26);
        if (_input is TextBox { Multiline: true }) _input.Height = Math.Max(20, Height - 20);
        _input.Location = new Point(13, Math.Max(7, (Height - _input.Height) / 2));
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e); e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = VisualTheme.Round(new RectangleF(1, 1, Width - 3, Height - 3), 9);
        using var fill = new SolidBrush(Color.White); e.Graphics.FillPath(fill, path);
        using var pen = new Pen(_input.ContainsFocus ? VisualTheme.Accent : Color.FromArgb(206, 217, 207), _input.ContainsFocus ? 1.8f : 1);
        e.Graphics.DrawPath(pen, path);
    }
}

public sealed class HeroPanel : Panel
{
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 45 };
    private float _phase;
    public HeroPanel()
    {
        DoubleBuffered = true; Height = 246; BackColor = VisualTheme.Accent;
        _timer.Tick += (_, _) => { _phase = (_phase + 0.025f) % 1000f; Invalidate(); };
        _timer.Start();
        Disposed += (_, _) => _timer.Dispose();
    }
    protected override void OnPaintBackground(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        e.Graphics.Clear(VisualTheme.Canvas(this));
        using var path = VisualTheme.Round(new RectangleF(0, 0, Width - 1, Height - 1), 24);
        using var brush = new LinearGradientBrush(ClientRectangle, Color.FromArgb(39, 69, 204), Color.FromArgb(13, 148, 136), 18f);
        e.Graphics.FillPath(brush, path);
        var state = e.Graphics.Save(); e.Graphics.SetClip(path);
        using var circle = new Pen(Color.FromArgb(32, Color.White), 28);
        for (var i = 0; i < 4; i++)
        {
            var offset = MathF.Sin(_phase + i) * 14;
            e.Graphics.DrawEllipse(circle, Width - 300 - i * 54 + offset, -118 + i * 25, 350 + i * 80, 350 + i * 80);
        }
        using var speck = new SolidBrush(Color.FromArgb(46, Color.White));
        for (var i = 0; i < 16; i++)
        {
            var x = 34 + i * 82 + MathF.Sin(_phase * 1.4f + i) * 8;
            var y = 24 + (i * 37 % Math.Max(80, Height - 60));
            e.Graphics.FillEllipse(speck, x, y, 3, 3);
        }
        e.Graphics.Restore(state);
    }
}
