using System.Buffers.Binary;
using System.Text;

namespace WirelessCanvas;

public enum ScreenKind { Pump, Fan }

/// <summary>Screen size and limits (upstream screen.rs): the pump is round 480×480, the LCD fan 400×400.</summary>
public sealed record ScreenSpec(ScreenKind Kind, int Size, int MaxJpeg, int Quality, bool Round)
{
    public static readonly ScreenSpec Pump = new(ScreenKind.Pump, 480, LcdProtocol.PumpMaxJpeg, 85, true);
    public static readonly ScreenSpec Fan = new(ScreenKind.Fan, 400, LcdProtocol.FanMaxJpeg, 90, false);
}

/// <summary>
/// Lian Li LCD protocol (VID 1CBE). The 512-byte header is DES-CBC encrypted with the key slv3tuzx.
/// SL LCD fans (PID 0005) use the legacy format: 504 bytes of plaintext → 512 bytes of ciphertext
/// (upstream PacketBuilder::build). The pump (PID A021) uses the WinUSB format from <see cref="HydroProtocol.Command"/>.
/// </summary>
public static class LcdProtocol
{
    public const string FanDeviceId = "VID_1CBE&PID_0005";
    /// <summary>A fan frame is always one 102,400-byte block: header, JPEG and zeros (upstream slv3_lcd.rs).</summary>
    public const int FanPacket = 102_400, FanMaxJpeg = FanPacket - 512, PumpMaxJpeg = 153_600;
    public const byte FanFrameRate = 120, PumpFrameRate = 30;
    static readonly Lazy<byte[]> EmptyPumpOverlay = new(() =>
    {
        using var bitmap = new Bitmap(480, 480, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var stream = new MemoryStream();
        bitmap.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
        return stream.ToArray();
    });

    /// <summary>Empty transparent H2 top layer: the same PushImage(PNG) as L-Connect ClearPngLayer.</summary>
    public static byte[] PumpClearOverlay(uint timestamp)
    {
        var png = EmptyPumpOverlay.Value;
        byte[] packet = new byte[512 + png.Length];
        HydroProtocol.Command(0x66, timestamp, Size(png.Length)).CopyTo(packet, 0);
        png.CopyTo(packet, 512);
        return packet;
    }

    /// <summary>
    /// Fan header. Only GetVer 0x0A, brightness 0x0E, frame rate 0x0F and a JPEG frame are allowed 0x65.
    /// Reboot 0x0B, the boot image 0x28 and the switch to desktop mode 0x96 are never built.
    /// </summary>
    public static byte[] FanHeader(byte command, uint timestamp, ReadOnlySpan<byte> parameters)
    {
        bool valid = command switch
        {
            0x0A => parameters.Length == 0,
            0x0E => parameters.Length == 1 && parameters[0] <= 100,
            0x0F => parameters.Length == 1 && parameters[0] is >= 1 and <= 120,
            0x65 => parameters.Length == 4 && BinaryPrimitives.ReadUInt32BigEndian(parameters) is >= 1 and <= FanMaxJpeg,
            _ => false,
        };
        if (!valid) throw new InvalidDataException(L10n.T("LCD fan command is outside the permitted protocol."));
        byte[] plain = HydroProtocol.Plain(504, command, timestamp); parameters.CopyTo(plain.AsSpan(8));
        byte[] header = HydroProtocol.Encrypt(plain);
        if (header.Length != 512) throw new InvalidOperationException(L10n.T("Invalid LCD header length."));
        return header;
    }
    static byte[] Size(int length) { byte[] size = new byte[4]; BinaryPrimitives.WriteUInt32BigEndian(size, (uint)length); return size; }

    /// <summary>Fan frame packet: 102,400 bytes, zero-padded.</summary>
    public static byte[] FanFrame(byte[] jpeg, uint timestamp)
    {
        if (jpeg.Length is < 1 or > FanMaxJpeg) throw new InvalidDataException(L10n.F((FormattableString)$"Fan JPEG is {jpeg.Length} bytes; limit is {FanMaxJpeg}."));
        byte[] packet = new byte[FanPacket];
        FanHeader(0x65, timestamp, Size(jpeg.Length)).CopyTo(packet, 0); jpeg.CopyTo(packet, 512);
        return packet;
    }
    /// <summary>Pump frame packet: header and JPEG without padding (upstream core.rs send_frame).</summary>
    public static byte[] PumpFrame(byte[] jpeg, uint timestamp)
    {
        if (jpeg.Length is < 1 or > PumpMaxJpeg) throw new InvalidDataException(L10n.F((FormattableString)$"Pump JPEG is {jpeg.Length} bytes; limit is {PumpMaxJpeg}."));
        byte[] packet = new byte[512 + jpeg.Length];
        HydroProtocol.Command(0x65, timestamp, Size(jpeg.Length)).CopyTo(packet, 0); jpeg.CopyTo(packet, 512);
        return packet;
    }
    /// <summary>SyncClock 0x33 parameters: year (BE), month, day, hours, minutes, seconds, mode (upstream: 2).</summary>
    public static byte[] Clock(DateTime now, byte mode = 2) => [(byte)(now.Year >> 8), (byte)now.Year, (byte)now.Month, (byte)now.Day, (byte)now.Hour, (byte)now.Minute, (byte)now.Second, mode];

    /// <summary>Fan brightness: SL LCD firmware expects a value from the table 0→0, 25→10, 50→30, 75→40, 100→100 (upstream slv3_brightness_lut).</summary>
    public static byte FanBrightness(int percent)
    {
        ReadOnlySpan<(int In, int Out)> anchors = [(0, 0), (25, 10), (50, 30), (75, 40), (100, 100)];
        int v = Math.Clamp(percent, 0, 100);
        for (int i = 0; i < anchors.Length; i++) if (anchors[i].In == v) return (byte)anchors[i].Out;
        int pos = 1; while (anchors[pos].In < v) pos++;
        var (lo, hi) = (anchors[pos - 1], anchors[pos]);
        int span = hi.In - lo.In, step = Math.Max(0, hi.Out - lo.Out);
        return (byte)(lo.Out + ((v - lo.In) * step + span / 2) / span);
    }

    /// <summary>Firmware version from the GetVer reply: code 0x0A, ASCII from byte 8 up to the null.</summary>
    public static string? Firmware(byte[] reply)
    {
        if (reply.Length <= 8 || reply[0] != 0x0A) return null;
        var name = reply.AsSpan(8, Math.Min(reply.Length, 40) - 8); int end = name.IndexOf((byte)0); if (end >= 0) name = name[..end];
        if (name.Length == 0 || name.ContainsAnyExceptInRange((byte)32, (byte)126)) return null;
        return Encoding.ASCII.GetString(name).Trim();
    }
    /// <summary>Pump buffer fill from the frame or QueryBlock reply (byte 8), otherwise null.</summary>
    public static int? BufferLevel(byte[] reply) => reply.Length > 8 && reply[0] is 0x65 or 0x7A ? reply[8] : null;
}
