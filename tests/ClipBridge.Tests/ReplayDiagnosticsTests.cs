using System.Diagnostics;
using ClipBridge.Core;

namespace ClipBridge.Tests;

public class ReplayDiagnosticsTests
{
    private static readonly Version Version = new(1, 2, 3);

    [Fact]
    public void ReportsSeparateTimingsAndTracksOnlyTheLatestSavedClip()
    {
        var report = new ReplayDiagnostics();
        var clip = MakeClip();
        var startedAt = Stopwatch.GetTimestamp();
        var readyAt = startedAt + Stopwatch.Frequency;
        report.BeginCapture();
        report.CaptureStarted("Windows Graphics Capture", "NVIDIA NVENC", "");
        report.BeginSave(viaHotkey: true);
        report.Mp4Ready(clip.Id, startedAt, readyAt);
        report.ClipChanged(clip, readyAt);
        report.ClipChanged(clip with { Id = Guid.NewGuid(), State = UploadState.Uploaded }, readyAt + Stopwatch.Frequency);

        Assert.Contains("Son kaydedilen klibin paylaşımı: Sırada", report.BuildReport(Version, true));
        Assert.Contains("MP4 hazır → paylaşım bağlantısı hazır: Ölçülmedi", report.BuildReport(Version, true));

        report.ClipChanged(clip with { State = UploadState.Uploaded, ShareUrl = "https://example.test/private" },
            readyAt + 4 * Stopwatch.Frequency);
        var result = report.BuildReport(Version, true);
        Assert.Contains("ClipBridge 1.2.3", result);
        Assert.Contains("Tetikleyici: F8", result);
        Assert.Contains("Tetikleme → MP4 hazır: 1.00 sn", result);
        Assert.Contains("Son kaydedilen klibin süresi: 59.00 sn", result);
        Assert.Contains("MP4 hazır → paylaşım bağlantısı hazır: 4.00 sn", result);
        Assert.DoesNotContain("private", result);

        report.BeginSave(viaHotkey: false);
        report.SaveFailed(new OperationCanceledException(), mp4Ready: false);
        result = report.BuildReport(Version, true);
        Assert.Contains("Son klip alma denemesi: Başarısız", result);
        Assert.Contains("Tetikleme → MP4 hazır: Ölçülmedi", result);
        Assert.Contains("Son kaydedilen klibin süresi: 59.00 sn", result);
    }

    [Fact]
    public void NeverExportsRawCaptureOrUploadErrors()
    {
        const string privateData = "C:/Users/PrivateName/secret.mp4 game-title https://example.test/access-token";
        var report = new ReplayDiagnostics();
        report.BeginCapture();
        report.CaptureStarted("Windows GDI", "Media Foundation", "Failed to create D3D11VA device: " + privateData);
        report.CaptureFailed(new CaptureException(privateData));
        var clip = MakeClip();
        report.BeginSave(viaHotkey: false);
        report.Mp4Ready(clip.Id, 0, Stopwatch.Frequency);
        report.ClipChanged(clip with
        {
            FilePath = privateData, Name = privateData, Game = privateData, ShareUrl = privateData,
            Error = "Gofile HTTP 429 " + privateData, State = UploadState.Failed
        }, 2 * Stopwatch.Frequency);

        var result = report.BuildReport(Version, false);
        Assert.Contains("Kayıt durumu: Başarısız", result);
        Assert.Contains("Yakalama yöntemi: Windows GDI", result);
        Assert.Contains("Yedek yöntem nedeni: Direct3D 11 yakalama aygıtı açılamadı.", result);
        Assert.Contains("Kayıt hatası: Yakalama motoru hata bildirdi.", result);
        Assert.Contains("Yükleme hatası: Yükleme servisi HTTP 429 döndürdü.", result);
        Assert.DoesNotContain("PrivateName", result);
        Assert.DoesNotContain("game-title", result);
        Assert.DoesNotContain("https://", result);
        Assert.DoesNotContain("access-token", result);
    }

    [Fact]
    public void ReportsStartupFailureBeforeAnyClipExists()
    {
        var report = new ReplayDiagnostics();
        report.BeginCapture();
        report.CaptureFailed(new CaptureException("Açık ve küçültülmemiş bir pencere seçin."));
        var result = report.BuildReport(Version, false);
        Assert.Contains("Kayıt hatası: Kayıt için açık bir pencere seçilmedi.", result);
        Assert.Contains("F8 kısayolu: Etkin değil", result);
        Assert.Contains("Son klip alma denemesi: Henüz denenmedi", result);
        Assert.Contains("Son kaydedilen klibin süresi: Ölçülmedi", result);
    }

    private static Clip MakeClip() => new(Guid.NewGuid(), "private.mp4", "private", DateTimeOffset.UtcNow,
        123, TimeSpan.FromSeconds(59), State: UploadState.Queued);
}
