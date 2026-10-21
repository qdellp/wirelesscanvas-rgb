using System.Text.Json;
using System.Globalization;
using System.Text.Json.Serialization;

namespace WirelessCanvas;

/// <summary>Element lighting: follows the shared mapping, a constant custom color, or off.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum LightMode { Mapping, Custom, Off }

public record FanPlacement
{
    public string Group { get; set; } = "";
    public int Fan { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    public double Rotation { get; set; }
    public double ProjectionY { get; set; } = 1;
    public double Scale { get; set; } = 1;
    public double Elevation { get; set; } = 90;
    public bool Enabled { get; set; } = true;
    public LightMode Light { get; set; }
    public string CustomColor { get; set; } = "#FFFFFF";
    /// <summary>Fan model (FanModel.Id); old profiles without the field are SL V3. Updated when groups are detected.</summary>
    public string Model { get; set; } = FanModel.SlV3.Id;
    [JsonIgnore] public FanModel ModelInfo => FanModel.FromId(Model) ?? FanModel.SlV3;
    [JsonIgnore] public string GroupLabel { get; set; } = "";
    [JsonIgnore] public int FanNumber => Fan + 1;
}

public sealed class Profile
{
    public int Version { get; set; } = 1;
    public string Palette { get; set; } = "#FF2454, #683DFF, #00D9DF, #FFCA48";
    public double PeriodSeconds { get; set; } = 6;
    public double Wavelength { get; set; } = 600;
    public double Direction { get; set; } = 0;
    public double Brightness { get; set; } = 0.5;
    public int Fps { get; set; } = 30;
    public ColorSpace ColorSpace { get; set; } = ColorSpace.Rgb;
    public double SaturationBoost { get; set; } = 1;
    /// <summary>LED gamma: the monitor shows colors on a ~2.2 curve while LEDs emit linearly. 1 means no correction.</summary>
    public double Gamma { get; set; } = 2.2;
    public List<FanPlacement> Fans { get; set; } = [];
    public Dictionary<string, List<LedPoint>> LedOverrides { get; set; } = [];
    public Dictionary<string, string> GroupNames { get; set; } = [];
    public string LayoutPreset { get; set; } = "";
    public bool ShowCanvasGradient { get; set; } = true;
    public LightingLayer InnerLayer { get; set; } = new();
    public LightingLayer OuterLayer { get; set; } = new();
    public PumpPlacement Pump { get; set; } = new();
    public bool OpenRgbOutput { get; set; }
    public bool OpenRgbAutoStart { get; set; }
    public List<ExternalPlacement> External { get; set; } = [];
    public KeyboardSettings Keyboard { get; set; } = new();
    public CoolingSettings Cooling { get; set; } = new();
    /// <summary>Mapping effect (shader) instead of the plain gradient, and the settings of each effect.</summary>
    public EffectSettings Effect { get; set; } = new();
    /// <summary>Effect canvas for non-case profiles (custom keyboard lighting), in mm; not saved.</summary>
    [System.Text.Json.Serialization.JsonIgnore] public RectangleF? EffectBounds { get; set; }
    /// <summary>LCD-screens: the pump (Id “pump”) and LCD fans (Id is the serial number USB).</summary>
    public List<ScreenSettings> Screens { get; set; } = [];
    public ScreenSettings Screen(string id, string name)
    {
        var screen = Screens.FirstOrDefault(s => s.Id == id);
        if (screen == null) { screen = new ScreenSettings { Id = id, Name = name }; Screens.Add(screen); }
        return screen;
    }
    public int GroupNumber(string mac) => Array.IndexOf(Fans.Select(f => f.Group).Distinct().OrderBy(g => g, StringComparer.Ordinal).ToArray(), mac) + 1;
    public int FanCount(string mac) => Fans.Count(f => f.Group == mac);
    public string GroupTitle(string mac) => $"G{GroupNumber(mac)}" + (GroupNames.TryGetValue(mac, out var name) && !string.IsNullOrWhiteSpace(name) ? $" — {name}" : "");
    public static JsonSerializerOptions Json { get; } = new() { WriteIndented = true };
    public void Validate()
    {
        if (Version != 1 || !double.IsFinite(PeriodSeconds) || PeriodSeconds is < 1 or > 60 ||
            !double.IsFinite(Wavelength) || Wavelength is < 20 or > 5000 || !double.IsFinite(Direction) ||
            !double.IsFinite(Brightness) || Brightness is < 0 or > 1 || Fps is < 1 or > 60 || Fans.Count > 40 ||
            !Enum.IsDefined(ColorSpace) || !double.IsFinite(SaturationBoost) || SaturationBoost is < 0 or > 2 || !double.IsFinite(Gamma) || Gamma is < 1 or > 3)
            throw new InvalidDataException(L10n.T("Invalid profile settings."));
        _ = Gradient.ParsePalette(Palette);
        InnerLayer.Validate(); OuterLayer.Validate();
        Pump.Validate();
        if (External.Count > 64) throw new InvalidDataException(L10n.T("Too many OpenRGB devices."));
        foreach (var device in External) device.Validate();
        Keyboard.Validate();
        Cooling.Validate();
        Effect.Validate();
        if (Screens.Count > 8 || Screens.GroupBy(x => x.Id).Any(g => g.Count() > 1)) throw new InvalidDataException(L10n.T("Screens: at most 8, no duplicates."));
        foreach (var screen in Screens) screen.Validate();
        if (GroupNames.Count > 40 || GroupNames.Any(p => string.IsNullOrWhiteSpace(p.Key) || p.Value == null || p.Value.Length > 48 || p.Value.Any(char.IsControl)))
            throw new InvalidDataException(L10n.T("Group names allow up to 48 characters with no line breaks."));
        if (Fans.GroupBy(f => (f.Group, f.Fan)).Any(g => g.Count() != 1) || Fans.Any(f =>
            string.IsNullOrWhiteSpace(f.Group) || f.Fan is < 0 or > 3 || !double.IsFinite(f.X) || !double.IsFinite(f.Y) ||
            Math.Abs(f.X) > 10000 || Math.Abs(f.Y) > 10000 || !double.IsFinite(f.Rotation) || !double.IsFinite(f.ProjectionY) || f.ProjectionY is < .2 or > 2 ||
            !double.IsFinite(f.Scale) || f.Scale is < .2 or > 4 || !double.IsFinite(f.Elevation) || f.Elevation is < 0 or > 90 ||
            !Enum.IsDefined(f.Light) || !ColorPickerDialog.TryParseHex(f.CustomColor, out _)))
            throw new InvalidDataException(L10n.T("Check fan coordinates and indices."));
        if (Fans.Any(f => FanModel.FromId(f.Model) == null)) throw new InvalidDataException(L10n.T("Unknown fan model in the profile."));
        foreach (var points in LedOverrides.Values)
            if (FanModel.All.All(m => m.LedsPerFan != points.Count) || points.Any(p => !double.IsFinite(p.X) || !double.IsFinite(p.Y) || Math.Abs(p.X) > 1000 || Math.Abs(p.Y) > 1000))
                throw new InvalidDataException(L10n.T("Calibration needs one point per fan LED."));
    }
    public void Save(string path)
    {
        Validate();
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(this, Json));
        File.Move(path + ".tmp", path, true);
    }
    public static Profile Load(string path)
    {
        if (new FileInfo(path).Length > 1_000_000) throw new InvalidDataException(L10n.T("Profile is too large."));
        var p = JsonSerializer.Deserialize<Profile>(File.ReadAllText(path), Json) ?? throw new InvalidDataException(L10n.T("Empty profile."));
        p.Cooling.Upgrade(); p.Validate(); CaseLayout.Upgrade(p); return p;
    }
}

public sealed class PumpPlacement
{
    public bool Enabled { get; set; } = true;
    public double X { get; set; } = 199;
    public double Y { get; set; } = 220;
    public double Diameter { get; set; } = 110;
    public double Rotation { get; set; }
    public double PhaseSeconds { get; set; }
    public LightMode Light { get; set; }
    public string CustomColor { get; set; } = "#FFFFFF";
    public LightingLayer Layer { get; set; } = new();
    public void Validate()
    {
        if (!double.IsFinite(X) || !double.IsFinite(Y) || Math.Abs(X) > 10000 || Math.Abs(Y) > 10000 ||
            !double.IsFinite(Diameter) || Diameter is < 20 or > 300 || !double.IsFinite(Rotation) ||
            !double.IsFinite(PhaseSeconds) || Math.Abs(PhaseSeconds) > 60 || !Enum.IsDefined(Light) || !ColorPickerDialog.TryParseHex(CustomColor, out _)) throw new InvalidDataException(L10n.T("Invalid pump geometry."));
        Layer.Validate();
    }
}

public sealed class LightingLayer
{
    public bool Enabled { get; set; } = true;
    public bool UseCanvasPalette { get; set; } = true;
    public string Palette { get; set; } = "#FF2454, #683DFF, #00D9DF, #FFCA48";
    public ColorSpace ColorSpace { get; set; } = ColorSpace.Rgb;
    public double SaturationBoost { get; set; } = 1;
    public double Brightness { get; set; } = 1;
    public void Validate()
    {
        _ = Gradient.ParsePalette(Palette);
        if (!Enum.IsDefined(ColorSpace) || !double.IsFinite(SaturationBoost) || SaturationBoost is < 0 or > 2 ||
            !double.IsFinite(Brightness) || Brightness is < 0 or > 1)
            throw new InvalidDataException(L10n.T("Invalid lighting layer settings."));
    }
}

public record LedPoint(double X, double Y);
public record ColorStop(double Position, Color Color);

public static class Gradient
{
    public static ColorStop[] ParsePalette(string value)
    {
        var tokens = value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length is < 2 or > 16) throw new InvalidDataException(L10n.T("Enter 2–16 colors separated by commas."));
        var result = tokens.Select((token, i) =>
        {
            var pair = token.Split('@');
            if (pair.Length > 2 || pair[0].Length != 7 || pair[0][0] != '#' || !int.TryParse(pair[0][1..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int rgb))
                throw new InvalidDataException(L10n.T("Color: #RRGGBB or #RRGGBB@position from 0 to 1."));
            double pos = pair.Length == 2 ? double.Parse(pair[1], CultureInfo.InvariantCulture) : (double)i / tokens.Length;
            if (!double.IsFinite(pos) || pos is < 0 or >= 1) throw new InvalidDataException(L10n.T("Color stop position must be at least 0 and less than 1."));
            return new ColorStop(pos, Color.FromArgb((rgb >> 16) & 255, (rgb >> 8) & 255, rgb & 255));
        }).OrderBy(x => x.Position).ToArray();
        if (result.Select(x => x.Position).Distinct().Count() != result.Length) throw new InvalidDataException(L10n.T("Color stop positions must differ."));
        return result;
    }
    /// <summary>SL V3 grid: banks 12+8+12+8 (upstream); physical coordinates need calibration.</summary>
    public static LedPoint LocalPoint(int led) => FanModel.SlV3.LocalPoint(led);
    public static LedPoint WorldPoint(Profile p, FanPlacement fan, int led)
    {
        var model = fan.ModelInfo;
        var local = p.LedOverrides.TryGetValue($"{fan.Group}/{fan.Fan}", out var points) && points.Count == model.LedsPerFan ? points[led] :
            model == FanModel.SlV3 && CaseLayout.IsPhoto(p) ? CaseLayout.LocalPoint(led) : model.LocalPoint(led);
        if (fan.Elevation < .01)
        {
            // Seen edge-on, the bars lie one behind the other in depth, not side by side on the canvas:
            // all LEDs sit on the center line along the length, so across it the fan changes color as a whole.
            local = new(model.LocalPoint(led).X, 0);
        }
        else local = new(local.X, local.Y * Math.Sin(fan.Elevation * Math.PI / 180));
        double rad = fan.Rotation * Math.PI / 180;
        return new LedPoint(fan.X + (local.X * Math.Cos(rad) - local.Y * Math.Sin(rad)) * fan.Scale, fan.Y + (local.X * Math.Sin(rad) + local.Y * Math.Cos(rad)) * fan.ProjectionY * fan.Scale);
    }
    public static RectangleF FanBounds(FanPlacement fan)
    {
        double a = fan.Rotation * Math.PI / 180;
        double halfHeight = Math.Max(14, 56 * Math.Sin(fan.Elevation * Math.PI / 180));
        float x = (float)((56 * Math.Abs(Math.Cos(a)) + halfHeight * Math.Abs(Math.Sin(a))) * fan.Scale);
        float y = (float)((56 * Math.Abs(Math.Sin(a)) + halfHeight * Math.Abs(Math.Cos(a))) * fan.Scale * fan.ProjectionY);
        return new((float)fan.X-x, (float)fan.Y-y, x*2, y*2);
    }
    public static LedPoint PumpPoint(PumpPlacement pump, int led)
    {
        double a = (led * 15 + pump.Rotation - 90) * Math.PI / 180, radius = pump.Diameter * .43;
        return new(pump.X + radius * Math.Cos(a), pump.Y + radius * Math.Sin(a));
    }
    /// <summary>Per-element lighting over the mapping: Off is black, Custom is a constant color with global brightness.</summary>
    public static Color Override(Profile p, LightMode mode, string custom, Func<Color> mapped, bool device, bool limitCurrent, double brightness = 1)
    {
        if (mode == LightMode.Mapping) return mapped();
        if (mode == LightMode.Off || !ColorPickerDialog.TryParseHex(custom, out var color)) return Color.Black;
        // Global brightness applies only to devices: the preview shows full-strength colors.
        int Scale(int value) => Math.Clamp((int)Math.Round(value * (device ? p.Brightness : 1) * brightness), 0, 255);
        var scaled = Color.FromArgb(Scale(color.R), Scale(color.G), Scale(color.B));
        return device ? Device(p, scaled, limitCurrent) : scaled;
    }
    public static Color FanLed(Profile p, FanPlacement fan, ColorStop[] canvas, ColorStop[] inner, ColorStop[] outer, int led, double seconds, bool device = false) =>
        Override(p, fan.Light, fan.CustomColor, () => LedColor(p, canvas, inner, outer, led, WorldPoint(p, fan, led), seconds, device, fan.ModelInfo), device, true);
    public static Color PumpColor(Profile p, ColorStop[] common, ColorStop[] own, LedPoint point, double seconds, bool device = false)
    {
        var layer = p.Pump.Layer;
        if (!p.Pump.Enabled) return Color.Black;
        if (p.Pump.Light != LightMode.Mapping) return Override(p, p.Pump.Light, p.Pump.CustomColor, () => Color.Black, device, true);
        if (!layer.Enabled) return Color.Black;
        var color = Shade(p, layer.UseCanvasPalette ? common : own, point, seconds + p.Pump.PhaseSeconds,
            layer.UseCanvasPalette ? p.ColorSpace : layer.ColorSpace, layer.UseCanvasPalette ? p.SaturationBoost : layer.SaturationBoost,
            (device ? p.Brightness : 1) * layer.Brightness, !device);
        return device ? Device(p, color, true) : color;
    }
    /// <summary>
    /// Screen color → LED value: gamma (so #FF1F1F does not turn pink and transitions
    /// look like the preview), then a channel-sum limit of 600 for fans and the pump.
    /// </summary>
    public static Color Device(Profile p, Color color, bool limitCurrent)
    {
        int Channel(int value) => p.Gamma == 1 ? value : (int)Math.Round(Math.Pow(value / 255.0, p.Gamma) * 255);
        int r = Channel(color.R), g = Channel(color.G), b = Channel(color.B), sum = r + g + b;
        if (limitCurrent && sum > 600) { r = r * 600 / sum; g = g * 600 / sum; b = b * 600 / sum; }
        return Color.FromArgb(r, g, b);
    }
    public static byte[] PumpFrame(Profile p, double seconds)
    {
        var common = Effects.Palette(p); var own = ParsePalette(p.Pump.Layer.Palette); byte[] frame = new byte[72];
        for (int led = 0; led < 24; led++)
        {
            var color = PumpColor(p, common, own, PumpPoint(p.Pump, led), seconds, true);
            frame[led*3] = color.R; frame[led*3+1] = color.G; frame[led*3+2] = color.B;
        }
        return frame;
    }
    public static bool IsInner(int led) => FanModel.SlV3.IsInner(led);
    public static LedPoint TravelDirection(double degrees)
    {
        double radians = degrees * Math.PI / 180;
        return new(Math.Cos(radians), Math.Sin(radians));
    }
    public static double Phase(Profile p, LedPoint point, double seconds)
    {
        var travel = TravelDirection(p.Direction);
        return (point.X * travel.X + point.Y * travel.Y) / p.Wavelength - seconds / p.PeriodSeconds;
    }
    public static Color LedColor(Profile p, ColorStop[] canvas, ColorStop[] inner, ColorStop[] outer, int led, LedPoint point, double seconds, bool device = false, FanModel? model = null)
    {
        bool isInner = (model ?? FanModel.SlV3).IsInner(led);
        var layer = isInner ? p.InnerLayer : p.OuterLayer;
        if (!layer.Enabled) return Color.Black;
        var stops = layer.UseCanvasPalette ? canvas : isInner ? inner : outer;
        var color = Shade(p, stops, point, seconds, layer.UseCanvasPalette ? p.ColorSpace : layer.ColorSpace,
            layer.UseCanvasPalette ? p.SaturationBoost : layer.SaturationBoost, (device ? p.Brightness : 1) * layer.Brightness, !device);
        return device ? Device(p, color, true) : color;
    }
    public static Color At(Profile p, ColorStop[] stops, LedPoint point, double seconds) =>
        Shade(p, stops, point, seconds, p.ColorSpace, p.SaturationBoost, p.Brightness, true);
    /// <summary>
    /// Color of a canvas point. Plain gradient: palette by wave phase (as before). Effect: the shader returns the position
    /// on the palette and brightness above the effect background; then global brightness and the current limit, as for the gradient.
    /// </summary>
    public static Color Shade(Profile p, ColorStop[] stops, LedPoint point, double seconds, ColorSpace space, double saturation, double brightness, bool limitCurrent)
    {
        if (!Effects.Active(p)) return Sample(stops, Phase(p, point, seconds), space, saturation, brightness, limitCurrent);
        var (u, v) = Effects.Evaluate(p, point, seconds);
        var color = Sample(stops, u, space, saturation); var back = Effects.Background(p);
        v = Math.Clamp(v, 0, 1);
        int Mix(int from, int to) => Math.Clamp((int)Math.Round((from + (to - from) * v) * brightness), 0, 255);
        int r = Mix(back.R, color.R), g = Mix(back.G, color.G), b = Mix(back.B, color.B), sum = r + g + b;
        if (limitCurrent && sum > 600) { r = r * 600 / sum; g = g * 600 / sum; b = b * 600 / sum; }
        return Color.FromArgb(r, g, b);
    }
    public static Color Sample(ColorStop[] stops, double phase, ColorSpace space = ColorSpace.Rgb, double saturation = 1, double brightness = 1, bool limitCurrent = false)
    {
        phase -= Math.Floor(phase);
        int right = Array.FindIndex(stops, x => x.Position > phase);
        if (right < 0) right = 0;
        int left = (right + stops.Length - 1) % stops.Length;
        double a = stops[left].Position, b = stops[right].Position;
        if (right == 0) b += 1;
        if (phase < a) phase += 1;
        double t = (phase - a) / (b - a);
        var first = stops[left].Color; var second = stops[right].Color;
        double red = first.R + (second.R - first.R) * t;
        double green = first.G + (second.G - first.G) * t;
        double blue = first.B + (second.B - first.B) * t;
        if (space == ColorSpace.Hsv)
        {
            var start = ColorMath.ToHsv(first); var end = ColorMath.ToHsv(second);
            if (start.Saturation < .00001) start = start with { Hue = end.Hue };
            if (end.Saturation < .00001) end = end with { Hue = start.Hue };
            double delta = end.Hue - start.Hue;
            if (delta > 180) delta -= 360;
            if (delta < -180) delta += 360;
            var mixed = ColorMath.FromHsv(start.Hue + delta * t, (start.Saturation + (end.Saturation - start.Saturation) * t) * saturation, start.Value + (end.Value - start.Value) * t);
            red = mixed.R; green = mixed.G; blue = mixed.B;
        }
        else if (saturation != 1)
        {
            var mixed = ColorMath.ToHsv(Color.FromArgb((int)Math.Round(red), (int)Math.Round(green), (int)Math.Round(blue)));
            var adjusted = ColorMath.FromHsv(mixed.Hue, mixed.Saturation * saturation, mixed.Value);
            red = adjusted.R; green = adjusted.G; blue = adjusted.B;
        }
        int Scale(double value) => Math.Clamp((int)Math.Round(value * brightness), 0, 255);
        int r = Scale(red), g = Scale(green), bl = Scale(blue);
        int sum = r + g + bl;
        if (limitCurrent && sum > 600) { r = r * 600 / sum; g = g * 600 / sum; bl = bl * 600 / sum; }
        return Color.FromArgb(r, g, bl);
    }
    /// <summary>Group frame for the device. model is the model reported by the controller; without it the layout model is used.</summary>
    public static byte[] Frame(Profile p, string group, int fanCount, double seconds, FanModel? model = null)
    {
        var stops = Effects.Palette(p);
        var inner = ParsePalette(p.InnerLayer.Palette); var outer = ParsePalette(p.OuterLayer.Palette);
        var placements = new FanPlacement[fanCount];
        for (int fan = 0; fan < fanCount; fan++)
        {
            var placement = p.Fans.SingleOrDefault(f => f.Group == group && f.Fan == fan);
            if (placement == null || !placement.Enabled) throw new InvalidDataException(L10n.T("Enable every fan of the selected group in the layout."));
            // LED count and layout follow the hardware: a stale model in the layout does not change the frame size.
            placements[fan] = model == null || placement.Model == model.Id ? placement : placement with { Model = model.Id };
        }
        int leds = (model ?? placements.FirstOrDefault()?.ModelInfo ?? FanModel.SlV3).LedsPerFan;
        if (placements.Any(f => f.ModelInfo.LedsPerFan != leds)) throw new InvalidDataException(L10n.T("The group contains fans of different models."));
        byte[] frame = new byte[fanCount * leds * 3];
        for (int fan = 0; fan < fanCount; fan++)
        {
            var placement = placements[fan];
            for (int led = 0; led < leds; led++)
            {
                var c = FanLed(p, placement, stops, inner, outer, led, seconds, true);
                int offset = (fan * leds + led) * 3;
                frame[offset] = c.R; frame[offset + 1] = c.G; frame[offset + 2] = c.B;
            }
        }
        return frame;
    }
}
