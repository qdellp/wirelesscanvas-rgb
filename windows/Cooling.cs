using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WirelessCanvas;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CoolingSource { Off, Cpu, Gpu, Coolant, Fixed }

/// <summary>Curve type: points (polyline) or linear, a straight line between two limits.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CurveKind { Points, Linear }

public sealed class CurvePoint
{
    public double Temperature { get; set; }
    public double Percent { get; set; }
    public CurvePoint() { }
    public CurvePoint(double temperature, double percent) { Temperature = temperature; Percent = percent; }
}

/// <summary>Speed of a wireless group: unmanaged, constant, or a temperature curve.</summary>
public sealed class CoolingGroup
{
    public string Mac { get; set; } = "";
    public CoolingSource Source { get; set; }
    public double FixedPercent { get; set; } = 50;
    public List<CurvePoint> Curve { get; set; } = DefaultCurve();
    public CurveKind Kind { get; set; }
    /// <summary>Linear: first speed below the first temperature, second speed above the second, a straight line in between.</summary>
    public List<CurvePoint> Linear { get; set; } = [new(40, 30), new(80, 100)];
    /// <summary>The curve currently used for the speed; points and line are stored separately and survive switching.</summary>
    [JsonIgnore] public List<CurvePoint> Active => Kind == CurveKind.Linear ? Linear : Curve;
    public static List<CurvePoint> DefaultCurve() => [new(30, 30), new(50, 40), new(65, 60), new(80, 85), new(90, 100)];
}

public sealed class CoolingSettings
{
    /// <summary>Key of the pump row in Groups from version 0.5.0 (pump curve); on profile load it is converted to PumpRpm.</summary>
    public const string PumpKey = "pump";
    /// <summary>
    /// Constant pump RPM, 1600–2500; null means the pump is not controlled. Independent of temperature
    /// and of the fan control checkbox: the pump gets the same unchanging command.
    /// </summary>
    public int? PumpRpm { get; set; }
    /// <summary>Profile 0.5.0: a constant pump row becomes PumpRpm, a pump curve is dropped (pump not controlled).</summary>
    public void Upgrade()
    {
        var old = Groups.FirstOrDefault(g => g.Mac == PumpKey);
        if (old == null) return;
        if (PumpRpm == null && old.Source == CoolingSource.Fixed) PumpRpm = Controller.PumpRpm(old.FixedPercent);
        Groups.RemoveAll(g => g.Mac == PumpKey);
    }
    public bool Enabled { get; set; }
    /// <summary>Lower speed limit, %: controlled fans never go below it.</summary>
    public double MinPercent { get; set; } = 25;
    /// <summary>Speed used when the temperature has been missing for more than 10 seconds.</summary>
    public double FailsafePercent { get; set; } = 100;
    /// <summary>Speed only decreases after the temperature has dropped by at least this many degrees.</summary>
    public double Hysteresis { get; set; } = 2;
    public bool CpuSensorAutoStart { get; set; }
    public List<CoolingGroup> Groups { get; set; } = [];
    public CoolingGroup For(string mac)
    {
        var group = Groups.FirstOrDefault(g => g.Mac == mac);
        if (group == null) { group = new CoolingGroup { Mac = mac }; Groups.Add(group); }
        return group;
    }
    public void Validate()
    {
        if (!double.IsFinite(MinPercent) || MinPercent is < 15 or > 100 || !double.IsFinite(FailsafePercent) || FailsafePercent is < 50 or > 100 ||
            !double.IsFinite(Hysteresis) || Hysteresis is < 0 or > 10 || Groups.Count > 32 || PumpRpm is < Controller.PumpMinRpm or > Controller.PumpMaxRpm)
            throw new InvalidDataException(L10n.T("Invalid cooling settings."));
        foreach (var g in Groups)
            if (string.IsNullOrWhiteSpace(g.Mac) || !Enum.IsDefined(g.Source) || !double.IsFinite(g.FixedPercent) || g.FixedPercent is < 0 or > 100 ||
                g.Curve.Count is < 2 or > 12 || g.Curve.Any(p => !double.IsFinite(p.Temperature) || p.Temperature is < 0 or > 120 || !double.IsFinite(p.Percent) || p.Percent is < 0 or > 100))
                throw new InvalidDataException(L10n.T("Cooling curve: 2–12 points, 0–120 °C, 0–100%."));
        foreach (var g in Groups)
            if (!Enum.IsDefined(g.Kind) || g.Linear.Count != 2 || g.Linear.Any(p => !double.IsFinite(p.Temperature) || p.Temperature is < 0 or > 120 || !double.IsFinite(p.Percent) || p.Percent is < 0 or > 100) ||
                g.Linear[0].Temperature >= g.Linear[1].Temperature)
                throw new InvalidDataException(L10n.T("Linear curve requires two limits; the first temperature must be lower."));
    }
}

public sealed record SensorReadings(double? Cpu, double? Gpu, double? Coolant)
{
    public double? For(CoolingSource source) => source switch { CoolingSource.Cpu => Cpu, CoolingSource.Gpu => Gpu, CoolingSource.Coolant => Coolant, _ => null };
}

/// <summary>Speed calculation: smoothing, curve, hysteresis on decrease, lower limit, failsafe speed.</summary>
public sealed class CoolingEngine
{
    public const long SensorTimeoutMs = 10_000;
    public sealed class State
    {
        public double? Smoothed, Percent, TempAtChange;
        public long? LastSeen;
        public bool Failsafe;
    }
    readonly Dictionary<string, State> states = [];
    public IReadOnlyDictionary<string, State> States => states;

    public static double Interpolate(IReadOnlyList<CurvePoint> curve, double temperature)
    {
        var points = curve.OrderBy(p => p.Temperature).ToArray();
        if (points.Length == 0) return 100;
        if (temperature <= points[0].Temperature) return points[0].Percent;
        for (int i = 1; i < points.Length; i++)
            if (temperature <= points[i].Temperature)
            {
                var (a, b) = (points[i - 1], points[i]);
                double t = b.Temperature == a.Temperature ? 1 : (temperature - a.Temperature) / (b.Temperature - a.Temperature);
                return a.Percent + (b.Percent - a.Percent) * t;
            }
        return points[^1].Percent;
    }

    /// <summary>Targets in percent per MAC. A group without a target is not controlled (hardware speed).</summary>
    public Dictionary<string, double> Compute(CoolingSettings settings, SensorReadings readings, long now)
    {
        var targets = new Dictionary<string, double>();
        foreach (var group in settings.Groups)
        {
            if (!states.TryGetValue(group.Mac, out var state)) states[group.Mac] = state = new State();
            if (group.Mac == CoolingSettings.PumpKey) continue; // the pump only gets a constant RPM, never temperature-based
            double floor = Math.Max(14, settings.MinPercent);
            switch (group.Source)
            {
                case CoolingSource.Off: states[group.Mac] = new State(); continue;
                case CoolingSource.Fixed:
                    double fixedPercent = Math.Clamp(Math.Max(floor, group.FixedPercent), 0, 100);
                    targets[group.Mac] = fixedPercent; state.Percent = fixedPercent; state.Failsafe = false; continue;
            }
            if (readings.For(group.Source) is not double temperature || !double.IsFinite(temperature) || temperature is < -20 or > 150)
            {
                // Sensor never seen: do nothing. Lost recently: keep the previous speed; lost for over 10 s: failsafe speed.
                if (state.LastSeen is not long seen) continue;
                if (now - seen > SensorTimeoutMs) { state.Failsafe = true; state.Percent = Math.Max(floor, settings.FailsafePercent); }
                if (state.Percent is double held) targets[group.Mac] = held;
                continue;
            }
            bool recovered = state.Failsafe;
            state.Failsafe = false; state.LastSeen = now;
            state.Smoothed = state.Smoothed is double previous && !recovered ? previous + .3 * (temperature - previous) : temperature;
            double percent = Math.Clamp(Math.Max(floor, Interpolate(group.Active, state.Smoothed.Value)), 0, 100);
            // Increase immediately; decrease only after the temperature drops by the hysteresis.
            if (!recovered && state.Percent is double last && percent < last && state.TempAtChange is double at && at - state.Smoothed.Value < settings.Hysteresis)
                percent = last;
            if (state.Percent is not double current || Math.Abs(current - percent) > .01) state.TempAtChange = state.Smoothed;
            targets[group.Mac] = percent; state.Percent = percent;
        }
        return targets;
    }
}

/// <summary>NVIDIA GPU temperature through the driver's NVML (nvml.dll), no administrator rights needed.</summary>
public static class GpuSensor
{
    [DllImport("nvml.dll")] static extern int nvmlInit_v2();
    [DllImport("nvml.dll")] static extern int nvmlDeviceGetHandleByIndex_v2(uint index, out IntPtr device);
    [DllImport("nvml.dll")] static extern int nvmlDeviceGetTemperature(IntPtr device, int sensor, out uint temperature);
    static IntPtr device;
    static int state; // 0 — not opened, 1 ready, -1 unavailable
    public static double? Read()
    {
        try
        {
            if (state == 0) state = nvmlInit_v2() == 0 && nvmlDeviceGetHandleByIndex_v2(0, out device) == 0 ? 1 : -1;
            return state == 1 && nvmlDeviceGetTemperature(device, 0, out uint t) == 0 ? t : null;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException) { state = -1; return null; }
    }
}

/// <summary>
/// CPU temperature. Windows does not expose it to regular programs: a separate process of this same app
/// running elevated (UAC prompt) reads Tctl/Tdie through LibreHardwareMonitor and the driver PawnIO
/// and writes it to a file once per second; the main window stays unelevated.
/// </summary>
public static class CpuSensor
{
    public static string FilePath => Path.Combine(Log.DirectoryPath, "cpu-sensor.json");
    public sealed record Sample(double Cpu, string Sensor, DateTimeOffset Time);

    public static double? Read(string? path = null, DateTimeOffset? now = null)
    {
        try
        {
            path ??= FilePath;
            if (!File.Exists(path)) return null;
            var sample = JsonSerializer.Deserialize<Sample>(File.ReadAllText(path));
            return sample != null && (now ?? DateTimeOffset.UtcNow) - sample.Time < TimeSpan.FromSeconds(5) && double.IsFinite(sample.Cpu) ? sample.Cpu : null;
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }

    public static void StartElevated()
    {
        string exe = Environment.ProcessPath ?? throw new InvalidOperationException(L10n.T("Application executable not found."));
        if (Path.GetFileNameWithoutExtension(exe).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException(L10n.T("CPU sensor can only start from the built application."));
        try
        {
            Process.Start(new ProcessStartInfo(exe, $"--cpu-sensor \"{FilePath}\" {Environment.ProcessId}") { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden })?.Dispose();
        }
        catch (System.ComponentModel.Win32Exception e) when (e.NativeErrorCode == 1223) { throw new OperationCanceledException(L10n.T("CPU sensor startup cancelled.")); }
    }

    /// <summary>Sensor mode (--cpu-sensor path pid): runs while the main window process is alive.</summary>
    public static int Host(string path, int parent)
    {
        var computer = new LibreHardwareMonitor.Hardware.Computer { IsCpuEnabled = true };
        computer.Open();
        try
        {
        while (Alive(parent))
        {
            foreach (var hardware in computer.Hardware.Where(h => h.HardwareType == LibreHardwareMonitor.Hardware.HardwareType.Cpu))
            {
                hardware.Update();
                var sensors = hardware.Sensors.Where(s => s.SensorType == LibreHardwareMonitor.Hardware.SensorType.Temperature && s.Value is > 0 and < 150).ToArray();
                var best = sensors.FirstOrDefault(s => s.Name.Contains("Tctl") || s.Name.Contains("Tdie")) ?? sensors.FirstOrDefault(s => s.Name.Contains("Package")) ?? sensors.FirstOrDefault();
                if (best?.Value is float value)
                {
                    File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(new Sample(Math.Round(value, 1), best.Name, DateTimeOffset.UtcNow)));
                    File.Move(path + ".tmp", path, true);
                }
            }
            Thread.Sleep(1000);
        }
        }
        finally { computer.Close(); }
        try { File.Delete(path); } catch (IOException) { }
        return 0;
    }

    static bool Alive(int pid)
    {
        try { using var process = Process.GetProcessById(pid); return !process.HasExited; }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
    }
}
