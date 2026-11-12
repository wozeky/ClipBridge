using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace RollClip.App;

internal static class EnglishUi
{
    private static readonly Dictionary<string, string> Labels = new(StringComparer.Ordinal)
    {
        ["Klip editörü"] = "Clip editor", ["Kırpma"] = "Trim", ["Ses"] = "Audio", ["Dışa aktarma"] = "Export",
        ["Yer imleri"] = "Bookmarks",
        ["Ana ekran"] = "Home", ["Ana ekrana dön"] = "Back to home",
        ["Ayarlar"] = "Settings", ["Ayarları kapat"] = "Close settings", ["Ayarları kaydet"] = "Save settings",
        ["Klip arşivi"] = "Clip library", ["Arşivi kapat"] = "Close library", ["Klip klasörü"] = "Clip folder",
        ["Klasörü tara"] = "Recover clips", ["Klibi hazırla"] = "Prepare clip",
        ["Klip al, kes, Discord’a gönder."] = "Capture, trim, share to Discord.",
        ["Son klibi kes ve paylaş"] = "Trim and share latest clip", ["Kes ve paylaş"] = "Trim and share",
        ["Tanılama raporunu kopyala"] = "Copy diagnostics", ["Pencereleri yenile"] = "Refresh windows",
        ["Oyun penceresini seç"] = "Select game window", ["Kaydedilecek pencere"] = "Window to capture",
        ["Kaydedilecek oyun penceresi"] = "Game window to capture", ["Klip süresi"] = "Clip length",
        ["Son anları tek tuşla kaydet."] = "Save the last moments with a key.",
        ["Kayıt"] = "Capture", ["Kayıt kapalı"] = "Capture off", ["Kaydı başlat"] = "Start capture",
        ["Kaydı durdur"] = "Stop capture", ["Son klibi MP4 olarak kaydet"] = "Save replay as MP4",
        ["Paylaşım"] = "Sharing", ["Otomatik"] = "Auto", ["Yeni klipleri otomatik yükle"] = "Automatically upload new clips",
        ["MP4 içe aktar"] = "Import MP4", ["KAYIT"] = "CAPTURE", ["Kayıt kalitesi"] = "Recording quality",
        ["Yazılımsal kodlayıcı 720p30 ile sınırlıdır."] = "Software capture is limited to 720p30.",
        ["Yüksek · 1080p60"] = "High · 1080p60", ["Dengeli · 1080p30"] = "Balanced · 1080p30", ["Hafif · 720p30"] = "Light · 720p30",
        ["Yakalama yöntemi"] = "Capture method", ["Otomatik · WGC → GDI"] = "Auto · WGC → GDI",
        ["WGC · Pencere"] = "WGC · Window", ["DXGI · Ekran 0 (tüm ekran)"] = "DXGI · Output 0 (full display)", ["GDI · Pencere"] = "GDI · Window",
        ["Klip kısayolu"] = "Clip shortcut", ["Oyun etiketi"] = "Game label",
        ["Sistem sesi (Discord dahil)"] = "System audio (including Discord)",
        ["Sistem sesi yerine yalnız seçili oyunun sesi"] = "Capture only the selected game's audio",
        ["Mikrofonu ayrı kaydet"] = "Record microphone separately", ["Seçilen oyun açılınca kaydı başlat"] = "Start capture when the selected game opens",
        ["OYUN PROFİLLERİ"] = "GAME PROFILES", ["Kayıtlı oyun profilleri"] = "Saved game profiles",
        ["Seçili oyun için kaydet"] = "Save for selected game", ["Profili yükle"] = "Load profile", ["Profili sil"] = "Delete profile",
        ["Klip kaydedilince ses çal"] = "Play a sound when a clip is saved",
        ["Klip kaydedilince hafif bildirim sesi çal"] = "Play a subtle sound when a clip is saved",
        ["Son klibi paylaş"] = "Share latest clip",
        ["KAYIT VE SES"] = "CAPTURE & AUDIO",
        ["PAYLAŞIM VE GÖRÜNÜM"] = "SHARING & APPEARANCE",
        ["Gelişmiş kayıt, profiller ve ek kısayollar"] = "Advanced capture, profiles & shortcuts",
        ["Arşiv yedekleme ve depolama"] = "Backup & storage",
        ["Güncellemeler ve tanılama"] = "Updates & diagnostics",
        ["Tuşa bas…"] = "Press key…",
        ["Önizle"] = "Preview",
        ["Duraklat"] = "Pause",
        ["Önizlemeyi kapat"] = "Close preview",
        ["Önce kaydı aç"] = "Turn on capture first",
        ["Klip al · F8"] = "Save clip · F8",
        ["Klip ara…"] = "Search clips…",
        ["Diğer işlemler"] = "More actions",
        ["Klip sınırı (GB · 0 = sınırsız)"] = "Clip limit (GB · 0 = unlimited)",
        ["Test klibi al ve kontrol et"] = "Save and review a test clip", ["KLİP KAYNAĞI"] = "CLIP SOURCE",
        ["Klasör izleme kapalı"] = "Folder watching off", ["Klip klasörü izle"] = "Watch clip folder", ["İzlemeyi kapat"] = "Stop watching",
        ["Yeni MP4’ler yerel arşive alınır. Mevcut klipleri sürükleyip bırakabilirsiniz."] = "New MP4s are imported locally. Drop existing clips here.",
        ["Windows ile tepside başlat"] = "Start in tray with Windows", ["PAYLAŞIM"] = "SHARING",
        ["Yükleme servisi"] = "Upload service", ["Litterbox · 24 saat"] = "Litterbox · 24 hours",
        ["Catbox · kalıcı (200 MB)"] = "Catbox · permanent (200 MB)",
        ["Hız sınırı · KiB/sn"] = "Rate limit · KiB/s", ["Yükleme hız sınırı"] = "Upload rate limit",
        ["0 = sınırsız"] = "0 = unlimited", ["Limitte alternatif servisi dene"] = "Try fallback service at limit",
        ["Hazır bağlantıyı panoya kopyala"] = "Copy share link to clipboard",
        ["Bağlantıya sahip kişiler yüklenen klibi görebilir."] = "Anyone with the link can view the uploaded clip.",
        ["Arayüz sesleri"] = "Interface sounds",
        ["GÖRÜNÜM"] = "APPEARANCE", ["Hareketli krom arka plan"] = "Animate chrome background",
        ["Kayıt sırasında ve arka planda durur."] = "Paused during capture and in the background.",
        ["UZUN SÜRELİ ARŞİV"] = "LONG-TERM BACKUP", ["Otomatik kopya kapalı"] = "Automatic backup off",
        ["Drive ile eşitlenen bir klasör seçin. Yeni klipler buraya da kopyalanır."] = "Choose a Drive-synced folder to copy new clips into it.",
        ["Yedek klasörü"] = "Backup folder", ["Klasör seç"] = "Choose folder", ["Klasörü aç"] = "Open folder",
        ["Kapat"] = "Close", ["Eski klipleri yedekle"] = "Back up existing clips",
        ["Bulut yüklemesini Drive yapar. Eşitleme durumunu Drive’dan kontrol edin."] = "Drive uploads the copies. Check sync status in Drive.",
        ["Henüz klip yok. Klip al veya bir MP4’ü buraya bırak."] = "No clips yet. Capture one or drop an MP4 here.",
        ["Bağlantıyı kopyala"] = "Copy link", ["Bağlantı oluştur"] = "Create link", ["Küçült"] = "Minimize",
        ["Büyüt / geri yükle"] = "Maximize / restore",
        ["RollClip · Klibi hazırla"] = "RollClip · Prepare clip", ["Oynat"] = "Play",
        ["Önizleme konumu"] = "Preview position", ["Zaman çizelgesi yer imleri"] = "Timeline bookmarks",
        ["Yer imi ekle"] = "Add bookmark", ["Sil"] = "Remove", ["Yer imine git"] = "Jump to bookmark",
        ["Başlangıcı burada yap"] = "Set start here", ["Başlangıç, saniye"] = "Start, seconds",
        ["Bitişi burada yap"] = "Set end here", ["Bitiş, saniye"] = "End, seconds",
        ["Mikrofonu ekle"] = "Include microphone", ["Sesi kapat"] = "Mute", ["Dosya boyutu hedefi"] = "File size target",
        ["Paylaşım dosyasını kopyala"] = "Copy share file", ["Kırpılmış kopyayı kaydet"] = "Save trimmed copy",
        ["Paylaşım kopyasını aç"] = "Open share copy", ["İptal"] = "Cancel",
        ["Klip adına göre ara"] = "Search clip names", ["Oyun filtresi"] = "Game filter",
        ["Tarih filtresi"] = "Date filter", ["Yükleme filtresi"] = "Upload filter", ["Sıralama"] = "Sort",
        ["Yalnız favoriler"] = "Favorites only", ["Izgara görünümü"] = "Grid view",
        ["Konumu aç"] = "Show in folder", ["Yüklemeyi iptal et"] = "Cancel upload",
        ["Seçimi oynat"] = "Play selection", ["Önizleme ses seviyesi"] = "Preview volume",
        ["Dışa aktarma ilerlemesi"] = "Export progress", ["Yerel orijinalin yerine kaydet"] = "Replace local original",
        ["EK KLİP KISAYOLLARI"] = "EXTRA CLIP SHORTCUTS", ["Ek kısayollar"] = "Extra shortcuts",
        ["Ek kısayol tuşu"] = "Extra shortcut key", ["Ek kısayol süresi"] = "Extra shortcut duration",
        ["Yeni"] = "New", ["Ekle / uygula"] = "Add / apply", ["Kaldır"] = "Remove", ["Yenile"] = "Refresh",
        ["Mikrofon aygıtı"] = "Microphone device", ["Oyun / sistem %"] = "Game / system %",
        ["Mikrofon %"] = "Microphone %", ["DEPOLAMA"] = "STORAGE", ["Kullanımı ölç"] = "Measure usage",
        ["Küçük resimleri temizle"] = "Clear thumbnails", ["Klip sınırı (GB)"] = "Clip limit (GB)",
        ["Yedeği doğrulanan eski klipleri otomatik temizle"] = "Clean up old clips with verified backups",
        ["Paylaşılmamış klipleri koru"] = "Keep unshared clips", ["GÜNCELLEMELER"] = "UPDATES",
        ["Güncellemeleri otomatik kontrol et"] = "Check for updates automatically",
        ["Ön sürümleri dahil et"] = "Include prereleases", ["Kontrol et"] = "Check now",
        ["Sürüm notları / indir"] = "Release notes / download",
        ["Başlangıç ve bitiş saniye cinsinden. Paylaşım kopyası seçilen boyut sınırına uyar."] =
            "Start and end are in seconds. The share copy fits the selected size limit."
    };

    public static string Choose(bool english, string turkish, string translation) => english ? translation : turkish;

    public static void Apply(DependencyObject element)
    {
        if (element is Window window && Labels.TryGetValue(window.Title, out var title))
            window.Title = title;
        if (element is TextBlock text && text.ReadLocalValue(TextBlock.TextProperty) is string value &&
            Labels.TryGetValue(value, out var label))
            text.Text = label;
        if (element is ContentControl control && control.Content is string content && Labels.TryGetValue(content, out var caption))
            control.Content = caption;
        if (element is FrameworkElement framework)
        {
            if (framework.ToolTip is string tooltip && Labels.TryGetValue(tooltip, out var description))
                framework.ToolTip = description;
            if (framework.Tag is string tag && Labels.TryGetValue(tag, out var translatedTag))
                framework.Tag = translatedTag;
            if (Labels.TryGetValue(AutomationProperties.GetName(framework), out var accessible))
                AutomationProperties.SetName(framework, accessible);
        }
        foreach (var child in LogicalTreeHelper.GetChildren(element).OfType<DependencyObject>().ToArray())
            Apply(child);
    }
}
