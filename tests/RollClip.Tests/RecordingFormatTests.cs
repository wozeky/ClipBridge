using RollClip.Core;

namespace RollClip.Tests;

public sealed class RecordingFormatTests
{
    [Theory]
    [InlineData(RecordingQuality.High, false, 1920, 1080, 60)]
    [InlineData(RecordingQuality.Balanced, false, 1920, 1080, 30)]
    [InlineData(RecordingQuality.Light, false, 1280, 720, 30)]
    [InlineData(RecordingQuality.High, true, 1280, 720, 30)]
    [InlineData(RecordingQuality.Balanced, true, 1280, 720, 30)]
    [InlineData(RecordingQuality.Light, true, 1280, 720, 30)]
    public void FormatRespectsProfileAndSoftwareLimit(RecordingQuality quality, bool software, int width, int height, int fps)
    {
        var format = RecordingFormat.Select(quality, software);
        Assert.Equal((width, height, fps), (format.Width, format.Height, format.Fps));
    }

    [Fact]
    public void LightProfileReducesBitrateEvenWithSoftwareEncoder()
    {
        Assert.True(RecordingFormat.Select(RecordingQuality.Light, true).Mbps
            < RecordingFormat.Select(RecordingQuality.High, true).Mbps);
    }
}
