using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Text.Json.Serialization;

namespace WirelessCanvas;

/// <summary>Dashboard theme of a screen in Sensor mode.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DashboardTheme { Arc, Ring, Segments, Minimal, List, Chart }

/// <summary>
/// Temperatures for the last 5 minutes (one sample per second from the cooling loop), for the Chart theme.
/// Written by the window and read by screen threads, hence the lock.
/// </summary>
public sealed class SensorHistory
{
    public const int Capacity = 300;
    readonly object gate = new();
    readonly Queue<SensorReadings> samples = new();
    public void Add(SensorReadings readings) { lock (gate) { samples.Enqueue(readings); while (samples.Count > Capacity) samples.Dequeue(); } }
    public double?[] Series(CoolingSource source) { lock (gate) return samples.Select(s => s.For(source)).ToArray(); }
}

/// <summary>
/// Minimal dashboards. Everything is drawn in the square screen, but the important parts stay inside the inscribed circle:
/// so one theme reads the same on the round pump and on a square LCD fan.
/// Element colors come from the screen settings: background, accent (empty: gradient palette by value), scale, number (empty: auto), caption.
/// </summary>
public static class Dashboard
{
    public static readonly string[] ThemeLabels = [L10n.T("Arc"), L10n.T("Ring"), L10n.T("Segments"), L10n.T("Minimal"), L10n.T("List"), L10n.T("Graph")];
    public static string Title(CoolingSource source) => source switch { CoolingSource.Gpu => "GPU", CoolingSource.Coolant => L10n.T("Coolant"), _ => "CPU" };
    public static string Text(double? value) => value is double v ? Math.Round(v).ToString("0") : "—";

    sealed class Style(ScreenSettings s, string palette)
    {
        public readonly ColorStop[] Palette = ParsePalette(palette);
        public readonly Color Background = Hex(s.BackgroundColor, Color.Black);
        public readonly Color Track = Hex(s.TrackColor, Color.FromArgb(38, 43, 54));
        public readonly Color Label = Hex(s.LabelColor, Color.FromArgb(154, 163, 181));
        readonly Color? accent = s.Color.Length > 0 ? Hex(s.Color, Color.DeepSkyBlue) : null;
        readonly Color? value = s.ValueColor.Length > 0 ? Hex(s.ValueColor, Color.White) : null;
        /// <summary>Accent: a custom color or the palette point at the scale position.</summary>
        public Color Accent(double t) => accent ?? Gradient.Sample(Palette, Math.Clamp(t, 0, 1) * .999);
        public Color Value => value ?? Color.FromArgb(244, 246, 250);
        static Color Hex(string text, Color fallback) => ColorPickerDialog.TryParseHex(text, out var c) ? c : fallback;
        static ColorStop[] ParsePalette(string palette)
        {
            try { return Gradient.ParsePalette(palette); }
            catch (Exception e) when (e is InvalidDataException or FormatException) { return Gradient.ParsePalette("#FF2454, #683DFF, #00D9DF, #FFCA48"); }
        }
    }

    static double Fraction(ScreenSettings s, double? value) => value is double v ? Math.Clamp((v - s.ArcMin) / (s.ArcMax - s.ArcMin), 0, 1) : 0;

    /// <summary>What the frame shows: a frame is sent again only when this changes (or every 10 s).</summary>
    public static string Key(ScreenSettings s, SensorReadings r, IReadOnlyList<double?> history) => s.Theme switch
    {
        DashboardTheme.List => string.Join("|", s.MultiSensors.Select(x => Text(r.For(x)))),
        DashboardTheme.Chart => Text(r.For(s.Sensor)) + "|" + history.Count / 5,
        _ => Text(r.For(s.Sensor)),
    };

    /// <summary>Dashboard frame at screen size (rotation and round mask are in <see cref="ScreenRenderer"/>).</summary>
    public static Bitmap Render(ScreenSettings s, SensorReadings r, IReadOnlyList<double?> history, string palette)
    {
        int size = s.Spec.Size;
        var style = new Style(s, palette);
        var bitmap = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias; g.TextRenderingHint = TextRenderingHint.AntiAlias; g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.Clear(style.Background);
            double? value = r.For(s.Sensor);
            switch (s.Theme)
            {
                case DashboardTheme.Ring: Ring(g, size, s, style, value); break;
                case DashboardTheme.Segments: Segments(g, size, s, style, value); break;
                case DashboardTheme.Minimal: Minimal(g, size, s, style, value); break;
                case DashboardTheme.List: List(g, size, s, style, r); break;
                case DashboardTheme.Chart: Chart(g, size, s, style, value, history); break;
                default: Arc(g, size, s, style, value); break;
            }
        }
        return bitmap;
    }

    // ── Themes ─────────────────────────────────────────────────────────────

    /// <summary>270° arc with the palette gradient, large centered number.</summary>
    static void Arc(Graphics g, int size, ScreenSettings s, Style st, double? value)
    {
        double t = Fraction(s, value);
        Glow(g, size, st, st.Accent(t), .16);
        float pad = size * .11f, stroke = size * .05f;
        var ring = new RectangleF(pad, pad, size - pad * 2, size - pad * 2);
        using (var track = new Pen(st.Track, stroke) { StartCap = LineCap.Round, EndCap = LineCap.Round }) g.DrawArc(track, ring, 135, 270);
        int segments = (int)Math.Round(t * 90);
        for (int i = 0; i < segments; i++)
        {
            using var pen = new Pen(st.Accent(i / 90.0), stroke) { StartCap = i == 0 ? LineCap.Round : LineCap.Flat, EndCap = i == segments - 1 ? LineCap.Round : LineCap.Flat };
            g.DrawArc(pen, ring, 135 + i * 3f, 3.4f);
        }
        Label(g, s.SensorLabel, st.Label, size, .27f, .062f);
        Number(g, Text(value), st, size, .5f, .3f, FontStyle.Bold, "Segoe UI", "°C", .062f, below: true);
    }

    /// <summary>Thin full progress ring with a dot at its end, large light digits.</summary>
    static void Ring(Graphics g, int size, ScreenSettings s, Style st, double? value)
    {
        double t = Fraction(s, value);
        float stroke = size * .028f, radius = size * .4f, c = size / 2f;
        var ring = new RectangleF(c - radius, c - radius, radius * 2, radius * 2);
        using (var track = new Pen(st.Track, stroke)) g.DrawEllipse(track, ring);
        if (value != null && t > 0)
        {
            var accent = st.Accent(t);
            using var pen = new Pen(accent, stroke) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawArc(pen, ring, -90, (float)(t * 360));
            double angle = (-90 + t * 360) * Math.PI / 180;
            float x = c + radius * (float)Math.Cos(angle), y = c + radius * (float)Math.Sin(angle), dot = stroke * 1.6f;
            using var head = new SolidBrush(st.Value); g.FillEllipse(head, x - dot / 2, y - dot / 2, dot, dot);
        }
        Number(g, Text(value), st, size, .46f, .3f, FontStyle.Regular, "Segoe UI Light", "°", .12f, below: false);
        Label(g, s.SensorLabel, st.Label, size, .68f, .055f);
    }

    /// <summary>Scale of 30 ticks over 240°: ticks up to the current value are lit; labels at the scale ends.</summary>
    static void Segments(Graphics g, int size, ScreenSettings s, Style st, double? value)
    {
        const int count = 30;
        double t = Fraction(s, value);
        float c = size / 2f, inner = size * .34f, outer = size * .43f, width = size * .024f;
        int lit = value == null ? 0 : (int)Math.Ceiling(t * count - 1e-9);
        for (int i = 0; i < count; i++)
        {
            double angle = (150 + 240.0 * i / (count - 1)) * Math.PI / 180;
            float cos = (float)Math.Cos(angle), sin = (float)Math.Sin(angle);
            using var pen = new Pen(i < lit ? st.Accent(i / (double)(count - 1)) : st.Track, width) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawLine(pen, c + inner * cos, c + inner * sin, c + outer * cos, c + outer * sin);
        }
        Number(g, Text(value), st, size, .47f, .26f, FontStyle.Regular, "Segoe UI Semibold", "°C", .06f, below: true);
        Label(g, s.SensorLabel, st.Label, size, .27f, .055f);
        // Scale end labels sit below the lowest ticks.
        using var small = new Font("Segoe UI", size * .045f, GraphicsUnit.Pixel);
        using var brush = new SolidBrush(st.Label);
        using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        foreach (var (text, degrees) in new[] { ($"{s.ArcMin:0}°", 150.0), ($"{s.ArcMax:0}°", 30.0) })
        {
            double a = degrees * Math.PI / 180;
            g.DrawString(text, small, brush, new PointF(c + size * .3f * (float)Math.Cos(a), c + size * .3f * (float)Math.Sin(a) + size * .085f), format);
        }
    }

    /// <summary>One large thin number, a caption and a thin line scale with end labels.</summary>
    static void Minimal(Graphics g, int size, ScreenSettings s, Style st, double? value)
    {
        double t = Fraction(s, value);
        Label(g, s.SensorLabel, st.Label, size, .28f, .058f);
        Number(g, Text(value), st, size, .48f, .36f, FontStyle.Regular, "Segoe UI Light", "°", .14f, below: false);
        float width = size * .56f, height = Math.Max(3, size * .014f), x = (size - width) / 2, y = size * .7f;
        Bar(g, new RectangleF(x, y, width, height), st.Track, value == null ? null : st.Accent(t), t);
        using var small = new Font("Segoe UI", size * .042f, GraphicsUnit.Pixel);
        using var brush = new SolidBrush(st.Label);
        g.DrawString($"{s.ArcMin:0}°", small, brush, x - size * .01f, y + height + size * .015f);
        using var right = new StringFormat { Alignment = StringAlignment.Far };
        g.DrawString($"{s.ArcMax:0}°", small, brush, new RectangleF(x, y + height + size * .015f, width + size * .01f, size * .06f), right);
    }

    /// <summary>Several sensors: caption on the left, value on the right, a bar for each value underneath.</summary>
    static void List(Graphics g, int size, ScreenSettings s, Style st, SensorReadings r)
    {
        var sources = s.MultiSensors.Count > 0 ? s.MultiSensors : [s.Sensor];
        float width = size * .62f, x = (size - width) / 2, row = size * .17f, top = size / 2f - row * sources.Count / 2f;
        using var label = new Font("Segoe UI Semibold", size * .052f, GraphicsUnit.Pixel);
        using var number = new Font("Segoe UI Semibold", size * .085f, GraphicsUnit.Pixel);
        using var labelBrush = new SolidBrush(st.Label);
        using var valueBrush = new SolidBrush(st.Value);
        using var right = new StringFormat { Alignment = StringAlignment.Far, LineAlignment = StringAlignment.Far };
        using var left = new StringFormat { Alignment = StringAlignment.Near, LineAlignment = StringAlignment.Far };
        for (int i = 0; i < sources.Count; i++)
        {
            double? value = r.For(sources[i]);
            double t = Fraction(s, value);
            float y = top + row * i, baseline = y + row * .62f;
            g.DrawString(Title(sources[i]).ToUpperInvariant(), label, labelBrush, new RectangleF(x, y, width, row * .6f), left);
            g.DrawString(Text(value) + "°", number, valueBrush, new RectangleF(x, y - row * .05f, width + size * .012f, row * .7f), right); // «°» with the font inset, up to the bar edge
            Bar(g, new RectangleF(x, baseline, width, Math.Max(4, size * .02f)), st.Track, value == null ? null : st.Accent(t), t);
        }
    }

    /// <summary>Value on top and a filled temperature chart for the last 5 minutes.</summary>
    static void Chart(Graphics g, int size, ScreenSettings s, Style st, double? value, IReadOnlyList<double?> history)
    {
        double t = Fraction(s, value);
        var accent = st.Accent(t);
        Label(g, s.SensorLabel, st.Label, size, .21f, .055f);
        Number(g, Text(value), st, size, .36f, .22f, FontStyle.Regular, "Segoe UI Light", "°", .09f, below: false);
        var area = new RectangleF(size * .17f, size * .52f, size * .66f, size * .22f);
        using (var baseLine = new Pen(st.Track, Math.Max(1, size * .004f))) g.DrawLine(baseLine, area.Left, area.Bottom, area.Right, area.Bottom);
        var points = history.Select((v, i) => (v, i)).Where(p => p.v is double).Select(p => (Index: p.i, Value: p.v!.Value)).ToArray();
        using var small = new Font("Segoe UI", size * .042f, GraphicsUnit.Pixel);
        using var labelBrush = new SolidBrush(st.Label);
        using var center = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        if (points.Length < 2) { g.DrawString(L10n.T("collecting data…"), small, labelBrush, new RectangleF(area.Left, area.Top, area.Width, area.Height), center); return; }
        double low = points.Min(p => p.Value), high = points.Max(p => p.Value);
        double middle = (low + high) / 2, span = Math.Max(6, high - low + 2);
        low = middle - span / 2; high = middle + span / 2;
        int window = Math.Max(SensorHistory.Capacity, history.Count) - 1;
        int offset = window - (history.Count - 1);
        PointF At((int Index, double Value) p) => new(area.Left + (float)((p.Index + offset) / (double)window) * area.Width, area.Bottom - (float)((p.Value - low) / (high - low)) * area.Height);
        var line = points.Select(At).ToArray();
        using (var fill = new GraphicsPath())
        {
            fill.AddLines(line); fill.AddLine(line[^1], new PointF(line[^1].X, area.Bottom)); fill.AddLine(new PointF(line[^1].X, area.Bottom), new PointF(line[0].X, area.Bottom)); fill.CloseFigure();
            using var gradient = new LinearGradientBrush(new RectangleF(area.Left, area.Top - 1, area.Width, area.Height + 2), Color.FromArgb(110, accent), Color.FromArgb(0, accent), LinearGradientMode.Vertical);
            g.FillPath(gradient, fill);
        }
        using (var pen = new Pen(accent, Math.Max(2, size * .008f)) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round }) g.DrawLines(pen, line);
        float dot = size * .022f;
        using (var head = new SolidBrush(st.Value)) g.FillEllipse(head, line[^1].X - dot / 2, line[^1].Y - dot / 2, dot, dot);
        g.DrawString(L10n.F((FormattableString)$"min {points.Min(p => p.Value):0}° · max {points.Max(p => p.Value):0}° · 5 min"), small, labelBrush, new RectangleF(0, area.Bottom + size * .02f, size, size * .06f), center);
    }

    // ── Shared elements ───────────────────────────────────────────────────

    static void Glow(Graphics g, int size, Style st, Color accent, double strength)
    {
        using var glow = new GraphicsPath();
        glow.AddEllipse(-size * .1f, -size * .1f, size * 1.2f, size * 1.2f);
        using var brush = new PathGradientBrush(glow) { CenterColor = PillButton.Blend(st.Background, accent, strength), SurroundColors = [st.Background] };
        g.FillRectangle(brush, 0, 0, size, size);
    }

    static void Label(Graphics g, string text, Color color, int size, float y, float height)
    {
        using var font = new Font("Segoe UI Semibold", size * height, GraphicsUnit.Pixel);
        using var brush = new SolidBrush(color);
        using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        g.DrawString(text.ToUpperInvariant(), font, brush, new RectangleF(0, size * (y - height), size, size * height * 2), format);
    }

    /// <summary>Large centered number; the unit goes top right (°) or on the line below (°C).</summary>
    static void Number(Graphics g, string text, Style st, int size, float y, float height, FontStyle weight, string family, string unit, float unitHeight, bool below)
    {
        using var font = new Font(family, size * height, weight, GraphicsUnit.Pixel);
        using var unitFont = new Font(family == "Segoe UI Light" ? family : "Segoe UI", size * unitHeight, GraphicsUnit.Pixel);
        using var brush = new SolidBrush(text == "—" ? Color.FromArgb(120, st.Value) : st.Value);
        using var unitBrush = new SolidBrush(st.Label);
        var format = StringFormat.GenericTypographic;
        var measured = g.MeasureString(text, font, PointF.Empty, format);
        float x = (size - measured.Width) / 2, top = size * y - measured.Height / 2;
        g.DrawString(text, font, brush, x, top, format);
        if (text == "—") return;
        if (below)
        {
            using var center = new StringFormat { Alignment = StringAlignment.Center };
            g.DrawString(unit, unitFont, unitBrush, new RectangleF(0, top + measured.Height * .98f, size, size * unitHeight * 1.6f), center);
        }
        else g.DrawString(unit, unitFont, unitBrush, x + measured.Width + size * .005f, top + measured.Height * .12f, format);
    }

    /// <summary>Rounded bar: track colored as the scale, filled with the accent up to t.</summary>
    static void Bar(Graphics g, RectangleF r, Color track, Color? fill, double t)
    {
        using (var path = Rounded(r)) using (var brush = new SolidBrush(track)) g.FillPath(brush, path);
        if (fill is not Color f || t <= 0) return;
        var part = new RectangleF(r.Left, r.Top, Math.Max(r.Height, (float)(r.Width * t)), r.Height);
        using var filled = Rounded(part); using var accent = new SolidBrush(f); g.FillPath(accent, filled);
    }
    static GraphicsPath Rounded(RectangleF r)
    {
        var path = new GraphicsPath(); float d = r.Height;
        path.AddArc(r.Left, r.Top, d, d, 90, 180); path.AddArc(r.Right - d, r.Top, d, d, 270, 180); path.CloseFigure();
        return path;
    }
}
