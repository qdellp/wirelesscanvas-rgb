using System.Diagnostics;
using System.Text.Json.Serialization;

namespace WirelessCanvas;

/// <summary>An OpenRGB device on the case canvas: a rectangle whose long side carries its LED.</summary>
public sealed class ExternalPlacement
{
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";
    public int Type { get; set; }
    public bool Enabled { get; set; } = true;
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; } = 10;
    public double Height { get; set; } = 10;
    public bool Reverse { get; set; }
    public double Brightness { get; set; } = 1;
    /// <summary>"" — auto: memory in Static, everything else in Direct. Otherwise the name of a mode with a color per LED.</summary>
    public string Mode { get; set; } = "";
    public LightMode Light { get; set; }
    public string CustomColor { get; set; } = "#FFFFFF";
    [JsonIgnore] public int LedCount { get; set; }
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Key) || Key.Length > 512 || Name.Length > 256 || !double.IsFinite(X) || !double.IsFinite(Y) || Math.Abs(X) > 10000 || Math.Abs(Y) > 10000 ||
            !double.IsFinite(Width) || !double.IsFinite(Height) || Width is < 1 or > 2000 || Height is < 1 or > 2000 || !double.IsFinite(Brightness) || Brightness is < 0 or > 1 || Mode is not ("" or "Direct" or "Static") ||
            !Enum.IsDefined(Light) || !ColorPickerDialog.TryParseHex(CustomColor, out _))
            throw new InvalidDataException(L10n.T("Invalid OpenRGB device placement."));
    }
    public RectangleF Bounds => new((float)(X - Width / 2), (float)(Y - Height / 2), (float)Width, (float)Height);
}

public static class ExternalLayout
{
    // Positions from a video of the reference case (canvas: x·0.6−24, y·0.6 of the photo): memory sticks right of the pump, the AERO logo on the GPU edge.
    static readonly (double X, double Y, double W, double H)[] Memory = [(307, 240, 8, 140), (331, 240, 8, 140)];
    static readonly (double X, double Y, double W, double H) GraphicsLogo = (110, 434, 50, 10);

    /// <summary>Matches found devices to saved placements; new ones get a position by type.</summary>
    public static List<ExternalPlacement> Merge(IReadOnlyList<ExternalPlacement> saved, IReadOnlyList<OpenRgbDevice> devices)
    {
        var result = new List<ExternalPlacement>();
        var usedLocations = new HashSet<string>();
        int memory = 0;
        foreach (var device in devices)
        {
            var placement = saved.FirstOrDefault(s => s.Key == device.Key) ?? saved.FirstOrDefault(s => s.Name == device.Name && !result.Contains(s));
            if (placement == null)
            {
                placement = new ExternalPlacement { Key = device.Key, Name = device.Name, Type = device.Type };
                (placement.X, placement.Y, placement.Width, placement.Height) = device.Type switch
                {
                    1 => memory < Memory.Length ? Memory[memory] : (Memory[^1].X + (memory - Memory.Length + 1) * 24, Memory[^1].Y, Memory[^1].W, Memory[^1].H),
                    2 => GraphicsLogo,
                    _ => (60 + result.Count * 40, 600, 30, 10),
                };
                // By default: memory and GPU. A repeated registration of the same chip (same address) is off.
                placement.Enabled = device.Type is 1 or 2 && !usedLocations.Contains(device.Location);
            }
            // 0.2.0 placed memory 200 high; checked on the case, the light guide is 30% shorter.
            if (device.Type == 1 && placement.Width == 8 && placement.Height == 200) placement.Height = 140;
            if (device.Type == 1) memory++;
            placement.Key = device.Key; placement.Name = device.Name; placement.Type = device.Type; placement.LedCount = device.LedCount;
            if (placement.Enabled) usedLocations.Add(device.Location);
            result.Add(placement);
        }
        // Keep placements of temporarily missing devices (for example memory without administrator rights).
        result.AddRange(saved.Where(s => !result.Contains(s) && result.All(r => r.Key != s.Key)));
        return result;
    }

    /// <summary>LED coordinates: a single LED is the center, otherwise evenly along the long side in device order.</summary>
    /// <summary>
    /// Output mode. The author's ENE DRAM DDR5 keeps playing its built-in rainbow in Direct,
    /// but accepts per-LED colors in Static, so memory uses this by default: Static.
    /// </summary>
    public static int ModeIndex(OpenRgbDevice device, ExternalPlacement placement)
    {
        string wanted = placement.Mode.Length > 0 ? placement.Mode : device.Type == 1 ? "Static" : "Direct";
        int index = Array.FindIndex(device.Modes, m => m.Name == wanted && m.PerLed);
        return index >= 0 ? index : Array.FindIndex(device.Modes, m => m.Name == "Direct" && m.PerLed);
    }

    public static LedPoint Point(ExternalPlacement placement, int led, int count)
    {
        if (count <= 1) return new(placement.X, placement.Y);
        double t = (double)led / (count - 1) - .5;
        if (placement.Reverse) t = -t;
        return placement.Height > placement.Width ? new(placement.X, placement.Y + t * placement.Height) : new(placement.X + t * placement.Width, placement.Y);
    }

    /// <summary>Device frame; device = false gives screen colors for the preview (no gamma LED).</summary>
    public static byte[] Frame(Profile p, ColorStop[] stops, ExternalPlacement placement, int count, double seconds, bool device = true)
    {
        byte[] rgb = new byte[count * 3];
        for (int led = 0; led < count; led++)
        {
            int index = led;
            var c = Gradient.Override(p, placement.Light, placement.CustomColor, () =>
            {
                var screen = Gradient.Shade(p, stops, Point(placement, index, count), seconds, p.ColorSpace, p.SaturationBoost, (device ? p.Brightness : 1) * placement.Brightness, false);
                return device ? Gradient.Device(p, screen, false) : screen;
            }, device, false, placement.Brightness);
            rgb[led * 3] = c.R; rgb[led * 3 + 1] = c.G; rgb[led * 3 + 2] = c.B;
        }
        return rgb;
    }
}

/// <summary>
/// Streams the shared gradient to the OpenRGB SDK server (127.0.0.1:6742) in real time.
/// The phase comes from the radio controller clock when the fan scene is uploaded, otherwise from the local clock.
/// </summary>
public sealed class OpenRgbBridge : IDisposable
{
    readonly Func<Profile> profile;
    readonly Func<double?> scenePhase;
    readonly int port;
    readonly CancellationTokenSource stop = new();
    readonly Stopwatch local = Stopwatch.StartNew();
    readonly Task worker;
    readonly object gate = new();
    OpenRgbDevice[] devices = [];
    volatile bool refresh = true;
    int instances;
    long nextInstanceCheck;
    public string Status { get; private set; } = L10n.T("OpenRGB: connecting…");
    public bool Connected { get; private set; }
    public OpenRgbDevice[] Devices { get { lock (gate) return devices; } }
    public event Action<OpenRgbDevice[]>? DevicesChanged;

    public OpenRgbBridge(Func<Profile> profile, Func<double?> scenePhase, int port = 6742)
    {
        this.profile = profile; this.scenePhase = scenePhase; this.port = port;
        worker = Task.Run(Run);
    }

    public void Refresh() => refresh = true;

    public double LocalSeconds => local.Elapsed.TotalSeconds;
    public double SceneSeconds(Profile p) => scenePhase() is double phase ? phase * p.PeriodSeconds : local.Elapsed.TotalSeconds;

    async Task Run()
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                using var client = new OpenRgbClient(port: port);
                Connected = true; refresh = true;
                var last = new Dictionary<int, byte[]>();
                var modes = new Dictionary<int, int>();
                while (!stop.IsCancellationRequested)
                {
                    if (refresh || client.DeviceListChanged)
                    {
                        refresh = false; var found = client.Devices();
                        lock (gate) devices = found;
                        last.Clear(); modes.Clear();
                        DevicesChanged?.Invoke(found);
                    }
                    var p = profile();
                    var stops = Effects.Palette(p);
                    double seconds = SceneSeconds(p);
                    int sent = 0;
                    foreach (var device in Devices)
                    {
                        // A keyboard with its own tab follows its own gradient, regardless of its placement on the case canvas.
                        bool keyboard = p.Keyboard.Enabled && KeyboardLayout.Matches(p.Keyboard, device);
                        var placement = p.External.FirstOrDefault(e => e.Key == device.Key);
                        if (device.LedCount == 0 || (!keyboard && (placement == null || !placement.Enabled))) continue;
                        // The mode is set once per connection (and when it changes), then only colors are sent LED.
                        int mode = keyboard ? Array.FindIndex(device.Modes, m => m.Name == "Direct" && m.PerLed) : ExternalLayout.ModeIndex(device, placement!);
                        if (mode < 0) continue;
                        if (!modes.TryGetValue(device.Index, out int current) || current != mode) {
                            // Direct — through SETCUSTOMMODE, as in 0.2.0 (the GPU worked with it); other modes — UpdateMode.
                            if (device.Modes[mode].Name == "Direct") client.SetCustomMode(device.Index); else client.SetMode(device.Index, mode, device.Modes[mode]);
                            modes[device.Index] = mode; last.Remove(device.Index);
                        }
                        var frame = keyboard ? KeyboardLayout.Frame(p, device.LedCount, seconds, local.Elapsed.TotalSeconds, true) : ExternalLayout.Frame(p, stops, placement!, device.LedCount, seconds);
                        if (last.TryGetValue(device.Index, out var previous) && previous.AsSpan().SequenceEqual(frame)) { sent++; continue; }
                        client.UpdateLeds(device.Index, frame); last[device.Index] = frame; sent++;
                    }
                    // Enumerating processes is expensive: check for a second instance every 2 seconds.
                    if (Environment.TickCount64 >= nextInstanceCheck) { instances = OpenRgbLauncher.Instances(); nextInstanceCheck = Environment.TickCount64 + 2000; }
                    Status = L10n.F((FormattableString)$"OpenRGB: {Devices.Length} devices found, sending to {sent}") + (scenePhase() != null ? L10n.T(" · fan clock phase") : L10n.T(" · local clock (fans not loaded)")) +
                        (instances > 1 ? L10n.F((FormattableString)$" · WARNING: {instances} OpenRGB instances running; click Restart OpenRGB") : "");
                    await Task.Delay(1000 / Math.Clamp(p.Fps, 1, 30), stop.Token);
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception e)
            {
                Connected = false;
                Status = L10n.T("OpenRGB unavailable: ") + e.Message + L10n.T(" Retrying in 3 s.");
                try { await Task.Delay(3000, stop.Token); } catch (OperationCanceledException) { break; }
            }
        }
        Connected = false;
    }

    public void Dispose()
    {
        stop.Cancel();
        try { worker.GetAwaiter().GetResult(); } catch (Exception e) { Log.Error(e); }
        stop.Dispose();
    }
}

public static class OpenRgbLauncher
{
    public static string? Find()
    {
        string programs = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        return new[] { Path.Combine(programs, "OpenRGB", "OpenRGB.exe"), Path.Combine(programs, "Lian-Li", "L-Connect 3", "openrgb", "OpenRGB.exe") }.FirstOrDefault(File.Exists);
    }
    public static int Instances() { var all = Process.GetProcessesByName("OpenRGB"); foreach (var p in all) p.Dispose(); return all.Length; }
    public static bool Running() => Instances() > 0;
    /// <summary>Start elevated (UAC prompt): without rights PawnIO gives OpenRGB no access to memory.</summary>
    public static void StartElevated()
    {
        // Two instances would access the memory SMBus at the same time, so a second one is not started.
        if (Running()) throw new InvalidOperationException(L10n.T("OpenRGB is already running. If memory is missing, click Restart OpenRGB."));
        string path = Find() ?? throw new FileNotFoundException(L10n.T("OpenRGB not found. Install OpenRGB or L-Connect 3 with its OpenRGB module."));
        Elevated(path, "--server --startminimized", Path.GetDirectoryName(path)!);
    }
    /// <summary>Closes all OpenRGB instances and starts one as administrator: one prompt UAC.</summary>
    public static void RestartElevated()
    {
        string path = Find() ?? throw new FileNotFoundException(L10n.T("OpenRGB not found. Install OpenRGB or L-Connect 3 with its OpenRGB module."));
        Elevated(Path.Combine(Environment.SystemDirectory, "cmd.exe"), $"/c taskkill /IM OpenRGB.exe /F >nul 2>&1 & timeout /t 2 /nobreak >nul & start \"\" /d \"{Path.GetDirectoryName(path)}\" \"{path}\" --server --startminimized", Environment.SystemDirectory, hidden: true);
    }
    /// <summary>OpenRGB started by this app: it stops when the app is fully closed.</summary>
    public static bool StartedByUs { get; private set; }
    /// <summary>
    /// Full close of the app: the OpenRGB it started is closed. An OpenRGB you opened yourself before
    /// the app started is left alone. Without administrator rights an elevated OpenRGB cannot be closed, so it stays.
    /// </summary>
    public static string? StopStarted()
    {
        if (!StartedByUs) return null;
        int stopped = 0;
        foreach (var process in Process.GetProcessesByName("OpenRGB"))
            using (process)
                try { process.Kill(entireProcessTree: true); process.WaitForExit(3000); stopped++; }
                catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { Log.Write(L10n.T("OpenRGB could not be stopped: ") + e.Message); }
        StartedByUs = false;
        return L10n.F((FormattableString)$"OpenRGB stopped: {stopped}");
    }
    static void Elevated(string file, string arguments, string directory, bool hidden = false)
    {
        StartedByUs = true;
        try
        {
            Process.Start(new ProcessStartInfo(file, arguments) { UseShellExecute = true, Verb = "runas", WorkingDirectory = directory, WindowStyle = hidden ? ProcessWindowStyle.Hidden : ProcessWindowStyle.Normal })?.Dispose();
        }
        catch (System.ComponentModel.Win32Exception e) when (e.NativeErrorCode == 1223) { throw new OperationCanceledException(L10n.T("OpenRGB administrator startup cancelled.")); }
    }
}
