using System.Globalization;
using System.Text.Json.Serialization;

namespace WirelessCanvas;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ColorSpace { Rgb, Hsv }
public readonly record struct HsvColor(double Hue, double Saturation, double Value);

public static class ColorMath
{
    public static HsvColor ToHsv(Color color)
    {
        double r = color.R / 255.0, g = color.G / 255.0, b = color.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), delta = max - min;
        double hue = delta == 0 ? 0 : max == r ? 60 * ((g - b) / delta % 6) : max == g ? 60 * ((b - r) / delta + 2) : 60 * ((r - g) / delta + 4);
        if (hue < 0) hue += 360;
        return new(hue, max == 0 ? 0 : delta / max, max);
    }
    public static Color FromHsv(double hue, double saturation, double value)
    {
        hue = (hue % 360 + 360) % 360; saturation = Math.Clamp(saturation, 0, 1); value = Math.Clamp(value, 0, 1);
        double c = value * saturation, x = c * (1 - Math.Abs(hue / 60 % 2 - 1)), m = value - c;
        var (r, g, b) = hue switch
        {
            < 60 => (c, x, 0.0), < 120 => (x, c, 0.0), < 180 => (0.0, c, x),
            < 240 => (0.0, x, c), < 300 => (x, 0.0, c), _ => (c, 0.0, x)
        };
        return Color.FromArgb((int)Math.Round((r + m) * 255), (int)Math.Round((g + m) * 255), (int)Math.Round((b + m) * 255));
    }
}

public sealed class PaletteModel
{
    readonly List<ColorStop> points;
    public IReadOnlyList<ColorStop> Points => points;
    public int Selected { get; private set; }
    public PaletteModel(string palette) { points = Gradient.ParsePalette(palette).ToList(); }
    public string Serialize() => string.Join(", ", points.Select(p => $"#{p.Color.R:X2}{p.Color.G:X2}{p.Color.B:X2}@{p.Position.ToString("G17", CultureInfo.InvariantCulture)}"));
    public void Select(int index) { if (index < 0 || index >= points.Count) throw new ArgumentOutOfRangeException(nameof(index)); Selected = index; }
    public bool MoveSelected(double position)
    {
        if (!double.IsFinite(position) || position is < 0 or >= 1) return false;
        if (points.Where((_, i) => i != Selected).Any(p => Math.Abs(p.Position - position) < .0005)) return false;
        var changed = points[Selected] with { Position = position };
        points[Selected] = changed; points.Sort((a, b) => a.Position.CompareTo(b.Position)); Selected = points.IndexOf(changed); return true;
    }
    public void SetColor(Color color) => points[Selected] = points[Selected] with { Color = Color.FromArgb(color.R, color.G, color.B) };
    public bool Add(double? position, ColorSpace space)
    {
        if (points.Count >= 16) return false;
        if (position == null)
        {
            int largest = Enumerable.Range(0, points.Count).MaxBy(i => (i + 1 == points.Count ? points[0].Position + 1 : points[i + 1].Position) - points[i].Position);
            position = (points[largest].Position + (largest + 1 == points.Count ? points[0].Position + 1 : points[largest + 1].Position)) / 2 % 1;
        }
        double at = Math.Clamp(Math.Round(position.Value, 3), 0, .999);
        if (points.Any(p => Math.Abs(p.Position - at) < .0005)) return false;
        var color = Gradient.Sample(points.ToArray(), at, space);
        var point = new ColorStop(at, color); points.Add(point); points.Sort((a, b) => a.Position.CompareTo(b.Position)); Selected = points.IndexOf(point); return true;
    }
    public bool RemoveSelected()
    {
        if (points.Count <= 2) return false;
        points.RemoveAt(Selected); Selected = Math.Min(Selected, points.Count - 1); return true;
    }
    public void EvenlySpace()
    {
        for (int i = 0; i < points.Count; i++) points[i] = points[i] with { Position = (double)i / points.Count };
    }
    public static PaletteModel Rainbow() => new("#FF0000, #FFFF00, #00FF00, #00FFFF, #0000FF, #FF00FF");
}
