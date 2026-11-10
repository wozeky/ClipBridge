using System.Text.Json;
using RollClip.Core;

namespace RollClip.Tests;

public sealed class ReleaseFeedTests
{
    [Theory]
    [InlineData(false, "v0.3.2")]
    [InlineData(true, "v0.4.0-rc.1")]
    public void SelectsNewerReleaseAndRespectsChannel(bool previews, string expected)
    {
        using var releases = JsonDocument.Parse("""
        [
          {"tag_name":"v8.0.0","draft":true,"prerelease":false},
          {"tag_name":"v0.3.2","draft":false,"prerelease":false},
          {"tag_name":"v0.4.0-rc.1","draft":false,"prerelease":true},
          {"tag_name":"v0.3.1","draft":false,"prerelease":false}
        ]
        """);
        Assert.Equal(expected, ReleaseFeed.LatestNewerTag(releases.RootElement, new Version(0, 3, 1), previews));
        Assert.Null(ReleaseFeed.LatestNewerTag(releases.RootElement, new Version(0, 4, 0), previews));
    }
}
