using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;

namespace WirelessCanvas;

internal static class SelfTests
{
    static readonly List<string> results = [];
    static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    static void Reject(Action action) { try { action(); } catch (Exception) { return; } throw new Exception("Invalid data was accepted."); }
    static void Test(string name, Action body)
    {
        try { body(); results.Add("PASS: " + name); }
        catch (Exception e) { results.Add("FAIL: " + name + ": " + e); }
    }
    public static int Run()
    {
        Test("Fans without gaps: horizontal and vertical groups touch", () =>
        {
            var p = new Profile(); CaseLayout.Populate(p); p.Validate();
            foreach (var group in p.Fans.GroupBy(f => f.Group).Where(g => g.Count() > 1))
            {
                bool horizontal = group.Max(f => f.X) - group.Min(f => f.X) > group.Max(f => f.Y) - group.Min(f => f.Y);
                var bounds = group.OrderBy(f => horizontal ? f.X : f.Y).Select(Gradient.FanBounds).ToArray();
                for (int i = 1; i < bounds.Length; i++) Check(Math.Abs(horizontal ? bounds[i].Left - bounds[i - 1].Right : bounds[i].Top - bounds[i - 1].Bottom) < .0001, "Adjacent fan masks have a gap or overlap");
            }
            Check(p.Fans[0].Y > p.Fans[1].Y && p.Fans[3].X < p.Fans[4].X && p.Fans[4].X < p.Fans[5].X, "Physical fan order changed");
            Check(p.Fans[2].X == 35 && p.Fans[2].Y == 209 && p.Pump.X == 199 && p.Pump.Y == 221, "Single fan or pump moved");
        });
        Test("Fans without gaps: migration keeps group centers and settings and runs once", () =>
        {
            var p = new Profile { Palette = "#FF0000, #00FFFF", Direction = 37 }; CaseLayout.Populate(p);
            p.LayoutPreset = "old-case-photo-v5";
            foreach (var fan in p.Fans)
            {
                fan.X += 40; fan.Y += 20;
                if (fan.Group == CaseLayout.DemoMacs[0]) fan.Y += fan.Fan == 0 ? 5.5 : -5.5;
                if (fan.Group == CaseLayout.DemoMacs[2] || fan.Group == CaseLayout.DemoMacs[3]) fan.X += (fan.Fan - 1) * 5;
            }
            p.GroupNames[CaseLayout.DemoMacs[0]] = "My group";
            p.InnerLayer.Enabled = false; p.Fans[0].Enabled = false;
            var beforeFrame = Gradient.Frame(p, CaseLayout.DemoMacs[2], 3, .37);
            var centers = p.Fans.GroupBy(f => f.Group).ToDictionary(g => g.Key, g =>
            {
                var b = g.Select(Gradient.FanBounds).ToArray(); return ((b.Min(r => r.Left) + b.Max(r => r.Right)) / 2, (b.Min(r => r.Top) + b.Max(r => r.Bottom)) / 2);
            });
            string path = Path.Combine(Path.GetTempPath(), "wcrgb-no-gaps-" + Guid.NewGuid() + ".json");
            try
            {
                p.Save(path); var migrated = Profile.Load(path);
                Check(migrated.LayoutPreset == CaseLayout.Id && migrated.GroupNames[CaseLayout.DemoMacs[0]] == "My group" && !migrated.InnerLayer.Enabled && !migrated.Fans[0].Enabled && migrated.Direction == 37 && migrated.Palette == p.Palette, "Migration lost lighting settings");
                foreach (var group in migrated.Fans.GroupBy(f => f.Group))
                {
                    var b = group.Select(Gradient.FanBounds).ToArray(); var previous = centers[group.Key];
                    Check(Math.Abs((b.Min(r => r.Left) + b.Max(r => r.Right)) / 2 - previous.Item1) < .001 && Math.Abs((b.Min(r => r.Top) + b.Max(r => r.Bottom)) / 2 - previous.Item2) < .001, "Migration moved the group center");
                }
                Check(!beforeFrame.SequenceEqual(Gradient.Frame(migrated, CaseLayout.DemoMacs[2], 3, .37)), "USB RGB scene still uses the old spaced coordinates");
                migrated.Fans[3].X += 17; migrated.Save(path); var again = Profile.Load(path);
                Check(again.Fans[3].X == migrated.Fans[3].X && !CaseLayout.Upgrade(again), "Migration reapplied on every load");
            }
            finally { if (File.Exists(path)) File.Delete(path); }
        });
        Test("Auto recovery: a persistent G2/G3 conflict restores the previous free slot G3", () =>
        {
            const string master = "D0D0D06232DC";
            var left = new WirelessGroup("D0D0D27A4EE0", master, 8, 9, 1, [20, 0, 0, 0], [0, 0, 0, 0], false, 0) { SlotIndex = 3 };
            var bottom = new WirelessGroup("D0D0D36232E1", master, 8, 10, 3, [21, 21, 21, 0], [0, 0, 0, 0], false, 0) { SlotIndex = 4 };
            var confirmed = new HashSet<string> { left.Mac, bottom.Mac };
            var policy = new RadioRecoveryPolicy();
            policy.Observe([left, bottom], confirmed, 0);
            bottom = bottom with { Slot = 9 };
            policy.Observe([left, bottom], confirmed, 100);
            Check(policy.Plan([left, bottom], master, new byte[] { 2, 6, 7, 9 }, confirmed, 2099) == null, "Transient collision triggered repair");
            var plan = policy.Plan([left, bottom], master, new byte[] { 2, 6, 7, 9 }, confirmed, 2100);
            Check(plan is { Mac: "D0D0D36232E1", Slot: 10 }, "Wrong group or old free slot");
            Check(policy.Plan([left, bottom], master, new byte[] { 2, 6, 7, 9 }, new HashSet<string>(), 5000) == null, "Unconfirmed address was accepted");
            bottom = bottom with { Slot = 10 }; policy.Observe([left, bottom], confirmed, 6000);
            Check(policy.Plan([left, bottom], master, new byte[] { 2, 6, 7, 9, 10 }, confirmed, 7000) == null, "Successful repair triggered another write");
        });
        Test("Auto recovery: safe free address, foreign and unsupported devices", () =>
        {
            const string master = "D0D0D06232DC";
            var a = new WirelessGroup("010203040506", master, 8, 9, 1, [20, 0, 0, 0], [0, 0, 0, 0], false, 0) { SlotIndex = 1 };
            var b = a with { Mac = "020304050607", SlotIndex = 2 };
            var confirmed = new HashSet<string> { a.Mac, b.Mac };
            var policy = new RadioRecoveryPolicy(); policy.Observe([a, b], confirmed, 0);
            var plan = policy.Plan([b, a], master, new byte[] { 1, 2, 3, 4, 9 }, confirmed, 2000);
            Check(plan is { Mac: "020304050607", Slot: 5 }, "Occupied slots or deterministic keeper ignored");
            Check(policy.Plan([a, b], master, Enumerable.Range(1, 13).Select(i => (byte)i).ToArray(), confirmed, 2000) == null, "No free slot was accepted");
            Check(policy.Plan([a, b with { Master = "000000000000" }], master, [], confirmed, 2000) == null, "Unbound fan was automatically paired");
            Check(policy.Plan([a, b with { Master = "AABBCCDDEEFF" }], master, [], confirmed, 2000) == null, "Foreign fan was rebound");
            var unknown = a with { Types = [36, 0, 0, 0] }; var pump = b with { DeviceType = 10, Fans = 0 };
            Check(policy.Plan([unknown, pump], master, [], confirmed, 2000) == null, "Pump or unsupported layout was rebound");
            Check(policy.Plan([a, a], master, [], confirmed, 2000) == null, "Duplicate MAC was accepted");
        });
        Test("Auto recovery: pause after an error, attempt limit and a conflict that went away", () =>
        {
            const string master = "D0D0D06232DC";
            var a = new WirelessGroup("010203040506", master, 8, 9, 1, [20, 0, 0, 0], [0, 0, 0, 0], false, 0) { SlotIndex = 1 };
            var b = a with { Mac = "020304050607", SlotIndex = 2 };
            var confirmed = new HashSet<string> { a.Mac, b.Mac };
            var policy = new RadioRecoveryPolicy(); policy.Observe([a, b], confirmed, 0);
            var first = policy.Plan([a, b], master, [], confirmed, 2000)!; policy.MarkAttempt(first.Mac, 2000);
            Check(policy.Plan([a, b], master, [], confirmed, 61999) == null, "Failure retry is not throttled");
            var second = policy.Plan([a, b], master, [], confirmed, 62000)!; policy.MarkAttempt(second.Mac, 62000);
            Check(policy.Plan([a, b], master, [], confirmed, 1_000_000) == null, "Per-group attempt limit exceeded");
            var transient = new RadioRecoveryPolicy(); transient.Observe([a, b], confirmed, 0);
            transient.Observe([a, b with { Slot = 10 }], confirmed, 1000);
            transient.Observe([a, b], confirmed, 2000);
            Check(transient.Plan([a, b], master, [], confirmed, 3000) == null, "Old collision time survived resolution");
        });
        Test("Auto recovery: shared attempt limit and disable switch for diagnostics", () =>
        {
            using var controller = new Controller();
            Check(!controller.AutoRepairConflicts, "Read-only controller enables automatic repair");
            var policy = new RadioRecoveryPolicy();
            for (int i = 0; i < 6; i++) policy.MarkAttempt(i.ToString(), i);
            var a = new WirelessGroup("010203040506", "D0D0D06232DC", 8, 9, 1, [20, 0, 0, 0], [0, 0, 0, 0], false, 0) { SlotIndex = 1 };
            var b = a with { Mac = "020304050607", SlotIndex = 2 };
            var confirmed = new HashSet<string> { a.Mac, b.Mac }; policy.Observe([a, b], confirmed, 0);
            Check(policy.Plan([a, b], a.Master, [], confirmed, 10_000) == null, "Total repair budget exceeded");
        });
        Test("Radio slot recovery: exact G3 address, free RX, safe PWM, SaveConfig as upstream", () =>
        {
            var group = new WirelessGroup("D0D0D36232E1", "D0D0D06232DC", 8, 9, 3, [21, 21, 21, 0], [0, 0, 0, 0], false, 0) { SlotIndex = 4, Pwm = [0, 80, 120, 0] };
            byte[] rf = Controller.RadioRepairPacket(group, group.Master, 8, 10, new byte[] { 2, 6, 7, 9 });
            Check(rf.Length == 240 && rf[0] == 0x12 && rf[1] == 0x10 && Convert.ToHexString(rf.AsSpan(2, 6)) == group.Mac && Convert.ToHexString(rf.AsSpan(8, 6)) == group.Master, "Wrong repair command or target MAC");
            Check(rf.AsSpan(14, 7).SequenceEqual(new byte[] { 10, 8, 4, 255, 80, 120, 0 }) && rf.AsSpan(21).IndexOfAnyExcept((byte)0) < 0, "Wrong bind layout or unsafe fan PWM");
            Check(group.Slot == 9 && group.Pwm[0] == 0, "Repair builder mutated discovery state");
            byte[] save = Controller.RadioSavePacket(group.Master);
            Check(save.Length == 240 && save[0] == 0x12 && save[1] == 0x15 && save.AsSpan(2, 6).IndexOfAnyExcept((byte)0xFF) < 0 && Convert.ToHexString(save.AsSpan(8, 6)) == group.Master && save[14] == 0xFF && save.AsSpan(15).IndexOfAnyExcept((byte)0) < 0, "SaveConfig differs from upstream");
        });
        Test("Radio slot recovery: taken addresses, a foreign master, the pump and unknown types are rejected", () =>
        {
            var group = new WirelessGroup("D0D0D36232E1", "D0D0D06232DC", 8, 9, 3, [21, 21, 21, 0], [0, 0, 0, 0], false, 0) { SlotIndex = 4, Pwm = [0, 0, 0, 0] };
            void Packet(WirelessGroup value, byte slot = 10) => Controller.RadioRepairPacket(value, group.Master, 8, slot, new byte[] { 2, 6, 7, 9 });
            Reject(() => Packet(group, 9)); Reject(() => Packet(group, 0)); Reject(() => Packet(group, 14));
            Reject(() => Packet(group with { Master = "010203040506" }));
            Reject(() => Packet(group with { DeviceType = 10, Fans = 0 }));
            Reject(() => Packet(group with { Types = [36, 36, 36, 0] }));
            Reject(() => Packet(group with { SlotIndex = 0 })); Reject(() => Packet(group with { Pwm = [] }));
            Reject(() => Packet(group with { Mac = "000000000000" })); Reject(() => Controller.RadioSavePacket("00"));
        });
        Test("Presets: 13 groups of 4 unique palettes, valid points and saturation", () =>
        {
            Check(GradientPresets.All.Count == 52 && GradientPresets.All.GroupBy(p => p.Family).Count() == 13 && GradientPresets.All.GroupBy(p => p.Family).All(g => g.Count() == 4), "Expected four presets per color family");
            Check(GradientPresets.All.Select(p => p.Palette).Distinct().Count() == 52, "Duplicate palettes");
            foreach (var preset in GradientPresets.All)
            {
                var model = new PaletteModel(preset.Palette);
                Check(model.Points.Count == 3 && model.Points[0].Position == 0 && model.Points.All(p => p.Position < 1), "Invalid preset points");
                var p = new Profile { Palette = model.Serialize(), ColorSpace = preset.Space }; p.Validate();
                Check(new PaletteModel(model.Serialize()).Serialize() == model.Serialize(), "Preset serialization changes colors or stops");
                if (preset.Name is "Vivid" or "Deep") Check(model.Points.All(p => ColorMath.ToHsv(p.Color).Saturation > .8), "Vivid preset lost saturation");
                model.SetColor(Color.Black);
                Check(new PaletteModel(preset.Palette).Points[0].Color != Color.Black, "Editing a preset mutates the catalog");
                Check(L10n.Entries.ContainsKey(preset.Name) && L10n.Entries.ContainsKey(preset.Family), "Missing preset translation");
            }
        });
        Test("Presets: cards pick the editable palette, cancel keeps the original profile", () =>
        {
            var p = new Profile { Brightness = .37, SaturationBoost = 1.4, Direction = 75, PeriodSeconds = 8 };
            string original = System.Text.Json.JsonSerializer.Serialize(p);
            using var editor = new GradientEditor(p) { ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Location = new Point(-30000, -30000) };
            editor.Show(); Application.DoEvents();
            IEnumerable<Control> Children(Control c) => c.Controls.Cast<Control>().SelectMany(x => new[] { x }.Concat(Children(x)));
            Check(editor.PresetFamily.Items.Count == 13, "Missing color families");
            for (int family = 0; family < 13; family++)
            {
                editor.PresetFamily.SelectedIndex = family; Application.DoEvents();
                var cards = Children(editor).OfType<GradientPresetTile>().ToArray();
                Check(cards.Length == 4, "Family does not show four swatches");
                cards[2].PerformClick();
                var preset = GradientPresets.All[family * 4 + 2];
                Check(editor.Palette == new PaletteModel(preset.Palette).Serialize() && editor.SelectedSpace == preset.Space && editor.SelectedSaturation == 1, "Card did not load the palette and interpolation mode");
                Check(cards[2].Active && cards.Count(c => c.Active) == 1, "Selected preset is not highlighted");
            }
            var strip = Children(editor).OfType<GradientStrip>().Single();
            strip.Model.SetColor(Color.Red); Check(strip.Model.Add(null, editor.SelectedSpace), "Preset points cannot be edited");
            editor.DialogResult = DialogResult.Cancel; editor.Close();
            Check(System.Text.Json.JsonSerializer.Serialize(p) == original, "Preview or cancellation modifies the input profile");
        });
        Test("Multi-select: Shift, box selection in both directions, group move, reset and lost capture", () =>
        {
            var p = new Profile();
            p.Pump.X = 500; p.Pump.Y = 400;
            p.Fans = [new() { Group = "a", Fan = 0, X = 100.25, Y = 100.5 }, new() { Group = "a", Fan = 1, X = 300.25, Y = 100.5 }, new() { Group = "b", Fan = 0, X = 500.25, Y = 100.5 }];
            int changes = 0;
            using var host = new Form { Width = 820, Height = 680, ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Location = new Point(-30000, -30000) };
            using var canvas = new Canvas(() => p, () => 0, () => changes++) { Dock = DockStyle.Fill };
            host.Controls.Add(canvas); host.Show(); Application.DoEvents();
            Point At(double x, double y) => Point.Round(canvas.ToScreen(x, y));
            void Click(FanPlacement fan, bool shift) { canvas.BeginPointer(At(fan.X, fan.Y), shift); canvas.EndPointer(); }
            Click(p.Fans[0], false); Click(p.Fans[1], true);
            Check(canvas.SelectedFans.Count == 2 && changes == 0, "Shift did not extend selection or modified coordinates");
            var first = p.Fans[0]; var second = p.Fans[1]; var third = p.Fans[2];
            var start = At(first.X, first.Y);
            canvas.BeginPointer(start, false); canvas.MovePointer(new Point(start.X + 40, start.Y + 25)); canvas.EndPointer();
            Check(changes > 0 && first.X != 100.25 && Math.Abs(second.X - first.X - 200) < .00001 && second.Y == first.Y && third.X == 500.25, "Group drag changed spacing or an unselected fan");
            Click(second, true); Check(canvas.SelectedFans.Count == 1, "Shift did not toggle selection off");
            canvas.BeginPointer(At(380, 200), false); canvas.MovePointer(At(15, 15)); canvas.EndPointer();
            Check(canvas.SelectedFans.Count == 2, "Reverse rectangle missed fans");
            canvas.BeginPointer(At(420, 15), true); canvas.MovePointer(At(590, 180)); canvas.EndPointer();
            Check(canvas.SelectedFans.Count == 3, "Shift rectangle did not retain existing selection");
            Click(first, false); Check(canvas.SelectedFans.Count == 1, "Ordinary click did not select one fan");
            start = At(first.X, first.Y); canvas.BeginPointer(start, false); canvas.Capture = false;
            double before = first.X; canvas.MovePointer(new Point(start.X + 120, start.Y));
            Check(first.X == before, "Lost capture continued dragging");
            canvas.BeginPointer(At(15, 300), false); canvas.EndPointer();
            Check(canvas.SelectedFans.Count == 0, "Empty click did not clear selection");
            Click(first, false);
            p = new Profile(); canvas.RefreshSettings();
            Check(canvas.SelectedFans.Count == 0, "New profile inherited old selection");
            host.Close();
        });
        Test("Resize by handle: one fan, a whole group kept touching, limits, pump", () =>
        {
            var p = new Profile();
            p.Pump.X = 500; p.Pump.Y = 420;
            p.Fans = [new() { Group = "a", Fan = 0, X = 100, Y = 100 }, new() { Group = "a", Fan = 1, X = 212, Y = 100 }, new() { Group = "b", Fan = 0, X = 450, Y = 100 }];
            int changes = 0;
            using var host = new Form { Width = 820, Height = 680, ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Location = new Point(-30000, -30000) };
            using var canvas = new Canvas(() => p, () => 0, () => changes++) { Dock = DockStyle.Fill };
            host.Controls.Add(canvas); host.Show(); Application.DoEvents();
            Point At(double x, double y) => Point.Round(canvas.ToScreen(x, y));
            RectangleF Union(IEnumerable<FanPlacement> fans) => fans.Select(Gradient.FanBounds).Aggregate(RectangleF.Union);
            void Drag(Point from, Point to) { canvas.BeginPointer(from, false); canvas.MovePointer(to); canvas.EndPointer(); }
            Check(canvas.HandleAt(At(44, 44)) == -1, "Handles visible without a selection.");
            canvas.BeginPointer(At(100, 100), false); canvas.EndPointer();
            var box = Gradient.FanBounds(p.Fans[0]);
            Check(canvas.HandleAt(At(box.Right, box.Bottom)) == 2 && canvas.HandleAt(At(box.Left, box.Top)) == 0 && canvas.HandleAt(At(box.Right, box.Top)) == 1, "Handles of the selected fan not found.");
            Drag(At(box.Right, box.Bottom), At(box.Right + 56, box.Bottom + 56));
            var grown = Gradient.FanBounds(p.Fans[0]);
            Check(changes > 0 && p.Fans[0].Scale > 1.3 && Math.Abs(grown.Left - box.Left) < .01 && Math.Abs(grown.Top - box.Top) < .01, "Single fan: size unchanged or the fixed corner moved.");
            Check(p.Fans[1].Scale == 1 && p.Fans[1].X == 212 && p.Fans[2].Scale == 1, "An unselected fan changed.");
            p.Fans[0].Scale = 1; p.Fans[0].X = 100; p.Fans[0].Y = 100;
            // Whole group: double-click, or pick the group on the Fans tab.
            canvas.SelectWholeGroup("a");
            Check(canvas.SelectedFans.Count == 2 && canvas.SelectedFans.All(f => f.Group == "a"), "Group not fully selected.");
            var group = Union(p.Fans.Take(2));
            Drag(At(group.Left, group.Top), At(group.Left + 40, group.Top + 20));
            var after = Union(p.Fans.Take(2));
            Check(p.Fans[0].Scale < 1 && Math.Abs(p.Fans[0].Scale - p.Fans[1].Scale) < 1e-9 && Math.Abs(after.Right - group.Right) < .01 && Math.Abs(after.Bottom - group.Bottom) < .01, "Group: scales differ or the fixed corner moved.");
            Check(Math.Abs(Gradient.FanBounds(p.Fans[1]).Left - Gradient.FanBounds(p.Fans[0]).Right) < .01, "Group: a gap appeared between fans.");
            Check(p.Fans[2].Scale == 1 && p.Fans[2].X == 450, "Another group changed.");
            group = Union(p.Fans.Take(2));
            Drag(At(group.Right, group.Bottom), At(group.Left - 300, group.Top - 300));
            Check(p.Fans.Take(2).All(f => f.Scale >= .2 - 1e-9) && p.Fans.Take(2).Any(f => Math.Abs(f.Scale - .2) < .01), "Scale limit 0.2 not enforced.");
            p.Validate();
            // Shift on a handle is selection, not resizing.
            double scale = p.Fans[0].Scale; group = Union(p.Fans.Take(2));
            canvas.BeginPointer(At(group.Right, group.Bottom), true); canvas.MovePointer(At(group.Right + 60, group.Bottom + 60)); canvas.EndPointer();
            Check(p.Fans[0].Scale == scale, "Shift on a handle changed the size.");
            // Pump: a click selects it, a handle changes its diameter.
            canvas.BeginPointer(At(500, 420), false); canvas.EndPointer();
            Check(canvas.SelectedFans.Count == 0, "Clicking the pump did not clear the fan selection.");
            double d = p.Pump.Diameter, right = p.Pump.X + d / 2, bottom = p.Pump.Y + d / 2;
            Drag(At(right, bottom), At(right + 30, bottom + 30));
            Check(p.Pump.Diameter > d + 20 && Math.Abs(p.Pump.X - p.Pump.Diameter / 2 - (500 - d / 2)) < .01, "Pump: diameter unchanged or the fixed corner moved.");
            p.Validate();
            canvas.BeginPointer(At(15, 600), false); canvas.EndPointer();
            Check(canvas.HandleAt(At(p.Pump.X + p.Pump.Diameter / 2, p.Pump.Y + p.Pump.Diameter / 2)) == -1, "Handles remained after clicking empty space.");
            host.Close();
        });
        Test("Multi-select: box selection and removed fans", () =>
        {
            var p = new Profile { Fans = [new() { Group = "a", X = 100, Y = 100 }, new() { Group = "a", Fan = 1, X = 300, Y = 100 }] };
            var selection = new FanSelection();
            selection.Box(p, new RectangleF(20, 20, 360, 160), []);
            Check(selection.Members(p).Count == 2, "Rectangle selection failed");
            p.Fans.RemoveAt(0); selection.Prune(p);
            Check(selection.Members(p).Count == 1, "Deleted fans remain selected");
        });
        Test("tinyuz decompression restores the original RGB data", () =>
        {
            foreach (byte[] input in new[] { Enumerable.Repeat((byte)0, 360).ToArray(), Enumerable.Range(0, 360).Select(i => (byte)i).ToArray(), Enumerable.Range(0, 36000).Select(i => (byte)(i / 360)).ToArray() })
                Check(TinyUz.Decompress(TinyUz.Compress(input), input.Length).SequenceEqual(input), "Broken round-trip tinyuz.");
        });
        Test("tinyuz matches the reference files upstream v1.2.2", () =>
        {
            string directory = Path.Combine(AppContext.BaseDirectory, "test-fixtures");
            foreach (var (name, raw) in new[]
            {
                ("01_static_16led", Enumerable.Range(0, 16).SelectMany(_ => new byte[] { 255, 128, 0 }).ToArray()),
                ("02_static_40led", Enumerable.Range(0, 40).SelectMany(_ => new byte[] { 0, 255, 0 }).ToArray()),
                ("03_breathing_40led", Enumerable.Range(0, 40).SelectMany(i => i % 2 == 0 ? new byte[] { 255, 0, 0 } : new byte[] { 0, 0, 255 }).ToArray()),
                ("07_gradient_80", Enumerable.Range(0, 80).SelectMany(i => new byte[] { (byte)i, (byte)(i * 2), (byte)(255 - i) }).ToArray()),
                ("08_black", new byte[240]),
                ("09_white", Enumerable.Repeat((byte)255, 240).ToArray())
            }) Check(TinyUz.Compress(raw).SequenceEqual(File.ReadAllBytes(Path.Combine(directory, name + ".yuz"))), "Result differs from " + name);
        });
        Test("42-SL byte record: MAC, groups, status; unknown models are blocked", () =>
        {
            byte[] record = new byte[42]; record[0] = 1; record[6] = 9; record[12] = 8; record[13] = 2; record[19] = 3;
            record[24] = 20; record[25] = 21; record[26] = 22; record[28] = 0x40; record[41] = 0x1C;
            var g = WirelessGroup.Parse(record)!;
            Check(g.Supported && g.LedCount == 120 && g.Mac == "010000000000" && g.MotherboardSync, "Wrong record SL.");
            record[25] = 36; Check(!WirelessGroup.Parse(record)!.Supported, "SL-INF taken as SL.");
            record[41] = 0; Check(WirelessGroup.Parse(record) == null, "Invalid marker accepted.");
            Check(WirelessGroup.Parse(record.AsSpan(0, 20)) == null, "Short reply accepted.");
        });
        Test("The keep-alive packet keeps the protective sentinel and carries no PWM/sensor data", () =>
        {
            byte[] packet = Controller.MasterClockPacket("010203040506");
            Check(packet[0] == 0x12 && packet[1] == 0x14 && packet.AsSpan(8, 6).SequenceEqual(new byte[] { 1, 2, 3, 4, 5, 6 }), "Wrong header.");
            Check(packet.AsSpan(14, 50).IndexOfAnyExcept((byte)0x14) < 0, "Broken protective sentinel.");
            Check(packet.AsSpan(64).IndexOfAnyExcept((byte)0) < 0, "Control data leaked into the keep-alive packet.");
        });
        Test("RGB-header, big endian, chunks, header repeat and zero tail", () =>
        {
            byte[] raw = Enumerable.Range(0, 120).Select(i => (byte)i).ToArray();
            var upload = new RgbUpload([raw, raw], 5333);
            var packets = upload.Packets("010203040506", "090807060504", 8, 2).ToArray();
            byte[] rf = packets.Take(4).SelectMany(p => p.Skip(4)).ToArray();
            Check(rf.Length == 240 && rf.Take(14).SequenceEqual(new byte[] { 0x12, 0x20, 1, 2, 3, 4, 5, 6, 9, 8, 7, 6, 5, 4 }), "Wrong addressing.");
            Check(BinaryPrimitives.ReadUInt32BigEndian(rf.AsSpan(20)) == upload.Compressed.Length && rf[25] == 0 && rf[26] == 2 && rf[27] == 40, "Wrong metadata.");
            Check(rf[32] == 0 && rf[33] == 53 && rf[34] == 33 && (rf[14] & 0x80) != 0, "Wrong interval or effect index.");
            for (int i = 0; i < packets.Length; i++) Check(packets[i].Length == 64 && packets[i][0] == 0x10 && packets[i][1] == i % 4 && packets[i][2] == 8 && packets[i][3] == 2, "Wrong USB chunk.");
            // The header is sent RgbUpload.HeaderRepeats times in a row (upstream send_rgb_upload(…, 4)).
            int headers = RgbUpload.HeaderRepeats;
            Check(headers == 4 && Enumerable.Range(1, headers - 1).All(r => packets.Take(4).SelectMany(x => x).SequenceEqual(packets.Skip(4 * r).Take(4).SelectMany(x => x))), "Header not repeated 4 times.");
            byte[] reconstructed = packets.Skip(4 * headers).Chunk(4).SelectMany(block => block.SelectMany(p => p.Skip(4)).Skip(20).Take(220)).Take(upload.Compressed.Length).ToArray();
            Check(reconstructed.SequenceEqual(upload.Compressed), "Compressed data order broken.");
            Check(TinyUz.Decompress(reconstructed, 240).SequenceEqual(raw.Concat(raw)), "The device would get a different RGB frame.");
            Reject(() => upload.Packets("010203040506", "090807060504", 0, 2).ToArray());
            Reject(() => new RgbUpload([new byte[4]], 100));
            Reject(() => new RgbUpload([raw, new byte[3]], 100));
        });
        Test("Shared spatial phase, direction, period and current limit", () =>
        {
            var p = new Profile { Brightness = 1, Palette = "#FF0000, #0000FF", Wavelength = 100, PeriodSeconds = 4 };
            var stops = Gradient.ParsePalette(p.Palette);
            Check(Gradient.At(p, stops, new(0, 0), 0).ToArgb() == Color.Red.ToArgb(), "Wrong start point.");
            Check(Gradient.At(p, stops, new(50, 0), 0).ToArgb() == Color.Blue.ToArgb(), "Shared coordinate ignored.");
            Check(Gradient.At(p, stops, new(10, 20), .25).ToArgb() == Gradient.At(p, stops, new(10, 20), 4.25).ToArgb(), "Cycle does not wrap.");
            p.Direction = 90;
            Check(Gradient.At(p, stops, new(0, 50), 0).B >= 254, "Wrong direction.");
            p.Palette = "#FFFFFF, #FFFFFF";
            var white = Gradient.At(p, Gradient.ParsePalette(p.Palette), new(0, 0), 0); Check(white.R + white.G + white.B <= 600, "No current limit.");
            p.Brightness = 0; Check(Gradient.At(p, stops, new(0, 0), 0).R == 0, "Brightness does not work: 0.");
            Reject(() => Gradient.ParsePalette("#FF0000@0, #00FF00@0")); Reject(() => Gradient.ParsePalette("#FF0000@NaN, #00FF00"));
        });
        Test("Profiles: save, restore and rejection of invalid parameters", () =>
        {
            string path = Path.Combine(Path.GetTempPath(), "wcrgb-test-" + Guid.NewGuid() + ".json");
            try
            {
                var p = new Profile { Direction = 45, Fans = [new() { Group = "010203040506", Fan = 0, X = 200, Y = 120 }] };
                p.Save(path); var restored = Profile.Load(path); Check(restored.Direction == 45 && restored.Fans[0].X == 200, "Profile lost data.");
                p.Fps = 0; Reject(p.Validate);
            }
            finally { if (File.Exists(path)) File.Delete(path); }
        });
        Test("Two-group scene: shared interval, memory limit and original frames after decompression", () =>
        {
            var groups = new[]
            {
                new WirelessGroup("010203040506", "090807060504", 8, 1, 3, [20, 21, 22, 0], new byte[4], false, 0),
                new WirelessGroup("020304050607", "090807060504", 8, 2, 3, [20, 21, 22, 0], new byte[4], false, 0)
            };
            var p = new Profile();
            p.Fans = groups.SelectMany((g, row) => Enumerable.Range(0, 3).Select(f => new FanPlacement { Group = g.Mac, Fan = f, X = 150 + 125 * f, Y = 120 + 170 * row })).ToList();
            var scene = RgbScene.Prepare(p, groups);
            Check(scene.Uploads.Values.All(u => u.Compressed.Length <= 12288 && u.Frames == scene.Frames && u.Interval == scene.Interval), "Shared timeline broken.");
            foreach (var g in groups)
            {
                var u = scene.Uploads[g.Mac]; var raw = TinyUz.Decompress(u.Compressed, scene.Frames * g.LedCount * 3);
                int index = scene.Frames / 2;
                Check(raw.AsSpan(index * 360, 360).SequenceEqual(Gradient.Frame(p, g.Mac, 3, index * p.PeriodSeconds / scene.Frames)), "Frame color changed after preparation.");
            }
            results.Add($"INFO: 6 s cycle, 2 groups of 3 fans: {scene.Frames} frames, {scene.Frames / 6.0:F1} computed FPS; sizes {string.Join(", ", scene.Uploads.Values.Select(u => u.Compressed.Length))} bytes. Device frame rate was not measured.");
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel(); Reject(() => RgbScene.Prepare(p, groups, cancelled.Token));
        });
        Test("OpenRGB: TCP handshake, description, Direct, zone and single color", () => SdkTest().GetAwaiter().GetResult());
        Test("Transfer of four groups 2+1+3+3 completes with 16 ms Windows pauses", () =>
        {
            int[] counts = [2, 1, 3, 3];
            var groups = counts.Select((fans, i) => new WirelessGroup($"0102030405{i + 1:X2}", "090807060504", 8, (byte)(i + 1), fans,
                Enumerable.Repeat((byte)20, fans).Concat(new byte[4 - fans]).ToArray(), new byte[4], false, 0)).ToArray();
            var p = new Profile { Gamma = 1 };
            p.Fans = groups.SelectMany((g, row) => Enumerable.Range(0, g.Fans).Select(f => new FanPlacement { Group = g.Mac, Fan = f, X = 130 + f * 125, Y = 100 + row * 155 })).ToList();
            var scene = RgbScene.Prepare(p, groups);
            int completedGroups = 0, longerThanOldLimit = 0;
            foreach (var group in groups)
            {
                var packets = scene.Uploads[group.Mac].Packets(group.Mac, group.Master, group.Channel, group.Slot).ToArray();
                List<byte[]> sent = [];
                var elapsed = TimeSpan.Zero;
                var lastHeartbeat = TimeSpan.Zero;
                var result = RgbTransfer.Send(packets,
                    frame => { sent.AddRange(frame); elapsed += TimeSpan.FromMilliseconds(4 * 16); },
                    () => { if ((elapsed - lastHeartbeat).TotalMilliseconds >= 800) { elapsed += TimeSpan.FromMilliseconds(80); lastHeartbeat = elapsed; } },
                    ms => elapsed += TimeSpan.FromMilliseconds(ms), elapsed: () => elapsed);
                Check(sent.Count == packets.Length && sent.SelectMany(x => x).SequenceEqual(packets.SelectMany(x => x)), "Transfer was cut or changed a packet.");
                Check(result.UsbPackets == packets.Length && result.RadioFrames == packets.Length / 4, "Wrong transfer counter.");
                if (result.Elapsed.TotalSeconds > 3) longerThanOldLimit++;
                completedGroups++;
            }
            Check(completedGroups == 4 && longerThanOldLimit >= 2, "The test did not reproduce the two-large-groups problem.");
        });
        Test("RGB over radio: 4 header copies 20 ms apart, retry only for an unconfirmed group, up to 6 times, fast polling", () =>
        {
            var upload = new RgbUpload([Enumerable.Range(0, 360).Select(i => (byte)i).ToArray()], 5300);
            var packets = upload.Packets("010203040506", "090807060504", 8, 2).ToArray();
            List<int> delays = []; int frames = 0;
            RgbTransfer.Send(packets, _ => frames++, () => { }, delays.Add, headerFrames: RgbUpload.HeaderRepeats);
            Check(delays.SequenceEqual(new[] { 20, 20, 20 }) && frames == packets.Length / 4, "Pauses between header copies must be 20 ms, none after data.");
            delays.Clear(); RgbTransfer.Send(packets.Skip(8).ToArray(), _ => { }, () => { }, delays.Add, headerFrames: 2);
            Check(delays.SequenceEqual(new[] { 2 }), "Two header copies: 2 ms pause, as before.");
            Check(RgbTransfer.BudgetFor(64 * 4) > TimeSpan.Zero, "The largest upload (56 chunks + 4 headers) must fit the limit.");
            var group = new WirelessGroup("010203040506", "090807060504", 8, 2, 3, [20, 20, 20, 0], upload.Effect, false, 0);
            var rainbow = group with { Effect = [1, 2, 3, 4] };
            Check(!Controller.NeedsResync(group, upload, (1000, 0), 5000), "A confirmed group must not get a retry.");
            Check(!Controller.NeedsResync(rainbow, upload, (6000, 0), 5000), "No retry before the pause expires.");
            Check(Controller.NeedsResync(rainbow, upload, (1000, 0), 5000) && Controller.NeedsResync(group with { MotherboardSync = true }, upload, (1000, 0), 5000), "A reset to the built-in effect or motherboard sync must trigger a retry.");
            Check(!Controller.NeedsResync(rainbow, upload, (1000, Controller.ResyncAttempts), 5000) && !Controller.NeedsResync(null, upload, (1000, 0), 5000), "No more than 6 retries; a missing group is not retried.");
            // Waiting for confirmation: fast polling while an unconfirmed group has retries left.
            Check(Controller.ResyncDelay == 1800 && Controller.ResyncAttempts == 6, "Pause or retry count changed.");
            Check(Controller.PollDue(1000, 900, 700, true) && !Controller.PollDue(1000, 900, 800, true) && !Controller.PollDue(1000, 900, 0, false) && Controller.PollDue(1800, 1000, 1700, false), "Poll interval while waiting for confirmation.");
            var wanted = new Dictionary<string, RgbUpload> { [group.Mac] = upload };
            Check(!Controller.AwaitingConfirmation(wanted, [group], new Dictionary<string, (long, int)>()), "A confirmed group keeps fast polling.");
            Check(Controller.AwaitingConfirmation(wanted, [rainbow], new Dictionary<string, (long, int)> { [group.Mac] = (0, 2) }), "An unconfirmed group did not speed up polling.");
            Check(!Controller.AwaitingConfirmation(wanted, [rainbow], new Dictionary<string, (long, int)> { [group.Mac] = (0, Controller.ResyncAttempts) }), "Fast polling does not stop after retries run out.");
            Check(new PollException() is IOException, "An incomplete poll is a kind of I/O error.");
        });
        Test("Brightness affects devices only: the preview canvas does not dim", () =>
        {
            var p = new Profile { Brightness = 1 }; CaseLayout.Populate(p);
            var fan = p.Fans[0]; var stops = Effects.Palette(p); var inner = Gradient.ParsePalette(p.InnerLayer.Palette); var outer = Gradient.ParsePalette(p.OuterLayer.Palette);
            var full = Gradient.FanLed(p, fan, stops, inner, outer, 3, 1.2).ToArgb(); byte[] bright = Gradient.Frame(p, fan.Group, p.FanCount(fan.Group), 1.2);
            p.Brightness = .2;
            Check(Gradient.FanLed(p, fan, stops, inner, outer, 3, 1.2).ToArgb() == full, "Fan preview dimmed with brightness.");
            var pumpStops = Gradient.ParsePalette(p.Pump.Layer.Palette); var ring = Gradient.PumpPoint(p.Pump, 0);
            int dimPump = Gradient.PumpColor(p, stops, pumpStops, ring, 1.2).ToArgb();
            p.Brightness = 1; int fullPump = Gradient.PumpColor(p, stops, pumpStops, ring, 1.2).ToArgb(); p.Brightness = .2;
            Check(dimPump == fullPump, "Pump ring preview dimmed with brightness.");
            Check(Gradient.Frame(p, fan.Group, p.FanCount(fan.Group), 1.2).Sum(b => b) < bright.Sum(b => b) / 2, "The device frame must dim with brightness.");
        });
        Test("A stuck transfer is time-limited; cancelling sends no packet", () =>
        {
            byte[][] packets = Enumerable.Range(0, 232).Select(_ => new byte[64]).ToArray();
            var elapsed = TimeSpan.Zero; int frames = 0;
            try
            {
                RgbTransfer.Send(packets, _ => { frames++; elapsed += TimeSpan.FromSeconds(1); }, () => { }, _ => { }, elapsed: () => elapsed);
                throw new Exception("Stuck transfer not stopped.");
            }
            catch (RgbTransferException e)
            {
                Check(e.CompletedFrames == frames && frames < 58 && e.TotalFrames == 58 && e.Elapsed.TotalSeconds < 16, "Wrong limit or error progress.");
            }
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            int sent = 0;
            Reject(() => RgbTransfer.Send(packets, _ => sent++, () => { }, _ => { }, cancelled.Token));
            Check(sent == 0, "A packet was sent after cancelling.");
        });
        Test("The precise Windows timer performs short waits without USB", () =>
        {
            using var delay = new RadioDelay();
            var clock = System.Diagnostics.Stopwatch.StartNew();
            for (int i = 0; i < 240; i++) delay.Wait(1);
            Check(clock.Elapsed.TotalMilliseconds >= 200, "Timer does not keep intervals.");
            results.Add($"INFO: 240 waits of 1 ms: {clock.Elapsed.TotalMilliseconds:F0} ms; high-resolution={delay.HighResolution}. USB was not opened.");
            Reject(() => delay.Wait(-1));
        });
        Test("Group finder: the 40 LEDs of each fan get their own color; the previous cycle is kept", () =>
        {
            byte[] marker = Identification.Frame(3);
            Check(marker.Length == 360, "Wrong finder frame length.");
            var expected = new byte[][] { [100, 0, 0], [0, 90, 0], [0, 20, 110] };
            for (int fan = 0; fan < 3; fan++)
                for (int led = 0; led < 40; led++) Check(marker.AsSpan((fan * 40 + led) * 3, 3).SequenceEqual(expected[fan]), "Fan numbers mixed up in the finder.");
            var previous = new RgbUpload([Enumerable.Repeat((byte)12, 360).ToArray(), Enumerable.Repeat((byte)35, 360).ToArray()], 5333);
            byte[] before = (byte[])previous.Compressed.Clone();
            var highlight = new RgbUpload([marker], 800000);
            var session = new IdentificationSession("010203040506", "090807060504", previous, highlight, 7000);
            Check(session.Previous.Compressed.SequenceEqual(before) && session.Previous.Frames == 2 && session.Previous.Interval == 5333, "Finder changed the saved cycle.");
            Check(TinyUz.Decompress(highlight.Compressed, 360).SequenceEqual(marker), "The device would get different finder colors.");
            Reject(() => Identification.Frame(0)); Reject(() => Identification.Frame(5));
        });
        Test("Group labels: unique G1–G4 with the same MAC tail, names and old profiles", () =>
        {
            var p = new Profile { Fans = [new() { Group = "D0D0D47A4EE0", Fan = 0 }, new() { Group = "D0D0D27A4EE0", Fan = 0 }] };
            Check(p.GroupTitle("D0D0D27A4EE0") == "G1" && p.GroupTitle("D0D0D47A4EE0") == "G2", "Groups with the same tail are not told apart.");
            p.GroupNames["D0D0D47A4EE0"] = "Top";
            Check(p.GroupTitle("D0D0D47A4EE0") == "G2 — Top", "Name not shown.");
            var restored = System.Text.Json.JsonSerializer.Deserialize<Profile>(System.Text.Json.JsonSerializer.Serialize(p))!;
            Check(restored.GroupNames["D0D0D47A4EE0"] == "Top" && restored.Fans[0].FanNumber == 1, "Name or number lost.");
            var old = System.Text.Json.JsonSerializer.Deserialize<Profile>("{\"Version\":1,\"Fans\":[{\"Group\":\"010203040506\",\"Fan\":0}]}")!;
            old.Validate(); Check(old.GroupNames.Count == 0 && old.GroupTitle("010203040506") == "G1", "Old profile incompatible.");
            p.GroupNames["D0D0D47A4EE0"] = "Top\nBottom"; Reject(p.Validate);
        });
        Test("Point editor: add, move, remove, limits and saving of colors/positions", () =>
        {
            var model = new PaletteModel("#FF0000@0, #0000FF@0.5");
            Check(model.Add(.25, ColorSpace.Hsv) && model.Points.Count == 3, "Point not added.");
            model.SetColor(Color.Lime); Check(model.MoveSelected(.75) && model.Points[model.Selected].Color.ToArgb() == Color.Lime.ToArgb(), "Color lost after moving.");
            Check(!model.MoveSelected(.5), "Overlapping points accepted.");
            var restored = Gradient.ParsePalette(model.Serialize());
            Check(restored.Length == 3 && restored[^1].Position == .75 && restored[^1].Color.ToArgb() == Color.Lime.ToArgb(), "Point not saved.");
            while (model.Points.Count < 16) Check(model.Add(null, ColorSpace.Hsv), "Could not fill the palette.");
            Check(!model.Add(.1, ColorSpace.Hsv), "Point limit exceeded.");
            while (model.Points.Count > 2) Check(model.RemoveSelected(), "Could not remove a point.");
            Check(!model.RemoveSelected(), "The second-to-last point was removed.");
            model.EvenlySpace(); Check(model.Points[0].Position == 0 && model.Points[1].Position == .5, "Wrong distribution.");
        });
        Test("HSV: saturated transitions, hue wrap and intentionally white/black colors", () =>
        {
            var stops = Gradient.ParsePalette("#FF0000, #00FFFF");
            var rgb = Gradient.Sample(stops, .25); var hsv = Gradient.Sample(stops, .25, ColorSpace.Hsv);
            Check(rgb.R == 128 && rgb.G == 128 && rgb.B == 128, "Old RGB blending changed.");
            Check(hsv.G == 255 && hsv.B == 0 && ColorMath.ToHsv(hsv).Saturation > .99, "HSV lost saturation.");
            var wrap = new[] { new ColorStop(0, ColorMath.FromHsv(350, 1, 1)), new ColorStop(.5, ColorMath.FromHsv(10, 1, 1)) };
            var middle = Gradient.Sample(wrap, .25, ColorSpace.Hsv); Check(middle.R >= 250 && middle.G < 5 && middle.B < 5, "Hue went around the whole wheel.");
            var whiteBlue = Gradient.Sample(Gradient.ParsePalette("#FFFFFF, #0000FF"), .25, ColorSpace.Hsv);
            Check(whiteBlue.B == 255 && Math.Abs(whiteBlue.R - 128) <= 1 && whiteBlue.R == whiteBlue.G, "White point got a wrong hue.");
            Check(Gradient.Sample(Gradient.ParsePalette("#000000, #000000"), .25, ColorSpace.Hsv, 2).ToArgb() == Color.Black.ToArgb(), "Black became colored.");
            var p = new Profile { ColorSpace = ColorSpace.Hsv, SaturationBoost = 2, Brightness = 0 };
            Check(Gradient.At(p, stops, new(0, 0), 0).ToArgb() == Color.Black.ToArgb(), "Brightness 0 no longer turns off RGB.");
            p.Brightness = 1;
            var safeWhite = Gradient.At(p, Gradient.ParsePalette("#FFFFFF, #FFFFFF"), new(0, 0), 0);
            Check(safeWhite.R + safeWhite.G + safeWhite.B <= 600, "Channel limit broken.");
        });
        Test("Old RGB profiles keep their look; new ones keep HSV and saturation", () =>
        {
            var old = System.Text.Json.JsonSerializer.Deserialize<Profile>("{\"Palette\":\"#FF0000, #00FFFF\",\"Brightness\":0.5}")!;
            old.Validate(); Check(old.ColorSpace == ColorSpace.Rgb && old.SaturationBoost == 1, "Old profile settings changed.");
            Check(Gradient.At(old, Gradient.ParsePalette(old.Palette), new(old.Wavelength * .25, 0), 0).ToArgb() == Color.FromArgb(64, 64, 64).ToArgb(), "Old frame changed.");
            old.ColorSpace = ColorSpace.Hsv; old.SaturationBoost = 1.5;
            var restored = System.Text.Json.JsonSerializer.Deserialize<Profile>(System.Text.Json.JsonSerializer.Serialize(old))!;
            Check(restored.ColorSpace == ColorSpace.Hsv && restored.SaturationBoost == 1.5, "Color settings lost.");
            restored.SaturationBoost = 3; Reject(restored.Validate);
        });
        Test("HSV rainbow: saturated RGB frames and the cycle fit in memory", () =>
        {
            var group = new WirelessGroup("010203040506", "090807060504", 8, 1, 3, [20, 20, 20, 0], new byte[4], false, 0);
            var p = new Profile { Palette = PaletteModel.Rainbow().Serialize(), ColorSpace = ColorSpace.Hsv, Brightness = 1 };
            p.Fans = Enumerable.Range(0, 3).Select(f => new FanPlacement { Group = group.Mac, Fan = f, X = 130 + f * 125, Y = 100 }).ToList();
            byte[] frame = Gradient.Frame(p, group.Mac, 3, 0);
            for (int i = 0; i < 120; i++) Check(frame.AsSpan(i * 3, 3).ToArray().Min() == 0 && frame.AsSpan(i * 3, 3).ToArray().Max() == 255, "Rainbow faded in the frame.");
            var scene = RgbScene.Prepare(p, [group]); Check(scene.Uploads[group.Mac].Compressed.Length <= 12288, "New mode does not fit the memory limit.");
        });
        Test("The gradient strip does not darken colors from top to bottom", () =>
        {
            using var strip = new GradientStrip { Size = new Size(320, 110), Model = PaletteModel.Rainbow(), Space = ColorSpace.Hsv };
            using var bitmap = new Bitmap(320, 110);
            strip.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
            for (int x = 30; x < 300; x += 25)
            {
                Check(bitmap.GetPixel(x, 17).ToArgb() == bitmap.GetPixel(x, 50).ToArgb(), "Preview darkened the lower part of the color.");
                Check(Math.Max(bitmap.GetPixel(x, 30).R, Math.Max(bitmap.GetPixel(x, 30).G, bitmap.GetPixel(x, 30).B)) == 255, "Preview brightness reduced.");
            }
        });
        Test("Layers: 24 inner + 16 outer LEDs, independent palettes, brightness and off", () =>
        {
            var p = new Profile { Gamma = 1, Brightness = .5, Fans = [new() { Group = "010203040506" }] };
            p.InnerLayer.UseCanvasPalette = p.OuterLayer.UseCanvasPalette = false;
            p.InnerLayer.Palette = "#FF0000, #FF0000"; p.InnerLayer.Brightness = .5;
            p.OuterLayer.Palette = "#0000FF, #0000FF";
            byte[] frame = Gradient.Frame(p, "010203040506", 1, 0);
            Check(Enumerable.Range(0, 40).Count(Gradient.IsInner) == 24, "Wrong bank split.");
            for (int led = 0; led < 40; led++)
                Check(frame.AsSpan(led * 3, 3).SequenceEqual(Gradient.IsInner(led) ? new byte[] { 64, 0, 0 } : new byte[] { 0, 0, 128 }), "Color leaked into the other layer.");
            p.InnerLayer.Enabled = false;
            var off = Gradient.Frame(p, "010203040506", 1, 0);
            for (int led = 0; led < 40; led++)
                Check(off.AsSpan(led * 3, 3).SequenceEqual(Gradient.IsInner(led) ? new byte[3] : frame.AsSpan(led * 3, 3).ToArray()), "Turning off changed the other layer.");
            p.InnerLayer.Brightness = 2; Reject(p.Validate);
        });
        Test("Layers of an old profile: the frame fully matches the shared gradient", () =>
        {
            var p = System.Text.Json.JsonSerializer.Deserialize<Profile>("{\"Fans\":[{\"Group\":\"010203040506\",\"X\":100,\"Y\":200}]}")!;
            var stops = Gradient.ParsePalette(p.Palette); var frame = Gradient.Frame(p, "010203040506", 1, .35);
            for (int led = 0; led < 40; led++)
            {
                // Old profile: layers match the shared gradient; only gamma is added to the device frame LED.
                var c = Gradient.Device(p, Gradient.Sample(stops, Gradient.Phase(p, Gradient.WorldPoint(p, p.Fans[0], led), .35), p.ColorSpace, p.SaturationBoost, p.Brightness), true);
                Check(p.Gamma == 2.2 && frame.AsSpan(led * 3, 3).SequenceEqual(new byte[] { c.R, c.G, c.B }), "Old profile bytes changed.");
            }
        });
        Test("Reference case: groups, sides, projection, spatial masks and layer preservation", () =>
        {
            var p = new Profile(); CaseLayout.Populate(p); p.Validate();
            FanPlacement[] Group(int i) => p.Fans.Where(f => f.Group == CaseLayout.DemoMacs[i]).OrderBy(f => f.Fan).ToArray();
            Check(Group(0).Length == 2 && Group(0)[0].X > Group(3)[2].X && Group(0)[0].Y > Group(0)[1].Y, "LCD-group is not on the right from bottom to top (G1·1 at the bottom).");
            Check(Group(1)[0].X < Group(3)[0].X && Group(3).All(f => f.Y < Group(1)[0].Y) && Group(2).All(f => f.Y > Group(1)[0].Y), "Wrong case sides.");
            var top = Gradient.WorldPoint(p, Group(3)[0], 0); var bottom = Gradient.WorldPoint(p, Group(2)[0], 0);
            Check(top.Y < 100 && bottom.Y > 480, "Projections do not match the top and bottom rows.");
            p.Direction = 90;
            Check(Gradient.Frame(p, CaseLayout.DemoMacs[2], 3, 0).AsSpan(0, 3).SequenceEqual(Gradient.Frame(p, CaseLayout.DemoMacs[3], 3, 0).AsSpan(0, 3)) == false, "Gradient restarts on every group.");
            p.OuterLayer.UseCanvasPalette = false; p.OuterLayer.Palette = "#00FF00, #00FFFF"; p.InnerLayer.Enabled = false;
            var restored = System.Text.Json.JsonSerializer.Deserialize<Profile>(System.Text.Json.JsonSerializer.Serialize(p))!; restored.Validate();
            Check(restored.LayoutPreset == CaseLayout.Id && restored.Fans[3].Elevation == 30 && restored.Fans[3].Scale == 1.5 && !restored.InnerLayer.Enabled && !restored.OuterLayer.UseCanvasPalette, "Profile lost the layout or layers.");
            restored.Fans.RemoveAt(0); Check(!CaseLayout.Apply(restored), "Layout applied to a different fan set.");
            p.Fans[0].ProjectionY = double.NaN; Reject(p.Validate);
        });
        Test("Case with two palettes: four groups, shared cycle, limit and frames after decompression", () =>
        {
            var p = new Profile { Palette = PaletteModel.Rainbow().Serialize(), ColorSpace = ColorSpace.Hsv };
            CaseLayout.Populate(p); p.OuterLayer.UseCanvasPalette = false; p.OuterLayer.Palette = "#FF0000, #0000FF, #00FF00";
            var groups = CaseLayout.DemoMacs.Select((mac, i) => new WirelessGroup(mac, "090807060504", 8, (byte)(i + 1), (byte)p.Fans.Count(f => f.Group == mac), [20,20,20,0], new byte[4], false, 0)).ToArray();
            var scene = RgbScene.Prepare(p, groups, includePump: true);
            Check(scene.Pump != null && scene.Pump.Frames == scene.Frames && scene.Pump.Interval == scene.Interval && scene.Interval % 100 == 0 && scene.Interval <= 25500, "Pump got a different period.");
            var pumpRaw = TinyUz.Decompress(scene.Pump!.Compressed, scene.Frames * 72);
            Check(pumpRaw.AsSpan(72, 72).SequenceEqual(Gradient.PumpFrame(p, p.PeriodSeconds/scene.Frames)), "Pump frame corrupted.");
            foreach (var group in groups)
            {
                var upload = scene.Uploads[group.Mac];
                Check(upload.Compressed.Length <= 12288 && upload.Frames == scene.Frames && upload.Interval == scene.Interval, "Limit or shared timing broken.");
                byte[] decoded = TinyUz.Decompress(upload.Compressed, scene.Frames * group.Fans * 120);
                int sample = scene.Frames / 2;
                Check(decoded.AsSpan(sample * group.Fans * 120, group.Fans * 120).SequenceEqual(Gradient.Frame(p, group.Mac, group.Fans, sample * p.PeriodSeconds / scene.Frames)), "Layers changed during packing.");
            }
        });
        Test("GUI: Mapping / Lian Li fans / OpenRGB tabs, group selection, coordinates and button style", () =>
        {
            var p = new Profile(); CaseLayout.Populate(p);
            using var form = new MainForm(false, p) { ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Location = new Point(-30000, -30000) };
            IEnumerable<Control> Descendants(Control parent) => parent.Controls.Cast<Control>().SelectMany(c => new[] { c }.Concat(Descendants(c)));
            var controls = Descendants(form).ToArray();
            var sections = controls.OfType<SegmentedTabs>().Single(t => t.Name == "sections");
            void Open(int i) { sections.Select(i); Application.DoEvents(); }
            var canvas = controls.OfType<Canvas>().Single(); var fansPanel = controls.OfType<FansPanel>().Single(); var openRgbPanel = controls.OfType<OpenRgbPanel>().Single();
            var coordinates = controls.OfType<Button>().Single(b => b.Text == "Coordinates");
            var table = controls.OfType<DataGridView>().Single(g => g.Columns.Cast<DataGridViewColumn>().Any(c => c.DataPropertyName == "Elevation"));
            form.Show(); Application.DoEvents();
            Check(sections.Items.SequenceEqual(MainForm.TabTitles) && sections.SelectedIndex == 0 && canvas.Visible && !fansPanel.Visible && !openRgbPanel.Visible, "The Mapping tab must be open at launch.");
            Check(sections.Indicator.X == sections.ItemBounds(0).X && sections.Indicator.Width == sections.ItemBounds(0).Width, "The marker must be on Mapping.");
            Check(controls.OfType<Button>().All(b => b is PillButton), "Old-style buttons remain.");
            Open(1);
            Check(fansPanel.Visible && !canvas.Visible && sections.SelectedIndex == 1, "Fans tab did not open.");
            // The marker slides smoothly: right after the click it is between sections, after the animation on Fans.
            var timer = System.Diagnostics.Stopwatch.StartNew();
            while (sections.Animating && timer.ElapsedMilliseconds < 2000) { Application.DoEvents(); Thread.Sleep(5); }
            Check(!sections.Animating && Math.Abs(sections.Indicator.X - sections.ItemBounds(1).X) < .5 && Math.Abs(sections.Indicator.Width - sections.ItemBounds(1).Width) < .5, "The marker did not reach the active section.");
            Check(Enumerable.Range(1, sections.Items.Count - 1).All(i => Math.Abs(sections.ItemBounds(i).X - sections.ItemBounds(i - 1).Right) < .01), "Sections must touch inside one pill.");
            Check(fansPanel.Cards.Count == 4 && fansPanel.Cards.Select(c => c.Rows.Count).SequenceEqual([2, 1, 3, 3]) && fansPanel.PumpCard is { Mac: null, Rows.Count: 1 } &&
                fansPanel.Cards.SelectMany(c => c.Rows).Select(r => r.Fan).ToHashSet().SetEquals(p.Fans), "Cards: G1–G4 with 2/1/3/3 fans plus the pump.");
            // Only the group has a name; each fan has its own lighting and color.
            var g3 = fansPanel.Cards[2]; g3.GroupNameBox.Text = "Bottom radiator";
            Check(p.GroupNames[g3.Mac!] == "Bottom radiator" && p.GroupTitle(g3.Mac!).EndsWith("Bottom radiator"), "Group name did not reach the profile.");
            Check(g3.Rows.Select(r => r.Title).SequenceEqual(["Fan 1", "Fan 2", "Fan 3"]) && !Descendants(g3).OfType<TextBox>().Any(t => t != g3.GroupNameBox),
                "The card must not have fan name fields.");
            var fanMenu = form.BuildElementMenu(p.Fans.Single(f => f.Group == g3.Mac && f.Fan == 2));
            Check(fanMenu.Items[0].Text!.StartsWith("Fan 3 · G3") && fanMenu.Items[0].Text!.Contains("Bottom radiator"), "Context menu does not show the group."); fanMenu.Dispose();
            g3.Rows[2].Color = "#12AB34"; g3.Rows[2].Light = LightMode.Custom; g3.RefreshValues();
            Check(p.Fans.Single(f => f.Group == g3.Mac && f.Fan == 2) is { Light: LightMode.Custom, CustomColor: "#12AB34" } && g3.Rows[0].Fan!.Light == LightMode.Mapping && g3.Rows[2].LightBox.SelectedIndex == (int)LightMode.Custom,
                "Custom color of a single fan.");
            g3.Rows[2].LightBox.SelectedIndex = (int)LightMode.Mapping;
            g3.Rows[1].LightBox.SelectedIndex = (int)LightMode.Off;
            Check(p.Fans.Single(f => f.Group == g3.Mac && f.Fan == 1).Light == LightMode.Off && g3.Rows[0].Fan!.Light == LightMode.Mapping, "Lighting in a fan row changes the wrong fan.");
            g3.Rows[1].LightBox.SelectedIndex = (int)LightMode.Mapping;
            g3.GroupNameBox.Text = ""; Check(!p.GroupNames.ContainsKey(g3.Mac!), "An empty group name must remove the name.");
            p.GroupNames[g3.Mac!] = "Bottom";
            Open(2); Check(openRgbPanel.Visible && !fansPanel.Visible, "OpenRGB tab did not open.");
            var keyboardPanel = controls.OfType<KeyboardPanel>().Single();
            Open(3); Check(keyboardPanel.Visible && !openRgbPanel.Visible && sections.Items[3] == "Keyboard", "Keyboard tab did not open.");
            var keyboardEnabled = Descendants(keyboardPanel).OfType<CheckBox>().Single(c => c.Name == "keyboardEnabled");
            keyboardEnabled.Checked = true; Check(p.Keyboard.Enabled && p.OpenRgbOutput, "Enabling the keyboard did not enable output to OpenRGB.");
            keyboardEnabled.Checked = false; Check(!p.Keyboard.Enabled, "Disabling the keyboard.");
            // Keyboard mode: Follow mapping by default; Own lighting shows its own settings and effect.
            Check(p.Keyboard.UseCaseGradient && keyboardPanel.Mode.SelectedIndex == 0 && !keyboardPanel.Effect.Visible, "By default the keyboard follows the mapping.");
            keyboardPanel.Mode.Select(1); Application.DoEvents();
            Check(!p.Keyboard.UseCaseGradient && keyboardPanel.Effect.Visible, "Own lighting did not turn on.");
            keyboardPanel.Effect.Kind.SelectedIndex = Array.FindIndex(Effects.All, e => e.Kind == EffectKind.Starfall); Application.DoEvents();
            Check(p.Keyboard.Effect.Kind == EffectKind.Starfall && p.Effect.Kind == EffectKind.Gradient, "The keyboard effect must be its own, not the mapping effect.");
            keyboardPanel.Effect.Kind.SelectedIndex = 0; keyboardPanel.Mode.Select(0); Application.DoEvents();
            var view = Descendants(keyboardPanel).OfType<KeyboardView>().Single();
            Check(Enumerable.Range(0, p.Keyboard.Map.Length).Select(view.KeyBounds).All(r => r.Width > 10 && view.ClientRectangle.Contains(Rectangle.Round(r))), "Keys do not fit the preview.");
            var coolingPanel = controls.OfType<CoolingPanel>().Single();
            Open(4); Check(coolingPanel.Visible && !keyboardPanel.Visible && sections.Items[4] == "Cooling" && coolingPanel.Rows.Count() == 5, "Cooling tab: 4 groups and the pump motor.");
            // The pump is the last table row: only Unmanaged / Constant, one line on the chart.
            var pumpRow = coolingPanel.Rows.Last();
            var pumpCell = Descendants(coolingPanel).OfType<DataGridView>().Single().Rows.Cast<DataGridViewRow>().Single(r => r.DataBoundItem == pumpRow).Cells["Source"] as DataGridViewComboBoxCell;
            Check(pumpRow.Pump && pumpRow.Fans == "pump" && pumpRow.Target == "unmanaged" && p.Cooling.PumpRpm == null && pumpCell!.Items.Count == 2, "Pump row: unmanaged by default, two items in the list.");
            pumpRow.Source = CoolingPanel.SourceLabels[4];
            Check(p.Cooling.PumpRpm == 2050 && pumpRow.Target == "2050 RPM" && !p.Cooling.Enabled && p.Cooling.Groups.All(g => g.Mac != CoolingSettings.PumpKey), "Constant pump: 2050 RPM, no curve in Groups.");
            coolingPanel.Curve.Mac = pumpRow.Mac; coolingPanel.Curve.SetPumpLine(80);
            Check(p.Cooling.PumpRpm == 2300 && pumpRow.Fixed == 78, "Pump line on the chart: 80% of the scale → 2320 → 50 step → 2300 RPM.");
            pumpRow.Fixed = 0; Check(p.Cooling.PumpRpm == 1600, "Percent input in the table for the pump.");
            pumpRow.Source = CoolingPanel.SourceLabels[0]; Check(p.Cooling.PumpRpm == null, "Unmanaged clears the pump RPM.");
            coolingPanel.Curve.SetPumpLine(50); Check(p.Cooling.PumpRpm == null, "The line must not enable an unmanaged pump.");
            var namedRow = coolingPanel.Rows.Single(r => r.Mac == g3.Mac);
            Check(namedRow.Fans == "3" && namedRow.Group.EndsWith("Bottom"), "Cooling must show the group name and fan count.");
            var coolingRow = coolingPanel.Rows.First(); coolingRow.Source = "Graphics card";
            Check(p.Cooling.For(coolingRow.Mac).Source == CoolingSource.Gpu && !p.Cooling.Enabled, "Source choice not saved, or control turned itself on.");
            coolingPanel.Curve.Mac = coolingRow.Mac; coolingPanel.Curve.MovePoint(1, 10, 150);
            var moved = p.Cooling.For(coolingRow.Mac).Curve.OrderBy(c => c.Temperature).ToArray();
            Check(moved[1].Temperature == moved[0].Temperature + 1 && moved[1].Percent == 100, "A curve point must stay between its neighbours and within limits 0–100 %.");
            var curvePoint = coolingPanel.Curve.FromScreen(coolingPanel.Curve.ToScreen(60, 50));
            Check(Math.Abs(curvePoint.Temperature - 60) < .01 && Math.Abs(curvePoint.Percent - 50) < .01, "Curve coordinate conversion.");
            // Linear mode: two limits instead of points, while the points are kept.
            var gpuGroup = p.Cooling.For(coolingRow.Mac); var pointsBefore = gpuGroup.Curve.Select(c => (c.Temperature, c.Percent)).ToArray();
            coolingPanel.KindTabs.Select(1); Application.DoEvents();
            Check(gpuGroup.Kind == CurveKind.Linear && ReferenceEquals(gpuGroup.Active, gpuGroup.Linear), "The Linear switch did not enable linear mode.");
            var (fromT, fromP, toT, toP) = coolingPanel.LinearInputs;
            fromT.Value = 45; fromP.Value = 35; toT.Value = 85; toP.Value = 90;
            Check(gpuGroup.Linear[0] is { Temperature: 45, Percent: 35 } && gpuGroup.Linear[1] is { Temperature: 85, Percent: 90 }, "Linear-mode limits not saved.");
            toT.Value = 30; Check(gpuGroup.Linear[1].Temperature == 46 && toT.Value == 46, "The second temperature must stay above the first.");
            toT.Value = 85;
            Check(Math.Abs(CoolingEngine.Interpolate(gpuGroup.Active, 65) - 62.5) < 1e-9 && CoolingEngine.Interpolate(gpuGroup.Active, 30) == 35 && CoolingEngine.Interpolate(gpuGroup.Active, 99) == 90, "Linear curve computed incorrectly.");
            coolingPanel.Curve.MovePoint(0, 50, 40); Check(gpuGroup.Linear[0] is { Temperature: 50, Percent: 40 } && fromT.Value == 50, "Dragging the line end did not update the fields.");
            coolingPanel.KindTabs.Select(0); Application.DoEvents();
            Check(gpuGroup.Kind == CurveKind.Points && gpuGroup.Curve.Select(c => (c.Temperature, c.Percent)).SequenceEqual(pointsBefore), "Switching back to points lost the points.");
            var coolingLabels = Descendants(coolingPanel).OfType<Label>().Select(l => l.Text).ToArray();
            Check(coolingLabels.Contains("Sensor lost, %") && coolingLabels.Contains("Slow down after cooling by, °C") && coolingLabels.Contains("Minimum, %"), "Clear labels for cooling settings.");
            // Effect on Mapping: the choice changes the profile, parameters and palette strip.
            var effectBar = controls.OfType<EffectBar>().Single(b => !Descendants(keyboardPanel).Contains(b));
            Check(p.Effect.Kind == EffectKind.Gradient && effectBar.Kind.Items.Count == 11, "Default is the plain gradient, 11 items in the list.");
            effectBar.Kind.SelectedIndex = Array.FindIndex(Effects.All, e => e.Kind == EffectKind.Fire); Application.DoEvents();
            Check(p.Effect.Kind == EffectKind.Fire && effectBar.Parameters.Count == 3 && controls.OfType<GradientStrip>().First(s => s.Compact).Model.Serialize().StartsWith("#1A0000"), "Fire: parameters or palette in the strip.");
            effectBar.Parameters[0].Value = 90; effectBar.Speed.Value = 2;
            Check(p.Effect.Current.Values[0] == 90 && p.Effect.Current.Speed == 2, "Effect parameters not saved.");
            effectBar.Kind.SelectedIndex = 0; Application.DoEvents();
            Check(p.Effect.Kind == EffectKind.Gradient && p.Effect.For(EffectKind.Fire).Values[0] == 90, "Switching back to the gradient lost the fire settings.");
            var screensPanel = controls.OfType<ScreensPanel>().Single();
            Open(5); Check(screensPanel.Visible && !coolingPanel.Visible && sections.Items[5] == "Screens", "Screens tab did not open.");
            // Without USB (self-test): only the pump card, and nothing is sent.
            Check(screensPanel.Cards.Count == 1 && screensPanel.Cards[0].Id == ScreenSettings.PumpId && p.Screens.Single().Mode == ScreenMode.Unmanaged, "Pump card defaults to Unmanaged.");
            screensPanel.Cards[0].Mode.Select((int)ScreenMode.Sensor); Application.DoEvents();
            Check(p.Screens.Single().Mode == ScreenMode.Sensor, "Screen mode not saved in the profile.");
            screensPanel.Cards[0].Mode.Select((int)ScreenMode.Mapping); Application.DoEvents();
            Check(p.Screens[0].Mode == ScreenMode.Mapping, "Mapping mode not selected");
            p.Screen("test-lcd", "LCD"); screensPanel.Reload();
            var lcd = screensPanel.Cards.Single(c => c.Id == "test-lcd");
            lcd.Mode.Select((int)ScreenMode.Mapping); lcd.MappingSource.SelectedIndex = 1; Application.DoEvents();
            Check(p.Screens.Single(s => s.Id == "test-lcd").MappingGroup.Length > 0, "LCD source selector does not save the association");
            lcd.MappingSource.SelectedIndex = 0;
            Check(p.Screens.Single(s => s.Id == "test-lcd").MappingGroup.Length == 0, "LCD source selector cannot clear the association");
            form.ShowTab(0); Application.DoEvents(); Check(canvas.Visible && sections.SelectedIndex == 0, "Back to mapping.");
            var select = typeof(MainForm).GetMethod("SelectGroup", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            foreach (int i in new[] { 3, 0, 2, 1 })
            {
                select.Invoke(form, [CaseLayout.DemoMacs[i], false]);
                Check(canvas.SelectedGroup == CaseLayout.DemoMacs[i], "Selection did not reach the canvas.");
            }
            coordinates.PerformClick(); Application.DoEvents();
            Check(table.Visible && table.DisplayedRowCount(false) > 0 && (table.CurrentRow?.DataBoundItem as FanPlacement)?.Group == CaseLayout.DemoMacs[1], "Coordinates did not open on the selected group.");
            table.CurrentCell = table.Rows[2].Cells[1]; Check(canvas.SelectedGroup == ((FanPlacement)table.Rows[2].DataBoundItem!).Group, "Row selection did not reach the canvas.");
            coordinates.PerformClick(); Application.DoEvents(); Check(!table.Visible, "Coordinates did not close.");
            var fanCenter = canvas.ToScreen(p.Fans[0].X, p.Fans[0].Y); var pumpCenter = canvas.ToScreen(p.Pump.X, p.Pump.Y);
            Check(ReferenceEquals(canvas.HitTest(Point.Round(fanCenter)), p.Fans[0]) && ReferenceEquals(canvas.HitTest(Point.Round(pumpCenter)), p.Pump), "Right-click does not find the fan or pump.");
            var button = controls.OfType<PillButton>().First(); var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            Check(button.FillOpacity == PillButton.Rest && Math.Abs(PillButton.Rest - .08) < 1e-9 && button.Height == 32, "Idle button: 8% white, height 32.");
            typeof(PillButton).GetMethod("OnMouseEnter", flags)!.Invoke(button, [EventArgs.Empty]);
            Check(Math.Abs(button.FillOpacity - .20) < 1e-9, "On hover the fill must be 20 %.");
            typeof(PillButton).GetMethod("OnMouseLeave", flags)!.Invoke(button, [EventArgs.Empty]);
            var back = Color.FromArgb(19, 22, 30); var blended = PillButton.Blend(back, Color.White, .08);
            Check(blended.R == 38 && blended.G == 41 && blended.B == 48, "8% white blend computed incorrectly.");
            form.Close();
        });
        Test("Per-element lighting: context menu, off and custom color for a fan, the pump and OpenRGB, back to mapping, saving", () =>
        {
            var p = new Profile { Brightness = .5, Gamma = 1 }; CaseLayout.Populate(p);
            using var form = new MainForm(false, p) { ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Location = new Point(-30000, -30000) };
            form.Show(); Application.DoEvents();
            var fan = p.Fans.First(f => f.Group == CaseLayout.DemoMacs[2] && f.Fan == 1);
            var original = Gradient.Frame(p, fan.Group, 3, .4);
            var menu = form.BuildElementMenu(fan);
            Check(menu.Items["mapping"] is ToolStripMenuItem { Checked: true } && menu.Items["custom"] != null && menu.Items["off"] != null, "Element menu incomplete.");
            menu.Items["off"]!.PerformClick(); menu.Dispose();
            var frame = Gradient.Frame(p, fan.Group, 3, .4);
            Check(fan.Light == LightMode.Off && frame.AsSpan(120, 120).IndexOfAnyExcept((byte)0) < 0 && frame.AsSpan(0, 120).IndexOfAnyExcept((byte)0) >= 0, "The wrong fan was turned off.");
            fan.Light = LightMode.Custom; fan.CustomColor = "#FF8040";
            frame = Gradient.Frame(p, fan.Group, 3, .4);
            Check(frame.AsSpan(120, 120).ToArray().Chunk(3).All(c => c.SequenceEqual(new byte[] { 128, 64, 32 })), "Custom color ignores global brightness.");
            menu = form.BuildElementMenu(fan); Check(menu.Items["custom"] is ToolStripMenuItem { Checked: true }, "Menu does not check the custom color.");
            menu.Items["mapping"]!.PerformClick(); menu.Dispose();
            Check(fan.Light == LightMode.Mapping && Gradient.Frame(p, fan.Group, 3, .4).SequenceEqual(original), "Back to mapping did not restore the frame.");
            p.Pump.Light = LightMode.Custom; p.Pump.CustomColor = "#00FF00";
            Check(Gradient.PumpFrame(p, .3).Chunk(3).All(c => c.SequenceEqual(new byte[] { 0, 128, 0 })), "Custom pump color.");
            p.Pump.Light = LightMode.Off; Check(Gradient.PumpFrame(p, .3).All(b => b == 0), "Pump off.");
            var gpu = new ExternalPlacement { Key = "gpu|1", Name = "GPU", Type = 2, X = 110, Y = 434, Width = 50, Height = 10, Light = LightMode.Off };
            var stops = Gradient.ParsePalette(p.Palette);
            Check(ExternalLayout.Frame(p, stops, gpu, 1, .3).All(b => b == 0), "Device off OpenRGB.");
            gpu.Light = LightMode.Custom; gpu.CustomColor = "#FFFFFF"; gpu.Brightness = .5;
            Check(ExternalLayout.Frame(p, stops, gpu, 1, .3).SequenceEqual(new byte[] { 64, 64, 64 }), "Custom OpenRGB color uses both brightness values.");
            menu = form.BuildElementMenu(gpu); menu.Items["off"]!.PerformClick(); menu.Dispose(); Check(gpu.Light == LightMode.Off, "Device menu OpenRGB.");
            p.External = [gpu]; fan.Light = LightMode.Custom;
            var restored = System.Text.Json.JsonSerializer.Deserialize<Profile>(System.Text.Json.JsonSerializer.Serialize(p))!; restored.Validate();
            Check(restored.Fans.Single(f => f.Group == fan.Group && f.Fan == 1).Light == LightMode.Custom && restored.Pump.Light == LightMode.Off && restored.External[0].Light == LightMode.Off && restored.Fans[0].Light == LightMode.Mapping, "Per-element lighting not saved.");
            Check(System.Text.Json.JsonSerializer.Serialize(p).Contains("\"Light\":\"Custom\""), "Lighting mode must be saved as a word.");
            fan.CustomColor = "red"; Reject(p.Validate); fan.CustomColor = "#FF8040";
            form.Close();
        });
        Test("Case migration: rows keep their height, nothing swaps twice, colors and layers survive", () =>
        {
            var p = new Profile { Direction = 45, Palette = "#FF0000, #00FF00", ShowCanvasGradient = false };
            CaseLayout.Populate(p); p.LayoutPreset = "old-case-photo-v1";
            foreach (var fan in p.Fans.Where(f => f.Group == CaseLayout.DemoMacs[2])) fan.Y = 75;
            foreach (var fan in p.Fans.Where(f => f.Group == CaseLayout.DemoMacs[3])) fan.Y = 545;
            p.GroupNames[CaseLayout.DemoMacs[2]] = "Top"; p.GroupNames[CaseLayout.DemoMacs[3]] = "Bottom";
            p.InnerLayer.Enabled = false; p.OuterLayer.UseCanvasPalette = false; p.OuterLayer.Palette = "#0000FF, #00FFFF";
            p.Fans[3].X = 190; p.Fans[6].X = 210;
            string path = Path.Combine(Path.GetTempPath(), "wcrgb-case-migrate-" + Guid.NewGuid() + ".json");
            try
            {
                p.Save(path); var loaded = Profile.Load(path);
                Check(loaded.Fans[3].X == 110 && loaded.Fans[3].Y == 79 && loaded.Fans[6].X == 110 && loaded.Fans[6].Y == 526, "New case geometry not applied.");
                Check(loaded.GroupNames[CaseLayout.DemoMacs[2]] == "Top" && loaded.GroupNames[CaseLayout.DemoMacs[3]] == "Bottom", "Side names not updated.");
                Check(loaded.Direction == 45 && loaded.Palette == p.Palette && !loaded.InnerLayer.Enabled && loaded.OuterLayer.Palette == p.OuterLayer.Palette && !loaded.ShowCanvasGradient, "Migration lost settings.");
                Check(!CaseLayout.Upgrade(loaded) && loaded.Fans[3].Y == 79, "Groups swapped a second time.");
                loaded.Fans[3].X = 110;
                loaded.Save(path); var again = Profile.Load(path); Check(again.Fans[3].X == 110 && again.LayoutPreset == CaseLayout.Id, "Loading overwrote the new geometry.");
            }
            finally { if (File.Exists(path)) File.Delete(path); }
        });
        Test("Canvas field: same phase as LEDs, angled motion and matching raster colors", () =>
        {
            var p = new Profile { Brightness = 1, Palette = PaletteModel.Rainbow().Serialize(), ColorSpace = ColorSpace.Hsv };
            var stops = Gradient.ParsePalette(p.Palette);
            using var field = new CanvasField(); using var bitmap = new Bitmap(240, 200); using var graphics = Graphics.FromImage(bitmap);
            foreach (double angle in new double[] { 0, 45, 90, 135, 180, -90, -45 })
            {
                p.Direction = angle; var travel = Gradient.TravelDirection(angle); var point = new LedPoint(91.5, 141.5);
                var before = Gradient.At(p, stops, point, 0);
                var advected = Gradient.At(p, stops, new(point.X + travel.X*p.Wavelength*.25, point.Y + travel.Y*p.Wavelength*.25), p.PeriodSeconds*.25);
                Check(before.ToArgb() == advected.ToArgb(), "The arrow does not match the actual motion direction.");
                field.Draw(graphics, new RectangleF(0, 0, 240, 200), 1, p, stops, 0);
                var pixel = bitmap.GetPixel(91, 141);
                Check(Math.Abs(pixel.R - (before.R*.55 + 12*.45)) < 6 && Math.Abs(pixel.G - (before.G*.55 + 15*.45)) < 6 && Math.Abs(pixel.B - (before.B*.55 + 22*.45)) < 6, "The background takes its color from another coordinate.");
                field.Draw(graphics, new RectangleF(0, 0, 240, 200), 1, p, stops, 1.5);
                Check(bitmap.GetPixel(91, 141).ToArgb() != pixel.ToArgb(), "The field does not move over time.");
            }
        });
        Test("GUI: angle as a number and a dial, field checkbox and parameter saving", () =>
        {
            var p = new Profile { Direction = 37 }; CaseLayout.Populate(p);
            using var form = new MainForm(false, p) { ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Location = new Point(-30000, -30000) };
            IEnumerable<Control> Descendants(Control parent) => parent.Controls.Cast<Control>().SelectMany(c => new[] { c }.Concat(Descendants(c)));
            var controls = Descendants(form).ToArray();
            var dial = controls.OfType<DirectionDial>().Single(d => d.Parent is Canvas); var number = controls.OfType<NumericUpDown>().Single(c => c.Name == "motionAngle");
            var show = controls.OfType<CheckBox>().Single(c => c.Text == "Gradient on case");
            form.Show(); Application.DoEvents();
            number.Value = 90; Application.DoEvents(); Check(p.Direction == 90 && dial.Angle == 90, "Numeric angle did not reach the dial.");
            var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            var down = typeof(DirectionDial).GetMethod("OnMouseDown", flags)!; var up = typeof(DirectionDial).GetMethod("OnMouseUp", flags)!;
            var mouse = new MouseEventArgs(MouseButtons.Left, 1, dial.Width/2 + 30, (dial.Height-24)/2 - 30, 0);
            down.Invoke(dial, [mouse]); up.Invoke(dial, [mouse]);
            Check(p.Direction == -45 && number.Value == -45, "Dragging the dial did not change the angle.");
            show.Checked = false; Check(!p.ShowCanvasGradient, "The checkbox did not turn off the field.");
            var restored = System.Text.Json.JsonSerializer.Deserialize<Profile>(System.Text.Json.JsonSerializer.Serialize(p))!;
            Check(restored.Direction == -45 && !restored.ShowCanvasGradient, "Field parameters lost."); form.Close();
        });
        Test("Layout projections: side masks, front-facing LCDs, 30° bottom row and LEDs inside the mask", () =>
        {
            var p = new Profile(); CaseLayout.Populate(p);
            var front = p.Fans[0]; var left = p.Fans[2]; var bottom = p.Fans[3]; var top = p.Fans[6];
            var a = Gradient.FanBounds(front); var b = Gradient.FanBounds(left); var c = Gradient.FanBounds(bottom); var d = Gradient.FanBounds(top);
            Check(Math.Abs(a.Width-a.Height) < .01 && a.Width > 160, "Front-facing fan is not square.");
            Check(b.Width/b.Height < .25 && d.Height/d.Width < .26 && bottom.Elevation == 30 && c.Height/c.Width is > .4f and < .5f, "Wrong side/tilted projection.");
            foreach (var fan in p.Fans)
            for (int led = 0; led < 40; led++)
            {
                var bounds = Gradient.FanBounds(fan); bounds.Inflate(.1f, .1f); var point = Gradient.WorldPoint(p, fan, led);
                Check(bounds.Contains((float)point.X, (float)point.Y), "LED ended up outside the mask.");
            }
            Check(p.Pump.X == 199 && p.Pump.Y == 221 && p.Pump.Diameter == 111, "Pump is offset from the reference layout.");
            bottom.Elevation = 91; Reject(p.Validate); bottom.Elevation = 30; bottom.Scale = double.NaN; Reject(p.Validate);
        });
        Test("Pump: 24 ring points, world phase, separate palette, off and saving", () =>
        {
            var p = new Profile { Gamma = 1, Brightness = 1 }; CaseLayout.Populate(p); var common = Gradient.ParsePalette(p.Palette);
            byte[] frame = Gradient.PumpFrame(p, .5);
            for (int led = 0; led < 24; led++)
            {
                var point = Gradient.PumpPoint(p.Pump, led); var color = Gradient.At(p, common, point, .5);
                Check(frame.AsSpan(led*3, 3).SequenceEqual(new byte[] { color.R, color.G, color.B }), "Ring does not use the shared canvas.");
            }
            p.Pump.Layer.UseCanvasPalette = false; p.Pump.Layer.Palette = "#00FF00, #00FF00"; p.Pump.Layer.Brightness = .5;
            Check(Gradient.PumpFrame(p, 0).Chunk(3).All(c => c.SequenceEqual(new byte[] { 0, 128, 0 })), "The ring's own palette does not work.");
            p.Pump.Enabled = false; Check(Gradient.PumpFrame(p, 0).All(b => b == 0), "Turning off does not send a black frame.");
            var restored = System.Text.Json.JsonSerializer.Deserialize<Profile>(System.Text.Json.JsonSerializer.Serialize(p))!; restored.Validate();
            Check(!restored.Pump.Enabled && restored.Pump.Layer.Palette == p.Pump.Layer.Palette, "Pump lost its parameters.");
            using var layers = new LayerEditor(p); layers.Pump.Brightness = .1; Check(p.Pump.Layer.Brightness == .5, "Cancelling the editor changes the original pump layer.");
        });
        Test("HydroShift: DES-CBC matches an independent OpenSSL, headers/tail and a ban on PWM", () =>
        {
            foreach (var (command, hash) in new (byte, string)[]
            {
                (10, "C5A8076ED6AFF0F9A4F87289E37792316B296A65AFA4EDB23805538612B13587"),
                (52, "6F3D9940420F7D2800992A0D2A6960362FB4802BA0BFFDCF7A08673B92473AA8"),
                (123, "18D625B9DD0D51DE4C19FCD88CC6A16026B0CBAAFCBF0A16426913E8B77F4BC7"),
                (252, "7A796286B3388C79511AFFD25B07C8ED1E44105E57719B1422620EA804144C3E")
            })
                Check(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(HydroProtocol.Header(command, 0x01020304, command == 252 ? 1234 : 0))) == hash, "Header differs from OpenSSL.");
            var upload = new RgbUpload([new byte[72], Enumerable.Repeat((byte)50, 72).ToArray()], 5300);
            byte[] payload = HydroProtocol.Payload(upload);
            Check(payload.AsSpan(0, payload.Length-4).SequenceEqual(upload.Compressed) && payload[^4] == 0 && payload[^3] == 2 && payload[^2] == 53 && payload[^1] == 24, "Wrong pump RGB tail.");
            Reject(() => HydroProtocol.Header(0xFB, 1)); Reject(() => HydroProtocol.Header(0x0B, 1)); Reject(() => HydroProtocol.Header(0xFC, 1));
            Reject(() => HydroProtocol.Payload(new RgbUpload([new byte[120]], 5300)));
            Reject(() => HydroProtocol.Payload(new RgbUpload([new byte[72]], 5333)));
            var reply = HydroTestLink.Reply(); Check(HydroProtocol.Firmware(reply) == "test-H2", "Version not parsed.");
            Check(HydroProtocol.Firmware(reply[..39]) == null, "Incomplete version reply accepted.");
            Check(HydroProtocol.Header(0xFA, 1).Length == 512, "No safe parameter request.");
        });
        Test("HydroShift: USB RGB is not sent with a confirmed RF binding", () =>
        {
            using var link = new HydroTestLink { LinkMac = "D0D0D5E566E1" }; uint stamp = 0;
            Reject(() => HydroTransfer.Send(link, new RgbUpload([new byte[72]], 5300), () => ++stamp, pause: _ => { }, boundHydroMacs: ["D0D0D5E566E1"]));
            Check(link.Commands.SequenceEqual(new byte[] { 0x7B, 0x34, 0x0A, 0xFA }) && link.Reopens == 0, "RGB sent to an RF pump, or the diagnostics order changed.");
        });
        Test("Pump over radio: H2 record, L-Wireless route instead of USB, shared cycle and 24 LEDs in the RF header", () =>
        {
            byte[] record = new byte[42]; Convert.FromHexString("D0D0D5E566E1").CopyTo(record, 0); Convert.FromHexString("D0D0D06232DC").CopyTo(record, 6);
            record[12] = 8; record[13] = 5; record[18] = 10; record[41] = 0x1C;
            var h2 = WirelessGroup.Parse(record)!;
            Check(h2.IsHydro && h2.RgbSupported && !h2.Supported && h2.LedCount == 24, "Pump record parsed incorrectly.");
            int slot = 1;
            WirelessGroup Sl(string mac, int fans) => new(mac, "D0D0D06232DC", 8, (byte)++slot, fans, Enumerable.Repeat((byte)20, fans).Concat(new byte[4 - fans]).ToArray(), new byte[4], false, 2);
            int[] counts = [2, 1, 3, 3];
            var sl = CaseLayout.DemoMacs.Select((mac, i) => Sl(mac, counts[i])).ToArray();
            Check(PumpRouting.Decide([.. sl, h2], true).Route == PumpRoute.Radio, "A bound pump is not sent over radio.");
            Check(PumpRouting.Decide(sl, true).Route == PumpRoute.Usb && PumpRouting.Decide(sl, false).Route == PumpRoute.None, "Unbound pump: wrong route.");
            Check(PumpRouting.Decide([.. sl, h2 with { Fans = 3 }], true).Route == PumpRoute.None, "A pump with fans was accepted without a confirmed layout.");
            Check(PumpRouting.Decide([.. sl, h2, h2 with { Mac = "D0D0D5E566E2" }], true).Route == PumpRoute.None, "Two pumps accepted.");
            var p = new Profile { Brightness = 1 }; CaseLayout.Populate(p);
            var scene = RgbScene.Prepare(p, sl, includePump: true);
            Check(scene.Pump != null && scene.Pump.Leds == 24 && scene.Uploads.Values.All(u => u.Interval == scene.Pump.Interval && u.Frames == scene.Pump.Frames), "Ring and fans are not in one cycle.");
            var raw = TinyUz.Decompress(scene.Pump!.Compressed, scene.Pump.Frames * 72);
            Check(raw.AsSpan(0, 72).SequenceEqual(Gradient.PumpFrame(p, 0)), "RF-pump frame differs from the canvas.");
            var first = scene.Pump.Packets(h2.Mac, h2.Master, h2.Channel, h2.Slot).First();
            Check(first[0] == 0x10 && first[2] == 8 && first[3] == 5 && first[5] == 0x20 && first.AsSpan(6, 6).SequenceEqual(Convert.FromHexString(h2.Mac)) && first[31] == 24, "Wrong pump RF header.");
        });
        Test("Placement matches the reference layout: fan and ring sizes and positions", () =>
        {
            var p = new Profile(); CaseLayout.Populate(p);
            // Original photo scheme, adjusted to join neighboring masks without changing group centers.
            RectangleF Scheme(float x0, float y0, float x1, float y1) => RectangleF.FromLTRB(x0*.6f-24, y0*.6f, x1*.6f-24, y1*.6f);
            var expected = new Dictionary<(int Group, int Fan), RectangleF>
            {
                [(0, 0)] = new(437, 288.5f, 168, 168), [(0, 1)] = new(437, 120.5f, 168, 168), [(1, 0)] = Scheme(66, 192, 130, 506),
                [(2, 0)] = new(26, 489.25f, 168, 73.5f), [(2, 1)] = new(194, 489.25f, 168, 73.5f), [(2, 2)] = new(362, 489.25f, 168, 73.5f),
                [(3, 0)] = new(26, 58, 168, 42), [(3, 1)] = new(194, 58, 168, 42), [(3, 2)] = new(362, 58, 168, 42),
            };
            foreach (var fan in p.Fans)
            {
                var want = expected[(Array.IndexOf(CaseLayout.DemoMacs, fan.Group), fan.Fan)]; var got = Gradient.FanBounds(fan);
                Check(Math.Abs(got.Left-want.Left) < 4 && Math.Abs(got.Right-want.Right) < 4 && Math.Abs(got.Top-want.Top) < 4 && Math.Abs(got.Bottom-want.Bottom) < 4,
                    $"{fan.Group}/{fan.Fan}: {got} instead of {want}.");
            }
            Check(Math.Abs(p.Pump.X-(372*.6-24)) < 2 && Math.Abs(p.Pump.Y-369*.6) < 2 && Math.Abs(p.Pump.Diameter-186*.6) < 2, "Pump ring is not where the layout puts it.");
        });
        Test("LED map from the 0.1.7 video: bars instead of a ring, right banks left/right from bottom to top, bottom row left to right, upgrade v3", () =>
        {
            var p = new Profile(); CaseLayout.Populate(p);
            var right = p.Fans[0]; var bottom = p.Fans[3];
            LedPoint W(FanPlacement f, int led) => Gradient.WorldPoint(p, f, led);
            Check(Enumerable.Range(0, 12).All(i => W(right, i).X < right.X) && Enumerable.Range(20, 12).All(i => W(right, i).X > right.X), "Right fans: bank 0 must be the left bar, bank 1 the right one.");
            Check(new[] { 0, 20 }.All(b => Enumerable.Range(b, 11).All(i => W(right, i + 1).Y <= W(right, i).Y + 1e-9) && W(right, b + 11).Y < W(right, b).Y - 50), "Right fans: indices must grow from bottom to top.");
            Check(Enumerable.Range(32, 8).All(i => W(right, i).X > W(right, 25).X), "Right fans: the outer strip of bank 1 must be on the right.");
            foreach (var b in new[] { 0, 12, 20, 32 })
            {
                int count = b % 20 == 0 ? 12 : 8;
                Check(Enumerable.Range(b, count - 1).All(i => W(bottom, i + 1).X > W(bottom, i).X), "Bottom row: the track does not run left to right.");
            }
            Check(W(bottom, 25).Y > W(bottom, 5).Y, "Bottom row: bank 1 must be the near edge.");
            var bar = Enumerable.Range(0, 12).Select(CaseLayout.LocalPoint).ToArray();
            Check(Math.Abs(bar[0].X + 47) < .01 && Math.Abs(bar[11].X - 47) < .01 && bar.All(q => q.Y < -31), "A bar must be chamfer–edge–chamfer along one side.");
            var old = new Profile(); CaseLayout.Populate(old); old.LayoutPreset = "old-case-photo-v3";
            foreach (var f in old.Fans.Where(f => f.Group == CaseLayout.DemoMacs[0])) f.Rotation = 90;
            old.Fans[0].X = 500; old.Palette = "#112233, #445566";
            Check(CaseLayout.Upgrade(old) && old.LayoutPreset == CaseLayout.Id && old.Fans[0].Rotation == -90 && old.Fans[1].Rotation == -90 && old.Fans[0].X == 500 && old.Palette == "#112233, #445566", "v3 upgraded incorrectly.");
            Check(!CaseLayout.Upgrade(old), "Repeated upgrade.");
            Check(old.Fans[0].Y > old.Fans[1].Y, "G1·1 must be at the bottom, G1·2 at the top.");
            var v4 = new Profile(); CaseLayout.Populate(v4); v4.LayoutPreset = "old-case-photo-v4";
            foreach (var f in v4.Fans.Where(f => f.Group == CaseLayout.DemoMacs[0])) f.Y = f.Fan == 0 ? 199 : 378;
            v4.Fans[0].X = 515;
            Check(CaseLayout.Upgrade(v4) && v4.LayoutPreset == CaseLayout.Id && v4.Fans[0].Y == 372.5 && v4.Fans[1].Y == 204.5 && v4.Fans[0].X == 515 && v4.Fans[0].Rotation == -90 && !CaseLayout.Upgrade(v4), "v4 → v6: G1 did not swap places, or a gap remained.");
        });
        Test("Side view: across its width the left fan changes color as a whole, the top fans along the row, as before", () =>
        {
            var p = new Profile { Brightness = 1, Palette = "#FF0000@0, #00FF00@0.45, #FF0000@0.55", Direction = 0 }; CaseLayout.Populate(p);
            var left = p.Fans[2]; var top = p.Fans[6];
            Check(left.Elevation == 0 && top.Elevation == 0, "Expected a side view.");
            Check(Enumerable.Range(0, 40).All(led => Math.Abs(Gradient.WorldPoint(p, left, led).X - left.X) < 1e-9), "Left: LEDs spread across.");
            Check(Enumerable.Range(0, 40).All(led => Math.Abs(Gradient.WorldPoint(p, top, led).Y - top.Y) < 1e-9), "Top: LEDs spread across.");
            var stops = Gradient.ParsePalette(p.Palette);
            for (double t = 0; t < 3; t += .05)
            {
                var frame = Gradient.Frame(p, left.Group, 1, t);
                Check(frame.Chunk(3).All(c => c.SequenceEqual(frame.Take(3))), "Left fan is not a single color with horizontal motion.");
            }
            var topFrame = Gradient.Frame(p, top.Group, 3, 1.2).Chunk(3).ToArray();
            Check(topFrame.Distinct(new ByteSeq()).Count() > 3, "Top fans lost the gradient along the row.");
            p.Direction = 90; var vertical = Gradient.Frame(p, left.Group, 1, 1.2).Chunk(3).ToArray();
            Check(vertical.Distinct(new ByteSeq()).Count() > 3, "With vertical motion the left fan must change along its length.");
        });
        Test("OpenRGB-client: protocol 0–4 descriptions, Direct mode and an LED frame through the SDK server", () =>
        {
            var group = new WirelessGroup("0A0B0C0D0E0F", "010203040506", 8, 3, 2, [20, 20, 0, 0], new byte[4], false, 0);
            for (uint version = 0; version <= 4; version++)
            {
                var d = OpenRgbClient.Parse(7, OpenRgbServer.Describe(group, new byte[240], version), version);
                Check(d.Index == 7 && d.Name.StartsWith("UNI FAN SL Wireless ") && d.Zones.Length == 2 && d.Zones.All(z => z.LedCount == 40) && d.LedCount == 80 && d.Modes.Select(m => m.Name).SequenceEqual(["Direct"]) && d.Modes[0].PerLed && (version == 0 || d.Vendor == "Lian Li"), $"Protocol {version} description parsed incorrectly.");
            }
            byte[]? received = null; using var signal = new ManualResetEventSlim();
            using var server = new OpenRgbServer([group], (_, colors) => { received = colors; signal.Set(); }, 0);
            using var client = new OpenRgbClient(port: server.Port);
            var devices = client.Devices();
            Check(client.Protocol == 4 && devices.Length == 1 && devices[0].LedCount == 80 && devices[0].Location.Contains(group.Mac), "Wrong OpenRGB device list.");
            client.SetCustomMode(0); client.SetMode(0, 0, devices[0].Modes[0]);
            Check(devices[0].Modes[0].Raw.Length > 40 && devices[0].Modes[0].PerLed, "Mode description for UpdateMode was not kept.");
            byte[] frame = Enumerable.Range(0, 240).Select(i => (byte)(i % 3 == 0 ? 200 : i % 3 == 1 ? 10 : 30)).ToArray();
            client.UpdateLeds(0, frame);
            Check(signal.Wait(3000) && received!.SequenceEqual(frame), "UpdateLEDs frame did not reach the server.");
        });
        Test("OpenRGB-placement: memory and GPU on the canvas, duplicate GPU off, frame matches the gradient", () =>
        {
            OpenRgbMode[] modes = [new("Direct", 0x20, 1, []), new("Off", 0, 0, []), new("Static", 0x20, 1, []), new("Rainbow", 3, 0, [])];
            OpenRgbDevice Device(int index, int type, string name, string location, int leds) => new(index, type, name, "", "", "", location, type == 1 ? modes : [modes[0]], 0, [new("Zone", 1, leds, 0, 0, null)], leds);
            OpenRgbDevice[] found = [Device(0, 2, "Gigabyte GeForce RTX 5080 Aero OC", "I2C: GPU 0, address 0x75", 1), Device(1, 2, "Gigabyte AERO GeForce RTX 5080 OC SFF", "I2C: GPU 0, address 0x75", 1),
                Device(2, 1, "ENE DRAM", "I2C: PIIX4, address 0x70", 8), Device(3, 1, "ENE DRAM", "I2C: PIIX4, address 0x71", 8), Device(4, 5, "AULA F75", "HID: kb", 90)];
            var placed = ExternalLayout.Merge([], found);
            Check(placed.Count == 5 && placed[0].Enabled && !placed[1].Enabled && placed[2].Enabled && placed[3].Enabled && !placed[4].Enabled, "Wrong default device selection.");
            Check(placed[2].X < placed[3].X && placed[2].Height == 140 && placed[2].Height > placed[2].Width && placed[0].Y > 400, "Memory/GPU not in place.");
            Check(ExternalLayout.ModeIndex(found[2], placed[2]) == 2 && ExternalLayout.ModeIndex(found[0], placed[0]) == 0, "Auto mode: memory must use Static and the GPU Direct.");
            placed[3].Mode = "Direct"; Check(ExternalLayout.ModeIndex(found[3], placed[3]) == 0, "Manual Direct choice does not work."); placed[3].Mode = "";
            var old = new ExternalPlacement { Key = found[2].Key, Name = "ENE DRAM", Type = 1, X = 300, Y = 240, Width = 8, Height = 200 };
            Check(ExternalLayout.Merge([old], found)[2].Height == 140 && old.X == 300, "Memory height from 0.2.0 was not reduced by 30 %.");
            placed[2].X = 280; placed[1].Enabled = true;
            var again = ExternalLayout.Merge(placed, found.Take(3).ToArray());
            Check(again[2].X == 280 && again[1].Enabled && again.Count == 5, "Rediscovery reset the position or lost the missing stick.");
            var ram = placed[2];
            Check(ExternalLayout.Point(ram, 0, 8).Y < ExternalLayout.Point(ram, 7, 8).Y && ExternalLayout.Point(placed[0], 0, 1) == new LedPoint(placed[0].X, placed[0].Y), "Wrong layout LED.");
            ram.Reverse = true; Check(ExternalLayout.Point(ram, 0, 8).Y > ExternalLayout.Point(ram, 7, 8).Y, "Reverse order does not work.");
            var p = new Profile { Gamma = 1, Brightness = 1, Palette = "#FF0000, #0000FF" }; var stops = Gradient.ParsePalette(p.Palette);
            var gpu = ExternalLayout.Frame(p, stops, placed[0], 1, 1.5); var want = Gradient.Sample(stops, Gradient.Phase(p, new(placed[0].X, placed[0].Y), 1.5), p.ColorSpace, p.SaturationBoost, 1);
            Check(gpu.SequenceEqual(new[] { want.R, want.G, want.B }), "GPU color does not match the canvas.");
            placed[0].Brightness = 0; Check(ExternalLayout.Frame(p, stops, placed[0], 1, 1.5).All(b => b == 0), "Device brightness not applied.");
            p.External = placed; p.Validate();
            var restored = System.Text.Json.JsonSerializer.Deserialize<Profile>(System.Text.Json.JsonSerializer.Serialize(p))!; restored.Validate();
            Check(restored.External.Count == 5 && restored.External[2].X == 280 && restored.External[2].Reverse, "OpenRGB placements not saved in the profile.");
        });
        Test("Phase from the radio controller clock: frame = ticks·100/interval mod frames (as upstream sync_clock)", () =>
        {
            Check(Math.Abs(Controller.PhaseAt(0, 0, 5300, 180) - 0) < 1e-12, "Zero phase.");
            Check(Math.Abs(Controller.PhaseAt(53 * 90, 0, 5300, 180) - .5) < 1e-12, "Half a cycle in ticks.");
            Check(Math.Abs(Controller.PhaseAt(53 * 90, 53 * 45 / 1600.0, 5300, 180) - .75) < 1e-9, "Time since the clock sample was not counted.");
            Check(Math.Abs(Controller.PhaseAt(53 * 180 * 7 + 53, 0, 5300, 180) - 1.0 / 180) < 1e-12, "Cycle does not wrap.");
            Check(Controller.PhaseAt(uint.MaxValue, 1, 5300, 180) is >= 0 and < 1, "Tick overflow.");
        });
        Test("LED gamma: red #FF1F1F does not turn pink, white is limited after gamma, the preview is unchanged", () =>
        {
            var p = new Profile { Brightness = 1 };
            Check(p.Gamma == 2.2, "Default gamma must be 2,2.");
            Check(Gradient.Device(p, Color.FromArgb(255, 31, 31), true) == Color.FromArgb(255, 2, 2), "#FF1F1F stays pink on LEDs.");
            Check(Gradient.Device(p, Color.FromArgb(255, 128, 128), true) == Color.FromArgb(255, 56, 56), "Middle of the white→red transition not corrected.");
            Check(Gradient.Device(p, Color.White, true).ToArgb() == Color.FromArgb(200, 200, 200).ToArgb() && Gradient.Device(p, Color.White, false).ToArgb() == Color.White.ToArgb(), "Current limit not applied after gamma.");
            p.Gamma = 1; Check(Gradient.Device(p, Color.FromArgb(255, 31, 31), true) == Color.FromArgb(255, 31, 31), "Gamma 1 must leave the color unchanged.");
            p.Gamma = 2.2; p.Palette = "#FF0000@0.375, #FFFFFF@0.389, #FFFFFF@0.526, #FF1F1F@0.533"; p.ColorSpace = ColorSpace.Hsv; CaseLayout.Populate(p);
            var stops = Gradient.ParsePalette(p.Palette);
            var preview = Gradient.LedColor(p, stops, stops, stops, 0, new(0, 0), .5);
            var device = Gradient.LedColor(p, stops, stops, stops, 0, new(0, 0), .5, true);
            Check(preview != device && device == Gradient.Device(p, Gradient.Sample(stops, Gradient.Phase(p, new(0, 0), .5), p.ColorSpace, p.SaturationBoost, 1), true), "Preview and LED frame are separated incorrectly.");
            // The long stretch after white (#FF1F1F → #FF0000) must be pure red on LEDs.
            for (double phase = .54; phase < 1.37; phase += .01)
            {
                var c = Gradient.Device(p, Gradient.Sample(stops, phase, p.ColorSpace, p.SaturationBoost, 1), true);
                Check(c.R == 255 && c.G <= 3 && c.B <= 3, $"Phase {phase:F2}: pink on LEDs {c}.");
            }
            Check(Gradient.Frame(p, CaseLayout.DemoMacs[2], 3, .5).Length == 360, "Bottom row frame not built.");
            p.Gamma = .9; Reject(p.Validate); p.Gamma = 3.1; Reject(p.Validate);
        });
        Test("Color picker: HEX paste (#RRGGBB, RRGGBB, #RGB, 0x…), exact color, invalid input rejected", () =>
        {
            foreach (var text in new[] { "#FF1F1F", "ff1f1f", " #ff1F1f ", "0xFF1F1F" })
                Check(ColorPickerDialog.TryParseHex(text, out var c) && c.ToArgb() == Color.FromArgb(255, 31, 31).ToArgb(), $"Not accepted {text}.");
            Check(ColorPickerDialog.TryParseHex("#F11", out var shortColor) && shortColor.ToArgb() == Color.FromArgb(255, 17, 17).ToArgb(), "Short form.");
            foreach (var text in new[] { "", "#FF1F1", "#GG0000", "red", "#FF1F1F1F" }) Check(!ColorPickerDialog.TryParseHex(text, out _), $"Accepted an invalid {text}.");
            using var dialog = new ColorPickerDialog(Color.FromArgb(10, 20, 30)) { ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Location = new Point(-30000, -30000) };
            dialog.Show(); Application.DoEvents();
            IEnumerable<Control> All(Control c) => c.Controls.Cast<Control>().SelectMany(x => new[] { x }.Concat(All(x)));
            var hex = All(dialog).OfType<TextBox>().Single(t => t.Name == "hex");
            Check(hex.Text == "#0A141E", "HEX does not show the initial color.");
            hex.Text = "#FF1F1F"; Check(dialog.SelectedColor.ToArgb() == Color.FromArgb(255, 31, 31).ToArgb(), "Pasted HEX not selected exactly.");
            hex.Text = "#12"; Check(dialog.SelectedColor.ToArgb() == Color.FromArgb(255, 31, 31).ToArgb(), "Incomplete input changed the color.");
            hex.Text = "00d9df"; Check(dialog.SelectedColor.ToArgb() == Color.FromArgb(0, 217, 223).ToArgb(), "HEX without # not accepted.");
            dialog.Close();
        });
        Test("Keyboard: AULA F75 matrix, per-key gradient, follow mapping, brightness, matrix from OpenRGB", () =>
        {
            var p = new Profile { Gamma = 1, Palette = "#FF0000, #0000FF", PeriodSeconds = 4 }; var k = p.Keyboard;
            k.Validate();
            Check(k.Width == 15 && k.Height == 6 && KeyboardLayout.LedCount(k) == 90 && k.Map.Count(v => v != KeyboardSettings.NoLed) == 80, "AULA F75 matrix incomplete (80 keys of 90 LED).");
            Check(KeyboardLayout.Point(k, 0) == new LedPoint(.5, .5) && KeyboardLayout.Point(k, 89) == new LedPoint(14.5, 5.5) && KeyboardLayout.Point(k, 12) == new LedPoint(2.5, .5), "Keys are not in place.");
            k.UseCaseGradient = false; k.Palette = "#00FF00, #FF00FF"; k.ColorSpace = ColorSpace.Rgb; k.Direction = 0; k.Wavelength = 15; k.PeriodSeconds = 3;
            var frame = KeyboardLayout.Frame(p, 90, 0, 1, false);
            Color Led(byte[] f, int led) => Color.FromArgb(f[led * 3], f[led * 3 + 1], f[led * 3 + 2]);
            Check(Led(frame, 1).ToArgb() == Led(frame, 5).ToArgb() && Led(frame, 0).ToArgb() != Led(frame, 89).ToArgb(), "Horizontal gradient: one color per column, different edges.");
            var want = Gradient.Sample(Gradient.ParsePalette(k.Palette), KeyboardLayout.Phase(k, KeyboardLayout.Point(k, 30), 1, 3), ColorSpace.Rgb, 1, 1);
            Check(Led(frame, 30).ToArgb() == want.ToArgb(), "Keyboard's own gradient computed incorrectly.");
            k.UseCaseGradient = true;
            var sharedFrame = KeyboardLayout.Frame(p, 90, 2, 0, false);
            // Follow mapping: keys are stretched over the case canvas, same color as that canvas point at the same scene time.
            var b0 = Effects.Bounds(p); var key = KeyboardLayout.Point(k, 30);
            var sharedWant = Gradient.Shade(p, Effects.Palette(p), new LedPoint(b0.Left + key.X / k.Width * b0.Width, b0.Top + key.Y / k.Height * b0.Height), 2, p.ColorSpace, p.SaturationBoost, 1, false);
            Check(Led(sharedFrame, 30).ToArgb() == sharedWant.ToArgb(), "Follow mapping must show the case canvas at the scene time.");
            // The mapping effect reaches the keyboard; the keyboard's own effect does not depend on the mapping.
            p.Effect.Kind = EffectKind.Plasma;
            Check(!KeyboardLayout.Frame(p, 90, 2, 0, false).SequenceEqual(sharedFrame), "Mapping effect did not reach the keyboard.");
            k.UseCaseGradient = false; var ownGradient = KeyboardLayout.Frame(p, 90, 0, 1, false);
            Check(ownGradient.SequenceEqual(frame), "Own lighting must not depend on the mapping effect.");
            k.Effect.Kind = EffectKind.Ripple;
            var ownEffect = KeyboardLayout.Frame(p, 90, 0, 1.3, false);
            Check(!ownEffect.SequenceEqual(KeyboardLayout.Frame(p, 90, 0, 0.4, false)) && ownEffect.Chunk(3).Select(c => c[0] << 16 | c[1] << 8 | c[2]).Distinct().Count() > 4, "Keyboard's own effect draws no pattern.");
            k.Effect.Kind = EffectKind.Gradient; p.Effect.Kind = EffectKind.Gradient; k.UseCaseGradient = true;
            k.Brightness = 0; Check(KeyboardLayout.Frame(p, 90, 2, 0, true).All(b => b == 0), "Keyboard brightness not applied.");
            k.Brightness = .5; p.Gamma = 2.2;
            Check(KeyboardLayout.Frame(p, 90, 2, 0, true).Max() < KeyboardLayout.Frame(p, 90, 2, 0, false).Max(), "LED gamma not applied to the keyboard frame.");
            uint[] matrix = [0, 1, 2, KeyboardSettings.NoLed, 3, 4];
            var kb = new OpenRgbDevice(0, 5, "AULA F75", "", "", "", "HID", [new("Direct", 0, 1, [])], 0, [new("Keyboard", 2, 5, 3, 2, matrix)], 5);
            Check(KeyboardLayout.Adopt(k, [kb]) && k.Width == 3 && k.Height == 2 && k.Map.SequenceEqual(matrix) && !KeyboardLayout.Adopt(k, [kb]), "Matrix from OpenRGB not accepted.");
            Profile restored;
            k.Effect.Kind = EffectKind.Lava; restored = System.Text.Json.JsonSerializer.Deserialize<Profile>(System.Text.Json.JsonSerializer.Serialize(p))!; restored.Validate();
            Check(restored.Keyboard.Width == 3 && restored.Keyboard.Map.SequenceEqual(matrix) && restored.Keyboard.Brightness == .5 && restored.Keyboard.UseCaseGradient && restored.Keyboard.Effect.Kind == EffectKind.Lava, "Keyboard settings not saved.");
            k.Wavelength = 0; Reject(p.Validate);
        });
        Test("Keyboard: the OpenRGB bridge sends its gradient without a placement on the case canvas", () =>
        {
            var group = new WirelessGroup("0A0B0C0D0E0F", "010203040506", 8, 3, 2, [20, 20, 0, 0], new byte[4], false, 0);
            byte[]? received = null; using var signal = new ManualResetEventSlim();
            using var server = new OpenRgbServer([group], (_, colors) => { received = colors; signal.Set(); }, 0);
            string name; using (var probe = new OpenRgbClient(port: server.Port)) name = probe.Devices()[0].Name;
            var p = new Profile { Gamma = 1, Brightness = 1, OpenRgbOutput = true };
            p.Keyboard.Enabled = true; p.Keyboard.Device = name; p.Keyboard.UseCaseGradient = false; p.Keyboard.Palette = "#FF0000, #FF0000"; p.Keyboard.Brightness = 1;
            using var bridge = new OpenRgbBridge(() => p, () => null, server.Port);
            Check(signal.Wait(5000) && received!.Length == 240 && received.Chunk(3).All(c => c[0] > 150 && c[1] == 0 && c[2] == 0), "Keyboard frame did not reach OpenRGB.");
            Check(p.External.Count == 0, "Keyboard must not require a canvas placement.");
        });
        Test("Speed: 0x12 0x10 packet as upstream, 14% limit, never 0, RPM/PWM/coolant parsing", () =>
        {
            var g = new WirelessGroup("010203040506", "090909090909", 8, 2, 3, [20, 20, 20, 0], new byte[4], false, 0) { SlotIndex = 3 };
            var packet = Controller.PwmPacket(g, "090909090909", 8, [6, 6, 6, 6]);
            // Reference from upstream fan_speed.rs (hardware_sync_packet_preserves_all_four_sentinels).
            Check(packet.Length == 240 && packet.AsSpan(0, 21).SequenceEqual(new byte[] { 0x12, 0x10, 1, 2, 3, 4, 5, 6, 9, 9, 9, 9, 9, 9, 2, 8, 3, 6, 6, 6, 6 }) && packet.AsSpan(21).IndexOfAnyExcept((byte)0) < 0, "Speed packet differs from upstream.");
            Reject(() => Controller.PwmPacket(g with { SlotIndex = 0 }, "090909090909", 8, [1, 1, 1, 1]));
            Check(Controller.PreparePwm(g, 0).SequenceEqual(new byte[] { 35, 35, 35, 0 }) && Controller.PreparePwm(g, 100).SequenceEqual(new byte[] { 255, 255, 255, 0 }) && Controller.PreparePwm(g, 50).SequenceEqual(new byte[] { 128, 128, 128, 0 }), "PWM: 0% → 14%, empty slot → 0.");
            byte[] record = new byte[42]; Convert.FromHexString("D0D0D5E566E1").CopyTo(record, 0); Convert.FromHexString("D0D0D06232DC").CopyTo(record, 6);
            record[12] = 8; record[13] = 5; record[18] = 10; record[27] = 31; record[41] = 0x1C;
            record[28] = 0x20 | 0x04; record[29] = 0xB0; record[34] = 0x0A; record[35] = 0x8C; record[36] = 90; record[39] = 200;
            var pump = WirelessGroup.Parse(record)!;
            Check(pump.Coolant == 31 && pump.PwmLine && pump.Rpm[0] == 0x4B0 && pump.Rpm[3] == 0xA8C && pump.Pwm[0] == 90 && pump.Pwm[3] == 200 && !pump.SupportsSpeed, "Pump record: coolant, RPM, PWM.");
            record[18] = 0; record[19] = 3; record[24] = 20; record[25] = 21; record[26] = 22;
            var fans = WirelessGroup.Parse(record)!;
            Check(fans.Coolant == null && fans.SupportsSpeed, "SL group: speed supported, no coolant.");
        });
        Test("L-Wireless models: type bytes, LED count and PWM limit as in upstream fan_type.rs", () =>
        {
            (int From, int To, FanModel Model)[] ranges = [(20, 26, FanModel.SlV3), (27, 35, FanModel.Tl), (36, 39, FanModel.SlInf), (40, 42, FanModel.Cl), (43, 50, FanModel.SlInfV3), (51, 58, FanModel.TlV3), (59, 62, FanModel.SlV4), (63, 63, FanModel.P28), (126, 127, FanModel.ClV2)];
            for (int b = 0; b < 256; b++)
            {
                var expected = ranges.Where(r => b >= r.From && b <= r.To).Select(r => r.Model).SingleOrDefault();
                Check(FanModel.FromTypeByte((byte)b) == expected, $"Type byte {b} classified incorrectly.");
            }
            var leds = new Dictionary<FanModel, (int Leds, byte Pwm, int Inner)>
            {
                [FanModel.SlV3] = (40, 35, 24), [FanModel.SlV4] = (52, 35, 26), [FanModel.SlInf] = (44, 28, 28), [FanModel.SlInfV3] = (44, 25, 28),
                [FanModel.Tl] = (26, 28, 0), [FanModel.TlV3] = (26, 28, 0), [FanModel.Cl] = (24, 25, 8), [FanModel.ClV2] = (24, 25, 8), [FanModel.P28] = (9, 20, 9),
            };
            Check(leds.Count == FanModel.All.Length && FanModel.All.Select(m => m.Id).Distinct().Count() == FanModel.All.Length, "Model list incomplete.");
            foreach (var (model, (count, pwm, inner)) in leds)
            {
                Check(model.LedsPerFan == count && model.MinimumPwm == pwm && Enumerable.Range(0, count).Count(model.IsInner) == inner, $"{model.Name}: LED, PWM or layers wrong.");
                Check(Enumerable.Range(0, count).All(i => model.LocalPoint(i) is { } pt && double.IsFinite(pt.X) && double.IsFinite(pt.Y) && Math.Abs(pt.X) <= 56 && Math.Abs(pt.Y) <= 56), $"{model.Name}: LED outside the fan frame.");
                Check(!model.ConnectsToNext(count - 1) && FanModel.FromId(model.Id) == model, $"{model.Name}: track or identifier.");
                Check(model.HardwareVerified == (model == FanModel.SlV3), "Marked as hardware-verified is not only SL V3.");
            }
            // SL V3 unchanged: the old 12+8 grid per bank.
            for (int led = 0; led < 40; led++)
            {
                int bank = led / 20, index = led % 20, count = index < 12 ? 12 : 8, step = index < 12 ? index : index - 12;
                var old = new LedPoint((step / (double)(count - 1) - .5) * 100, (bank == 0 ? -1 : 1) * (index < 12 ? 38 : 50));
                Check(Math.Abs(FanModel.SlV3.LocalPoint(led).X - old.X) < 1e-9 && Math.Abs(FanModel.SlV3.LocalPoint(led).Y - old.Y) < 1e-9 && FanModel.SlV3.IsInner(led) == (index < 12), "SL V3 layout changed.");
                Check(FanModel.SlV3.ConnectsToNext(led) == led is not 11 and not 19 and not 31 and not 39, "SL V3 tracks changed.");
            }
            // Hub rings: each position holds exactly one LED; the second CL outer bank repeats the first.
            foreach (var model in new[] { FanModel.Cl, FanModel.SlInf, FanModel.P28 })
                Check(Enumerable.Range(0, 8).Select(i => model.LocalPoint(i)).Distinct().Count() == 8, $"{model.Name}: LED hub positions overlap.");
            Check(Enumerable.Range(8, 8).All(i => FanModel.Cl.LocalPoint(i) == FanModel.Cl.LocalPoint(i + 8)), "CL: second outer bank does not match the first.");
            Check(new byte[] { 23, 26, 27, 32, 35, 43, 44, 47, 48, 51, 52, 55, 56 }.All(FanModel.HasLcd) && new byte[] { 20, 22, 28, 31, 36, 45, 46, 49, 50, 53, 54, 57, 58, 59, 63, 126 }.All(b => !FanModel.HasLcd(b)), "Wrong LCD flag.");
        });
        Test("L-Wireless models: record parsing, mixed groups, SL-INF on the right, PWM and filter CL", () =>
        {
            byte[] Record(int fans, params byte[] types)
            {
                byte[] r = new byte[42]; r[0] = 1; r[6] = 9; r[12] = 8; r[13] = 2; r[19] = (byte)fans; r[41] = 0x1C; types.CopyTo(r, 24);
                for (int i = 0; i < 4; i++) { r[29 + i * 2] = (byte)(10 * (i + 1)); r[36 + i] = (byte)(50 + i); }
                return r;
            }
            var tl = WirelessGroup.Parse(Record(2, 27, 28))!;
            Check(tl.Model == FanModel.Tl && tl.Supported && tl.SupportsSpeed && tl.LedCount == 52 && !tl.RepairSupported && !tl.RightAttach, "TL Wireless parsed incorrectly.");
            Check(WirelessGroup.Parse(Record(4, 63, 63, 63, 63))!.LedCount == 36 && WirelessGroup.Parse(Record(4, 59, 60, 61, 62))!.LedCount == 208, "P28 or SL V4: count of LED.");
            var mixed = WirelessGroup.Parse(Record(2, 20, 27))!;
            Check(mixed.Model == null && !mixed.Supported && mixed.LedCount == 0, "Mixed group accepted.");
            Check(!WirelessGroup.Parse(Record(1, 64))!.Supported && !WirelessGroup.Parse(Record(0))!.Supported && !WirelessGroup.Parse(Record(5, 20, 20, 20, 20))!.Supported, "Unknown type or fan count accepted.");
            Check(WirelessGroup.Parse(Record(3, 21, 21, 21))!.RepairSupported, "SL V3 lost radio address recovery.");
            var inf = WirelessGroup.Parse(Record(13, 36, 36, 36))!;
            Check(inf.RightAttach && inf.Fans == 3 && inf.Model == FanModel.SlInf && inf.LedCount == 132 && inf.Rpm.SequenceEqual(new[] { 30, 20, 10, 40 }) && inf.Pwm.SequenceEqual(new byte[] { 52, 51, 50, 53 }), "SL-INF on the right: slot count or order.");
            var pwm = Controller.PwmPacket(inf with { SlotIndex = 2 }, "090000000000", 8, [100, 150, 200, 0]);
            Check(pwm.AsSpan(17, 4).SequenceEqual(new byte[] { 200, 150, 100, 0 }), "SL-INF on the right: PWM not reversed on the wire (upstream reverse_fan_order).");
            var plain = WirelessGroup.Parse(Record(3, 36, 36, 36))! with { SlotIndex = 2 };
            Check(!plain.RightAttach && Controller.PwmPacket(plain, "090000000000", 8, [100, 150, 200, 0]).AsSpan(17, 4).SequenceEqual(new byte[] { 100, 150, 200, 0 }), "Normal slot order changed.");
            Check(Controller.PreparePwm(tl, 0).SequenceEqual(new byte[] { 28, 28, 0, 0 }) && Controller.PreparePwm(WirelessGroup.Parse(Record(1, 63))!, 0)[0] == 20, "Model PWM limit not applied.");
            var cl = WirelessGroup.Parse(Record(2, 41, 41))!;
            foreach (var (percent, expected) in new[] { (60.0, 152), (60.2, 152), (60.6, 156), (59.5, 152), (61.2, 156) })
                Check(Controller.PreparePwm(cl, percent)[0] == expected && Controller.PreparePwm(cl, percent)[2] == 0, $"CL PWM filter for {percent} %.");
            Check(Controller.PreparePwm(tl, 60)[0] == 153, "CL filter applied to another model.");
            Reject(() => Controller.PreparePwm(mixed, 50));
        });
        Test("L-Wireless models: frame, scene, group finder, OpenRGB and profile", () =>
        {
            byte[] record = new byte[42]; record[0] = 7; record[6] = 9; record[12] = 8; record[13] = 3; record[19] = 2; record[24] = 27; record[25] = 28; record[41] = 0x1C;
            var tl = WirelessGroup.Parse(record)!;
            var p = new Profile { Palette = "#FF0000@0, #00FF00@0.5", Fans = [new() { Group = tl.Mac, Fan = 0, X = 100, Y = 100 }, new() { Group = tl.Mac, Fan = 1, X = 220, Y = 100 }] };
            // The layout still says SL V3, but the frame follows the model from the controller reply.
            byte[] frame = Gradient.Frame(p, tl.Mac, 2, .3, tl.Model);
            Check(frame.Length == 2 * 26 * 3 && Gradient.Frame(p, tl.Mac, 2, .3).Length == 2 * 40 * 3 && p.Fans.All(f => f.Model == FanModel.SlV3.Id), "Frame size does not follow the model, or rendering changed the layout.");
            Check(frame.Chunk(3).Distinct(new ByteSeq()).Count() > 3, "Gradient does not change along the arcs TL.");
            foreach (var f in p.Fans) f.Model = FanModel.Tl.Id;
            Check(Gradient.Frame(p, tl.Mac, 2, .3).SequenceEqual(frame), "Layout model and hardware model produced different frames.");
            var scene = RgbScene.Prepare(p, [tl]);
            Check(scene.Uploads[tl.Mac].Leds == 52 && TinyUz.Decompress(scene.Uploads[tl.Mac].Compressed, scene.Frames * 52 * 3).AsSpan(0, 156).SequenceEqual(Gradient.Frame(p, tl.Mac, 2, 0, tl.Model)), "TL scene built incorrectly.");
            p.Fans[1].Model = FanModel.Cl.Id; Reject(() => Gradient.Frame(p, tl.Mac, 2, 0)); p.Fans[1].Model = FanModel.Tl.Id;
            p.InnerLayer.Enabled = false;
            Check(Gradient.Frame(p, tl.Mac, 2, 0).Any(b => b != 0), "TL: turning off the inner layer also turned off the outer arcs.");
            p.InnerLayer.Enabled = true;
            Check(Identification.Frame(2, 26).Length == 156 && Identification.Frame(4, 9).Length == 108, "Group finder: marker size.");
            Reject(() => Identification.Frame(1, 41));
            var d = OpenRgbClient.Parse(1, OpenRgbServer.Describe(tl, new byte[156], 4), 4);
            Check(d.Name.StartsWith("UNI FAN TL Wireless") && d.Zones.Length == 2 && d.Zones.All(z => z.LedCount == 26) && d.LedCount == 52, "OpenRGB: zones TL.");
            // Profile: the model is saved, an old profile without the field is SL V3, an unknown model is rejected.
            string path = Path.Combine(Path.GetTempPath(), $"wcrgb-models-{Environment.ProcessId}.json");
            try
            {
                p.Save(path); Check(Profile.Load(path).Fans.All(f => f.ModelInfo == FanModel.Tl), "Model not saved in the profile.");
                File.WriteAllText(path, System.Text.RegularExpressions.Regex.Replace(File.ReadAllText(path), ",\\s*\"Model\":\\s*\"tl\"", ""));
                Check(!File.ReadAllText(path).Contains("\"Model\""), "Model field not removed from the test profile.");
                Check(Profile.Load(path).Fans.All(f => f.ModelInfo == FanModel.SlV3), "Old profile without a model did not become SL V3.");
            }
            finally { File.Delete(path); }
            p.Fans[0].Model = "unknown"; Reject(p.Validate); p.Fans[0].Model = FanModel.Tl.Id;
            p.LedOverrides[$"{tl.Mac}/0"] = [.. Enumerable.Range(0, 26).Select(i => new LedPoint(i, 0))]; p.Validate();
            Check(Math.Abs(Gradient.WorldPoint(p, p.Fans[0], 5).X - 105) < 1e-9, "26-point TL calibration not applied.");
            p.LedOverrides[$"{tl.Mac}/0"] = [.. Enumerable.Range(0, 27).Select(i => new LedPoint(i, 0))]; Reject(p.Validate);
        });
        Test("Long running: poll interference does not drop the link, unlimited reconnects, pump coolant does not change its address", () =>
        {
            Check(Controller.PollFailureLimit * Controller.HeartbeatInterval >= 100_000, "Short poll interference drops the link.");
            Check(!Controller.ReconnectDue(0) && !Controller.ReconnectDue(24) && Controller.ReconnectDue(25) && Controller.ReconnectDue(300), "First minute: one attempt every 5 s.");
            Check(!Controller.ReconnectDue(325) && Controller.ReconnectDue(375) && Controller.ReconnectDue(75 * 100_000), "After a minute: every 15 s, no attempt limit.");
            Check(Enumerable.Range(1, 18_000).Count(Controller.ReconnectDue) >= 230, "An hour offline must give at least two hundred attempts.");
            byte[] record = new byte[42]; Convert.FromHexString("E684CAE566E1").CopyTo(record, 0); Convert.FromHexString("282EDB6232DC").CopyTo(record, 6);
            record[12] = 8; record[13] = 6; record[18] = 10; record[24] = 1; record[27] = 31; record[41] = 0x1C;
            var warm = WirelessGroup.Parse(record)!; record[27] = 32; var warmer = WirelessGroup.Parse(record)!;
            Check(warm.Coolant == 31 && warmer.Coolant == 32 && warm.Types.SequenceEqual(warmer.Types) && warm.Types[0] == 1 && warm.Types[3] == 0, "Coolant temperature leaked into the pump types.");
            record[18] = 0; record[19] = 3; record[24] = 21; record[25] = 21; record[26] = 21; record[27] = 21;
            Check(WirelessGroup.Parse(record)!.Types.SequenceEqual(new byte[] { 21, 21, 21, 21 }), "Fans lost their fourth type.");
        });
        Test("Cooling curve: smoothing, immediate increase, decrease with hysteresis, minimum, lost sensor → failsafe", () =>
        {
            var s = new CoolingSettings { Enabled = true, MinPercent = 25, FailsafePercent = 100, Hysteresis = 2 };
            var cpu = s.For("A"); cpu.Source = CoolingSource.Cpu;
            var fixedGroup = s.For("B"); fixedGroup.Source = CoolingSource.Fixed; fixedGroup.FixedPercent = 10;
            s.For("C").Source = CoolingSource.Off;
            Check(Math.Abs(CoolingEngine.Interpolate(cpu.Curve, 40) - 35) < 1e-9 && CoolingEngine.Interpolate(cpu.Curve, 10) == 30 && CoolingEngine.Interpolate(cpu.Curve, 120) == 100, "Curve interpolation.");
            var e = new CoolingEngine();
            Check(e.Compute(s, new(null, null, null), 0).Keys.SequenceEqual(["B"]), "Without a sensor a curve group is not controlled, a constant one is, an unmanaged one is not.");
            Check(e.Compute(s, new(null, null, null), 0)["B"] == 25, "A constant speed below the minimum is raised to the minimum.");
            var t = e.Compute(s, new(70, null, null), 1000); double at70 = t["A"];
            Check(Math.Abs(at70 - (60 + 25.0 * 5 / 15)) < 1e-9, "70 °C on the default curve.");
            t = e.Compute(s, new(90, null, null), 2000); Check(t["A"] > at70 && Math.Abs(e.States["A"].Smoothed!.Value - 76) < 1e-9, "Immediate increase, with smoothing 0,3.");
            double high = t["A"];
            t = e.Compute(s, new(75, null, null), 3000); Check(t["A"] == high, "A drop smaller than the hysteresis does not lower the speed.");
            for (int i = 0; i < 10; i++) t = e.Compute(s, new(40, null, null), 4000 + i * 1000);
            Check(t["A"] < high && t["A"] >= 25, "A large drop lowers the speed, but not below the minimum.");
            double before = t["A"];
            t = e.Compute(s, new(null, null, null), 15000); Check(t["A"] == before && !e.States["A"].Failsafe, "Short sensor loss: previous speed.");
            t = e.Compute(s, new(null, null, null), 30000); Check(t["A"] == 100 && e.States["A"].Failsafe, "Loss over 10 s: failsafe speed.");
            t = e.Compute(s, new(35, null, null), 31000); Check(!e.States["A"].Failsafe && e.States["A"].Smoothed == 35 && t["A"] < 100, "After the sensor returns: back on the curve without stale smoothing.");
            var restored = System.Text.Json.JsonSerializer.Deserialize<CoolingSettings>(System.Text.Json.JsonSerializer.Serialize(s))!; restored.Validate();
            Check(restored.For("A").Source == CoolingSource.Cpu && restored.For("B").FixedPercent == 10 && System.Text.Json.JsonSerializer.Serialize(s).Contains("\"Cpu\""), "Cooling settings not saved.");
            s.MinPercent = 5; Reject(s.Validate); s.MinPercent = 25; cpu.Curve = [new(50, 50)]; Reject(s.Validate);
        });
        Test("Sensors: CPU file fresh/stale/corrupt, NVML does not crash without a GPU", () =>
        {
            string path = Path.Combine(Path.GetTempPath(), $"wcrgb-cpu-{Environment.ProcessId}.json");
            try
            {
                var now = DateTimeOffset.UtcNow;
                File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(new CpuSensor.Sample(54.5, "Core (Tctl/Tdie)", now)));
                Check(CpuSensor.Read(path, now.AddSeconds(2)) == 54.5, "Fresh CPU temperature not read.");
                Check(CpuSensor.Read(path, now.AddSeconds(8)) == null, "Stale temperature accepted.");
                File.WriteAllText(path, "{oops"); Check(CpuSensor.Read(path) == null, "Corrupt file accepted.");
                Check(CpuSensor.Read(path + ".missing") == null, "No file must mean no temperature.");
            }
            finally { File.Delete(path); }
            var gpu = GpuSensor.Read();
            Check(gpu == null || gpu is > 0 and < 120, "GPU temperature out of range.");
            Log.Write($"Self-test: GPU {gpu?.ToString() ?? "no NVML"}");
        });
        Test("HydroShift: stop/push/reopen/GetVer, a partial write restores USB without a retry RGB", () =>
        {
            var upload = new RgbUpload([new byte[72]], 5300); uint timestamp = 0;
            using var normal = new HydroTestLink { RequireSettle = true };
            Check(HydroTransfer.Send(normal, upload, () => ++timestamp, pause: milliseconds => { if (milliseconds >= 2500) normal.Settled = true; }) == "test-H2", "Recovery not confirmed.");
            Check(normal.Commands.SequenceEqual(new byte[] { 0x7B, 0x34, 0x0A, 0xFC, 0x0A }) && normal.Reopens == 1, "Transaction order broken.");
            using var partial = new HydroTestLink { FailRgb = true, RequireSettle = true };
            Reject(() => HydroTransfer.Send(partial, upload, () => ++timestamp, pause: milliseconds => { if (milliseconds >= 2500) partial.Settled = true; }));
            Check(partial.Reopens == 1 && partial.Commands.Count(c => c == 0xFC) == 1 && partial.Commands[^1] == 0x0A, "Partial RGB was resent or not recovered USB.");
        });
        Test("HydroShift: zero-length USB packets are skipped, the overall timeout is bounded", () =>
        {
            Queue<byte[]> packets = new([[], [], HydroTestLink.Reply()]); int calls = 0;
            var reply = HydroProtocol.ReadResponse(_ => { calls++; return packets.Dequeue(); }, 1000);
            Check(calls == 3 && HydroProtocol.Firmware(reply) == "test-H2", "ZLP taken as no reply.");
            var clock = System.Diagnostics.Stopwatch.StartNew();
            Check(HydroProtocol.ReadResponse(_ => [], 30).Length == 0 && clock.ElapsedMilliseconds < 1000, "Reading empty packets is unbounded.");
        });
        Test("LCD: fan and pump headers match OpenSSL byte for byte, dangerous commands are never built", () =>
        {
            string Hash(byte[] data) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(data));
            const uint ts = 0x01020304;
            byte[] size = [0, 1, 0x23, 0x45];
            // Reference: upstream crypto.rs, rebuilt with openssl enc -des-cbc (see TECHNICAL.md, section 0.5.0).
            foreach (var (header, hash) in new (byte[], string)[]
            {
                (LcdProtocol.FanHeader(0x0A, ts, []), "BEF4EC0751CD90777A76A3BB8F03AA4B36D371AD2DA48CAB19EB1809249CCA78"),
                (LcdProtocol.FanHeader(0x0F, ts, [120]), "A93382913C065C407461346C13F285DBFC1A9C5DF642991681A2880E0201FC1E"),
                (LcdProtocol.FanHeader(0x0E, ts, [30]), "86604AF23D64B855ED85905DC5BABED3CFAC6EBEC7E1EAD14E9FB32FE1DE61A3"),
                (LcdProtocol.FanHeader(0x65, ts, size), "A2B5934CE71ED941F5AC521EC578EE377F47F2D569AD2996094EDA9F1910FC77"),
                (HydroProtocol.Command(0x0F, ts, [30]), "8F042AAFB0E994ACFEE651A941982BE23B67D6FE6662E00D91695181D2198C3B"),
                (HydroProtocol.Command(0x0E, ts, [80]), "97C1FDB618C3000DFF88EF20B4C6425DAA3512CE5B52A4DFEF5745FBA67C811E"),
                (HydroProtocol.Command(0x65, ts, size), "E8E04904AA08CE9258633A39C8CA30E27A442A136AE17321A75A1DB06F29B006"),
                (HydroProtocol.Command(0x33, ts, LcdProtocol.Clock(new DateTime(2026, 10, 21, 12, 34, 56))), "0504041C23429727004547C36C1CE1C44ED6EFCD14A892B101CCEFCE5A1C7BA5"),
                (HydroProtocol.Command(0x34, ts, [0]), "6F3D9940420F7D2800992A0D2A6960362FB4802BA0BFFDCF7A08673B92473AA8"),
                (HydroProtocol.Command(0x7A, ts, []), "8EAA63379646B076D948310D07CCF58DDD71F7BDDC94756A3E8C7740FC81CEE0"),
            })
                Check(header.Length == 512 && Hash(header) == hash, "LCD header differs from the reference OpenSSL.");
            // Reboot, boot image, desktop mode, mode warning, pump/PWM, RGB: never through the screen protocol.
            foreach (byte forbidden in new byte[] { 0x0B, 0x28, 0x96, 0x2E, 0xFB, 0xF9, 0xFC, 0x0D, 0x66, 0x79 })
            {
                Reject(() => LcdProtocol.FanHeader(forbidden, 1, [])); Reject(() => HydroProtocol.Command(forbidden, 1, []));
            }
            Reject(() => LcdProtocol.FanHeader(0x0E, 1, [101])); Reject(() => HydroProtocol.Command(0x0F, 1, [0]));
            Reject(() => LcdProtocol.FanFrame(new byte[LcdProtocol.FanMaxJpeg + 1], 1)); Reject(() => LcdProtocol.PumpFrame(new byte[LcdProtocol.PumpMaxJpeg + 1], 1));
            byte[] jpeg = Enumerable.Range(0, 3000).Select(i => (byte)(i | 1)).ToArray();
            byte[] fan = LcdProtocol.FanFrame(jpeg, ts), pump = LcdProtocol.PumpFrame(jpeg, ts);
            Check(fan.Length == 102_400 && fan.AsSpan(512, jpeg.Length).SequenceEqual(jpeg) && fan.AsSpan(512 + jpeg.Length).IndexOfAnyExcept((byte)0) < 0, "Fan frame is not 102,400 bytes with zero padding.");
            Check(pump.Length == 512 + jpeg.Length && pump.AsSpan(512).SequenceEqual(jpeg) && pump[510] == 0xA1 && pump[511] == 0x1A, "Pump frame built incorrectly.");
            // Brightness table from upstream slv3_brightness_lut and its tests.
            foreach (var (input, output) in new[] { (0, 0), (25, 10), (50, 30), (75, 40), (100, 100), (37, 20), (62, 35), (200, 100), (-5, 0) })
                Check(LcdProtocol.FanBrightness(input) == output, $"Brightness {input} → {LcdProtocol.FanBrightness(input)}, expected {output}.");
            // GetVer replies captured on the author's PC (artifacts/lcd-probe.json).
            byte[] Reply(string head, int length) { byte[] r = new byte[length]; Convert.FromHexString(head).CopyTo(r, 0); return r; }
            Check(LcdProtocol.Firmware(Reply("0AC8440000000000736C76335F323032343039313030345F3031", 511)) == "slv3_2024091004_01", "Fan version not parsed.");
            Check(LcdProtocol.Firmware(Reply("0AC8CE00000000006C69616E6C6968325F303030365F30303230", 512)) == "lianlih2_0006_0020", "Pump version not parsed.");
            Check(LcdProtocol.Firmware([0x0A, 0, 0]) == null && LcdProtocol.Firmware(Reply("65", 16)) == null, "Accepted a foreign version reply.");
            Check(LcdProtocol.BufferLevel(Reply("6500000000000000" + "04", 16)) == 4 && LcdProtocol.BufferLevel(Reply("0A00000000000000" + "04", 16)) == null, "Wrong buffer level.");
        });
        Test("LCD: fan and pump sequence, recovery after an error, without reboot/desktop", () =>
        {
            int opened = 0;
            var fanLinks = new List<LcdTestLink>();
            var fan = new FanScreen("test", () => { opened++; var l = new LcdTestLink(legacy: true); fanLinks.Add(l); return l; });
            byte[] jpeg = new byte[2000]; jpeg[0] = 0xFF; jpeg[1] = 0xD8;
            fan.SendFrame(jpeg); fan.SetBrightness(50); fan.SendFrame(jpeg);
            Check(opened == 1 && fanLinks[0].Commands.SequenceEqual(new byte[] { 0x0F, 0x0A, 0x65, 0x0E, 0x65 }) && fan.Firmware == "test-LCD", "Wrong fan command order.");
            Check(fanLinks[0].Packets[2].Length == 102_400 && fanLinks[0].Parameters[3] == 30 && fanLinks[0].Parameters[0] == 120, "Fan frame, frame rate or brightness wrong.");
            fanLinks[0].FailNext = true;
            Reject(() => fan.SendFrame(jpeg));
            Check(fanLinks[0].Disposed, "Fan USB link not closed after an error.");
            fan.SendFrame(jpeg);
            Check(opened == 2 && fanLinks[1].Commands.SequenceEqual(new byte[] { 0x0F, 0x0A, 0x65 }), "Screen not prepared again after an error.");

            var link = new LcdTestLink(legacy: false) { Level = 5 };
            using var pump = new HydroController(link);
            var screen = new PumpScreen(pump);
            screen.SendFrame(jpeg);
            // A transparent PNG must replace the built-in layer before the first JPEG.
            Check(link.Commands.Take(10).SequenceEqual(new byte[] { 0x7B, 0x0A, 0x0F, 0x68, 0x33, 0x34, 0x67, 0x67, 0x66, 0x65 }) && link.Parameters[2] == 30 && screen.Firmware == "test-LCD", "Wrong pump screen preparation.");
            Reject(() => HydroProtocol.Command(0x68, 1, [1])); Reject(() => HydroProtocol.Command(0x68, 1, Enumerable.Repeat((byte)1, 32).ToArray())); Reject(() => HydroProtocol.Command(0x67, 1, [0]));
            Check(link.Commands.Skip(10).All(c => c == 0x7A) && link.Commands.Count(c => c == 0x7A) is >= 1 and <= 60, "No wait for the pump buffer, or the wait is unbounded.");
            link.Commands.Clear(); link.Level = 1;
            screen.SetBrightness(70); screen.SendFrame(jpeg);
            Check(link.Commands.SequenceEqual(new byte[] { 0x0E, 0x65 }) && link.Parameters[^2] == 70, "Preparation repeated without a USB reopen, or wrong brightness.");
            Check(!link.AllCommands.Any(c => c is 0x0B or 0x96 or 0x28 or 0xFB or 0xFC), "Screen sent a dangerous command.");
            var silent = new LcdTestLink(legacy: false) { IgnoreOverlayReply = true };
            using var silentPump = new HydroController(silent);
            Reject(() => new PumpScreen(silentPump).SendFrame(jpeg));
            Check(silent.Commands.Contains((byte)0x66) && !silent.Commands.Contains((byte)0x65), "No reply to the layer clear, yet the JPEG was sent as after a successful preparation.");
        });
        Test("Pump: the top layer is replaced by a fully transparent 480×480 PNG with the exact length in the header", () =>
        {
            var packet = LcdProtocol.PumpClearOverlay(123);
            using var des = System.Security.Cryptography.DES.Create(); des.Key = "slv3tuzx"u8.ToArray(); des.IV = des.Key;
            var plain = des.DecryptCbc(packet.AsSpan(0, 504), des.IV);
            Check(plain[0] == 0x66 && BinaryPrimitives.ReadUInt32BigEndian(plain.AsSpan(8)) == packet.Length - 512 && packet.Length <= 4608, "Wrong PNG packet.");
            using var stream = new MemoryStream(packet[512..]); using var bitmap = new Bitmap(stream);
            Check(bitmap.Width == 480 && bitmap.Height == 480, "Wrong pump layer size.");
            for (int y = 0; y < bitmap.Height; y++) for (int x = 0; x < bitmap.Width; x++)
                Check(bitmap.GetPixel(x, y).A == 0, "Empty layer contains an opaque pixel.");
            Reject(() => HydroProtocol.Command(0x66, 1, []));
            Reject(() => HydroProtocol.Command(0x66, 1, new byte[4]));
            Reject(() => HydroProtocol.Command(0x66, 1, [0, 0, 16, 1]));
        });
        Test("Screens: frame of the right size, JPEG within the limit, pump circle, rotation, GIF under the FPS limit, profile", () =>
        {
            using var source = new Bitmap(800, 600);
            using (var g = Graphics.FromImage(source)) { g.Clear(Color.Red); g.FillRectangle(Brushes.Lime, 0, 0, 400, 600); }
            var pump = new ScreenSettings { Id = ScreenSettings.PumpId };
            var fan = new ScreenSettings { Id = "fedcba9876543210" };
            using (var picture = ScreenRenderer.Picture(source, pump))
            {
                Check(picture.Size == new Size(480, 480) && picture.GetPixel(2, 2).ToArgb() == Color.Black.ToArgb() && picture.GetPixel(100, 240).G > 200 && picture.GetPixel(380, 240).R > 200, "Pump: size, round mask or fill.");
                Check(ScreenRenderer.Encode(picture, pump.Spec).Length <= LcdProtocol.PumpMaxJpeg, "JPEG of the pump exceeds the limit.");
            }
            using (var picture = ScreenRenderer.Picture(source, fan))
                Check(picture.Size == new Size(400, 400) && picture.GetPixel(2, 2).G > 200, "Fan: square screen without a mask.");
            fan.Rotation = 180;
            using (var turned = ScreenRenderer.Picture(source, fan)) Check(turned.GetPixel(20, 200).R > 200 && turned.GetPixel(380, 200).G > 200, "180° rotation not applied.");
            fan.Fit = ScreenFit.Contain; fan.Rotation = 0;
            using (var contained = ScreenRenderer.Picture(source, fan)) Check(contained.GetPixel(200, 10).ToArgb() == Color.Black.ToArgb() && contained.GetPixel(200, 200).ToArgb() != Color.Black.ToArgb(), "Fit cropped the image.");
            // Noise does not compress: quality drops, but the frame still fits in 102,400 − 512 bytes.
            using (var noise = new Bitmap(400, 400))
            {
                var random = new Random(1); for (int y = 0; y < 400; y++) for (int x = 0; x < 400; x++) noise.SetPixel(x, y, Color.FromArgb(random.Next(256), random.Next(256), random.Next(256)));
                Check(ScreenRenderer.Encode(noise, fan.Spec).Length <= LcdProtocol.FanMaxJpeg, "Noisy frame does not fit the fan limit.");
            }
            // All dashboard themes: with and without data, on the round pump screen and the square fan screen. Samples are in artifacts/screen-themes.
            string themes = Path.Combine(Environment.CurrentDirectory, "artifacts", "screen-themes"); Directory.CreateDirectory(themes);
            var history = new SensorHistory();
            for (int i = 0; i < 240; i++) history.Add(new SensorReadings(48 + 9 * Math.Sin(i / 25.0) + i / 40.0, 41, 31));
            var readings = new SensorReadings(57, 43, 31);
            const string palette = "#FF2454, #683DFF, #00D9DF, #FFCA48";
            foreach (DashboardTheme theme in Enum.GetValues<DashboardTheme>())
                foreach (var screen in new[] { pump, fan })
                {
                    screen.Theme = theme; screen.Rotation = 0;
                    using (var frame = ScreenRenderer.Dashboard(screen, readings, history.Series(screen.Sensor), palette))
                    {
                        Check(frame.Width == screen.Spec.Size && frame.GetPixel(frame.Width / 2, frame.Height / 2 + frame.Height / 8).ToArgb() != 0 && ScreenRenderer.Encode(frame, screen.Spec).Length <= screen.Spec.MaxJpeg, $"Theme {theme}: frame.");
                        frame.Save(Path.Combine(themes, $"{(int)theme + 1}-{theme}-{(screen.IsPump ? "pump" : "fan")}.png"));
                    }
                    using (var empty = ScreenRenderer.Dashboard(screen, new SensorReadings(null, null, null), [], "bad palette")) Check(empty.Width == screen.Spec.Size, $"Theme {theme} without sensors.");
                }
            // Element colors: custom background and custom accent reach the frame.
            fan.Theme = DashboardTheme.Minimal; fan.BackgroundColor = "#102030"; fan.Color = "#00FF00";
            using (var colored = ScreenRenderer.Dashboard(fan, readings, [], palette))
            {
                Check(colored.GetPixel(4, 4).ToArgb() == Color.FromArgb(0x10, 0x20, 0x30).ToArgb(), "Custom background color not applied.");
                bool green = false; for (int x = 0; x < 400 && !green; x++) for (int y = 270; y < 300 && !green; y++) { var p = colored.GetPixel(x, y); green = p.G > 200 && p.R < 60 && p.B < 60; }
                Check(green, "Custom accent color (scale bar) not applied.");
            }
            fan.Theme = DashboardTheme.Arc; fan.BackgroundColor = "#000000"; fan.Color = "";
            Check(Dashboard.Key(new ScreenSettings { Id = "x", Theme = DashboardTheme.List }, readings, []) == "57|43|31" && Dashboard.Key(fan, new SensorReadings(56.6, 0, 0), []) == "57", "Frame resend key.");
            var bad = new ScreenSettings { Id = "x", MultiSensors = [] }; Reject(bad.Validate);
            bad.MultiSensors = [CoolingSource.Cpu, CoolingSource.Cpu]; Reject(bad.Validate);
            bad.MultiSensors = [CoolingSource.Cpu]; bad.TrackColor = "none"; Reject(bad.Validate);
            string gif = Path.Combine(Path.GetTempPath(), "wcrgb-test-" + Guid.NewGuid() + ".gif");
            try
            {
                File.WriteAllBytes(gif, Convert.FromBase64String("R0lGODlhCAAIAIEAAAAA/wAAAAAAAAAAACH/C05FVFNDQVBFMi4wAwEAAAAh+QQAAgAAACwAAAAACAAIAAAIDwABCBxIsKDBgwgTKkwYEAAh+QQBAgABACwAAAAACAAIAIEoANcAAAAAAAAAAAAIDwABCBxIsKDBgwgTKkwYEAAh+QQBAgABACwAAAAACAAIAIFQAK8AAAAAAAAAAAAIDwABCBxIsKDBgwgTKkwYEAAh+QQBAgABACwAAAAACAAIAIF4AIcAAAAAAAAAAAAIDwABCBxIsKDBgwgTKkwYEAAh+QQBAgABACwAAAAACAAIAIGgAF8AAAAAAAAAAAAIDwABCBxIsKDBgwgTKkwYEAAh+QQBAgABACwAAAAACAAIAIHIADcAAAAAAAAAAAAIDwABCBxIsKDBgwgTKkwYEAA7"));
                // 6 frames of 20 ms with a 20 FPS limit (50 ms): every three frames merge into 60 ms.
                var frames = ScreenRenderer.Animation(gif, new ScreenSettings { Id = "x", MaxFps = 20 });
                Check(frames.Count == 2 && frames.All(f => f.Milliseconds == 60 && f.Jpeg.Length <= LcdProtocol.FanMaxJpeg), $"GIF under the limit FPS: {string.Join(", ", frames.Select(f => f.Milliseconds))}.");
                Check(ScreenRenderer.Animation(gif, new ScreenSettings { Id = "x", MaxFps = 30 }).Count == 3, "A 30 FPS limit must keep 40 ms frames.");
            }
            finally { File.Delete(gif); }
            var profile = new Profile();
            profile.Screen(ScreenSettings.PumpId, "Pump").Mode = ScreenMode.Sensor;
            profile.Screen("0123456789abcdef", "LCD 1").Rotation = 90;
            profile.Validate();
            string path = Path.Combine(Path.GetTempPath(), "wcrgb-test-" + Guid.NewGuid() + ".json");
            try { profile.Save(path); var restored = Profile.Load(path); Check(restored.Screens.Count == 2 && restored.Screens[0].Mode == ScreenMode.Sensor && restored.Screens[1].Rotation == 90, "Screens not saved in the profile."); }
            finally { File.Delete(path); }
            profile.Screens[1].Rotation = 45; Reject(profile.Validate); profile.Screens[1].Rotation = 0;
            profile.Screens[1].Brightness = 101; Reject(profile.Validate); profile.Screens[1].Brightness = 50;
            profile.Screens.Add(new ScreenSettings { Id = ScreenSettings.PumpId }); Reject(profile.Validate);
        });
        Test("LCD mapping: spatial crop, shared phase, immutable snapshot, mask and JPEG", () =>
        {
            var p = new Profile { Palette = "#FF0000, #0000FF", Wavelength = 300, Direction = 25 };
            p.Fans.Add(new FanPlacement { Group = "test", Fan = 0, X = 100, Y = 120 });
            p.Fans.Add(new FanPlacement { Group = "test", Fan = 1, X = 200, Y = 220 });
            var s = new ScreenSettings { Id = "lcd", Mode = ScreenMode.Mapping, MappingGroup = "test" };
            s.Validate();
            var view = ScreenMapping.Viewport(p, s);
            Check(Math.Abs(view.X + view.Width / 2 - 100) < .001, "Wrong fan crop center");
            var scene = new ScreenMapping(p);
            using var a = scene.Render(s, 0);
            var expected = Gradient.Shade(p, Effects.Palette(p), new LedPoint(100, 120), 0, p.ColorSpace, p.SaturationBoost, 1, true);
            var actual = a.GetPixel(200, 200);
            Check(Math.Abs(expected.R - actual.R) < 5 && Math.Abs(expected.B - actual.B) < 5, "LCD does not sample shared canvas");
            using var b = scene.Render(s, 1);
            Check(a.GetPixel(200, 200) != b.GetPixel(200, 200), "Animation is frozen");
            s.MappingFan = 1; using var c = scene.Render(s, 0);
            Check(a.GetPixel(200, 200) != c.GetPixel(200, 200), "Different fans share the same crop");
            p.Palette = "#00FF00, #00FF00"; p.Fans[1].X = 900;
            using var unchanged = scene.Render(s, 0);
            Check(c.GetPixel(200, 200) == unchanged.GetPixel(200, 200), "Worker reads mutable live profile");
            s.Id = ScreenSettings.PumpId; using var pump = scene.Render(s, 0);
            Check(pump.GetPixel(0, 0).ToArgb() == Color.Black.ToArgb() && pump.Width == 480, "Missing pump mask");
            Check(ScreenRenderer.Encode(pump, ScreenSpec.Pump).Length <= LcdProtocol.PumpMaxJpeg, "Oversized pump JPEG");
            s.Id = "lcd"; s.MappingGroup = "missing"; Reject(() => ScreenMapping.Viewport(p, s));
            s.MappingFps = 16; Reject(s.Validate);
            var restored = System.Text.Json.JsonSerializer.Deserialize<ScreenSettings>(System.Text.Json.JsonSerializer.Serialize(new ScreenSettings { Id = "lcd", Mode = ScreenMode.Mapping, MappingGroup = "test", MappingFan = 1 }))!;
            Check(restored.Mode == ScreenMode.Mapping && restored.MappingFan == 1 && restored.MappingFps == 15 && (int)ScreenMode.Off == 4, "Profile compatibility");
            s.Id = ScreenSettings.PumpId; s.MappingFps = 15;
            foreach (var effect in Effects.All)
            {
                p.Effect.Kind = effect.Kind;
                using var effectFrame = new ScreenMapping(p).Render(s, .75);
                Check(ScreenRenderer.Encode(effectFrame, s.Spec).Length <= s.Spec.MaxJpeg, "Mapping effect exceeds LCD frame limit");
            }
        });
        Test("LCD mapping: worker cadence, scene updates and unmanaged stop without hardware", () =>
        {
            var p = new Profile { Palette = "#FF0000, #0000FF" };
            var s = new ScreenSettings { Id = ScreenSettings.PumpId, Mode = ScreenMode.Mapping, MappingFps = 5 };
            var device = new FakeScreen();
            using var worker = new ScreenWorker(device, () => new SensorReadings(0, 0, 0), "mapping test", sceneSeconds: () => 1.5);
            // FakeScreen accepts the fan JPEG limit; smooth pump gradients are also well below it.
            worker.Update(s, p.Palette, p);
            var timer = System.Diagnostics.Stopwatch.StartNew();
            while (worker.FramesSent < 3 && timer.ElapsedMilliseconds < 5000) Thread.Sleep(20);
            Check(worker.FramesSent >= 3 && timer.ElapsedMilliseconds >= 350 && worker.Status.Contains("mapping"), "Mapping cadence or mode failed");
            s.Mode = ScreenMode.Unmanaged; worker.Update(s, p.Palette, p);
            Thread.Sleep(150); int count = worker.FramesSent; Thread.Sleep(300);
            Check(worker.FramesSent == count, "Unmanaged mode keeps transmitting");
        });
        Test("Screens: frame thread with image, Off, number, file error without a crash, stop", () =>
        {
            string png = Path.Combine(Path.GetTempPath(), "wcrgb-test-" + Guid.NewGuid() + ".png");
            using (var b = new Bitmap(64, 64)) { using (var g = Graphics.FromImage(b)) g.Clear(Color.Orange); b.Save(png, System.Drawing.Imaging.ImageFormat.Png); }
            var device = new FakeScreen();
            var worker = new ScreenWorker(device, () => new SensorReadings(55, 40, 30), "test");
            bool Wait(Func<bool> condition) { var t = System.Diagnostics.Stopwatch.StartNew(); while (t.ElapsedMilliseconds < 5000) { if (condition()) return true; Thread.Sleep(20); } return false; }
            try
            {
                var s = new ScreenSettings { Id = "fedcba9876543210", Mode = ScreenMode.Image, ImagePath = png, Brightness = 70 };
                worker.Update(s, "#FF0000, #0000FF");
                Check(Wait(() => device.Has("frame")) && device.Snapshot()[0] == "b:70", "Image: brightness first, then the frame.");
                s.Mode = ScreenMode.Off; worker.Update(s, "#FF0000, #0000FF");
                Check(Wait(() => device.Has("b:0")) && Wait(() => device.Snapshot().SkipWhile(c => c != "b:0").Any(c => c.StartsWith("frame"))), "Off: brightness 0 and a black frame.");
                worker.Identify(2);
                // The number shows even on a screen that is off: brightness at least 60% (here the configured 70 %).
                Check(Wait(() => worker.Status.Contains("number 2")) && device.Snapshot().SkipWhile(c => c != "b:0").Contains("b:70"), "Identification number not shown.");
                s.Mode = ScreenMode.Image; s.ImagePath = png + ".missing"; worker.Update(s, "");
                Check(Wait(() => worker.Status.StartsWith("error")) && worker.Status.Contains("not found"), "File error not shown in the status.");
                s.Mode = ScreenMode.Sensor; worker.Update(s, "#FF0000, #0000FF");
                Check(Wait(() => worker.Status.Contains("CPU: 55")), "Dashboard did not show the temperature CPU.");
            }
            finally
            {
                var clock = System.Diagnostics.Stopwatch.StartNew(); worker.Dispose();
                File.Delete(png);
                Check(clock.ElapsedMilliseconds < 3000 && device.Has("dispose"), "Screen thread did not stop.");
            }
        });
        Test("Mapping effects: 10 shaders with no seam at the cycle wrap, pattern, device memory, profile", () =>
        {
            var p = new Profile { Brightness = 1, PeriodSeconds = 6 }; CaseLayout.Populate(p);
            Check(Effects.All.Length == 11 && Effects.All.Count(e => e.Kind != EffectKind.Gradient) == 10, "There must be 10 effects plus the plain gradient.");
            var stops0 = Effects.Palette(p);
            var gradient = Gradient.At(p, stops0, new LedPoint(120, 80), 1.3);
            Check(gradient.ToArgb() == Gradient.Sample(stops0, Gradient.Phase(p, new LedPoint(120, 80), 1.3), p.ColorSpace, p.SaturationBoost, p.Brightness, true).ToArgb(), "Without an effect the color must match the old gradient.");
            string folder = Path.Combine(Environment.CurrentDirectory, "artifacts", "effects"); Directory.CreateDirectory(folder);
            var points = p.Fans.SelectMany(f => Enumerable.Range(0, 40).Select(led => Gradient.WorldPoint(p, f, led))).Append(Gradient.PumpPoint(p.Pump, 0)).ToArray();
            foreach (var info in Effects.All.Where(e => e.Kind != EffectKind.Gradient))
            {
                p.Effect.Kind = info.Kind; p.Effect.Current.Speed = 1;
                var stops = Effects.Palette(p);
                // The cycle end flows into its start without a jump: the color before and after the wrap is nearly the same.
                int jump = points.Max(pt =>
                {
                    var before = Gradient.At(p, stops, pt, p.PeriodSeconds * (1 - 1e-7)); var after = Gradient.At(p, stops, pt, 0);
                    return Math.Max(Math.Abs(before.R - after.R), Math.Max(Math.Abs(before.G - after.G), Math.Abs(before.B - after.B)));
                });
                Check(jump <= 3, $"«{info.Title}»: jump at the cycle wrap {jump}.");
                // The effect draws a pattern, not a flat fill, and changes over time.
                var frameA = points.Select(pt => Gradient.At(p, stops, pt, 1.1).ToArgb()).ToArray();
                var frameB = points.Select(pt => Gradient.At(p, stops, pt, 2.9).ToArgb()).ToArray();
                Check(frameA.Distinct().Count() > 8 && !frameA.SequenceEqual(frameB), $"«{info.Title}»: no pattern or motion.");
                // Case canvas frame for viewing (3 mm per pixel).
                using var image = new Bitmap(200, 187);
                for (int y = 0; y < image.Height; y++) for (int x = 0; x < image.Width; x++) image.SetPixel(x, y, Gradient.At(p, stops, new LedPoint(8 + x * 3, 20 + y * 3), 1.7));
                image.Save(Path.Combine(folder, $"{(int)info.Kind:00}-{info.Kind}.png"));
            }
            // Device scene with an effect: 4 groups and the pump fit in memory, frame 0 matches the canvas calculation.
            p.Effect.Kind = EffectKind.Starfall;
            var groups = CaseLayout.DemoMacs.Select((mac, i) => new WirelessGroup(mac, "D0D0D06232DC", 8, (byte)(i + 2), new[] { 2, 1, 3, 3 }[i], Enumerable.Repeat((byte)20, new[] { 2, 1, 3, 3 }[i]).Concat(new byte[4 - new[] { 2, 1, 3, 3 }[i]]).ToArray(), new byte[4], false, 0)).ToArray();
            var scene = RgbScene.Prepare(p, groups, includePump: true);
            var g0 = groups[0];
            Check(scene.Frames >= 2 && TinyUz.Decompress(scene.Uploads[g0.Mac].Compressed, scene.Frames * g0.LedCount * 3).AsSpan(0, g0.LedCount * 3).SequenceEqual(Gradient.Frame(p, g0.Mac, g0.Fans, 0)), "Effect scene for devices.");
            results.Add($"INFO: Starfall on 4 groups + pump: {scene.Frames} frames in {p.PeriodSeconds} s ({scene.Frames / p.PeriodSeconds:F1} frames/s on devices).");
            // Profile: the effect and its settings are saved; each effect remembers its own.
            p.Effect.Kind = EffectKind.Fire; p.Effect.Current.Values[0] = 80; p.Effect.Current.Background = "#100000";
            p.Effect.Kind = EffectKind.Ripple; p.Effect.Current.Speed = 3;
            string file = Path.Combine(Path.GetTempPath(), "wcrgb-test-" + Guid.NewGuid() + ".json");
            try
            {
                p.Save(file); var restored = Profile.Load(file);
                Check(restored.Effect.Kind == EffectKind.Ripple && restored.Effect.Current.Speed == 3 && restored.Effect.For(EffectKind.Fire).Values[0] == 80 && restored.Effect.For(EffectKind.Fire).Background == "#100000", "Effect not saved in the profile.");
            }
            finally { File.Delete(file); }
            p.Effect.Current.Speed = 11; Reject(p.Validate); p.Effect.Current.Speed = 1;
            p.Effect.Current.Values[0] = 5; Reject(p.Validate); p.Effect.Current.Values[0] = 200;
            p.Effect.Current.Palette = "bad"; Reject(p.Validate);
        });
        Test("Start with Windows: scheduled task with highest privileges, no 72-hour limit, normal priority", () =>
        {
            string exe = @"C:\Apps\WirelessCanvas RGB & test\WirelessCanvasRGB.exe", user = @"PC\User & <test>";
            var xml = System.Xml.Linq.XDocument.Parse(AutoStart.TaskXml(exe, user));
            System.Xml.Linq.XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
            string Value(string name) => xml.Descendants(ns + name).Single().Value;
            Check(Value("RunLevel") == "HighestAvailable" && Value("LogonType") == "InteractiveToken" && Value("ExecutionTimeLimit") == "PT0S" && Value("Priority") == "4", "Task rights, logon type, time limit or priority.");
            Check(Value("Command") == exe && Value("Arguments") == "--startup" && Value("WorkingDirectory") == Path.GetDirectoryName(exe) && Value("Delay") == "PT10S", "Task command.");
            Check(xml.Descendants(ns + "UserId").All(u => u.Value == user) && Value("DisallowStartIfOnBatteries") == "false" && Value("MultipleInstancesPolicy") == "IgnoreNew", "Task user or start conditions.");
        });
        Test("Pump motor over radio: timer as upstream, packets 0x12 0x19 / 0x12 0x21, curve without the fan lower limit", () =>
        {
            foreach (var (rpm, timer) in new[] { (1600, 1500), (1700, 1333), (1800, 1140), (1900, 963), (2100, 640), (2350, 150), (2450, 35), (2500, 10), (1000, 1500), (5000, 10) })
                Check(Controller.PumpTimer(rpm) == timer, $"Timer for {rpm} RPM: {Controller.PumpTimer(rpm)}, expected {timer}.");
            Check(Controller.PumpRpm(0) == 1600 && Controller.PumpRpm(50) == 2050 && Controller.PumpRpm(100) == 2500 && Controller.PumpRpm(150) == 2500, "Percent → pump RPM.");
            Check(Controller.NextSequence(6) == 7 && Controller.NextSequence(253) == 254 && Controller.NextSequence(254) == 1 && Controller.NextSequence(255) == 1, "Command number as upstream next_target_sequence.");
            var pump = new WirelessGroup("010203040506", "090909090909", 8, 2, 0, new byte[4], new byte[4], false, 6, 10) { SlotIndex = 3 };
            // Reference upstream aio.rs: wireless_theme_packet_carries_slot_sequence_and_requires_fresh_ack.
            byte[] theme = Controller.PumpThemePacket(pump, "090909090909", 8, 7);
            Check(theme.Length == 240 && theme.AsSpan(0, 18).SequenceEqual(new byte[] { 0x12, 0x19, 1, 2, 3, 4, 5, 6, 9, 9, 9, 9, 9, 9, 2, 8, 3, 7 }) && theme.AsSpan(18).IndexOfAnyExcept((byte)0) < 0, "Wireless theme packet differs from upstream.");
            byte[] block = Controller.PumpParameterBlock(new Controller.PumpParameters(2100, 80, 1));
            // No temperatures in the block: the pump command is the same at any temperature.
            Check(block.Length == 32 && block.AsSpan(0, 4).IndexOfAnyExcept((byte)0) < 0 && block.AsSpan(8, 4).IndexOfAnyExcept((byte)0) < 0 && block[6] == 3 && block[7] == 1 && block[26] == 1 && block[27] == 0 &&
                block.AsSpan(13, 12).IndexOfAnyExcept((byte)0xFF) < 0 && block[25] == 80 && BinaryPrimitives.ReadUInt16BigEndian(block.AsSpan(28)) == 640 && block[30] == 1, "Pump parameter block.");
            byte[] rf = Controller.PumpParametersPacket(pump, "090909090909", 8, block);
            Check(rf.Length == 240 && rf.AsSpan(0, 17).SequenceEqual(new byte[] { 0x12, 0x21, 1, 2, 3, 4, 5, 6, 9, 9, 9, 9, 9, 9, 2, 8, 3 }) && rf[17] == 0 && rf.AsSpan(18, 32).SequenceEqual(block) && rf.AsSpan(50).IndexOfAnyExcept((byte)0) < 0, "Parameter packet differs from upstream set_aio_params.");
            Reject(() => Controller.PumpParametersPacket(pump with { DeviceType = 0, Fans = 3 }, "090909090909", 8, block));
            Check(Controller.PumpParameterBlock(new Controller.PumpParameters(2100, 80, 1)).SequenceEqual(block), "Identical settings must give an identical packet.");
            // Profile 0.5.0: a constant pump row becomes a constant RPM, a pump curve is dropped.
            var settings = new CoolingSettings { MinPercent = 40 };
            settings.For(CoolingSettings.PumpKey).Source = CoolingSource.Fixed; settings.For(CoolingSettings.PumpKey).FixedPercent = 50;
            settings.For("010203040506").Source = CoolingSource.Fixed; settings.For("010203040506").FixedPercent = 0;
            Check(!new CoolingEngine().Compute(settings, new SensorReadings(50, 40, 30), 0).ContainsKey(CoolingSettings.PumpKey), "The pump must not follow temperature.");
            settings.Upgrade();
            Check(settings.PumpRpm == 2050 && settings.Groups.All(g => g.Mac != CoolingSettings.PumpKey), "Pump RPM migration from the profile 0.5.0.");
            var curve = new CoolingSettings(); curve.For(CoolingSettings.PumpKey).Source = CoolingSource.Cpu; curve.Upgrade();
            Check(curve.PumpRpm == null && curve.Groups.Count == 0, "A pump curve from 0.5.0 must be dropped, not turned into an RPM.");
            settings.PumpRpm = 1500; Reject(settings.Validate); settings.PumpRpm = 2600; Reject(settings.Validate); settings.PumpRpm = 2500; settings.Validate();
            Check(new CoolingEngine().Compute(settings, new SensorReadings(50, 40, 30), 0)["010203040506"] == 40, "Fan lower limit.");
        });
        Test("Localization: Windows languages, complete catalog and message parameters without changing the number culture", () =>
        {
            foreach (var (system, expected) in new[] { ("ru-RU", "ru"), ("en-US", "en"), ("zh-CN", "zh-Hans"), ("zh-TW", "zh-Hans"), ("es-MX", "es"), ("pt-BR", "pt"), ("pt-PT", "pt"), ("de-DE", "en") })
                Check(L10n.Resolve("system", system) == expected, "Wrong language for Windows: " + system);
            Check(L10n.Resolve("es", "ru-RU") == "es" && L10n.Resolve("bad", "pt-PT") == "pt", "Manual choice or fallback language.");
            var culture = System.Globalization.CultureInfo.CurrentCulture;
            string old = L10n.Language;
            try
            {
                Check(L10n.Entries.Count == 659, "Catalog incomplete.");
                foreach (var entry in L10n.Entries)
                {
                    Check(entry.Value.Length == 4 && entry.Value.All(s => !string.IsNullOrWhiteSpace(s)) && !System.Text.RegularExpressions.Regex.IsMatch(entry.Key, "[А-Яа-яЁё]") && entry.Value.Skip(1).All(s => !System.Text.RegularExpressions.Regex.IsMatch(s, "[А-Яа-яЁё]")), "Incomplete translation: " + entry.Key);
                    string[] Slots(string s) => System.Text.RegularExpressions.Regex.Matches(s, @"\{\d+(?:[,\:][^{}]*)?\}").Select(m => m.Value).Order().ToArray();
                    foreach (string translation in entry.Value)
                    {
                        Check(Slots(translation).SequenceEqual(Slots(entry.Key)), "Translation parameters lost: " + entry.Key);
                        _ = System.Text.CompositeFormat.Parse(translation);
                    }
                }
                foreach (string language in L10n.Codes)
                {
                    L10n.Initialize(language);
                    Check(L10n.T("Settings") != "Settings" || language == "en", "Settings title not translated.");
                    Check(L10n.F($"Enter a number from {1.5} to {2.5}.").Contains(1.5.ToString(culture)), "Number culture changed.");
                }
                Check(ReferenceEquals(System.Globalization.CultureInfo.CurrentCulture, culture), "Language changed the number format.");
            }
            finally { L10n.Initialize(old); }
        });
        Test("Settings: start with Windows moved from mapping, six language choices, the choice is saved separately from the profile", () =>
        {
            string previousExe = @"C:\Apps\Update-0.7.0\LianLi.Windows.exe", currentExe = @"C:\Apps\Update-0.8.0\LianLi.Windows.exe";
            Check(AutoStart.NeedsPathUpdate(AutoStart.TaskXml(previousExe, "test"), currentExe), "Startup still points to the old release.");
            Check(!AutoStart.NeedsPathUpdate(AutoStart.TaskXml(currentExe, "test"), currentExe) && !AutoStart.NeedsPathUpdate(AutoStart.TaskXml(@"C:\Apps\Other.exe", "test"), currentExe), "Unneeded startup change.");
            var p = new Profile(); CaseLayout.Populate(p);
            using var form = new MainForm(false, p) { ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Location = new(-30000, -30000) };
            IEnumerable<Control> All(Control c) => c.Controls.Cast<Control>().SelectMany(x => new[] { x }.Concat(All(x)));
            form.Show(); form.ShowTab(6); Application.DoEvents();
            var settings = All(form).OfType<SettingsPanel>().Single();
            var startup = All(form).OfType<CheckBox>().Single(x => x.Name == "startWithWindows");
            Check(settings.Visible && startup.Parent!.Parent == settings && !All(form).OfType<Canvas>().Single().Visible, "Startup option is not in settings.");
            startup.Checked = !startup.Checked; // HardwareOutput=false: system startup is not changed.
            var language = All(settings).OfType<ComboBox>().Single();
            Check(language.Items.Count == 6 && ((SettingsPanel.LanguageChoice)language.SelectedItem!).Code == "system", "No system language by default.");
            var preferences = new AppPreferences(); int saved = 0;
            using var panel = new SettingsPanel(new CheckBox(), preferences, () => saved++);
            var selector = All(panel).OfType<ComboBox>().Single(); selector.SelectedIndex = 3;
            Check(preferences.Language == "zh-Hans" && saved == 1 && L10n.Language == "en", "The choice is not saved, or changes the running UI before a restart.");
            string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + "-language.json");
            try { preferences.Save(path); Check(AppPreferences.Load(path).Language == "zh-Hans", "Language does not survive a restart."); File.WriteAllText(path, "invalid"); Check(AppPreferences.Load(path).Language == "system", "An invalid file does not give the system language."); }
            finally { if (File.Exists(path)) File.Delete(path); }
        });
        Test("OpenRGB at launch: refresh at once, after 3 and 10 s, cancelled on close and when output is turned off", () =>
        {
            var delays = new List<int>(); int refreshes = 0;
            OpenRgbStartup.RefreshDevices(() => refreshes++, () => true, CancellationToken.None,
                (milliseconds, _) => { delays.Add(milliseconds); return Task.CompletedTask; }).GetAwaiter().GetResult();
            Check(refreshes == 3 && delays.SequenceEqual(new[] { 3000, 7000 }), "Bounded retry after services did not run.");
            bool enabled = true; refreshes = 0;
            OpenRgbStartup.RefreshDevices(() => refreshes++, () => enabled, CancellationToken.None,
                (_, _) => { enabled = false; return Task.CompletedTask; }).GetAwaiter().GetResult();
            Check(refreshes == 1, "Refresh continued after turning off OpenRGB.");
            using var stop = new CancellationTokenSource(); refreshes = 0;
            OpenRgbStartup.RefreshDevices(() => refreshes++, () => true, stop.Token,
                (_, _) => { stop.Cancel(); return Task.CompletedTask; }).GetAwaiter().GetResult();
            Check(refreshes == 1, "Refresh continued after the window closed.");
            refreshes = 0;
            OpenRgbStartup.RefreshDevices(() => refreshes++, () => false, CancellationToken.None).GetAwaiter().GetResult();
            Check(refreshes == 0, "Disabled bridge requested the list.");
        });
        Test("OpenRGB: new LEDs update existing table rows; the button uses the same refresh routine", () =>
        {
            var placement = new ExternalPlacement { Key = "RAM|DIMM1", Name = "RAM", Type = 1, X = 250, Y = 200, Brightness = .6 };
            var p = new Profile { External = [placement] }; CaseLayout.Populate(p);
            using var form = new MainForm(false, p) { ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Location = new(-30000, -30000) };
            IEnumerable<Control> All(Control c) => c.Controls.Cast<Control>().SelectMany(x => new[] { x }.Concat(All(x)));
            form.Show(); form.ShowTab(2); Application.DoEvents();
            var panel = All(form).OfType<OpenRgbPanel>().Single(); var grid = All(panel).OfType<DataGridView>().Single();
            object row = grid.Rows[0].DataBoundItem!;
            var device = new OpenRgbDevice(0, 1, "RAM", "Test", "Test", "", "DIMM1", [], 0, [], 8);
            form.AdoptOpenRgbDevices([device]); Application.DoEvents();
            Check(ReferenceEquals(p.External[0], placement) && placement.Brightness == .6 && placement.X == 250, "Refresh lost the settings of an existing device.");
            Check(!ReferenceEquals(row, grid.Rows[0].DataBoundItem) && grid.Rows[0].Cells["Leds"].Value?.ToString() == "8", "Table shows the old LED count after detection.");
            row = grid.Rows[0].DataBoundItem!;
            All(panel).OfType<Button>().Single(b => b.Name == "refreshOpenRgbDevices").PerformClick();
            Check(!ReferenceEquals(row, grid.Rows[0].DataBoundItem), "The button did not call the shared refresh routine.");
        });
        string directory = Path.Combine(Environment.CurrentDirectory, "artifacts"); Directory.CreateDirectory(directory);
        File.WriteAllLines(Path.Combine(directory, "self-test-results.txt"), results, Encoding.UTF8);
        foreach (string line in results) Console.WriteLine(line);
        return results.Any(x => x.StartsWith("FAIL")) ? 1 : 0;
    }
    sealed class ByteSeq : IEqualityComparer<byte[]>
    {
        public bool Equals(byte[]? a, byte[]? b) => a!.SequenceEqual(b!);
        public int GetHashCode(byte[] a) => a[0] | a[1] << 8 | a[2] << 16;
    }
    sealed class HydroTestLink : IHydroLink
    {
        public List<byte> Commands { get; } = [];
        public int Reopens { get; private set; }
        public bool FailRgb { get; init; }
        public bool RequireSettle { get; init; }
        public bool Settled { get; set; }
        public string? LinkMac { get; init; }
        public static byte[] Reply() { byte[] reply = new byte[512]; reply[0] = 0x0A; "test-H2"u8.CopyTo(reply.AsSpan(8)); return reply; }
        public void Write(byte[] packet)
        {
            using var des = System.Security.Cryptography.DES.Create(); des.Key = "slv3tuzx"u8.ToArray(); des.IV = des.Key;
            byte command = des.DecryptCbc(packet.AsSpan(0, 504), des.IV)[0]; Commands.Add(command);
            if (command == 0xFC && FailRgb) throw new IOException("Simulated partial write");
        }
        public byte[] Read(int timeout)
        {
            if (timeout != 5 && Commands.LastOrDefault() == 0x0A) return Reply();
            if (Commands.LastOrDefault() == 0xFA && LinkMac != null)
            {
                byte[] reply = new byte[512]; reply[0] = 0xFA; Convert.FromHexString(LinkMac).CopyTo(reply, 22); return reply;
            }
            return [];
        }
        public void Reopen() { if (RequireSettle && !Settled) throw new IOException("RGB settling pause omitted"); Reopens++; }
        public void Dispose() { }
    }
    sealed class FakeScreen : IScreenDevice
    {
        readonly List<string> calls = [];
        public ScreenSpec Spec => ScreenSpec.Fan;
        public string? Firmware => "fake";
        public void SendFrame(byte[] jpeg) { Check(jpeg.Length <= LcdProtocol.FanMaxJpeg && jpeg[0] == 0xFF && jpeg[1] == 0xD8, "Not JPEG."); lock (calls) calls.Add("frame:" + jpeg.Length); }
        public void SetBrightness(int percent) { lock (calls) calls.Add("b:" + percent); }
        public void Dispose() { lock (calls) calls.Add("dispose"); }
        public string[] Snapshot() { lock (calls) return [.. calls]; }
        public bool Has(string prefix) => Snapshot().Any(c => c.StartsWith(prefix));
    }
    /// <summary>Test screen: decrypts headers (legacy fan format or pump WinUSB) and replies like the firmware.</summary>
    sealed class LcdTestLink(bool legacy) : IHydroLink
    {
        public List<byte> Commands { get; } = [];
        public List<byte> AllCommands { get; } = [];
        public List<byte> Parameters { get; } = [];
        public List<byte[]> Packets { get; } = [];
        public bool FailNext { get; set; }
        public bool Disposed { get; private set; }
        public int Level { get; set; }
        public bool IgnoreOverlayReply { get; init; }
        public void Write(byte[] packet)
        {
            if (Disposed) throw new ObjectDisposedException("link");
            if (FailNext) { FailNext = false; throw new IOException("Simulated USB error"); }
            using var des = System.Security.Cryptography.DES.Create(); des.Key = "slv3tuzx"u8.ToArray(); des.IV = des.Key;
            byte[] plain = des.DecryptCbc(packet.AsSpan(0, legacy ? 512 : 504), des.IV);
            Check(plain.Length == (legacy ? 504 : 500) && plain[2] == 0x1A && plain[3] == 0x6D && (legacy || packet[510] == 0xA1 && packet[511] == 0x1A), "Wrong header format LCD.");
            Commands.Add(plain[0]); AllCommands.Add(plain[0]); Parameters.Add(plain[8]); Packets.Add(packet);
        }
        public byte[] Read(int timeout)
        {
            byte last = Commands.LastOrDefault(); byte[] reply = new byte[512]; reply[0] = last;
            if (last == 0x66 && IgnoreOverlayReply) return [];
            if (last == 0x0A) "test-LCD"u8.CopyTo(reply.AsSpan(8));
            if (last is 0x65 or 0x7A) { reply[8] = (byte)Level; if (last == 0x7A && Level > 0) Level--; }
            return reply;
        }
        public void Reopen() { }
        public void Dispose() => Disposed = true;
    }
    static async Task SdkTest()
    {
        var group = new WirelessGroup("010203040506", "090807060504", 8, 1, 2, [20, 21, 0, 0], new byte[4], false, 0);
        byte[]? received = null;
        using var signal = new SemaphoreSlim(0);
        using var server = new OpenRgbServer([group], (_, f) => { received = f; signal.Release(); }, 0);
        using var client = new TcpClient(); await client.ConnectAsync("127.0.0.1", server.Port);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var stream = client.GetStream();
        async Task Send(uint kind, byte[] body)
        {
            byte[] header = new byte[16]; "ORGB"u8.CopyTo(header); BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8), kind); BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(12), (uint)body.Length);
            await stream.WriteAsync(header, timeout.Token); await stream.WriteAsync(body, timeout.Token);
        }
        async Task<byte[]> Reply()
        {
            byte[] header = new byte[16]; await stream.ReadExactlyAsync(header, timeout.Token); int size = (int)BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(12));
            Check(size < 65536, "Unexpected extra reply."); byte[] body = new byte[size]; await stream.ReadExactlyAsync(body, timeout.Token); return body;
        }
        await Send(40, BitConverter.GetBytes(6u)); Check(BitConverter.ToUInt32(await Reply()) == 4, "Invalid SDK version.");
        await Send(0, []); Check(BitConverter.ToUInt32(await Reply()) == 1, "Wrong controller count.");
        await Send(1, BitConverter.GetBytes(4u)); byte[] description = await Reply();
        Check(BinaryPrimitives.ReadUInt32LittleEndian(description) == description.Length && Encoding.UTF8.GetString(description).Contains("Direct"), "Wrong controller description.");
        var mode = new MemoryStream(); using (var w = new BinaryWriter(mode, Encoding.UTF8, true))
        {
            w.Write(0u); w.Write(0u); w.Write((ushort)7); w.Write("Direct\0"u8);
            for (int i = 0; i < 12; i++) w.Write(i == 1 ? 32u : i == 11 ? 1u : 0u);
            w.Write((ushort)0);
        }
        byte[] modeData = mode.ToArray(); BinaryPrimitives.WriteUInt32LittleEndian(modeData, (uint)modeData.Length); await Send(1101, modeData);
        byte[] frame = new byte[4 + 2 + 80 * 4]; BinaryPrimitives.WriteUInt32LittleEndian(frame, (uint)frame.Length); BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(4), 80);
        for (int i = 0; i < 80; i++) frame[6 + i * 4] = 100;
        await Send(1050, frame); await signal.WaitAsync(timeout.Token); Check(received!.Length == 240 && received[0] == 100 && received[3] == 100, "Wrong RGB data.");
        byte[] zone = new byte[4 + 4 + 2 + 40 * 4]; BinaryPrimitives.WriteUInt32LittleEndian(zone, (uint)zone.Length); BinaryPrimitives.WriteUInt32LittleEndian(zone.AsSpan(4), 1); BinaryPrimitives.WriteUInt16LittleEndian(zone.AsSpan(8), 40);
        for (int i = 0; i < 40; i++) zone[10 + i * 4 + 1] = 150;
        await Send(1051, zone); await signal.WaitAsync(timeout.Token); Check(received![0] == 100 && received[120] == 0 && received[121] == 150, "Zones mixed up.");
        await Send(1052, [0, 0, 0, 0, 0, 0, 200, 0]); await signal.WaitAsync(timeout.Token); Check(received![2] == 200 && received[3] == 100, "Wrong single LED.");
        await Send(1050, [5, 0, 0, 0, 1]); Check(await stream.ReadAsync(new byte[1], timeout.Token) == 0, "Invalid packet not rejected.");
    }
}
