using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace WirelessCanvas;

public record UsbDeviceInfo(string Id, string Instance, string Name, string Service, string[] InterfaceGuids, string[] Paths);

public static class UsbInventory
{
    public static readonly string[] AllowedIds = ["VID_0416&PID_8040", "VID_0416&PID_8041", "VID_1A86&PID_E304", "VID_1A86&PID_E305", HydroProtocol.DeviceId, LcdProtocol.FanDeviceId];
    /// <summary>Lian Li screens (VID 1CBE): 512-byte bulk transfers, frames up to 150 KB.</summary>
    public static bool IsLcd(string id) => id.Contains(HydroProtocol.DeviceId, StringComparison.OrdinalIgnoreCase) || id.Contains(LcdProtocol.FanDeviceId, StringComparison.OrdinalIgnoreCase);
    public static List<UsbDeviceInfo> Scan()
    {
        List<UsbDeviceInfo> found = [];
        using var root = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\USB");
        if (root == null) return found;
        foreach (var id in root.GetSubKeyNames().Where(id => AllowedIds.Any(a => id.StartsWith(a, StringComparison.OrdinalIgnoreCase))))
        {
            using var model = root.OpenSubKey(id);
            if (model == null) continue;
            foreach (var instance in model.GetSubKeyNames())
            {
                using var device = model.OpenSubKey(instance);
                using var parameters = device?.OpenSubKey("Device Parameters");
                string[] guids = (parameters?.GetValue("DeviceInterfaceGUIDs") ?? parameters?.GetValue("DeviceInterfaceGUID")) switch
                {
                    string[] list => list,
                    string single => [single],
                    _ => []
                };
                if (IsLcd(id))
                    guids = guids.Concat(new[] { "{a5dcbf10-6530-11d2-901f-00c04fb951ed}" }).Distinct().ToArray();
                var paths = guids.Where(g => Guid.TryParse(g, out _)).SelectMany(g => Interfaces(Guid.Parse(g)))
                    .Where(p => p.Contains(id, StringComparison.OrdinalIgnoreCase) && p.Contains("#" + instance + "#", StringComparison.OrdinalIgnoreCase)).Distinct().ToArray();
                found.Add(new(id, instance, device?.GetValue("FriendlyName")?.ToString() ?? device?.GetValue("DeviceDesc")?.ToString() ?? "L-Wireless", device?.GetValue("Service")?.ToString() ?? "", guids, paths));
            }
        }
        return found;
    }
    static IEnumerable<string> Interfaces(Guid guid)
    {
        var result = new List<string>();
        IntPtr set = Native.SetupDiGetClassDevs(ref guid, null, IntPtr.Zero, 0x12);
        if (set == new IntPtr(-1)) return result;
        try
        {
            for (uint index = 0; ; index++)
            {
                var data = new Native.InterfaceData { Size = Marshal.SizeOf<Native.InterfaceData>() };
                if (!Native.SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref guid, index, ref data))
                {
                    int error = Marshal.GetLastWin32Error();
                    if (error != 259) throw new Win32Exception(error);
                    break;
                }
                Native.SetupDiGetDeviceInterfaceDetail(set, ref data, IntPtr.Zero, 0, out uint size, IntPtr.Zero);
                if (size is < 8 or > 65536) continue;
                IntPtr detail = Marshal.AllocHGlobal((int)size);
                try
                {
                    Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                    if (Native.SetupDiGetDeviceInterfaceDetail(set, ref data, detail, size, out _, IntPtr.Zero))
                    {
                        string? path = Marshal.PtrToStringUni(detail + 4);
                        if (path != null) result.Add(path);
                    }
                }
                finally { Marshal.FreeHGlobal(detail); }
            }
        }
        finally { Native.SetupDiDestroyDeviceInfoList(set); }
        return result;
    }
}

public sealed class WinUsbTransport : IDisposable
{
    readonly SafeFileHandle file;
    IntPtr usb;
    readonly int packetSize;
    public string Path { get; }
    public WinUsbTransport(string path, int packetSize = 64, bool exclusive = false)
    {
        if (!UsbInventory.AllowedIds.Any(id => path.Contains(id, StringComparison.OrdinalIgnoreCase))) throw new InvalidOperationException(L10n.T("USB device is not permitted."));
        bool hydro = UsbInventory.IsLcd(path);
        if (packetSize != (hydro ? 512 : 64)) throw new InvalidDataException(L10n.T("Packet size does not match USB device family."));
        this.packetSize = packetSize;
        Path = path;
        file = Native.CreateFile(path, 0xC0000000, exclusive ? 0u : 3u, IntPtr.Zero, 3, 0x40000000, IntPtr.Zero);
        if (file.IsInvalid) { int error = Marshal.GetLastWin32Error(); file.Dispose(); throw new Win32Exception(error, L10n.T("Could not open Lian Li USB. Close L-Connect 3.")); }
        try
        {
            Check(Native.WinUsb_Initialize(file, out usb));
            Check(Native.WinUsb_QueryInterfaceSettings(usb, 0, out var descriptor));
            bool input = false, output = false;
            for (byte i = 0; i < descriptor.Endpoints; i++)
            {
                Check(Native.WinUsb_QueryPipe(usb, 0, i, out var pipe));
                input |= pipe.Id == 0x81 && (hydro ? pipe.Type == 2 : pipe.Type is 2 or 3);
                output |= pipe.Id == 0x01 && (hydro ? pipe.Type == 2 : pipe.Type is 2 or 3);
            }
            if (!input || !output) throw new InvalidDataException(L10n.T("Expected USB endpoints 0x81/0x01 not found."));
            SetTimeout(0x81, 100); SetTimeout(0x01, 500);
        }
        catch { Dispose(); throw; }
    }
    public void SetTimeout(byte pipe, uint milliseconds) => Check(Native.WinUsb_SetPipePolicy(usb, pipe, 3, 4, ref milliseconds));
    public void ClearEndpointHalts()
    {
        if (packetSize != 512) throw new InvalidOperationException(L10n.T("Endpoint clearing is allowed only for Lian Li screens."));
        Check(Native.WinUsb_ResetPipe(usb, 0x01)); Check(Native.WinUsb_ResetPipe(usb, 0x81));
    }
    static void Check(bool success) { if (!success) throw new Win32Exception(Marshal.GetLastWin32Error()); }
    public void Write(byte[] data)
    {
        if (data.Length != packetSize) throw new InvalidDataException(L10n.F((FormattableString)$"USB packet must contain {packetSize} bytes."));
        WriteFull(data);
    }
    public void WriteFull(byte[] data)
    {
        if (data.Length < packetSize || data.Length > (packetSize == 64 ? 64 : 512 + LcdProtocol.PumpMaxJpeg)) throw new InvalidDataException(L10n.T("Invalid USB transaction length."));
        Check(Native.WinUsb_WritePipe(usb, 0x01, data, (uint)data.Length, out uint count, IntPtr.Zero));
        if (count != data.Length) throw new IOException(L10n.F((FormattableString)$"Incomplete USB write: {count}/{data.Length}. Packet is not retried automatically."));
    }
    public byte[] Read(int timeout)
    {
        SetTimeout(0x81, (uint)timeout);
        byte[] data = new byte[packetSize];
        if (!Native.WinUsb_ReadPipe(usb, 0x81, data, (uint)packetSize, out uint count, IntPtr.Zero))
        {
            int error = Marshal.GetLastWin32Error();
            if (error is 121 or 1460) return [];
            throw new Win32Exception(error);
        }
        return data[..(int)count];
    }
    public void Flush() { for (int i = 0; i < 16; i++) if (Read(5).Length == 0) return; throw new IOException(L10n.T("USB channel is busy with another application.")); }
    public byte[] ReadAll(int maximum, int firstTimeout)
    {
        using var stream = new MemoryStream();
        while (stream.Length < maximum)
        {
            var block = Read(stream.Length == 0 ? firstTimeout : 10);
            if (block.Length == 0) break;
            if (stream.Length + block.Length > maximum) throw new InvalidDataException(L10n.T("Excess USB response data."));
            stream.Write(block);
        }
        return stream.ToArray();
    }
    public void Dispose() { if (usb != IntPtr.Zero) { Native.WinUsb_Free(usb); usb = IntPtr.Zero; } file.Dispose(); }
}

internal static class Native
{
    [StructLayout(LayoutKind.Sequential)] internal struct InterfaceData { public int Size; public Guid Guid; public uint Flags; public UIntPtr Reserved; }
    [StructLayout(LayoutKind.Sequential, Pack = 1)] internal struct UsbInterface { public byte Length, Type, Number, Alternate, Endpoints, Class, SubClass, Protocol, StringIndex; }
    [StructLayout(LayoutKind.Sequential)] internal struct Pipe { public int Type; public byte Id; public ushort PacketSize; public byte Interval; }
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "SetupDiGetClassDevsW")] internal static extern IntPtr SetupDiGetClassDevs(ref Guid guid, string? enumerator, IntPtr parent, uint flags);
    [DllImport("setupapi.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr device, ref Guid guid, uint index, ref InterfaceData data);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "SetupDiGetDeviceInterfaceDetailW")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref InterfaceData data, IntPtr detail, uint size, out uint required, IntPtr device);
    [DllImport("setupapi.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateFileW")] internal static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("winusb.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool WinUsb_Initialize(SafeFileHandle file, out IntPtr handle);
    [DllImport("winusb.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool WinUsb_Free(IntPtr handle);
    [DllImport("winusb.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool WinUsb_QueryInterfaceSettings(IntPtr handle, byte alternate, out UsbInterface descriptor);
    [DllImport("winusb.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool WinUsb_QueryPipe(IntPtr handle, byte alternate, byte index, out Pipe info);
    [DllImport("winusb.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool WinUsb_SetPipePolicy(IntPtr handle, byte pipe, uint policy, uint length, ref uint value);
    [DllImport("winusb.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool WinUsb_ResetPipe(IntPtr handle, byte pipe);
    [DllImport("winusb.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool WinUsb_WritePipe(IntPtr handle, byte pipe, byte[] data, uint length, out uint count, IntPtr overlapped);
    [DllImport("winusb.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool WinUsb_ReadPipe(IntPtr handle, byte pipe, byte[] data, uint length, out uint count, IntPtr overlapped);
}
