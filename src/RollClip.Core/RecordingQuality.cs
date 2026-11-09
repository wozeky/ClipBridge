namespace RollClip.Core;

public enum RecordingQuality
{
    High,
    Balanced,
    Light
}

public readonly record struct RecordingFormat(int Width, int Height, int Fps, int Mbps, int MaxMbps)
{
    public static RecordingFormat Select(RecordingQuality quality, bool software) => (quality, software) switch
    {
        (RecordingQuality.Light, _) => new(1280, 720, 30, 4, 6),
        (_, true) => new(1280, 720, 30, 6, 8),
        (RecordingQuality.Balanced, false) => new(1920, 1080, 30, 8, 10),
        _ => new(1920, 1080, 60, 12, 14)
    };
}
