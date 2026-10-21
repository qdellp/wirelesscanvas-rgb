namespace WirelessCanvas;

/// <summary>
/// Keyboard lighting: follow mapping is a scaled-down copy of the case canvas (palette, effect, cycle, angle, length,
/// phase from the fan clock), or its own: palette, effect, cycle, length in keys, angle. Brightness is separate in both modes.
/// </summary>
public sealed class KeyboardSettings
{
    public const uint NoLed = uint.MaxValue;
    // AULA F75 matrix as reported by OpenRGB on the author's PC (Keyboard zone, 15 × 6): LED index per cell, or “no key”.
    public static readonly uint[] AulaF75 =
    [
        0, NoLed, 12, 18, 24, 30, 36, 42, 48, 54, 60, 66, 72, 78, NoLed,
        1, 7, 13, 19, 25, 31, 37, 43, 49, 55, 61, 67, 73, 79, 85,
        2, 8, 14, 20, 26, 32, 38, 44, 50, 56, 62, 68, 74, 80, 86,
        3, 9, 15, 21, 27, 33, 39, 45, 51, 57, 63, 69, NoLed, 81, 87,
        4, NoLed, 10, 16, 22, 28, 34, 40, 46, 52, 58, 64, 70, 82, 88,
        5, 11, 17, NoLed, NoLed, NoLed, 35, NoLed, NoLed, NoLed, 53, 59, 77, 83, 89,
    ];
    public bool Enabled { get; set; }
    public string Device { get; set; } = "AULA F75";
    /// <summary>true — follow mapping: everything comes from the case canvas, phase from the fan clock; false means own lighting.</summary>
    public bool UseCaseGradient { get; set; } = true;
    /// <summary>The keyboard's own effect (in own-lighting mode), with its own palettes and parameters.</summary>
    public EffectSettings Effect { get; set; } = new();
    /// <summary>Key pitch in mm: lengths and effects of own lighting are computed in mm, as on the canvas.</summary>
    public const double KeyPitch = 19.05;
    [System.Text.Json.Serialization.JsonIgnore] Profile? own;
    /// <summary>
    /// Own lighting as a profile for the shared color calculation (Gradient.Shade). One object per keyboard; fields are updated
    /// on every call, with no allocation per frame. The effect is the same settings object as the keyboard's.
    /// </summary>
    public Profile Own()
    {
        var p = own ??= new Profile();
        p.Palette = Palette; p.ColorSpace = ColorSpace; p.SaturationBoost = SaturationBoost; p.PeriodSeconds = PeriodSeconds;
        p.Direction = Direction; p.Wavelength = Math.Clamp(Wavelength * KeyPitch, 20, 5000); p.Brightness = 1; p.Effect = Effect;
        p.EffectBounds = new RectangleF(0, 0, (float)(Width * KeyPitch), (float)(Height * KeyPitch));
        return p;
    }
    public string Palette { get; set; } = "#FF2454, #683DFF, #00D9DF, #FFCA48";
    public ColorSpace ColorSpace { get; set; } = ColorSpace.Hsv;
    public double SaturationBoost { get; set; } = 1;
    public double PeriodSeconds { get; set; } = 6;
    /// <summary>Wavelength in keys (width F75 — 15).</summary>
    public double Wavelength { get; set; } = 15;
    public double Direction { get; set; }
    public double Brightness { get; set; } = 1;
    public int Width { get; set; } = 15;
    public int Height { get; set; } = 6;
    public uint[] Map { get; set; } = (uint[])AulaF75.Clone();

    public void Validate()
    {
        _ = Gradient.ParsePalette(Palette);
        if (Device.Length is 0 or > 256 || !Enum.IsDefined(ColorSpace) || !double.IsFinite(SaturationBoost) || SaturationBoost is < 0 or > 2 ||
            !double.IsFinite(PeriodSeconds) || PeriodSeconds is < 1 or > 60 || !double.IsFinite(Wavelength) || Wavelength is < 1 or > 500 ||
            !double.IsFinite(Direction) || !double.IsFinite(Brightness) || Brightness is < 0 or > 1 ||
            Width is < 1 or > 64 || Height is < 1 or > 64 || Map.Length != Width * Height)
            throw new InvalidDataException(L10n.T("Invalid keyboard settings."));
        Effect.Validate();
    }
}

public static class KeyboardLayout
{
    /// <summary>Key center of an LED in key units; LEDs outside the matrix are placed in a row below the keyboard.</summary>
    public static LedPoint Point(KeyboardSettings k, int led)
    {
        int cell = Array.IndexOf(k.Map, (uint)led);
        return cell >= 0 ? new(cell % k.Width + .5, cell / k.Width + .5) : new(led % k.Width + .5, k.Height + .5);
    }

    public static double Phase(KeyboardSettings k, LedPoint point, double seconds, double period)
    {
        var travel = Gradient.TravelDirection(k.Direction);
        return (point.X * travel.X + point.Y * travel.Y) / k.Wavelength - seconds / period;
    }

    /// <summary>
    /// Keyboard frame. caseSeconds is the case scene time (fan clock), localSeconds the keyboard's own clock.
    /// device = true — for OpenRGB (profile LED gamma); false gives screen colors for the preview.
    /// </summary>
    public static byte[] Frame(Profile p, int ledCount, double caseSeconds, double localSeconds, bool device)
    {
        var k = p.Keyboard;
        bool shared = k.UseCaseGradient;
        var source = Source(p);
        var stops = Effects.Palette(source);
        double seconds = shared ? caseSeconds : localSeconds;
        var bounds = Effects.Bounds(p);
        byte[] rgb = new byte[ledCount * 3];
        for (int led = 0; led < ledCount; led++)
        {
            var key = Point(k, led);
            // Follow mapping: the keys are stretched over the case canvas, so the keyboard shows a scaled-down copy.
            var point = shared ? new LedPoint(bounds.Left + key.X / k.Width * bounds.Width, bounds.Top + key.Y / k.Height * bounds.Height)
                : new LedPoint(key.X * KeyboardSettings.KeyPitch, key.Y * KeyboardSettings.KeyPitch);
            var c = Gradient.Shade(source, stops, point, seconds, source.ColorSpace, source.SaturationBoost, k.Brightness, false);
            if (device) c = Gradient.Device(p, c, false);
            rgb[led * 3] = c.R; rgb[led * 3 + 1] = c.G; rgb[led * 3 + 2] = c.B;
        }
        return rgb;
    }

    /// <summary>What drives the keyboard: the case profile (follow mapping) or its own lighting.</summary>
    public static Profile Source(Profile p) => p.Keyboard.UseCaseGradient ? p : p.Keyboard.Own();
    public static bool Matches(KeyboardSettings k, OpenRgbDevice device) => device.Name == k.Device;

    /// <summary>Takes the keyboard matrix from OpenRGB (one matrix zone for the whole device) so the preview matches the device.</summary>
    public static bool Adopt(KeyboardSettings k, IEnumerable<OpenRgbDevice> devices)
    {
        var device = devices.FirstOrDefault(d => Matches(k, d));
        if (device == null || device.Zones.Length != 1 || device.Zones[0] is not { Matrix: { } matrix, Width: > 0, Height: > 0 } zone || zone.LedCount != device.LedCount) return false;
        if (k.Width == zone.Width && k.Height == zone.Height && k.Map.SequenceEqual(matrix)) return false;
        k.Width = zone.Width; k.Height = zone.Height; k.Map = (uint[])matrix.Clone();
        return true;
    }

    public static int LedCount(KeyboardSettings k) => k.Map.Where(v => v != KeyboardSettings.NoLed).DefaultIfEmpty(0u).Max(v => (int)v + 1);
}
