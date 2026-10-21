namespace WirelessCanvas;

/// <summary>L-Wireless fan families (upstream wireless/fan_type.rs). LCD and LED variants of a family light up the same way.</summary>
public enum FanFamily { SlV3, SlV4, SlInf, SlInfV3, Tl, TlV3, Cl, ClV2, P28 }

/// <summary>
/// Fan model: LED count, lower PWM limit and LED positions on the front face.
/// LED order in a frame follows upstream lianli-media (sl_wireless, sl_v4, sl_inf, tl, cl, p28).
/// Coordinates in the same units as SL V3: frame ±56, fan center at — 0.
/// Only SL V3 is verified on hardware; layouts of the other families come from upstream and are not calibrated.
/// </summary>
public sealed class FanModel
{
    public FanFamily Family { get; }
    public string Id { get; }
    public string Name { get; }
    public int LedsPerFan { get; }
    /// <summary>Lower duty-cycle limit, % (upstream min_duty_percent).</summary>
    public int MinDutyPercent { get; }
    /// <summary>Lower limit as PWM 0–255, fraction truncated, as in upstream apply_pwm_constraints.</summary>
    public byte MinimumPwm => (byte)(MinDutyPercent * 255 / 100);
    /// <summary>CL firmware does not accept PWM 153–155: 153/154 → 152, 155 → 156.</summary>
    public bool ClPwmFilter => Family is FanFamily.Cl or FanFamily.ClV2;
    /// <summary>Verified on the author's hardware (radio address recovery is allowed only for these).</summary>
    public bool HardwareVerified => Family == FanFamily.SlV3;
    readonly LedPoint[] points;
    readonly bool[] inner;
    readonly bool[] linked;

    FanModel(FanFamily family, string id, string name, int minDuty, (LedPoint Point, bool Inner, bool Linked)[] leds)
    {
        Family = family; Id = id; Name = name; MinDutyPercent = minDuty; LedsPerFan = leds.Length;
        points = [.. leds.Select(l => l.Point)]; inner = [.. leds.Select(l => l.Inner)]; linked = [.. leds.Select(l => l.Linked)];
    }

    public LedPoint LocalPoint(int led) => points[led];
    /// <summary>LED belongs to the inner layer (hub, inner bars); the rest belong to the outer layer.</summary>
    public bool IsInner(int led) => inner[led];
    /// <summary>LED and the next one lie on one straight track: they can be joined by a line on the canvas.</summary>
    public bool ConnectsToNext(int led) => linked[led];

    // Layout data is declared before the models: static fields are initialized in order.
    // SL light guide: chamfer → straight run along the edge → chamfer (half of an octagonal frame).
    static readonly LedPoint[] Bar = [new(-47, 32), new(-32, 47), new(32, 47), new(47, 32)];
    // upstream cl::engine::CENTER_ORDER (one-based) and sl_inf::center_order PN0 go around the hub.
    static readonly int[] ClCenter = [1, 2, 0, 3, 7, 4, 6, 5];
    static readonly int[] InfCenter = [2, 3, 1, 4, 0, 5, 7, 6];
    // upstream tl::engine::LED_POINTS: upper arc 0–12 and lower arc 13–25 in the frame 187×166.
    static readonly (int X, int Y)[] TlPoints =
    [
        (0, 63), (13, 46), (27, 29), (44, 17), (61, 7), (77, 0), (94, 0), (111, 0), (127, 7), (144, 17), (161, 29), (173, 46), (187, 63),
        (0, 99), (13, 116), (27, 133), (44, 146), (61, 156), (77, 166), (94, 166), (111, 166), (127, 156), (144, 146), (161, 133), (173, 116), (187, 99),
    ];

    public static readonly FanModel SlV3 = new(FanFamily.SlV3, "slv3", "SL V3 Wireless", 14, Banks(inner: Grid(12, 38), outer: Grid(8, 50)));
    public static readonly FanModel SlV4 = new(FanFamily.SlV4, "slv4", "SL V4 Wireless", 14, Banks(inner: Along(Bar, 13), outer: Grid(13, 55)));
    public static readonly FanModel SlInf = new(FanFamily.SlInf, "slinf", "SL-INF Wireless", 11, Infinity());
    public static readonly FanModel SlInfV3 = new(FanFamily.SlInfV3, "slinfv3", "SL-INF V3 Wireless", 10, Infinity());
    public static readonly FanModel Tl = new(FanFamily.Tl, "tl", "TL Wireless", 11, Arcs());
    public static readonly FanModel TlV3 = new(FanFamily.TlV3, "tlv3", "TL V3 Wireless", 11, Arcs());
    public static readonly FanModel Cl = new(FanFamily.Cl, "cl", "CL Wireless", 10, Rings());
    public static readonly FanModel ClV2 = new(FanFamily.ClV2, "clv2", "CL V2 Wireless", 10, Rings());
    public static readonly FanModel P28 = new(FanFamily.P28, "p28", "P28 V2 Wireless", 8, Hub());
    public static readonly FanModel[] All = [SlV3, SlV4, SlInf, SlInfV3, Tl, TlV3, Cl, ClV2, P28];

    public static FanModel? FromId(string? id) => All.FirstOrDefault(m => m.Id == id);
    /// <summary>Fan type byte from the device record (bytes 24–27), ranges upstream from_fan_type_byte.</summary>
    public static FanModel? FromTypeByte(byte b) => b switch
    {
        >= 20 and <= 26 => SlV3,
        >= 27 and <= 35 => Tl,
        >= 36 and <= 39 => SlInf,
        >= 40 and <= 42 => Cl,
        >= 43 and <= 50 => SlInfV3,
        >= 51 and <= 58 => TlV3,
        >= 59 and <= 62 => SlV4,
        63 => P28,
        126 or 127 => ClV2,
        _ => null,
    };
    /// <summary>The fan has its own LCD (connected by a separate USB).</summary>
    public static bool HasLcd(byte b) => b is >= 23 and <= 26 or 27 or >= 32 and <= 35 or 43 or 44 or 47 or 48 or 51 or 52 or 55 or 56;
    /// <summary>Group model: all used slots belong to one family, otherwise null (mixed groups are left alone).</summary>
    public static FanModel? Of(byte[] types, int fans)
    {
        if (fans is < 1 or > 4 || types.Length < fans) return null;
        var models = types.Take(fans).Select(FromTypeByte).ToArray();
        return models[0] != null && models.All(m => m == models[0]) ? models[0] : null;
    }

    // ── Layouts ──────────────────────────────────────────────────────────

    static LedPoint[] Grid(int count, double y) => [.. Enumerable.Range(0, count).Select(i => new LedPoint((i / (double)(count - 1) - .5) * 100, y))];
    static LedPoint[] Along(LedPoint[] path, int count)
    {
        double[] lengths = [.. path.Zip(path.Skip(1), (a, b) => Math.Sqrt(Math.Pow(b.X - a.X, 2) + Math.Pow(b.Y - a.Y, 2)))];
        return [.. Enumerable.Range(0, count).Select(i =>
        {
            double distance = i / (double)(count - 1) * lengths.Sum(); int segment = 0;
            while (segment < lengths.Length - 1 && distance > lengths[segment]) distance -= lengths[segment++];
            double t = distance / lengths[segment]; var (from, to) = (path[segment], path[segment + 1]);
            return new LedPoint(from.X + (to.X - from.X) * t, from.Y + (to.Y - from.Y) * t);
        })];
    }
    static LedPoint Mirror(LedPoint p, double side) => new(p.X, p.Y * side);
    static IEnumerable<(LedPoint, bool, bool)> Track(IEnumerable<LedPoint> track, bool isInner, bool linked = true)
    {
        var list = track.ToArray();
        return list.Select((p, i) => (p, isInner, linked && i < list.Length - 1));
    }
    /// <summary>SL V3 / SL V4: bank 0 (−Y edge): inner, then outer; bank 1 (+Y edge): the same.</summary>
    static (LedPoint, bool, bool)[] Banks(LedPoint[] inner, LedPoint[] outer) =>
        [.. new[] { -1.0, 1.0 }.SelectMany(side => Track(inner.Select(p => Mirror(p, side)), true).Concat(Track(outer.Select(p => Mirror(p, side)), false)))];
    /// <summary>Ring of 8 LEDs: position k clockwise from the top is taken by LED order[k].</summary>
    static LedPoint[] Ring(int[] order, double radius)
    {
        var result = new LedPoint[order.Length];
        for (int k = 0; k < order.Length; k++)
        {
            double a = (-90 + k * 360.0 / order.Length) * Math.PI / 180;
            result[order[k]] = new(radius * Math.Cos(a), radius * Math.Sin(a));
        }
        return result;
    }
    /// <summary>SL-INF: hub 0–7, inner 8–17, outer 18–25, inner 26–35, outer 36–43 (sl_inf::place).</summary>
    static (LedPoint, bool, bool)[] Infinity() =>
    [
        .. Track(Ring(InfCenter, 18), true, false),
        .. Track(Along(Bar, 10).Select(p => Mirror(p, -1)), true), .. Track(Grid(8, -55), false),
        .. Track(Along(Bar, 10), true), .. Track(Grid(8, 55), false),
    ];
    /// <summary>TL: two arcs on the frame ends form the outer layer.</summary>
    static (LedPoint, bool, bool)[] Arcs()
    {
        var all = TlPoints.Select(p => new LedPoint((p.X - 93.5) / 93.5 * 50, (p.Y - 83) / 83.0 * 52)).ToArray();
        return [.. Track(all[..13], false), .. Track(all[13..], false)];
    }
    /// <summary>CL: hub 0–7, outer ring 8–15 (reversed order) and its second bank 16–23 at the same positions (cl::place).</summary>
    static (LedPoint, bool, bool)[] Rings()
    {
        var outer = Ring([7, 6, 5, 4, 3, 2, 1, 0], 48);
        return [.. Track(Ring(ClCenter, 20), true, false), .. Track(outer, false, false), .. Track(outer, false, false)];
    }
    /// <summary>P28: hub ring as on CL, plus a ninth LED with the eighth's color (cl::project_p28) in the center.</summary>
    static (LedPoint, bool, bool)[] Hub() => [.. Track(Ring(ClCenter, 30), true, false), (new LedPoint(0, 0), true, false)];
}
