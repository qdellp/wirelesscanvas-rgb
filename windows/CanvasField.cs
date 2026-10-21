using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace WirelessCanvas;

// Small reusable raster: its phase in world coordinates matches the LED.
internal sealed class CanvasField : IDisposable
{
    Bitmap? bitmap;
    byte[] pixels = [];
    public void Draw(Graphics graphics, RectangleF world, double scale, Profile p, ColorStop[] stops, double seconds, double gain = .55)
    {
        int limit = Effects.Active(p) ? 120 : 192;
        int width = Math.Clamp((int)Math.Ceiling(world.Width * scale / 4), 16, limit);
        int height = Math.Clamp((int)Math.Ceiling(world.Height * scale / 4), 16, limit);
        if (bitmap == null || bitmap.Width != width || bitmap.Height != height)
        {
            bitmap?.Dispose(); bitmap = new(width, height, PixelFormat.Format32bppArgb); pixels = new byte[width * height * 4];
        }
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            var point = new LedPoint(world.X + (x + .5) * world.Width / width, world.Y + (y + .5) * world.Height / height);
            // The canvas ignores global brightness: it only affects devices, so the preview never dims.
            var c = Gradient.Shade(p, stops, point, seconds, p.ColorSpace, p.SaturationBoost, 1, true);
            int offset = (y * width + x) * 4;
            pixels[offset] = (byte)(c.B * gain + 22 * (1-gain)); pixels[offset + 1] = (byte)(c.G * gain + 15 * (1-gain));
            pixels[offset + 2] = (byte)(c.R * gain + 12 * (1-gain)); pixels[offset + 3] = 255;
        }
        var data = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try { Marshal.Copy(pixels, 0, data.Scan0, pixels.Length); } finally { bitmap.UnlockBits(data); }
        var state = graphics.Save(); graphics.InterpolationMode = InterpolationMode.Bilinear; graphics.PixelOffsetMode = PixelOffsetMode.Half;
        graphics.DrawImage(bitmap, world); graphics.Restore(state);
    }
    public void Dispose() { bitmap?.Dispose(); bitmap = null; }
}
