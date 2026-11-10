namespace RollClip.Core;

public enum CaptureMethod { Auto, Wgc, Dxgi, Gdi }

public sealed record CaptureProfile(RecordingQuality Quality, int Seconds, bool DesktopAudio, bool Microphone,
    bool GameOnlyAudio, CaptureMethod Method, string? Label, bool AutoStart = false, CaptureAudio? Audio = null);

public sealed record CaptureAudio(string? MicrophoneId = null, int SystemPercent = 100, int MicrophonePercent = 100);

public static class CaptureTargets
{
    private static readonly HashSet<string> Excluded = new(StringComparer.OrdinalIgnoreCase)
    {
        "vgc", "vgtray", "vgk", "RiotClientServices", "RiotClientUx", "RiotClientUxRender",
        "EasyAntiCheat", "EasyAntiCheat_EOS", "EasyAntiCheat_launcher", "start_protected_game",
        "BEService", "BEService_x64", "BELauncher", "EAAntiCheat.GameService",
        "EAAntiCheat.GameServiceLauncher", "FACEITService", "FACEITClient",
        "steam", "steamwebhelper", "EpicGamesLauncher", "EpicWebHelper", "Battle.net"
    };

    public static bool IsExcluded(string? processName)
    {
        if (string.IsNullOrWhiteSpace(processName))
            return false;
        var name = processName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? processName[..^4] : processName;
        return Excluded.Contains(name) || name.EndsWith("_BE", StringComparison.OrdinalIgnoreCase);
    }
}
