using System.Diagnostics;
using System.Text.Json;

namespace WirelessCanvas;

/// <summary>One LCD screen: panel preparation, JPEG frame, brightness. Errors are exceptions caught by the screen thread.</summary>
internal interface IScreenDevice : IDisposable
{
    ScreenSpec Spec { get; }
    string? Firmware { get; }
    void SendFrame(byte[] jpeg);
    void SetBrightness(int percent);
}

/// <summary>Header timestamp: milliseconds since open, strictly increasing (upstream PacketBuilder).</summary>
internal sealed class LcdStamp
{
    readonly Stopwatch clock = Stopwatch.StartNew();
    uint last;
    public uint Next() => last = Math.Max(last + 1, (uint)Math.Min(uint.MaxValue - 1L, clock.ElapsedMilliseconds));
}

/// <summary>
/// LCD-SL Wireless fan (1CBE:0005) over its own USB, as upstream slv3_lcd.rs: on first contact a frame rate
/// of 120 and GetVer, then each frame is one 102,400-byte block followed by a reply read. Replies are optional.
/// </summary>
internal sealed class FanScreen : IScreenDevice
{
    readonly Func<IHydroLink> open;
    readonly Action? beforeTraffic;
    readonly LcdStamp stamp = new();
    IHydroLink? link;
    public string Serial { get; }
    public ScreenSpec Spec => ScreenSpec.Fan;
    public string? Firmware { get; private set; }
    /// <param name="beforeTraffic">upstream before frames and brightness, turns on L-Wireless video mode (the same commands as starting RGB).</param>
    public FanScreen(string serial, Func<IHydroLink> open, Action? beforeTraffic = null) { Serial = serial; this.open = open; this.beforeTraffic = beforeTraffic; }
    IHydroLink Ready()
    {
        if (link != null) return link;
        var opened = open();
        try
        {
            opened.Write(LcdProtocol.FanHeader(0x0F, stamp.Next(), [LcdProtocol.FanFrameRate])); _ = opened.Read(1000);
            opened.Write(LcdProtocol.FanHeader(0x0A, stamp.Next(), [])); Firmware = LcdProtocol.Firmware(opened.Read(1000));
            Log.Write(L10n.F((FormattableString)$"LCD {Serial}: screen prepared, firmware {Firmware ?? L10n.T("no reply")}"));
        }
        catch { opened.Dispose(); throw; }
        return link = opened;
    }
    void Exchange(byte[] packet)
    {
        var usb = Ready(); beforeTraffic?.Invoke();
        try { usb.Write(packet); _ = usb.Read(1000); }
        catch { Drop(); throw; }
    }
    public void SendFrame(byte[] jpeg) => Exchange(LcdProtocol.FanFrame(jpeg, stamp.Next()));
    public void SetBrightness(int percent) => Exchange(LcdProtocol.FanHeader(0x0E, stamp.Next(), [LcdProtocol.FanBrightness(percent)]));
    void Drop() { link?.Dispose(); link = null; }
    public void Dispose() => Drop();
}

/// <summary>
/// HydroShift II pump screen (1CBE:A021) on the same USB as the pump RGB (<see cref="HydroController.Exchange"/>).
/// Preparation as upstream h2_lcd.rs: StopPlay, GetVer, frame rate 30, SyncClock, StopClock. A frame is a header + JPEG;
/// if the panel buffer holds more than 3, wait with QueryBlock until it drops to 2. Preparation repeats after every
/// USB reopen (including after an RGB upload over USB).
/// </summary>
internal sealed class PumpScreen(HydroController pump) : IScreenDevice
{
    int generation = -1;
    public ScreenSpec Spec => ScreenSpec.Pump;
    public string? Firmware { get; private set; }
    T Exchange<T>(Func<IHydroLink, Func<uint>, T> body)
    {
        if (!pump.Connected) pump.Connect();
        return pump.Exchange((link, stamp) =>
        {
            if (generation != pump.Generation) { Prepare(link, stamp); generation = pump.Generation; }
            return body(link, stamp);
        });
    }
    void Prepare(IHydroLink link, Func<uint> stamp)
    {
        link.Write(HydroProtocol.Command(0x7B, stamp(), [])); _ = link.Read(100);
        Thread.Sleep(150);
        link.Write(HydroProtocol.Command(0x0A, stamp(), [])); Firmware = LcdProtocol.Firmware(link.Read(1000));
        link.Write(HydroProtocol.Command(0x0F, stamp(), [LcdProtocol.PumpFrameRate])); _ = link.Read(500);
        // The overlay layer (the pump's built-in theme, L-Connect leftovers) is drawn over our JPEG.
        // Hide overlay frames and clear the transparent layer twice, in the order upstream hs2_oled do_init.
        link.Write(HydroProtocol.Command(0x68, stamp(), new byte[32])); _ = link.Read(500);
        link.Write(HydroProtocol.Command(0x33, stamp(), LcdProtocol.Clock(DateTime.Now))); _ = link.Read(500);
        link.Write(HydroProtocol.Command(0x34, stamp(), [0])); _ = link.Read(500);
        for (int i = 0; i < 2; i++) { link.Write(HydroProtocol.Command(0x67, stamp(), [])); _ = link.Read(500); }
        // After a cold start, H2 keeps the built-in theme PNG above the JPEG.
        // L-Connect H2Controller.ClearPngLayer replaces it with a transparent one PNG 480×480.
        link.Write(LcdProtocol.PumpClearOverlay(stamp()));
        var overlayReply = link.Read(1000);
        if (overlayReply.Length == 0 || overlayReply[0] != 0x66)
            throw new IOException(L10n.T("Pump did not acknowledge PNG clearing; screen setup will be retried."));
        Log.Write(L10n.T("Pump LCD: transparent PNG uploaded, 0x66 reply received"));
        Log.Write(L10n.F((FormattableString)$"Pump LCD: screen prepared, firmware {Firmware ?? L10n.T("no reply")}"));
    }
    public void SendFrame(byte[] jpeg) => Exchange((link, stamp) =>
    {
        link.Write(LcdProtocol.PumpFrame(jpeg, stamp()));
        int? level = LcdProtocol.BufferLevel(link.Read(2000));
        // upstream wait_buffer: poll every 50 ms, but for no more than ~3 s, so the pump USB is not held.
        for (int i = 0; level > 3 && i < 60; i++)
        {
            Thread.Sleep(50);
            link.Write(HydroProtocol.Command(0x7A, stamp(), [])); level = LcdProtocol.BufferLevel(link.Read(500));
        }
        return 0;
    });
    public void SetBrightness(int percent) => Exchange((link, stamp) => { link.Write(HydroProtocol.Command(0x0E, stamp(), [(byte)Math.Clamp(percent, 0, 100)])); return link.Read(500); });
    public void Dispose() { }
}

/// <summary>Found screens: the pump and LCD fans on WinUSB. The serial number is part of the Windows device path.</summary>
public static class ScreenInventory
{
    public sealed record Found(ScreenKind Kind, string Serial, string Path, string Service);
    public static List<Found> Scan() => UsbInventory.Scan()
        .Where(d => UsbInventory.IsLcd(d.Id) && d.Paths.Length > 0 && d.Service.Equals("WINUSB", StringComparison.OrdinalIgnoreCase))
        .Select(d => new Found(d.Id.Contains(LcdProtocol.FanDeviceId, StringComparison.OrdinalIgnoreCase) ? ScreenKind.Fan : ScreenKind.Pump, d.Instance, d.Paths[0], d.Service))
        .OrderBy(f => f.Kind).ThenBy(f => f.Serial, StringComparer.Ordinal).ToList();

    /// <summary>
    /// Read-only diagnostics: each screen gets one GetVer version request and the reply is saved.
    /// No frames, brightness, frame rate, clock or stop commands are sent.
    /// </summary>
    public static string Probe()
    {
        if (Controller.VendorRunning()) throw new IOException(L10n.T("Close L-Connect 3 before screen diagnostics."));
        var stamp = new LcdStamp();
        var report = new List<object>();
        foreach (var device in UsbInventory.Scan().Where(d => UsbInventory.IsLcd(d.Id)))
        {
            bool fan = device.Id.Contains(LcdProtocol.FanDeviceId, StringComparison.OrdinalIgnoreCase);
            var entry = new Dictionary<string, object?> { ["Kind"] = fan ? L10n.T("LCD fan") : L10n.T("Pump"), ["Id"] = device.Id, ["Serial"] = device.Instance, ["Service"] = device.Service, ["Interfaces"] = device.Paths.Length };
            if (device.Paths.Length == 0 || !device.Service.Equals("WINUSB", StringComparison.OrdinalIgnoreCase)) { entry["Result"] = L10n.T("skipped: no WinUSB interface"); report.Add(entry); continue; }
            try
            {
                using var link = new HydroLink(device.Paths[0]);
                link.Write(fan ? LcdProtocol.FanHeader(0x0A, stamp.Next(), []) : HydroProtocol.Command(0x0A, stamp.Next(), []));
                byte[] reply = link.Read(1000);
                entry["ReplyBytes"] = reply.Length; entry["ReplyHead"] = Convert.ToHexString(reply.AsSpan(0, Math.Min(48, reply.Length)));
                entry["Firmware"] = LcdProtocol.Firmware(reply); entry["Result"] = reply.Length > 0 ? L10n.T("reply received") : L10n.T("no reply within 1 s");
            }
            catch (Exception e) { entry["Result"] = L10n.T("error: ") + e.Message; }
            Log.Write("LCD probe: " + JsonSerializer.Serialize(entry));
            report.Add(entry);
        }
        return JsonSerializer.Serialize(new { Time = DateTimeOffset.Now, Note = L10n.T("Only GetVer 0x0A was sent"), Screens = report }, Profile.Json);
    }
}
