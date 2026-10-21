using System.ComponentModel;
using System.Drawing.Drawing2D;

namespace WirelessCanvas;

public sealed class GradientStrip : Control
{
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)] public PaletteModel Model { get; set; } = new(new Profile().Palette);
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)] public ColorSpace Space { get; set; }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)] public double Saturation { get; set; } = 1;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)] public bool Compact { get; set; }
    public event Action? Changed;
    public event Action? ColorRequested;
    bool dragging;
    Rectangle Bar => Compact ? new(1, 1, Math.Max(1, Width - 2), Math.Max(1, Height - 2)) : new(14, 12, Math.Max(1, Width - 28), Math.Max(1, Height - 65));
    public GradientStrip()
    {
        DoubleBuffered = true; TabStop = true; Cursor = Cursors.Hand;
        BackColor = Color.FromArgb(19, 22, 30); ForeColor = Color.WhiteSmoke;
        MouseDown += (_, e) =>
        {
            if (Compact || e.Button != MouseButtons.Left) return;
            Focus(); int hit = Hit(e.Location);
            if (hit >= 0) { Model.Select(hit); dragging = true; Capture = true; Changed?.Invoke(); Invalidate(); }
        };
        MouseMove += (_, e) =>
        {
            if (dragging && Model.MoveSelected(Math.Clamp(Math.Round((e.X - Bar.Left) / (double)Bar.Width, 3), 0, .999))) { Changed?.Invoke(); Invalidate(); }
        };
        MouseUp += (_, _) => { dragging = false; Capture = false; };
        MouseCaptureChanged += (_, _) => { if (!Capture) dragging = false; };
        MouseDoubleClick += (_, e) =>
        {
            if (Compact || e.Button != MouseButtons.Left) return;
            int hit = Hit(e.Location);
            if (hit >= 0) { Model.Select(hit); Changed?.Invoke(); ColorRequested?.Invoke(); }
            else if (Bar.Contains(e.Location) && Model.Add((e.X - Bar.Left) / (double)Bar.Width, Space)) { Changed?.Invoke(); ColorRequested?.Invoke(); }
            Invalidate();
        };
        KeyDown += (_, e) =>
        {
            if (Compact) return;
            if (e.KeyCode == Keys.Delete) { Model.RemoveSelected(); Changed?.Invoke(); Invalidate(); e.Handled = true; }
            if (e.KeyCode == Keys.Enter) { ColorRequested?.Invoke(); e.Handled = true; }
        };
    }
    int Hit(Point point)
    {
        if (point.Y < Bar.Bottom - 5 || point.Y > Bar.Bottom + 29) return -1;
        return Enumerable.Range(0, Model.Points.Count).Where(i => Math.Abs(point.X - (Bar.Left + Model.Points[i].Position * Bar.Width)) <= 11)
            .OrderBy(i => Math.Abs(point.X - (Bar.Left + Model.Points[i].Position * Bar.Width))).DefaultIfEmpty(-1).First();
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e); var bar = Bar;
        var stops = Model.Points.ToArray();
        for (int x = 0; x < bar.Width; x++)
        {
            using var fill = new SolidBrush(Gradient.Sample(stops, x / (double)Math.Max(1, bar.Width - 1), Space, Saturation));
            e.Graphics.FillRectangle(fill, bar.Left + x, bar.Top, 1, bar.Height);
        }
        using var border = new Pen(Color.FromArgb(105, 116, 140)); e.Graphics.DrawRectangle(border, bar);
        if (Compact) return;
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        for (int i = 0; i < stops.Length; i++)
        {
            float x = (float)(bar.Left + stops[i].Position * bar.Width);
            PointF[] marker = [new(x, bar.Bottom + 1), new(x - 9, bar.Bottom + 12), new(x - 9, bar.Bottom + 26), new(x + 9, bar.Bottom + 26), new(x + 9, bar.Bottom + 12)];
            using var fill = new SolidBrush(stops[i].Color); using var outline = new Pen(i == Model.Selected ? Color.White : Color.FromArgb(115, 126, 150), i == Model.Selected ? 3 : 1);
            e.Graphics.FillPolygon(fill, marker); e.Graphics.DrawPolygon(outline, marker);
        }
        using var brush = new SolidBrush(ForeColor);
        e.Graphics.DrawString("0%", Font, brush, bar.Left, bar.Bottom + 31);
        var end = e.Graphics.MeasureString(L10n.T("100% / cycle start"), Font);
        e.Graphics.DrawString(L10n.T("100% / cycle start"), Font, brush, bar.Right - end.Width, bar.Bottom + 31);
    }
}

public sealed class GradientEditor : Form
{
    readonly GradientStrip strip = new() { Dock = DockStyle.Fill };
    readonly NumericUpDown position = new() { Minimum = 0, Maximum = 99.9m, DecimalPlaces = 1, Increment = .1m, Width = 80 };
    readonly NumericUpDown saturation = new() { Minimum = 0, Maximum = 200, Value = 100, Width = 70 };
    readonly ComboBox space = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 235 };
    readonly Label selected = new() { AutoSize = true };
    readonly Button color = new() { Text = L10n.T("Choose color…"), AutoSize = true };
    readonly Button remove = new() { Text = L10n.T("Remove stop"), AutoSize = true };
    readonly Button add = new() { Text = L10n.T("+ Stop"), AutoSize = true };
    internal readonly ComboBox PresetFamily = new() { Name = "presetFamily", DropDownStyle = ComboBoxStyle.DropDownList, Width = 235 };
    readonly FlowLayoutPanel presetTiles = new() { Dock = DockStyle.Fill, WrapContents = false, Margin = Padding.Empty };
    readonly List<(GradientPreset Preset, GradientPresetTile Tile)> tiles = [];
    bool updating;
    public string Palette => strip.Model.Serialize();
    public ColorSpace SelectedSpace => strip.Space;
    public double SelectedSaturation => strip.Saturation;
    public GradientEditor(Profile profile)
    {
        Text = L10n.T("Gradient editor"); ClientSize = new Size(920, 495); MinimumSize = new Size(940, 535); StartPosition = FormStartPosition.CenterParent;
        BackColor = Color.FromArgb(19, 22, 30); ForeColor = Color.WhiteSmoke; Font = new Font("Segoe UI", 10);
        strip.Model = new PaletteModel(profile.Palette); strip.Space = profile.ColorSpace; strip.Saturation = profile.SaturationBoost;
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(16), ColumnCount = 1, RowCount = 7 };
        root.ColumnStyles.Add(new(SizeType.Percent, 100));
        foreach (int height in new[] { 28, 118, 40, 40, 130, 50, 40 }) root.RowStyles.Add(new(SizeType.Absolute, height));
        Controls.Add(root);
        root.Controls.Add(new Label { Text = L10n.T("Double-click the bar to add a stop, or a stop to choose its color. Drag to move."), AutoSize = true, MaximumSize = new Size(810, 0) }, 0, 0);
        root.Controls.Add(strip, 0, 1);
        var points = Row(); points.Controls.Add(selected); points.Controls.Add(add); points.Controls.Add(remove); points.Controls.Add(color);
        points.Controls.Add(new Label { Text = L10n.T("Position, %"), AutoSize = true, Padding = new Padding(5, 5, 0, 0) }); points.Controls.Add(position);
        var even = MakeButton(L10n.T("Distribute evenly"), () => { strip.Model.EvenlySpace(); RefreshSelection(); }); points.Controls.Add(even); root.Controls.Add(points, 0, 2);
        var settings = Row(); settings.Controls.Add(new Label { Text = L10n.T("Transitions"), AutoSize = true, Padding = new Padding(0, 5, 0, 0) }); settings.Controls.Add(space);
        space.Items.AddRange([L10n.T("Soft blends · RGB"), L10n.T("Vivid colors · HSV")]); space.SelectedIndex = profile.ColorSpace == ColorSpace.Hsv ? 1 : 0;
        settings.Controls.Add(new Label { Text = L10n.T("Saturation, %"), AutoSize = true, Padding = new Padding(8, 5, 0, 0) }); settings.Controls.Add(saturation); saturation.Value = (decimal)(profile.SaturationBoost * 100);
        settings.Controls.Add(MakeButton(L10n.T("Vivid rainbow"), () => { strip.Model = PaletteModel.Rainbow(); space.SelectedIndex = 1; saturation.Value = 100; strip.Space = ColorSpace.Hsv; strip.Saturation = 1; RefreshSelection(); })); root.Controls.Add(settings, 0, 3);
        var presets = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = Padding.Empty };
        presets.RowStyles.Add(new(SizeType.Absolute, 40)); presets.RowStyles.Add(new(SizeType.Percent, 100));
        var families = Row(); families.Controls.Add(new Label { Text = L10n.T("Presets"), AutoSize = true, Padding = new Padding(0, 5, 8, 0) }); families.Controls.Add(PresetFamily);
        PresetFamily.Items.AddRange(GradientPresets.All.Select(p => p.Family).Distinct().Select(f => (object)L10n.T(f)).ToArray());
        PresetFamily.SelectedIndexChanged += (_, _) => ShowPresets();
        presets.Controls.Add(families, 0, 0); presets.Controls.Add(presetTiles, 0, 1); root.Controls.Add(presets, 0, 4);
        PresetFamily.SelectedIndex = 0;
        root.Controls.Add(new Label { Text = L10n.F((FormattableString)$"HSV keeps transitions saturated. Main brightness is {profile.Brightness * 100:F0}%.\nClick Apply gradient on the main screen to send changes."), AutoSize = true }, 0, 5);
        var buttons = Row(); buttons.FlowDirection = FlowDirection.RightToLeft;
        var ok = MakeButton(L10n.T("Done"), () => { DialogResult = DialogResult.OK; Close(); }); var cancel = MakeButton(L10n.T("Cancel"), () => { DialogResult = DialogResult.Cancel; Close(); });
        buttons.Controls.Add(ok); buttons.Controls.Add(cancel); root.Controls.Add(buttons, 0, 6); AcceptButton = ok; CancelButton = cancel;
        add.Click += (_, _) => { if (strip.Model.Add(null, strip.Space)) { RefreshSelection(); ChooseColor(); } };
        remove.Click += (_, _) => { strip.Model.RemoveSelected(); RefreshSelection(); };
        color.Click += (_, _) => ChooseColor(); strip.ColorRequested += ChooseColor; strip.Changed += RefreshSelection;
        position.ValueChanged += (_, _) => { if (!updating) { strip.Model.MoveSelected((double)position.Value / 100); RefreshSelection(); } };
        space.SelectedIndexChanged += (_, _) => { strip.Space = space.SelectedIndex == 1 ? ColorSpace.Hsv : ColorSpace.Rgb; strip.Invalidate(); HighlightPreset(); };
        saturation.ValueChanged += (_, _) => { strip.Saturation = (double)saturation.Value / 100; strip.Invalidate(); HighlightPreset(); };
        RefreshSelection();
    }
    void ShowPresets()
    {
        foreach (Control tile in presetTiles.Controls.Cast<Control>().ToArray()) tile.Dispose();
        presetTiles.Controls.Clear(); tiles.Clear();
        string family = GradientPresets.All.Select(p => p.Family).Distinct().ElementAt(PresetFamily.SelectedIndex);
        foreach (var preset in GradientPresets.All.Where(p => p.Family == family))
        {
            var tile = new GradientPresetTile(preset); tile.Click += (_, _) => ApplyPreset(preset);
            tiles.Add((preset, tile)); presetTiles.Controls.Add(tile);
        }
        HighlightPreset();
    }
    internal void ApplyPreset(GradientPreset preset)
    {
        strip.Model = new PaletteModel(preset.Palette); strip.Space = preset.Space; strip.Saturation = 1;
        space.SelectedIndex = preset.Space == ColorSpace.Hsv ? 1 : 0; saturation.Value = 100;
        RefreshSelection();
    }
    void HighlightPreset()
    {
        foreach (var (preset, tile) in tiles)
        {
            tile.Active = Palette == new PaletteModel(preset.Palette).Serialize() && strip.Space == preset.Space && strip.Saturation == 1;
            tile.Invalidate();
        }
    }
    static FlowLayoutPanel Row() => new() { Dock = DockStyle.Fill, WrapContents = false, Margin = Padding.Empty };
    static Button MakeButton(string text, Action action) => Ui.Button(text, action);
    void RefreshSelection()
    {
        updating = true;
        try
        {
            var point = strip.Model.Points[strip.Model.Selected]; selected.Text = L10n.F((FormattableString)$"Stop {strip.Model.Selected + 1}/{strip.Model.Points.Count}");
            position.Value = Math.Clamp((decimal)(point.Position * 100), 0, 99.9m);
            color.BackColor = point.Color; color.ForeColor = point.Color.R * .299 + point.Color.G * .587 + point.Color.B * .114 > 140 ? Color.Black : Color.White;
            remove.Enabled = strip.Model.Points.Count > 2; add.Enabled = strip.Model.Points.Count < 16; strip.Invalidate();
            HighlightPreset();
        }
        finally { updating = false; }
    }
    void ChooseColor()
    {
        using var dialog = new ColorPickerDialog(strip.Model.Points[strip.Model.Selected].Color);
        if (dialog.ShowDialog(this) == DialogResult.OK) { strip.Model.SetColor(dialog.SelectedColor); RefreshSelection(); }
    }
}
