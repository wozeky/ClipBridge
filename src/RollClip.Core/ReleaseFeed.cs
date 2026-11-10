using System.Text.Json;

namespace RollClip.Core;

public static class ReleaseFeed
{
    public static string? LatestNewerTag(JsonElement releases, Version installed, bool includePreviews)
    {
        installed = new Version(installed.Major, installed.Minor, Math.Max(installed.Build, 0), 0);
        Version? newest = null;
        string? selected = null;
        var selectedPreview = true;
        foreach (var release in releases.EnumerateArray())
        {
            if (release.GetProperty("draft").GetBoolean())
                continue;
            var preview = release.GetProperty("prerelease").GetBoolean();
            var tag = release.GetProperty("tag_name").GetString();
            if ((preview && !includePreviews) || !TryParseTag(tag, out var version) || version <= installed)
                continue;
            if (newest is not null && (version < newest || (version == newest && (!selectedPreview || preview))))
                continue;
            newest = version;
            selected = tag;
            selectedPreview = preview;
        }
        return selected;
    }

    private static bool TryParseTag(string? tag, out Version version)
    {
        version = new Version();
        if (string.IsNullOrEmpty(tag) || tag.Length > 64 || !tag.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-'))
            return false;
        var core = tag.TrimStart('v', 'V').Split('-', 2)[0];
        if (!Version.TryParse(core, out var parsed) || parsed.Build < 0 || parsed.Revision >= 0)
            return false;
        version = new Version(parsed.Major, parsed.Minor, parsed.Build, 0);
        return true;
    }
}
