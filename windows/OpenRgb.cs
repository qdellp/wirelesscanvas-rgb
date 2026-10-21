using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace WirelessCanvas;

public sealed class OpenRgbServer : IDisposable
{
    readonly TcpListener listener;
    readonly CancellationTokenSource stop = new();
    readonly Task worker;
    readonly WirelessGroup[] groups;
    readonly Action<string, byte[]> receive;
    readonly object gate = new();
    readonly Dictionary<string, byte[]> colors;
    readonly List<Task> clients = [];
    public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;
    public OpenRgbServer(WirelessGroup[] groups, Action<string, byte[]> receive, int port = 6743)
    {
        this.groups = groups.Where(g => g.Supported).ToArray(); this.receive = receive;
        colors = this.groups.ToDictionary(g => g.Mac, g => new byte[g.LedCount * 3]);
        listener = new TcpListener(IPAddress.Loopback, port); listener.Start(4); worker = Task.Run(Accept);
    }
    async Task Accept()
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                TcpClient client = await listener.AcceptTcpClientAsync(stop.Token);
                clients.RemoveAll(t => t.IsCompleted);
                if (clients.Count >= 4) { client.Dispose(); continue; }
                clients.Add(Handle(client));
            }
        }
        catch (OperationCanceledException) { }
        catch (SocketException) when (stop.IsCancellationRequested) { }
    }
    async Task Handle(TcpClient client)
    {
        using (client)
        {
            uint version = 0;
            try
            {
                var stream = client.GetStream();
                while (!stop.IsCancellationRequested)
                {
                    byte[] header = new byte[16];
                    int first = await stream.ReadAsync(header.AsMemory(0, 1), stop.Token);
                    if (first == 0) return;
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
                    deadline.CancelAfter(TimeSpan.FromSeconds(5));
                    await stream.ReadExactlyAsync(header.AsMemory(1), deadline.Token);
                    if (!header.AsSpan(0, 4).SequenceEqual("ORGB"u8)) throw new InvalidDataException(L10n.T("Invalid OpenRGB signature."));
                    uint index = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4));
                    uint kind = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8));
                    uint length = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(12));
                    if (length > 65536) throw new InvalidDataException(L10n.T("OpenRGB packet exceeds the size limit."));
                    byte[] data = new byte[(int)length]; await stream.ReadExactlyAsync(data, deadline.Token);
                    byte[]? response = null;
                    switch (kind)
                    {
                        case 40:
                            if (data.Length != 4) throw new InvalidDataException(L10n.T("Invalid SDK version."));
                            version = Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(data), 4); response = BitConverter.GetBytes(4u); break;
                        case 0:
                            if (data.Length != 0) throw new InvalidDataException();
                            response = BitConverter.GetBytes((uint)groups.Length); break;
                        case 1:
                            if (index >= groups.Length || data.Length is not (0 or 4)) throw new InvalidDataException();
                            uint requested = data.Length == 4 ? Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(data), version) : version;
                            lock (gate) response = Describe(groups[index], colors[groups[index].Mac], requested);
                            break;
                        case 50:
                            if (data.Length > 256 || data.Length == 0 || data[^1] != 0) throw new InvalidDataException();
                            break;
                        case 1050: case 1051: case 1052:
                            if (index >= groups.Length) throw new InvalidDataException();
                            Update(groups[index], kind, data); break;
                        case 1100:
                            if (index >= groups.Length || data.Length != 0) throw new InvalidDataException();
                            break;
                        case 1101: case 1102:
                            if (index >= groups.Length) throw new InvalidDataException();
                            ValidateDirectMode(data, version); break;
                        case 150: case 200:
                            response = [6, 0, 0, 0, 0, 0]; break;
                    }
                    if (response != null)
                    {
                        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(12), (uint)response.Length);
                        await stream.WriteAsync(header, deadline.Token); await stream.WriteAsync(response, deadline.Token);
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (IOException) { }
            catch (Exception e) { Log.Error(e); }
        }
    }
    static void ValidateDirectMode(byte[] data, uint version)
    {
        using var reader = new BinaryReader(new MemoryStream(data));
        if (reader.ReadUInt32() != data.Length || reader.ReadUInt32() != 0) throw new InvalidDataException();
        int nameSize = reader.ReadUInt16();
        var name = reader.ReadBytes(nameSize);
        if (name.Length != nameSize || nameSize == 0 || name[^1] != 0 || Encoding.UTF8.GetString(name[..^1]) != "Direct") throw new InvalidDataException();
        for (int i = 0; i < (version >= 3 ? 12 : 9); i++) reader.ReadUInt32();
        if (reader.ReadUInt16() != 0 || reader.BaseStream.Position != data.Length) throw new InvalidDataException();
    }
    void Update(WirelessGroup group, uint kind, byte[] data)
    {
        byte[] next;
        lock (gate)
        {
            next = (byte[])colors[group.Mac].Clone();
            using var reader = new BinaryReader(new MemoryStream(data));
            if (kind == 1052)
            {
                uint led = reader.ReadUInt32(); if (led >= group.LedCount || data.Length != 8) throw new InvalidDataException();
                var color = reader.ReadBytes(4); Array.Copy(color, 0, next, (int)led * 3, 3);
            }
            else
            {
                if (reader.ReadUInt32() != data.Length) throw new InvalidDataException(L10n.T("Invalid RGB packet length."));
                int offset = 0, expected = group.LedCount;
                if (kind == 1051)
                {
                    uint zone = reader.ReadUInt32(); if (zone >= group.Fans) throw new InvalidDataException();
                    expected = group.Model!.LedsPerFan; offset = (int)zone * expected;
                }
                int count = reader.ReadUInt16();
                if (count != expected || data.Length - reader.BaseStream.Position != count * 4) throw new InvalidDataException(L10n.T("Invalid LED count."));
                for (int i = 0; i < count; i++) { var c = reader.ReadBytes(4); c.AsSpan(0, 3).CopyTo(next.AsSpan((offset + i) * 3)); }
            }
            for (int i = 0; i < next.Length; i += 3)
            {
                int sum = next[i] + next[i + 1] + next[i + 2];
                if (sum > 600) for (int ch = 0; ch < 3; ch++) next[i + ch] = (byte)(next[i + ch] * 600 / sum);
            }
            colors[group.Mac] = next;
        }
        receive(group.Mac, next);
    }
    static void String(BinaryWriter w, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value); w.Write((ushort)(bytes.Length + 1)); w.Write(bytes); w.Write((byte)0);
    }
    public static byte[] Describe(WirelessGroup group, byte[] colors, uint version)
    {
        using var stream = new MemoryStream(); using var w = new BinaryWriter(stream);
        w.Write(0u); w.Write(4u); String(w, "UNI FAN " + (group.Model is { } model && model != FanModel.SlV3 ? model.Name : "SL Wireless") + " " + group.Mac[^4..]); // SL V3 keeps its old name: OpenRGB profiles refer to it
        if (version >= 1) String(w, "Lian Li");
        String(w, "WirelessCanvas RGB lighting bridge"); String(w, "1.0"); String(w, group.Mac); String(w, "USB: L-Wireless " + group.Mac);
        w.Write((ushort)1); w.Write(0);
        String(w, "Direct"); w.Write(0u); w.Write(32u); w.Write(0u); w.Write(0u);
        if (version >= 3) { w.Write(0u); w.Write(0u); }
        w.Write(0u); w.Write(0u); w.Write(0u);
        if (version >= 3) w.Write(0u);
        w.Write(0u); w.Write(1u); w.Write((ushort)0);
        uint perFan = (uint)(group.Model?.LedsPerFan ?? 0);
        w.Write((ushort)group.Fans);
        for (int fan = 0; fan < group.Fans; fan++)
        {
            String(w, $"Fan {fan + 1}"); w.Write(1u); w.Write(perFan); w.Write(perFan); w.Write(perFan); w.Write((ushort)0);
            if (version >= 4) w.Write((ushort)0);
        }
        w.Write((ushort)group.LedCount);
        for (int led = 0; led < group.LedCount; led++) { String(w, $"Fan {led / perFan + 1} LED {led % perFan + 1}"); w.Write((uint)led); }
        w.Write((ushort)group.LedCount);
        for (int i = 0; i < group.LedCount; i++) { w.Write(colors, i * 3, 3); w.Write((byte)0); }
        byte[] result = stream.ToArray(); BinaryPrimitives.WriteUInt32LittleEndian(result, (uint)result.Length); return result;
    }
    public void Dispose()
    {
        stop.Cancel(); listener.Stop(); worker.GetAwaiter().GetResult(); Task.WhenAll(clients).GetAwaiter().GetResult(); stop.Dispose();
    }
}
