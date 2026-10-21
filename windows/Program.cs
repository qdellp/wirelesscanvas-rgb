using System.Text.Json;

namespace WirelessCanvas;

internal static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        try
        {
            L10n.Initialize(args.Contains("--self-test") ? "en" : AppPreferences.Load().Language);
            int languageArgument = Array.IndexOf(args, "--language");
            if (languageArgument >= 0 && languageArgument + 1 < args.Length) L10n.Initialize(args[languageArgument + 1]);
            if (args.Length == 3 && args[0] == "--cpu-sensor" && int.TryParse(args[2], out int parent))
                return CpuSensor.Host(args[1], parent); // separate elevated process without a window
            if (args.Contains("--self-test"))
            {
                ApplicationConfiguration.Initialize();
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
                return SelfTests.Run();
            }
            if (args.Contains("--export-case"))
            {
                string saved = Path.Combine(Log.DirectoryPath, "last-profile.json");
                var p = File.Exists(saved) ? Profile.Load(saved) : new Profile();
                if (p.Fans.Count == 0) CaseLayout.Populate(p);
                if (!CaseLayout.Apply(p)) throw new InvalidDataException(L10n.T("Saved profile does not match the case groups."));
                p.Save(args.SkipWhile(x => x != "--export-case").Skip(1).First()); return 0;
            }
            if (args.Contains("--probe-hydro"))
            {
                using var pump = new HydroController();
                string result = pump.Probe();
                File.WriteAllText(args.SkipWhile(x => x != "--probe-hydro").Skip(1).First(), result); return 0;
            }
            if (args.Contains("--probe-hydro-params"))
            {
                using var pump = new HydroController();
                File.WriteAllText(args.SkipWhile(x => x != "--probe-hydro-params").Skip(1).First(), pump.ProbeParameters()); return 0;
            }
            if (args.Contains("--probe-lcd"))
            {
                // Only GetVer on each screen: panel image and settings are not changed.
                File.WriteAllText(args.SkipWhile(x => x != "--probe-lcd").Skip(1).First(), ScreenInventory.Probe()); return 0;
            }
            if (args.Contains("--repair-radio-slot"))
            {
                if (Elevation.OtherInstanceRunning())
                    throw new IOException(L10n.T("Close other WirelessCanvas RGB instances before radio diagnostics."));
                var repair = args.SkipWhile(x => x != "--repair-radio-slot").Skip(1).ToArray();
                if (repair.Length is < 2 or > 3) throw new ArgumentException("--repair-radio-slot MAC report.json [preferred-slot]");
                using var radio = new Controller(); radio.Connect(); var before = radio.Groups.ToArray();
                string result = radio.RepairRadioSlot(repair[0].ToUpperInvariant(), repair.Length > 2 ? byte.Parse(repair[2]) : (byte)0);
                File.WriteAllText(repair[1], JsonSerializer.Serialize(new { Result = result, Before = before, After = radio.Groups }, Profile.Json)); return 0;
            }
            if (args.Contains("--probe-radio"))
            {
                if (Elevation.OtherInstanceRunning())
                    throw new IOException(L10n.T("Close other WirelessCanvas RGB instances before radio diagnostics."));
                using var radio = new Controller(); radio.Connect();
                File.WriteAllText(args.SkipWhile(x => x != "--probe-radio").Skip(1).First(), JsonSerializer.Serialize(new { radio.BoundHydroMacs, radio.Groups }, Profile.Json)); return 0;
            }
            if (args.Contains("--probe-openrgb"))
            {
                // Reads device descriptions only: modes and colors are not changed.
                using var client = new OpenRgbClient();
                File.WriteAllText(args.SkipWhile(x => x != "--probe-openrgb").Skip(1).First(), JsonSerializer.Serialize(new { client.Protocol, Devices = client.Devices() }, Profile.Json)); return 0;
            }
            if (args.Contains("--openrgb-color"))
            {
                // Diagnostics: device N is switched to Direct and gets one #RRGGBB color on all LED.
                var rest = args.SkipWhile(x => x != "--openrgb-color").Skip(1).ToArray();
                int index = int.Parse(rest[0]); byte[] rgb = Convert.FromHexString(rest[1].TrimStart('#'));
                using var client = new OpenRgbClient();
                var device = client.Devices().Single(d => d.Index == index);
                // Optional third argument: name of a per-LED color mode (for example, Static).
                int mode = rest.Length > 2 ? Array.FindIndex(device.Modes, m => m.Name == rest[2] && m.PerLed) : -1;
                if (rest.Length > 2 && mode < 0) throw new InvalidDataException(L10n.F((FormattableString)$"Device has no {rest[2]} mode with per-LED colors."));
                if (mode >= 0) client.SetMode(index, mode, device.Modes[mode]); else client.SetCustomMode(index);
                client.UpdateLeds(index, Enumerable.Range(0, device.LedCount).SelectMany(_ => rgb).ToArray());
                Thread.Sleep(300); return 0;
            }
            if (args.Contains("--apply-hydro"))
            {
                var paths = args.SkipWhile(x => x != "--apply-hydro").Skip(1).ToArray();
                var p = Profile.Load(paths[0]);
                using var pump = new HydroController(); pump.Connect();
                var scene = RgbScene.Prepare(p, [], includePump: true);
                File.WriteAllText(paths[1], pump.Apply(scene.Pump!)); return 0;
            }
            if (args.Contains("--render-layers"))
            {
                ApplicationConfiguration.Initialize();
                using var form = new LayerEditor(new Profile());
                RenderForm(form, args.SkipWhile(x => x != "--render-layers").Skip(1).First()); return 0;
            }
            if (args.Contains("--render-openrgb"))
            {
                ApplicationConfiguration.Initialize();
                var rest = args.SkipWhile(x => x != "--render-openrgb").Skip(1).ToArray();
                var p = rest.Length > 1 ? Profile.Load(rest[1]) : new Profile();
                using var form = new MainForm(false, p);
                form.ShowTab(2); RenderForm(form, rest[0]); return 0;
            }
            if (args.Contains("--render-pump"))
            {
                ApplicationConfiguration.Initialize(); using var form = new PumpEditor(new PumpPlacement(), L10n.T("HydroShift II LCD-C · preview"));
                RenderForm(form, args.SkipWhile(x => x != "--render-pump").Skip(1).First()); return 0;
            }
            if (args.Contains("--render-editor") || args.Contains("--render-picker"))
            {
                ApplicationConfiguration.Initialize();
                bool picker = args.Contains("--render-picker");
                var p = new Profile { Palette = PaletteModel.Rainbow().Serialize(), ColorSpace = ColorSpace.Hsv };
                using Form form = picker ? new ColorPickerDialog(Color.FromArgb(180, 35, 255)) : new GradientEditor(p);
                RenderForm(form, args.SkipWhile(x => x != (picker ? "--render-picker" : "--render-editor")).Skip(1).First());
                return 0;
            }
            if (args.Contains("--render-preview"))
            {
                ApplicationConfiguration.Initialize();
                var rest = args.SkipWhile(x => x != "--render-preview").Skip(1).ToArray();
                using var form = new MainForm(false, rest.Length > 1 ? Profile.Load(rest[1]) : null);
                // Third argument: tab index (0 mapping, 1 fans, 2 — OpenRGB).
                if (rest.Length > 2) form.ShowTab(int.Parse(rest[2]));
                // Fourth argument: the Cooling row whose curve to show (group MAC or pump).
                if (rest.Length > 3) form.ShowCoolingCurve(rest[3]);
                RenderForm(form, rest[0]); return 0;
            }
            if (args.Contains("--diagnose"))
            {
                var target = args.SkipWhile(x => x != "--diagnose").Skip(1).FirstOrDefault() ?? "diagnostics.json";
                File.WriteAllText(target, JsonSerializer.Serialize(UsbInventory.Scan(), Profile.Json));
                return 0;
            }
            if (Elevation.AlreadyRunning()) { MessageBox.Show(L10n.T("Application is already running.")); return 1; }
            // One UAC prompt at launch: when elevated, the CPU sensor and OpenRGB start without prompts of their own.
            if (!Elevation.IsAdministrator && !args.Contains("--no-elevate") && Elevation.Relaunch(args)) return 0;
            using var mutex = new Mutex(true, Elevation.MutexName, out bool owner);
            if (!owner) { MessageBox.Show(L10n.T("Application is already running.")); return 1; }
            ApplicationConfiguration.Initialize();
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (_, e) => { Log.Error(e.Exception); MessageBox.Show(e.Exception.Message, L10n.T("Error")); };
            MainForm.HardwareOutput = true;
            Application.Run(new MainForm(args.Contains("--startup")));
            return 0;
        }
        catch (Exception e)
        {
            Log.Error(e);
            if (args.Length == 0) MessageBox.Show(e.Message, "WirelessCanvas RGB");
            return 1;
        }
    }
    static void RenderForm(Form form, string path)
    {
        form.ShowInTaskbar = false; form.StartPosition = FormStartPosition.Manual; form.Location = new Point(-30000, -30000);
        form.Show(); Application.DoEvents();
        using var bitmap = new Bitmap(form.Width, form.Height); form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
        bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png); form.Close();
    }
}

internal static class Log
{
    static readonly object Gate = new();
    static readonly Lazy<string> DataDirectory = new(() =>
    {
        string data = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string current = Path.Combine(data, "WirelessCanvasRGB"), legacy = Path.Combine(data, "LianLi.Windows");
        // Releases up to 0.13 were called LianLi.Windows: their profile and settings are copied once, on the first normal
        // run (not on self-tests or renders, which would copy a profile the old release may still be changing).
        bool normalRun = Environment.GetCommandLineArgs().Skip(1).All(a => a == "--startup");
        try { if (normalRun) ImportLegacy(legacy, current); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return current;
    });
    public static string DirectoryPath => DataDirectory.Value;
    static void ImportLegacy(string from, string to)
    {
        string marker = Path.Combine(to, "imported-from-LianLi.Windows");
        if (!Directory.Exists(from) || File.Exists(marker)) return;
        Directory.CreateDirectory(to);
        // Never overwrite: anything this release already saved wins over the old copy.
        foreach (string name in new[] { "last-profile.json", "settings.json" })
            if (File.Exists(Path.Combine(from, name)) && !File.Exists(Path.Combine(to, name)))
                File.Copy(Path.Combine(from, name), Path.Combine(to, name));
        File.WriteAllText(marker, DateTimeOffset.Now.ToString("O"));
    }
    public static void Write(string message)
    {
        lock (Gate)
        {
            Directory.CreateDirectory(DirectoryPath);
            string file = Path.Combine(DirectoryPath, "application.log");
            if (File.Exists(file) && new FileInfo(file).Length > 2_000_000) File.Move(file, file + ".old", true);
            File.AppendAllText(file, $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}");
        }
    }
    public static void Error(Exception e) => Write(e.ToString());
}
