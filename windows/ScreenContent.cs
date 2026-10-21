using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Text.Json.Serialization;

namespace WirelessCanvas;

/// <summary>
/// What a screen shows. Unmanaged: the app sends nothing to it; Off: brightness 0 and a black frame.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ScreenMode { Unmanaged, Image, Gif, Sensor, Off, Mapping }

/// <summary>How an image fits the screen: fill and crop the edges, or fit it whole.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ScreenFit { Cover, Contain }

/// <summary>Settings of one screen. Id: “pump” for the pump, the USB serial number for an LCD fan.</summary>
public sealed class ScreenSettings
{
    public const string PumpId = "pump";
    public static readonly string[] ModeLabels = [L10n.T("Unmanaged"), L10n.T("Image"), "GIF", L10n.T("Sensor"), L10n.T("Off"), L10n.T("Follow mapping")];
    public string MappingGroup { get; set; } = "";
    public int MappingFan { get; set; }
    public int MappingFps { get; set; } = 15;
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public ScreenMode Mode { get; set; }
    public string ImagePath { get; set; } = "";
    public string GifPath { get; set; } = "";
    public ScreenFit Fit { get; set; }
    /// <summary>Zoom on top of fill, 1–4 (larger means more cropping).</summary>
    public double Zoom { get; set; } = 1;
    /// <summary>Image rotation to match how the screen sits in the case: 0, 90, 180, 270.</summary>
    public int Rotation { get; set; }
    public int Brightness { get; set; } = 80;
    /// <summary>GIF frame rate limit; the dashboard refreshes once per second.</summary>
    public int MaxFps { get; set; } = 20;
    public CoolingSource Sensor { get; set; } = CoolingSource.Cpu;
    /// <summary>Caption above the number; empty means CPU / GPU / Coolant.</summary>
    public string Label { get; set; } = "";
    /// <summary>Dashboard theme: arc, ring, segments, minimal, sensor list, 5-minute chart.</summary>
    public DashboardTheme Theme { get; set; }
    /// <summary>Sensors of the List theme, one to three.</summary>
    public List<CoolingSource> MultiSensors { get; set; } = [CoolingSource.Cpu, CoolingSource.Gpu, CoolingSource.Coolant];
    /// <summary>Accent (arc, ring, ticks, bars, chart) #RRGGBB; empty uses the gradient palette by the scale value.</summary>
    public string Color { get; set; } = "";
    public string BackgroundColor { get; set; } = "#000000";
    /// <summary>Unfilled part of the scale.</summary>
    public string TrackColor { get; set; } = "#262B36";
    /// <summary>Number color; empty means auto (near white).</summary>
    public string ValueColor { get; set; } = "";
    public string LabelColor { get; set; } = "#9AA3B5";
    public double ArcMin { get; set; } = 20;
    public double ArcMax { get; set; } = 90;
    [JsonIgnore] public bool IsPump => Id == PumpId;
    [JsonIgnore] public ScreenSpec Spec => IsPump ? ScreenSpec.Pump : ScreenSpec.Fan;
    public string SensorLabel => Label.Length > 0 ? Label : Sensor switch { CoolingSource.Gpu => "GPU", CoolingSource.Coolant => L10n.T("Coolant"), _ => "CPU" };
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Id) || Id.Length > 64 || Name.Length > 48 || Name.Any(char.IsControl) || !Enum.IsDefined(Mode) || !Enum.IsDefined(Fit) ||
            ImagePath.Length > 1024 || GifPath.Length > 1024 || !double.IsFinite(Zoom) || Zoom is < 1 or > 4 || Rotation is not (0 or 90 or 180 or 270) ||
            Brightness is < 0 or > 100 || MaxFps is < 1 or > 30 || MappingFps is < 1 or > 15 || MappingGroup.Length > 64 || MappingFan is < 0 or > 3 || Sensor is not (CoolingSource.Cpu or CoolingSource.Gpu or CoolingSource.Coolant) ||
            Label.Length > 24 || Label.Any(char.IsControl) || (Color.Length > 0 && !ColorPickerDialog.TryParseHex(Color, out _)) ||
            !Enum.IsDefined(Theme) || MultiSensors == null || MultiSensors.Count is < 1 or > 3 || MultiSensors.Distinct().Count() != MultiSensors.Count ||
            MultiSensors.Any(m => m is not (CoolingSource.Cpu or CoolingSource.Gpu or CoolingSource.Coolant)) ||
            new[] { BackgroundColor, TrackColor, LabelColor }.Any(c => !ColorPickerDialog.TryParseHex(c, out _)) || (ValueColor.Length > 0 && !ColorPickerDialog.TryParseHex(ValueColor, out _)) ||
            !double.IsFinite(ArcMin) || !double.IsFinite(ArcMax) || ArcMin < -20 || ArcMax > 150 || ArcMax - ArcMin < 5)
            throw new InvalidDataException(L10n.T("Invalid screen settings: ") + (Name.Length > 0 ? Name : Id) + ".");
    }
}

/// <summary>Screen frames: drawn with System.Drawing, JPEG within the device limit.</summary>
public static class ScreenRenderer
{
    public static Bitmap Blank(int size) { var b = new Bitmap(size, size, PixelFormat.Format24bppRgb); using var g = Graphics.FromImage(b); g.Clear(System.Drawing.Color.Black); return b; }

    /// <summary>Image (or GIF frame) for the screen: fill/fit, zoom, rotation, round pump mask.</summary>
    public static Bitmap Picture(Image source, ScreenSettings s)
    {
        int size = s.Spec.Size;
        var bitmap = Blank(size);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic; g.PixelOffsetMode = PixelOffsetMode.HighQuality; g.SmoothingMode = SmoothingMode.AntiAlias;
            double fit = s.Fit == ScreenFit.Cover ? Math.Max((double)size / source.Width, (double)size / source.Height) : Math.Min((double)size / source.Width, (double)size / source.Height);
            double scale = fit * s.Zoom, w = source.Width * scale, h = source.Height * scale;
            using var attributes = new ImageAttributes(); attributes.SetWrapMode(WrapMode.TileFlipXY); // without a light fringe at the edges
            g.DrawImage(source, new Rectangle((int)Math.Round((size - w) / 2), (int)Math.Round((size - h) / 2), (int)Math.Round(w), (int)Math.Round(h)), 0, 0, source.Width, source.Height, GraphicsUnit.Pixel, attributes);
        }
        return Finish(bitmap, s);
    }

    /// <summary>Dashboard of the selected theme (<see cref="WirelessCanvas.Dashboard"/>) with the screen rotation and mask.</summary>
    public static Bitmap Dashboard(ScreenSettings s, SensorReadings readings, IReadOnlyList<double?> history, string palette) =>
        Finish(WirelessCanvas.Dashboard.Render(s, readings, history, palette), s);

    /// <summary>Large digit for identification: which serial number is which physical fan.</summary>
    public static Bitmap Number(ScreenSettings s, int number)
    {
        int size = s.Spec.Size;
        var bitmap = Blank(size);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias; g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            using var ring = new Pen(System.Drawing.Color.DeepSkyBlue, size * .04f);
            g.DrawEllipse(ring, size * .08f, size * .08f, size * .84f, size * .84f);
            using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            using var font = new Font("Segoe UI", size * .5f, FontStyle.Bold, GraphicsUnit.Pixel);
            using var small = new Font("Segoe UI", size * .055f, GraphicsUnit.Pixel);
            g.DrawString(number.ToString(), font, Brushes.White, new RectangleF(0, -size * .04f, size, size), format);
            using var dim = new SolidBrush(System.Drawing.Color.FromArgb(170, 255, 255, 255));
            g.DrawString(s.Id.ToUpperInvariant(), small, dim, new RectangleF(0, size * .72f, size, size * .1f), format);
        }
        return Finish(bitmap, s);
    }

    static ColorStop[] TryPalette(string palette)
    {
        try { return Gradient.ParsePalette(palette); } catch (Exception e) when (e is InvalidDataException or FormatException) { return Gradient.ParsePalette("#FF2454, #683DFF, #00D9DF, #FFCA48"); }
    }

    /// <summary>Rotation to match how the screen is mounted, and black outside the pump circle (it is invisible and compresses better).</summary>
    internal static Bitmap Finish(Bitmap bitmap, ScreenSettings s)
    {
        bitmap.RotateFlip(s.Rotation switch { 90 => RotateFlipType.Rotate90FlipNone, 180 => RotateFlipType.Rotate180FlipNone, 270 => RotateFlipType.Rotate270FlipNone, _ => RotateFlipType.RotateNoneFlipNone });
        if (s.Spec.Round)
        {
            using var g = Graphics.FromImage(bitmap); g.SmoothingMode = SmoothingMode.AntiAlias;
            using var outside = new GraphicsPath { FillMode = FillMode.Alternate };
            outside.AddRectangle(new Rectangle(-1, -1, bitmap.Width + 2, bitmap.Height + 2)); outside.AddEllipse(0, 0, bitmap.Width - 1, bitmap.Height - 1);
            g.FillPath(Brushes.Black, outside);
        }
        return bitmap;
    }

    static readonly ImageCodecInfo Jpeg = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
    /// <summary>JPEG at the screen quality; if it does not fit the frame limit, quality is lowered in steps.</summary>
    public static byte[] Encode(Bitmap bitmap, ScreenSpec spec)
    {
        if (bitmap.Width != spec.Size || bitmap.Height != spec.Size) throw new InvalidDataException(L10n.T("Frame size does not match the screen."));
        for (long quality = spec.Quality; ; quality -= 10)
        {
            using var stream = new MemoryStream();
            using var parameters = new EncoderParameters(1); parameters.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, quality);
            bitmap.Save(stream, Jpeg, parameters);
            if (stream.Length <= spec.MaxJpeg) return stream.ToArray();
            if (quality <= 35) throw new InvalidDataException(L10n.F((FormattableString)$"Frame exceeds {spec.MaxJpeg / 1024} KB even at JPEG quality {quality}."));
        }
    }

    /// <summary>Image from disk without locking the file (an in-memory copy).</summary>
    public static Bitmap Load(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException(L10n.T("Image file not found: ") + path);
        if (new FileInfo(path).Length > 64 * 1024 * 1024) throw new InvalidDataException(L10n.T("File exceeds 64 MB."));
        using var stream = new MemoryStream(File.ReadAllBytes(path));
        using var image = Image.FromStream(stream);
        return new Bitmap(image);
    }

    /// <summary>
    /// GIF frames ready for the screen: JPEG and duration. Frames that come too often are merged to fit the limit FPS,
    /// long animations are capped at 600 frames and 60 MB to save memory.
    /// </summary>
    public static List<(byte[] Jpeg, int Milliseconds)> Animation(string path, ScreenSettings s, CancellationToken cancel = default)
    {
        if (!File.Exists(path)) throw new FileNotFoundException(L10n.T("GIF file not found: ") + path);
        if (new FileInfo(path).Length > 64 * 1024 * 1024) throw new InvalidDataException(L10n.T("File exceeds 64 MB."));
        using var stream = new MemoryStream(File.ReadAllBytes(path));
        using var image = Image.FromStream(stream);
        int count = image.FrameDimensionsList.Contains(FrameDimension.Time.Guid) ? image.GetFrameCount(FrameDimension.Time) : 1;
        int[] delays = new int[count];
        try
        {
            byte[] raw = image.GetPropertyItem(0x5100)!.Value!; // GIF frame delays in hundredths of a second
            for (int i = 0; i < count && i * 4 + 3 < raw.Length; i++) delays[i] = BitConverter.ToInt32(raw, i * 4) * 10;
        }
        catch (ArgumentException) { }
        int minimum = (int)Math.Ceiling(1000.0 / s.MaxFps);
        var frames = new List<(byte[], int)>();
        long bytes = 0; int pending = 0;
        for (int i = 0; i < count && frames.Count < 600; i++)
        {
            cancel.ThrowIfCancellationRequested();
            int delay = delays[i] <= 10 ? 100 : delays[i]; // 0 and 1 are shown by browsers as 100 ms
            // A frame shorter than the FPS limit is not sent separately: its time is added to the previous one.
            if (frames.Count > 0 && pending < minimum) { var last = frames[^1]; frames[^1] = (last.Item1, last.Item2 + delay); pending += delay; continue; }
            image.SelectActiveFrame(FrameDimension.Time, i);
            using var frame = Picture(image, s);
            byte[] jpeg = Encode(frame, s.Spec); bytes += jpeg.Length;
            if (bytes > 60 * 1024 * 1024) break;
            frames.Add((jpeg, delay)); pending = delay;
        }
        if (frames.Count == 0) throw new InvalidDataException(L10n.T("GIF contains no frames."));
        return frames.Select(f => (f.Item1, Math.Max(minimum, f.Item2))).ToList();
    }
}
