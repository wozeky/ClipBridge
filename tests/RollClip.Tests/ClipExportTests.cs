using RollClip.Core;

namespace RollClip.Tests;

public sealed class ClipExportTests
{
    [Fact]
    public void VideoBudgetReservesSpaceForAudioAndMp4()
    {
        const int seconds = 60;
        var rate = ClipExport.VideoBudget(seconds, ClipExport.DiscordBytes);

        Assert.InRange(rate, 2_000_000, 2_400_000);
        Assert.True((rate + 96_000L) * seconds / 8 < ClipExport.DiscordBytes);
    }

    [Fact]
    public void VeryShortClipsDoNotRequestAnExcessiveBitrate()
    {
        Assert.Equal(8_000_000, ClipExport.VideoBudget(1, ClipExport.DiscordBytes));
        Assert.Throws<ArgumentOutOfRangeException>(() => ClipExport.VideoBudget(double.NaN, ClipExport.DiscordBytes));
    }

    [Fact]
    public void ExistingLinksWithoutAnExpiryRemainUsable()
    {
        var now = DateTimeOffset.UtcNow;
        var clip = new Clip(Guid.NewGuid(), "clip.mp4", "Klip", now, 200, null);
        Assert.False(clip.LinkExpired(now));
        Assert.True((clip with { LinkExpiresAt = now }).LinkExpired(now));
    }
}
