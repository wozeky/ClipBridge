using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace RollClip.Core;

// A session report. Only fixed labels, numeric measurements and known error categories are exported.
public sealed class ReplayDiagnostics
{
    private const string Unmeasured = "Ölçülmedi";
    private string _captureStatus = "Henüz denenmedi";
    private string _source = "Belirlenmedi";
    private string _encoder = "Belirlenmedi";
    private (int Width, int Height, int Fps)? _videoFormat;
    private string? _fallback;
    private string? _captureError;
    private string _saveStatus = "Henüz denenmedi";
    private string _trigger = "Henüz denenmedi";
    private string? _saveError;
    private TimeSpan? _saveTime;
    private Guid? _clipId;
    private long _mp4ReadyAt;
    private TimeSpan? _duration;
    private UploadState? _uploadState;
    private TimeSpan? _linkTime;
    private string? _uploadError;

    public void BeginCapture()
    {
        _captureStatus = "Başlatılıyor";
        _source = _encoder = "Belirlenmedi";
        _fallback = _captureError = null;
        _videoFormat = null;
    }

    public void CaptureStarted(string source, string encoder, string fallbackDetails,
        (int Width, int Height, int Fps)? videoFormat = null)
    {
        _captureStatus = "Hazır";
        _source = source is "Windows Graphics Capture" or "Windows GDI" ? source : "Bilinmiyor";
        _encoder = encoder is "NVIDIA NVENC" or "AMD AMF" or "Media Foundation" ? encoder : "Bilinmiyor";
        _videoFormat = videoFormat;
        _fallback = FallbackReason(fallbackDetails);
    }

    public void CaptureStopped() => _captureStatus = "Durduruldu";

    public void CaptureFailed(Exception error, string fallbackDetails = "")
    {
        _captureStatus = "Başarısız";
        _captureError = CaptureError(error);
        _fallback = FallbackReason(fallbackDetails) ?? _fallback;
    }

    public void BeginSave(bool viaHotkey, string hotkey = "F8")
    {
        _trigger = viaHotkey ? hotkey : "Klip al düğmesi veya tepsi";
        _saveStatus = "MP4 hazırlanıyor";
        _saveError = null;
        _saveTime = null;
    }

    public void Mp4Ready(Guid id, long saveStartedAt, long readyAt)
    {
        _saveStatus = "MP4 hazır";
        _saveTime = Stopwatch.GetElapsedTime(saveStartedAt, readyAt);
        _clipId = id;
        _mp4ReadyAt = readyAt;
        _duration = _linkTime = null;
        _uploadState = null;
        _uploadError = null;
    }

    public void SaveFailed(Exception error, bool mp4Ready)
    {
        _saveStatus = mp4Ready ? "MP4 hazır, arşive ekleme başarısız" : "Başarısız";
        _saveError = CaptureError(error);
    }

    public void ClipChanged(Clip clip, long changedAt)
    {
        if (_clipId != clip.Id)
            return;
        _duration = clip.Duration;
        _uploadState = clip.State;
        _uploadError = clip.Error is null ? null : UploadError(clip.Error);
        if (clip.State == UploadState.Uploaded && _linkTime is null)
            _linkTime = Stopwatch.GetElapsedTime(_mp4ReadyAt, changedAt);
    }

    public string BuildReport(Version version, bool hotkeyRegistered, string hotkey = "F8")
    {
        var shareStatus = _uploadState switch
        {
            UploadState.Local => "Yükleme başlatılmadı",
            UploadState.Queued => "Sırada",
            UploadState.Uploading => "Yükleniyor",
            UploadState.Uploaded => "Bağlantı hazır",
            UploadState.Failed => "Başarısız",
            _ => "Yükleme bilgisi yok"
        };

        var format = _videoFormat is { } video
            ? FormattableString.Invariant($"{video.Width}x{video.Height} @ {video.Fps} FPS")
            : "Belirlenmedi";
        return string.Join(Environment.NewLine,
            $"RollClip {version.ToString(3)} · Tanılama raporu",
            $"Kayıt durumu: {_captureStatus}",
            $"Yakalama yöntemi: {_source}",
            $"Kodlayıcı: {_encoder}",
            $"Kayıt biçimi: {format}",
            $"Yedek yöntem nedeni: {_fallback ?? "Yok"}",
            $"Kayıt hatası: {_captureError ?? "Yok"}",
            $"{hotkey} kısayolu: {(hotkeyRegistered ? "Etkin" : "Etkin değil")}",
            $"Son klip alma denemesi: {_saveStatus}",
            $"Tetikleyici: {_trigger}",
            $"Klip alma hatası: {_saveError ?? "Yok"}",
            $"Tetikleme → MP4 hazır: {Seconds(_saveTime)}",
            $"Son kaydedilen klibin süresi: {Seconds(_duration)}",
            $"Son kaydedilen klibin paylaşımı: {shareStatus}",
            $"MP4 hazır → paylaşım bağlantısı hazır: {Seconds(_linkTime)}",
            $"Yükleme hatası: {_uploadError ?? "Yok"}");
    }

    private static string Seconds(TimeSpan? time) => time is { } measured
        ? measured.TotalSeconds.ToString("F2", CultureInfo.InvariantCulture) + " sn"
        : Unmeasured;

    private static string CaptureError(Exception error) => error switch
    {
        OperationCanceledException => "İşlem zaman aşımına uğradı veya iptal edildi.",
        UnauthorizedAccessException => "Windows dosyaya veya aygıta erişimi reddetti.",
        IOException => "Yerel dosya veya arşiv işlemi tamamlanamadı.",
        COMException or Win32Exception => "Windows aygıtı veya kayıt işlemi başlatılamadı.",
        CaptureException => KnownCaptureReason(error.Message) ?? "Yakalama motoru hata bildirdi.",
        _ => "Kayıt işlemi tamamlanamadı."
    };

    private static string? FallbackReason(string details) => string.IsNullOrWhiteSpace(details)
        ? null
        : KnownCaptureReason(details) ?? "Önceki yakalama veya kodlama yöntemi başlatılamadı.";

    private static string? KnownCaptureReason(string message)
    {
        if (message.Contains("Failed to create D3D11VA device", StringComparison.OrdinalIgnoreCase))
            return "Direct3D 11 yakalama aygıtı açılamadı.";
        if (message.Contains("nvencodeapi64.dll", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("No capable devices found", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("minimum required Nvidia driver", StringComparison.OrdinalIgnoreCase))
            return "NVIDIA NVENC aygıtı veya sürücüsü kullanılamadı.";
        if (message.Contains("İlk kareler zamanında gelmedi", StringComparison.Ordinal))
            return "Seçilen pencereden ilk kareler zamanında gelmedi.";
        if (message.StartsWith("MP4 kaydedilemedi.", StringComparison.Ordinal))
            return "Yakalama motoru MP4 dosyasını yazamadı.";

        return message switch
        {
            "Açık ve küçültülmemiş bir pencere seçin." => "Kayıt için açık bir pencere seçilmedi.",
            "Kayıt ayarları kaydedilemedi." => "Kayıt ayarları kaydedilemedi.",
            "FFmpeg bulunamadı. GitHub Actions'taki tam RollClip ZIP paketini kullanın." => "Kayıt motoru paket içinde bulunamadı.",
            "FFmpeg başlatılamadı." => "Kayıt motoru başlatılamadı.",
            "Windows ses aygıtının PCM biçimi desteklenmiyor." => "Windows ses aygıtının biçimi desteklenmiyor.",
            "İlk kayıt parçasının tamamlanmasını bekleyin." => "Kayıt tamponu henüz dolmaya başlamadı.",
            "Pencereden yeni görüntü gelmiyor. Pencereyi geri açın." or "Windows görüntü akışı durdu."
                => "Seçilen pencereden görüntü akışı durdu.",
            "Kayıt parçasının zaman bilgisi okunamadı." => "Kayıt parçasının zaman bilgisi okunamadı.",
            _ => null
        };
    }

    private static string UploadError(string message)
    {
        var fields = message.Split(' ', 4);
        if (fields is ["Litterbox" or "Gofile", "HTTP", var code, _] &&
            int.TryParse(code, NumberStyles.None, CultureInfo.InvariantCulture, out var status) &&
            status is >= 400 and <= 599)
            return $"Yükleme servisi HTTP {status} döndürdü.";

        return message switch
        {
            "Yükleme servisine bağlanılamadı." => "Yükleme servisine bağlanılamadı.",
            "Yükleme zaman aşımına uğradı." => "Yükleme zaman aşımına uğradı.",
            "Yerel video dosyası okunamadı." => "Yerel video dosyası okunamadı.",
            "Litterbox'ın 1 GB dosya sınırı aşıldı." => "Yükleme servisinin dosya sınırı aşıldı.",
            "Servis geçerli bir paylaşım bağlantısı döndürmedi." => "Servis geçerli bir paylaşım bağlantısı döndürmedi.",
            _ => "Yükleme denemesi başarısız oldu."
        };
    }
}
