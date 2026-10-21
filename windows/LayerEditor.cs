using System.Text.Json;

namespace WirelessCanvas;

public sealed class LayerEditor : Form
{
    public LightingLayer Inner { get; }
    public LightingLayer Outer { get; }
    public LightingLayer Pump { get; }
    public LayerEditor(Profile profile)
    {
        Inner = JsonSerializer.Deserialize<LightingLayer>(JsonSerializer.Serialize(profile.InnerLayer))!;
        Outer = JsonSerializer.Deserialize<LightingLayer>(JsonSerializer.Serialize(profile.OuterLayer))!;
        Pump = JsonSerializer.Deserialize<LightingLayer>(JsonSerializer.Serialize(profile.Pump.Layer))!;
        Text = L10n.T("Lighting layers · shared case canvas"); ClientSize = new(780, 521);
        FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false; StartPosition = FormStartPosition.CenterParent;
        BackColor = Color.FromArgb(19, 22, 30); ForeColor = Color.WhiteSmoke; Font = new("Segoe UI", 10);
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new(18), ColumnCount = 1, RowCount = 5 };
        root.ColumnStyles.Add(new(SizeType.Percent, 100));
        root.RowStyles.Add(new(SizeType.Absolute, 52)); root.RowStyles.Add(new(SizeType.Absolute, 126)); root.RowStyles.Add(new(SizeType.Absolute, 126)); root.RowStyles.Add(new(SizeType.Absolute, 126)); root.RowStyles.Add(new(SizeType.Percent, 100));
        Controls.Add(root);
        root.Controls.Add(new Label { Text = L10n.T("All layers share case coordinates and motion.\nLayer brightness multiplies master brightness. Click Apply to send."), AutoSize = true }, 0, 0);
        root.Controls.Add(Card(L10n.T("Inner lighting · 24 LEDs / fan"), Inner, profile), 0, 1);
        root.Controls.Add(Card(L10n.T("Outer strip · 16 LEDs / fan"), Outer, profile), 0, 2);
        root.Controls.Add(Card(L10n.T("Pump ring · HydroShift II LCD-C · 24 LEDs"), Pump, profile), 0, 3);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
        var done = MakeButton(L10n.T("Done")); done.DialogResult = DialogResult.OK;
        var cancel = MakeButton(L10n.T("Cancel")); cancel.DialogResult = DialogResult.Cancel;
        buttons.Controls.Add(done); buttons.Controls.Add(cancel); root.Controls.Add(buttons, 0, 4); AcceptButton = done; CancelButton = cancel;
    }
    static Button MakeButton(string text) => Ui.Button(text);
    Control Card(string title, LightingLayer layer, Profile common)
    {
        var panel = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new(8), BackColor = Color.FromArgb(28, 33, 44) };
        var heading = new Label { Text = title, AutoSize = true, Font = new(Font, FontStyle.Bold) }; panel.Controls.Add(heading); panel.SetFlowBreak(heading, true);
        var enabled = new CheckBox { Text = L10n.T("Enabled"), Checked = layer.Enabled, AutoSize = true };
        var linked = new CheckBox { Text = L10n.T("Use canvas colors"), Checked = layer.UseCanvasPalette, AutoSize = true };
        var level = new NumericUpDown { Minimum = 0, Maximum = 100, Value = (decimal)(layer.Brightness * 100), Width = 65 };
        panel.Controls.Add(enabled); panel.Controls.Add(linked); panel.Controls.Add(new Label { Text = L10n.T("Brightness, %"), AutoSize = true, Padding = new(4, 4, 0, 0) }); panel.Controls.Add(level); panel.SetFlowBreak(level, true);
        var strip = new GradientStrip { Width = 390, Height = 28, Compact = true };
        var edit = MakeButton(L10n.T("Own gradient…"));
        void RefreshStrip()
        {
            strip.Model = new(layer.UseCanvasPalette ? common.Palette : layer.Palette);
            strip.Space = layer.UseCanvasPalette ? common.ColorSpace : layer.ColorSpace;
            strip.Saturation = layer.UseCanvasPalette ? common.SaturationBoost : layer.SaturationBoost; strip.Invalidate();
        }
        void Edit()
        {
            var p = new Profile { Palette = layer.UseCanvasPalette ? common.Palette : layer.Palette,
                ColorSpace = layer.UseCanvasPalette ? common.ColorSpace : layer.ColorSpace,
                SaturationBoost = layer.UseCanvasPalette ? common.SaturationBoost : layer.SaturationBoost };
            using var dialog = new GradientEditor(p);
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            layer.Palette = dialog.Palette; layer.ColorSpace = dialog.SelectedSpace; layer.SaturationBoost = dialog.SelectedSaturation;
            linked.Checked = false; layer.UseCanvasPalette = false; RefreshStrip();
        }
        edit.Click += (_, _) => Edit(); strip.Click += (_, _) => Edit();
        enabled.CheckedChanged += (_, _) => layer.Enabled = enabled.Checked;
        linked.CheckedChanged += (_, _) => { layer.UseCanvasPalette = linked.Checked; RefreshStrip(); };
        level.ValueChanged += (_, _) => layer.Brightness = (double)level.Value / 100;
        panel.Controls.Add(strip); panel.Controls.Add(edit); RefreshStrip(); return panel;
    }
}
