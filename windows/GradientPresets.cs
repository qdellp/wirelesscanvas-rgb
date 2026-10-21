namespace WirelessCanvas;

internal sealed record GradientPreset(string Family, string Name, string Palette, ColorSpace Space);

internal static class GradientPresets
{
    public static readonly IReadOnlyList<GradientPreset> All = Create().AsReadOnly();
    static List<GradientPreset> Create()
    {
        string[] families = ["Red", "Orange", "Yellow", "Lime", "Green", "Mint", "Turquoise", "Azure", "Blue", "Violet", "Magenta", "Pink"];
        var result = new List<GradientPreset>();
        for (int i = 0; i < families.Length; i++)
        {
            double h = i * 30;
            string Colors(params (double H, double S, double V)[] values) => string.Join(", ", values.Select(v => ColorPickerDialog.Hex(ColorMath.FromHsv(v.H, v.S, v.V))));
            result.Add(new(families[i], "Vivid", Colors((h - 10, 1, 1), (h, 1, 1), (h + 12, .85, .85)), ColorSpace.Hsv));
            result.Add(new(families[i], "Neon", Colors((h - 20, 1, .65), (h, .65, 1), (h + 20, 1, 1)), ColorSpace.Hsv));
            result.Add(new(families[i], "Deep", Colors((h, 1, .12), (h - 8, 1, .8), (h + 12, 1, .35)), ColorSpace.Hsv));
            result.Add(new(families[i], "Pastel", Colors((h - 12, .22, 1), (h, .5, 1), (h + 15, .3, 1)), ColorSpace.Hsv));
        }
        result.Add(new("Neutral", "Pearl", "#FFFFFF, #B9C4D8, #F5F1FF", ColorSpace.Rgb));
        result.Add(new("Neutral", "Ice", "#F4FFFF, #83CADD, #DAF3FF", ColorSpace.Rgb));
        result.Add(new("Neutral", "Warm light", "#FFF5D6, #DDA975, #FFF0D6", ColorSpace.Rgb));
        result.Add(new("Neutral", "Graphite", "#111827, #8794A8, #303A4C", ColorSpace.Rgb));
        return result;
    }
}

internal sealed class GradientPresetTile : Button
{
    readonly GradientPreset preset;
    readonly ColorStop[] stops;
    public bool Active;
    public GradientPresetTile(GradientPreset preset)
    {
        this.preset = preset; stops = Gradient.ParsePalette(preset.Palette);
        Text = L10n.T(preset.Name); AccessibleName = L10n.T(preset.Family) + " · " + Text;
        Width = 196; Height = 76; Margin = new Padding(0, 0, 12, 0); Cursor = Cursors.Hand;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; g.Clear(Color.FromArgb(26, 30, 40));
        int w = Math.Max(1, Width - 16);
        for (int x = 0; x < w; x++)
        {
            using var brush = new SolidBrush(Gradient.Sample(stops, x / (double)Math.Max(1, w - 1), preset.Space));
            g.FillRectangle(brush, 8 + x, 8, 1, 30);
        }
        TextRenderer.DrawText(g, Text, Font, new Rectangle(8, 42, Width - 16, 26), Color.WhiteSmoke, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        using var pen = new Pen(Active || Focused ? Color.DeepSkyBlue : Color.FromArgb(73, 84, 111), Active || Focused ? 2 : 1);
        g.DrawRectangle(pen, 1, 1, Width - 3, Height - 3);
    }
}
