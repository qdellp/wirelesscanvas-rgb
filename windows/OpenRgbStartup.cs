namespace WirelessCanvas;

internal static class OpenRgbStartup
{
    /// <summary>Regular refresh after the services start, plus two retries for late detection OpenRGB.</summary>
    public static async Task RefreshDevices(Action refresh, Func<bool> enabled, CancellationToken cancellation,
        Func<int, CancellationToken, Task>? delay = null)
    {
        delay ??= Task.Delay;
        try
        {
            foreach (int wait in new[] { 0, 3000, 7000 })
            {
                cancellation.ThrowIfCancellationRequested();
                if (!enabled()) return;
                if (wait > 0) await delay(wait, cancellation);
                cancellation.ThrowIfCancellationRequested();
                if (!enabled()) return;
                refresh();
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
    }
}
