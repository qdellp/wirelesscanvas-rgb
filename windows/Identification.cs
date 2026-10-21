namespace WirelessCanvas;

public static class Identification
{
    public const int DurationMilliseconds = 7000;
    static readonly byte[][] Colors = [[100, 0, 0], [0, 90, 0], [0, 20, 110], [90, 65, 0]];
    public static byte[] Frame(int fans, int ledsPerFan = 40)
    {
        if (fans is < 1 or > 4) throw new ArgumentOutOfRangeException(nameof(fans));
        if (FanModel.All.All(m => m.LedsPerFan != ledsPerFan)) throw new ArgumentOutOfRangeException(nameof(ledsPerFan));
        byte[] frame = new byte[fans * ledsPerFan * 3];
        for (int fan = 0; fan < fans; fan++)
            for (int led = 0; led < ledsPerFan; led++) Colors[fan].CopyTo(frame, (fan * ledsPerFan + led) * 3);
        return frame;
    }
}

internal sealed record IdentificationSession(string Mac, string Master, RgbUpload Previous, RgbUpload Marker, long RestoreAt);
