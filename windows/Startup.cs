using Microsoft.Win32;
using System.Diagnostics;
using System.Security;
using System.Security.Principal;
using System.Text;

namespace WirelessCanvas;

/// <summary>
/// Administrator rights with a single prompt at launch. The CPU sensor (PawnIO) and OpenRGB (memory SMBus)
/// then start without their own UAC prompts: an elevated process passes its rights to its children.
/// </summary>
public static class Elevation
{
    public const string MutexName = "Local\\WirelessCanvasRGB.Lighting";
    const string LegacyMutexName = @"Local\LianLi.Windows.Lighting";
    public static readonly string[] ProcessNames = ["WirelessCanvasRGB", "LianLi.Windows"];
    public static bool IsAdministrator
    {
        get { using var identity = WindowsIdentity.GetCurrent(); return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator); }
    }
    /// <summary>The app is already open (an elevated copy makes the mutex visible but inaccessible).</summary>
    public static bool AlreadyRunning()
    {
        try { foreach (string name in new[] { MutexName, LegacyMutexName }) if (Mutex.TryOpenExisting(name, out var existing)) { existing.Dispose(); return true; } return false; }
        catch (UnauthorizedAccessException) { return true; }
    }
    /// <summary>Another copy of this app (including a release made before the rename) is running.</summary>
    public static bool OtherInstanceRunning() => ProcessNames.SelectMany(Process.GetProcessesByName).Any(p => { using (p) return p.Id != Environment.ProcessId; });
    /// <summary>
    /// Restarts this exe as administrator (one UAC prompt) with the same arguments.
    /// false: running under dotnet (development) or the user declined, so the app keeps running unelevated.
    /// </summary>
    public static bool Relaunch(string[] args)
    {
        string? exe = Environment.ProcessPath;
        if (exe == null || Path.GetFileNameWithoutExtension(exe).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) return false;
        try
        {
            Process.Start(new ProcessStartInfo(exe, string.Join(" ", args.Select(a => a.Contains(' ') ? $"\"{a}\"" : a))) { UseShellExecute = true, Verb = "runas", WorkingDirectory = Path.GetDirectoryName(exe)! })?.Dispose();
            return true;
        }
        catch (System.ComponentModel.Win32Exception e) when (e.NativeErrorCode == 1223)
        {
            Log.Write(L10n.T("Administrator rights not granted; CPU sensor and OpenRGB will request them separately."));
            return false;
        }
    }
}

/// <summary>
/// Start with Windows. When elevated, a "WirelessCanvasRGB" Task Scheduler task with highest privileges
/// starts the app with administrator rights and no UAC prompt at each sign-in.
/// Without rights, the registry Run key is used (Windows then asks for permission at sign-in).
/// </summary>
public static class AutoStart
{
    public const string TaskName = "WirelessCanvasRGB";
    /// <summary>Task and Run value used by releases up to 0.13, before the rename.</summary>
    public const string LegacyTaskName = "LianLi.Windows";
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public static bool RunKeyEnabled { get { using var run = Registry.CurrentUser.OpenSubKey(RunKey); return run?.GetValue(TaskName) != null || run?.GetValue(LegacyTaskName) != null; } }
    public static bool TaskEnabled => Schtasks($"/Query /TN \"{TaskName}\"").Code == 0;
    static bool LegacyTaskEnabled => Schtasks($"/Query /TN \"{LegacyTaskName}\"").Code == 0;
    public static bool Enabled => RunKeyEnabled || TaskEnabled || LegacyTaskEnabled;

    /// <summary>
    /// Task: current user sign-in, 10 s delay (so USB devices appear), highest privileges, no run-time limit
    /// (Task Scheduler stops tasks after 72 hours by default), normal priority, no battery conditions,
    /// single instance.
    /// </summary>
    public static string TaskXml(string exe, string user)
    {
        string X(string value) => SecurityElement.Escape(value);
        return $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo><Description>WirelessCanvas RGB: lighting, cooling and screens at Windows sign-in</Description></RegistrationInfo>
              <Triggers><LogonTrigger><Enabled>true</Enabled><UserId>{X(user)}</UserId><Delay>PT10S</Delay></LogonTrigger></Triggers>
              <Principals><Principal id="Author"><UserId>{X(user)}</UserId><LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal></Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <AllowHardTerminate>false</AllowHardTerminate>
                <StartWhenAvailable>false</StartWhenAvailable>
                <IdleSettings><StopOnIdleEnd>false</StopOnIdleEnd><RestartOnIdle>false</RestartOnIdle></IdleSettings>
                <AllowStartOnDemand>true</AllowStartOnDemand>
                <Enabled>true</Enabled>
                <Hidden>false</Hidden>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Priority>4</Priority>
              </Settings>
              <Actions Context="Author"><Exec><Command>{X(exe)}</Command><Arguments>--startup</Arguments><WorkingDirectory>{X(Path.GetDirectoryName(exe)!)}</WorkingDirectory></Exec></Actions>
            </Task>
            """;
    }

    /// <summary>Enables start with Windows and returns how it was done.</summary>
    public static string Enable()
    {
        string exe = Environment.ProcessPath ?? throw new InvalidOperationException(L10n.T("Application executable not found."));
        if (Path.GetFileNameWithoutExtension(exe).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException(L10n.T("Startup can only be enabled from the built application."));
        if (Elevation.IsAdministrator)
        {
            string file = Path.Combine(Path.GetTempPath(), $"wcrgb-task-{Guid.NewGuid():N}.xml");
            try
            {
                using (var identity = WindowsIdentity.GetCurrent()) File.WriteAllText(file, TaskXml(exe, identity.Name), Encoding.Unicode);
                var (code, output) = Schtasks($"/Create /TN \"{TaskName}\" /XML \"{file}\" /F");
                if (code != 0) throw new IOException(L10n.T("Task Scheduler could not create startup task: ") + output.Trim());
            }
            finally { File.Delete(file); }
            using (var run = Registry.CurrentUser.CreateSubKey(RunKey)) { run.DeleteValue(TaskName, false); run.DeleteValue(LegacyTaskName, false); }
            RemoveLegacyTask();
            Log.Write(L10n.T("Startup: scheduled task with highest privileges."));
            return L10n.T("Startup enabled: launches as administrator at Windows sign-in without a UAC prompt.");
        }
        using (var run = Registry.CurrentUser.CreateSubKey(RunKey)) { run.DeleteValue(LegacyTaskName, false); run.SetValue(TaskName, $"\"{exe}\" --startup"); }
        return L10n.T("Startup enabled without administrator rights: Windows asks permission at sign-in. Run as administrator and enable startup again to avoid this.");
    }

    public static void Disable()
    {
        using (var run = Registry.CurrentUser.CreateSubKey(RunKey)) { run.DeleteValue(TaskName, false); run.DeleteValue(LegacyTaskName, false); }
        RemoveLegacyTask();
        if (!TaskEnabled) return;
        var (code, output) = Schtasks($"/Delete /TN \"{TaskName}\" /F");
        if (code != 0) throw new IOException(L10n.T("Startup task could not be removed (administrator rights required): ") + output.Trim());
    }

    /// <summary>Removes the task created by releases made before the rename (it points to LianLi.Windows.exe).</summary>
    static void RemoveLegacyTask()
    {
        if (LegacyTaskEnabled) Schtasks($"/Delete /TN \"{LegacyTaskName}\" /F");
    }

    /// <summary>
    /// When elevated, replaces the Run key (UAC prompt at every sign-in) and the pre-rename task with this app's task.
    /// </summary>
    public static string? Upgrade()
    {
        if (!Elevation.IsAdministrator) return null;
        try
        {
            if (TaskEnabled)
            {
                var task = Schtasks($"/Query /TN \"{TaskName}\" /XML");
                if (task.Code == 0 && Environment.ProcessPath is { } exe && NeedsPathUpdate(task.Output, exe)) return Enable();
                return null;
            }
            if (LegacyTaskEnabled) return Enable();
            if (!RunKeyEnabled) return null;
            Enable(); return L10n.T("Startup migrated to Task Scheduler; no UAC prompt at sign-in.");
        }
        catch (Exception e) { Log.Error(e); return null; }
    }

    /// <summary>Update only this app's own task when the user enabled start with Windows in an older release.</summary>
    public static bool NeedsPathUpdate(string xml, string currentExe)
    {
        try
        {
            var doc = System.Xml.Linq.XDocument.Parse(xml);
            System.Xml.Linq.XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
            var actions = doc.Root?.Element(ns + "Actions")?.Elements(ns + "Exec").ToArray();
            string? command = actions is { Length: 1 } ? actions[0].Element(ns + "Command")?.Value : null;
            bool Ours(string path) => Elevation.ProcessNames.Any(name => Path.GetFileName(path).Equals(name + ".exe", StringComparison.OrdinalIgnoreCase));
            return command != null && Ours(command) && Ours(currentExe) &&
                !Path.GetFullPath(command).Equals(Path.GetFullPath(currentExe), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception e) when (e is System.Xml.XmlException or ArgumentException or NotSupportedException) { return false; }
    }

    static (int Code, string Output) Schtasks(string arguments)
    {
        using var process = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "schtasks.exe"), arguments)
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true })!;
        string output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        if (!process.WaitForExit(15_000)) { process.Kill(); return (-1, L10n.T("no reply")); }
        return (process.ExitCode, output);
    }
}
