using System.ComponentModel;

namespace WirelessCanvas;

public static class LightLabels
{
    public static readonly string[] All = [L10n.T("Follow mapping"), L10n.T("Custom color"), L10n.T("Off")];
    public static string Of(LightMode mode) => All[(int)mode];
    public static LightMode Parse(string? text) => (LightMode)Math.Max(0, Array.IndexOf(All, text));
}

/// <summary>OpenRGB tab: output, server start, devices, mode, custom lighting, brightness and direction. Changes apply immediately.</summary>
public sealed class OpenRgbPanel : UserControl
{
    readonly Func<Profile> profile;
    readonly Func<OpenRgbBridge?> bridge;
    readonly Action changed;
    readonly CheckBox output, autoStart;
    readonly Label status = new() { AutoSize = true, MaximumSize = new(1100, 0), Margin = new Padding(0, 6, 0, 6) };
    readonly DataGridView grid = new() { Dock = DockStyle.Fill, AutoGenerateColumns = false, AllowUserToAddRows = false, AllowUserToDeleteRows = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect };
    readonly BindingList<Row> rows = [];

    public sealed class Row(ExternalPlacement placement)
    {
        public ExternalPlacement Placement { get; } = placement;
        public bool Enabled { get => Placement.Enabled; set => Placement.Enabled = value; }
        public string Kind => Placement.Type switch { 1 => L10n.T("Memory"), 2 => L10n.T("Graphics card"), 0 => L10n.T("Board"), 5 => L10n.T("Keyboard"), _ => L10n.F((FormattableString)$"Type {Placement.Type}") };
        public string Name => Placement.Name;
        public string Leds => Placement.LedCount > 0 ? Placement.LedCount.ToString() : "—";
        public string Light { get => LightLabels.Of(Placement.Light); set => Placement.Light = LightLabels.Parse(value); }
        public string Color => Placement.CustomColor;
        public int Brightness { get => (int)Math.Round(Placement.Brightness * 100); set => Placement.Brightness = Math.Clamp(value, 0, 100) / 100.0; }
        public bool Reverse { get => Placement.Reverse; set => Placement.Reverse = value; }
        public string Mode { get => Placement.Mode.Length == 0 ? L10n.T("Auto") : Placement.Mode; set => Placement.Mode = value == L10n.T("Auto") ? "" : value; }
    }

    public OpenRgbPanel(Func<Profile> profile, Func<OpenRgbBridge?> bridge, Action<bool> setOutput, Action changed)
    {
        this.profile = profile; this.bridge = bridge; this.changed = changed;
        BackColor = Ui.Background; ForeColor = Ui.Text; Dock = DockStyle.Fill;
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5 };
        root.RowStyles.Add(new(SizeType.AutoSize)); root.RowStyles.Add(new(SizeType.AutoSize)); root.RowStyles.Add(new(SizeType.AutoSize)); root.RowStyles.Add(new(SizeType.Percent, 100)); root.RowStyles.Add(new(SizeType.AutoSize));
        Controls.Add(root);
        output = new CheckBox { Name = "openRgbOutput", Text = L10n.T("Send case gradient to OpenRGB (127.0.0.1:6742)"), AutoSize = true, Checked = profile().OpenRgbOutput, Margin = new Padding(0, 8, 16, 4) };
        output.CheckedChanged += (_, _) => setOutput(output.Checked);
        autoStart = new CheckBox { Text = L10n.T("Start OpenRGB without admin rights too (UAC prompt)"), AutoSize = true, Checked = profile().OpenRgbAutoStart, Margin = new Padding(0, 8, 0, 4) };
        autoStart.CheckedChanged += (_, _) => profile().OpenRgbAutoStart = autoStart.Checked;
        var options = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = true };
        options.Controls.Add(output); options.Controls.Add(autoStart);
        var actions = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = true };
        actions.Controls.Add(Ui.Button(L10n.T("Start OpenRGB as administrator"), () => Try(() => { OpenRgbLauncher.StartElevated(); status.Text = L10n.T("OpenRGB starting. Device discovery takes about 10 seconds."); })));
        actions.Controls.Add(Ui.Button(L10n.T("Restart OpenRGB"), () => Try(() => { OpenRgbLauncher.RestartElevated(); status.Text = L10n.T("Restarting a single OpenRGB instance. Device list refreshes in about 10 seconds."); })));
        var refreshDevices = Ui.Button(L10n.T("Refresh devices"), RefreshDevices);
        refreshDevices.Name = "refreshOpenRgbDevices"; actions.Controls.Add(refreshDevices);
        actions.Controls.Add(Ui.Button(L10n.T("Custom color…"), () => PickColor(Selected())));
        actions.Controls.Add(Ui.Button(L10n.T("Follow mapping"), () => { foreach (var row in Selected()) row.Placement.Light = LightMode.Mapping; Changed(); }));
        root.Controls.Add(options, 0, 0); root.Controls.Add(actions, 0, 1); root.Controls.Add(status, 0, 2);
        foreach (var (name, title, width, edit) in new[] { ("Enabled", L10n.T("On"), 50, true), ("Kind", L10n.T("Type"), 105, false), ("Name", L10n.T("Device"), 280, false), ("Leds", "LED", 50, false),
            ("Light", L10n.T("Lighting"), 130, true), ("Color", L10n.T("Color"), 95, false), ("Mode", L10n.T("Mode"), 90, true), ("Brightness", L10n.T("Brightness, %"), 95, true), ("Reverse", L10n.T("Reverse"), 85, true) })
        {
            DataGridViewColumn column = name is "Enabled" or "Reverse" ? new DataGridViewCheckBoxColumn() :
                name == "Mode" ? new DataGridViewComboBoxColumn { DataSource = new[] { L10n.T("Auto"), "Direct", "Static" }, FlatStyle = FlatStyle.Flat } :
                name == "Light" ? new DataGridViewComboBoxColumn { DataSource = LightLabels.All, FlatStyle = FlatStyle.Flat } : new DataGridViewTextBoxColumn();
            column.Name = name; column.DataPropertyName = name; column.HeaderText = title; column.Width = width; column.ReadOnly = !edit; grid.Columns.Add(column);
        }
        Ui.StyleGrid(grid);
        grid.DataSource = rows; grid.DataError += (_, e) => { e.ThrowException = false; status.Text = L10n.T("Brightness must be an integer from 0 to 100."); };
        grid.CurrentCellDirtyStateChanged += (_, _) => { if (grid.IsCurrentCellDirty && grid.CurrentCell is DataGridViewCheckBoxCell or DataGridViewComboBoxCell) grid.CommitEdit(DataGridViewDataErrorContexts.Commit); };
        grid.CellValueChanged += (_, _) => changed();
        // The color is shown as the cell fill; clicking it opens the color picker.
        grid.CellFormatting += (_, e) =>
        {
            if (e.RowIndex < 0 || grid.Columns[e.ColumnIndex].Name != "Color") return;
            var row = rows[e.RowIndex];
            if (row.Placement.Light != LightMode.Custom) { e.Value = "—"; e.FormattingApplied = true; return; }
            if (!ColorPickerDialog.TryParseHex(row.Color, out var c)) return;
            e.CellStyle!.BackColor = c; e.CellStyle.SelectionBackColor = c; e.CellStyle.ForeColor = e.CellStyle.SelectionForeColor = c.GetBrightness() > .55 ? Color.Black : Color.White;
        };
        grid.CellClick += (_, e) => { if (e.RowIndex >= 0 && grid.Columns[e.ColumnIndex].Name == "Color") PickColor([rows[e.RowIndex]]); };
        root.Controls.Add(grid, 0, 3);
        root.Controls.Add(new Label
        {
            AutoSize = true, MaximumSize = new(1100, 0), Margin = new Padding(0, 8, 0, 0),
            Text = L10n.T("Memory requires OpenRGB running as administrator (PawnIO). Auto mode uses Static for memory (Direct ignores its colors), Direct for other devices. ") +
                   L10n.T("Gigabyte GPUs may appear twice; enable one entry only. Close GPU lighting control in Gigabyte Control Center. ") +
                   L10n.T("Drag devices on the Mapping canvas. Right-click to turn off or set a custom color. OpenRGB changes apply immediately."),
        }, 0, 4);
        Reload();
    }

    IEnumerable<Row> Selected() => grid.SelectedRows.Cast<DataGridViewRow>().Select(r => (Row)r.DataBoundItem!).DefaultIfEmpty(grid.CurrentRow?.DataBoundItem as Row).OfType<Row>();

    void PickColor(IEnumerable<Row> targets)
    {
        var list = targets.ToList(); if (list.Count == 0) return;
        ColorPickerDialog.TryParseHex(list[0].Color, out var initial);
        using var picker = new ColorPickerDialog(initial);
        if (picker.ShowDialog(FindForm()) != DialogResult.OK) return;
        foreach (var row in list) { row.Placement.CustomColor = ColorPickerDialog.Hex(picker.SelectedColor); row.Placement.Light = LightMode.Custom; }
        Changed();
    }

    void Changed() { grid.Refresh(); changed(); }

    void Try(Action action) { try { action(); } catch (Exception e) { status.Text = e.Message; } }

    /// <summary>Shared routine for the button and for the end of service startup.</summary>
    public void RefreshDevices()
    {
        bridge()?.Refresh();
        Reload();
        status.Text = L10n.T("Device list requested.");
    }

    /// <summary>Called from the window timer: bridge status and row sync with the profile.</summary>
    public void Tick()
    {
        string text = bridge()?.Status ?? L10n.T("OpenRGB output disabled. “—” under LED means the device is currently absent from OpenRGB.");
        if (status.Text != text) status.Text = text;
        if (output.Checked != profile().OpenRgbOutput) output.Checked = profile().OpenRgbOutput;
        if (rows.Count != profile().External.Count || rows.Select(r => r.Placement).Where((p, i) => !ReferenceEquals(p, profile().External[i])).Any()) Reload();
    }

    public void Reload()
    {
        rows.RaiseListChangedEvents = false; rows.Clear();
        foreach (var placement in profile().External) rows.Add(new Row(placement));
        rows.RaiseListChangedEvents = true; rows.ResetBindings();
        autoStart.Checked = profile().OpenRgbAutoStart;
    }

}
