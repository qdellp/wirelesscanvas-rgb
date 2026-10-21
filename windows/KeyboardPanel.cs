using System.Drawing.Drawing2D;

namespace WirelessCanvas;

/// <summary>
/// Keyboard tab: AULA F75 matrix and lighting mode, either Follow mapping (a copy of the case canvas in time with the fans)
/// or Own lighting (own palette, effect, cycle, length, angle). Keyboard brightness applies in both modes.
/// </summary>
public sealed class KeyboardPanel : UserControl
{
    readonly Func<Profile> profile;
    readonly Func<OpenRgbBridge?> bridge;
    readonly Action<bool> ensureBridge;
    readonly CheckBox enabled = new() { Name = "keyboardEnabled", Text = L10n.T("Send gradient to keyboard (via OpenRGB)"), AutoSize = true, Margin = new Padding(0, 9, 16, 0) };
    readonly ComboBox device = new() { Width = 220, DropDownStyle = ComboBoxStyle.DropDown, Margin = new Padding(3, 6, 16, 3) };
    public readonly SegmentedTabs Mode = new() { Name = "keyboardMode", Items = [L10n.T("Follow mapping"), L10n.T("Own lighting")], AutoSize = true };
    readonly FlowLayoutPanel own = new() { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true, Margin = Padding.Empty };
    readonly Label syncNote = new() { AutoSize = true, ForeColor = Color.FromArgb(150, 160, 185), Margin = new Padding(0, 9, 16, 0), Text = L10n.T("Use the Mapping palette, effect, cycle, tilt and wavelength. The keyboard mirrors the case canvas in sync with the fans.") };
    public readonly EffectBar Effect;
    readonly GradientStrip palette = new() { Width = 310, Height = 28, Compact = true, Margin = new Padding(0, 6, 8, 4) };
    readonly PillButton editor;
    readonly NumericUpDown period = Number(1, 60, 6, 1), wavelength = Number(1, 500, 15, 1), direction = Number(-360, 360, 0), brightness = Number(0, 100, 100);
    readonly KeyboardView view;
    readonly DirectionDial dial = new() { Location = new(14, 14) };
    readonly Label status = new() { AutoSize = false, Dock = DockStyle.Fill, AutoEllipsis = true };
    bool populating;

    public KeyboardPanel(Func<Profile> profile, Func<OpenRgbBridge?> bridge, Func<double> caseSeconds, Func<double> localSeconds, Action<bool> ensureBridge, Action changed)
    {
        this.profile = profile; this.bridge = bridge; this.ensureBridge = ensureBridge;
        BackColor = Ui.Background; ForeColor = Ui.Text; Dock = DockStyle.Fill;
        direction.Name = "keyboardAngle";
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4 };
        root.RowStyles.Add(new(SizeType.AutoSize)); root.RowStyles.Add(new(SizeType.AutoSize)); root.RowStyles.Add(new(SizeType.Percent, 100)); root.RowStyles.Add(new(SizeType.Absolute, 26));
        Controls.Add(root);
        var options = Flow();
        options.Controls.Add(enabled);
        options.Controls.Add(new Label { Text = L10n.T("OpenRGB device"), AutoSize = true, Margin = new Padding(0, 9, 0, 0) });
        options.Controls.Add(device);
        root.Controls.Add(options, 0, 0);
        var settings = Flow();
        settings.Controls.Add(new Label { Text = L10n.T("Lighting"), AutoSize = true, Font = new Font("Segoe UI", 10, FontStyle.Bold), Margin = new Padding(0, 12, 8, 0) });
        settings.Controls.Add(Mode);
        settings.Controls.Add(new Label { Text = L10n.T("Brightness, %"), AutoSize = true, Margin = new Padding(8, 12, 0, 0) }); settings.Controls.Add(brightness);
        settings.SetFlowBreak(brightness, true);
        settings.Controls.Add(syncNote);
        // Own lighting: palette, cycle, length in keys, angle and its own effect.
        editor = Ui.Button(L10n.T("Gradient editor…"), EditGradient);
        own.Controls.Add(palette); own.Controls.Add(editor);
        own.SetFlowBreak(editor, true);
        foreach (var (label, number) in new[] { (L10n.T("Cycle, s"), period), (L10n.T("Wavelength, keys"), wavelength), (L10n.T("Tilt, °"), direction) })
        { own.Controls.Add(new Label { Text = label, AutoSize = true, Margin = new Padding(0, 9, 0, 0) }); own.Controls.Add(number); }
        own.SetFlowBreak(direction, true);
        Effect = new EffectBar(() => profile().Keyboard.Own(), () => { ShowSettings(); changed(); });
        own.Controls.Add(Effect);
        settings.Controls.Add(own);
        root.Controls.Add(settings, 0, 1);
        view = new KeyboardView(profile, caseSeconds, localSeconds) { Dock = DockStyle.Fill };
        view.Controls.Add(dial);
        root.Controls.Add(view, 0, 2); root.Controls.Add(status, 0, 3);

        enabled.CheckedChanged += (_, _) => { if (populating) return; profile().Keyboard.Enabled = enabled.Checked; if (enabled.Checked) ensureBridge(true); changed(); };
        Mode.SelectedChanged += index => { if (populating) return; profile().Keyboard.UseCaseGradient = index == 0; ShowSettings(); changed(); };
        device.TextChanged += (_, _) => { if (!populating && device.Text.Trim().Length > 0) { profile().Keyboard.Device = device.Text.Trim(); changed(); } };
        palette.Click += (_, _) => EditGradient();
        foreach (var number in new[] { period, wavelength, direction, brightness }) number.ValueChanged += (_, _) => Read();
        dial.Changed += angle => direction.Value = (decimal)Math.Clamp(angle, -360, 360);
        ShowSettings();
    }

    static NumericUpDown Number(decimal min, decimal max, decimal value, int decimals = 0) => new() { Minimum = min, Maximum = max, Value = value, DecimalPlaces = decimals, Width = 68, Increment = decimals > 0 ? .5m : 1, Margin = new Padding(3, 6, 12, 3) };
    static FlowLayoutPanel Flow() => new() { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true, Margin = Padding.Empty };

    void Read()
    {
        if (populating) return;
        var k = profile().Keyboard;
        k.PeriodSeconds = (double)period.Value; k.Wavelength = (double)wavelength.Value; k.Direction = (double)direction.Value; k.Brightness = (double)brightness.Value / 100;
        dial.Angle = k.Direction; view.Invalidate();
    }

    /// <summary>Shows the profile settings; in follow-mapping mode the palette and cycle come from the Mapping tab.</summary>
    public void ShowSettings()
    {
        populating = true;
        try
        {
            var p = profile(); var k = p.Keyboard;
            enabled.Checked = k.Enabled; Mode.Select(k.UseCaseGradient ? 0 : 1, animate: Mode.IsHandleCreated && Mode.Visible);
            if (device.Text != k.Device) device.Text = k.Device;
            // In follow-mapping mode the angle comes from the mapping, so the keyboard's own dial is hidden.
            own.Visible = dial.Visible = !k.UseCaseGradient; syncNote.Visible = k.UseCaseGradient;
            var source = k.Own();
            palette.Model = new PaletteModel(Effects.Active(source) ? source.Effect.Current.Palette : k.Palette);
            palette.Space = k.ColorSpace; palette.Saturation = k.SaturationBoost; palette.Invalidate();
            wavelength.Enabled = !Effects.Active(source);
            period.Value = (decimal)Math.Clamp(k.PeriodSeconds, 1, 60);
            wavelength.Value = (decimal)Math.Clamp(k.Wavelength, 1, 500); direction.Value = (decimal)Math.Clamp(k.Direction, -360, 360);
            brightness.Value = (decimal)Math.Round(Math.Clamp(k.Brightness, 0, 1) * 100); dial.Angle = k.Direction;
        }
        finally { populating = false; }
        view.Invalidate();
    }

    void EditGradient()
    {
        var k = profile().Keyboard; if (k.UseCaseGradient) return;
        // With its own effect the editor changes that effect's palette, otherwise the keyboard gradient palette.
        bool effect = Effects.Active(k.Own());
        using var dialog = new GradientEditor(new Profile { Palette = effect ? k.Effect.Current.Palette : k.Palette, ColorSpace = k.ColorSpace, SaturationBoost = k.SaturationBoost, Brightness = k.Brightness });
        if (dialog.ShowDialog(FindForm()) != DialogResult.OK) return;
        if (effect) k.Effect.Current.Palette = dialog.Palette; else k.Palette = dialog.Palette;
        k.ColorSpace = dialog.SelectedSpace; k.SaturationBoost = dialog.SelectedSaturation;
        ShowSettings();
    }

    /// <summary>Window timer: preview animation, device list and output state.</summary>
    public void Tick(bool slow)
    {
        if (!Visible) return;
        view.Invalidate();
        if (!slow) return;
        var k = profile().Keyboard; var b = bridge();
        var names = (b?.Devices ?? []).Where(d => d.Type == 5 || d.Zones.Any(z => z.Matrix != null)).Select(d => d.Name).Append(k.Device).Distinct().ToArray();
        if (!names.SequenceEqual(device.Items.Cast<string>())) { populating = true; device.Items.Clear(); device.Items.AddRange(names); device.Text = k.Device; populating = false; }
        string text = !k.Enabled ? L10n.T("Keyboard output disabled. Preview shows the gradient appearance.")
            : b == null ? L10n.T("Enable output in the OpenRGB tab; the keyboard uses the same server.")
            : b.Devices.Any(d => KeyboardLayout.Matches(k, d)) ? L10n.F((FormattableString)$"{k.Device}: sending to {KeyboardLayout.LedCount(k)} keys. ") + (k.UseCaseGradient ? L10n.T("Follows mapping, synchronized with fans.") : L10n.T("Own lighting, independent keyboard clock."))
            : L10n.F((FormattableString)$"{k.Device} not found in OpenRGB. {b.Status}");
        if (status.Text != text) status.Text = text;
    }
}

/// <summary>Keyboard preview: OpenRGB matrix keys colored by the gradient.</summary>
public sealed class KeyboardView : Panel
{
    readonly Func<Profile> profile;
    readonly Func<double> caseSeconds, localSeconds;
    public KeyboardView(Func<Profile> profile, Func<double> caseSeconds, Func<double> localSeconds)
    {
        this.profile = profile; this.caseSeconds = caseSeconds; this.localSeconds = localSeconds;
        DoubleBuffered = true; BackColor = Color.FromArgb(12, 15, 22);
    }
    /// <summary>Key rectangle in window pixels (for layout checks).</summary>
    public RectangleF KeyBounds(int cell)
    {
        var k = profile().Keyboard;
        float left = 160, top = 24, size = (float)Math.Max(4, Math.Min((Width - left - 24) / (double)k.Width, (Height - top * 2) / (double)k.Height));
        float x0 = left + (Width - left - 24 - size * k.Width) / 2, y0 = (Height - size * k.Height) / 2;
        return new(x0 + cell % k.Width * size + 3, y0 + cell / k.Width * size + 3, size - 6, size - 6);
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var p = profile(); var k = p.Keyboard;
        byte[] colors;
        try { colors = KeyboardLayout.Frame(p, KeyboardLayout.LedCount(k), caseSeconds(), localSeconds(), false); } catch { return; }
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var empty = new Pen(Color.FromArgb(40, 46, 60));
        for (int cell = 0; cell < k.Map.Length; cell++)
        {
            var key = KeyBounds(cell); uint led = k.Map[cell];
            using var path = Rounded(key, Math.Min(10, key.Width / 4));
            if (led == KeyboardSettings.NoLed || led * 3 + 2 >= colors.Length) { e.Graphics.DrawPath(empty, path); continue; }
            var color = Color.FromArgb(colors[led * 3], colors[led * 3 + 1], colors[led * 3 + 2]);
            using var fill = new SolidBrush(color); e.Graphics.FillPath(fill, path);
            using var rim = new Pen(Color.FromArgb(70, 255, 255, 255)); e.Graphics.DrawPath(rim, path);
        }
        using var text = new SolidBrush(Color.LightGray);
        e.Graphics.DrawString($"{k.Device} · {k.Width} × {k.Height}", Font, text, 14, Height - 30);
    }
    static GraphicsPath Rounded(RectangleF r, float radius)
    {
        var path = new GraphicsPath(); float d = Math.Max(1, radius * 2);
        path.AddArc(r.Left, r.Top, d, d, 180, 90); path.AddArc(r.Right - d, r.Top, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); path.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure(); return path;
    }
}
