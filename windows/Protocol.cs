using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace WirelessCanvas;

public record WirelessGroup(string Mac, string Master, byte Channel, byte Slot, int Fans, byte[] Types, byte[] Effect, bool MotherboardSync, byte Sequence, byte DeviceType = 0, int ListIndex = -1)
{
    /// <summary>RPM of each fan (12 bits, record bytes 28–35); on the pump, slot 3 is the pump motor.</summary>
    public int[] Rpm { get; init; } = new int[4];
    /// <summary>Current PWM of each slot 0–255 (bytes 36–39), confirming the speed command.</summary>
    public byte[] Pwm { get; init; } = new byte[4];
    /// <summary>Flag 0x20 of byte 28: the device sees the motherboard PWM line.</summary>
    public bool PwmLine { get; init; }
    /// <summary>HydroShift coolant temperature, °C (byte 27), otherwise null.</summary>
    public int? Coolant { get; init; }
    /// <summary>Device number among those bound to the master (1…), field 16 of the speed command, like next_slot_index in upstream.</summary>
    public int SlotIndex { get; init; }
    /// <summary>SL-INF chain connected on the right (fan count in the record ≥ 10): PWM and RPM slots are in reverse order.</summary>
    public bool RightAttach { get; init; }
    /// <summary>Fan model of a group from its type bytes; null for an unknown or mixed group, whose RGB and speed are left alone.</summary>
    public FanModel? Model => DeviceType == 0 ? FanModel.Of(Types, Fans) : null;
    public bool SupportsSpeed => Supported;
    /// <summary>Radio address rebinding is only for models verified on hardware.</summary>
    public bool RepairSupported => Model?.HardwareVerified == true && !RightAttach;
    public bool Supported => Model != null;
    public bool IsHydro => DeviceType == 10 && Fans == 0;
    public bool RgbSupported => Supported || IsHydro;
    public int LedCount => IsHydro ? 24 : Fans * (Model?.LedsPerFan ?? 0);
    public static WirelessGroup? Parse(ReadOnlySpan<byte> record, int listIndex = -1)
    {
        if (record.Length < 42 || record[41] != 0x1C || record[18] is not (0 or 10) || record[..6].IndexOfAnyExcept((byte)0) < 0) return null;
        int[] rpm = new int[4];
        for (int i = 0; i < 4; i++) rpm[i] = ((record[28 + i * 2] & 0x0F) << 8) | record[29 + i * 2];
        byte[] pwm = record[36..40].ToArray();
        // upstream parse_device_record: fan_num ≥ 10 — SL-INF, connected on the right; fans fan_num − 10.
        bool right = record[18] == 0 && record[19] >= 10;
        int fans = right ? Math.Min(record[19] - 10, 4) : record[19];
        // On the wire slot 0 is the rightmost (upstream reverse_fan_order); in the app the fan number is the order RGB.
        if (right && fans > 1) { Array.Reverse(rpm, 0, fans); Array.Reverse(pwm, 0, fans); }
        // For the pump byte 27 is the coolant temperature, not a type: otherwise every degree looks like "group changed".
        byte[] types = record[24..28].ToArray(); if (record[18] == 10) types[3] = 0;
        return new(Convert.ToHexString(record[..6]), Convert.ToHexString(record[6..12]), record[12], record[13], fans, types, record[20..24].ToArray(), (record[28] & 0x40) != 0, record[40], record[18], listIndex)
        {
            Rpm = rpm, Pwm = pwm, PwmLine = (record[28] & 0x20) != 0, RightAttach = right,
            Coolant = record[18] == 10 && record[27] > 0 ? record[27] : null,
        };
    }
}

public static class TinyUz
{
    [DllImport("tinyuz.dll", CallingConvention = CallingConvention.Cdecl)] static extern nuint tuz_max_compressed_size(nuint length);
    [DllImport("tinyuz.dll", CallingConvention = CallingConvention.Cdecl)] static extern nuint tuz_compress_mem(byte[] input, nuint length, byte[] output, nuint capacity, nuint dictionary);
    [DllImport("tuz-dec.dll", CallingConvention = CallingConvention.Cdecl)] static extern uint tuz_decompress_mem(byte[] input, nuint length, byte[] output, ref nuint size);
    public static byte[] Compress(byte[] input)
    {
        if (input.Length is < 1 or > 2_000_000) throw new InvalidDataException(L10n.T("Invalid RGB data size."));
        var output = new byte[checked((int)tuz_max_compressed_size((nuint)input.Length))];
        int length = checked((int)tuz_compress_mem(input, (nuint)input.Length, output, (nuint)output.Length, 4096));
        if (length == 0 || length > output.Length) throw new IOException(L10n.T("tinyuz compression error."));
        return output[..length];
    }
    public static byte[] Decompress(byte[] input, int length)
    {
        byte[] output = new byte[length]; nuint size = (nuint)length;
        uint status = tuz_decompress_mem(input, (nuint)input.Length, output, ref size);
        if (status != 1 || size != (nuint)length) throw new IOException(L10n.F((FormattableString)$"tinyuz decompression error: {status}."));
        return output;
    }
}

public sealed class RgbUpload
{
    public byte[] Compressed { get; }
    public byte[] Effect { get; }
    public int Frames { get; }
    public int Leds { get; }
    public uint Interval { get; }
    public RgbUpload(byte[][] frames, uint interval)
    {
        if (frames.Length is < 1 or > 4096 || frames[0].Length % 3 != 0 || frames[0].Length is < 3 or > 765 || frames.Any(f => f.Length != frames[0].Length) || interval is < 100 or > 6553599)
            throw new InvalidDataException(L10n.T("Invalid RGB cycle format."));
        Frames = frames.Length; Leds = frames[0].Length / 3; Interval = interval;
        byte[] raw = new byte[frames.Length * frames[0].Length];
        for (int i = 0; i < frames.Length; i++) frames[i].CopyTo(raw, i * frames[0].Length);
        Compressed = TinyUz.Compress(raw);
        byte[] tail = new byte[13];
        BinaryPrimitives.WriteUInt16BigEndian(tail, (ushort)(interval / 100)); tail[2] = (byte)(interval % 100);
        BinaryPrimitives.WriteUInt16BigEndian(tail.AsSpan(3), (ushort)Frames); tail[5] = (byte)Leds;
        // The secondary interval, frame count and outer_longest fields are zero.
        uint hash = 0x811C9DC5;
        foreach (byte b in Compressed.Concat(tail.Take(11))) hash = unchecked((hash ^ b) * 0x01000193);
        Effect = new byte[4]; BinaryPrimitives.WriteUInt32BigEndian(Effect, hash); Effect[0] |= 0x80;
    }
    /// <summary>How many times the cycle header is sent: a lost header is the main reason for “not accepted the first time”.</summary>
    public const int HeaderRepeats = 4;
    public IEnumerable<byte[]> Packets(string mac, string master, byte channel, byte slot, int repeats = HeaderRepeats)
    {
        if (Compressed.Length > 12288 || channel is < 1 or > 39 || slot is < 1 or >= 14) throw new InvalidDataException(L10n.T("RGB cycle or address exceeds safe limits."));
        byte[] target = Convert.FromHexString(mac), owner = Convert.FromHexString(master);
        if (target.Length != 6 || owner.Length != 6) throw new InvalidDataException(L10n.T("Invalid MAC address."));
        int chunks = (Compressed.Length + 219) / 220;
        for (int index = 0; index <= chunks; index++)
        {
            byte[] rf = new byte[240]; rf[0] = 0x12; rf[1] = 0x20;
            target.CopyTo(rf, 2); owner.CopyTo(rf, 8); Effect.CopyTo(rf, 14);
            rf[18] = (byte)index; rf[19] = (byte)(chunks + 1);
            if (index == 0)
            {
                BinaryPrimitives.WriteUInt32BigEndian(rf.AsSpan(20), (uint)Compressed.Length);
                BinaryPrimitives.WriteUInt16BigEndian(rf.AsSpan(25), (ushort)Frames); rf[27] = (byte)Leds;
                BinaryPrimitives.WriteUInt16BigEndian(rf.AsSpan(32), (ushort)(Interval / 100)); rf[34] = (byte)(Interval % 100);
            }
            else Array.Copy(Compressed, (index - 1) * 220, rf, 20, Math.Min(220, Compressed.Length - (index - 1) * 220));
            for (int repeat = 0; repeat < (index == 0 ? Math.Clamp(repeats, 1, 4) : 1); repeat++)
                for (int part = 0; part < 4; part++)
                {
                    byte[] usb = new byte[64]; usb[0] = 0x10; usb[1] = (byte)part; usb[2] = channel; usb[3] = slot;
                    Array.Copy(rf, part * 60, usb, 4, 60); yield return usb;
                }
        }
    }
}

public sealed record RgbScene(int Frames, uint Interval, Dictionary<string, RgbUpload> Uploads, RgbUpload? Pump = null)
{
    public static RgbScene Prepare(Profile profile, WirelessGroup[] selected, CancellationToken cancellation = default, bool includePump = false)
    {
        profile.Validate();
        if ((selected.Length == 0 && !includePump) || selected.Any(g => !g.Supported)) throw new InvalidDataException(L10n.T("No confirmed devices for this scene."));
        int frames = Math.Clamp((int)Math.Round(profile.PeriodSeconds * profile.Fps), 2, 4096);
        while (true)
        {
            cancellation.ThrowIfCancellationRequested();
            uint interval = checked((uint)Math.Round(profile.PeriodSeconds * 160000 / frames));
            if (includePump)
            {
                interval = (uint)Math.Max(1, Math.Round(interval / 100.0)) * 100;
                if (interval > 25500) throw new InvalidDataException(L10n.T("Pump needs a higher FPS for this cycle. Increase FPS or shorten the cycle."));
            }
            var uploads = selected.ToDictionary(g => g.Mac, g => new RgbUpload(Enumerable.Range(0, frames).Select(f => Gradient.Frame(profile, g.Mac, g.Fans, f * profile.PeriodSeconds / frames, g.Model)).ToArray(), interval));
            var pump = includePump ? new RgbUpload(Enumerable.Range(0, frames).Select(f => Gradient.PumpFrame(profile, f * profile.PeriodSeconds / frames)).ToArray(), interval) : null;
            if (uploads.Values.All(u => u.Compressed.Length <= 12288) && (pump == null || pump.Compressed.Length <= 12288)) return new(frames, interval, uploads, pump);
            if (frames <= 2) throw new IOException(L10n.T("Gradient exceeds device memory."));
            frames = Math.Max(2, frames * 3 / 4);
        }
    }
}
