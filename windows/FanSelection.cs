namespace WirelessCanvas;

/// <summary>Selection uses hardware identities so UI refreshes cannot select unrelated objects.</summary>
internal sealed class FanSelection
{
    readonly HashSet<(string Group, int Fan)> selected = [];
    public bool Contains(FanPlacement fan) => selected.Contains((fan.Group, fan.Fan));
    public void Clear() => selected.Clear();
    public void Select(FanPlacement fan, bool extend)
    {
        var key = (fan.Group, fan.Fan);
        if (extend) { if (!selected.Add(key)) selected.Remove(key); }
        else if (!selected.Contains(key)) { selected.Clear(); selected.Add(key); }
    }
    /// <summary>Select every fan of a group (double-click, or pick the group on the Fans tab).</summary>
    public void SelectGroup(Profile profile, string group)
    {
        selected.Clear();
        foreach (var fan in profile.Fans.Where(f => f.Group == group)) selected.Add((fan.Group, fan.Fan));
    }
    public void Prune(Profile profile) => selected.RemoveWhere(key => !profile.Fans.Any(f => (f.Group, f.Fan) == key));
    public void Box(Profile profile, RectangleF rectangle, IEnumerable<FanPlacement> initial)
    {
        selected.Clear();
        foreach (var fan in initial.Concat(profile.Fans.Where(f => rectangle.IntersectsWith(Gradient.FanBounds(f))))) selected.Add((fan.Group, fan.Fan));
    }
    public List<FanPlacement> Members(Profile p) => p.Fans.Where(Contains).ToList();
}
