using System.Drawing.Drawing2D;
using System.Globalization;

namespace WirelessCanvas;

public sealed class ColorPickerDialog : Form
{
    readonly HsvPickerSurface surface;
    public Color SelectedColor => surface.SelectedColor;
    public ColorPickerDialog(Color initial)
    {
        Text = L10n.T("Choose color"); ClientSize = new Size(420, 390); FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false; StartPosition = FormStartPosition.CenterParent;
        BackColor = Color.FromArgb(19, 22, 30); ForeColor = Color.WhiteSmoke; Font = new Font("Segoe UI", 10);
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(14), RowCount = 4, ColumnCount = 1 };
        root.RowStyles.Add(new(SizeType.Absolute, 26)); root.RowStyles.Add(new(SizeType.Percent, 100)); root.RowStyles.Add(new(SizeType.Absolute, 45)); root.RowStyles.Add(new(SizeType.Absolute, 40)); Controls.Add(root);
        root.Controls.Add(new Label { Text = L10n.T("Square: color. Bottom bar: hue."), AutoSize = true }, 0, 0);
        surface = new HsvPickerSurface(initial) { Dock = DockStyle.Fill }; root.Controls.Add(surface, 0, 1);
        var row = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, Margin = Padding.Empty };
        row.ColumnStyles.Add(new(SizeType.Percent, 100)); row.ColumnStyles.Add(new(SizeType.AutoSize)); row.ColumnStyles.Add(new(SizeType.Absolute, 120));
        var preview = new Panel { Dock = DockStyle.Fill, BackColor = initial, Margin = new Padding(8) };
        var hex = new TextBox { Name = "hex", Text = Hex(initial), Width = 110, MaxLength = 16, Margin = new Padding(4, 10, 4, 4), BackColor = Color.FromArgb(32, 37, 49), ForeColor = Color.WhiteSmoke, BorderStyle = BorderStyle.FixedSingle };
        row.Controls.Add(preview, 0, 0); row.Controls.Add(new Label { Text = "HEX", AutoSize = true, Margin = new Padding(4, 13, 0, 0) }, 1, 0); row.Controls.Add(hex, 2, 0);
        root.Controls.Add(row, 0, 2);
        bool syncing = false;
        surface.Changed += () =>
        {
            preview.BackColor = surface.SelectedColor;
            if (!syncing) { syncing = true; hex.Text = Hex(surface.SelectedColor); hex.ForeColor = Color.WhiteSmoke; syncing = false; }
        };
        hex.TextChanged += (_, _) =>
        {
            if (syncing) return;
            bool valid = TryParseHex(hex.Text, out var color);
            hex.ForeColor = valid ? Color.WhiteSmoke : Color.FromArgb(255, 120, 120);
            if (!valid) return;
            syncing = true; surface.SetColor(color); syncing = false;
        };
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
        var ok = Ui.Button(L10n.T("Select"), result: DialogResult.OK);
        var cancel = Ui.Button(L10n.T("Cancel"), result: DialogResult.Cancel);
        buttons.Controls.Add(ok); buttons.Controls.Add(cancel); root.Controls.Add(buttons, 0, 3); AcceptButton = ok; CancelButton = cancel;
    }
    public static string Hex(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";
    /// <summary>Accepts #RRGGBB, RRGGBB, #RGB, 0xRRGGBB; whitespace and case are ignored.</summary>
    public static bool TryParseHex(string? text, out Color color)
    {
        color = Color.Empty;
        string value = (text ?? "").Trim().Replace(" ", "");
        if (value.StartsWith('#')) value = value[1..];
        else if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) value = value[2..];
        if (value.Length == 3) value = string.Concat(value.Select(c => new string(c, 2)));
        if (value.Length != 6 || !int.TryParse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int rgb)) return false;
        color = Color.FromArgb((rgb >> 16) & 255, (rgb >> 8) & 255, rgb & 255);
        return true;
    }
}

public sealed class HsvPickerSurface : Control
{
    HsvColor hsv;
    Color? exact;
    int dragging;
    // A typed HEX value is returned without HSV rounding loss until the user moves the picker.
    public Color SelectedColor => exact ?? ColorMath.FromHsv(hsv.Hue, hsv.Saturation, hsv.Value);
    public void SetColor(Color color)
    {
        var next = ColorMath.ToHsv(color);
        hsv = next.Saturation < .00001 ? next with { Hue = hsv.Hue } : next; exact = Color.FromArgb(color.R, color.G, color.B);
        Invalidate(); Changed?.Invoke();
    }
    public event Action? Changed;
    Rectangle Square => new(8, 8, Math.Max(1, Width - 16), Math.Max(1, Height - 52));
    Rectangle Hue => new(8, Height - 30, Math.Max(1, Width - 16), 22);
    public HsvPickerSurface(Color color)
    {
        hsv = ColorMath.ToHsv(color); DoubleBuffered = true; Cursor = Cursors.Cross;
        MouseDown += (_, e) => { if (e.Button != MouseButtons.Left) return; dragging = Square.Contains(e.Location) ? 1 : Hue.Contains(e.Location) ? 2 : 0; Capture = dragging != 0; MovePointer(e.Location); };
        MouseMove += (_, e) => { if (dragging != 0) MovePointer(e.Location); };
        MouseUp += (_, _) => { dragging = 0; Capture = false; };
        MouseCaptureChanged += (_, _) => { if (!Capture) dragging = 0; };
    }
    void MovePointer(Point point)
    {
        if (dragging == 1) hsv = hsv with { Saturation = Math.Clamp((point.X - Square.Left) / (double)Square.Width, 0, 1), Value = 1 - Math.Clamp((point.Y - Square.Top) / (double)Square.Height, 0, 1) };
        if (dragging == 2) hsv = hsv with { Hue = Math.Clamp((point.X - Hue.Left) / (double)Hue.Width, 0, .999999) * 360 };
        if (dragging != 0) { exact = null; Invalidate(); Changed?.Invoke(); }
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e); var square = Square; var hue = Hue;
        using var color = new LinearGradientBrush(square, Color.White, ColorMath.FromHsv(hsv.Hue, 1, 1), LinearGradientMode.Horizontal);
        using var black = new LinearGradientBrush(square, Color.FromArgb(0, 0, 0, 0), Color.Black, LinearGradientMode.Vertical);
        e.Graphics.FillRectangle(color, square); e.Graphics.FillRectangle(black, square);
        for (int x = 0; x < hue.Width; x++) { using var pen = new Pen(ColorMath.FromHsv(x * 360.0 / hue.Width, 1, 1)); e.Graphics.DrawLine(pen, hue.Left + x, hue.Top, hue.Left + x, hue.Bottom); }
        float cx = (float)(square.Left + hsv.Saturation * square.Width), cy = (float)(square.Top + (1 - hsv.Value) * square.Height);
        using var outside = new Pen(Color.Black, 4); using var inside = new Pen(Color.White, 2);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        e.Graphics.DrawEllipse(outside, cx - 6, cy - 6, 12, 12); e.Graphics.DrawEllipse(inside, cx - 6, cy - 6, 12, 12);
        float hx = (float)(hue.Left + hsv.Hue / 360 * hue.Width);
        e.Graphics.DrawRectangle(outside, hx - 3, hue.Top, 6, hue.Height); e.Graphics.DrawRectangle(inside, hx - 3, hue.Top, 6, hue.Height);
    }
}
