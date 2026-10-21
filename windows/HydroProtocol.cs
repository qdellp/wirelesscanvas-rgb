using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace WirelessCanvas;

public static class HydroProtocol
{
    public const string DeviceId = "VID_1CBE&PID_A021";
    public static byte[] Header(byte command, uint timestamp, int payloadLength = 0)
    {
        if (command is not (0x0A or 0x7B or 0x34 or 0xFA or 0xFC) || payloadLength is < 0 or > 12292 || (command != 0xFC && payloadLength != 0) || (command == 0xFC && payloadLength < 5))
            throw new InvalidDataException(L10n.T("HydroShift command is outside the permitted RGB protocol."));
        byte[] plain = Plain(500, command, timestamp);
        if (command == 0xFC) BinaryPrimitives.WriteUInt32BigEndian(plain.AsSpan(12), (uint)payloadLength);
        return Seal(plain);
    }
    /// <summary>
    /// Pump screen commands (upstream build_winusb): parameters from byte 8. Only allowed: version read,,
    /// stop display, clock, frame rate, brightness, buffer level, overlay clear (0x67, 0x68 “no frames”)
    /// JPEG-a frame, and a small PNG for the transparent layer (0x66, like L-Connect ClearPngLayer). Reboot, the
    /// desktop mode, the boot image and pump/PWM are not included.
    /// </summary>
    public static byte[] Command(byte command, uint timestamp, ReadOnlySpan<byte> parameters)
    {
        bool valid = command switch
        {
            0x0A or 0x7B or 0x7A or 0x67 => parameters.Length == 0,
            0x68 => parameters.Length == 32 && parameters.IndexOfAnyExcept((byte)0) < 0,
            0x34 => parameters.Length == 0 || parameters.SequenceEqual(new byte[1]),
            0x0E => parameters.Length == 1 && parameters[0] <= 100,
            0x0F => parameters.Length == 1 && parameters[0] is >= 1 and <= 120,
            0x33 => parameters.Length == 8,
            0x65 => parameters.Length == 4 && BinaryPrimitives.ReadUInt32BigEndian(parameters) is >= 1 and <= LcdProtocol.PumpMaxJpeg,
            0x66 => parameters.Length == 4 && BinaryPrimitives.ReadUInt32BigEndian(parameters) is >= 1 and <= 4096,
            _ => false,
        };
        if (!valid) throw new InvalidDataException(L10n.T("Pump screen command is outside the permitted protocol."));
        byte[] plain = Plain(500, command, timestamp); parameters.CopyTo(plain.AsSpan(8));
        return Seal(plain);
    }
    internal static byte[] Plain(int size, byte command, uint timestamp)
    {
        byte[] plain = new byte[size]; plain[0] = command; plain[2] = 0x1A; plain[3] = 0x6D;
        BinaryPrimitives.WriteUInt32LittleEndian(plain.AsSpan(4), timestamp);
        return plain;
    }
    internal static byte[] Encrypt(byte[] plain)
    {
        using var des = DES.Create(); des.Key = "slv3tuzx"u8.ToArray(); des.IV = "slv3tuzx"u8.ToArray();
        return des.EncryptCbc(plain, des.IV, PaddingMode.PKCS7);
    }
    static byte[] Seal(byte[] plain)
    {
        byte[] header = new byte[512]; Encrypt(plain).CopyTo(header, 0); header[510] = 0xA1; header[511] = 0x1A;
        return header;
    }
    public static string? Firmware(byte[] reply)
    {
        if (reply.Length < 40 || reply[0] != 0x0A) return null;
        var name = reply.AsSpan(8, 32); int end = name.IndexOf((byte)0); if (end >= 0) name = name[..end];
        if (name.Length == 0 || name.ContainsAnyExceptInRange((byte)32, (byte)126)) return null;
        return Encoding.ASCII.GetString(name);
    }
    public static byte[] Payload(RgbUpload upload)
    {
        if (upload.Leds != 24 || upload.Compressed.Length > 12288 || upload.Interval % 100 != 0 || upload.Interval / 100 is < 1 or > 255)
            throw new InvalidDataException(L10n.T("Pump requires 24 LEDs and an integer interval of 1–255 ticks."));
        byte[] payload = new byte[upload.Compressed.Length + 4]; upload.Compressed.CopyTo(payload, 0);
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(upload.Compressed.Length), (ushort)upload.Frames);
        payload[^2] = (byte)(upload.Interval / 100); payload[^1] = 24; return payload;
    }
    internal static byte[] ReadResponse(Func<int, byte[]> read, int timeout)
    {
        long until = Environment.TickCount64 + timeout;
        do
        {
            var data = read((int)Math.Max(1, until-Environment.TickCount64));
            if (data.Length > 0) return data;
            if (Environment.TickCount64 >= until) break;
            Thread.Sleep(1);
        } while (Environment.TickCount64 < until);
        return [];
    }
}

internal interface IHydroLink : IDisposable
{
    void Write(byte[] packet);
    byte[] Read(int timeout);
    void Reopen();
}

internal sealed class HydroLink : IHydroLink
{
    readonly string path;
    WinUsbTransport? usb;
    public HydroLink(string path)
    {
        this.path = path; usb = Open(path);
        try { Drain(); } catch { Dispose(); throw; }
    }
    static WinUsbTransport Open(string path)
    {
        var result = new WinUsbTransport(path, 512, true);
        try { result.SetTimeout(0x01, 2000); result.ClearEndpointHalts(); return result; } catch { result.Dispose(); throw; }
    }
    public void Write(byte[] packet) => (usb ?? throw new IOException(L10n.T("Pump USB is closed."))).WriteFull(packet);
    public byte[] Read(int timeout)
    {
        // HydroShift inserts a ZLP between replies. A successful ReadPipe with 0 bytes
        // is not a timeout: keep reading until a reply or the overall deadline.
        return HydroProtocol.ReadResponse(wait => (usb ?? throw new IOException(L10n.T("Pump USB is closed."))).Read(wait), timeout);
    }
    void Drain()
    {
        for (int i = 0; i < 32; i++) if (Read(10).Length == 0) return;
        throw new IOException(L10n.T("Pump USB is receiving continuous replies. Close other control software."));
    }
    public void Reopen() { usb?.Dispose(); usb = null; Thread.Sleep(100); usb = Open(path); Drain(); }
    public void Dispose() { usb?.Dispose(); usb = null; }
}

internal static class HydroTransfer
{
    public static string Send(IHydroLink link, RgbUpload upload, Func<uint> timestamp, Action<string>? log = null, Action<int>? pause = null, IReadOnlyCollection<string>? boundHydroMacs = null)
    {
        pause ??= Thread.Sleep;
        byte[] payload = HydroProtocol.Payload(upload);
        // Finish recovery even after a failed or partial RGB write. No reset/PWM.
        link.Write(HydroProtocol.Header(0x7B, timestamp())); var stopReply = link.Read(100);
        pause(150);
        link.Write(HydroProtocol.Header(0x34, timestamp())); var clockReply = link.Read(100);
        pause(150);
        link.Write(HydroProtocol.Header(0x0A, timestamp())); var wakeReply = link.Read(1000);
        if (HydroProtocol.Firmware(wakeReply) == null) throw new IOException(L10n.T("Pump did not confirm USB mode; RGB was not sent."));
        if (boundHydroMacs is { Count: > 0 })
        {
            // USB-cable does not rule out RF ownership of RGB. Checked after StopPlay,
            // when a parameter request cannot overlap the LCD stream.
            link.Write(HydroProtocol.Header(0xFA, timestamp())); var parameters = link.Read(1000);
            if (parameters.Length < 32 || parameters[0] != 0xFA) throw new IOException(L10n.T("Could not verify pump RF binding; RGB was not sent."));
            string mac = Convert.ToHexString(parameters.AsSpan(22, 6));
            if (boundHydroMacs.Contains(mac, StringComparer.OrdinalIgnoreCase))
                throw new IOException(L10n.T("Pump USB is connected, but L-Wireless controls its RGB. USB RGB skipped; binding unchanged."));
        }
        pause(150);
        log?.Invoke($"H2: preamble replies {stopReply.Length}/{clockReply.Length}/{wakeReply.Length}, payload {payload.Length}, {upload.Frames} frames / {upload.Interval/100} ticks");
        Exception? writeError = null;
        try { link.Write(HydroProtocol.Header(0xFC, timestamp(), payload.Length).Concat(payload).ToArray()); _ = link.Read(100); }
        catch (Exception e) { writeError = e; }
        // Measured on A021 FW 0006_0020: the MCU must finish processing
        // before reopen. Requests sent before this pause leave the channel without replies.
        pause(2500);
        link.Reopen();
        long started = Environment.TickCount64, until = started + 8000;
        long queryStarted = started;
        bool reopenedAgain = false;
        do
        {
            link.Write(HydroProtocol.Header(0x0A, checked((uint)(Environment.TickCount64-queryStarted+1))));
            byte[] response = link.Read(500);
            log?.Invoke($"H2: recovery reply {response.Length}, opcode {(response.Length > 0 ? response[0].ToString("X2") : "none")}");
            string? firmware = HydroProtocol.Firmware(response);
            if (firmware != null)
            {
                if (writeError != null) throw new IOException(L10n.T("Pump RGB write incomplete; USB recovered. Apply again."), writeError);
                return firmware;
            }
            // FW 0006_0020 does not always reply after an immediate reopen:
            // a later reopen restores the channel without writing again RGB.
            if (!reopenedAgain && Environment.TickCount64 - started >= 2500)
            {
                link.Reopen(); reopenedAgain = true; queryStarted = Environment.TickCount64;
            }
            pause(250);
        } while (Environment.TickCount64 < until);
        throw new IOException(L10n.T("No HydroShift reply after RGB upload. No packet retry; reconnect USB in the app."), writeError);
    }
}

public sealed class HydroController : IDisposable
{
    readonly object gate = new();
    IHydroLink? link;
    public HydroController() { }
    /// <summary>Self-tests only: a ready test link instead of USB.</summary>
    internal HydroController(IHydroLink testLink) { link = testLink; Generation = 1; test = true; }
    readonly bool test;
    uint stamp;
    readonly System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
    string? lastPayload;
    public bool Connected => link != null;
    public string Status { get; private set; } = L10n.T("Pump USB disconnected");
    uint Timestamp() => stamp = checked(Math.Max(stamp + 1, (uint)Math.Min(uint.MaxValue - 1L, clock.ElapsedMilliseconds)));
    public void Connect()
    {
        lock (gate)
        {
            if (link != null) return;
            if (test) throw new IOException(L10n.T("Self-test: real pump USB must not be opened."));
            if (Controller.VendorRunning()) throw new IOException(L10n.T("Close L-Connect 3 before connecting the pump."));
            var devices = UsbInventory.Scan().Where(d => d.Id.Equals(HydroProtocol.DeviceId, StringComparison.OrdinalIgnoreCase) && d.Service.Equals("WINUSB", StringComparison.OrdinalIgnoreCase) && d.Paths.Length > 0).ToArray();
            if (devices.Length != 1) throw new IOException(L10n.F((FormattableString)$"HydroShift II LCD-C: {devices.Length} WinUSB devices available, exactly one required. Drivers unchanged."));
            link = new HydroLink(devices[0].Paths[0]); lastPayload = null; Generation++;
            Status = L10n.T("HydroShift II LCD-C · USB open; RGB not sent yet");
        }
    }
    public string Probe()
    {
        lock (gate)
        {
            Connect(); string? firmware = null;
            for (int i = 0; i < 3; i++)
            {
                uint ts = Timestamp(); link!.Write(HydroProtocol.Header(0x0A, ts)); var response = link.Read(1000);
                Log.Write($"H2 probe {i}, ts={ts}, bytes={response.Length}, head={Convert.ToHexString(response.AsSpan(0, Math.Min(48, response.Length)))}");
                firmware = HydroProtocol.Firmware(response); Thread.Sleep(150);
            }
            Status = firmware == null ? L10n.T("HydroShift USB open; no GetVer reply yet") : "HydroShift USB: " + firmware;
            return Status;
        }
    }
    public string ProbeParameters()
    {
        lock (gate)
        {
            Connect();
            // Manual diagnostics only, with L-Connect closed: do not poll during the LCD stream.
            for (int i = 0; i < 2; i++)
            {
                link!.Write(HydroProtocol.Header(0xFA, Timestamp())); var reply = link.Read(1000);
                Log.Write($"H2 params: bytes={reply.Length}, head={Convert.ToHexString(reply.AsSpan(0, Math.Min(48, reply.Length)))}");
                if (reply.Length >= 32 && reply[0] == 0xFA)
                    return L10n.F((FormattableString)$"H2: link MAC {Convert.ToHexString(reply.AsSpan(22, 6))}; raw {Convert.ToHexString(reply.AsSpan(0, 32))}. A MAC alone does not confirm RF binding.");
                Thread.Sleep(150);
            }
            throw new IOException(L10n.T("No valid GetH2Params reply. Pump settings unchanged."));
        }
    }
    public string Apply(RgbUpload upload, IReadOnlyCollection<string>? boundHydroMacs = null)
    {
        lock (gate)
        {
            if (link == null || Controller.VendorRunning()) throw new IOException(L10n.T("Connect pump USB and close L-Connect 3."));
            byte[] payload = HydroProtocol.Payload(upload);
            string key = Convert.ToHexString(SHA256.HashData(payload));
            if (key == lastPayload) return Status = L10n.T("Pump: this RGB cycle is already loaded");
            try
            {
                string firmware;
                try { firmware = HydroTransfer.Send(link, upload, Timestamp, Log.Write, boundHydroMacs: boundHydroMacs); }
                finally { Generation++; }
                lastPayload = key;
                Status = L10n.F((FormattableString)$"Pump: {upload.Frames} frames, USB recovered ({firmware}); check lighting visually.");
                Log.Write(Status); return Status;
            }
            catch (Exception e) { link.Dispose(); link = null; lastPayload = null; Status = e.Message; Log.Error(e); throw; }
        }
    }
    /// <summary>
    /// Changes on every USB open and after an RGB upload (which reopens USB):
    /// the pump screen uses it to know the panel must be prepared again.
    /// </summary>
    public int Generation { get; private set; }
    /// <summary>
    /// Pump screen traffic uses the same USB as RGB, under a shared lock, so a frame
    /// never overlaps a ring upload or diagnostics.
    /// </summary>
    internal T Exchange<T>(Func<IHydroLink, Func<uint>, T> body)
    {
        lock (gate)
        {
            if (link == null) throw new IOException(L10n.T("Pump USB is not open."));
            try { return body(link, Timestamp); }
            catch (Exception e) when (e is IOException or System.ComponentModel.Win32Exception)
            {
                // Connection lost: the next access reopens USB.
                link.Dispose(); link = null; Generation++; Status = L10n.T("Pump USB: connection lost — ") + e.Message;
                throw;
            }
        }
    }
    public void Dispose() { lock (gate) { link?.Dispose(); link = null; } }
}
