namespace WirelessCanvas;

internal sealed record RadioRepairPlan(string Mac, byte Slot);

/// <summary>Pure policy: persistent collisions only, own supported fans, bounded retries and unused slots.</summary>
internal sealed class RadioRecoveryPolicy
{
    internal const long SettleMilliseconds = 2000, RetryMilliseconds = 60_000;
    readonly Dictionary<string, long> collisions = [];
    readonly Dictionary<string, byte> stableSlots = [];
    readonly Dictionary<string, (long At, int Count)> attempts = [];
    int totalAttempts;
    public static bool HasConflicts(IReadOnlyList<WirelessGroup> groups) => groups.GroupBy(g => g.Slot).Any(g => g.Count() > 1);
    public void Observe(IReadOnlyList<WirelessGroup> groups, IReadOnlySet<string> confirmed, long now)
    {
        var current = new HashSet<string>();
        foreach (var cluster in groups.GroupBy(g => g.Slot))
        {
            if (cluster.Count() == 1)
            {
                var group = cluster.Single();
                if (confirmed.Contains(group.Mac) && group.Slot is >= 1 and < 14) stableSlots[group.Mac] = group.Slot;
                continue;
            }
            foreach (var group in cluster)
            {
                string key = CollisionKey(group); current.Add(key);
                collisions.TryAdd(key, now);
            }
        }
        foreach (string stale in collisions.Keys.Except(current).ToArray()) collisions.Remove(stale);
    }
    static string CollisionKey(WirelessGroup g) => $"{g.Mac}/{g.Master}/{g.Channel}/{g.Slot}";
    public RadioRepairPlan? Plan(IReadOnlyList<WirelessGroup> groups, string master, IReadOnlyCollection<byte> occupied, IReadOnlySet<string> confirmed, long now)
    {
        if (totalAttempts >= 6 || groups.Select(g => g.Mac).Distinct().Count() != groups.Count) return null;
        foreach (var cluster in groups.Where(g => g.Master == master).GroupBy(g => g.Slot).Where(g => g.Count() > 1).OrderBy(g => g.Key))
        {
            // Keep pumps/unknown types untouched, otherwise keep the stable or lowest-MAC member.
            var ordered = cluster.OrderBy(g => g.RepairSupported ? 1 : 0)
                .ThenBy(g => stableSlots.TryGetValue(g.Mac, out byte previous) && previous == g.Slot ? 0 : 1)
                .ThenBy(g => g.Mac, StringComparer.Ordinal).ToArray();
            foreach (var group in ordered.Skip(1))
            {
                if (!group.RepairSupported || group.Master != master || !confirmed.Contains(group.Mac) || group.Channel is < 1 or > 39 || group.SlotIndex is < 1 or > 255 ||
                    !collisions.TryGetValue(CollisionKey(group), out long since) || now - since < SettleMilliseconds) continue;
                if (attempts.TryGetValue(group.Mac, out var tried) && (tried.Count >= 2 || now - tried.At < RetryMilliseconds)) continue;
                bool Free(byte slot) => slot is >= 1 and < 14 && !occupied.Contains(slot) && groups.All(g => g.Slot != slot);
                byte target = stableSlots.TryGetValue(group.Mac, out byte oldSlot) && Free(oldSlot) ? oldSlot : (byte)Enumerable.Range(1, 13).FirstOrDefault(s => Free((byte)s));
                if (target != 0) return new(group.Mac, target);
            }
        }
        return null;
    }
    public void MarkAttempt(string mac, long now)
    {
        attempts[mac] = (now, attempts.GetValueOrDefault(mac).Count + 1); totalAttempts++;
    }
}
