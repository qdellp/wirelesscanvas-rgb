using System.Drawing.Drawing2D;

namespace WirelessCanvas;

/// <summary>
/// Lian Li fans tab: one card per wireless group (SL, TL, SL-INF, CL, P28) (group name, named fans,
/// lighting, RPM) plus the pump card. Names appear on the Cooling tab, on the canvas and in the context menu.
/// </summary>
public sealed class FansPanel : UserControl
{
    readonly Func<Profile> profile;
    readonly Func<WirelessGroup[]> groups;
    readonly Func<string> pumpState;
    readonly Action<string> identify, reverse;
    readonly Action changed;
    readonly FlowLayoutPanel list = new() { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, Padding = new Padding(0, 0, 8, 8) };
    readonly Label status = new() { AutoSize = false, Dock = DockStyle.Fill, AutoEllipsis = true };
    readonly List<GroupCard> cards = [];
    public event Action<string?>? GroupSelected;
    public IReadOnlyList<GroupCard> Cards => cards;
    public GroupCard? PumpCard { get; private set; }

    public FansPanel(Func<Profile> profile, Func<WirelessGroup[]> groups, Func<string> pumpState, Action<string> identify, Action<string> reverse, Action changed)
    {
        this.profile = profile; this.groups = groups; this.pumpState = pumpState; this.identify = identify; this.reverse = reverse; this.changed = changed;
        BackColor = Ui.Background; ForeColor = Ui.Text; Dock = DockStyle.Fill;
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
        root.RowStyles.Add(new(SizeType.AutoSize)); root.RowStyles.Add(new(SizeType.Percent, 100)); root.RowStyles.Add(new(SizeType.Absolute, 26));
        Controls.Add(root);
        var top = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, Margin = Padding.Empty };
        top.Controls.Add(new Label { Text = L10n.T("Group names appear in Cooling and on the canvas. Set custom colors per group or per fan."), AutoSize = true, Margin = new Padding(0, 9, 16, 6), ForeColor = Color.FromArgb(150, 160, 185) });
        top.Controls.Add(Ui.Button(L10n.T("Reset all to mapping"), () =>
        {
            foreach (var fan in profile().Fans) fan.Light = LightMode.Mapping;
            profile().Pump.Light = LightMode.Mapping;
            RefreshCards(); Changed(L10n.T("All lighting follows mapping again. Click Apply gradient."));
        }));
        root.Controls.Add(top, 0, 0);
        root.Controls.Add(list, 0, 1); root.Controls.Add(status, 0, 2);
        list.Resize += (_, _) => FitCards();
        Reload();
    }

    internal void Changed(string message) { if (status.Text != message) status.Text = message; changed(); }
    void FitCards() { int width = Math.Max(600, list.ClientSize.Width - list.Padding.Horizontal - 4); foreach (Control card in list.Controls) card.Width = width; }

    /// <summary>Rebuilds the cards from the layout groups (G1…) and adds the pump card.</summary>
    public void Reload()
    {
        var p = profile();
        list.SuspendLayout();
        foreach (Control control in list.Controls.Cast<Control>().ToArray()) control.Dispose();
        list.Controls.Clear(); cards.Clear();
        foreach (var mac in p.Fans.Select(f => f.Group).Distinct().OrderBy(p.GroupNumber))
        {
            var card = new GroupCard(this, profile, mac, groups, identify, reverse);
            card.Selected += () => { SelectGroup(mac); GroupSelected?.Invoke(mac); };
            cards.Add(card); list.Controls.Add(card);
        }
        PumpCard = new GroupCard(this, profile, null, groups, identify, reverse, pumpState);
        list.Controls.Add(PumpCard);
        FitCards(); list.ResumeLayout();
    }

    /// <summary>Highlights a group card (selected on the canvas) and scrolls to it.</summary>
    public void SelectGroup(string? mac)
    {
        foreach (var card in cards) card.Highlighted = card.Mac == mac && mac != null;
        if (cards.FirstOrDefault(c => c.Highlighted) is { } selected && Visible) list.ScrollControlIntoView(selected);
    }

    public void RefreshCards() { foreach (Control card in list.Controls) if (card is GroupCard c) c.RefreshValues(); }
    public void Tick() { if (!IsDisposed && Visible) foreach (Control card in list.Controls) if (card is GroupCard c) c.RefreshLive(); }
}

/// <summary>Group card: header with name and actions, then one row per fan. mac == null means the pump.</summary>
public sealed class GroupCard : Panel
{
    internal static readonly ToolTip Hints = new();
    static readonly Color Field = Color.FromArgb(22, 25, 33), Muted = Color.FromArgb(150, 160, 185);
    readonly FansPanel owner;
    readonly Func<WirelessGroup[]> groups;
    readonly Func<string>? pumpState;
    readonly Label link = new() { AutoSize = true, ForeColor = Muted, Margin = new Padding(12, 9, 0, 0) };
    readonly List<FanRow> rows = [];
    bool highlighted, populating;
    public string? Mac { get; }
    public event Action? Selected;
    public TextBox GroupNameBox { get; }
    public IReadOnlyList<FanRow> Rows => rows;
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool Highlighted { get => highlighted; set { if (highlighted != value) { highlighted = value; Invalidate(); } } }

    public GroupCard(FansPanel owner, Func<Profile> profile, string? mac, Func<WirelessGroup[]> groups, Action<string> identify, Action<string> reverse, Func<string>? pumpState = null)
    {
        this.owner = owner; this.groups = groups; this.pumpState = pumpState; Mac = mac;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = Ui.Background; ForeColor = Ui.Text; Padding = new Padding(16, 12, 16, 12); Margin = new Padding(0, 0, 0, 12);
        var p = profile();
        var layout = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1, BackColor = Ui.Card };
        var header = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = true, BackColor = Ui.Card, Margin = new Padding(0, 0, 0, 6) };
        header.Controls.Add(new Label { Text = mac == null ? L10n.T("Pump") : $"G{p.GroupNumber(mac)}", AutoSize = true, Font = new Font("Segoe UI", 12, FontStyle.Bold), Margin = new Padding(0, 6, 10, 0) });
        GroupNameBox = Box(mac == null ? "HydroShift II LCD-C" : p.GroupNames.TryGetValue(mac, out var n) ? n : "", L10n.T("Group name, e.g. Top"), 230);
        GroupNameBox.ReadOnly = mac == null; GroupNameBox.Name = "groupName";
        GroupNameBox.TextChanged += (_, _) =>
        {
            if (populating || Mac == null) return;
            string text = GroupNameBox.Text.Trim();
            if (text.Length == 0) profile().GroupNames.Remove(Mac); else profile().GroupNames[Mac] = text[..Math.Min(48, text.Length)];
            owner.Changed(L10n.T("Group name changed. Click Save profile to keep names."));
        };
        header.Controls.Add(GroupNameBox); header.Controls.Add(link);
        layout.Controls.Add(header);
        var actions = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = true, BackColor = Ui.Card, Margin = new Padding(0, 0, 0, 8) };
        if (mac != null)
        {
            actions.Controls.Add(Ui.Button(L10n.T("Identify · 7 s"), () => identify(mac)));
            actions.Controls.Add(Ui.Button(L10n.T("Reverse order"), () => reverse(mac)));
        }
        actions.Controls.Add(Ui.Button(mac == null ? L10n.T("Custom color…") : L10n.T("Group color…"), () => PickColor(rows)));
        actions.Controls.Add(Ui.Button(L10n.T("Turn off"), () => SetLight(rows, LightMode.Off)));
        actions.Controls.Add(Ui.Button(L10n.T("Follow mapping"), () => SetLight(rows, LightMode.Mapping)));
        layout.Controls.Add(actions);
        var fansTable = new TableLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, ColumnCount = 5, BackColor = Ui.Card };
        foreach (var width in new[] { 44, 170, 166, 126 }) fansTable.ColumnStyles.Add(new(SizeType.Absolute, width));
        fansTable.ColumnStyles.Add(new(SizeType.Percent, 100));
        foreach (var (text, column) in new[] { ("№", 0), (mac == null ? L10n.T("Element") : L10n.T("Fan"), 1), (L10n.T("Lighting"), 2), (L10n.T("Color"), 3), (L10n.T("Status"), 4) })
            fansTable.Controls.Add(new Label { Text = text, AutoSize = true, ForeColor = Muted, Margin = new Padding(0, 0, 0, 4) }, column, 0);
        FanPlacement?[] fans = mac == null ? [null] : [.. p.Fans.Where(f => f.Group == mac).OrderBy(f => f.Fan)];
        for (int i = 0; i < fans.Length; i++)
        {
            var row = new FanRow(this, profile, fans[i]);
            rows.Add(row); row.AddTo(fansTable, i + 1);
        }
        layout.Controls.Add(fansTable);
        Controls.Add(layout);
        foreach (Control control in Descendants(this)) control.MouseDown += (_, _) => Selected?.Invoke();
        MouseDown += (_, _) => Selected?.Invoke();
        layout.SizeChanged += (_, _) => Height = layout.Height + Padding.Vertical;
        RefreshLive();
    }

    static IEnumerable<Control> Descendants(Control parent) => parent.Controls.Cast<Control>().SelectMany(c => new[] { c }.Concat(Descendants(c)));

    internal static TextBox Box(string text, string placeholder, int width) => new()
    {
        Text = text, PlaceholderText = placeholder, Width = width, MaxLength = 48, BackColor = Field, ForeColor = Ui.Text, BorderStyle = BorderStyle.FixedSingle, Margin = new Padding(0, 4, 8, 4),
    };

    internal void Changed(string message) => owner.Changed(message);

    internal void SetLight(IEnumerable<FanRow> targets, LightMode mode)
    {
        foreach (var row in targets.ToList()) row.Light = mode;
        RefreshValues();
        owner.Changed(mode == LightMode.Mapping ? L10n.T("Lighting follows mapping again. Click Apply gradient.") : L10n.T("Lighting changed. Click Apply gradient to send it to devices."));
    }

    internal void PickColor(IEnumerable<FanRow> targets)
    {
        var list = targets.ToList(); if (list.Count == 0) return;
        ColorPickerDialog.TryParseHex(list[0].Color, out var initial);
        using var picker = new ColorPickerDialog(initial);
        if (picker.ShowDialog(FindForm()) != DialogResult.OK) return;
        foreach (var row in list) { row.Color = ColorPickerDialog.Hex(picker.SelectedColor); row.Light = LightMode.Custom; }
        RefreshValues();
        owner.Changed(L10n.T("Custom color set. Click Apply gradient to send it to devices."));
    }

    public void RefreshValues() { populating = true; try { foreach (var row in rows) row.RefreshValues(); } finally { populating = false; } Invalidate(true); }

    public void RefreshLive()
    {
        var group = Mac == null ? null : groups().FirstOrDefault(g => g.Mac == Mac);
        string text = Mac == null ? (pumpState?.Invoke() ?? "") : group == null ? L10n.F((FormattableString)$"{Mac} · disconnected") : L10n.F((FormattableString)$"{Mac} · online · {group.Fans} fans") + (group.Model is { } model ? " · " + model.Name : "");
        if (link.Text != text) link.Text = text;
        foreach (var row in rows) row.RefreshLive(group);
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
        using var border = new Pen(highlighted ? Color.DeepSkyBlue : PillButton.Blend(back, Color.White, .12), highlighted ? 2 : 1); e.Graphics.DrawPath(border, path);
    }
}

/// <summary>Row of a fan (or the pump ring): name, lighting, custom color, RPM and PWM.</summary>
public sealed class FanRow
{
    readonly GroupCard card;
    readonly Func<Profile> profile;
    readonly Label index = new() { AutoSize = true, Font = new Font("Segoe UI", 10, FontStyle.Bold), Margin = new Padding(0, 8, 0, 0) };
    readonly ComboBox light = new() { DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat, Width = 156, BackColor = System.Drawing.Color.FromArgb(22, 25, 33), ForeColor = Ui.Text, Margin = new Padding(0, 4, 8, 4) };
    readonly ColorChip chip = new() { Width = 116, Height = 26, Margin = new Padding(0, 5, 8, 4) };
    readonly Label live = new() { AutoSize = true, ForeColor = System.Drawing.Color.FromArgb(150, 160, 185), Margin = new Padding(0, 8, 0, 0) };
    bool populating;
    public FanPlacement? Fan { get; }
    public bool IsPump => Fan == null;
    readonly Label title = new() { AutoSize = true, Margin = new Padding(0, 8, 0, 0) };
    public string Title => title.Text;
    public ComboBox LightBox => light;
    public string LiveText => live.Text;

    public FanRow(GroupCard card, Func<Profile> profile, FanPlacement? fan)
    {
        this.card = card; this.profile = profile; Fan = fan;
        index.Text = fan == null ? "◯" : (fan.Fan + 1).ToString();
        title.Text = fan == null ? L10n.T("Pump ring") : L10n.F((FormattableString)$"Fan {fan.Fan + 1}");
        light.Items.AddRange(LightLabels.All);
        light.SelectedIndexChanged += (_, _) => { if (!populating) card.SetLight([this], (LightMode)light.SelectedIndex); };
        chip.Click += (_, _) => card.PickColor([this]);
        GroupCard.Hints.SetToolTip(chip, L10n.T("Click to set a custom color for this fan only"));
        RefreshValues();
    }

    public LightMode Light { get => Fan?.Light ?? profile().Pump.Light; set { if (Fan != null) Fan.Light = value; else profile().Pump.Light = value; } }
    public string Color { get => Fan?.CustomColor ?? profile().Pump.CustomColor; set { if (Fan != null) Fan.CustomColor = value; else profile().Pump.CustomColor = value; } }

    public void AddTo(TableLayoutPanel table, int row)
    {
        table.Controls.Add(index, 0, row); table.Controls.Add(title, 1, row); table.Controls.Add(light, 2, row); table.Controls.Add(chip, 3, row); table.Controls.Add(live, 4, row);
    }

    public void RefreshValues()
    {
        populating = true;
        try
        {
            light.SelectedIndex = (int)Light;
            chip.Value = Light == LightMode.Custom && ColorPickerDialog.TryParseHex(Color, out var c) ? c : null;
            chip.Text = Light == LightMode.Off ? L10n.T("off") : L10n.T("set color");
        }
        finally { populating = false; }
        chip.Invalidate();
    }

    public void RefreshLive(WirelessGroup? group)
    {
        string text = Fan == null ? "" : group == null ? "—" : L10n.F((FormattableString)$"{group.Rpm[Math.Min(3, Fan.Fan)]} RPM · PWM {Math.Round(group.Pwm[Math.Min(3, Fan.Fan)] * 100 / 255.0):F0}%");
        if (live.Text != text) live.Text = text;
    }
}

/// <summary>Custom color swatch; without a custom color it shows the mode as text. Click to pick a color.</summary>
public sealed class ColorChip : Control
{
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Color? Value { get; set; }
    public ColorChip()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Cursor = Cursors.Hand; ForeColor = Color.FromArgb(150, 160, 185);
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        var back = Parent?.BackColor ?? Ui.Background;
        e.Graphics.Clear(back); e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var r = new RectangleF(.5f, .5f, Width - 1, Height - 1); float d = Math.Min(r.Height, 100);
        using var path = new GraphicsPath();
        path.AddArc(r.Left, r.Top, d, d, 90, 180); path.AddArc(r.Right - d, r.Top, d, d, 270, 180); path.CloseFigure();
        using var fill = new SolidBrush(Value ?? PillButton.Blend(back, Color.White, PillButton.Rest)); e.Graphics.FillPath(fill, path);
        string text = Value is Color c ? ColorPickerDialog.Hex(c) : Text;
        var fore = Value is Color v ? (v.GetBrightness() > .55 ? Color.Black : Color.White) : ForeColor;
        TextRenderer.DrawText(e.Graphics, text, Font, ClientRectangle, fore, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
    }
    protected override void OnTextChanged(EventArgs e) { Invalidate(); base.OnTextChanged(e); }
}
