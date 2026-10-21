using System.ComponentModel;
using System.Drawing.Drawing2D;

namespace WirelessCanvas;

/// <summary>Cooling tab: speed of wireless groups by CPU, GPU or coolant temperature.</summary>
public sealed class CoolingPanel : UserControl
{
    public static readonly string PumpTitle = L10n.T("Pump · motor");
    public static readonly string[] SourceLabels = [L10n.T("Unmanaged"), L10n.T("CPU"), L10n.T("Graphics card"), L10n.T("Coolant (pump)"), L10n.T("Fixed")];
    readonly Func<Profile> profile;
    readonly Func<WirelessGroup[]> groups;
    readonly Func<SensorReadings> readings;
    readonly Func<CoolingEngine> engine;
    readonly Action changed;
    readonly CheckBox enabled = new() { Name = "coolingEnabled", Text = L10n.T("Control fan speed"), AutoSize = true, Margin = new Padding(0, 9, 16, 0) };
    readonly CheckBox autoStart = new() { Text = L10n.T("Start CPU sensor without admin rights too (UAC prompt)"), AutoSize = true, Margin = new Padding(0, 9, 0, 0) };
    readonly Label sensors = new() { AutoSize = true, Margin = new Padding(0, 9, 24, 0), Font = new Font("Segoe UI", 10, FontStyle.Bold) };
    readonly NumericUpDown minimum = Number(15, 100, 25), failsafe = Number(50, 100, 100), hysteresis = Number(0, 10, 2);
    readonly DataGridView grid = new() { Dock = DockStyle.Fill, AutoGenerateColumns = false, AllowUserToAddRows = false, AllowUserToDeleteRows = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect, MultiSelect = false };
    readonly BindingList<Row> rows = [];
    readonly CurveView curve;
    readonly Label status = new() { AutoSize = false, Dock = DockStyle.Fill, AutoEllipsis = true };
    readonly ToolTip hints = new() { AutoPopDelay = 15000 };
    // Curve type and linear-mode limits.
    readonly SegmentedTabs kind = new() { Name = "curveKind", Items = [L10n.T("Points"), L10n.T("Linear")], AutoSize = true };
    readonly NumericUpDown fromTemp = Number(0, 119, 40), fromPercent = Number(0, 100, 30), toTemp = Number(1, 120, 80), toPercent = Number(0, 100, 100);
    readonly FlowLayoutPanel linearBox = new() { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
    bool populating;

    public sealed class Row(Func<Profile> profile, string mac, Func<WirelessGroup[]> groups, Func<CoolingEngine> engine)
    {
        public string Mac { get; } = mac;
        // The pump is the “pump” row: only Unmanaged or Constant, stored in Cooling.PumpRpm, not in Groups.
        public bool Pump => Mac == CoolingSettings.PumpKey;
        CoolingSettings Cooling => profile().Cooling;
        CoolingGroup Settings => Cooling.For(Mac);
        WirelessGroup? Live => groups().FirstOrDefault(g => Pump ? g.IsHydro : g.Mac == Mac);
        public string Group => Pump ? PumpTitle : profile().GroupTitle(Mac);
        public string Fans => Pump ? L10n.T("pump") : profile().FanCount(Mac).ToString();
        public string Source
        {
            get => Pump ? SourceLabels[Cooling.PumpRpm == null ? 0 : 4] : SourceLabels[(int)Settings.Source];
            set
            {
                if (Pump) Cooling.PumpRpm = value == SourceLabels[4] ? Cooling.PumpRpm ?? 2050 : null;
                else Settings.Source = (CoolingSource)Math.Max(0, Array.IndexOf(SourceLabels, value));
            }
        }
        public int Fixed
        {
            get => Pump ? (int)Math.Round(Controller.PumpPercent(Cooling.PumpRpm ?? 2050)) : (int)Math.Round(Settings.FixedPercent);
            set { if (!Pump) Settings.FixedPercent = Math.Clamp(value, 0, 100); else if (Cooling.PumpRpm != null) Cooling.PumpRpm = Controller.PumpStep(value); }
        }
        public string Temperature => !Pump && engine().States.TryGetValue(Mac, out var s) && s.Smoothed is double t && Settings.Source is not (CoolingSource.Off or CoolingSource.Fixed) ? $"{t:F1} °C" : "—";
        public string Target => Pump ? (Cooling.PumpRpm is int rpm ? L10n.F((FormattableString)$"{rpm} RPM") : L10n.T("unmanaged"))
            : engine().States.TryGetValue(Mac, out var s) && s.Percent is double p && Settings.Source != CoolingSource.Off ? $"{p:F0} %" + (s.Failsafe ? L10n.T(" · failsafe") : "") : L10n.T("hardware");
        public string Pwm => Live is { } g ? $"{Math.Round(g.Pwm.Take(Math.Max(1, g.Fans)).Average(v => v) * 100 / 255.0):F0} %" : "—";
        // RPM values are labelled with fan names, so you can see how each fan spins.
        public string Rpm => Pump ? (Live is { } h ? L10n.F((FormattableString)$"pump: {h.Rpm[3]}") : L10n.T("no radio connection")) : Live is { } g ? string.Join(" · ", profile().Fans.Where(f => f.Group == Mac).OrderBy(f => f.Fan).Select(f => $"{f.Fan + 1}: {g.Rpm[Math.Min(3, f.Fan)]}")) : L10n.T("disconnected");
    }

    public CoolingPanel(Func<Profile> profile, Func<WirelessGroup[]> groups, Func<SensorReadings> readings, Func<CoolingEngine> engine, Action changed)
    {
        this.profile = profile; this.groups = groups; this.readings = readings; this.engine = engine; this.changed = changed;
        BackColor = Ui.Background; ForeColor = Ui.Text; Dock = DockStyle.Fill;
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4 };
        root.RowStyles.Add(new(SizeType.AutoSize)); root.RowStyles.Add(new(SizeType.AutoSize)); root.RowStyles.Add(new(SizeType.Percent, 100)); root.RowStyles.Add(new(SizeType.Absolute, 44));
        Controls.Add(root);
        var top = Flow();
        top.Controls.Add(enabled);
        top.Controls.Add(Ui.Button(L10n.T("Start CPU sensor (administrator)"), () =>
        {
            try { CpuSensor.StartElevated(); status.Text = L10n.T("CPU sensor starting; temperature will appear shortly."); }
            catch (Exception e) { status.Text = e.Message; }
        }));
        top.Controls.Add(autoStart);
        root.Controls.Add(top, 0, 0);
        var settings = Flow();
        settings.Controls.Add(sensors);
        foreach (var (label, number, hint) in new[]
        {
            (L10n.T("Minimum, %"), minimum, L10n.T("Fans controlled by the app never run below this value.")),
            (L10n.T("Sensor lost, %"), failsafe, L10n.T("Failsafe speed if sensor data is missing for over 10 seconds (e.g. CPU sensor closed).")),
            (L10n.T("Slow down after cooling by, °C"), hysteresis, L10n.T("Hysteresis: speed increases immediately and decreases only after this temperature drop, avoiding oscillation.")),
        })
        {
            var caption = new Label { Text = label, AutoSize = true, Margin = new Padding(0, 9, 0, 0) };
            hints.SetToolTip(caption, hint); hints.SetToolTip(number, hint);
            settings.Controls.Add(caption); settings.Controls.Add(number);
        }
        root.Controls.Add(settings, 0, 1);
        var split = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
        split.ColumnStyles.Add(new(SizeType.Percent, 60)); split.ColumnStyles.Add(new(SizeType.Percent, 40));
        foreach (var (name, title, width, edit) in new[] { ("Group", L10n.T("Group"), 140, false), ("Fans", L10n.T("Fans"), 50, false), ("Source", L10n.T("Source"), 130, true), ("Fixed", L10n.T("Fixed, %"), 60, true),
            ("Temperature", L10n.T("Temp."), 70, false), ("Target", L10n.T("Target"), 95, false), ("Rpm", L10n.T("Speed, RPM"), 190, false) })
        {
            DataGridViewColumn column = name == "Source" ? new DataGridViewComboBoxColumn { DataSource = SourceLabels, FlatStyle = FlatStyle.Flat } : new DataGridViewTextBoxColumn();
            column.Name = name; column.DataPropertyName = name; column.HeaderText = title; column.FillWeight = width; column.MinimumWidth = 40; column.ReadOnly = !edit; grid.Columns.Add(column);
        }
        Ui.StyleGrid(grid);
        grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill; // the table always fits its column width, without scrolling
        // Group and fan names wrap to a second line instead of being cut off.
        grid.DefaultCellStyle.WrapMode = DataGridViewTriState.True; grid.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCells;
        grid.DataBindingComplete += (_, _) =>
        {
            foreach (DataGridViewRow row in grid.Rows)
                if (row.DataBoundItem is Row { Pump: true } && row.Cells["Source"] is DataGridViewComboBoxCell cell && cell.Items.Count != 2)
                    cell.DataSource = new[] { SourceLabels[0], SourceLabels[4] };
        };
        grid.DataSource = rows; grid.DataError += (_, e) => { e.ThrowException = false; status.Text = L10n.T("Fixed speed must be an integer from 0 to 100."); };
        grid.CurrentCellDirtyStateChanged += (_, _) => { if (grid.IsCurrentCellDirty && grid.CurrentCell is DataGridViewComboBoxCell) grid.CommitEdit(DataGridViewDataErrorContexts.Commit); };
        grid.CellValueChanged += (_, _) => { curve?.Invalidate(); changed(); };
        // Row selection and current cell change in different orders (mouse, keyboard, code); the chart follows both.
        void FollowRow() { if (curve == null || grid.CurrentRow?.DataBoundItem is not Row row || curve.Mac == row.Mac) return; curve.Mac = row.Mac; ShowCurveSettings(); curve.Invalidate(); }
        grid.SelectionChanged += (_, _) => FollowRow();
        grid.CurrentCellChanged += (_, _) => FollowRow();
        split.Controls.Add(grid, 0, 0);
        curve = new CurveView(profile, engine) { Dock = DockStyle.Fill };
        curve.Changed += () => { ShowCurveSettings(); changed(); grid.Refresh(); };
        var right = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = Padding.Empty };
        right.RowStyles.Add(new(SizeType.AutoSize)); right.RowStyles.Add(new(SizeType.Percent, 100));
        var toolbar = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true, Margin = Padding.Empty };
        toolbar.Controls.Add(new Label { Text = L10n.T("Curve"), AutoSize = true, Margin = new Padding(0, 12, 8, 0) });
        toolbar.Controls.Add(kind);
        void Caption(string text, int left) => linearBox.Controls.Add(new Label { Text = text, AutoSize = true, Margin = new Padding(left, 12, 4, 0) });
        foreach (var number in new[] { fromTemp, fromPercent, toTemp, toPercent }) { number.Width = 52; number.Margin = new Padding(0, 8, 2, 0); }
        Caption(L10n.T("from"), 8); linearBox.Controls.Add(fromTemp); Caption("°C:", 0); linearBox.Controls.Add(fromPercent); Caption(L10n.T("%   to"), 0);
        linearBox.Controls.Add(toTemp); Caption("°C:", 0); linearBox.Controls.Add(toPercent); Caption("%", 0);
        hints.SetToolTip(linearBox, L10n.T("Below the first temperature: first speed. Above the second: second speed. Linear between them."));
        toolbar.Controls.Add(linearBox);
        right.Controls.Add(toolbar, 0, 0); right.Controls.Add(curve, 0, 1);
        split.Controls.Add(right, 1, 0);
        kind.SelectedChanged += index =>
        {
            if (populating || curve.Mac is not { } mac || mac == CoolingSettings.PumpKey) return;
            profile().Cooling.For(mac).Kind = (CurveKind)index; ShowCurveSettings(); curve.Invalidate(); changed();
        };
        foreach (var number in new[] { fromTemp, fromPercent, toTemp, toPercent }) number.ValueChanged += (_, _) => ReadLinear();
        root.Controls.Add(split, 0, 2);
        root.Controls.Add(status, 0, 3);
        enabled.CheckedChanged += (_, _) => { if (populating) return; profile().Cooling.Enabled = enabled.Checked; changed(); };
        autoStart.CheckedChanged += (_, _) => { if (!populating) profile().Cooling.CpuSensorAutoStart = autoStart.Checked; };
        foreach (var number in new[] { minimum, failsafe, hysteresis }) number.ValueChanged += (_, _) => Read();
        Reload();
    }

    static NumericUpDown Number(decimal min, decimal max, decimal value) => new() { Minimum = min, Maximum = max, Value = value, Width = 60, Margin = new Padding(3, 6, 12, 3) };
    static FlowLayoutPanel Flow() => new() { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true, Margin = Padding.Empty };
    public IEnumerable<Row> Rows => rows;
    public CurveView Curve => curve;

    void Read()
    {
        if (populating) return;
        var c = profile().Cooling;
        c.MinPercent = (double)minimum.Value; c.FailsafePercent = (double)failsafe.Value; c.Hysteresis = (double)hysteresis.Value;
        curve.Invalidate(); changed();
    }

    /// <summary>Rows are the wireless groups of the case layout (G1–G4), in numeric order.</summary>
    public void Reload()
    {
        populating = true;
        try
        {
            var p = profile(); var c = p.Cooling;
            enabled.Checked = c.Enabled; autoStart.Checked = c.CpuSensorAutoStart;
            minimum.Value = (decimal)Math.Clamp(c.MinPercent, 15, 100); failsafe.Value = (decimal)Math.Clamp(c.FailsafePercent, 50, 100); hysteresis.Value = (decimal)Math.Clamp(c.Hysteresis, 0, 10);
            // The pump is the last row; its chart is a single horizontal line.
            var macs = p.Fans.Select(f => f.Group).Distinct().OrderBy(p.GroupNumber).Append(CoolingSettings.PumpKey).ToArray();
            if (!macs.SequenceEqual(rows.Select(r => r.Mac)))
            {
                rows.RaiseListChangedEvents = false; rows.Clear();
                foreach (var mac in macs) rows.Add(new Row(profile, mac, groups, engine));
                rows.RaiseListChangedEvents = true; rows.ResetBindings();
            }
            curve.Mac ??= rows.FirstOrDefault()?.Mac;
            ShowCurveSettings();
        }
        finally { populating = false; }
        grid.Refresh(); curve.Invalidate();
    }

    public void RefreshNames() { grid.Refresh(); curve.Invalidate(); }
    public void SelectRow(string mac)
    {
        var row = grid.Rows.Cast<DataGridViewRow>().FirstOrDefault(r => (r.DataBoundItem as Row)?.Mac == mac);
        if (row != null) grid.CurrentCell = row.Cells[0];
    }

    public SegmentedTabs KindTabs => kind;
    public (NumericUpDown FromTemp, NumericUpDown FromPercent, NumericUpDown ToTemp, NumericUpDown ToPercent) LinearInputs => (fromTemp, fromPercent, toTemp, toPercent);

    /// <summary>Shows the curve type of the selected group and the linear-mode limits.</summary>
    void ShowCurveSettings()
    {
        if (curve?.Mac is not { } mac) return;
        if (mac == CoolingSettings.PumpKey) { linearBox.Visible = false; kind.Enabled = false; return; }
        var g = profile().Cooling.For(mac);
        populating = true;
        try
        {
            kind.Select((int)g.Kind, animate: kind.IsHandleCreated && kind.Visible);
            fromTemp.Value = (decimal)Math.Clamp(g.Linear[0].Temperature, 0, 119); fromPercent.Value = (decimal)Math.Clamp(g.Linear[0].Percent, 0, 100);
            toTemp.Value = (decimal)Math.Clamp(g.Linear[1].Temperature, 1, 120); toPercent.Value = (decimal)Math.Clamp(g.Linear[1].Percent, 0, 100);
            linearBox.Visible = g.Kind == CurveKind.Linear;
            kind.Enabled = linearBox.Enabled = g.Source is CoolingSource.Cpu or CoolingSource.Gpu or CoolingSource.Coolant;
        }
        finally { populating = false; }
    }

    /// <summary>Linear-mode limits from the fields: the first temperature is always at least 1 °C below the second.</summary>
    void ReadLinear()
    {
        if (populating || curve?.Mac is not { } mac || mac == CoolingSettings.PumpKey) return;
        var line = profile().Cooling.For(mac).Linear;
        double from = (double)fromTemp.Value, to = (double)toTemp.Value;
        if (to <= from) { to = Math.Min(120, from + 1); populating = true; toTemp.Value = (decimal)to; populating = false; }
        line[0].Temperature = from; line[0].Percent = (double)fromPercent.Value;
        line[1].Temperature = to; line[1].Percent = (double)toPercent.Value;
        curve.Invalidate(); changed();
    }

    /// <summary>Window timer: sensor readings, targets, PWM and RPM.</summary>
    public void Tick(string controllerStatus)
    {
        if (!Visible) return;
        var r = readings();
        string text = L10n.F((FormattableString)$"CPU {Format(r.Cpu)} · GPU {Format(r.Gpu)} · coolant {Format(r.Coolant)}");
        if (sensors.Text != text) sensors.Text = text;
        if (grid.Columns["Temperature"] is { } t) { grid.InvalidateColumn(t.Index); grid.InvalidateColumn(grid.Columns["Target"]!.Index); grid.InvalidateColumn(grid.Columns["Rpm"]!.Index); }
        curve.Invalidate();
        var c = profile().Cooling;
        string note = !c.Enabled ? L10n.T("Control disabled: fans use hardware speed. Enable control and select a source for each group.")
            : r.Cpu == null && c.Groups.Any(g => g.Source == CoolingSource.Cpu) ? L10n.T("No CPU temperature: start the CPU sensor. CPU groups use hardware speed until available; loss during operation triggers failsafe after 10 s.")
            : controllerStatus;
        if (status.Text != note) status.Text = note;
    }
    static string Format(double? value) => value is double v ? $"{v:F0} °C" : "—";
}

/// <summary>Group speed curve: drag points, double-click adds a point, right-click removes one.</summary>
public sealed class CurveView : Control
{
    public const double MinTemp = 20, MaxTemp = 100;
    readonly Func<Profile> profile;
    readonly Func<CoolingEngine> engine;
    int dragging = -1;
    public event Action? Changed;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string? Mac { get; set; }
    public CurveView(Func<Profile> profile, Func<CoolingEngine> engine)
    {
        this.profile = profile; this.engine = engine;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = Color.FromArgb(12, 15, 22); ForeColor = Ui.Text; Cursor = Cursors.Cross;
    }
    bool PumpView => Mac == CoolingSettings.PumpKey;
    CoolingGroup? Group => Mac == null || PumpView ? null : profile().Cooling.For(Mac);
    float PumpLineY => ToScreen(MinTemp, Controller.PumpPercent(profile().Cooling.PumpRpm ?? 2050)).Y;
    bool PumpLineHit(Point p) => PumpView && profile().Cooling.PumpRpm != null && Plot.Contains(p.X, Math.Clamp(p.Y, Plot.Top, Plot.Bottom)) && Math.Abs(p.Y - PumpLineY) <= 10;
    /// <summary>Pump line at the scale-percent height: RPM in steps of 50, the same at any temperature.</summary>
    public void SetPumpLine(double percent)
    {
        if (!PumpView || profile().Cooling.PumpRpm == null) return;
        profile().Cooling.PumpRpm = Controller.PumpStep(Math.Clamp(percent, 0, 100));
        Invalidate(); Changed?.Invoke();
    }
    bool Editable => Group?.Source is CoolingSource.Cpu or CoolingSource.Gpu or CoolingSource.Coolant;
    RectangleF Plot => new(54, 16, Math.Max(10, Width - 54 - 32), Math.Max(10, Height - 52));
    public PointF ToScreen(double temperature, double percent) { var r = Plot; return new((float)(r.Left + (temperature - MinTemp) / (MaxTemp - MinTemp) * r.Width), (float)(r.Bottom - percent / 100 * r.Height)); }
    public (double Temperature, double Percent) FromScreen(PointF p) { var r = Plot; return (MinTemp + (p.X - r.Left) / r.Width * (MaxTemp - MinTemp), (r.Bottom - p.Y) / r.Height * 100); }
    int Hit(Point p) => Group is { } g ? g.Active.FindIndex(c => { var s = ToScreen(c.Temperature, c.Percent); return Math.Abs(s.X - p.X) <= 8 && Math.Abs(s.Y - p.Y) <= 8; }) : -1;

    /// <summary>Moves a point: temperature between its neighbours (1 °C step), speed 0–100 %.</summary>
    public void MovePoint(int index, double temperature, double percent)
    {
        if (Group is not { } g || index < 0 || index >= g.Active.Count) return;
        g.Active.Sort((a, b) => a.Temperature.CompareTo(b.Temperature));
        double low = index > 0 ? g.Active[index - 1].Temperature + 1 : 0, high = index < g.Active.Count - 1 ? g.Active[index + 1].Temperature - 1 : 120;
        g.Active[index].Temperature = Math.Round(Math.Clamp(temperature, low, high));
        g.Active[index].Percent = Math.Round(Math.Clamp(percent, 0, 100));
        Invalidate(); Changed?.Invoke();
    }
    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Left && PumpLineHit(e.Location)) { dragging = -2; Capture = true; return; }
        if (!Editable || Group is not { } g) return;
        int hit = Hit(e.Location);
        if (e.Button == MouseButtons.Right && hit >= 0 && g.Kind == CurveKind.Points && g.Active.Count > 2) { g.Active.RemoveAt(hit); Invalidate(); Changed?.Invoke(); return; }
        if (e.Button == MouseButtons.Left && hit >= 0) { g.Active.Sort((a, b) => a.Temperature.CompareTo(b.Temperature)); dragging = Hit(e.Location); Capture = true; }
    }
    protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); if (dragging == -2) SetPumpLine(FromScreen(e.Location).Percent); else if (PumpView) Cursor = PumpLineHit(e.Location) ? Cursors.SizeNS : Cursors.Default; else if (dragging >= 0) { var (t, p) = FromScreen(e.Location); MovePoint(dragging, t, p); } else Cursor = Editable && Hit(e.Location) >= 0 ? Cursors.Hand : Cursors.Cross; }
    protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); dragging = -1; Capture = false; }
    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        if (!Editable || Group is not { } g || g.Kind != CurveKind.Points || g.Active.Count >= 12 || !Plot.Contains(e.Location) || Hit(e.Location) >= 0) return;
        var (t, p) = FromScreen(e.Location);
        if (g.Active.Any(c => Math.Abs(c.Temperature - Math.Round(t)) < 1)) return;
        g.Active.Add(new CurvePoint(Math.Round(t), Math.Round(Math.Clamp(p, 0, 100))));
        g.Active.Sort((a, b) => a.Temperature.CompareTo(b.Temperature)); Invalidate(); Changed?.Invoke();
    }
    /// <summary>Pump: one horizontal line, the RPM does not depend on temperature. Left scale in RPM.</summary>
    void DrawPump(Graphics g, RectangleF plot, Color muted)
    {
        int? rpm = profile().Cooling.PumpRpm;
        TextRenderer.DrawText(g, $"{CoolingPanel.PumpTitle} · {(rpm == null ? CoolingPanel.SourceLabels[0] : CoolingPanel.SourceLabels[4])}", Font, new Point((int)plot.Left + 8, (int)plot.Top + 4), ForeColor);
        TextRenderer.DrawText(g, L10n.T("Independent of temperature · via L-Wireless"), Font, new Point((int)plot.Left + 8, (int)plot.Top + 24), muted);
        if (rpm is not int value)
        {
            TextRenderer.DrawText(g, L10n.T("Pump unmanaged; select Fixed in the table"), Font, Rectangle.Round(plot), muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            return;
        }
        float y = PumpLineY;
        using (var line = new Pen(Color.DeepSkyBlue, 3)) g.DrawLine(line, plot.Left, y, plot.Right, y);
        using (var handle = new SolidBrush(Color.White)) g.FillEllipse(handle, plot.Left + plot.Width / 2 - 6, y - 6, 12, 12);
        TextRenderer.DrawText(g, L10n.F((FormattableString)$"{value} RPM"), Font, new Point((int)(plot.Left + plot.Width / 2 + 12), (int)y - 22), Color.DeepSkyBlue);
        TextRenderer.DrawText(g, L10n.T("Drag the line up or down · 50 RPM steps"), Font, new Point((int)plot.Left + 8, (int)plot.Bottom - 22), muted);
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; g.Clear(BackColor); g.SmoothingMode = SmoothingMode.AntiAlias;
        var plot = Plot; var p = profile(); var group = Group;
        using var grid = new Pen(Color.FromArgb(34, 40, 54)); using var text = new SolidBrush(Color.FromArgb(150, 160, 185));
        for (int t = 20; t <= 100; t += 10) { var x = ToScreen(t, 0).X; g.DrawLine(grid, x, plot.Top, x, plot.Bottom); TextRenderer.DrawText(g, $"{t}°", Font, new Point((int)x - 12, (int)plot.Bottom + 6), text.Color); }
        for (int s = 0; s <= 100; s += 20) { var y = ToScreen(MinTemp, s).Y; g.DrawLine(grid, plot.Left, y, plot.Right, y); TextRenderer.DrawText(g, PumpView ? $"{Controller.PumpRpm(s)}" : $"{s}%", Font, new Point(PumpView ? 4 : 8, (int)y - 9), text.Color); }
        if (PumpView) { DrawPump(g, plot, text.Color); return; }
        // Speed never goes below the minimum, so that area is shaded.
        float floorY = ToScreen(MinTemp, Math.Max(14, p.Cooling.MinPercent)).Y;
        using (var floor = new SolidBrush(Color.FromArgb(40, 255, 90, 90))) g.FillRectangle(floor, plot.Left, floorY, plot.Width, plot.Bottom - floorY);
        if (group == null) return;
        // Which group and which fans are being configured, by name.
        string title = $"{p.GroupTitle(group.Mac)} · {CoolingPanel.SourceLabels[(int)group.Source]}";
        TextRenderer.DrawText(g, L10n.F((FormattableString)$"{p.FanCount(group.Mac)} fans · shared group speed"), Font, new Point((int)plot.Left + 8, (int)plot.Top + 24), Color.FromArgb(150, 160, 185));
        TextRenderer.DrawText(g, title, Font, new Point((int)plot.Left + 8, (int)plot.Top + 4), ForeColor);
        if (!Editable)
        {
            string note = group.Source == CoolingSource.Fixed ? L10n.F((FormattableString)$"Fixed speed {group.FixedPercent:F0}%") : L10n.T("Group unmanaged · hardware speed");
            TextRenderer.DrawText(g, note, Font, Rectangle.Round(plot), text.Color, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            if (group.Source == CoolingSource.Fixed) { var y = ToScreen(MinTemp, Math.Max(group.FixedPercent, p.Cooling.MinPercent)).Y; using var fixedPen = new Pen(Color.DeepSkyBlue, 2); g.DrawLine(fixedPen, plot.Left, y, plot.Right, y); }
            return;
        }
        var points = group.Active.OrderBy(c => c.Temperature).Select(c => ToScreen(c.Temperature, c.Percent)).ToList();
        points.Insert(0, new PointF(plot.Left, points[0].Y)); points.Add(new PointF(plot.Right, points[^1].Y));
        using (var line = new Pen(Color.DeepSkyBlue, 2.5f)) g.DrawLines(line, points.ToArray());
        foreach (var c in group.Active)
        {
            var s = ToScreen(c.Temperature, c.Percent);
            using var dot = new SolidBrush(Color.White); g.FillEllipse(dot, s.X - 5, s.Y - 5, 10, 10);
        }
        if (engine().States.TryGetValue(group.Mac, out var state) && state.Smoothed is double temperature && state.Percent is double percent)
        {
            var now = ToScreen(Math.Clamp(temperature, MinTemp, MaxTemp), percent);
            using var marker = new Pen(Color.FromArgb(255, 200, 80), 1.5f) { DashStyle = DashStyle.Dash };
            g.DrawLine(marker, now.X, plot.Top, now.X, plot.Bottom);
            using var current = new SolidBrush(Color.FromArgb(255, 200, 80)); g.FillEllipse(current, now.X - 6, now.Y - 6, 12, 12);
            TextRenderer.DrawText(g, $"{temperature:F0} °C → {percent:F0} %", Font, new Point((int)now.X + 8, (int)now.Y - 22), current.Color);
        }
        TextRenderer.DrawText(g, group.Kind == CurveKind.Linear ? L10n.T("Drag endpoints or enter values above") : L10n.T("Drag points · double-click to add · right-click to remove"), Font, new Point((int)plot.Left + 8, (int)plot.Bottom - 22), text.Color);
    }
}
