using System.Drawing.Drawing2D;

namespace WirelessCanvas;

/// <summary>
/// Compact borderless button: 8% white fill over the background, 20% on hover, on press 28 %,
/// radius 100 (fully round ends at 32 px height). Selected is the tab state.
/// </summary>
public sealed class PillButton : Button
{
    public const double Rest = .08, Hover = .20, Pressed = .28;
    bool hover, down, selected;
    Font? bold;
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool Selected { get => selected; set { selected = value; Invalidate(); } }
    public double FillOpacity => !Enabled ? .04 : down ? Pressed : hover || selected ? Hover : Rest;

    public PillButton()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        FlatStyle = FlatStyle.Flat; FlatAppearance.BorderSize = 0; UseVisualStyleBackColor = false;
        ForeColor = Color.FromArgb(236, 239, 247); Cursor = Cursors.Hand;
        AutoSize = true; AutoSizeMode = AutoSizeMode.GrowAndShrink; Margin = new Padding(0, 4, 8, 4);
    }

    public override Size GetPreferredSize(Size proposedSize) => new(TextRenderer.MeasureText(Text, Font).Width + 30, 32);
    protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hover = down = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) { down = true; Invalidate(); } base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { down = false; Invalidate(); base.OnMouseUp(e); }
    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
    protected override void OnFontChanged(EventArgs e) { bold?.Dispose(); bold = null; base.OnFontChanged(e); }
    protected override void Dispose(bool disposing) { if (disposing) bold?.Dispose(); base.Dispose(disposing); }

    public static Color Blend(Color back, Color over, double alpha) => Color.FromArgb(
        (int)Math.Round(back.R + (over.R - back.R) * alpha), (int)Math.Round(back.G + (over.G - back.G) * alpha), (int)Math.Round(back.B + (over.B - back.B) * alpha));

    protected override void OnPaint(PaintEventArgs e)
    {
        var back = Parent?.BackColor ?? BackColor;
        e.Graphics.Clear(back);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var bounds = new RectangleF(.5f, .5f, Width - 1, Height - 1);
        float radius = Math.Min(100, Math.Min(bounds.Width, bounds.Height) / 2);
        using var path = new GraphicsPath();
        path.AddArc(bounds.Left, bounds.Top, radius * 2, radius * 2, 180, 90);
        path.AddArc(bounds.Right - radius * 2, bounds.Top, radius * 2, radius * 2, 270, 90);
        path.AddArc(bounds.Right - radius * 2, bounds.Bottom - radius * 2, radius * 2, radius * 2, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - radius * 2, radius * 2, radius * 2, 90, 90);
        path.CloseFigure();
        using var fill = new SolidBrush(Blend(back, Color.White, FillOpacity));
        e.Graphics.FillPath(fill, path);
        if (Focused && ShowFocusCues) { using var focus = new Pen(Blend(back, Color.White, .45)); e.Graphics.DrawPath(focus, path); }
        TextRenderer.DrawText(e.Graphics, Text, selected ? bold ??= new Font(Font, FontStyle.Bold) : Font, ClientRectangle,
            Enabled ? ForeColor : Blend(back, ForeColor, .45), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
    }
}

public static class Ui
{
    public static readonly Color Background = Color.FromArgb(19, 22, 30);
    public static readonly Color Text = Color.FromArgb(230, 234, 245);
    /// <summary>Card background: 5% white over the window background.</summary>
    public static readonly Color Card = PillButton.Blend(Background, Color.White, .05);
    public static PillButton Button(string text, Action? click = null, DialogResult result = DialogResult.None)
    {
        var button = new PillButton { Text = text, DialogResult = result };
        if (click != null) button.Click += (_, _) => click();
        return button;
    }
    public static void StyleGrid(DataGridView grid)
    {
        grid.BackgroundColor = Color.FromArgb(26, 30, 40); grid.BorderStyle = BorderStyle.None; grid.RowHeadersVisible = false;
        grid.DefaultCellStyle.BackColor = Color.FromArgb(32, 37, 49); grid.DefaultCellStyle.ForeColor = Text;
        grid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(64, 79, 106); grid.DefaultCellStyle.SelectionForeColor = Text;
        grid.EnableHeadersVisualStyles = false; grid.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(38, 44, 59); grid.ColumnHeadersDefaultCellStyle.ForeColor = Text;
        grid.GridColor = Color.FromArgb(44, 50, 66);
    }
    /// <summary>Dark context menu in the app colors.</summary>
    public static ContextMenuStrip Menu() => new() { BackColor = Color.FromArgb(32, 37, 49), ForeColor = Text, ShowImageMargin = false, Renderer = new ToolStripProfessionalRenderer(new DarkColors()) };
    sealed class DarkColors : ProfessionalColorTable
    {
        public override Color MenuItemSelected => Color.FromArgb(64, 79, 106);
        public override Color MenuItemBorder => Color.FromArgb(64, 79, 106);
        public override Color MenuBorder => Color.FromArgb(54, 61, 80);
        public override Color ToolStripDropDownBackground => Color.FromArgb(32, 37, 49);
        public override Color SeparatorDark => Color.FromArgb(54, 61, 80);
        public override Color SeparatorLight => Color.FromArgb(54, 61, 80);
    }
}

/// <summary>
/// Section switcher: one pill (8% white) inside which the active-section marker
/// (20% white) slides smoothly. Hover highlights a section up to 14 %.
/// </summary>
public sealed class SegmentedTabs : Control
{
    readonly List<string> items = [];
    readonly System.Windows.Forms.Timer animation = new() { Interval = 15 };
    int selected, hovered = -1;
    float indicatorX, indicatorWidth, fromX, fromWidth;
    long animationStart;
    const int Pad = 4, ItemPadding = 30, AnimationMs = 180;
    Font? bold;
    public event Action<int>? SelectedChanged;
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public IReadOnlyList<string> Items { get => items; set { items.Clear(); items.AddRange(value); SnapIndicator(); Invalidate(); } }
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public int SelectedIndex { get => selected; set => Select(value, animate: IsHandleCreated && Visible); }
    public bool Animating => animation.Enabled;
    public RectangleF Indicator => new(indicatorX, Pad, indicatorWidth, Height - Pad * 2);

    public SegmentedTabs()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        ForeColor = Color.FromArgb(236, 239, 247); Cursor = Cursors.Hand; Height = 40; Margin = new Padding(0, 2, 8, 2); TabStop = true;
        animation.Tick += (_, _) => Step();
    }

    /// <summary>Bounds of section i: width fits the text, sections touch inside the shared pill.</summary>
    public RectangleF ItemBounds(int index)
    {
        float x = Pad;
        for (int i = 0; i < items.Count; i++)
        {
            float width = TextRenderer.MeasureText(items[i], BoldFont).Width + ItemPadding;
            if (i == index) return new(x, Pad, width, Height - Pad * 2);
            x += width;
        }
        return RectangleF.Empty;
    }
    Font BoldFont => bold ??= new Font(Font, FontStyle.Bold);
    public override Size GetPreferredSize(Size proposedSize) => items.Count == 0 ? new(40, 40) : new((int)Math.Ceiling(ItemBounds(items.Count - 1).Right + Pad), 40);
    protected override void OnFontChanged(EventArgs e) { bold?.Dispose(); bold = null; base.OnFontChanged(e); SnapIndicator(); }
    protected override void OnResize(EventArgs e) { base.OnResize(e); SnapIndicator(); }

    public void Select(int index, bool animate = true)
    {
        if (index < 0 || index >= items.Count) return;
        bool changed = index != selected; selected = index;
        if (animate) { fromX = indicatorX; fromWidth = indicatorWidth; animationStart = Environment.TickCount64; animation.Start(); }
        else SnapIndicator();
        Invalidate();
        if (changed) SelectedChanged?.Invoke(index);
    }
    void SnapIndicator() { animation.Stop(); if (items.Count == 0) return; var target = ItemBounds(selected); indicatorX = target.X; indicatorWidth = target.Width; }
    void Step()
    {
        var target = ItemBounds(selected);
        double t = Math.Clamp((Environment.TickCount64 - animationStart) / (double)AnimationMs, 0, 1);
        double ease = 1 - Math.Pow(1 - t, 3); // eases out at the end
        indicatorX = (float)(fromX + (target.X - fromX) * ease); indicatorWidth = (float)(fromWidth + (target.Width - fromWidth) * ease);
        if (t >= 1) animation.Stop();
        Invalidate();
    }
    int HitTest(Point point) { for (int i = 0; i < items.Count; i++) if (ItemBounds(i).Contains(point)) return i; return -1; }
    protected override void OnMouseMove(MouseEventArgs e) { int hit = HitTest(e.Location); if (hit != hovered) { hovered = hit; Invalidate(); } base.OnMouseMove(e); }
    protected override void OnMouseLeave(EventArgs e) { hovered = -1; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left && HitTest(e.Location) is int hit and >= 0) { Focus(); Select(hit); } base.OnMouseDown(e); }
    protected override bool IsInputKey(Keys keyData) => keyData is Keys.Left or Keys.Right || base.IsInputKey(keyData);
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Left) Select(Math.Max(0, selected - 1));
        else if (e.KeyCode == Keys.Right) Select(Math.Min(items.Count - 1, selected + 1));
        base.OnKeyDown(e);
    }
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

    static System.Drawing.Drawing2D.GraphicsPath Pill(RectangleF r)
    {
        float radius = Math.Min(100, Math.Min(r.Width, r.Height) / 2), d = radius * 2;
        var path = new System.Drawing.Drawing2D.GraphicsPath();
        path.AddArc(r.Left, r.Top, d, d, 180, 90); path.AddArc(r.Right - d, r.Top, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); path.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure(); return path;
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        var back = Parent?.BackColor ?? BackColor;
        e.Graphics.Clear(back);
        e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using (var track = Pill(new RectangleF(.5f, .5f, Width - 1, Height - 1)))
        using (var fill = new SolidBrush(PillButton.Blend(back, Color.White, PillButton.Rest))) e.Graphics.FillPath(fill, track);
        var trackColor = PillButton.Blend(back, Color.White, PillButton.Rest);
        if (hovered >= 0 && hovered != selected)
            using (var hover = Pill(ItemBounds(hovered))) using (var brush = new SolidBrush(PillButton.Blend(back, Color.White, .14))) e.Graphics.FillPath(brush, hover);
        if (items.Count > 0)
            using (var marker = Pill(Indicator)) using (var brush = new SolidBrush(PillButton.Blend(back, Color.White, PillButton.Hover))) e.Graphics.FillPath(brush, marker);
        for (int i = 0; i < items.Count; i++)
            TextRenderer.DrawText(e.Graphics, items[i], i == selected ? BoldFont : Font, Rectangle.Round(ItemBounds(i)),
                i == selected ? ForeColor : PillButton.Blend(trackColor, ForeColor, .75), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
        if (Focused && ShowFocusCues) using (var focus = Pill(Indicator)) using (var pen = new Pen(PillButton.Blend(back, Color.White, .45))) e.Graphics.DrawPath(pen, focus);
    }
    protected override void Dispose(bool disposing) { if (disposing) { animation.Dispose(); bold?.Dispose(); } base.Dispose(disposing); }
}
