using RollClip.Core;

namespace RollClip.Tests;

public sealed class CaptureProfileTests
{
    [Theory]
    [InlineData("vgtray")]
    [InlineData("RiotClientServices.exe")]
    [InlineData("easyanticheat_eos.EXE")]
    [InlineData("BEService_x64")]
    [InlineData("DayZ_BE")]
    public void KnownLauncherAndAntiCheatProcessesAreExcluded(string executable) =>
        Assert.True(CaptureTargets.IsExcluded(executable));

    [Theory]
    [InlineData("VALORANT-Win64-Shipping")]
    [InlineData("RiotGame")]
    [InlineData("cs2.exe")]
    public void ActualGamesRemainCaptureTargets(string executable) =>
        Assert.False(CaptureTargets.IsExcluded(executable));

    [Fact]
    public void SharingPresetsKeepDiscordAsTheDefault()
    {
        Assert.Equal(new long[] { 19_000_000, 10_000_000, 50_000_000, 100_000_000, 500_000_000 },
            ClipExport.SizePresets.Select(preset => preset.Bytes));
    }
}
