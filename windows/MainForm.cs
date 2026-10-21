using Microsoft.Win32;
using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;

namespace WirelessCanvas;

public sealed class MainForm : Form
{
    readonly Controller controller = new();
    readonly HydroController pump = new();
    OpenRgbServer? sdk;
    OpenRgbBridge? bridge;
    Profile profile = new();
    readonly BindingList<FanPlacement> placements = [];
    readonly DataGridView grid = new() { Dock = DockStyle.Fill, AutoGenerateColumns = false, AllowUserToAddRows = false, AllowUserToDeleteRows = false, RowHeadersVisible = false, BackgroundColor = Color.FromArgb(26, 30, 40) };
    readonly Canvas canvas;
    readonly FansPanel fans;
    readonly OpenRgbPanel openRgbPanel;
    readonly KeyboardPanel keyboardPanel;
    readonly CoolingPanel coolingPanel;
    readonly EffectBar effects;
    readonly ScreensPanel screensPanel;
    // Screens get frames only in a normal run: self-tests and window renders never open screen USB.
    readonly ScreenHub? screens;
    readonly CoolingEngine coolingEngine = new();
    SensorReadings readings = new(null, null, null);
    // Temperatures for 5 minutes for the Chart screen theme.
    readonly SensorHistory history = new();
    long nextCooling;
    readonly Panel[] pages;
    readonly SegmentedTabs sections;
    readonly TableLayoutPanel mapping;
    // Bottom lines have a fixed height: text changes do not relayout the window (otherwise the bottom flickers).
    readonly Label status = new() { AutoSize = false, Dock = DockStyle.Fill, AutoEllipsis = true, Margin = Padding.Empty };
    readonly Label mode = new() { Text = L10n.T("PREVIEW • USB not open"), AutoSize = false, Dock = DockStyle.Fill, AutoEllipsis = true, ForeColor = Color.LightSkyBlue, Margin = Padding.Empty };
    readonly ToolTip hints = new();
    readonly GradientStrip palette = new() { Width = 310, Height = 28, Compact = true, Margin = new Padding(0, 6, 8, 4) };
    readonly NumericUpDown period = Number(1, 60, 6, 1), wavelength = Number(20, 5000, 600), direction = Number(-360, 360, 0), brightness = Number(0, 100, 50), fps = Number(1, 60, 30), gamma = Number(1, 3, 2.2m, 1);
    readonly CheckBox openRgb = new() { Text = L10n.T("Receive OpenRGB colors · 127.0.0.1:6743"), AutoSize = true };
    readonly CheckBox startup = new() { Text = L10n.T("Start at Windows sign-in (administrator, no UAC prompt)"), AutoSize = true };
    readonly System.Windows.Forms.Timer timer = new() { Interval = 33 };
    readonly Stopwatch clock = Stopwatch.StartNew();
    readonly NotifyIcon tray = new() { Icon = SystemIcons.Application, Text = "WirelessCanvas RGB · " + L10n.T("lighting") };
    bool busy, closing, populating, syncingGrid;
    string lastStatus = "";
    string? selectedGroup;
    long nextPanelTick;
    readonly bool startupLaunch;
    readonly AppPreferences preferences;
    readonly CancellationTokenSource lifetime = new();
    public static bool HardwareOutput { get; set; }
    static string LastProfile => Path.Combine(Log.DirectoryPath, "last-profile.json");
    public static string[] TabTitles => [L10n.T("Mapping"), L10n.T("Lian Li fans"), "OpenRGB", L10n.T("Keyboard"), L10n.T("Cooling"), L10n.T("Screens"), L10n.T("Settings")];

    public MainForm(bool startupLaunch, Profile? initialProfile = null)
    {
        this.startupLaunch = startupLaunch;
        controller.AutoRepairConflicts = HardwareOutput;
        preferences = HardwareOutput ? AppPreferences.Load() : new();
        Text = $"WirelessCanvas RGB {Application.ProductVersion.Split('+')[0]} · " + L10n.T("lighting, cooling and screens");
        direction.Name = "motionAngle";
        Size = new Size(1280, 900); MinimumSize = new Size(1180, 860); StartPosition = FormStartPosition.CenterScreen;
        BackColor = Ui.Background; ForeColor = Ui.Text; Font = new Font("Segoe UI", 10);
        if (initialProfile != null) profile = initialProfile;
        else if (File.Exists(LastProfile))
        {
            try { profile = Profile.Load(LastProfile); } catch (Exception e) { Log.Error(e); status.Text = L10n.T("Saved profile could not be loaded: ") + e.Message; }
        }
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(20, 14, 20, 12), ColumnCount = 1, RowCount = 5 };
        root.RowStyles.Add(new(SizeType.AutoSize)); root.RowStyles.Add(new(SizeType.AutoSize)); root.RowStyles.Add(new(SizeType.Percent, 100));
        root.RowStyles.Add(new(SizeType.Absolute, 24)); root.RowStyles.Add(new(SizeType.Absolute, 44));
        Controls.Add(root);
        // The full text of a truncated line is in the tooltip.
        mode.TextChanged += (_, _) => hints.SetToolTip(mode, mode.Text);
        status.TextChanged += (_, _) => hints.SetToolTip(status, status.Text);
        // Header: title and the main section tabs.
        var header = Flow();
        header.Controls.Add(new Label { Text = "WIRELESSCANVAS RGB", AutoSize = true, Font = new Font(Font, FontStyle.Bold), Margin = new Padding(0, 10, 24, 0) });
        sections = new SegmentedTabs { Name = "sections", Items = TabTitles, AutoSize = true };
        sections.SelectedChanged += ShowTab;
        header.Controls.Add(sections);
        root.Controls.Add(header, 0, 0);
        var actions = Flow();
        actions.Controls.Add(Button(L10n.T("Connect USB"), async () => await Run(ConnectDevices)));
        actions.Controls.Add(Button(L10n.T("Apply gradient"), async () => await Apply()));
        actions.Controls.Add(Button(L10n.T("Save profile"), SaveProfile));
        actions.Controls.Add(Button(L10n.T("Open profile"), LoadProfile));
        actions.Controls.Add(Button(L10n.T("USB diagnostics"), Diagnose));
        actions.Controls.Add(Button(L10n.T("Minimize to tray"), () => { tray.Visible = true; Hide(); }));
        root.Controls.Add(actions, 0, 1);

        // Mapping tab: gradient, canvas, coordinates.
        mapping = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Margin = Padding.Empty };
        mapping.RowStyles.Add(new(SizeType.AutoSize)); mapping.RowStyles.Add(new(SizeType.Percent, 100)); mapping.RowStyles.Add(new(SizeType.Absolute, 0)); mapping.RowStyles.Add(new(SizeType.AutoSize));
        var settings = Flow();
        settings.Controls.Add(palette); settings.Controls.Add(Button(L10n.T("Gradient editor…"), EditGradient));
        settings.Controls.Add(Button(L10n.T("Lighting layers…"), EditLayers));
        settings.Controls.Add(Button(L10n.T("Pump…"), EditPump));
        settings.Controls.Add(Button(L10n.T("Case photo layout"), ApplyCaseLayout));
        settings.Controls.Add(Button(L10n.T("Coordinates"), ToggleCoordinates));
        settings.SetFlowBreak(settings.Controls[settings.Controls.Count - 1], true);
        palette.Click += (_, _) => { if (!busy) EditGradient(); };
        AddNumber(settings, L10n.T("Cycle, s"), period); AddNumber(settings, L10n.T("Length, mm"), wavelength); AddNumber(settings, L10n.T("Tilt, °"), direction); AddNumber(settings, L10n.T("Brightness, %"), brightness); AddNumber(settings, "FPS", fps); AddNumber(settings, L10n.T("LED gamma"), gamma);
        // Effect instead of the plain gradient: shaders with their own palettes and parameters.
        settings.SetFlowBreak(gamma, true);
        effects = new EffectBar(() => profile, EffectChanged);
        settings.Controls.Add(effects);
        settings.SizeChanged += (_, _) => effects.MaximumSize = new Size(Math.Max(600, settings.ClientSize.Width - 8), 0);
        mapping.Controls.Add(settings, 0, 0);
        canvas = new Canvas(() => profile, SceneSeconds, () => { grid.Refresh(); }) { Dock = DockStyle.Fill };
        canvas.FanSelected += fan => SelectGroup(fan.Group);
        canvas.DirectionChanged += angle => direction.Value = (decimal)angle;
        canvas.PumpDoubleClick += EditPump;
        canvas.ElementMenu += ShowElementMenu;
        mapping.Controls.Add(canvas, 0, 1);
        foreach (var (name, title, width) in new[] { ("Enabled", L10n.T("On"), 50), ("GroupLabel", L10n.T("Group / name"), 195), ("Group", L10n.T("Group MAC"), 185), ("FanNumber", L10n.T("Fan no."), 115), ("X", L10n.T("X, mm"), 100), ("Y", L10n.T("Y, mm"), 100), ("Rotation", L10n.T("Rotation, °"), 115), ("Elevation", L10n.T("View: 0° side / 90° front"), 170), ("Scale", L10n.T("Scale"), 100), ("ProjectionY", L10n.T("Y projection"), 105) })
        {
            DataGridViewColumn column = name == "Enabled" ? new DataGridViewCheckBoxColumn() : new DataGridViewTextBoxColumn();
            column.DataPropertyName = name; column.HeaderText = title; column.Width = width; column.ReadOnly = name is "Group" or "GroupLabel" or "FanNumber"; grid.Columns.Add(column);
        }
        Ui.StyleGrid(grid);
        grid.DataSource = placements; grid.DataError += (_, e) => { e.ThrowException = false; status.Text = L10n.T("Enter a valid number in the table."); };
        grid.CellValidating += (_, e) =>
        {
            string name = grid.Columns[e.ColumnIndex].DataPropertyName;
            if (name is not ("X" or "Y" or "Rotation" or "ProjectionY" or "Scale" or "Elevation")) return;
            (double min, double max) = name switch { "ProjectionY" => (.2, 2), "Scale" => (.2, 4), "Elevation" => (0, 90), "Rotation" => (-360, 360), _ => (-10000, 10000) };
            bool valid = double.TryParse(e.FormattedValue?.ToString(), out double value) && double.IsFinite(value) && value >= min && value <= max;
            e.Cancel = !valid; grid.Rows[e.RowIndex].ErrorText = valid ? "" : L10n.F((FormattableString)$"Enter a number from {min} to {max}.");
        };
        grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        grid.CurrentCellChanged += (_, _) => { if (!populating && !syncingGrid && grid.CurrentRow?.DataBoundItem is FanPlacement fan) SelectGroup(fan.Group); };
        // When shown, the grid selects its first row by itself; that is not a user choice.
        grid.VisibleChanged += (_, _) =>
        {
            if (!grid.Visible || !IsHandleCreated) { SyncGridSelection(); return; }
            syncingGrid = true;
            BeginInvoke(() => { mapping.PerformLayout(); grid.PerformLayout(); syncingGrid = false; SyncGridSelection(); });
        };
        grid.SizeChanged += (_, _) => SyncGridSelection();
        mapping.Controls.Add(grid, 0, 2);
        grid.Visible = false;
        var options = Flow(); options.Controls.Add(openRgb);
        options.Controls.Add(new Label { Text = L10n.T("Drag to reposition. Right-click for custom lighting or off. Click Apply gradient to update fans."), AutoSize = true, Margin = new Padding(0, 4, 0, 0) });
        options.Controls.Add(new Label { Text = L10n.T("Shift + click adds or removes a fan; drag a box to select; double-click selects the whole group; drag to move; drag frame corners to resize."), AutoSize = true, Margin = new Padding(0, 4, 0, 0) });
        mapping.Controls.Add(options, 0, 3);

        fans = new FansPanel(() => profile, () => controller.Connected ? controller.Groups : [], PumpLink, mac => _ = IdentifyGroup(mac), ReverseGroup,
            () =>
            {
                // Names and lighting: the canvas, the coordinate table and Cooling show new values immediately.
                foreach (var fan in profile.Fans) fan.GroupLabel = profile.GroupTitle(fan.Group);
                canvas.Invalidate(); grid.Refresh(); coolingPanel?.RefreshNames();
            });
        fans.GroupSelected += mac => { if (mac != null) SelectGroup(mac, fromFans: true); };
        openRgbPanel = new OpenRgbPanel(() => profile, () => bridge, SetBridge, canvas.Invalidate);
        keyboardPanel = new KeyboardPanel(() => profile, () => bridge, SceneSeconds, () => bridge?.LocalSeconds ?? clock.Elapsed.TotalSeconds, SetBridge, openRgbPanel.Reload);
        coolingPanel = new CoolingPanel(() => profile, () => controller.Connected ? controller.Groups : [], () => readings, () => coolingEngine, () => nextCooling = 0);
        if (HardwareOutput) screens = new ScreenHub(pump, controller, () => readings, history, SceneSeconds);
        screensPanel = new ScreensPanel(() => profile, () => screens, () => readings, () => history, () => { try { grid.EndEdit(); ReadSettings(); profile.Save(LastProfile); } catch (Exception e) { Log.Error(e); status.Text = e.Message; } }, SceneSeconds);
        var appSettings = new SettingsPanel(startup, preferences, () => { if (HardwareOutput) preferences.Save(); });
        pages = [Page(mapping), Page(fans), Page(openRgbPanel), Page(keyboardPanel), Page(coolingPanel), Page(screensPanel), Page(appSettings)];
        var content = new Panel { Dock = DockStyle.Fill, Margin = new Padding(0, 6, 0, 6) };
        foreach (var page in pages) content.Controls.Add(page);
        root.Controls.Add(content, 0, 2);
        root.Controls.Add(mode, 0, 3); root.Controls.Add(status, 0, 4);

        CaseLayout.Upgrade(profile);
        if (profile.Fans.Count == 0) Demo();
        else if (profile.LayoutPreset != CaseLayout.Id) CaseLayout.Apply(profile);
        ShowProfile();
        ShowTab(0);
        foreach (var control in new NumericUpDown[] { period, wavelength, direction, brightness, fps, gamma }) control.ValueChanged += (_, _) => ReadSettings();
        openRgb.CheckedChanged += (_, _) => SetSdk();
        // OpenRGB output only in a normal run, never in tests or window renders.
        if (profile.OpenRgbOutput && HardwareOutput) SetBridge(true);
        if (HardwareOutput) try { startup.Checked = AutoStart.Enabled; } catch (Exception e) { Log.Error(e); }
        startup.CheckedChanged += (_, _) => SetStartup();
        var menu = Ui.Menu(); menu.Items.Add(L10n.T("Open"), null, (_, _) => { Show(); WindowState = FormWindowState.Normal; Activate(); }); menu.Items.Add(L10n.T("Exit"), null, (_, _) => Close()); tray.ContextMenuStrip = menu;
        tray.DoubleClick += (_, _) => { Show(); Activate(); };
        timer.Tick += (_, _) =>
        {
            if (pages[0].Visible) canvas.Invalidate();
            if (controller.Status != lastStatus) { lastStatus = controller.Status; status.Text = lastStatus; }
            var pumpRoute = PumpState().Route;
            canvas.PumpLabel = pumpRoute switch { PumpRoute.Radio => L10n.T("Pump · radio"), PumpRoute.Usb => L10n.T("Pump · USB"), _ => L10n.T("Pump") };
            string line = controller.Connected || pump.Connected
                ? L10n.F((FormattableString)$"USB: fans {(controller.Connected ? L10n.T("connected") : L10n.T("none"))} · pump {pumpRoute switch { PumpRoute.Radio => L10n.T("via L-Wireless radio"), PumpRoute.Usb => L10n.T("via USB"), _ => L10n.T("none") }}")
                : L10n.T("PREVIEW • USB disconnected • hardware control inactive");
            if (bridge != null) line += " • " + bridge.Status;
            if (HardwareOutput) line += " • " + Services();
            // Text is assigned once, and only when it changes.
            if (mode.Text != line) mode.Text = line;
            if (Environment.TickCount64 >= nextCooling) { nextCooling = Environment.TickCount64 + 1000; RunCooling(); }
            bool slow = Environment.TickCount64 >= nextPanelTick;
            keyboardPanel.Tick(slow);
            screensPanel.Tick();
            if (slow) { nextPanelTick = Environment.TickCount64 + 500; fans.Tick(); if (pages[2].Visible) openRgbPanel.Tick(); coolingPanel.Tick(CoolingStatus()); }
        };
        timer.Start();
        // The window may be disposed without closing (tests, renders): the timer must not outlive the controls.
        Disposed += (_, _) => timer.Dispose();
        FormClosing += (_, _) => StopServices();
        Shown += async (_, _) =>
        {
            // Tests and window renders start nothing.
            if (!HardwareOutput) return;
            await Run(StartServices, quiet: true);
            if (startupLaunch) { tray.Visible = true; Hide(); }
        };
    }
    static Panel Page(Control content) { var page = new Panel { Dock = DockStyle.Fill, Visible = false }; page.Controls.Add(content); content.Dock = DockStyle.Fill; return page; }
    /// <summary>For window renders: after showing, selects the Cooling row (group MAC or pump).</summary>
    public void ShowCoolingCurve(string mac) => Shown += (_, _) => coolingPanel.SelectRow(mac);
    /// <summary>Opens a section; the switcher marker slides to it.</summary>
    public void ShowTab(int index)
    {
        if (sections.SelectedIndex != index) { sections.SelectedIndex = index; return; } // SelectedChanged calls again ShowTab
        for (int i = 0; i < pages.Length; i++) pages[i].Visible = i == index;
        if (index == 1) { fans.Reload(); fans.SelectGroup(selectedGroup); }
        if (index == 2) { openRgbPanel.Reload(); openRgbPanel.Tick(); }
        if (index == 3) { keyboardPanel.ShowSettings(); keyboardPanel.Tick(true); }
        if (index == 4) { coolingPanel.Reload(); coolingPanel.Tick(CoolingStatus()); }
        if (index == 5) { screensPanel.Reload(); screensPanel.Tick(); }
    }
    /// <summary>
    /// Once per second: sensors → curves → PWM targets. Without control enabled or without USB the app
    /// sends no speed commands and the fans run at hardware speed.
    /// </summary>
    void RunCooling()
    {
        readings = new(CpuSensor.Read(), GpuSensor.Read(), controller.Connected ? controller.Groups.FirstOrDefault(g => g.IsHydro)?.Coolant : null);
        history.Add(readings);
        // The pump gets a constant RPM through a separate radio command, independent of temperature and the fan checkbox.
        // Brightness and rotation of the pump's built-in theme come from its screen settings.
        var screen = profile.Screens.FirstOrDefault(s => s.IsPump);
        controller.SetPumpTarget(controller.Connected && profile.Cooling.PumpRpm is int rpm ? new Controller.PumpParameters(rpm,
            screen == null || screen.Mode == ScreenMode.Unmanaged ? 80 : screen.Mode == ScreenMode.Off ? 0 : screen.Brightness, (screen?.Rotation ?? 0) / 90,
            UseTheme: screen == null || screen.Mode == ScreenMode.Unmanaged) : null);
        if (!profile.Cooling.Enabled || !controller.Connected) { if (controller.ControllingFans) controller.ReleaseFans(); return; }
        controller.SetFanTargets(coolingEngine.Compute(profile.Cooling, readings, Environment.TickCount64));
    }
    string CoolingStatus() => !controller.Connected ? L10n.T("Click Connect USB; speed is controlled via L-Wireless.") : string.Join(" · ", new[] { controller.FanStatus, controller.PumpStatus }.Where(x => x.Length > 0));
    /// <summary>Case scene time: from the fan clock after Apply, otherwise local.</summary>
    double SceneSeconds() => controller.ScenePhase() is double phase ? phase * profile.PeriodSeconds : clock.Elapsed.TotalSeconds;
    string PumpLink()
    {
        var (route, note) = PumpState();
        return route switch { PumpRoute.Radio => L10n.T("via L-Wireless radio"), PumpRoute.Usb => L10n.T("via USB"), _ => note.Length > 0 ? note : L10n.T("disconnected") };
    }
    static NumericUpDown Number(decimal min, decimal max, decimal value, int decimals = 0) => new() { Minimum = min, Maximum = max, Value = value, DecimalPlaces = decimals, Width = 68, Increment = decimals > 0 ? .1m : 1, Margin = new Padding(3, 6, 12, 3) };
    static FlowLayoutPanel Flow() => new() { Dock = DockStyle.Fill, WrapContents = true, AutoScroll = false, AutoSize = true, Margin = Padding.Empty };
    static void AddNumber(Control flow, string label, NumericUpDown number) { flow.Controls.Add(new Label { Text = label, AutoSize = true, Margin = new Padding(0, 9, 0, 0) }); flow.Controls.Add(number); }
    Button Button(string text, Action action) => Ui.Button(text, () => { if (!busy) action(); });
    void Demo()
    {
        CaseLayout.Populate(profile);
    }
    void ToggleCoordinates()
    {
        bool show = mapping.RowStyles[2].Height == 0;
        // The first show binds the grid to data and selects the first row; that is not a user choice.
        syncingGrid = true;
        try { mapping.RowStyles[2].Height = show ? 150 : 0; grid.Visible = show; }
        finally { syncingGrid = false; }
        SyncGridSelection();
    }
    /// <summary>Context menu of a canvas element: follow mapping, custom color or off.</summary>
    void ShowElementMenu(object element, Point location)
    {
        if (busy) return;
        var menu = BuildElementMenu(element);
        menu.Closed += (_, _) => BeginInvoke(menu.Dispose);
        menu.Show(canvas, location);
    }
    public ContextMenuStrip BuildElementMenu(object element)
    {
        var (title, current, color) = element switch
        {
            FanPlacement f => (L10n.F((FormattableString)$"Fan {f.Fan + 1} · G{profile.GroupNumber(f.Group)}") + (profile.GroupNames.TryGetValue(f.Group, out var n) ? $" — {n}" : ""), f.Light, f.CustomColor),
            PumpPlacement pp => (L10n.T("HydroShift II LCD-C pump"), pp.Light, pp.CustomColor),
            ExternalPlacement d => (d.Name, d.Light, d.CustomColor),
            _ => throw new ArgumentException(L10n.T("Unknown canvas element."), nameof(element)),
        };
        void Set(LightMode light, string? custom)
        {
            switch (element)
            {
                case FanPlacement f: f.Light = light; if (custom != null) f.CustomColor = custom; break;
                case PumpPlacement pp: pp.Light = light; if (custom != null) pp.CustomColor = custom; break;
                case ExternalPlacement d: d.Light = light; if (custom != null) d.CustomColor = custom; break;
            }
            canvas.Invalidate(); fans.Reload(); openRgbPanel.Reload();
            // OpenRGB gets frames right away; fans and the pump play the uploaded cycle.
            status.Text = element is ExternalPlacement ? $"{title}: {LightLabels.Of(light).ToLowerInvariant()}." : L10n.F((FormattableString)$"{title}: {LightLabels.Of(light).ToLowerInvariant()}. Click Apply gradient to update the device.");
        }
        var menu = Ui.Menu();
        menu.Items.Add(new ToolStripLabel(title) { ForeColor = Color.FromArgb(150, 160, 185) });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem(L10n.T("Follow mapping"), null, (_, _) => Set(LightMode.Mapping, null)) { Name = "mapping", Checked = current == LightMode.Mapping });
        menu.Items.Add(new ToolStripMenuItem(L10n.T("Custom color…"), null, (_, _) =>
        {
            ColorPickerDialog.TryParseHex(color, out var initial);
            using var picker = new ColorPickerDialog(initial);
            if (picker.ShowDialog(this) == DialogResult.OK) Set(LightMode.Custom, ColorPickerDialog.Hex(picker.SelectedColor));
        }) { Name = "custom", Checked = current == LightMode.Custom });
        menu.Items.Add(new ToolStripMenuItem(L10n.T("Turn lighting off"), null, (_, _) => Set(LightMode.Off, null)) { Name = "off", Checked = current == LightMode.Off });
        if (element is FanPlacement fan)
        {
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(new ToolStripMenuItem(L10n.T("Identify group · 7 s"), null, (_, _) => _ = IdentifyGroup(fan.Group)));
        }
        return menu;
    }
    void ApplyCaseLayout()
    {
        if (!CaseLayout.Apply(profile)) { status.Text = L10n.T("Photo layout requires your G1–G4 groups: 2 + 1 + 3 + 3 fans with saved MAC addresses."); return; }
        ShowProfile(); status.Text = L10n.T("Case: G1 right, G2 left, G3 bottom, G4 top. Rows left to right; G1 top to bottom.");
    }
    void SetBridge(bool enabled)
    {
        profile.OpenRgbOutput = enabled;
        if (enabled == (bridge != null) || (enabled && !HardwareOutput)) return;
        if (!enabled) { var old = bridge; bridge = null; Task.Run(() => old!.Dispose()); return; }
        bridge = new OpenRgbBridge(() => profile, controller.ScenePhase);
        var current = bridge;
        current.DevicesChanged += found =>
        {
            if (closing || !IsHandleCreated) return;
            try { BeginInvoke(() => { if (!closing && ReferenceEquals(bridge, current)) AdoptOpenRgbDevices(found); }); }
            catch (InvalidOperationException) when (closing || IsDisposed || !IsHandleCreated) { }
        };
    }
    internal void AdoptOpenRgbDevices(OpenRgbDevice[] found)
    {
        profile.External = ExternalLayout.Merge(profile.External, found);
        // Merge keeps the objects of existing placements. They must be refreshed explicitly
        // BindingList: otherwise new LedCount values stay invisible in old rows.
        openRgbPanel.Reload();
        if (KeyboardLayout.Adopt(profile.Keyboard, found)) keyboardPanel.ShowSettings();
        canvas.Invalidate();
    }
    void EditLayers()
    {
        ReadSettings(); using var dialog = new LayerEditor(profile);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        profile.InnerLayer = dialog.Inner; profile.OuterLayer = dialog.Outer; profile.Pump.Layer = dialog.Pump; canvas.Invalidate();
        status.Text = L10n.T("Layers changed. Shared angle, wavelength and cycle preserve motion. Click Apply gradient.");
    }
    void EditPump()
    {
        if (busy) return;
        var state = PumpState(); using var dialog = new PumpEditor(profile.Pump, state.Route == PumpRoute.Radio || state.Note.Length > 0 ? state.Note : pump.Status);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        profile.Pump = dialog.Value; canvas.Invalidate(); status.Text = L10n.T("Pump geometry changed. Colors: Lighting layers. Send with Apply.");
    }
    (PumpRoute Route, string Note) PumpState() => PumpRouting.Decide(controller.Connected ? controller.Groups : [], pump.Connected);
    async Task ConnectDevices()
    {
        List<string> results = [];
        try { await Task.Run(controller.Connect); AdoptGroups(); results.Add(L10n.T("Wireless fans connected")); }
        catch (Exception e) { Log.Error(e); results.Add(e.Message); }
        var radio = PumpState();
        if (radio.Route == PumpRoute.Radio)
        {
            // L-Wireless drives the ring: pump USB is not needed for RGB and is opened only for its screen.
            results.Add(radio.Note + L10n.T("; pump USB for screen only"));
        }
        else
        {
            if (radio.Note.Length > 0) results.Add(radio.Note);
            if (controller.Connected && controller.BoundHydroMacs.Length > 0) results.Add(L10n.T("Pump paired to L-Wireless; USB RGB upload skipped."));
            else
            {
                try { await Task.Run(pump.Connect); results.Add(pump.Status); }
                catch (Exception e) { Log.Error(e); results.Add(e.Message); }
            }
        }
        if (screens != null)
        {
            try
            {
                await Task.Run(screens.Rescan); screensPanel.Reload();
                screens.Apply(profile.Screens, profile.Palette, profile);
                int managed = profile.Screens.Count(x => x.Mode != ScreenMode.Unmanaged);
                results.Add(L10n.F((FormattableString)$"screens found {screens.Found.Count}") + (managed > 0 ? L10n.F((FormattableString)$", managed {managed}") : ""));
            }
            catch (Exception e) { Log.Error(e); results.Add(L10n.T("Screens: ") + e.Message); }
        }
        status.Text = string.Join("; ", results);
        if (!controller.Connected && !pump.Connected && (screens == null || screens.Found.Count == 0)) throw new IOException(status.Text);
    }
    void ShowProfile()
    {
        populating = true;
        try
        {
            RefreshPalette(); period.Value = (decimal)profile.PeriodSeconds; wavelength.Value = (decimal)profile.Wavelength;
            direction.Value = (decimal)Math.Clamp(profile.Direction, -360, 360); brightness.Value = (decimal)(profile.Brightness * 100); fps.Value = profile.Fps; gamma.Value = (decimal)Math.Clamp(profile.Gamma, 1, 3);
            placements.Clear(); foreach (var fan in profile.Fans) placements.Add(fan);
            RefreshGroupChoices(); canvas?.RefreshSettings(); keyboardPanel?.ShowSettings(); keyboardPanel?.Effect.Reload(); coolingPanel?.Reload(); effects?.Reload(); wavelength.Enabled = !Effects.Active(profile);
        }
        finally { populating = false; }
        SyncGridSelection();
    }
    void RefreshGroupChoices()
    {
        foreach (var fan in profile.Fans) fan.GroupLabel = profile.GroupTitle(fan.Group);
        fans.Reload(); grid.Refresh();
    }
    void SelectGroup(string mac, bool fromFans = false)
    {
        selectedGroup = mac; canvas.SelectedGroup = mac;
        if (!fromFans) fans.SelectGroup(mac); else canvas.SelectWholeGroup(mac);
        SyncGridSelection(); canvas.Invalidate();
    }
    void SyncGridSelection()
    {
        // A hidden grid has zero height. The selection lives in the canvas and the fans tab,
        // and the row syncs when the grid can show it again.
        if (populating || syncingGrid || !grid.Visible || !grid.IsHandleCreated || grid.DisplayedRowCount(false) == 0 || selectedGroup == null) return;
        if ((grid.CurrentRow?.DataBoundItem as FanPlacement)?.Group == selectedGroup) return;
        var row = grid.Rows.Cast<DataGridViewRow>().FirstOrDefault(r => (r.DataBoundItem as FanPlacement)?.Group == selectedGroup);
        if (row == null) return;
        syncingGrid = true;
        try { grid.CurrentCell = row.Cells[1]; } // DataGridView shows the current cell by itself.
        finally { syncingGrid = false; }
    }
    void ReverseGroup(string mac)
    {
        var fans = profile.Fans.Where(f => f.Group == mac).OrderBy(f => f.Fan).ToArray();
        var positions = fans.Select(f => (f.X, f.Y, f.Rotation, f.ProjectionY, f.Scale, f.Elevation)).Reverse().ToArray();
        for (int i = 0; i < fans.Length; i++) (fans[i].X, fans[i].Y, fans[i].Rotation, fans[i].ProjectionY, fans[i].Scale, fans[i].Elevation) = positions[i];
        grid.Refresh(); canvas.Invalidate(); status.Text = L10n.T("Group order reversed on canvas. Physical fan indices unchanged. Click Apply.");
    }
    async Task IdentifyGroup(string mac)
    {
        await Run(async () =>
        {
            if (openRgb.Checked) openRgb.Checked = false;
            await Task.Run(() => controller.Identify(mac));
            while (!closing && controller.Identifying) await Task.Delay(100);
            if (!closing) status.Text = controller.Status;
        });
    }
    void ReadSettings()
    {
        if (populating) return;
        profile.PeriodSeconds = (double)period.Value; profile.Wavelength = (double)wavelength.Value;
        profile.Direction = (double)direction.Value; profile.Brightness = (double)brightness.Value / 100; profile.Fps = (int)fps.Value; profile.Gamma = (double)gamma.Value;
        canvas.RefreshSettings();
    }
    void RefreshPalette()
    {
        palette.Model = new PaletteModel(Effects.Active(profile) ? profile.Effect.Current.Palette : profile.Palette); palette.Space = profile.ColorSpace; palette.Saturation = profile.SaturationBoost; palette.Invalidate();
    }
    void EffectChanged()
    {
        RefreshPalette(); wavelength.Enabled = !Effects.Active(profile); canvas.Invalidate();
        string text = Effects.Active(profile) ? L10n.F((FormattableString)$"Effect “{Effects.Info(profile.Effect.Kind).Title}” on canvas. Click Apply gradient to update fans and pump.") : L10n.T("Standard gradient. Click Apply gradient to update fans.");
        if (status.Text != text) status.Text = text;
    }
    void EditGradient()
    {
        ReadSettings();
        // With an effect the editor changes the effect palette: each effect has its own.
        bool effect = Effects.Active(profile);
        var source = effect ? JsonSerializer.Deserialize<Profile>(JsonSerializer.Serialize(profile))! : profile;
        if (effect) source.Palette = profile.Effect.Current.Palette;
        using var editor = new GradientEditor(source);
        if (editor.ShowDialog(this) != DialogResult.OK) return;
        if (effect) profile.Effect.Current.Palette = editor.Palette; else profile.Palette = editor.Palette;
        profile.ColorSpace = editor.SelectedSpace; profile.SaturationBoost = editor.SelectedSaturation;
        RefreshPalette(); canvas.Invalidate(); status.Text = (effect ? L10n.F((FormattableString)$"Palette for effect “{Effects.Info(profile.Effect.Kind).Title}” changed.") : L10n.T("Gradient changed.")) + L10n.T(" Click Apply gradient to update fans.");
    }
    void AdoptGroups()
    {
        sdk?.Dispose(); sdk = null; openRgb.Checked = false;
        var previous = profile.Fans;
        profile.Fans = controller.Groups.Where(g => g.Supported).SelectMany((g, row) => Enumerable.Range(0, g.Fans).Select(f =>
        {
            var fan = previous.SingleOrDefault(p => p.Group == g.Mac && p.Fan == f) ?? new FanPlacement { Group = g.Mac, Fan = f, X = 130 + 112 * f, Y = 100 + 155 * row };
            fan.Model = g.Model!.Id; // the model comes from the controller reply, even if the layout was saved with an old one
            return fan;
        })).ToList();
        if (profile.LayoutPreset != CaseLayout.Id) CaseLayout.Apply(profile);
        ShowProfile();
        status.Text = controller.Groups.Length == 0 ? L10n.T("Controller ready, but no paired groups found yet. Reconnect later.") : string.Join("; ", controller.Groups.Select(GroupSummary));
    }
    /// <summary>Group status line: model and LED count; an unknown model shows type bytes, and RGB and speed are left alone.</summary>
    static string GroupSummary(WirelessGroup g) => g.Model is { } model
        ? L10n.F((FormattableString)$"{g.Mac}: {model.Name}, {g.Fans} fans, {model.LedsPerFan} LEDs/fan") + (model.HardwareVerified ? "" : L10n.T(" (LED layout from upstream, not verified on hardware)"))
        : L10n.F((FormattableString)$"{g.Mac}: {g.Fans} fans, types {Convert.ToHexString(g.Types)}") + L10n.T(", RGB blocked: unknown model");
    /// <summary>
    /// Launching the app starts every service: CPU sensor, OpenRGB (if OpenRGB output is on), USB
    /// for fans, the pump and screens, then the saved profile: lighting, screens, cooling and pump.
    /// USB may appear late at Windows sign-in: up to 6 attempts, 5 seconds apart.
    /// </summary>
    async Task StartServices()
    {
        try { await StartServicesCore(); }
        finally
        {
            // The bridge may have read the list before the sensor/OpenRGB started and USB.
            // Run the regular Refresh list again after the whole sequence,
            // including when USB or applying the profile failed.
            if (!closing && HardwareOutput && profile.OpenRgbOutput)
            {
                SetBridge(true);
                _ = OpenRgbStartup.RefreshDevices(openRgbPanel.RefreshDevices,
                    () => !closing && HardwareOutput && profile.OpenRgbOutput && bridge != null, lifetime.Token);
                Log.Write(L10n.T("Startup: OpenRGB device list refresh requested after services started."));
            }
        }
    }
    async Task StartServicesCore()
    {
        List<string> notes = [];
        if (AutoStart.Upgrade() is { } upgraded) notes.Add(upgraded);
        bool admin = Elevation.IsAdministrator;
        // When elevated, the CPU sensor is always needed: temperature for cooling, screens and the table.
        if (CpuSensor.Read() == null && (admin || profile.Cooling.CpuSensorAutoStart))
            try { CpuSensor.StartElevated(); notes.Add(L10n.T("CPU sensor started")); } catch (Exception e) { Log.Error(e); notes.Add(e.Message); }
        if (profile.OpenRgbOutput && !OpenRgbLauncher.Running() && (admin || profile.OpenRgbAutoStart))
            try { OpenRgbLauncher.StartElevated(); notes.Add(L10n.T("OpenRGB started")); } catch (Exception e) { Log.Error(e); notes.Add(e.Message); }
        for (int attempt = 1; ; attempt++)
        {
            try { await ConnectDevices(); } catch (Exception e) when (attempt < 6) { Log.Write(L10n.F((FormattableString)$"Startup: USB attempt {attempt}: {e.Message}")); }
            if (controller.Connected || closing || attempt >= 6) break;
            status.Text = L10n.F((FormattableString)$"Startup: waiting for L-Wireless USB, attempt {attempt} of 6…");
            await Task.Delay(5000);
        }
        if (closing) return;
        if (controller.Connected && profile.Fans.Count > 0) await ApplyScene();
        notes.Insert(0, status.Text);
        status.Text = string.Join("; ", notes.Where(n => n.Length > 0));
        Log.Write(L10n.T("Startup: ") + status.Text);
    }
    /// <summary>
    /// A full close stops everything the app started: speed commands (fans return to
    /// hardware speed), the pump, screen threads (they finish the current frame), the OpenRGB bridge and server, OpenRGB,
    /// if the app started it, and USB. The CPU sensor exits together with the app.
    /// </summary>
    void StopServices()
    {
        if (closing) return;
        closing = true; lifetime.Cancel(); timer.Stop();
        controller.ReleaseFans(); controller.SetPumpTarget(null);
        hints.Dispose(); sdk?.Dispose(); bridge?.Dispose(); screens?.Dispose(); controller.Dispose(); pump.Dispose();
        if (HardwareOutput) try { if (OpenRgbLauncher.StopStarted() is { } note) Log.Write(note); } catch (Exception e) { Log.Error(e); }
        Log.Write(L10n.T("Application closed; services stopped."));
        tray.Dispose(); timer.Dispose();
        lifetime.Dispose();
    }
    /// <summary>Service state for the bottom line.</summary>
    string Services()
    {
        int managedScreens = profile.Screens.Count(s => s.Mode != ScreenMode.Unmanaged);
        return string.Join(" · ", new[]
        {
            Elevation.IsAdministrator ? L10n.T("administrator") : L10n.T("no administrator rights"),
            readings.Cpu != null ? L10n.T("CPU sensor running") : L10n.T("CPU sensor not running"),
            profile.OpenRgbOutput ? (OpenRgbLauncher.Running() ? L10n.T("OpenRGB running") : L10n.T("OpenRGB not running")) : "",
            managedScreens > 0 ? L10n.F((FormattableString)$"managed screens {managedScreens}") : "",
            controller.ControllingPump ? L10n.T("pump: fixed RPM") : "",
        }.Where(x => x.Length > 0));
    }
    async Task Run(Func<Task> operation, bool quiet = false)
    {
        var disabled = new List<Control>();
        void DisableInputs(Control parent)
        {
            foreach (Control control in parent.Controls)
            {
                if (control is ButtonBase or TextBoxBase or NumericUpDown or ComboBox or DataGridView or Canvas)
                {
                    if (control.Enabled) { disabled.Add(control); control.Enabled = false; }
                }
                else DisableInputs(control);
            }
        }
        busy = true; UseWaitCursor = true; DisableInputs(this); status.Text = L10n.T("Working…");
        try { await operation(); }
        catch (Exception e) { Log.Error(e); if (!closing) { status.Text = e.Message; if (!quiet) MessageBox.Show(this, e.Message, L10n.T("Operation incomplete"), MessageBoxButtons.OK, MessageBoxIcon.Warning); } }
        finally { if (!closing) { busy = false; UseWaitCursor = false; foreach (var control in disabled) control.Enabled = true; } }
    }
    async Task Apply()
    {
        await Run(async () =>
        {
            if (openRgb.Checked) openRgb.Checked = false;
            await ApplyScene();
        });
    }
    async Task ApplyScene()
    {
        grid.EndEdit(); ReadSettings(); profile.Validate();
        var groups = controller.Connected ? controller.Groups.Where(g => g.Supported && profile.Fans.Any(f => f.Group == g.Mac && f.Enabled)).ToArray() : [];
        var (route, note) = PumpState();
        if (route == PumpRoute.None && !controller.Connected) throw new IOException(L10n.T("Connect USB first."));
        var scene = await Task.Run(() => RgbScene.Prepare(profile, groups, includePump: route != PumpRoute.None));
        List<string> results = [];
        if (route == PumpRoute.None && note.Length > 0) results.Add(note);
        // Over radio the ring goes in the same upload as the fans: shared interval and cycle start.
        if (groups.Length > 0 || route == PumpRoute.Radio)
            results.Add(await Task.Run(() => controller.Apply(profile, route == PumpRoute.Radio ? scene : scene with { Pump = null })));
        if (route == PumpRoute.Usb && scene.Pump != null)
        {
            status.Text = L10n.T("Uploading pump RGB · waiting for USB recovery…");
            results.Add(await Task.Run(() => pump.Apply(scene.Pump, controller.BoundHydroMacs)));
        }
        screens?.Apply(profile.Screens, profile.Palette, profile);
        profile.Save(LastProfile); status.Text = string.Join(" ", results);
    }
    void SaveProfile()
    {
        using var dialog = new SaveFileDialog { Filter = L10n.T("JSON profile|*.json"), FileName = "sl-wireless.json" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try { grid.EndEdit(); ReadSettings(); profile.Save(dialog.FileName); profile.Save(LastProfile); status.Text = L10n.T("Profile saved."); } catch (Exception e) { MessageBox.Show(e.Message); }
    }
    void LoadProfile()
    {
        using var dialog = new OpenFileDialog { Filter = L10n.T("JSON profile|*.json") };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try { var loaded = Profile.Load(dialog.FileName); profile = loaded; ShowProfile(); screensPanel.Reload(); screens?.Apply(profile.Screens, profile.Palette, profile); SetBridge(loaded.OpenRgbOutput); bridge?.Refresh(); status.Text = L10n.T("Profile loaded. Click Apply gradient to send it."); } catch (Exception e) { MessageBox.Show(e.Message); }
    }
    void Diagnose()
    {
        using var dialog = new SaveFileDialog { Filter = L10n.T("JSON diagnostics|*.json"), FileName = "wcrgb-usb-diagnostics.json" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try { File.WriteAllText(dialog.FileName, JsonSerializer.Serialize(UsbInventory.Scan(), Profile.Json)); status.Text = L10n.T("Diagnostics saved. No USB commands sent."); } catch (Exception e) { MessageBox.Show(e.Message); }
    }
    void SetSdk()
    {
        sdk?.Dispose(); sdk = null;
        if (!openRgb.Checked) return;
        try
        {
            if (!controller.Connected || !controller.Groups.Any(g => g.Supported)) throw new IOException(L10n.T("Connect USB and discover wireless fan groups first."));
            sdk = new(controller.Groups, controller.QueueDirect);
            status.Text = L10n.T("In OpenRGB: SDK Client → 127.0.0.1, port 6743 → Connect. Total output limit: 5 FPS.");
        }
        catch (Exception e) { openRgb.Checked = false; status.Text = e.Message; MessageBox.Show(e.Message, "OpenRGB"); }
    }
    void SetStartup()
    {
        if (populating || !HardwareOutput) return;
        try
        {
            if (startup.Checked) { grid.EndEdit(); ReadSettings(); profile.Save(LastProfile); status.Text = AutoStart.Enable(); }
            else { AutoStart.Disable(); status.Text = L10n.T("Startup disabled."); }
        }
        catch (Exception e) { Log.Error(e); status.Text = e.Message; MessageBox.Show(e.Message); }
    }
}

public sealed class Canvas : Panel
{
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string? SelectedGroup { get; set; }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string PumpLabel { get; set; } = L10n.T("Pump");
    public event Action<FanPlacement>? FanSelected;
    public event Action<double>? DirectionChanged;
    public event Action? PumpDoubleClick;
    /// <summary>Right-click on a fan, the pump or a device OpenRGB.</summary>
    public event Action<object, Point>? ElementMenu;
    readonly CanvasField field = new();
    readonly CanvasField maskField = new();
    readonly DirectionDial angleDial = new() { Location = new(18, 294) };
    readonly CheckBox showField = new() { Text = L10n.T("Gradient on case"), AutoSize = true, Location = new(18, 242), ForeColor = Color.WhiteSmoke, BackColor = Color.FromArgb(12, 15, 22) };
    readonly Func<Profile> getProfile;
    readonly Func<double> seconds;
    readonly Action changed;
    FanPlacement? dragged;
    readonly FanSelection selection = new();
    List<(FanPlacement Fan, double X, double Y)> movingFans = [];
    List<FanPlacement> boxInitial = [];
    bool selectingBox, moved, exclusiveOnClick;
    Point pointer;
    Profile? selectionProfile;
    internal IReadOnlyList<FanPlacement> SelectedFans => selection.Members(getProfile());
    ExternalPlacement? draggedExternal;
    bool draggedPump;
    Point origin;
    double startX, startY;
    (double Scale, double X, double Y) dragView;
    /// <summary>The pump or an OpenRGB device selected by a click: they have resize handles too.</summary>
    object? focused;
    /// <summary>Resizing by a handle: the opposite corner stays fixed and the whole selection scales together.</summary>
    sealed record ResizeDrag(PointF Anchor, PointF Corner, (FanPlacement Fan, double X, double Y, double Scale)[] Fans,
        (double X, double Y, double Diameter)? Pump, (ExternalPlacement Device, double X, double Y, double Width, double Height)? External);
    ResizeDrag? resizing;
    const int HandlePixels = 8;
    public Canvas(Func<Profile> profile, Func<double> seconds, Action changed)
    {
        getProfile = profile; this.seconds = seconds; this.changed = changed;
        DoubleBuffered = true; BackColor = Color.FromArgb(12, 15, 22);
        Controls.Add(showField); Controls.Add(angleDial);
        angleDial.Changed += angle => { DirectionChanged?.Invoke(angle); Invalidate(); };
        showField.CheckedChanged += (_, _) => { getProfile().ShowCanvasGradient = showField.Checked; Invalidate(); };
        MouseUp += (_, e) =>
        {
            if (e.Button != MouseButtons.Right || HitTest(e.Location) is not { } element) return;
            if (element is FanPlacement fan) FanSelected?.Invoke(fan);
            ElementMenu?.Invoke(element, e.Location);
        };
        MouseDown += (_, e) =>
        {
            if (e.Button != MouseButtons.Left) return;
            BeginPointer(e.Location, (ModifierKeys & Keys.Shift) != 0);
        };
        MouseMove += (_, e) =>
        {
            if (!Capture) Cursor = HandleAt(e.Location) switch { 0 or 2 => Cursors.SizeNWSE, 1 or 3 => Cursors.SizeNESW, _ => Cursors.Default };
            MovePointer(e.Location);
        };
        MouseUp += (_, e) => { if (e.Button == MouseButtons.Left) EndPointer(); };
        MouseCaptureChanged += (_, _) => { if (!Capture) ResetPointer(); };
        MouseDoubleClick += (_, e) =>
        {
            var p = getProfile(); var v = ViewFor(p); double x = (e.X-v.X)/v.Scale, y = (e.Y-v.Y)/v.Scale;
            if (Math.Pow(x-p.Pump.X, 2) + Math.Pow(y-p.Pump.Y, 2) < Math.Pow(p.Pump.Diameter/2, 2)) { draggedPump = false; Capture = false; PumpDoubleClick?.Invoke(); }
            else if (e.Button == MouseButtons.Left && HitTest(e.Location) is FanPlacement fan) { ResetPointer(); Capture = false; SelectWholeGroup(fan.Group); FanSelected?.Invoke(fan); }
        };
        RefreshSettings();
    }
    void EnsureSelection()
    {
        var p = getProfile();
        if (!ReferenceEquals(p, selectionProfile)) { selection.Clear(); ResetPointer(); selectionProfile = p; Capture = false; }
        selection.Prune(p);
    }
    /// <summary>Selects the whole group: the frame handles resize all of its fans.</summary>
    public void SelectWholeGroup(string group)
    {
        EnsureSelection(); selection.SelectGroup(getProfile(), group); focused = null; Invalidate();
    }
    /// <summary>Frame of whatever the handles resize: selected fans, otherwise the selected pump or device OpenRGB.</summary>
    RectangleF? ResizeBounds(Profile p)
    {
        var fans = selection.Members(p);
        if (fans.Count > 0) return fans.Select(Gradient.FanBounds).Aggregate(RectangleF.Union);
        if (focused is PumpPlacement && ReferenceEquals(focused, p.Pump)) return new((float)(p.Pump.X - p.Pump.Diameter / 2), (float)(p.Pump.Y - p.Pump.Diameter / 2), (float)p.Pump.Diameter, (float)p.Pump.Diameter);
        if (focused is ExternalPlacement device && device.Enabled && p.External.Contains(device)) return device.Bounds;
        return null;
    }
    static PointF[] Corners(RectangleF r) => [new(r.Left, r.Top), new(r.Right, r.Top), new(r.Right, r.Bottom), new(r.Left, r.Bottom)];
    /// <summary>Index of the handle under the cursor (0 is top left, clockwise), otherwise −1.</summary>
    internal int HandleAt(Point location)
    {
        EnsureSelection();
        var p = getProfile(); if (ResizeBounds(p) is not { } bounds) return -1;
        var v = ViewFor(p); var corners = Corners(bounds);
        for (int i = 0; i < 4; i++)
            if (Math.Abs(v.X + corners[i].X * v.Scale - location.X) <= HandlePixels && Math.Abs(v.Y + corners[i].Y * v.Scale - location.Y) <= HandlePixels) return i;
        return -1;
    }
    internal void BeginPointer(Point location, bool shift)
    {
        Focus(); EnsureSelection();
        var p = getProfile(); dragView = ViewFor(p); origin = pointer = location; moved = false;
        if (!shift && HandleAt(location) is int corner and >= 0 && ResizeBounds(p) is { } box)
        {
            var corners = Corners(box);
            var external = focused as ExternalPlacement;
            resizing = new(corners[(corner + 2) % 4], corners[corner],
                [.. selection.Members(p).Select(f => (f, f.X, f.Y, f.Scale))],
                selection.Members(p).Count == 0 && focused is PumpPlacement ? (p.Pump.X, p.Pump.Y, p.Pump.Diameter) : null,
                selection.Members(p).Count == 0 && external != null ? (external, external.X, external.Y, external.Width, external.Height) : null);
            Capture = true; Invalidate(); return;
        }
        var hit = HitTest(location);
        focused = hit is PumpPlacement or ExternalPlacement ? hit : null;
        if (hit is FanPlacement fan)
        {
            exclusiveOnClick = !shift && selection.Contains(fan);
            selection.Select(fan, shift); FanSelected?.Invoke(fan);
            if (selection.Contains(fan))
            {
                dragged = fan;
                movingFans = selection.Members(p).Select(f => (f, f.X, f.Y)).ToList();
            }
        }
        else if (hit is PumpPlacement pump)
        {
            if (!shift) selection.Clear();
            draggedPump = true; startX = pump.X; startY = pump.Y;
        }
        else if (hit is ExternalPlacement external)
        {
            if (!shift) selection.Clear();
            draggedExternal = external; startX = external.X; startY = external.Y;
        }
        else
        {
            boxInitial = shift ? selection.Members(p) : [];
            if (!shift) selection.Clear();
            selectingBox = true;
        }
        Capture = true; Invalidate();
    }
    internal void MovePointer(Point location)
    {
        if (!selectingBox && dragged == null && !draggedPump && draggedExternal == null && resizing == null) return;
        if (!ReferenceEquals(selectionProfile, getProfile())) { EndPointer(); selection.Clear(); return; }
        pointer = location;
        if (!moved && Math.Abs(location.X - origin.X) < SystemInformation.DragSize.Width / 2 && Math.Abs(location.Y - origin.Y) < SystemInformation.DragSize.Height / 2) return;
        moved = true;
        if (resizing != null) { ApplyResize(resizing, location); changed(); Invalidate(); return; }
        if (selectingBox)
        {
            var rect = PointerBox();
            var world = new RectangleF((float)((rect.X - dragView.X) / dragView.Scale), (float)((rect.Y - dragView.Y) / dragView.Scale), (float)(rect.Width / dragView.Scale), (float)(rect.Height / dragView.Scale));
            selection.Box(getProfile(), world, boxInitial); Invalidate(); return;
        }
        double dx = Math.Round((location.X - origin.X) / dragView.Scale), dy = Math.Round((location.Y - origin.Y) / dragView.Scale);
        if (dragged != null)
        {
            // Clamp one shared delta to preserve the spacing of the complete selection.
            dx = Math.Clamp(dx, movingFans.Max(f => -10000 - f.X), movingFans.Min(f => 10000 - f.X));
            dy = Math.Clamp(dy, movingFans.Max(f => -10000 - f.Y), movingFans.Min(f => 10000 - f.Y));
            foreach (var entry in movingFans) { entry.Fan.X = entry.X + dx; entry.Fan.Y = entry.Y + dy; }
        }
        else
        {
            double x = Math.Round(Math.Clamp(startX + dx, -9900, 9900)), y = Math.Round(Math.Clamp(startY + dy, -9900, 9900));
            if (draggedPump) { getProfile().Pump.X = x; getProfile().Pump.Y = y; }
            else if (draggedExternal != null) { draggedExternal.X = x; draggedExternal.Y = y; }
        }
        changed(); Invalidate();
    }
    /// <summary>
    /// The factor is the cursor projection onto the fixed-corner → grabbed-corner diagonal. Scale and position
    /// scale together: neighbouring fans of a group stay touching and nothing drifts.
    /// Limits match profile validation: fan scale 0.2–4, pump diameter 20–300, device size 1–2000.
    /// </summary>
    void ApplyResize(ResizeDrag r, Point location)
    {
        double x = (location.X - dragView.X) / dragView.Scale, y = (location.Y - dragView.Y) / dragView.Scale;
        double vx = r.Corner.X - r.Anchor.X, vy = r.Corner.Y - r.Anchor.Y, length = vx * vx + vy * vy;
        if (length < 1e-6) return;
        double k = ((x - r.Anchor.X) * vx + (y - r.Anchor.Y) * vy) / length, low = .05, high = 20;
        foreach (var f in r.Fans) { low = Math.Max(low, .2 / f.Scale); high = Math.Min(high, 4 / f.Scale); }
        if (r.Pump is { } pump) { low = Math.Max(low, 20 / pump.Diameter); high = Math.Min(high, 300 / pump.Diameter); }
        if (r.External is { } device) { low = Math.Max(low, 1 / Math.Min(device.Width, device.Height)); high = Math.Min(high, 2000 / Math.Max(device.Width, device.Height)); }
        k = Math.Clamp(Math.Round(k, 3), low, Math.Max(low, high));
        double X(double value) => r.Anchor.X + (value - r.Anchor.X) * k;
        double Y(double value) => r.Anchor.Y + (value - r.Anchor.Y) * k;
        foreach (var f in r.Fans) { f.Fan.Scale = Math.Clamp(f.Scale * k, .2, 4); f.Fan.X = X(f.X); f.Fan.Y = Y(f.Y); }
        if (r.Pump is { } before) { var pp = getProfile().Pump; pp.Diameter = Math.Clamp(before.Diameter * k, 20, 300); pp.X = X(before.X); pp.Y = Y(before.Y); }
        if (r.External is { } old) { old.Device.Width = Math.Clamp(old.Width * k, 1, 2000); old.Device.Height = Math.Clamp(old.Height * k, 1, 2000); old.Device.X = X(old.X); old.Device.Y = Y(old.Y); }
    }
    Rectangle PointerBox() => Rectangle.FromLTRB(Math.Min(origin.X, pointer.X), Math.Min(origin.Y, pointer.Y), Math.Max(origin.X, pointer.X), Math.Max(origin.Y, pointer.Y));
    internal void EndPointer()
    {
        if (!moved && exclusiveOnClick && dragged is { } fan) { selection.Clear(); selection.Select(fan, false); }
        ResetPointer(); Capture = false; Invalidate();
    }
    void ResetPointer() { dragged = null; draggedExternal = null; draggedPump = false; selectingBox = false; resizing = null; exclusiveOnClick = false; movingFans.Clear(); boxInitial.Clear(); }
    /// <summary>Element under a point: the pump, then OpenRGB devices, then fans (same order as dragging).</summary>
    public object? HitTest(Point location)
    {
        var p = getProfile(); var v = ViewFor(p);
        double x = (location.X - v.X) / v.Scale, y = (location.Y - v.Y) / v.Scale;
        if (Math.Pow(x - p.Pump.X, 2) + Math.Pow(y - p.Pump.Y, 2) < Math.Pow(p.Pump.Diameter / 2, 2)) return p.Pump;
        if (p.External.LastOrDefault(d => d.Enabled && RectangleF.Inflate(d.Bounds, 6, 6).Contains((float)x, (float)y)) is { } device) return device;
        return p.Fans.LastOrDefault(f => Gradient.FanBounds(f).Contains((float)x, (float)y));
    }
    public PointF ToScreen(double x, double y) { var v = ViewFor(getProfile()); return new((float)(v.X + x * v.Scale), (float)(v.Y + y * v.Scale)); }
    public void RefreshSettings()
    {
        EnsureSelection(); var p = getProfile(); angleDial.Angle = p.Direction; showField.Checked = p.ShowCanvasGradient; Invalidate();
    }
    protected override void Dispose(bool disposing) { if (disposing) { field.Dispose(); maskField.Dispose(); } base.Dispose(disposing); }
    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e); Invalidate();
        if (showField != null) showField.Visible = Width > 850 && Height >= 270;
        if (angleDial != null) angleDial.Visible = Width > 850 && Height >= 430;
    }
    (double Scale, double X, double Y) ViewFor(Profile p)
    {
        var bounds = p.Fans.Select(Gradient.FanBounds).Append(new((float)(p.Pump.X-p.Pump.Diameter/2), (float)(p.Pump.Y-p.Pump.Diameter/2), (float)p.Pump.Diameter, (float)p.Pump.Diameter)).Concat(p.External.Where(d => d.Enabled).Select(d => d.Bounds)).ToArray();
        double left = Math.Min(0, bounds.Min(b => b.Left)-8), top = Math.Min(0, bounds.Min(b => b.Top)-8);
        double right = Math.Max(620, bounds.Max(b => b.Right)+8), bottom = Math.Max(CaseLayout.IsPhoto(p) ? 600 : 380, bounds.Max(b => b.Bottom)+8);
        int legend = Width > 850 ? 230 : 0;
        double scale = Math.Max(.01, Math.Min((Width - legend - 32) / (right - left), (Height - 24) / (bottom - top)));
        return (scale, legend + (Width - legend - (right-left)*scale)/2 - left*scale, (Height - (bottom-top)*scale)/2 - top*scale);
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        EnsureSelection();
        var p = getProfile(); ColorStop[] stops, inner, outer;
        try { stops = Effects.Palette(p); inner = Gradient.ParsePalette(p.InnerLayer.Palette); outer = Gradient.ParsePalette(p.OuterLayer.Palette); } catch { return; }
        double time = seconds();
        e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using var line = new Pen(Color.FromArgb(27, 33, 45));
        for (int x = 0; x < Width; x += 30) e.Graphics.DrawLine(line, x, 0, x, Height);
        for (int y = 0; y < Height; y += 30) e.Graphics.DrawLine(line, 0, y, Width, y);
        using var textBrush = new SolidBrush(Color.LightGray);
        if (Width > 850)
        {
            e.Graphics.DrawString(L10n.T("CASE CANVAS"), Font, textBrush, 18, 18);
            int row = 53;
            foreach (var mac in p.Fans.Select(f => f.Group).Distinct().OrderBy(x => x, StringComparer.Ordinal))
            { e.Graphics.DrawString(p.GroupTitle(mac), Font, textBrush, 18, row); row += 28; }
            row += 18;
            e.Graphics.DrawString(L10n.T("Inner: ") + (p.InnerLayer.Enabled ? L10n.T("on") : L10n.T("off")), Font, textBrush, 18, row);
            e.Graphics.DrawString(L10n.T("Outer: ") + (p.OuterLayer.Enabled ? L10n.T("on") : L10n.T("off")), Font, textBrush, 18, row + 28);
            if (angleDial.Visible) e.Graphics.DrawString(L10n.T("Movement tilt · drag arrow"), Font, textBrush, 18, 274);
        }
        var view = dragged != null || draggedPump || draggedExternal != null || selectingBox || resizing != null ? dragView : ViewFor(p); float scale = (float)view.Scale;
        var state = e.Graphics.Save();
        e.Graphics.TranslateTransform((float)view.X, (float)view.Y); e.Graphics.ScaleTransform(scale, scale);
        if (p.LayoutPreset == CaseLayout.Id)
        {
            if (p.ShowCanvasGradient) field.Draw(e.Graphics, new RectangleF(8, 20, 600, 560), scale, p, stops, time);
            using var masks = new System.Drawing.Drawing2D.GraphicsPath();
            foreach (var fan in p.Fans.Where(f => f.Enabled && f.Light == LightMode.Mapping)) masks.AddRectangle(Gradient.FanBounds(fan));
            var clip = e.Graphics.Save(); e.Graphics.SetClip(masks, System.Drawing.Drawing2D.CombineMode.Intersect);
            maskField.Draw(e.Graphics, new RectangleF(8, 20, 600, 560), scale, p, stops, time, 1); e.Graphics.Restore(clip);
            using var casePen = new Pen(Color.FromArgb(91, 104, 130), 2 / scale);
            e.Graphics.DrawRectangle(casePen, 8, 20, 600, 560);
            using var ghost = new Pen(Color.FromArgb(100, 190, 202, 225), 2 / scale);
            e.Graphics.DrawRectangle(ghost, 150, 145, 300, 290);
            e.Graphics.DrawRectangle(ghost, 115, 402, 380, 40);
        }
        foreach (var fan in p.Fans)
        {
            float x = (float)fan.X, y = (float)fan.Y; var bounds = Gradient.FanBounds(fan);
            bool chosen = selection.Contains(fan);
            using var outline = new Pen(chosen ? Color.White : fan.Group == SelectedGroup ? Color.DeepSkyBlue : Color.FromArgb(73, 84, 111), (chosen ? 3 : fan.Group == SelectedGroup ? 2 : 1) / scale);
            using var body = new SolidBrush(Color.FromArgb(16, 21, 30));
            if (p.LayoutPreset != CaseLayout.Id || !fan.Enabled || fan.Light == LightMode.Off) e.Graphics.FillRectangle(body, bounds);
            else if (fan.Light == LightMode.Custom && ColorPickerDialog.TryParseHex(fan.CustomColor, out var own))
            { using var tint = new SolidBrush(Color.FromArgb(150, own)); e.Graphics.FillRectangle(tint, bounds); }
            e.Graphics.DrawRectangle(outline, bounds.X, bounds.Y, bounds.Width, bounds.Height);
            var model = fan.ModelInfo;
            for (int led = 0; led < model.LedsPerFan; led++)
            {
                var point = Gradient.WorldPoint(p, fan, led);
                var color = fan.Enabled ? Gradient.FanLed(p, fan, stops, inner, outer, led, time) : Color.DimGray;
                using var brush = new SolidBrush(color);
                if (p.LayoutPreset == CaseLayout.Id && !p.LedOverrides.ContainsKey($"{fan.Group}/{fan.Fan}"))
                {
                    // Each track is a separate bar: bank ends are not joined.
                    if (model.ConnectsToNext(led))
                    {
                        var end = Gradient.WorldPoint(p, fan, led + 1);
                        using var stroke = new Pen(color, model.IsInner(led) ? 4 : 2);
                        e.Graphics.DrawLine(stroke, (float)point.X, (float)point.Y, (float)end.X, (float)end.Y);
                    }
                }
                e.Graphics.FillEllipse(brush, (float)point.X - 2, (float)point.Y - 2, 4, 4);
            }
            string name = $"G{p.GroupNumber(fan.Group)} · {fan.Fan + 1}" + (fan.Light == LightMode.Off ? L10n.T(" · off") : "");
            using var labelFont = new Font(Font.FontFamily, Math.Max(11, 8 / scale));
            var textSize = e.Graphics.MeasureString(name, labelFont);
            e.Graphics.DrawString(name, labelFont, textBrush, x - textSize.Width / 2, y - textSize.Height / 2);
        }
        var pumpStops = Gradient.ParsePalette(p.Pump.Layer.Palette);
        for (int led = 0; led < 24; led++)
        {
            var point = Gradient.PumpPoint(p.Pump, led); var color = Gradient.PumpColor(p, stops, pumpStops, point, time);
            using var stroke = new Pen(color, (float)(p.Pump.Diameter*.14));
            float radius = (float)(p.Pump.Diameter*.43);
            e.Graphics.DrawArc(stroke, (float)p.Pump.X-radius, (float)p.Pump.Y-radius, radius*2, radius*2, (float)(led*15+p.Pump.Rotation-97.5), 15.5f);
        }
        foreach (var device in p.External.Where(d => d.Enabled))
        {
            int count = device.LedCount > 0 ? device.LedCount : device.Type == 1 ? 8 : 1;
            var colors = ExternalLayout.Frame(p, stops, device, count, time, device: false);
            using var outline = new Pen(Color.FromArgb(120, 150, 160, 190), 1 / scale);
            e.Graphics.DrawRectangle(outline, device.Bounds.X, device.Bounds.Y, device.Bounds.Width, device.Bounds.Height);
            for (int led = 0; led < count; led++)
            {
                var point = ExternalLayout.Point(device, led, count);
                using var brush = new SolidBrush(Color.FromArgb(colors[led*3], colors[led*3+1], colors[led*3+2]));
                float w = count == 1 ? device.Bounds.Width : (float)Math.Max(3, Math.Min(device.Width, device.Height)), h = count == 1 ? device.Bounds.Height : w;
                if (count > 1 && device.Height > device.Width) h = (float)(device.Height / count); else if (count > 1) w = (float)(device.Width / count);
                e.Graphics.FillRectangle(brush, (float)point.X - w/2, (float)point.Y - h/2, w, h);
            }
            // One label per type: otherwise labels of neighbouring memory sticks overlap.
            if (p.External.First(d => d.Enabled && d.Type == device.Type) != device) continue;
            using var small = new Font(Font.FontFamily, Math.Max(8, 6/scale));
            string tag = device.Type switch { 1 => "RAM", 2 => "GPU", _ => "RGB" };
            var tagSize = e.Graphics.MeasureString(tag, small);
            if (device.Height > device.Width) e.Graphics.DrawString(tag, small, textBrush, device.Bounds.Left, device.Bounds.Top - tagSize.Height);
            else e.Graphics.DrawString(tag, small, textBrush, device.Bounds.Right + 2, device.Bounds.Top + device.Bounds.Height/2 - tagSize.Height/2);
        }
        string pumpLabel = PumpLabel;
        using var pumpFont = new Font(Font.FontFamily, Math.Max(11, 8/scale)); var pumpText = e.Graphics.MeasureString(pumpLabel, pumpFont);
        e.Graphics.DrawString(pumpLabel, pumpFont, textBrush, (float)p.Pump.X-pumpText.Width/2, (float)p.Pump.Y-pumpText.Height/2);
        if (ResizeBounds(p) is { } resizeBox)
        {
            // Resize handles: a frame around the selection and squares of constant on-screen size.
            using var frame = new Pen(Color.FromArgb(160, 255, 255, 255), 1 / scale) { DashStyle = System.Drawing.Drawing2D.DashStyle.Dash };
            if (selection.Members(p).Count > 1) e.Graphics.DrawRectangle(frame, resizeBox.X, resizeBox.Y, resizeBox.Width, resizeBox.Height);
            float half = HandlePixels / 2f / scale;
            using var handleFill = new SolidBrush(Color.White); using var handleEdge = new Pen(Color.FromArgb(0, 120, 215), 1.5f / scale);
            foreach (var corner in Corners(resizeBox))
            {
                e.Graphics.FillRectangle(handleFill, corner.X - half, corner.Y - half, half * 2, half * 2);
                e.Graphics.DrawRectangle(handleEdge, corner.X - half, corner.Y - half, half * 2, half * 2);
            }
        }
        e.Graphics.Restore(state);
        if (selectingBox && moved)
        {
            var rect = PointerBox();
            using var fill = new SolidBrush(Color.FromArgb(40, 0, 160, 255));
            using var border = new Pen(Color.DeepSkyBlue);
            e.Graphics.FillRectangle(fill, rect); e.Graphics.DrawRectangle(border, rect);
        }
    }
}
