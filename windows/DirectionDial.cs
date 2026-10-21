using System.ComponentModel;
using System.Drawing.Drawing2D;

namespace WirelessCanvas;

public sealed class DirectionDial : Control
{
    double angle;
    bool dragging;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public double Angle { get => angle; set { if (angle != value) { angle = value; Invalidate(); } } }
    public event Action<double>? Changed;
    PointF Center => new(Width / 2f, (Height - 24) / 2f);
    float Radius => Math.Max(1, Math.Min(Width - 20, Height - 36) / 2f);
    public DirectionDial()
    {
        DoubleBuffered = true; Size = new(124, 130); Cursor = Cursors.Hand;
        BackColor = Color.FromArgb(12, 15, 22); ForeColor = Color.WhiteSmoke;
    }
    void UpdateAngle(Point point)
    {
        double x = point.X - Center.X, y = point.Y - Center.Y;
        if (x*x + y*y < 16) return;
        Angle = Math.Round(Math.Atan2(y, x) * 180 / Math.PI);
        Changed?.Invoke(Angle);
    }
    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        dragging = true; Capture = true; UpdateAngle(e.Location);
    }
    protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); if (dragging) UpdateAngle(e.Location); }
    protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); dragging = false; Capture = false; }
    protected override void OnMouseCaptureChanged(EventArgs e) { base.OnMouseCaptureChanged(e); if (!Capture) dragging = false; }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e); e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var center = Center; float radius = Radius;
        using var outline = new Pen(Color.FromArgb(90, 105, 137));
        e.Graphics.DrawEllipse(outline, center.X-radius, center.Y-radius, radius*2, radius*2);
        for (int a = 0; a < 360; a += 45)
        {
            var v = Gradient.TravelDirection(a);
            e.Graphics.DrawLine(outline, center.X + (float)v.X * (radius-5), center.Y + (float)v.Y * (radius-5), center.X + (float)v.X * radius, center.Y + (float)v.Y * radius);
        }
        var direction = Gradient.TravelDirection(Angle);
        using var head = new AdjustableArrowCap(4, 5);
        using var arrow = new Pen(Color.DeepSkyBlue, 3) { CustomEndCap = head };
        e.Graphics.DrawLine(arrow, center.X - (float)direction.X*radius*.5f, center.Y - (float)direction.Y*radius*.5f,
            center.X + (float)direction.X*radius*.8f, center.Y + (float)direction.Y*radius*.8f);
        using var brush = new SolidBrush(ForeColor);
        string label = $"{Angle:0}°"; var size = e.Graphics.MeasureString(label, Font);
        e.Graphics.DrawString(label, Font, brush, (Width-size.Width)/2, Height-23);
    }
}
