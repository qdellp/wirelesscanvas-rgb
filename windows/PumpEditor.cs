using System.Text.Json;

namespace WirelessCanvas;

public sealed class PumpEditor : Form
{
    public PumpPlacement Value { get; }
    public PumpEditor(PumpPlacement pump, string status)
    {
        Value = JsonSerializer.Deserialize<PumpPlacement>(JsonSerializer.Serialize(pump))!;
        Text = L10n.T("Pump · HydroShift II LCD-C"); ClientSize = new(620, 355); StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog; MinimizeBox = false; MaximizeBox = false;
        BackColor = Color.FromArgb(19, 22, 30); ForeColor = Color.WhiteSmoke; Font = new("Segoe UI", 10);
        var flow = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new(18), WrapContents = true };
        Controls.Add(flow);
        var info = new Label { Text = status, AutoSize = true, MaximumSize = new(570, 0) }; flow.Controls.Add(info); flow.SetFlowBreak(info, true);
        var enabled = new CheckBox { Text = L10n.T("Ring lighting enabled"), AutoSize = true, Checked = Value.Enabled };
        enabled.CheckedChanged += (_, _) => Value.Enabled = enabled.Checked; flow.Controls.Add(enabled); flow.SetFlowBreak(enabled, true);
        void Number(string label, double value, decimal min, decimal max, Action<double> changed, bool end = false, int decimals = 0)
        {
            flow.Controls.Add(new Label { Text = label, AutoSize = true, Padding = new(0, 5, 0, 0) });
            var number = new NumericUpDown { Minimum = min, Maximum = max, Value = (decimal)value, DecimalPlaces = decimals, Increment = decimals == 0 ? 1 : .1m, Width = 75 };
            number.ValueChanged += (_, _) => changed((double)number.Value); flow.Controls.Add(number); if (end) flow.SetFlowBreak(number, true);
        }
        Number("X", Value.X, -9900, 9900, x => Value.X = x);
        Number("Y", Value.Y, -9900, 9900, x => Value.Y = x);
        Number(L10n.T("Diameter"), Value.Diameter, 20, 300, x => Value.Diameter = x, true);
        Number(L10n.T("LED rotation, °"), Value.Rotation, -360, 360, x => Value.Rotation = x, true);
        Number(L10n.T("Phase offset, s"), Value.PhaseSeconds, -60, 60, x => Value.PhaseSeconds = x, true, 1);
        var hint = new Label { Text = L10n.T("Drag the ring on the canvas to reposition it.\nPalette and brightness: Lighting layers.\nVia L-Wireless, ring and fans share the upload.\nUSB RGB upload briefly pauses the LCD.\nClick Apply gradient to send changes."), AutoSize = true }; flow.Controls.Add(hint); flow.SetFlowBreak(hint, true);
        var ok = Ui.Button(L10n.T("Done"), result: DialogResult.OK);
        var cancel = Ui.Button(L10n.T("Cancel"), result: DialogResult.Cancel);
        flow.Controls.Add(ok); flow.Controls.Add(cancel); AcceptButton = ok; CancelButton = cancel;
    }
}
