using ClipBridge.Core;

namespace ClipBridge.Tests;

public sealed class ReplayDurationsTests
{
    [Fact]
    public void DefaultsToSixtySeconds()
    {
        Assert.Equal(60, ReplayDurations.Default);
        Assert.Contains(ReplayDurations.Default, ReplayDurations.Options);
    }
}
