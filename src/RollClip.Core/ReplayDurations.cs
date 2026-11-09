namespace RollClip.Core;

public static class ReplayDurations
{
    public const int Default = 60;
    public static IReadOnlyList<int> Options { get; } = Array.AsReadOnly<int>([15, 30, 60, 120, 180]);
}
