using Microsoft.Win32.SafeHandles;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace WirelessCanvas;

public sealed class RadioDelay : IDisposable
{
    readonly SafeWaitHandle timer;
    public bool HighResolution { get; }
    public RadioDelay()
    {
        timer = CreateWaitableTimerEx(IntPtr.Zero, null, 2, 0x00100002);
        HighResolution = !timer.IsInvalid;
        if (!HighResolution)
        {
            timer.Dispose();
            timer = CreateWaitableTimerEx(IntPtr.Zero, null, 0, 0x00100002);
            if (timer.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), L10n.T("Could not create RGB transfer timer."));
        }
    }
    public void Wait(int milliseconds)
    {
        if (milliseconds is < 0 or > 1000) throw new ArgumentOutOfRangeException(nameof(milliseconds));
        if (milliseconds == 0) return;
        long due = -milliseconds * 10000L;
        if (!SetWaitableTimerEx(timer, ref due, 0, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0)) throw new Win32Exception(Marshal.GetLastWin32Error());
        uint result = WaitForSingleObject(timer, (uint)(milliseconds + 1000));
        if (result == 0xFFFFFFFF) throw new Win32Exception(Marshal.GetLastWin32Error());
        if (result != 0) throw new TimeoutException(L10n.T("Windows did not complete the RGB timer wait."));
    }
    public void Dispose() => timer.Dispose();
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateWaitableTimerExW")]
    static extern SafeWaitHandle CreateWaitableTimerEx(IntPtr attributes, string? name, uint flags, uint access);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool SetWaitableTimerEx(SafeWaitHandle timer, ref long due, int period, IntPtr routine, IntPtr argument, IntPtr context, uint tolerance);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern uint WaitForSingleObject(SafeWaitHandle handle, uint milliseconds);
}

public sealed record RgbTransferResult(int RadioFrames, int UsbPackets, TimeSpan Elapsed);

public sealed class RgbTransferException(string message, int completedFrames, int totalFrames, TimeSpan elapsed) : IOException(message)
{
    public int CompletedFrames { get; } = completedFrames;
    public int TotalFrames { get; } = totalFrames;
    public TimeSpan Elapsed { get; } = elapsed;
}

public static class RgbTransfer
{
    public static TimeSpan BudgetFor(int usbPackets)
    {
        // Up to 56 data chunks (12,288 bytes) and 4 header copies: no more than 64 RF packets.
        if (usbPackets is < 8 or > 256 || usbPackets % 4 != 0) throw new ArgumentOutOfRangeException(nameof(usbPackets));
        // Sleep(1) can take a whole Windows timer tick; the margin holds even when the timer falls back.
        return TimeSpan.FromMilliseconds(Math.Clamp(1000 + usbPackets * 50, 3000, 15000));
    }
    /// <param name="headerFrames">How many leading RF packets are header copies: 2 ms gap between them (up to two copies) or 20 ms, as upstream.</param>
    public static RgbTransferResult Send(byte[][] packets, Action<byte[][]> sendFrame, Action heartbeat,
        Action<int> delay, CancellationToken cancellation = default, Func<TimeSpan>? elapsed = null, int headerFrames = 2)
    {
        TimeSpan budget = BudgetFor(packets.Length);
        if (packets.Any(p => p.Length != 64)) throw new InvalidDataException(L10n.T("Incomplete RGB USB packet."));
        var clock = Stopwatch.StartNew();
        elapsed ??= () => clock.Elapsed;
        int total = packets.Length / 4;
        void CheckDeadline(int completed)
        {
            if (elapsed() >= budget)
                throw new RgbTransferException(L10n.F((FormattableString)$"RGB upload timed out: {completed}/{total} RF packets in {elapsed().TotalSeconds:F1} s (limit {budget.TotalSeconds:F1} s)."), completed, total, elapsed());
        }
        for (int i = 0; i < packets.Length; i += 4)
        {
            cancellation.ThrowIfCancellationRequested();
            CheckDeadline(i / 4);
            heartbeat();
            cancellation.ThrowIfCancellationRequested();
            CheckDeadline(i / 4);
            sendFrame(packets[i..(i + 4)]);
            if (i / 4 < headerFrames - 1) delay(headerFrames <= 2 ? 2 : 20);
        }
        return new(total, packets.Length, elapsed());
    }
}
