namespace WirelessCanvas;

/// <summary>
/// The reference case layout traced from a photo of the author's build: four SL Wireless groups of
/// 2 (right, LCD) + 1 (left) + 3 (bottom) + 3 (top) fans around a pump. Groups are recognised by fan count,
/// so the layout works with any controller whose groups have that shape.
/// </summary>
public static class CaseLayout
{
    public const string Id = "reference-case-v7";
    /// <summary>Layout ids written by earlier releases end with "-case-photo-v1" … "-case-photo-v6".</summary>
    static int LegacyVersion(Profile p) =>
        p.LayoutPreset is { } id && id.Length > 1 && id[..^1].EndsWith("-case-photo-v") && id[^1] is >= '1' and <= '6' ? id[^1] - '0' : 0;
    public static bool IsPhoto(Profile p) => p.LayoutPreset == Id || LegacyVersion(p) > 0;
    /// <summary>Placeholder addresses for the preview shown before any controller is connected.</summary>
    public static readonly string[] DemoMacs = ["D0D0D1E566E1", "D0D0D27A4EE0", "D0D0D36232E1", "D0D0D47A4EE0"];
    static readonly int[] Counts = [2, 1, 3, 3];
    static readonly string[] Names = [L10n.T("Right · LCD"), L10n.T("Left"), L10n.T("Bottom"), L10n.T("Top")];
    // Light guide of one bank: chamfer → straight run along the edge → chamfer (half of an octagonal frame).
    static readonly LedPoint[] Bar = [new(-47, 32), new(-32, 47), new(32, 47), new(47, 32)];

    /// <summary>
    /// Group MACs in layout order (right, left, bottom, top), or null when the profile does not have that shape.
    /// The two three-fan rows are told apart by their current height; on a fresh profile the first one listed is the bottom row.
    /// </summary>
    public static string[]? GroupOrder(Profile p)
    {
        var groups = p.Fans.GroupBy(f => f.Group).ToArray();
        if (p.Fans.Count != 9 || groups.Length != 4 ||
            groups.Any(g => !g.Select(f => f.Fan).Order().SequenceEqual(Enumerable.Range(0, g.Count())))) return null;
        var right = groups.Where(g => g.Count() == 2).ToArray();
        var left = groups.Where(g => g.Count() == 1).ToArray();
        var rows = groups.Where(g => g.Count() == 3).Select((g, i) => (g.Key, Y: g.Average(f => f.Y), Index: i))
            .OrderByDescending(r => r.Y).ThenBy(r => r.Index).ToArray();
        if (right.Length != 1 || left.Length != 1 || rows.Length != 2) return null;
        return [right[0].Key, left[0].Key, rows[0].Key, rows[1].Key];
    }
    public static bool Matches(Profile p) => GroupOrder(p) != null;
    public static bool Apply(Profile p)
    {
        if (GroupOrder(p) is not { } macs) return false;
        p.LayoutPreset = Id;
        // Reference photo 1108×996 px → canvas: x·0.6−24, y·0.6.
        p.Pump.X = 199; p.Pump.Y = 221; p.Pump.Diameter = 111;
        foreach (var f in p.Fans)
        {
            int group = Array.IndexOf(macs, f.Group);
            f.X = group switch { 0 => 521, 1 => 35, _ => 105 + f.Fan * 173 };
            f.Y = group switch { 0 => RightY(f.Fan), 1 => 209, 2 => 526, _ => 79 };
            f.Rotation = group switch { 0 => RightRotation, 1 => 90, _ => 0 };
            // Elevation: 0° — seen edge-on (top, left), 90° — seen face-on (right),
            // 30° — the bottom row is tilted towards the viewer: visible face height ≈ sin 30°.
            f.ProjectionY = group switch { 1 => 1.12, 2 => .875, _ => 1 }; f.Scale = 1.5; f.Elevation = group switch { 0 => 90, 2 => 30, _ => 0 };
            p.GroupNames[f.Group] = Names[group];
        }
        CloseGroupGaps(p);
        return true;
    }
    // Hardware video 0.1.7: on the right fans bank 0 is the left bar, bank 1 the right one,
    // and LED indices grow from bottom to top. That is a −90° rotation, not +90°.
    const double RightRotation = -90;
    // Check in 0.2.0: fan 1 of the right group sits at the bottom, fan 2 at the top.
    static double RightY(int fan) => fan == 0 ? 378 : 199;
    public static bool Upgrade(Profile p)
    {
        int version = LegacyVersion(p);
        if (version == 0) return false;
        if (version <= 2) return Apply(p);
        if (GroupOrder(p) is not { } macs) return false;
        // v3 → v4: bar mask instead of a ring; right fans are turned around, user positions are kept.
        if (version == 3)
            foreach (var f in p.Fans.Where(f => f.Group == macs[0] && f.Rotation == 90)) f.Rotation = RightRotation;
        // v4 → v5: right fan 1 at the bottom, fan 2 at the top; X and the other parameters are unchanged.
        if (version <= 4)
            foreach (var f in p.Fans.Where(f => f.Group == macs[0])) f.Y = RightY(f.Fan);
        // v5 → v6: fans inside a group touch each other. v6 → v7: only the id changed.
        if (version <= 5) CloseGroupGaps(p);
        p.LayoutPreset = Id; return true;
    }
    /// <summary>Join projected masks in each row while preserving its center and physical fan order.</summary>
    internal static void CloseGroupGaps(Profile p)
    {
        foreach (var group in p.Fans.GroupBy(f => f.Group).Where(g => g.Count() > 1))
        {
            bool horizontal = group.Max(f => f.X) - group.Min(f => f.X) >= group.Max(f => f.Y) - group.Min(f => f.Y);
            var ordered = group.OrderBy(f => horizontal ? f.X : f.Y).ThenBy(f => f.Fan).ToArray();
            var bounds = ordered.Select(Gradient.FanBounds).ToArray();
            double start = horizontal ? bounds.Min(b => b.Left) : bounds.Min(b => b.Top);
            double end = horizontal ? bounds.Max(b => b.Right) : bounds.Max(b => b.Bottom);
            double total = bounds.Sum(b => horizontal ? (double)b.Width : b.Height);
            double cursor = (start + end - total) / 2;
            for (int i = 0; i < ordered.Length; i++)
            {
                double size = horizontal ? bounds[i].Width : bounds[i].Height;
                if (horizontal) ordered[i].X = cursor + size / 2; else ordered[i].Y = cursor + size / 2;
                cursor += size;
            }
        }
    }
    public static void Populate(Profile p)
    {
        p.Fans = DemoMacs.SelectMany((mac, i) => Enumerable.Range(0, Counts[i]).Select(f => new FanPlacement { Group = mac, Fan = f })).ToList();
        Apply(p);
    }
    public static LedPoint LocalPoint(int led)
    {
        // Confirmed by upstream and on video: banks of 12 inner + 8 outer LEDs, twice; on all four
        // tracks the index grows in the same direction (+X). Bank 0 is the −Y edge, bank 1 the +Y edge.
        int bank = led / 20, index = led % 20; double side = bank == 0 ? -1 : 1;
        if (index >= 12) return new((index - 12) / 7.0 * 100 - 50, side * 55);
        double[] lengths = [.. Bar.Zip(Bar.Skip(1), (a, b) => Math.Sqrt(Math.Pow(b.X-a.X, 2) + Math.Pow(b.Y-a.Y, 2)))];
        double distance = index / 11.0 * lengths.Sum();
        int segment = 0;
        while (segment < lengths.Length - 1 && distance > lengths[segment]) distance -= lengths[segment++];
        double t = distance / lengths[segment];
        var (from, to) = (Bar[segment], Bar[segment + 1]);
        return new(from.X + (to.X - from.X) * t, side * (from.Y + (to.Y - from.Y) * t));
    }
}
