using System.Buffers.Binary;
using System.Diagnostics;

namespace WirelessCanvas;

public enum PumpRoute { None, Radio, Usb }

public static class PumpRouting
{
    // As in upstream h2_aio.rs: when the pump is bound to L-Wireless, the radio drives the ring,
    // and USB PushRgbData is skipped. USB is used only for an unbound pump.
    public static (PumpRoute Route, string Note) Decide(IReadOnlyCollection<WirelessGroup> radioGroups, bool usbConnected)
    {
        var pumps = radioGroups.Where(g => g.DeviceType == 10).ToArray();
        if (pumps.Length == 1 && pumps[0].IsHydro) return (PumpRoute.Radio, L10n.F((FormattableString)$"Pump {pumps[0].Mac}: RGB via L-Wireless"));
        if (pumps.Length > 1) return (PumpRoute.None, L10n.T("Multiple pumps are paired; pump RGB skipped."));
        if (pumps.Length == 1) return (PumpRoute.None, L10n.F((FormattableString)$"Pump {pumps[0].Mac} has {pumps[0].Fans} attached fans; LED layout is unverified. RGB skipped."));
        return usbConnected ? (PumpRoute.Usb, L10n.T("Pump: RGB via USB")) : (PumpRoute.None, "");
    }
}

/// <summary>The receiver returned an incomplete group list. Radio interference causes this; polling again usually works.</summary>
public sealed class PollException() : IOException(L10n.T("Incomplete or invalid group list response."));

public sealed class Controller : IDisposable
{
    readonly object gate = new();
    WinUsbTransport? tx, rx;
    readonly CancellationTokenSource stop = new();
    Task? worker;
    RadioDelay? radioDelay;
    string master = "";
    byte channel;
    int pages = 1;
    bool active;
    long lastRf, lastHeartbeat;
    string lastTarget = "";
    byte lastOpcode;
    readonly Dictionary<string, RgbUpload> desired = [];
    // Tracking of sent cycles: when to check and how many retries have happened (Resync).
    readonly Dictionary<string, (long CheckAt, int Attempts)> watch = [];
    // A retry goes only to a group that has not confirmed, 1.8 s after its transfer and at the same interval afterwards:
    // according to the logs, successful groups confirm a cycle in 0.7–1.6 s. While we wait for confirmation, polling runs
    // every 0.25 s instead of 0.8 s: it only reads receiver records, with no new radio transmissions.
    public const int ResyncAttempts = 6, ResyncDelay = 1800, ConfirmPollInterval = 250, HeartbeatInterval = 800;
    long lastPoll;
    volatile bool awaitingConfirmation;
    /// <summary>Poll at the normal 0.8 s interval, or every 0.25 s while groups have not confirmed a new cycle.</summary>
    internal static bool PollDue(long now, long lastHeartbeat, long lastPoll, bool awaiting) =>
        now - lastHeartbeat >= HeartbeatInterval || (awaiting && now - lastPoll >= ConfirmPollInterval);
    /// <summary>Some group has not confirmed its cycle yet and may get a retry.</summary>
    internal static bool AwaitingConfirmation(IReadOnlyDictionary<string, RgbUpload> desired, IReadOnlyList<WirelessGroup> groups, IReadOnlyDictionary<string, (long CheckAt, int Attempts)> watch) =>
        desired.Any(d => !groups.Any(g => g.Mac == d.Key && !g.MotherboardSync && g.Effect.SequenceEqual(d.Value.Effect)) && (!watch.TryGetValue(d.Key, out var state) || state.Attempts < ResyncAttempts));
    int pollFailures;
    readonly Dictionary<string, byte[]> pending = [];
    readonly Dictionary<string, byte[]> lastFrames = [];
    readonly Dictionary<string, (string Address, int Count)> sightings = [];
    readonly HashSet<byte> occupiedSlots = [];
    readonly RadioRecoveryPolicy radioRecovery = new();
    /// <summary>Enabled by the normal app only; read-only diagnostics and tests keep this false.</summary>
    public bool AutoRepairConflicts { get; set; }
    Profile? recoveryProfile;
    bool includePumpTiming;
    IdentificationSession? identification;
    public bool Identifying => identification != null;
    string lastLive = "";
    long nextLive;
    long lastError;
    long lastVendorCheck;
    public WirelessGroup[] Groups { get; private set; } = [];
    // Speed control: PWM target per group MAC. The command repeats once per second:
    // without a stream of commands the fans return to hardware speed by themselves (upstream fan_speed.rs).
    readonly Dictionary<string, byte[]> fanTargets = [];
    readonly Dictionary<string, long> fanSent = [];
    public const byte MinimumPwm = 35; // 14% is the SL V3 lower limit (min_duty_percent); other models use FanModel.MinimumPwm
    public string FanStatus { get; private set; } = "";
    /// <summary>Sets group PWM (0–100% per fan of the group). Groups missing from the dictionary are not controlled.</summary>
    public void SetFanTargets(IReadOnlyDictionary<string, double> percent)
    {
        lock (gate)
        {
            fanTargets.Clear();
            foreach (var (mac, value) in percent)
            {
                var group = Groups.SingleOrDefault(g => g.Mac == mac);
                if (group == null || !group.SupportsSpeed) continue;
                fanTargets[mac] = PreparePwm(group, value);
            }
            foreach (var stale in fanSent.Keys.Except(fanTargets.Keys).ToArray()) fanSent.Remove(stale);
        }
    }
    /// <summary>Stops speed commands: within a few seconds the fans return to hardware speed.</summary>
    public void ReleaseFans() { lock (gate) { fanTargets.Clear(); fanSent.Clear(); FanStatus = ""; } }
    public bool ControllingFans { get { lock (gate) return fanTargets.Count > 0; } }
    /// <summary>PWM for the group slots: percent → 0–255, never below the model limit (14% for SL V3) and never 0 for an existing fan; empty slots get — 0.</summary>
    public static byte[] PreparePwm(WirelessGroup group, double percent)
    {
        var model = group.Model ?? throw new InvalidDataException(L10n.T("Unknown fan model: speed is not set."));
        int value = Math.Clamp((int)Math.Round(Math.Clamp(percent, 0, 100) * 255 / 100), model.MinimumPwm, 255);
        // upstream apply_pwm_constraints: CL does not accept 153–155.
        if (model.ClPwmFilter) value = value switch { 153 or 154 => 152, 155 => 156, _ => value };
        return [.. Enumerable.Range(0, 4).Select(i => i < group.Fans ? (byte)value : (byte)0)];
    }
    /// <summary>RF 0x12 0x10: MAC, master, rx slot, master channel, device number, 4 × PWM (upstream build_pwm_packet).</summary>
    public static byte[] PwmPacket(WirelessGroup group, string master, byte masterChannel, byte[] pwm)
    {
        if (pwm.Length != 4 || group.SlotIndex is < 1 or > 255) throw new InvalidDataException(L10n.T("Device address for speed control is unconfirmed."));
        byte[] rf = new byte[240]; rf[0] = 0x12; rf[1] = 0x10;
        Convert.FromHexString(group.Mac).CopyTo(rf, 2); Convert.FromHexString(master).CopyTo(rf, 8);
        rf[14] = group.Slot; rf[15] = masterChannel; rf[16] = (byte)group.SlotIndex;
        pwm.CopyTo(rf, 17);
        // SL-INF on the right: the slot order on the wire is reversed (upstream reverse_fan_order).
        if (group.RightAttach && group.Fans > 1) Array.Reverse(rf, 17, Math.Min(group.Fans, 4));
        return rf;
    }
    void SendFanTargets()
    {
        if (fanTargets.Count == 0 || identification != null) return;
        EnableLighting(); // starts the 0x14 keep-alive pulse once per second: without it the firmware falls back to standalone mode
        long now = Environment.TickCount64;
        List<string> notes = [];
        foreach (var (mac, pwm) in fanTargets.ToArray())
        {
            var group = Groups.SingleOrDefault(g => g.Mac == mac);
            if (group == null) { notes.Add(L10n.F((FormattableString)$"{mac}: disconnected")); continue; }
            bool acked = group.Pwm.Zip(pwm).All(x => Math.Abs(x.First - x.Second) <= 5);
            if (fanSent.TryGetValue(mac, out long last) && now - last < 1000) { if (!acked && now - last > 600) notes.Add(L10n.F((FormattableString)$"{mac}: awaiting confirmation")); continue; }
            // An unconfirmed group address is skipped instead of dropping the connection (RGB and other groups keep working).
            try { ValidateTarget(group); } catch (IOException e) { notes.Add($"{mac}: {e.Message}"); continue; }
            SendRf(group.Mac, 0x10, Wrap(PwmPacket(group, master, channel, pwm), group.Channel is >= 1 and <= 39 ? group.Channel : channel, group.Slot));
            fanSent[mac] = now;
        }
        FanStatus = notes.Count == 0 ? L10n.F((FormattableString)$"Speed control groups: {fanTargets.Count}") : string.Join("; ", notes);
    }
    public string[] BoundHydroMacs { get; private set; } = [];

    // Pump of a HydroShift II bound to L-Wireless. As in upstream controllers/aio.rs: the pump is switched once
    // to the wireless theme (RF 0x12 0x19, acknowledged by the command number in its record), then
    // receives a 32-byte parameter block (RF 0x12 0x21) with the target pump RPM once per second.
    // The RPM is constant and no CPU/GPU temperatures go into the block: the packet only changes when settings change.
    // USB-speed command SyncPumpFan is not used: upstream skips it for a bound pump.
    public const int PumpMinRpm = 1600, PumpMaxRpm = 2500; // HydroShift II LCD-C (WaterBlock), upstream pump_rpm_range
    PumpParameters? pumpTarget;
    long pumpSent;
    (byte Sequence, long SentAt, bool Acknowledged)? pumpTheme;
    public string PumpStatus { get; private set; } = "";
    /// <param name="UseTheme">Switch the pump to its built-in wireless theme (as upstream). false when the app drives the pump screen:
    /// the theme draws coolant and RPM over the USB image; the pump RPM is sent only in the parameter block.</param>
    public sealed record PumpParameters(int Rpm, int Brightness, int Rotation, bool UseTheme = true);
    /// <summary>Pump target, or null: no pump commands are sent (the pump keeps its last value).</summary>
    public void SetPumpTarget(PumpParameters? target)
    {
        lock (gate)
        {
            if (target == null && pumpTarget != null) PumpStatus = "";
            pumpTarget = target;
        }
    }
    public bool ControllingPump { get { lock (gate) return pumpTarget != null; } }
    /// <summary>Curve percent (0–100) → RPM within the pump range, as upstream rpm_from_percent.</summary>
    /// <summary>Pump RPM → chart scale percent (0% = 1600, 100% = 2500 RPM).</summary>
    public static double PumpPercent(int rpm) => (Math.Clamp(rpm, PumpMinRpm, PumpMaxRpm) - PumpMinRpm) * 100.0 / (PumpMaxRpm - PumpMinRpm);
    /// <summary>Scale percent → RPM in 50 RPM steps.</summary>
    public static int PumpStep(double percent) => Math.Clamp((int)(Math.Round(PumpRpm(percent) / 50.0) * 50), PumpMinRpm, PumpMaxRpm);
    public static int PumpRpm(double percent) => (int)Math.Round(PumpMinRpm + Math.Clamp(percent, 0, 100) / 100 * (PumpMaxRpm - PumpMinRpm));
    /// <summary>LCD-C pump PWM timer from RPM (upstream circle_pump_timer, piecewise-linear firmware table).</summary>
    public static ushort PumpTimer(int rpm)
    {
        double r = Math.Clamp(rpm, PumpMinRpm, PumpMaxRpm);
        double t = r <= 1720 ? 1500 - (r - 1600) * 1.667 : r <= 1870 ? 1300 - (r - 1720) * 2.0 : r <= 2000 ? 1000 - (r - 1870) * 1.23
            : r <= 2300 ? 840 - (r - 2000) * 2.0 : r <= 2400 ? 240 - (r - 2300) * 1.8 : 60 - (r - 2400) * 0.5;
        return (ushort)Math.Clamp((int)(float)t, 0, ushort.MaxValue);
    }
    /// <summary>
    /// 32-byte aio_param block (upstream build_aio_param): colors, brightness, theme 0, pump timer, rotation.
    /// Temperature fields (0–3) and their flags (8–11) are zero, as upstream does without sensors.
    /// </summary>
    public static byte[] PumpParameterBlock(PumpParameters target)
    {
        byte[] p = new byte[32];
        p[6] = 3; p[7] = 1;
        p.AsSpan(13, 12).Fill(0xFF); // three ARGB colors are white
        p[25] = (byte)Math.Clamp(target.Brightness, 0, 100); p[26] = 1; p[27] = 0;
        BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(28), PumpTimer(target.Rpm));
        p[30] = (byte)Math.Clamp(target.Rotation, 0, 3);
        return p;
    }
    /// <summary>RF 0x12 0x21: MAC of the pump, master, rx slot, master channel, device number, parameter block from byte 18.</summary>
    public static byte[] PumpParametersPacket(WirelessGroup pump, string master, byte masterChannel, byte[] block)
    {
        if (block.Length != 32 || pump.SlotIndex is < 1 or > 255 || !pump.IsHydro) throw new InvalidDataException(L10n.T("Pump address for speed control is unconfirmed."));
        byte[] rf = new byte[240]; rf[0] = 0x12; rf[1] = 0x21;
        Convert.FromHexString(pump.Mac).CopyTo(rf, 2); Convert.FromHexString(master).CopyTo(rf, 8);
        rf[14] = pump.Slot; rf[15] = masterChannel; rf[16] = (byte)pump.SlotIndex;
        block.CopyTo(rf, 18);
        return rf;
    }
    /// <summary>RF 0x12 0x19: switches the pump to the wireless theme with a command number (upstream wireless_theme_packet).</summary>
    public static byte[] PumpThemePacket(WirelessGroup pump, string master, byte masterChannel, byte sequence)
    {
        if (pump.SlotIndex is < 1 or > 255 || !pump.IsHydro) throw new InvalidDataException(L10n.T("Pump address for speed control is unconfirmed."));
        byte[] rf = new byte[240]; rf[0] = 0x12; rf[1] = 0x19;
        Convert.FromHexString(pump.Mac).CopyTo(rf, 2); Convert.FromHexString(master).CopyTo(rf, 8);
        rf[14] = pump.Slot; rf[15] = masterChannel; rf[16] = (byte)pump.SlotIndex; rf[17] = sequence;
        return rf;
    }
    public static byte NextSequence(byte observed) => observed <= 253 ? (byte)(observed + 1) : (byte)1;
    void SendPumpTarget()
    {
        if (pumpTarget == null || identification != null) return;
        long now = Environment.TickCount64;
        if (now - pumpSent < 1000) return;
        var pump = Groups.SingleOrDefault(g => g.IsHydro);
        if (pump == null) { PumpStatus = L10n.T("pump not found via radio"); return; }
        try { ValidateTarget(pump); } catch (IOException e) { PumpStatus = e.Message; return; }
        EnableLighting();
        byte targetChannel = pump.Channel is >= 1 and <= 39 ? pump.Channel : channel;
        if (pumpTheme is { } theme && !theme.Acknowledged && pump.Sequence == theme.Sequence) pumpTheme = theme with { Acknowledged = true };
        if (pumpTarget.UseTheme && pumpTheme is not { Acknowledged: true } && (pumpTheme == null || now - pumpTheme.Value.SentAt >= 2000))
        {
            byte sequence = NextSequence(pumpTheme?.Sequence ?? pump.Sequence);
            SendRf(pump.Mac, 0x19, Wrap(PumpThemePacket(pump, master, channel, sequence), targetChannel, pump.Slot));
            pumpTheme = (sequence, now, false);
            Log.Write(L10n.F((FormattableString)$"Pump {pump.Mac}: wireless theme, command {sequence}"));
        }
        SendRf(pump.Mac, 0x21, Wrap(PumpParametersPacket(pump, master, channel, PumpParameterBlock(pumpTarget)), targetChannel, pump.Slot));
        pumpSent = now;
        PumpStatus = L10n.F((FormattableString)$"pump: fixed {pumpTarget.Rpm} RPM, actual {pump.Rpm[3]}") + (!pumpTarget.UseTheme ? L10n.T(" · no wireless theme (app controls screen)") : pumpTheme is { Acknowledged: true } ? "" : L10n.T(" · awaiting pump confirmation"));
    }
    // Master RGB clock (0.625 ms tick). Devices pick the frame as ticks·100/interval mod frames
    // (upstream sync_clock.rs), so external OpenRGB devices that follow it stay in phase with the fans.
    sealed record ClockSample(uint Ticks, long Stamp);
    sealed record SceneTiming(int Frames, uint Interval);
    ClockSample? clock;
    SceneTiming? sceneTiming;
    long lastClock;
    public static double PhaseAt(uint ticks, double elapsedSeconds, uint interval, int frames)
    {
        double elapsed = Math.Max(0, elapsedSeconds) * 1600, whole = Math.Floor(elapsed);
        double now = unchecked(ticks + (uint)(ulong)whole) + (elapsed - whole);
        return now * 100 / interval % frames / frames;
    }
    /// <summary>Cycle position 0–1 the fans are playing now; null when the clock or scene is unknown.</summary>
    public double? ScenePhase()
    {
        var (sample, timing) = (clock, sceneTiming);
        if (sample == null || timing == null) return null;
        return PhaseAt(sample.Ticks, Stopwatch.GetElapsedTime(sample.Stamp).TotalSeconds, timing.Interval, timing.Frames);
    }
    void SampleClock()
    {
        lastClock = Environment.TickCount64;
        byte[] cmd = new byte[64]; cmd[0] = 0x11; cmd[1] = channel;
        tx!.Flush(); tx.Write(cmd);
        var response = tx.Read(500); long stamp = Stopwatch.GetTimestamp();
        if (response.Length < 11 || response[0] != 0x11 || Convert.ToHexString(response.AsSpan(1, 6)) != master) return;
        uint ticks = BinaryPrimitives.ReadUInt32BigEndian(response.AsSpan(7));
        if (ticks > 1) clock = new(ticks, stamp);
    }
    public bool Connected => tx != null && rx != null;
    public string Status { get; private set; } = L10n.T("Hardware disconnected");
    public static bool VendorRunning() => Process.GetProcesses().Any(p => { using (p) return p.ProcessName.Contains("L-Connect", StringComparison.OrdinalIgnoreCase) || p.ProcessName.Contains("LConnect", StringComparison.OrdinalIgnoreCase); });
    public void Connect()
    {
        lock (gate)
        {
            EnsureNotIdentifying();
            if (tx != null)
            {
                for (int sample = 0; sample < 3; sample++) { Poll(); if (sample < 2) Thread.Sleep(100); }
                RecoverRadioConflicts(waitForStable: true);
                return;
            }
            if (VendorRunning()) throw new InvalidOperationException(L10n.T("L-Connect 3 detected. Close its window and background processes before connecting. Services are not stopped automatically."));
            var inventory = UsbInventory.Scan();
            var pairs = new[] { ("VID_0416&PID_8040", "VID_0416&PID_8041"), ("VID_1A86&PID_E304", "VID_1A86&PID_E305") };
            foreach (var pair in pairs)
            {
                var sender = inventory.Where(x => x.Id.StartsWith(pair.Item1, StringComparison.OrdinalIgnoreCase) && x.Paths.Length > 0).ToArray();
                var receiver = inventory.Where(x => x.Id.StartsWith(pair.Item2, StringComparison.OrdinalIgnoreCase) && x.Paths.Length > 0).ToArray();
                if (sender.Length == 0 || receiver.Length == 0) continue;
                if (sender.Length != 1 || receiver.Length != 1) throw new IOException(L10n.T("Multiple controllers found. Connect one L-Wireless Controller to select TX/RX."));
                try
                {
                    tx = new(sender[0].Paths[0]); rx = new(receiver[0].Paths[0]);
                    radioDelay ??= new RadioDelay();
                    pages = 1; lastRf = 0; lastTarget = ""; active = false;
                    sightings.Clear(); QueryMaster();
                    for (int sample = 0; sample < 3; sample++) { Poll(); if (sample < 2) Thread.Sleep(100); }
                    RecoverRadioConflicts(waitForStable: true);
                    Status = L10n.F((FormattableString)$"Connected: {Groups.Count(g => g.Supported)} wireless fan groups, MAC {master}") + (Groups.Any(g => g.IsHydro) ? L10n.T("; wireless pump found") : "");
                    Log.Write(L10n.F((FormattableString)$"{Status}; high-resolution timer: {radioDelay.HighResolution}"));
                    worker ??= Task.Run(Work);
                    return;
                }
                catch { CloseHandles(); throw; }
            }
            string summary = inventory.Count == 0 ? L10n.T("TX/RX not found.") : string.Join("; ", inventory.Select(x => L10n.F((FormattableString)$"{x.Id}: driver {x.Service}, interfaces {x.Paths.Length}")));
            throw new IOException(L10n.F((FormattableString)$"No WinUSB TX/RX pair available. {summary} Save diagnostics. Drivers are not modified."));
        }
    }
    void QueryMaster()
    {
        int failures = 0;
        foreach (byte ch in new byte[] { 8, 8, 8 }.Concat(Enumerable.Range(1, 39).Where(x => x != 8).Select(x => (byte)x)))
        {
            stop.Token.ThrowIfCancellationRequested();
            byte[] cmd = new byte[64]; cmd[0] = 0x11; cmd[1] = ch;
            tx!.Flush();
            try { tx.Write(cmd); } catch { if (++failures >= 2) throw; continue; }
            var response = tx.Read(500);
            if (response.Length < 13 || response[0] != 0x11 || response.AsSpan(1, 6).IndexOfAnyExcept((byte)0) < 0 || BinaryPrimitives.ReadUInt32BigEndian(response.AsSpan(7)) <= 1) continue;
            master = Convert.ToHexString(response.AsSpan(1, 6)); channel = ch;
            clock = new(BinaryPrimitives.ReadUInt32BigEndian(response.AsSpan(7)), Stopwatch.GetTimestamp()); lastClock = Environment.TickCount64;
            return;
        }
        throw new IOException(L10n.T("Invalid MAC query response. Reconnect the controller and retry."));
    }
    void Poll()
    {
        byte[] request = new byte[64]; request[0] = 0x10; request[1] = (byte)pages;
        rx!.Flush(); rx.Write(request); lastPoll = Environment.TickCount64;
        byte[] data = rx.ReadAll(pages * 512, 100);
        if (data.Length < 4 || data[0] != 0x10 || data.Length < 4 + Math.Min((int)data[1], pages * 10) * 42) throw new PollException();
        int count = Math.Min((int)data[1], pages * 10); pages = Math.Clamp((data[1] + 9) / 10, 1, 26);
        List<WirelessGroup> groups = [];
        List<string> hydroMacs = [];
        int bound = 0;
        occupiedSlots.Clear();
        for (int i = 0; i < count; i++)
        {
            var record = data.AsSpan(4 + i * 42, 42);
            // Number for the speed command: position among the records bound to our master (as next_slot_index).
            bool ours = record[41] == 0x1C && record[18] != 0xFF && record[..6].IndexOfAnyExcept((byte)0) >= 0 && Convert.ToHexString(record[6..12]) == master;
            if (ours) { bound++; occupiedSlots.Add(record[13]); }
            // H2 has its own device_type=10; it must not be treated as SL fans.
            if (record[41] == 0x1C && record[18] == 10 && Convert.ToHexString(record[6..12]) == master && record[..6].IndexOfAnyExcept((byte)0) >= 0)
                hydroMacs.Add(Convert.ToHexString(record[..6]));
            var group = WirelessGroup.Parse(record, i);
            if (group != null && group.Master == master) groups.Add(group with { SlotIndex = bound });
        }
        BoundHydroMacs = hydroMacs.Distinct().ToArray();
        Groups = groups.OrderBy(g => g.Mac, StringComparer.Ordinal).ToArray();
        foreach (var stale in sightings.Keys.Except(Groups.Select(g => g.Mac)).ToArray()) sightings.Remove(stale);
        foreach (var group in Groups)
        {
            string address = $"{group.Master}/{group.Channel}/{group.Slot}/{group.Fans}/{Convert.ToHexString(group.Types)}";
            int confirmations = sightings.TryGetValue(group.Mac, out var previous) && previous.Address == address ? Math.Min(previous.Count + 1, 3) : 1;
            sightings[group.Mac] = (address, confirmations);
        }
        radioRecovery.Observe(Groups, ConfirmedRadioMacs(), Environment.TickCount64);
    }
    HashSet<string> ConfirmedRadioMacs() => sightings.Where(s => s.Value.Count >= 3).Select(s => s.Key).ToHashSet(StringComparer.Ordinal);
    void RecoverRadioConflicts(bool waitForStable)
    {
        if (!AutoRepairConflicts || !RadioRecoveryPolicy.HasConflicts(Groups) || identification != null || VendorRunning()) return;
        if (waitForStable)
        {
            if (radioRecovery.Plan(Groups, master, occupiedSlots, ConfirmedRadioMacs(), Environment.TickCount64 + RadioRecoveryPolicy.SettleMilliseconds) == null) return;
            var settling = Stopwatch.StartNew();
            while (RadioRecoveryPolicy.HasConflicts(Groups) && settling.ElapsedMilliseconds < RadioRecoveryPolicy.SettleMilliseconds)
            {
                stop.Token.ThrowIfCancellationRequested(); Thread.Sleep(150); PollRetry();
            }
        }
        for (int repair = 0; repair < 3; repair++)
        {
            var plan = radioRecovery.Plan(Groups, master, occupiedSlots, ConfirmedRadioMacs(), Environment.TickCount64);
            if (plan == null) return;
            radioRecovery.MarkAttempt(plan.Mac, Environment.TickCount64);
            Status = L10n.F((FormattableString)$"Recovering radio address {plan.Mac} → slot {plan.Slot}…");
            Log.Write(Status);
            try
            {
                RepairRadioSlot(plan.Mac, plan.Slot);
                fanSent.Remove(plan.Mac); // Apply the cooling target again using the confirmed new address.
                Status = L10n.F((FormattableString)$"Radio address {plan.Mac} recovered: slot {plan.Slot}.");
                Log.Write(Status);
            }
            catch (Exception e) when (e is IOException or InvalidDataException)
            {
                Status = L10n.F((FormattableString)$"Could not recover radio address {plan.Mac}: {e.Message}");
                Log.Write(Status); return;
            }
        }
    }
    void ValidateTarget(WirelessGroup group)
    {
        if (!group.RgbSupported || group.Master != master || group.Channel is < 1 or > 39 || group.Slot is < 1 or >= 14 || Groups.Count(g => g.Slot == group.Slot) != 1 || !sightings.TryGetValue(group.Mac, out var sighting) || sighting.Count < 3)
        {
            Log.Write($"RGB target rejected: MAC={group.Mac}, master={group.Master}/{master}, channel={group.Channel}, RX={group.Slot}, supported={group.RgbSupported}, sightings={sightings.GetValueOrDefault(group.Mac).Count}, sharedRX={string.Join(',', Groups.Where(g => g.Slot == group.Slot).Select(g => g.Mac))}");
            throw new IOException(L10n.T("Group type or radio address unconfirmed. Transfer blocked."));
        }
    }
    /// <summary>Explicit maintenance operation: repair one locally bound SL group, never an unbind or pump operation.</summary>
    public string RepairRadioSlot(string mac, byte preferredSlot = 0)
    {
        lock (gate)
        {
            EnsureNotIdentifying();
            if (tx == null || rx == null || VendorRunning()) throw new IOException("Close other RGB applications and connect the controller before repairing its radio address.");
            for (int i = 0; i < 3; i++) { PollRetry(); Thread.Sleep(100); }
            var group = Groups.SingleOrDefault(g => g.Mac == mac) ?? throw new IOException("Radio group not found: " + mac);
            if (Groups.Count(g => g.Slot == group.Slot) < 2) return "No slot collision found; no commands sent.";
            if (!sightings.TryGetValue(mac, out var seen) || seen.Count < 3) throw new IOException("Radio address is not stable; repair cancelled.");
            byte slot = preferredSlot != 0 ? preferredSlot : (byte)Enumerable.Range(1, 13).FirstOrDefault(s => !occupiedSlots.Contains((byte)s));
            byte[] rf = RadioRepairPacket(group, master, channel, slot, occupiedSlots);
            var baseline = Groups.ToArray();
            Log.Write($"Radio repair start: {mac}, RX {group.Slot} -> {slot}; PWM {Convert.ToHexString(rf.AsSpan(17, 4))}");
            var deadline = Stopwatch.StartNew(); bool confirmed = false;
            while (deadline.Elapsed < TimeSpan.FromSeconds(5) && !confirmed)
            {
                stop.Token.ThrowIfCancellationRequested();
                if (VendorRunning()) throw new IOException("L-Connect started during radio repair; operation stopped.");
                // Upstream bind.rs: full MAC in the payload, broadcast outer RX because the old slot collides.
                for (int repeat = 0; repeat < 6; repeat++) { SendRf(mac, 0x10, Wrap(rf, group.Channel, 0xFF)); Thread.Sleep(30); }
                int stable = 0;
                for (int sample = 0; sample < 5; sample++)
                {
                    Thread.Sleep(150); PollRetry();
                    if (baseline.Where(g => g.Mac != mac).Any(old => !Groups.Any(f => f.Mac == old.Mac && f.Master == old.Master && f.Channel == old.Channel && f.Slot == old.Slot && f.DeviceType == old.DeviceType && f.Fans == old.Fans && f.Types.SequenceEqual(old.Types))))
                        throw new IOException("Another radio group changed during repair; configuration was not saved.");
                    var fresh = Groups.SingleOrDefault(g => g.Mac == mac);
                    bool matches = fresh != null && fresh.Master == master && fresh.Channel == channel && fresh.Slot == slot && fresh.DeviceType == group.DeviceType && fresh.Fans == group.Fans && fresh.Types.SequenceEqual(group.Types) && Groups.Count(g => g.Slot == slot) == 1;
                    stable = matches ? stable + 1 : 0;
                    if (stable >= 3) { confirmed = true; break; }
                }
            }
            if (!confirmed) throw new IOException("New radio address was not confirmed. Configuration was not saved; run the radio probe before retrying.");
            byte[] save = RadioSavePacket(master);
            for (int repeat = 0; repeat < 3; repeat++) { SendRf("FFFFFFFFFFFF", 0x15, Wrap(save, channel, 0xFF)); Thread.Sleep(200); }
            PollRetry();
            var result = Groups.Single(g => g.Mac == mac);
            ValidateTarget(result);
            if (result.Slot != slot || result.Channel != channel) throw new IOException("Radio address changed after SaveConfig; verify the controller.");
            string note = $"Radio repair confirmed: {mac}, RX {group.Slot} -> {slot}; configuration save sent. Cold-boot persistence requires a power-cycle check.";
            Log.Write(note); return note;
        }
    }
    internal static byte[] RadioRepairPacket(WirelessGroup group, string master, byte channel, byte slot, IReadOnlyCollection<byte> occupied)
    {
        if (Convert.FromHexString(group.Mac).Length != 6 || Convert.FromHexString(master).Length != 6 || group.Mac == "000000000000" || master == "000000000000" || !group.RepairSupported || group.Master != master || group.Channel is < 1 or > 39 || channel is < 1 or > 39 || slot is < 1 or >= 14 || occupied.Contains(slot) || group.Pwm.Length != 4 || group.Types.Length != 4 || group.SlotIndex is < 1 or > 255)
            throw new InvalidDataException("Unconfirmed radio repair target or no free RX slot.");
        // Same 0x12/0x10 command as upstream build_bind_packet. Never send zero to an installed fan.
        byte[] pwm = [.. Enumerable.Range(0, 4).Select(i => i < group.Fans ? (group.Pwm[i] == 0 ? (byte)255 : Math.Max(MinimumPwm, group.Pwm[i])) : (byte)0)];
        return PwmPacket(group with { Slot = slot }, master, channel, pwm);
    }
    internal static byte[] RadioSavePacket(string master)
    {
        if (Convert.FromHexString(master).Length != 6 || master == "000000000000") throw new InvalidDataException("Invalid master MAC.");
        byte[] rf = new byte[240]; rf[0] = 0x12; rf[1] = 0x15;
        rf.AsSpan(2, 6).Fill(0xFF); Convert.FromHexString(master).CopyTo(rf, 8); rf[14] = 0xFF;
        return rf;
    }
    void SendRf(string target, byte opcode, IEnumerable<byte[]> packets)
    {
        if (target != lastTarget || opcode != lastOpcode)
        {
            long remaining = (lastOpcode == 0x20 ? 10 : 5) - (Environment.TickCount64 - lastRf);
            if (remaining > 0) radioDelay!.Wait((int)remaining);
        }
        foreach (byte[] packet in packets) { tx!.Write(packet); radioDelay!.Wait(1); }
        lastTarget = target; lastOpcode = opcode; lastRf = Environment.TickCount64;
    }
    static IEnumerable<byte[]> Wrap(byte[] rf, byte channel, byte slot)
    {
        for (int i = 0; i < 4; i++)
        {
            byte[] packet = new byte[64]; packet[0] = 0x10; packet[1] = (byte)i; packet[2] = channel; packet[3] = slot;
            Array.Copy(rf, i * 60, packet, 4, 60); yield return packet;
        }
    }
    void Heartbeat()
    {
        if (!active || Environment.TickCount64 - lastHeartbeat < 800) return;
        byte[] rf = MasterClockPacket(master);
        SendRf("000000000000", 0x14, Wrap(rf, channel, 0xFF)); lastHeartbeat = Environment.TickCount64;
    }
    public static byte[] MasterClockPacket(string master)
    {
        byte[] rf = new byte[240]; rf[0] = 0x12; rf[1] = 0x14;
        Convert.FromHexString(master).CopyTo(rf, 8);
        // Sentinel from upstream protects the radio address from mistakenly capturing sensor data.
        rf.AsSpan(14, 50).Fill(0x14);
        return rf;
    }
    void EnableLighting()
    {
        if (active) return;
        byte[] cmd = new byte[64]; cmd[0] = 0x11; cmd[1] = 1; tx!.Write(cmd); radioDelay!.Wait(2);
        for (int i = 0; i < Math.Max(1, Groups.Length); i++)
        {
            byte[] prep = new byte[64]; prep[0] = 0x10; prep[1] = (byte)i; prep[2] = channel; prep[3] = 0xFF; tx.Write(prep); radioDelay!.Wait(1);
        }
        active = true; Heartbeat();
    }
    /// <summary>
    /// upstream ensure_video_mode: before talking to LCD fans, L-Wireless is switched to video mode.
    /// These are the same 0x11 0x01 and slot preparation as the RGB start (EnableLighting): no new commands.
    /// Waits no longer than 200 ms when the radio is busy uploading RGB, and never throws into the screen thread.
    /// </summary>
    public void EnsureVideoMode()
    {
        if (active || tx == null || !Monitor.TryEnter(gate, 200)) return;
        try { if (tx != null && identification == null) EnableLighting(); }
        catch (Exception e) { Log.Write(L10n.T("L-Wireless LCD video mode not enabled: ") + e.Message); }
        finally { Monitor.Exit(gate); }
    }
    void DisableMotherboardSync(WirelessGroup group)
    {
        if (!group.MotherboardSync) return;
        byte[] rf = new byte[240]; rf[0] = 0x12; rf[1] = 0x27;
        Convert.FromHexString(group.Mac).CopyTo(rf, 2); Convert.FromHexString(master).CopyTo(rf, 8);
        int groupIndex = group.ListIndex;
        if (groupIndex is < 0 or > 255) throw new IOException(L10n.T("RX group index unconfirmed; cannot disable motherboard RGB sync."));
        rf[14] = group.Slot; rf[15] = channel; rf[16] = (byte)groupIndex; rf[17] = unchecked((byte)(group.Sequence + 1));
        SendRf(group.Mac, 0x27, Wrap(rf, group.Channel, group.Slot));
        for (int i = 0; i < 10; i++)
        {
            Thread.Sleep(100); Heartbeat(); Poll();
            var fresh = Groups.SingleOrDefault(g => g.Mac == group.Mac);
            if (fresh != null && !fresh.MotherboardSync && fresh.Sequence == rf[17]) return;
        }
        throw new IOException(L10n.T("Motherboard RGB sync disable was not confirmed."));
    }
    void Upload(WirelessGroup group, RgbUpload upload)
    {
        group = Groups.SingleOrDefault(candidate => candidate.Mac == group.Mac) ?? throw new IOException(L10n.T("Group disappeared before RGB upload."));
        ValidateTarget(group);
        if (upload.Leds != group.LedCount) throw new InvalidDataException(L10n.T("LED count mismatch."));
        EnableLighting(); DisableMotherboardSync(group);
        var packets = upload.Packets(group.Mac, master, group.Channel, group.Slot).ToArray();
        try
        {
            var transfer = RgbTransfer.Send(packets, frame => SendRf(group.Mac, 0x20, frame), Heartbeat, radioDelay!.Wait, stop.Token, headerFrames: RgbUpload.HeaderRepeats);
            Log.Write(L10n.F((FormattableString)$"RGB sent: {(group.IsHydro ? L10n.T("pump") : L10n.T("group"))} {group.Mac}, {(group.IsHydro ? L10n.T("24-LED ring") : L10n.F((FormattableString)$"{group.Fans} fans"))}, {upload.Compressed.Length} bytes, {transfer.RadioFrames} RF packets, {transfer.Elapsed.TotalMilliseconds:F0} ms; awaiting confirmation."));
        }
        catch (Exception e) when (e is not OperationCanceledException) { throw new IOException(L10n.F((FormattableString)$"Group {group.Mac} ({group.Fans} fans): {e.Message}"), e); }
        desired[group.Mac] = upload; awaitingConfirmation = true;
        watch[group.Mac] = (Environment.TickCount64 + ResyncDelay, watch.TryGetValue(group.Mac, out var state) ? state.Attempts : 0);
    }
    /// <summary>
    /// As upstream (rgb_upload_applied / rgb_drifted): a group that has not confirmed the new cycle within 1.8 s, or later
    /// reset (the firmware went back to its built-in rainbow), gets the cycle again: only that group, up to 6 times 1.8 s apart.
    /// The check uses the poll already done, with no extra controller requests; data is resent only on failure.
    /// Synchronization is not affected: the frame is chosen by the shared master clock.
    /// </summary>
    internal static bool NeedsResync(WirelessGroup? group, RgbUpload upload, (long CheckAt, int Attempts) state, long now) =>
        group != null && !(group.Effect.SequenceEqual(upload.Effect) && !group.MotherboardSync) && now >= state.CheckAt && state.Attempts < ResyncAttempts;
    void Resync()
    {
        if (identification != null || pending.Count > 0) return;
        foreach (var (mac, upload) in desired.ToArray())
        {
            var group = Groups.SingleOrDefault(g => g.Mac == mac);
            (long CheckAt, int Attempts) state = watch.TryGetValue(mac, out var known) ? known : (Environment.TickCount64 + ResyncDelay, 0);
            if (group != null && group.Effect.SequenceEqual(upload.Effect) && !group.MotherboardSync)
            {
                if (state.Attempts > 0) Log.Write(L10n.F((FormattableString)$"RGB {mac}: cycle confirmed after retry {state.Attempts}."));
                watch[mac] = (state.CheckAt, 0);
                continue;
            }
            if (!NeedsResync(group, upload, state, Environment.TickCount64)) { watch[mac] = state; continue; }
            Log.Write(L10n.F((FormattableString)$"RGB {mac}: cycle unconfirmed or reset to built-in effect; retry {state.Attempts + 1}/{ResyncAttempts}."));
            watch[mac] = (0, state.Attempts + 1);
            try { Upload(group!, upload); }
            catch (IOException e) { Log.Write(L10n.F((FormattableString)$"RGB {mac}: retry not sent: {e.Message}")); }
            // Even pause after each transfer: a lost radio packet is random, and waiting longer does not fix it.
            watch[mac] = (Environment.TickCount64 + ResyncDelay, state.Attempts + 1);
        }
    }
    /// <summary>Poll with one retry: a single incomplete receiver reply does not abort applying.</summary>
    void PollRetry()
    {
        try { Poll(); }
        catch (PollException) { Thread.Sleep(50); Poll(); }
    }
    public string Apply(Profile profile, RgbScene? prepared = null)
    {
        profile = System.Text.Json.JsonSerializer.Deserialize<Profile>(System.Text.Json.JsonSerializer.Serialize(profile))!;
        profile.Validate();
        WirelessGroup[] selected;
        lock (gate)
        {
            EnsureNotIdentifying();
            if (VendorRunning()) throw new IOException(L10n.T("Close L-Connect 3 before sending RGB."));
            if (tx == null) throw new IOException(L10n.T("Connect the controller first."));
            PollRetry();
            RecoverRadioConflicts(waitForStable: true);
            selected = Groups.Where(g => g.Supported && profile.Fans.Any(f => f.Group == g.Mac && f.Enabled)).ToArray();
            if (prepared?.Pump != null || (prepared == null && includePumpTiming))
            {
                var (route, note) = PumpRouting.Decide(Groups, false);
                if (route != PumpRoute.Radio) throw new IOException(note.Length > 0 ? note : L10n.T("Pump no longer found in L-Wireless. Reconnect."));
                selected = selected.Concat(Groups.Where(g => g.IsHydro)).ToArray();
            }
            if (selected.Length == 0) throw new IOException(L10n.T("No confirmed RGB devices selected."));
            foreach (var g in selected) ValidateTarget(g);
        }
        var scene = prepared ?? RgbScene.Prepare(profile, selected.Where(g => g.Supported).ToArray(), stop.Token, includePumpTiming);
        if (!scene.Uploads.Keys.Order().SequenceEqual(selected.Where(g => g.Supported).Select(g => g.Mac).Order())) throw new IOException(L10n.T("Device list changed while preparing the scene."));
        includePumpTiming = scene.Pump != null;
        lock (gate)
        {
            EnsureNotIdentifying();
            if (tx == null || VendorRunning()) throw new IOException(L10n.T("Connection changed while preparing RGB."));
            PollRetry();
            foreach (var group in selected)
            {
                var fresh = Groups.SingleOrDefault(g => g.Mac == group.Mac);
                if (fresh == null || fresh.DeviceType != group.DeviceType || fresh.Fans != group.Fans || !fresh.Types.SequenceEqual(group.Types)) throw new IOException(L10n.T("Group changed during preparation. Apply again."));
                ValidateTarget(fresh);
            }
            // Rendering depends on LED layout; radio addresses may have recovered while it ran.
            selected = selected.Select(g => Groups.Single(f => f.Mac == g.Mac)).ToArray();
            pending.Clear(); desired.Clear(); lastFrames.Clear(); watch.Clear();
            recoveryProfile = null;
            int sentGroups = 0;
            try
            {
                foreach (var g in selected)
                {
                    Status = L10n.F((FormattableString)$"Uploading {sentGroups + 1}/{selected.Length}: {g.Mac}, ") + (g.IsHydro ? L10n.T("pump ring") : L10n.F((FormattableString)$"{g.Fans} fans"));
                    Upload(g, g.IsHydro ? scene.Pump! : scene.Uploads[g.Mac]); sentGroups++;
                }
                recoveryProfile = System.Text.Json.JsonSerializer.Deserialize<Profile>(System.Text.Json.JsonSerializer.Serialize(profile));
                sceneTiming = new(scene.Frames, scene.Interval);
                Status =L10n.F((FormattableString)$"RGB sent: {scene.Frames} frames, {scene.Frames / profile.PeriodSeconds:F1} FPS. Awaiting device confirmation.");
            }
            catch (Exception e)
            {
                CloseHandles();
                Status = L10n.F((FormattableString)$"Groups sent: {sentGroups}/{selected.Length}. {e.Message}");
                throw new IOException(Status, e);
            }
            return Status;
        }
    }
    public void QueueDirect(string mac, byte[] frame)
    {
        lock (gate)
        {
            if (identification != null) return;
            var group = Groups.SingleOrDefault(g => g.Mac == mac);
            if (group == null || !group.Supported || frame.Length != group.LedCount * 3 || tx == null) return;
            if (lastFrames.TryGetValue(mac, out var previous) && previous.SequenceEqual(frame)) return;
            pending[mac] = (byte[])frame.Clone(); lastFrames[mac] = pending[mac]; desired.Remove(mac); recoveryProfile = null;
        }
    }
    void EnsureNotIdentifying()
    {
        if (identification != null) throw new InvalidOperationException(L10n.T("Wait for group identification to finish."));
    }
    public void Identify(string mac)
    {
        lock (gate)
        {
            EnsureNotIdentifying();
            if (tx == null || VendorRunning()) throw new IOException(L10n.T("Connect USB and close L-Connect before identifying a group."));
            if (pending.Count != 0 || !desired.TryGetValue(mac, out var previous))
                throw new IOException(L10n.T("Apply a gradient to this group first so it can be restored after identification."));
            Poll();
            var group = Groups.SingleOrDefault(g => g.Mac == mac) ?? throw new IOException(L10n.T("Selected group not found."));
            ValidateTarget(group);
            var marker = new RgbUpload([Identification.Frame(group.Fans, group.Model?.LedsPerFan ?? 0)], 800000);
            identification = new(mac, master, previous, marker, long.MaxValue);
            try
            {
                Upload(group, marker);
                identification = identification with { RestoreAt = Environment.TickCount64 + Identification.DurationMilliseconds };
                Status = L10n.F((FormattableString)$"Identify {mac}: 1 red, 2 green, 3 blue, 4 yellow. Restores in 7 seconds.");
            }
            catch { CloseHandles(); throw; }
        }
    }
    void RestoreIdentification()
    {
        var session = identification;
        if (session == null) return;
        if (tx == null || master != session.Master) throw new IOException(L10n.T("Connection changed; restoration waits for reconnection."));
        var group = Groups.SingleOrDefault(g => g.Mac == session.Mac) ?? throw new IOException(L10n.T("Group disappeared during identification."));
        Upload(group, session.Previous);
        identification = null;
        Status = L10n.F((FormattableString)$"Identification {session.Mac} finished. Previous RGB cycle sent; awaiting confirmation.");
    }
    async Task Work()
    {
        int reconnects = 0;
        while (!stop.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(awaitingConfirmation ? 50 : 200, stop.Token);
                lock (gate)
                {
                    if (tx == null)
                    {
                        if (++reconnects % 25 == 0 && reconnects <= 125 && !VendorRunning())
                        {
                            Connect();
                            if (recoveryProfile != null && recoveryProfile.Fans.Where(f => f.Enabled).All(f => Groups.Any(g => g.Mac == f.Group && g.Supported && f.Fan < g.Fans)))
                            {
                                // A missing pump must not block fan recovery.
                                if (includePumpTiming && PumpRouting.Decide(Groups, false).Route != PumpRoute.Radio) includePumpTiming = false;
                                Apply(recoveryProfile);
                            }
                        }
                        continue;
                    }
                    reconnects = 0;
                    if (Environment.TickCount64 - lastVendorCheck >= 1000)
                    {
                        lastVendorCheck = Environment.TickCount64;
                        if (VendorRunning()) throw new IOException(L10n.T("L-Connect 3 started; USB released to avoid conflicts."));
                    }
                    if (active && PollDue(Environment.TickCount64, lastHeartbeat, lastPoll, awaitingConfirmation))
                    {
                        if (VendorRunning()) throw new IOException(L10n.T("L-Connect 3 started; USB released to avoid conflicts."));
                        Heartbeat(); // keeps its own 0.8 s interval
                        // One or two incomplete replies in a row are interference, not a disconnect: the link and uploaded cycles are kept.
                        bool polled = true;
                        try { Poll(); pollFailures = 0; }
                        catch (PollException e) when (++pollFailures < 3) { polled = false; Log.Write(L10n.F((FormattableString)$"{e.Message} Poll {pollFailures}/3; keeping connection.")); }
                        if (polled) RecoverRadioConflicts(waitForStable: false);
                        if (polled && desired.Count > 0 && identification == null)
                        {
                            int confirmed = desired.Count(d => Groups.Any(g => g.Mac == d.Key && !g.MotherboardSync && g.Effect.SequenceEqual(d.Value.Effect)));
                            string nextStatus = L10n.F((FormattableString)$"RGB: groups confirmed {confirmed}/{desired.Count}. ") + (confirmed == desired.Count ? L10n.T("Check visual synchronization on the fans.") : L10n.T("Awaiting device confirmation."));
                            if (Status != nextStatus) { Status = nextStatus; Log.Write(nextStatus); }
                            if (confirmed < desired.Count) Resync();
                            awaitingConfirmation = AwaitingConfirmation(desired, Groups, watch);
                        }
                    }
                    if (sceneTiming != null && pending.Count == 0 && Environment.TickCount64 - lastClock >= 3000) SampleClock();
                    SendFanTargets();
                    SendPumpTarget();
                    if (identification != null && Environment.TickCount64 >= identification.RestoreAt) RestoreIdentification();
                    if (pending.Count > 0 && Environment.TickCount64 >= nextLive)
                    {
                        if (VendorRunning()) throw new IOException(L10n.T("L-Connect 3 started; transfer stopped."));
                        var item = pending.FirstOrDefault(p => p.Key != lastLive);
                        if (item.Key == null) item = pending.First();
                        pending.Remove(item.Key); lastLive = item.Key;
                        var group = Groups.SingleOrDefault(g => g.Mac == item.Key);
                        if (group != null) { Upload(group, new RgbUpload([item.Value], 800000)); Status = L10n.T("OpenRGB: frame sent (5 FPS total limit)."); }
                        nextLive = Environment.TickCount64 + 200;
                    }
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception e)
            {
                lock (gate) { CloseHandles(); Status = L10n.T("Connection lost: ") + e.Message; }
                if (Environment.TickCount64 - lastError > 5000) { Log.Error(e); lastError = Environment.TickCount64; }
            }
        }
    }
    void CloseHandles() { tx?.Dispose(); rx?.Dispose(); tx = null; rx = null; active = false; awaitingConfirmation = false; pumpTheme = null; watch.Clear(); pollFailures = 0; pending.Clear(); desired.Clear(); lastFrames.Clear(); identification = null; }
    public void Dispose()
    {
        lock (gate)
        {
            if (identification != null)
                try { RestoreIdentification(); } catch (Exception e) { Log.Error(e); }
        }
        stop.Cancel();
        worker?.GetAwaiter().GetResult();
        lock (gate) CloseHandles();
        radioDelay?.Dispose();
        stop.Dispose();
    }
}
