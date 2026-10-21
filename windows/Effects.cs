using System.Text.Json.Serialization;

namespace WirelessCanvas;

/// <summary>What the mapping draws: a plain gradient or one of the shader effects.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum EffectKind { Gradient, Waves, Fire, Starfall, Twinkle, Aurora, Plasma, Rain, Ripple, Radar, Lava }

/// <summary>Effect parameter: label, limits, default value, step (decimal places), hint.</summary>
public sealed record EffectParameter(string Label, double Min, double Max, double Default, int Decimals, string Hint);
public sealed record EffectInfo(EffectKind Kind, string Title, string Description, string Palette, string Background, int Speed, EffectParameter[] Parameters);

/// <summary>Settings of one effect: its own palette, background color, speed (repeats per cycle) and parameters.</summary>
public sealed class EffectParams
{
    public string Palette { get; set; } = "";
    public string Background { get; set; } = "#000000";
    public int Speed { get; set; } = 1;
    public List<double> Values { get; set; } = [];
}

/// <summary>Selected effect and the settings of every effect (nothing is lost when switching).</summary>
public sealed class EffectSettings
{
    public EffectKind Kind { get; set; }
    public Dictionary<string, EffectParams> Saved { get; set; } = [];
    /// <summary>Effect settings; missing values are filled with defaults.</summary>
    public EffectParams For(EffectKind kind)
    {
        var info = Effects.Info(kind);
        if (!Saved.TryGetValue(kind.ToString(), out var value)) Saved[kind.ToString()] = value = new EffectParams { Palette = info.Palette, Background = info.Background, Speed = info.Speed };
        while (value.Values.Count < info.Parameters.Length) value.Values.Add(info.Parameters[value.Values.Count].Default);
        if (value.Values.Count > info.Parameters.Length) value.Values.RemoveRange(info.Parameters.Length, value.Values.Count - info.Parameters.Length);
        return value;
    }
    [JsonIgnore] public EffectParams Current => For(Kind);
    public void Validate()
    {
        if (!Enum.IsDefined(Kind) || Saved.Count > 16) throw new InvalidDataException(L10n.T("Invalid mapping effect."));
        foreach (var (key, value) in Saved)
        {
            if (!Enum.TryParse<EffectKind>(key, out var kind) || value == null || value.Speed is < 1 or > 10 || !ColorPickerDialog.TryParseHex(value.Background, out _))
                throw new InvalidDataException(L10n.T("Invalid effect settings: ") + key + ".");
            _ = Gradient.ParsePalette(value.Palette);
            var parameters = Effects.Info(kind).Parameters;
            if (value.Values.Count > parameters.Length || value.Values.Select((v, i) => !double.IsFinite(v) || v < parameters[i].Min || v > parameters[i].Max).Any(x => x))
                throw new InvalidDataException(L10n.T("Effect parameters “") + Effects.Info(kind).Title + L10n.T("” are out of range."));
        }
    }
}

/// <summary>
/// Mapping shader effects. Each effect is a function of a canvas point (mm) and time that returns a palette position (U)
/// and brightness above the background (V): color = background + (palette(U) − background) · V. The cycle is seamless: devices loop the uploaded
/// frames, so time enters as the cycle fraction τ and speed is an integer number of repeats per cycle; particles
/// move along closed paths. Fire flows continuously and closes by blending the end of the cycle into its start.
/// </summary>
public static class Effects
{
    const double Tau = Math.PI * 2;
    public static readonly EffectInfo[] All =
    [
        new(EffectKind.Gradient, L10n.T("Gradient"), L10n.T("The palette moves across the case at the Tilt angle."), "", "#000000", 1, []),
        new(EffectKind.Waves, L10n.T("Waves"), L10n.T("Waves of different lengths overlap, with darker troughs."), "#0B3D91@0, #1E90FF@0.3, #00D9DF@0.6, #7FFFD4@0.85", "#000814", 1,
        [
            new(L10n.T("Wavelength, mm"), 60, 1200, 320, 0, L10n.T("Distance between main wave crests.")),
            new(L10n.T("Layers"), 1, 3, 2, 0, L10n.T("Overlapping waves: 1 is smooth, 3 adds complex ripples.")),
            new(L10n.T("Trough depth, %"), 0, 100, 35, 0, L10n.T("How dark the wave troughs become.")),
        ]),
        new(EffectKind.Fire, L10n.T("Flame"), L10n.T("Flames rise from the bottom; palette colors follow fire intensity."), "#1A0000@0, #A01000@0.3, #FF5A00@0.55, #FFB000@0.75, #FFF2B0@0.93", "#000000", 1,
        [
            new(L10n.T("Flame height, %"), 20, 100, 65, 0, L10n.T("How far the fire reaches up the case.")),
            new(L10n.T("Flame size, mm"), 40, 400, 110, 0, L10n.T("Larger values give broad, calm flames; smaller values give frequent flames.")),
            new(L10n.T("Turbulence, %"), 0, 100, 55, 0, L10n.T("How irregular and elongated the flames are.")),
        ]),
        new(EffectKind.Starfall, L10n.T("Starfall"), L10n.T("Stars with trails move at the Tilt angle, each using a palette color."), "#FFFFFF@0, #9BD4FF@0.3, #C9A7FF@0.6, #FFE6A8@0.85", "#000000", 2,
        [
            new(L10n.T("Stars"), 3, 60, 18, 0, L10n.T("Number of stars visible at once.")),
            new(L10n.T("Trail length, mm"), 20, 500, 160, 0, L10n.T("The trail fades toward its end.")),
            new(L10n.T("Star size, mm"), 6, 60, 16, 0, L10n.T("Width of each star and its trail.")),
        ]),
        new(EffectKind.Twinkle, L10n.T("Twinkle"), L10n.T("Random points flash and fade like stars in the sky."), "#FFFFFF@0, #FFE6A8@0.3, #A8D8FF@0.6, #FFC0E0@0.85", "#000000", 1,
        [
            new(L10n.T("Density, %"), 5, 100, 40, 0, L10n.T("Proportion of points that twinkle.")),
            new(L10n.T("Point size, mm"), 10, 200, 45, 0, L10n.T("Small values affect individual LEDs; large values create patches.")),
            new(L10n.T("Flash sharpness"), 1, 10, 4, 0, L10n.T("1: smooth breathing. 10: short spark.")),
        ]),
        new(EffectKind.Aurora, L10n.T("Aurora"), L10n.T("Wavy ribbons of light flow across the top of the case."), "#00FF9C@0, #00C2FF@0.35, #7B2CFF@0.7", "#020812", 1,
        [
            new(L10n.T("Ribbon spacing, mm"), 60, 800, 260, 0, L10n.T("Distance between aurora ribbons.")),
            new(L10n.T("Waviness, %"), 0, 100, 60, 0, L10n.T("How much the ribbons bend.")),
            new(L10n.T("Aurora height, %"), 20, 100, 70, 0, L10n.T("How far the light reaches down from the top.")),
        ]),
        new(EffectKind.Plasma, L10n.T("Plasma"), L10n.T("Shifting patches of palette colors, like classic demo plasma."), "#FF2454@0, #683DFF@0.25, #00D9DF@0.5, #FFCA48@0.75", "#000000", 1,
        [
            new(L10n.T("Scale, mm"), 60, 800, 240, 0, L10n.T("Size of the patches.")),
            new(L10n.T("Complexity"), 1, 4, 3, 0, L10n.T("Number of waves mixed together.")),
            new(L10n.T("Color cycling"), 0, 3, 1, 0, L10n.T("Palette repetitions per cycle (0 keeps colors in place).")),
        ]),
        new(EffectKind.Rain, L10n.T("Rain"), L10n.T("Drops fall in thin streams with fading trails."), "#4FC3F7@0, #81D4FA@0.3, #B3E5FC@0.6, #29B6F6@0.85", "#00060F", 2,
        [
            new(L10n.T("Drops"), 5, 100, 45, 0, L10n.T("Number of streams visible at once.")),
            new(L10n.T("Drop length, mm"), 20, 300, 70, 0, L10n.T("Length of the glowing trail.")),
            new(L10n.T("Width, mm"), 4, 40, 10, 0, L10n.T("Stream thickness.")),
        ]),
        new(EffectKind.Ripple, L10n.T("Ripple"), L10n.T("Rings spread from a point like ripples on water; color follows distance."), "#FF2454@0, #683DFF@0.33, #00D9DF@0.66", "#000000", 1,
        [
            new(L10n.T("Ring spacing, mm"), 60, 600, 200, 0, L10n.T("Distance between adjacent rings.")),
            new(L10n.T("Ring thickness, %"), 10, 100, 45, 0, L10n.T("Fraction of spacing occupied by the ring and its trail.")),
            new(L10n.T("Center X, %"), 0, 100, 50, 0, L10n.T("Origin: 0 is the left edge, 100 is the right edge.")),
            new(L10n.T("Center Y, %"), 0, 100, 50, 0, L10n.T("0 is the top of the case, 100 is the bottom.")),
        ]),
        new(EffectKind.Radar, L10n.T("Radar"), L10n.T("A rotating beam leaves a fading trail; color follows distance from the center."), "#7CFFB2@0, #00E676@0.45, #00838F@0.85", "#00140A", 1,
        [
            new(L10n.T("Trail width, °"), 10, 270, 70, 0, L10n.T("Angular length of the fading trail behind the beam.")),
            new(L10n.T("Center X, %"), 0, 100, 50, 0, L10n.T("Rotation axis: 0 is the left edge, 100 is the right edge.")),
            new(L10n.T("Center Y, %"), 0, 100, 50, 0, L10n.T("0 is the top of the case, 100 is the bottom.")),
        ]),
        new(EffectKind.Lava, L10n.T("Lava"), L10n.T("Soft lava drops float across the case and merge together."), "#FF3D00@0, #FF9100@0.3, #D500F9@0.6, #FF1744@0.85", "#14000A", 1,
        [
            new(L10n.T("Drops"), 2, 8, 5, 0, L10n.T("Number of floating drops.")),
            new(L10n.T("Size, mm"), 60, 400, 170, 0, L10n.T("Drop size.")),
            new(L10n.T("Edge softness, %"), 0, 100, 40, 0, L10n.T("0: sharp drops. 100: blurred drops.")),
        ]),
    ];
    public static EffectInfo Info(EffectKind kind) => All.First(e => e.Kind == kind);
    public static bool Active(Profile p) => p.Effect.Kind != EffectKind.Gradient;
    /// <summary>Mapping palette: the effect's own palette when an effect is active, otherwise the gradient palette.</summary>
    public static ColorStop[] Palette(Profile p) => Gradient.ParsePalette(Active(p) ? p.Effect.Current.Palette : p.Palette);
    public static Color Background(Profile p) => ColorPickerDialog.TryParseHex(p.Effect.Current.Background, out var c) ? c : Color.Black;

    /// <summary>Effect canvas in mm: the reference case, or the bounds of all fans and the pump.</summary>
    public static RectangleF Bounds(Profile p)
    {
        if (p.EffectBounds is { } own) return own;
        if (p.LayoutPreset == CaseLayout.Id || p.Fans.Count == 0) return new RectangleF(8, 20, 600, 560);
        var all = p.Fans.Select(Gradient.FanBounds).Append(new RectangleF((float)(p.Pump.X - p.Pump.Diameter / 2), (float)(p.Pump.Y - p.Pump.Diameter / 2), (float)p.Pump.Diameter, (float)p.Pump.Diameter)).ToArray();
        float left = all.Min(b => b.Left), top = all.Min(b => b.Top);
        return new RectangleF(left, top, Math.Max(40, all.Max(b => b.Right) - left), Math.Max(40, all.Max(b => b.Bottom) - top));
    }

    /// <summary>U — position on the palette (0…1), V is brightness above the background (0…1).</summary>
    public static (double U, double V) Evaluate(Profile p, LedPoint point, double seconds)
    {
        var e = p.Effect.Current;
        double tau = Frac(seconds / p.PeriodSeconds);
        int k = Math.Clamp(e.Speed, 1, 10);
        var v = e.Values; var b = Bounds(p);
        double x = point.X, y = point.Y;
        return p.Effect.Kind switch
        {
            EffectKind.Waves => Waves(p, x, y, tau, k, v[0], (int)v[1], v[2] / 100),
            EffectKind.Fire => Fire(b, x, y, tau, k, v[0] / 100, v[1], v[2] / 100),
            EffectKind.Starfall => Particles(b, x, y, tau, k, Gradient.TravelDirection(p.Direction), (int)v[0], v[1], v[2], seed: 11),
            EffectKind.Twinkle => Twinkle(x, y, tau, k, v[0] / 100, v[1], v[2]),
            EffectKind.Aurora => Aurora(b, x, y, tau, k, v[0], v[1] / 100, v[2] / 100),
            EffectKind.Plasma => Plasma(b, x, y, tau, k, v[0], (int)v[1], (int)v[2]),
            EffectKind.Rain => Particles(b, x, y, tau, k, new LedPoint(0, 1), (int)v[0], v[1], v[2], seed: 23),
            EffectKind.Ripple => Ripple(b, x, y, tau, k, v[0], v[1] / 100, v[2] / 100, v[3] / 100),
            EffectKind.Radar => Radar(b, x, y, tau, k, v[0] / 360, v[1] / 100, v[2] / 100),
            EffectKind.Lava => Lava(b, x, y, tau, k, (int)v[0], v[1], v[2] / 100),
            _ => (Frac(Gradient.Phase(p, point, seconds)), 1),
        };
    }

    // ── Effects ──────────────────────────────────────────────────────────

    /// <summary>Up to three waves at different angles; speeds are integer multiples of k, so the cycle closes.</summary>
    static (double, double) Waves(Profile p, double x, double y, double tau, int k, double length, int layers, double depth)
    {
        ReadOnlySpan<double> angle = [0, 37, -61], scale = [1, .63, 1.41], amp = [1, .6, .45];
        ReadOnlySpan<int> speed = [1, -2, 1];
        double sum = 0, norm = 0;
        for (int j = 0; j < Math.Clamp(layers, 1, 3); j++)
        {
            var dir = Gradient.TravelDirection(p.Direction + angle[j]);
            double d = x * dir.X + y * dir.Y;
            sum += amp[j] * Math.Sin(Tau * (d / (length * scale[j]) - speed[j] * k * tau) + j * 1.7);
            norm += amp[j];
        }
        double s = sum / norm;
        return ((.5 + .5 * s) * .999, 1 - depth * (.5 - .5 * s));
    }

    /// <summary>
    /// Noise flames flow upwards and fade with height. The noise shifts 3·k cells per cycle, and the end of the cycle
    /// blends smoothly into the start (mixing shifts τ and τ−1), so there is no seam.
    /// </summary>
    static (double, double) Fire(RectangleF b, double x, double y, double tau, int k, double height, double size, double turbulence)
    {
        double nx = (x - b.Left) / size * (1 + turbulence), ny = (y - b.Top) / size, shift = 3.0 * k;
        double F(double offset) => Fbm(nx, ny + offset, 3);
        double noise = (1 - tau) * F(tau * shift) + tau * F((tau - 1) * shift);
        double rise = Math.Clamp((b.Bottom - y) / b.Height, 0, 1);
        double heat = Math.Clamp(noise * (1.1 + turbulence * .6) - rise / Math.Max(.2, height) + .2, 0, 1);
        heat = heat * heat * (3 - 2 * heat);
        // Color follows flame strength, never past the last palette color (otherwise it would wrap to the dark first one).
        return (heat * .92, heat);
    }

    /// <summary>
    /// Particles with tails (starfall, rain): each has its own lane across the motion and its own phase; the head travels
    /// the whole canvas diagonal an integer number of times per cycle. Each particle uses its own palette point.
    /// </summary>
    static (double, double) Particles(RectangleF b, double x, double y, double tau, int k, LedPoint dir, int count, double tail, double size, int seed)
    {
        double cx = b.Left + b.Width / 2, cy = b.Top + b.Height / 2, span = Math.Sqrt(b.Width * b.Width + b.Height * b.Height);
        double a = (x - cx) * dir.X + (y - cy) * dir.Y, across = -(x - cx) * dir.Y + (y - cy) * dir.X;
        double best = 0, u = 0;
        for (int i = 0; i < count; i++)
        {
            double lane = (Hash(i, seed, 1) - .5) * span;
            double side = Math.Abs(across - lane);
            if (side > size) continue;
            int laps = k * (1 + (int)(Hash(i, seed, 2) * 2));
            double head = -span / 2 - tail + Frac(laps * tau + Hash(i, seed, 3)) * (span + 2 * tail);
            double behind = head - a, width = 1 - side / size, value = 0;
            if (Math.Abs(behind) < size) value = width * (1 - Math.Abs(behind) / size);
            if (behind > 0 && behind < tail) value = Math.Max(value, width * Math.Pow(1 - behind / tail, 2) * .85);
            if (value > best) { best = value; u = Hash(i, seed, 4) * .999; }
        }
        return (u, best);
    }

    /// <summary>Cells of size cell flash along a sine with an integer frequency (k, 2k or 3k times per cycle) and their own phase.</summary>
    static (double, double) Twinkle(double x, double y, double tau, int k, double density, double cell, double sharpness)
    {
        int cx = (int)Math.Floor(x / cell), cy = (int)Math.Floor(y / cell);
        if (Hash(cx, cy, 1) > density) return (0, 0);
        int laps = k * (1 + (int)(Hash(cx, cy, 2) * 3));
        double wave = Math.Max(0, Math.Sin(Tau * (laps * tau + Hash(cx, cy, 3))));
        // A soft round spot randomly offset inside the cell, not a square.
        double px = (cx + .3 + .4 * Hash(cx, cy, 5)) * cell, py = (cy + .3 + .4 * Hash(cx, cy, 6)) * cell;
        double spot = Math.Clamp(1 - Math.Sqrt((x - px) * (x - px) + (y - py) * (y - py)) / (cell * .55), 0, 1);
        return (Hash(cx, cy, 4) * .999, Math.Pow(wave, sharpness) * Math.Sqrt(spot));
    }

    /// <summary>Wavy bands at the top of the canvas; their bend and drift are sines with integer frequencies in τ.</summary>
    static (double, double) Aurora(RectangleF b, double x, double y, double tau, int k, double width, double waviness, double height)
    {
        double fromTop = Math.Clamp((y - b.Top) / b.Height, 0, 1);
        double wave = Math.Sin(Tau * (x / width + k * tau) + waviness * 2.5 * Math.Sin(Tau * k * tau + x / (width * .7) + y / width));
        double curtain = Math.Pow(.5 + .5 * wave, 1.2);
        double fade = Math.Pow(Math.Clamp(1 - fromTop / Math.Max(.2, height), 0, 1), .8);
        double u = Frac(x / (width * 3) + .15 * Math.Sin(Tau * k * tau + y / width));
        return (u * .999, Math.Clamp((curtain * 1.15 + .12) * fade, 0, 1));
    }

    /// <summary>Sum of sines with shifts that are multiples of k; the palette runs an integer number of times per cycle.</summary>
    static (double, double) Plasma(RectangleF b, double x, double y, double tau, int k, double scale, int complexity, int colorCycles)
    {
        double X = (x - b.Left) / scale * Tau / 2, Y = (y - b.Top) / scale * Tau / 2, t = Tau * tau;
        double cx = b.Width / scale * Math.PI / 2 * (1 + .6 * Math.Cos(k * t)), cy = b.Height / scale * Math.PI / 2 * (1 + .6 * Math.Sin(k * t));
        Span<double> terms = [Math.Sin(X + k * t), Math.Sin(Y * 1.3 - 2 * k * t), Math.Sin((X + Y) * .7 + k * t), Math.Sin(Math.Sqrt((X - cx) * (X - cx) + (Y - cy) * (Y - cy)) * 1.5 - k * t)];
        int n = Math.Clamp(complexity, 1, 4); double sum = 0;
        for (int i = 0; i < n; i++) sum += terms[i];
        return (Frac(.5 + .5 * sum / n + colorCycles * tau) * .999, 1);
    }

    /// <summary>Rings move out k spacings per cycle; bright front and fading trail; color by distance.</summary>
    static (double, double) Ripple(RectangleF b, double x, double y, double tau, int k, double spacing, double thickness, double centerX, double centerY)
    {
        double dx = x - (b.Left + b.Width * centerX), dy = y - (b.Top + b.Height * centerY), r = Math.Sqrt(dx * dx + dy * dy);
        double w = Frac(k * tau - r / spacing);
        double v = w < thickness ? Math.Pow(1 - w / thickness, 1.6) : 0;
        return (Frac(r / (spacing * 3)) * .999, v);
    }

    /// <summary>The beam makes k turns per cycle with a fading trail behind it; faint background glow.</summary>
    static (double, double) Radar(RectangleF b, double x, double y, double tau, int k, double trail, double centerX, double centerY)
    {
        double dx = x - (b.Left + b.Width * centerX), dy = y - (b.Top + b.Height * centerY);
        double angle = Math.Atan2(dy, dx) / Tau, behind = Frac(k * tau - angle);
        double v = behind < trail ? Math.Pow(1 - behind / trail, 1.6) : 0;
        double reach = Math.Sqrt(b.Width * b.Width + b.Height * b.Height) / 2;
        return (Math.Clamp(Math.Sqrt(dx * dx + dy * dy) / reach, 0, .999), Math.Max(v, .06));
    }

    /// <summary>Metaballs: blobs follow Lissajous curves with integer frequencies; the field merges neighbouring blobs.</summary>
    static (double, double) Lava(RectangleF b, double x, double y, double tau, int k, int count, double size, double softness)
    {
        double field = 0, colorSum = 0;
        for (int i = 0; i < Math.Clamp(count, 1, 8); i++)
        {
            int fx = 1 + (int)(Hash(i, 31, 1) * 2), fy = 1 + (int)(Hash(i, 31, 2) * 2);
            double cx = b.Left + b.Width * (.5 + .38 * Math.Sin(Tau * (fx * k * tau + Hash(i, 31, 3))));
            double cy = b.Top + b.Height * (.5 + .38 * Math.Sin(Tau * (fy * k * tau + Hash(i, 31, 4))));
            double d2 = (x - cx) * (x - cx) + (y - cy) * (y - cy), c = size * size / (d2 + 1) * .25;
            field += c; colorSum += c * Hash(i, 31, 5);
        }
        double soft = .05 + softness * .85;
        double v = Math.Clamp((field - (1 - soft)) / (2 * soft), 0, 1);
        v = v * v * (3 - 2 * v);
        return (field > 0 ? colorSum / field * .999 : 0, v);
    }

    // ── Noise and hash ────────────────────────────────────────────────────────

    static double Frac(double value) => value - Math.Floor(value);
    /// <summary>Deterministic number 0…1 from three integers: the effect looks the same in the preview and on devices.</summary>
    static double Hash(int a, int b, int c)
    {
        unchecked
        {
            uint h = (uint)(a * 374761393 + b * 668265263 + c * 1442695041) ^ 0x9E3779B9;
            h = (h ^ (h >> 15)) * 2246822519u; h = (h ^ (h >> 13)) * 3266489917u; h ^= h >> 16;
            return (h & 0xFFFFFF) / 16777216.0;
        }
    }
    static double Noise(double x, double y)
    {
        int x0 = (int)Math.Floor(x), y0 = (int)Math.Floor(y);
        double fx = x - x0, fy = y - y0, sx = fx * fx * (3 - 2 * fx), sy = fy * fy * (3 - 2 * fy);
        double a = Hash(x0, y0, 7), b = Hash(x0 + 1, y0, 7), c = Hash(x0, y0 + 1, 7), d = Hash(x0 + 1, y0 + 1, 7);
        return a + (b - a) * sx + (c - a) * sy + (a - b - c + d) * sx * sy;
    }
    static double Fbm(double x, double y, int octaves)
    {
        double sum = 0, amp = .5, norm = 0;
        for (int i = 0; i < octaves; i++) { sum += Noise(x, y) * amp; norm += amp; x *= 2.03; y *= 2.03; amp *= .5; }
        return sum / norm;
    }
}
