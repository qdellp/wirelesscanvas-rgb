using System.Drawing.Drawing2D;
using System.Text.Json;

namespace WirelessCanvas;

/// <summary>Immutable scene snapshot shared by LCD workers; never reads the live UI profile.</summary>
internal sealed class ScreenMapping
{
    readonly Profile scene;
    readonly ColorStop[] palette;
    public ScreenMapping(Profile source)
    {
        scene = JsonSerializer.Deserialize<Profile>(JsonSerializer.Serialize(source))!;
        palette = Effects.Palette(scene);
        scene.EffectBounds = Effects.Bounds(scene);
    }

    internal static RectangleF Viewport(Profile p, ScreenSettings s)
    {
        double x, y, size;
        if (s.IsPump) { x = p.Pump.X; y = p.Pump.Y; size = p.Pump.Diameter * .65; }
        else
        {
            var fan = p.Fans.FirstOrDefault(f => f.Group == s.MappingGroup && f.Fan == s.MappingFan)
                ?? throw new InvalidDataException(L10n.T("Choose the LCD fan position on the canvas."));
            x = fan.X; y = fan.Y; size = 45 * fan.Scale;
        }
        return new RectangleF((float)(x - size / 2), (float)(y - size / 2), (float)size, (float)size);
    }

    public Bitmap Render(ScreenSettings s, double seconds)
    {
        var view = Viewport(scene, s);
        // Bounded sampling cost for three simultaneous screens, then smooth enlargement.
        using var field = new CanvasField();
        using var small = ScreenRenderer.Blank(120);
        using (var g = Graphics.FromImage(small))
        {
            g.ScaleTransform(120 / view.Width, 120 / view.Height);
            g.TranslateTransform(-view.X, -view.Y);
            field.Draw(g, view, 4 * 120 / view.Width, scene, palette, seconds, gain: 1);
        }
        var bitmap = ScreenRenderer.Blank(s.Spec.Size);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.InterpolationMode = InterpolationMode.Bilinear;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            using var attributes = new System.Drawing.Imaging.ImageAttributes();
            attributes.SetWrapMode(WrapMode.TileFlipXY);
            g.DrawImage(small, new Rectangle(0, 0, bitmap.Width, bitmap.Height), 0, 0, small.Width, small.Height, GraphicsUnit.Pixel, attributes);
        }
        return ScreenRenderer.Finish(bitmap, s);
    }
}
