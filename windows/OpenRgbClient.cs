using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;

namespace WirelessCanvas;

/// <summary>Device mode; Raw is its description in protocol format, used to send UpdateMode back.</summary>
public sealed record OpenRgbMode(string Name, uint Flags, uint ColorMode, [property: System.Text.Json.Serialization.JsonIgnore] byte[] Raw)
{
    public bool PerLed => ColorMode == 1;
}

public sealed record OpenRgbZone(string Name, int Type, int LedCount, int Width, int Height, uint[]? Matrix);

public sealed record OpenRgbDevice(int Index, int Type, string Name, string Vendor, string Description, string Serial, string Location,
    OpenRgbMode[] Modes, int ActiveMode, OpenRgbZone[] Zones, int LedCount)
{
    public string Kind => Type switch { 1 => L10n.T("Memory"), 2 => L10n.T("Graphics card"), 0 => L10n.T("Motherboard"), _ => L10n.F((FormattableString)$"Type {Type}") };
    public string Key => $"{Name}|{Location}";
}

// OpenRGB SDK client (protocol 0–4): reads descriptions only, Direct/Custom mode and UpdateLEDs.
// Commands that save profiles, modes with parameters or restart detection are never sent.
public sealed class OpenRgbClient : IDisposable
{
    public const uint ClientProtocol = 4;
    const int MaxPacket = 4 * 1024 * 1024;
    readonly TcpClient tcp;
    readonly NetworkStream stream;
    public uint Protocol { get; }
    public bool DeviceListChanged { get; private set; }

    public OpenRgbClient(string host = "127.0.0.1", int port = 6742, int timeout = 3000)
    {
        tcp = new TcpClient { NoDelay = true, ReceiveTimeout = timeout, SendTimeout = timeout };
        try
        {
            if (!tcp.ConnectAsync(host, port).Wait(timeout)) throw new IOException(L10n.F((FormattableString)$"OpenRGB not responding at {host}:{port}. Start OpenRGB with the SDK server enabled."));
            stream = tcp.GetStream();
            Send(0, 40, BitConverter.GetBytes(ClientProtocol));
            var reply = Receive(0, 40);
            Protocol = reply.Length >= 4 ? Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(reply), ClientProtocol) : 0;
            Send(0, 50, [.. Encoding.UTF8.GetBytes("WirelessCanvas RGB"), 0]);
        }
        catch (AggregateException e) { tcp.Dispose(); throw new IOException(L10n.F((FormattableString)$"OpenRGB unavailable at {host}:{port}: {e.InnerException?.Message}"), e.InnerException); }
        catch { tcp.Dispose(); throw; }
    }

    public OpenRgbDevice[] Devices()
    {
        DeviceListChanged = false;
        Send(0, 0, []);
        var count = Receive(0, 0);
        if (count.Length < 4) throw new InvalidDataException(L10n.T("Invalid OpenRGB device count response."));
        int total = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(count), 256u);
        var result = new OpenRgbDevice[total];
        for (int i = 0; i < total; i++)
        {
            Send((uint)i, 1, BitConverter.GetBytes(Protocol));
            result[i] = Parse(i, Receive((uint)i, 1), Protocol);
        }
        return result;
    }

    public void SetCustomMode(int device) => Send((uint)device, 1100, []);

    /// <summary>Selects a specific mode (RGBCONTROLLER_UPDATEMODE) with its original parameters.</summary>
    public void SetMode(int device, int mode, OpenRgbMode description)
    {
        byte[] data = new byte[8 + description.Raw.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(data, (uint)data.Length);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(4), mode);
        description.Raw.CopyTo(data, 8);
        Send((uint)device, 1101, data);
    }

    public void UpdateLeds(int device, ReadOnlySpan<byte> rgb)
    {
        if (rgb.Length % 3 != 0 || rgb.Length / 3 > ushort.MaxValue) throw new ArgumentException(L10n.T("Invalid RGB frame."));
        int count = rgb.Length / 3;
        byte[] data = new byte[6 + count * 4];
        BinaryPrimitives.WriteUInt32LittleEndian(data, (uint)data.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(4), (ushort)count);
        for (int i = 0; i < count; i++) rgb.Slice(i * 3, 3).CopyTo(data.AsSpan(6 + i * 4));
        Send((uint)device, 1050, data);
    }

    void Send(uint device, uint kind, byte[] data)
    {
        byte[] packet = new byte[16 + data.Length];
        "ORGB"u8.CopyTo(packet);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4), device);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(8), kind);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(12), (uint)data.Length);
        data.CopyTo(packet, 16);
        stream.Write(packet);
    }

    byte[] Receive(uint device, uint kind)
    {
        for (int skipped = 0; skipped < 64; skipped++)
        {
            byte[] header = new byte[16];
            stream.ReadExactly(header);
            if (!header.AsSpan(0, 4).SequenceEqual("ORGB"u8)) throw new InvalidDataException(L10n.T("Invalid OpenRGB response signature."));
            uint index = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4));
            uint id = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8));
            uint length = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(12));
            if (length > MaxPacket) throw new InvalidDataException(L10n.T("OpenRGB response too large."));
            byte[] data = new byte[length]; stream.ReadExactly(data);
            if (id == 100) { DeviceListChanged = true; continue; }
            if (id == kind && index == device) return data;
        }
        throw new InvalidDataException(L10n.T("Expected OpenRGB response not received."));
    }

    public static OpenRgbDevice Parse(int index, byte[] data, uint protocol)
    {
        using var r = new BinaryReader(new MemoryStream(data), Encoding.UTF8);
        if (r.ReadUInt32() != data.Length) throw new InvalidDataException(L10n.T("Invalid OpenRGB device description length."));
        int type = r.ReadInt32();
        string name = Text(r), vendor = protocol >= 1 ? Text(r) : "", description = Text(r);
        _ = Text(r); string serial = Text(r), location = Text(r);
        int modeCount = r.ReadUInt16(); int active = r.ReadInt32();
        var modes = new OpenRgbMode[modeCount];
        for (int m = 0; m < modeCount; m++)
        {
            int start = (int)r.BaseStream.Position;
            string modeName = Text(r);
            r.ReadInt32(); uint flags = r.ReadUInt32(); r.ReadUInt32(); r.ReadUInt32(); // value, flags, speed min/max
            if (protocol >= 3) { r.ReadUInt32(); r.ReadUInt32(); }               // brightness min/max
            r.ReadUInt32(); r.ReadUInt32(); r.ReadUInt32();                      // colors min/max, speed
            if (protocol >= 3) r.ReadUInt32();                                   // brightness
            r.ReadUInt32(); uint colorMode = r.ReadUInt32();                     // direction, color mode
            Skip(r, r.ReadUInt16() * 4);
            modes[m] = new(modeName, flags, colorMode, data[start..(int)r.BaseStream.Position]);
        }
        int zoneCount = r.ReadUInt16();
        var zones = new OpenRgbZone[zoneCount];
        for (int z = 0; z < zoneCount; z++)
        {
            string zoneName = Text(r); int zoneType = r.ReadInt32();
            r.ReadUInt32(); r.ReadUInt32(); int leds = (int)r.ReadUInt32();
            int matrixBytes = r.ReadUInt16(); int width = 0, height = 0; uint[]? matrix = null;
            if (matrixBytes > 0)
            {
                height = (int)r.ReadUInt32(); width = (int)r.ReadUInt32();
                if (height < 0 || width < 0 || (long)height * width > 4096 || matrixBytes != 8 + height * width * 4) throw new InvalidDataException(L10n.T("Invalid OpenRGB zone matrix."));
                matrix = new uint[height * width];
                for (int i = 0; i < matrix.Length; i++) matrix[i] = r.ReadUInt32();
            }
            if (protocol >= 4)
                for (int s = r.ReadUInt16(); s > 0; s--) { Text(r); r.ReadInt32(); r.ReadUInt32(); r.ReadUInt32(); }
            zones[z] = new(zoneName, zoneType, leds, width, height, matrix);
        }
        int ledCount = r.ReadUInt16();
        for (int i = 0; i < ledCount; i++) { Text(r); r.ReadUInt32(); }
        return new(index, type, name, vendor, description, serial, location, modes, active, zones, ledCount);
    }

    static string Text(BinaryReader r)
    {
        int length = r.ReadUInt16();
        var bytes = r.ReadBytes(length);
        if (bytes.Length != length) throw new EndOfStreamException();
        return Encoding.UTF8.GetString(bytes).TrimEnd('\0');
    }

    static void Skip(BinaryReader r, int bytes)
    {
        if (r.ReadBytes(bytes).Length != bytes) throw new EndOfStreamException();
    }

    public void Dispose() { stream?.Dispose(); tcp.Dispose(); }
}
