namespace WirelessCanvas;

/// <summary>
/// Effect row on the Mapping tab: effect choice, speed (repeats per cycle), parameters of the selected effect,
/// background color and description. The effect palette is the palette strip and “Gradient editor…” above the row.
/// </summary>
public sealed class EffectBar : FlowLayoutPanel
{
    readonly Func<Profile> profile;
    readonly Action changed;
    readonly ToolTip hints = new() { AutoPopDelay = 15000 };
    public readonly ComboBox Kind = new() { Name = "effectKind", DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat, Width = 132, BackColor = Color.FromArgb(22, 25, 33), ForeColor = Ui.Text, Margin = new Padding(3, 6, 10, 3) };
    public readonly NumericUpDown Speed = new() { Name = "effectSpeed", Minimum = 1, Maximum = 10, Value = 1, Width = 48, Margin = new Padding(3, 6, 10, 3) };
    readonly Label description = new() { AutoSize = true, ForeColor = Color.FromArgb(150, 160, 185), Margin = new Padding(0, 4, 0, 4), MaximumSize = new Size(1100, 0) };
    readonly List<Control> dynamic = [];
    public IReadOnlyList<NumericUpDown> Parameters => [.. dynamic.OfType<NumericUpDown>()];
    bool populating;

    public EffectBar(Func<Profile> profile, Action changed)
    {
        this.profile = profile; this.changed = changed;
        AutoSize = true; WrapContents = true; Dock = DockStyle.Fill; Margin = Padding.Empty;
        Kind.Items.AddRange([.. Effects.All.Select(e => e.Title)]);
        Kind.SelectedIndexChanged += (_, _) => { if (populating) return; profile().Effect.Kind = Effects.All[Kind.SelectedIndex].Kind; Build(); changed(); };
        Speed.ValueChanged += (_, _) => { if (populating) return; profile().Effect.Current.Speed = (int)Speed.Value; changed(); };
        hints.SetToolTip(Speed, L10n.T("Effect repetitions per cycle. Whole numbers keep playback seamless on devices."));
        Reload();
    }

    /// <summary>Shows the effect stored in the profile (after the profile is loaded).</summary>
    public void Reload() { populating = true; try { Kind.SelectedIndex = Array.FindIndex(Effects.All, e => e.Kind == profile().Effect.Kind); } finally { populating = false; } Build(); }

    /// <summary>Rebuilds the row for the selected effect: each effect has its own parameters.</summary>
    void Build()
    {
        SuspendLayout();
        foreach (var control in dynamic) { Controls.Remove(control); control.Dispose(); }
        dynamic.Clear(); Controls.Clear();
        Add(new Label { Text = L10n.T("Effect"), AutoSize = true, Font = new Font("Segoe UI", 10, FontStyle.Bold), Margin = new Padding(0, 9, 4, 0) });
        Controls.Add(Kind);
        var p = profile(); var info = Effects.Info(p.Effect.Kind);
        if (info.Kind != EffectKind.Gradient)
        {
            var e = p.Effect.Current;
            populating = true;
            try
            {
                Add(new Label { Text = L10n.T("Speed"), AutoSize = true, Margin = new Padding(0, 9, 0, 0) }); Controls.Add(Speed); Speed.Value = Math.Clamp(e.Speed, 1, 10);
                var background = new ColorSwatch(L10n.T("Background"), () => profile().Effect.Current.Background, v => profile().Effect.Current.Background = v, info.Background, null);
                background.Changed += changed;
                Add(background);
                for (int i = 0; i < info.Parameters.Length; i++)
                {
                    var parameter = info.Parameters[i]; int index = i;
                    var caption = new Label { Text = parameter.Label, AutoSize = true, Margin = new Padding(0, 9, 0, 0) };
                    var number = new NumericUpDown { Minimum = (decimal)parameter.Min, Maximum = (decimal)parameter.Max, DecimalPlaces = parameter.Decimals, Width = 58, Margin = new Padding(3, 6, 10, 3),
                        Value = (decimal)Math.Clamp(e.Values[i], parameter.Min, parameter.Max), Increment = parameter.Max - parameter.Min > 100 ? 10 : 1 };
                    number.ValueChanged += (_, _) => { if (populating) return; profile().Effect.Current.Values[index] = (double)number.Value; changed(); };
                    hints.SetToolTip(caption, parameter.Hint); hints.SetToolTip(number, parameter.Hint);
                    Add(caption); Add(number);
                }
            }
            finally { populating = false; }
        }
        description.Text = info.Description;
        hints.SetToolTip(description, info.Kind == EffectKind.Gradient ? "" : L10n.T("Use the palette bar or Gradient editor above for effect colors. Each effect has its own palette. Tilt controls wave and starfall direction."));
        SetFlowBreak(Controls[^1], true);
        Add(description, keep: true);
        ResumeLayout(true);
    }
    void Add(Control control, bool keep = false) { Controls.Add(control); if (!keep) dynamic.Add(control); }
    protected override void Dispose(bool disposing) { if (disposing) hints.Dispose(); base.Dispose(disposing); }
}
