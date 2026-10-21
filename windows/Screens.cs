using System.Text.Json;

namespace WirelessCanvas;

/// <summary>
/// Thread of one screen. Its own thread and exceptions: a screen error does not stop lighting
/// or cooling. A transfer is never cut in the middle: stop is checked between frames.
/// Image and Off repeat every 10 s (a reconnected panel gets a frame again),
/// the dashboard once per second when the number changes, GIF by its own delays but no faster than the limit FPS.
/// </summary>
internal sealed class ScreenWorker : IDisposable
{
    readonly IScreenDevice device;
    readonly Func<SensorReadings> sensors;
    readonly SensorHistory? history;
    readonly object gate = new();
    readonly AutoResetEvent wake = new(false);
    readonly Thread thread;
    readonly CancellationTokenSource stop = new();
    ScreenSettings? settings;
    string palette = "";
    ScreenMapping? mapping;
    string sceneKey = "";
    readonly Func<double> sceneSeconds;
    int version;
    (int Number, long Until)? identify;
    public string Status { get; private set; } = L10n.T("waiting");
    public int FramesSent { get; private set; }
    public string? Firmware => device.Firmware;

    public ScreenWorker(IScreenDevice device, Func<SensorReadings> sensors, string name, SensorHistory? history = null, Func<double>? sceneSeconds = null)
    {
        this.device = device; this.sensors = sensors; this.history = history;
        this.sceneSeconds = sceneSeconds ?? (() => Environment.TickCount64 / 1000.0);
        thread = new Thread(Run) { IsBackground = true, Name = "LCD " + name };
        thread.Start();
    }
    public void Update(ScreenSettings value, string newPalette, Profile? scene = null)
    {
        var copy = JsonSerializer.Deserialize<ScreenSettings>(JsonSerializer.Serialize(value))!;
        string key = scene != null && copy.Mode == ScreenMode.Mapping ? JsonSerializer.Serialize(scene) : sceneKey;
        var snapshot = scene != null && copy.Mode == ScreenMode.Mapping && key != sceneKey ? new ScreenMapping(scene) : mapping;
        lock (gate)
        {
            if (settings != null && JsonSerializer.Serialize(settings) == JsonSerializer.Serialize(copy) && palette == newPalette && key == sceneKey) return;
            // New settings immediately replace the number display.
            settings = copy; palette = newPalette; identify = null; version++;
            mapping = snapshot; sceneKey = key;
        }
        wake.Set();
    }
    /// <summary>A large digit for 8 seconds, then the screen returns to its content.</summary>
    public void Identify(int number) { lock (gate) { identify = (number, Environment.TickCount64 + 8000); version++; } wake.Set(); }

    void Run()
    {
        int applied = -1, frame = 0, failures = 0;
        int? brightnessSent = null;
        List<(byte[] Jpeg, int Milliseconds)> frames = [];
        long nextFrame = 0, lastSend = 0, lastError = 0;
        string lastText = "";
        while (!stop.IsCancellationRequested)
        {
            ScreenSettings? s; string currentPalette; int currentVersion; (int Number, long Until)? marker;
            ScreenMapping? currentMapping;
            lock (gate) { s = settings; currentPalette = palette; currentVersion = version; marker = identify; currentMapping = mapping; }
            int wait = 1000;
            try
            {
                long now = Environment.TickCount64;
                if (marker is { } m && now >= m.Until) { lock (gate) { if (identify == marker) { identify = null; version++; } } continue; }
                if (s == null || (s.Mode == ScreenMode.Unmanaged && marker == null))
                {
                    Status = L10n.T("unmanaged: app sends nothing to this screen"); brightnessSent = null; applied = currentVersion;
                    wake.WaitOne(1000); continue;
                }
                if (applied != currentVersion)
                {
                    frames = [];
                    if (marker == null && s.Mode == ScreenMode.Image) { using var picture = ScreenRenderer.Load(s.ImagePath); using var b = ScreenRenderer.Picture(picture, s); frames = [(ScreenRenderer.Encode(b, s.Spec), 0)]; }
                    if (marker == null && s.Mode == ScreenMode.Gif) { Status = L10n.T("preparing GIF…"); frames = ScreenRenderer.Animation(s.GifPath, s, stop.Token); }
                    if (marker == null && s.Mode == ScreenMode.Off) { using var b = ScreenRenderer.Blank(s.Spec.Size); frames = [(ScreenRenderer.Encode(b, s.Spec), 0)]; }
                    if (marker is { } n) { using var b = ScreenRenderer.Number(s, n.Number); frames = [(ScreenRenderer.Encode(b, s.Spec), 0)]; }
                    applied = currentVersion; frame = 0; lastSend = 0; nextFrame = 0; lastText = "";
                }
                int brightness = marker != null ? Math.Max(60, s.Brightness) : s.Mode == ScreenMode.Off ? 0 : s.Brightness;
                if (brightnessSent != brightness) { device.SetBrightness(brightness); brightnessSent = brightness; }
                now = Environment.TickCount64;
                if (marker == null && s.Mode == ScreenMode.Sensor)
                {
                    var readings = sensors(); var series = history?.Series(s.Sensor) ?? [];
                    // A frame is sent only when something visible changes (number, set of numbers, chart) or every 10 s.
                    string key = Dashboard.Key(s, readings, series);
                    if (key != lastText || now - lastSend >= 10_000)
                    {
                        using var b = ScreenRenderer.Dashboard(s, readings, series, currentPalette);
                        Send(ScreenRenderer.Encode(b, s.Spec)); lastSend = now; lastText = key;
                    }
                    Status = L10n.F((FormattableString)$"sensor {s.SensorLabel}: {Dashboard.Text(readings.For(s.Sensor))} °C · theme “{Dashboard.ThemeLabels[(int)s.Theme]}” · frames {FramesSent}");
                }
                else if (marker == null && s.Mode == ScreenMode.Mapping)
                {
                    if (now >= nextFrame)
                    {
                        if (currentMapping == null) throw new InvalidDataException(L10n.T("No mapping scene available."));
                        using var b = currentMapping.Render(s, sceneSeconds());
                        Send(ScreenRenderer.Encode(b, s.Spec));
                        // Slow USB skips obsolete frames instead of accumulating a queue.
                        nextFrame = Math.Max(now + (1000 + s.MappingFps - 1) / s.MappingFps, Environment.TickCount64);
                    }
                    wait = (int)Math.Clamp(nextFrame - Environment.TickCount64, 1, 1000);
                    Status = L10n.T("Follow mapping") + $" · ≤{s.MappingFps} FPS · {FramesSent}";
                }
                else if (marker == null && s.Mode == ScreenMode.Gif && frames.Count > 1)
                {
                    if (now >= nextFrame)
                    {
                        var (jpeg, duration) = frames[frame];
                        Send(jpeg); nextFrame = Math.Max(now, nextFrame) + duration; frame = (frame + 1) % frames.Count;
                    }
                    wait = (int)Math.Clamp(nextFrame - Environment.TickCount64, 1, 1000);
                    Status = L10n.F((FormattableString)$"GIF: {frames.Count} frames, up to {s.MaxFps} FPS · sent {FramesSent}");
                }
                else
                {
                    if (lastSend == 0 || now - lastSend >= 10_000) { Send(frames[0].Jpeg); lastSend = now; }
                    Status = marker is { } n ? L10n.F((FormattableString)$"showing number {n.Number}") : s.Mode == ScreenMode.Off ? L10n.T("off: brightness 0, black frame") : L10n.F((FormattableString)$"image shown · {frames[0].Jpeg.Length / 1024} KB");
                }
                failures = 0;
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
            catch (Exception e)
            {
                failures++; brightnessSent = null; lastSend = 0;
                // A bad file will not fix itself on retry, so wait for new settings.
                bool content = e is FileNotFoundException or InvalidDataException or ArgumentException or OutOfMemoryException or System.Runtime.InteropServices.ExternalException;
                if (content) applied = currentVersion;
                Status = L10n.T("error: ") + e.Message + (content ? "" : L10n.T(" · retrying in a few seconds"));
                if (Environment.TickCount64 - lastError > 10_000) { Log.Write($"LCD {thread.Name}: {e}"); lastError = Environment.TickCount64; }
                wait = content ? 2000 : Math.Min(30_000, 2000 * failures);
            }
            wake.WaitOne(wait);
        }
        try { device.Dispose(); } catch (Exception e) { Log.Error(e); }
    }
    void Send(byte[] jpeg) { device.SendFrame(jpeg); FramesSent++; }
    public void Dispose()
    {
        stop.Cancel(); wake.Set();
        // A frame in flight is finished (within USB timeouts); the thread is never aborted.
        if (!thread.Join(8000)) Log.Write(L10n.F((FormattableString)$"LCD {thread.Name}: worker did not stop within 8 s"));
        wake.Dispose(); stop.Dispose();
    }
}

/// <summary>All screens: device discovery, threads and shared access to the pump USB. Created only in a normal run (HardwareOutput).</summary>
public sealed class ScreenHub : IDisposable
{
    readonly HydroController pump;
    readonly Controller controller;
    readonly Func<SensorReadings> sensors;
    readonly SensorHistory history;
    readonly Func<double>? sceneSeconds;
    readonly Dictionary<string, ScreenWorker> workers = [];
    public IReadOnlyList<ScreenInventory.Found> Found { get; private set; } = [];
    public ScreenHub(HydroController pump, Controller controller, Func<SensorReadings> sensors, SensorHistory history, Func<double>? sceneSeconds = null) { this.pump = pump; this.controller = controller; this.sensors = sensors; this.history = history; this.sceneSeconds = sceneSeconds; }

    public void Rescan() => Found = ScreenInventory.Scan();

    /// <summary>Applies settings: a thread is created for each found screen whose mode is not Unmanaged.</summary>
    public void Apply(IEnumerable<ScreenSettings> screens, string palette, Profile? scene = null)
    {
        foreach (var s in screens)
        {
            if (!workers.TryGetValue(s.Id, out var worker))
            {
                if (s.Mode == ScreenMode.Unmanaged) continue;
                worker = Create(s);
                if (worker == null) continue;
                workers[s.Id] = worker;
            }
            worker.Update(s, palette, scene);
        }
    }
    ScreenWorker? Create(ScreenSettings s)
    {
        if (s.IsPump) return Found.Any(f => f.Kind == ScreenKind.Pump) ? new ScreenWorker(new PumpScreen(pump), sensors, L10n.T("pump"), history, sceneSeconds) : null;
        if (!Found.Any(f => f.Kind == ScreenKind.Fan && f.Serial == s.Id)) return null;
        string serial = s.Id;
        return new ScreenWorker(new FanScreen(serial, () =>
        {
            if (Controller.VendorRunning()) throw new IOException(L10n.T("Close L-Connect 3; fan screen cannot be opened."));
            var device = ScreenInventory.Scan().FirstOrDefault(f => f.Kind == ScreenKind.Fan && f.Serial == serial) ?? throw new IOException(L10n.T("LCD fan ") + serial + L10n.T(" not found via USB."));
            return new HydroLink(device.Path);
        }, controller.EnsureVideoMode), sensors, serial, history, sceneSeconds);
    }
    public void Identify(ScreenSettings s, int number, string palette, Profile? scene = null)
    {
        if (!workers.ContainsKey(s.Id)) { var worker = Create(s) ?? throw new IOException(L10n.T("Screen not found via USB. Click Connect USB.")); workers[s.Id] = worker; worker.Update(s, palette, scene); }
        workers[s.Id].Identify(number);
    }
    public string Status(string id) => workers.TryGetValue(id, out var w) ? w.Status : Found.Any(f => f.Serial == id || (id == ScreenSettings.PumpId && f.Kind == ScreenKind.Pump)) ? L10n.T("found · unmanaged") : L10n.T("not found via USB");
    public string? Firmware(string id) => workers.TryGetValue(id, out var w) ? w.Firmware : null;
    public void Dispose()
    {
        // All threads stop together: each finishes its frame.
        Parallel.ForEach(workers.Values, worker => worker.Dispose());
        workers.Clear();
    }
}
