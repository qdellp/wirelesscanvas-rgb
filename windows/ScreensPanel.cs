using System.Drawing.Drawing2D;

namespace WirelessCanvas;

/// <summary>
/// Screens tab: a card for the pump and each LCD fan with preview, mode, file, sensor,
/// brightness and rotation. Show on screen sends the settings to the screen thread and saves the profile.
/// </summary>
public sealed class ScreensPanel : UserControl
{
    readonly Func<Profile> profile;
    readonly Func<ScreenHub?> hub;
    readonly Func<SensorReadings> readings;
    readonly Func<SensorHistory> history;
    readonly Action save;
    readonly Func<double> sceneSeconds;
    long nextPreview;
    internal double SceneSeconds => sceneSeconds();
    readonly FlowLayoutPanel list = new() { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, Padding = new Padding(0, 0, 8, 8) };
    readonly Label status = new() { AutoSize = false, Dock = DockStyle.Fill, AutoEllipsis = true };
    readonly List<ScreenCard> cards = [];
    public IReadOnlyList<ScreenCard> Cards => cards;

    public ScreensPanel(Func<Profile> profile, Func<ScreenHub?> hub, Func<SensorReadings> readings, Func<SensorHistory> history, Action save, Func<double>? sceneSeconds = null)
    {
        this.profile = profile; this.hub = hub; this.readings = readings; this.history = history; this.save = save;
        this.sceneSeconds = sceneSeconds ?? (() => Environment.TickCount64 / 1000.0);
        BackColor = Ui.Background; ForeColor = Ui.Text; Dock = DockStyle.Fill;
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
        root.RowStyles.Add(new(SizeType.AutoSize)); root.RowStyles.Add(new(SizeType.Percent, 100)); root.RowStyles.Add(new(SizeType.Absolute, 26));
        Controls.Add(root);
        var top = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, Margin = Padding.Empty };
        top.Controls.Add(new Label { Text = L10n.T("Screens receive content via USB while the app runs. Unmanaged sends nothing to the screen."), AutoSize = true, Margin = new Padding(0, 9, 16, 6), ForeColor = Color.FromArgb(150, 160, 185) });
        top.Controls.Add(Ui.Button(L10n.T("Find screens"), () =>
        {
            if (hub() is not { } h) { Note(L10n.T("Preview: screens are not queried.")); return; }
            try { h.Rescan(); Reload(); Note(L10n.F((FormattableString)$"Screens found: {h.Found.Count}.")); } catch (Exception e) { Note(e.Message); }
        }));
        root.Controls.Add(top, 0, 0);
        root.Controls.Add(list, 0, 1); root.Controls.Add(status, 0, 2);
        list.Resize += (_, _) => FitCards();
        Reload();
    }

    internal void Note(string text) { if (status.Text != text) status.Text = text; }
    void FitCards() { int width = Math.Max(760, list.ClientSize.Width - list.Padding.Horizontal - 4); foreach (Control card in list.Controls) card.Width = width; }

    /// <summary>The pump is always first; LCD fans are those found over USB and those saved in the profile, by serial number.</summary>
    public void Reload()
    {
        var p = profile();
        p.Screen(ScreenSettings.PumpId, L10n.T("Pump"));
        var serials = (hub()?.Found.Where(f => f.Kind == ScreenKind.Fan).Select(f => f.Serial) ?? [])
            .Concat(p.Screens.Where(s => !s.IsPump).Select(s => s.Id)).Distinct().OrderBy(s => s, StringComparer.Ordinal).ToArray();
        for (int i = 0; i < serials.Length; i++) p.Screen(serials[i], L10n.F((FormattableString)$"LCD fan {i + 1}"));
        var ordered = new[] { p.Screen(ScreenSettings.PumpId, L10n.T("Pump")) }.Concat(serials.Select(s => p.Screens.First(x => x.Id == s))).ToArray();
        if (ordered.Select(s => s.Id).SequenceEqual(cards.Select(c => c.Id))) { foreach (var card in cards) card.Reload(); return; }
        list.SuspendLayout();
        foreach (Control control in list.Controls.Cast<Control>().ToArray()) control.Dispose();
        list.Controls.Clear(); cards.Clear();
        for (int i = 0; i < ordered.Length; i++)
        {
            var card = new ScreenCard(this, profile, ordered[i].Id, i);
            cards.Add(card); list.Controls.Add(card);
        }
        FitCards(); list.ResumeLayout();
    }

    internal ScreenHub? Hub => hub();
    internal SensorReadings Readings => readings();
    internal SensorHistory History => history();
    internal void Save() => save();
    public void Tick() { if (IsDisposed || !Visible || Environment.TickCount64 < nextPreview) return; nextPreview = Environment.TickCount64 + 200; foreach (var card in cards) card.Tick(); }
}

/// <summary>Screen card: preview on the left (circle for the pump, square for a fan), settings on the right.</summary>
public sealed class ScreenCard : Panel
{
    static readonly Color Muted = Color.FromArgb(150, 160, 185);
    readonly ScreensPanel owner;
    readonly Func<Profile> profile;
    readonly int index;
    readonly ScreenPreview preview = new() { Width = 236, Height = 236, Margin = new Padding(0, 0, 20, 0) };
    readonly Label state = new() { AutoSize = true, ForeColor = Muted, Margin = new Padding(12, 9, 0, 0) };
    readonly TextBox name;
    public readonly SegmentedTabs Mode = new() { Name = "screenMode", Items = ScreenSettings.ModeLabels, AutoSize = true };
    readonly SegmentedTabs fit = new() { Items = [L10n.T("Fill"), L10n.T("Fit")], AutoSize = true };
    readonly SegmentedTabs rotation = new() { Items = ["0°", "90°", "180°", "270°"], AutoSize = true };
    readonly ComboBox sensor = new() { DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat, Width = 130, BackColor = Color.FromArgb(22, 25, 33), ForeColor = Ui.Text, Margin = new Padding(0, 6, 12, 4) };
    readonly TextBox label;
    public readonly SegmentedTabs Theme = new() { Name = "dashboardTheme", Items = Dashboard.ThemeLabels, AutoSize = true };
    readonly CheckBox[] multi = [.. new[] { "CPU", "GPU", L10n.T("Coolant") }.Select(t => new CheckBox { Text = t, AutoSize = true, Margin = new Padding(0, 9, 10, 0) })];
    static readonly CoolingSource[] MultiSources = [CoolingSource.Cpu, CoolingSource.Gpu, CoolingSource.Coolant];
    public readonly ColorSwatch[] Swatches;
    readonly NumericUpDown brightness = Number(0, 100, 80), zoom = Number(100, 400, 100), fps = Number(1, 30, 20), arcMin = Number(-20, 140, 20), arcMax = Number(-15, 150, 90);
    readonly Label file = new() { AutoSize = true, ForeColor = Muted, Margin = new Padding(0, 9, 12, 0), MaximumSize = new Size(420, 0), AutoEllipsis = true };
    readonly PillButton pick;
    readonly FlowLayoutPanel pictureRow, sensorRow;
    readonly FlowLayoutPanel mappingRow;
    public readonly ComboBox MappingSource = new() { Name = "mappingSource", DropDownStyle = ComboBoxStyle.DropDownList, Width = 310, Margin = new Padding(3, 6, 12, 3) };
    readonly NumericUpDown mappingFps = Number(1, 15, 15);
    sealed record Source(string Group, int Fan, string Title) { public override string ToString() => Title; }
    readonly Label fpsCaption;
    bool populating;
    string previewKey = "";
    public string Id { get; }
    ScreenSettings S => profile().Screens.First(s => s.Id == Id);

    public ScreenCard(ScreensPanel owner, Func<Profile> profile, string id, int index)
    {
        this.owner = owner; this.profile = profile; Id = id; this.index = index;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = Ui.Background; ForeColor = Ui.Text; Padding = new Padding(16, 14, 16, 14); Margin = new Padding(0, 0, 0, 12);
        var s = S;
        var layout = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, BackColor = Ui.Card };
        layout.ColumnStyles.Add(new(SizeType.AutoSize)); layout.ColumnStyles.Add(new(SizeType.Percent, 100));
        preview.Round = s.IsPump;
        layout.Controls.Add(preview, 0, 0);
        var right = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, Dock = DockStyle.Fill, BackColor = Ui.Card, Margin = Padding.Empty };
        var header = Row();
        header.Controls.Add(new Label { Text = s.IsPump ? L10n.T("Pump · 480×480") : L10n.T("LCD fan · 400×400"), AutoSize = true, Font = new Font("Segoe UI", 12, FontStyle.Bold), Margin = new Padding(0, 6, 10, 0) });
        name = GroupCard.Box(s.Name, s.IsPump ? L10n.T("Pump") : L10n.T("For example, G1·1 bottom"), 200);
        name.TextChanged += (_, _) => { if (!populating) { S.Name = name.Text.Trim(); owner.Note(L10n.T("Screen name changed. Saved with Show on screen or Save profile.")); } };
        header.Controls.Add(name); header.Controls.Add(state);
        right.Controls.Add(header);
        var modeRow = Row(); modeRow.Controls.Add(Mode); right.Controls.Add(modeRow);
        Mode.SelectedChanged += i => { if (populating) return; S.Mode = (ScreenMode)i; ShowMode(); Changed(); };

        pictureRow = Row();
        pick = Ui.Button(L10n.T("Choose file…"), PickFile);
        pictureRow.Controls.Add(pick); pictureRow.Controls.Add(file);
        pictureRow.Controls.Add(fit); Caption(pictureRow, L10n.T("Scale, %")); pictureRow.Controls.Add(zoom);
        fpsCaption = Caption(pictureRow, L10n.T("FPS limit")); pictureRow.Controls.Add(fps);
        right.Controls.Add(pictureRow);
        fit.SelectedChanged += i => { if (!populating) { S.Fit = (ScreenFit)i; Changed(); } };
        mappingRow = Row();
        if (s.IsPump) Caption(mappingRow, L10n.T("Pump position on the canvas"));
        else
        {
            mappingRow.Controls.Add(MappingSource);
            MappingSource.SelectedIndexChanged += (_, _) =>
            {
                if (populating) return;
                if (MappingSource.SelectedItem is Source source) { S.MappingGroup = source.Group; S.MappingFan = source.Fan; }
                else S.MappingGroup = "";
                Changed();
            };
        }
        Caption(mappingRow, L10n.T("FPS limit")); mappingRow.Controls.Add(mappingFps);
        right.Controls.Add(mappingRow);

        // Sensor: theme, what to show, scale and colors of individual elements.
        sensorRow = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, BackColor = Ui.Card, Margin = Padding.Empty };
        var themeRow = Row(); Caption(themeRow, L10n.T("Theme")); themeRow.Controls.Add(Theme); sensorRow.Controls.Add(themeRow);
        Theme.SelectedChanged += i => { if (!populating) { S.Theme = (DashboardTheme)i; ShowMode(); Changed(); } };
        var sourceRow = Row();
        sensor.Items.AddRange([L10n.T("CPU"), L10n.T("Graphics card"), L10n.T("Coolant (pump)")]);
        sourceRow.Controls.Add(sensor);
        foreach (var box in multi) sourceRow.Controls.Add(box);
        label = GroupCard.Box(s.Label, L10n.T("Label: CPU"), 110); label.MaxLength = 24;
        sourceRow.Controls.Add(label);
        Caption(sourceRow, L10n.T("Scale from")); sourceRow.Controls.Add(arcMin); Caption(sourceRow, L10n.T("to, °C")); sourceRow.Controls.Add(arcMax);
        sensorRow.Controls.Add(sourceRow);
        var colorRow = Row(); Caption(colorRow, L10n.T("Colors"));
        Swatches =
        [
            new ColorSwatch(L10n.T("Background"), () => S.BackgroundColor, v => S.BackgroundColor = v, "#000000", null),
            new ColorSwatch(L10n.T("Accent"), () => S.Color, v => S.Color = v, "", L10n.T("palette")),
            new ColorSwatch(L10n.T("Scale"), () => S.TrackColor, v => S.TrackColor = v, "#262B36", null),
            new ColorSwatch(L10n.T("Value"), () => S.ValueColor, v => S.ValueColor = v, "", L10n.T("auto")),
            new ColorSwatch(L10n.T("Label"), () => S.LabelColor, v => S.LabelColor = v, "#9AA3B5", null),
        ];
        foreach (var swatch in Swatches) { swatch.Changed += Changed; colorRow.Controls.Add(swatch); }
        GroupCard.Hints.SetToolTip(colorRow, L10n.T("Accent colors arcs, rings, marks, bars and graph lines. Palette picks a case gradient color from the value on the scale."));
        sensorRow.Controls.Add(colorRow);
        right.Controls.Add(sensorRow);
        sensor.SelectedIndexChanged += (_, _) => { if (!populating) { S.Sensor = sensor.SelectedIndex switch { 1 => CoolingSource.Gpu, 2 => CoolingSource.Coolant, _ => CoolingSource.Cpu }; Changed(); } };
        label.TextChanged += (_, _) => { if (!populating) { S.Label = label.Text.Trim(); Changed(); } };
        foreach (var box in multi) box.CheckedChanged += (_, _) => ReadMulti();

        var common = Row();
        Caption(common, L10n.T("Brightness, %")); common.Controls.Add(brightness);
        Caption(common, L10n.T("Rotation")); common.Controls.Add(rotation);
        rotation.SelectedChanged += i => { if (!populating) { S.Rotation = i * 90; Changed(); } };
        right.Controls.Add(common);
        var actions = Row();
        actions.Controls.Add(Ui.Button(L10n.T("Show on screen"), Send));
        if (!s.IsPump) actions.Controls.Add(Ui.Button(L10n.T("Show number"), Identify));
        right.Controls.Add(actions);
        foreach (var number in new[] { brightness, zoom, fps, mappingFps, arcMin, arcMax }) number.ValueChanged += (_, _) => ReadNumbers();
        GroupCard.Hints.SetToolTip(fps, L10n.T("GIF frame rate is capped at this value to limit screen and USB load."));
        GroupCard.Hints.SetToolTip(rotation, L10n.T("Rotate to match the screen orientation in the case."));
        layout.Controls.Add(right, 1, 0);
        Controls.Add(layout);
        layout.SizeChanged += (_, _) => Height = layout.Height + Padding.Vertical;
        Reload();
    }

    static FlowLayoutPanel Row() => new() { AutoSize = true, WrapContents = true, BackColor = Ui.Card, Margin = new Padding(0, 0, 0, 4), MaximumSize = new Size(900, 0) };
    static Label Caption(Control row, string text) { var caption = new Label { Text = text, AutoSize = true, Margin = new Padding(4, 9, 0, 0) }; row.Controls.Add(caption); return caption; }
    static NumericUpDown Number(decimal min, decimal max, decimal value) => new() { Minimum = min, Maximum = max, Value = value, Width = 62, Margin = new Padding(3, 6, 12, 3) };

    public void Reload()
    {
        var s = S; populating = true;
        try
        {
            if (name.Text != s.Name) name.Text = s.Name;
            Mode.Select((int)s.Mode, animate: false); fit.Select((int)s.Fit, animate: false); rotation.Select(s.Rotation / 90, animate: false);
            brightness.Value = Math.Clamp(s.Brightness, 0, 100); zoom.Value = (decimal)Math.Clamp(Math.Round(s.Zoom * 100), 100, 400); fps.Value = Math.Clamp(s.MaxFps, 1, 30);
            mappingFps.Value = Math.Clamp(s.MappingFps, 1, 15);
            MappingSource.Items.Clear();
            MappingSource.Items.Add(L10n.T("Choose the LCD fan position on the canvas."));
            foreach (var fan in profile().Fans.OrderBy(f => f.Group).ThenBy(f => f.Fan))
                MappingSource.Items.Add(new Source(fan.Group, fan.Fan, $"{profile().GroupTitle(fan.Group)} · {fan.Fan + 1}"));
            MappingSource.SelectedIndex = 0;
            for (int i = 1; i < MappingSource.Items.Count; i++)
                if (MappingSource.Items[i] is Source source && source.Group == s.MappingGroup && source.Fan == s.MappingFan) MappingSource.SelectedIndex = i;
            sensor.SelectedIndex = s.Sensor switch { CoolingSource.Gpu => 1, CoolingSource.Coolant => 2, _ => 0 };
            label.Text = s.Label; Theme.Select((int)s.Theme, animate: false);
            for (int i = 0; i < multi.Length; i++) multi[i].Checked = s.MultiSensors.Contains(MultiSources[i]);
            arcMin.Value = (decimal)Math.Clamp(s.ArcMin, -20, 140); arcMax.Value = (decimal)Math.Clamp(s.ArcMax, -15, 150);
        }
        finally { populating = false; }
        ShowMode(); RenderPreview(force: true);
    }

    void ShowMode()
    {
        var s = S;
        pictureRow.Visible = s.Mode is ScreenMode.Image or ScreenMode.Gif;
        fpsCaption.Visible = fps.Visible = s.Mode == ScreenMode.Gif;
        sensorRow.Visible = s.Mode == ScreenMode.Sensor;
        mappingRow.Visible = s.Mode == ScreenMode.Mapping;
        string path = s.Mode == ScreenMode.Gif ? s.GifPath : s.ImagePath;
        file.Text = path.Length == 0 ? L10n.T("no file selected") : Path.GetFileName(path);
        bool list = s.Theme == DashboardTheme.List;
        sensor.Visible = label.Visible = !list;
        foreach (var box in multi) box.Visible = list;
        foreach (var swatch in Swatches) swatch.Invalidate();
    }

    void ReadNumbers()
    {
        if (populating) return;
        var s = S;
        s.Brightness = (int)brightness.Value; s.Zoom = (double)zoom.Value / 100; s.MaxFps = (int)fps.Value;
        s.MappingFps = (int)mappingFps.Value;
        double min = (double)arcMin.Value, max = (double)arcMax.Value;
        if (max - min < 5) { max = Math.Min(150, min + 5); populating = true; arcMax.Value = (decimal)max; populating = false; }
        s.ArcMin = min; s.ArcMax = max;
        Changed();
    }

    void Changed() { ShowMode(); RenderPreview(force: true); owner.Note(L10n.T("Preview updates immediately. Click Show on screen to send.")); }

    void PickFile()
    {
        var s = S; bool gif = s.Mode == ScreenMode.Gif;
        using var dialog = new OpenFileDialog { Filter = gif ? L10n.T("GIF animation|*.gif") : L10n.T("Images|*.png;*.jpg;*.jpeg;*.bmp;*.gif") };
        if (dialog.ShowDialog(FindForm()) != DialogResult.OK) return;
        if (gif) s.GifPath = dialog.FileName; else s.ImagePath = dialog.FileName;
        Changed();
    }

    /// <summary>List: at least one sensor stays checked.</summary>
    void ReadMulti()
    {
        if (populating) return;
        var chosen = MultiSources.Where((_, i) => multi[i].Checked).ToList();
        if (chosen.Count == 0) { populating = true; multi[0].Checked = true; populating = false; chosen = [CoolingSource.Cpu]; }
        S.MultiSensors = chosen; Changed();
    }

    void Send()
    {
        try
        {
            S.Validate(); if (S.Mode == ScreenMode.Mapping) _ = ScreenMapping.Viewport(profile(), S); owner.Save();
            if (owner.Hub is not { } hub) { owner.Note(L10n.T("Preview: nothing is sent to screens.")); return; }
            if (hub.Found.Count == 0) hub.Rescan();
            hub.Apply([S], profile().Palette, profile());
            owner.Note(S.Mode == ScreenMode.Unmanaged ? L10n.T("App stopped sending frames; the screen retains its last image.") : L10n.F((FormattableString)$"“{Title}”: settings sent, profile saved. Check the screen visually."));
        }
        catch (Exception e) { Log.Error(e); owner.Note(e.Message); }
    }
    void Identify()
    {
        try
        {
            if (owner.Hub is not { } hub) { owner.Note(L10n.T("Preview: nothing is sent to screens.")); return; }
            if (hub.Found.Count == 0) hub.Rescan();
            hub.Identify(S, index, profile().Palette, profile());
            owner.Note(L10n.F((FormattableString)$"“{Title}” displays {index} for 8 seconds. Identify the fan and name it, e.g. G1·1 bottom."));
        }
        catch (Exception e) { Log.Error(e); owner.Note(e.Message); }
    }
    string Title => S.Name.Length > 0 ? S.Name : S.Id;

    /// <summary>The preview is drawn by the same code as the screen frame; the dashboard refreshes on a timer.</summary>
    void RenderPreview(bool force)
    {
        var s = S;
        var r = owner.Readings;
        string key = $"{System.Text.Json.JsonSerializer.Serialize(s)}|{profile().Palette}|{(s.Mode == ScreenMode.Sensor ? Dashboard.Key(s, r, owner.History.Series(s.Sensor)) : "")}";
        if (!force && key == previewKey && s.Mode != ScreenMode.Mapping) return;
        previewKey = key;
        Bitmap? image = null; string? note = null;
        try
        {
            switch (s.Mode)
            {
                case ScreenMode.Mapping: image = new ScreenMapping(profile()).Render(s, owner.SceneSeconds); break;
                case ScreenMode.Image or ScreenMode.Gif:
                    string path = s.Mode == ScreenMode.Gif ? s.GifPath : s.ImagePath;
                    if (path.Length == 0) { note = L10n.T("Choose a file"); break; }
                    using (var source = ScreenRenderer.Load(path)) image = ScreenRenderer.Picture(source, s);
                    if (s.Mode == ScreenMode.Gif) note = L10n.T("GIF · first frame");
                    break;
                case ScreenMode.Sensor: image = ScreenRenderer.Dashboard(s, r, owner.History.Series(s.Sensor), profile().Palette); break;
                case ScreenMode.Off: image = ScreenRenderer.Blank(s.Spec.Size); note = L10n.T("Off"); break;
                default: note = L10n.T("Unmanaged"); break;
            }
        }
        catch (Exception e) { note = e.Message; }
        preview.SetImage(image, note);
    }

    public void Tick()
    {
        string text = owner.Hub is { } hub ? hub.Status(Id) + (hub.Firmware(Id) is { } fw ? $" · {fw}" : "") : L10n.T("preview");
        text = (S.IsPump ? "" : Id.ToUpperInvariant() + " · ") + text;
        if (state.Text != text) state.Text = text;
        if (S.Mode is ScreenMode.Sensor or ScreenMode.Mapping) RenderPreview(force: false);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var back = Parent?.BackColor ?? Ui.Background;
        e.Graphics.Clear(back); e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var r = new RectangleF(.5f, .5f, Width - 1, Height - 1); float d = 28;
        using var path = new GraphicsPath();
        path.AddArc(r.Left, r.Top, d, d, 180, 90); path.AddArc(r.Right - d, r.Top, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); path.AddArc(r.Left, r.Bottom - d, d, d, 90, 90); path.CloseFigure();
        using var fill = new SolidBrush(Ui.Card); e.Graphics.FillPath(fill, path);
        using var border = new Pen(PillButton.Blend(back, Color.White, .12)); e.Graphics.DrawPath(border, path);
    }
}

/// <summary>Screen preview: a circle for the pump, a rounded square for an LCD fan.</summary>
public sealed class ScreenPreview : Control
{
    Bitmap? image;
    string? note;
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool Round { get; set; }
    public string? Note => note;
    public ScreenPreview()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        ForeColor = Color.FromArgb(150, 160, 185);
    }
    public void SetImage(Bitmap? value, string? text) { image?.Dispose(); image = value; note = text; Invalidate(); }
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; g.Clear(Parent?.BackColor ?? Ui.Card); g.SmoothingMode = SmoothingMode.AntiAlias; g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        var r = new RectangleF(4, 4, Width - 9, Height - 9);
        using var shape = new GraphicsPath();
        if (Round) shape.AddEllipse(r);
        else { float d = 22; shape.AddArc(r.Left, r.Top, d, d, 180, 90); shape.AddArc(r.Right - d, r.Top, d, d, 270, 90); shape.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); shape.AddArc(r.Left, r.Bottom - d, d, d, 90, 90); shape.CloseFigure(); }
        g.FillPath(Brushes.Black, shape);
        if (image != null) { g.SetClip(shape); g.DrawImage(image, r); g.ResetClip(); }
        using var border = new Pen(Color.FromArgb(70, 255, 255, 255), 2); g.DrawPath(border, shape);
        if (note != null) TextRenderer.DrawText(g, note, Font, Rectangle.Round(new RectangleF(r.Left, r.Bottom - 40, r.Width, 28)), Color.FromArgb(200, 210, 225), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
    }
    protected override void Dispose(bool disposing) { if (disposing) image?.Dispose(); base.Dispose(disposing); }
}

/// <summary>
/// Color swatch of a dashboard element: a pill (8% white, 20% on hover) with a color dot and a label.
/// An empty value means auto (gradient palette or automatic color); the dot then shows the palette colors.
/// </summary>
public sealed class ColorSwatch : Control
{
    readonly Func<string> get;
    readonly Action<string> set;
    readonly string fallback;
    readonly string? auto;
    bool hover;
    public event Action? Changed;
    public string Caption { get; }
    public string Value => get();
    public ColorSwatch(string caption, Func<string> get, Action<string> set, string fallback, string? auto)
    {
        Caption = caption; this.get = get; this.set = set; this.fallback = fallback; this.auto = auto;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        ForeColor = Ui.Text; Cursor = Cursors.Hand; Height = 32; Margin = new Padding(0, 4, 8, 4);
        Width = TextRenderer.MeasureText(caption, new Font("Segoe UI", 10)).Width + 46;
        GroupCard.Hints.SetToolTip(this, auto == null ? L10n.F((FormattableString)$"{caption}: click to choose color") : L10n.F((FormattableString)$"{caption}: click for custom color or “{auto}”"));
    }
    protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }
    /// <summary>Sets the color (empty means auto), the same as picking it in the menu.</summary>
    public void Set(string value) { set(value); Invalidate(); Changed?.Invoke(); }
    protected override void OnClick(EventArgs e)
    {
        base.OnClick(e);
        var menu = Ui.Menu();
        menu.Items.Add(L10n.T("Custom color…"), null, (_, _) =>
        {
            ColorPickerDialog.TryParseHex(Value.Length > 0 ? Value : fallback.Length > 0 ? fallback : "#38BDF8", out var initial);
            using var picker = new ColorPickerDialog(initial);
            if (picker.ShowDialog(FindForm()) == DialogResult.OK) Set(ColorPickerDialog.Hex(picker.SelectedColor));
        });
        if (auto != null) menu.Items.Add(auto == L10n.T("palette") ? L10n.T("From gradient palette") : L10n.T("Auto"), null, (_, _) => Set(""));
        else menu.Items.Add(L10n.T("Default"), null, (_, _) => Set(fallback));
        menu.Closed += (_, _) => BeginInvoke(menu.Dispose);
        menu.Show(this, new Point(0, Height));
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; var back = Parent?.BackColor ?? Ui.Card;
        g.Clear(back); g.SmoothingMode = SmoothingMode.AntiAlias;
        var r = new RectangleF(.5f, .5f, Width - 1, Height - 1);
        using (var pill = new GraphicsPath())
        {
            float d = r.Height; pill.AddArc(r.Left, r.Top, d, d, 90, 180); pill.AddArc(r.Right - d, r.Top, d, d, 270, 180); pill.CloseFigure();
            using var fill = new SolidBrush(PillButton.Blend(back, Color.White, hover ? PillButton.Hover : PillButton.Rest)); g.FillPath(fill, pill);
        }
        var dot = new RectangleF(9, 8, 16, 16);
        if (Value.Length > 0 && ColorPickerDialog.TryParseHex(Value, out var color)) { using var brush = new SolidBrush(color); g.FillEllipse(brush, dot); }
        else
        {
            // Auto: a rainbow dot (palette) or a half-white dot (automatic number color).
            using var path = new GraphicsPath(); path.AddEllipse(dot);
            using var brush = new PathGradientBrush(path) { CenterColor = Color.White, SurroundColors = auto == L10n.T("palette") ? [Color.FromArgb(255, 36, 84), Color.FromArgb(104, 61, 255), Color.FromArgb(0, 217, 223), Color.FromArgb(255, 202, 72)] : [Color.FromArgb(150, 160, 185)] };
            g.FillEllipse(brush, dot);
        }
        using (var ring = new Pen(Color.FromArgb(90, 255, 255, 255))) g.DrawEllipse(ring, dot);
        TextRenderer.DrawText(g, Caption, Font, new Rectangle(31, 0, Width - 35, Height), ForeColor, TextFormatFlags.VerticalCenter | TextFormatFlags.Left);
    }
}
