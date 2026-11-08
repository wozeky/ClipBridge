# ClipBridge

Windows 11 x64 için ücretsiz, MIT lisanslı klip arşivi ve paylaşım uygulaması. Yerel bir MP4'ü içe aktarabilir veya OBS Studio'nun Replay Buffer'ından F8 ile klip alabilirsiniz.

## İndir ve çalıştır

[Windows x64 Actions](https://github.com/wozeky/ClipBridge/actions/workflows/windows.yml) sayfasındaki başarılı çalıştırmanın **Artifacts** bölümünden `ClipBridge-win-x64` ZIP'ini indirin. GitHub indirme için oturum açmanızı isteyebilir. ZIP'in tamamını bir klasöre çıkarıp `ClipBridge.exe` dosyasını çalıştırın. Paket .NET çalışma zamanını içerir. Erken sürüm dijital olarak imzalanmamıştır.

## İlk klip

1. **MP4 içe aktar** ile yerel bir video seçin. ClipBridge orijinali korur, Windows Videolar klasörü altındaki `ClipBridge` klasörüne bir kopya koyar.
2. **Yükle / yeniden dene** ile yüklemeyi başlatın. Her yeni klibin arka planda yüklenmesi için **Ayarlar → Yeni klipleri otomatik yükle** seçeneğini etkinleştirin ve ayarları kaydedin.
3. Hazır bağlantıyı Discord'a yapıştırın. Varsayılan pano ayarı bağlantıyı otomatik kopyalar. Önceki bağlantılar arşivden tekrar kopyalanabilir.

Kütüphane ve ayarlar `%LOCALAPPDATA%\ClipBridge` altında saklanır. Uygulama pencere kapatıldığında tepsiye iner. Tepsideki **Çıkış** ClipBridge'i kapatır. Bekleyen yüklemeler sonraki açılışta devam eder. Önceki isteğin sunucuya ulaşıp ulaşmadığı belirsiz kalan kesintilerde arşivden tekrar deneyebilirsiniz. Yerel video dosyaları korunur.

## F8 ile replay

1. [Güncel OBS Studio'yu](https://obsproject.com/download) kurun. OBS'de **Profil → Yeni** menüsünden tam olarak `ClipBridge` adlı ayrı bir profil oluşturup seçin.
2. Bir sahneye Windows Graphics Capture kullanan **Pencere Yakalama** veya **Ekran Yakalama** kaynağı ekleyin. Ekran yakalama masaüstündeki diğer pencereleri de kaydedebilir. Oyun sesini ve isterseniz mikrofonu OBS ses karıştırıcısından ayarlayın.
3. OBS'nin **Çıkış** ayarlarında Replay Buffer'ı açın. Kayıt için **Hybrid MP4**, **NVIDIA NVENC H.264** ve **AAC** seçin. Video ayarlarında 1920×1080, 60 FPS ile başlayın. 2560×1440 seçeneğini OBS'den değiştirebilirsiniz.
4. OBS'de **Araçlar → obs-websocket Ayarları** bölümünde sunucuyu varsayılan 4455 portunda açın ve parola doğrulamasını etkinleştirin. ClipBridge ayarlarına bu parolayı girin. Parola Windows kullanıcı hesabına bağlı DPAPI ile korunur. ClipBridge yalnızca yerel IPv4 adresindeki OBS sunucusuna bağlanır.
5. ClipBridge'de **Replay Buffer'ı başlat** düğmesine basın. Varsayılan süre 60 saniyedir. 15, 30, 60, 120 ve 180 saniye arasından seçim yapabilirsiniz. Tam süreyi alabilmek için tamponun dolmasını bekleyin ve **F8**'e basın. OBS'de de F8 için ikinci bir kısayol tanımlamaktan kaçının.
6. Süreyi değiştirmek veya yakalamayı durdurmak için OBS'de Replay Buffer'ı durdurun. ClipBridge, seçili OBS profilinin kayıt klasörünü Windows Videolar altındaki ClipBridge klasörüne yönlendirir. OBS'nin kullandığı kodlama ayarları ve tampon belleği, yakalanan gerçek süreyi etkiler. ClipBridge, OBS profilindeki tampon belleğini 512 MB olarak ayarlar. OBS bazı sabit bit hızlı ayarlarda bellek alanını süre ve bit hızından hesaplar. ClipBridge OBS'yi açık bırakır.

## Yükleme sağlayıcıları

| Servis | Ücretsiz otomasyon | Paylaşım ve sınırlar |
| --- | --- | --- |
| [Litterbox](https://litterbox.catbox.moe/) | Hesapsız, multipart API | Varsayılan. Doğrudan HTTPS video bağlantısı, 24 saatlik saklama, ClipBridge üst sınırı 1 GB. |
| [Gofile](https://gofile.io/api) | Hesapsız yükleme sentetik bir videoyla doğrulandı | Paylaşım sayfası bağlantısı. Ücretsiz saklama ve erişim koşulları değişebilir. |
| [Catbox](https://catbox.moe/faq.php) | Hesapsız API | 200 MB, anonim dosyalarda iki yıl erişimsizlikten sonra silme. Geçici paylaşım için Litterbox seçildi. |
| [Pixeldrain](https://pixeldrain.com/api) | API anahtarı gerekir | Ücretsiz indirme kotası, önizleme ve saklama kuralları uygulanır. |
| [Streamable](https://streamable.com/pricing) | Ücretsiz plan mevcut. Ücretsiz API erişimi doğrulanamadı. | 250 MB, 10 dakika, 90 gün saklama, reklamlı oynatma. |

Paylaşım bağlantısına sahip kişiler yüklenen klibe erişebilir. İsteğe bağlı sağlayıcı geçişi yalnızca kesin dosya veya kota reddinde devreye girer. Ağ kesintilerinde aynı sağlayıcı sınırlı sayıda yeniden denenir. Aktarım tek dosyalık kuyrukla çalışır ve hız sınırı KiB/sn olarak ayarlanabilir. Discord önizlemesi, sağlayıcının ve Discord'un erişim durumuna bağlıdır. Türkiye'deki erişim ve gecikme yerel bağlantıdan ölçülmelidir.

## Geliştirme ve doğrulama

.NET 10 SDK ile `dotnet build ClipBridge.slnx` ve `dotnet test ClipBridge.slnx` çalıştırın. [Windows workflow'u](.github/workflows/windows.yml) testleri, self-contained win-x64 yayını ve WPF penceresi açılış kontrolünü çalıştırır.

`dotnet run --project tools/ClipBridge.Bench -- test.mp4 litterbox` komutu gerçek bir yükleme yapar. `gofile` ikinci seçenektir. Paylaşabileceğiniz sentetik MP4 kullanın. Araç gövdenin istemcide yazılmasını, sunucu yanıtı beklemesini ve bağlantının hazır oluşunu ayrı ölçer. Paylaşım bağlantısını geçici bir yerel arşivden yeniden okur. Litterbox için ilk video baytlarına kadar geçen süreyi ve dosya bütünlüğünü de kontrol eder. Gofile paylaşım sayfası için izlenebilirlik ölçümü yoktur.

WPF, Windows'a özel ve .NET SDK ile doğrudan dağıtılabildiği için seçildi. WinUI 3 ek Windows App SDK dağıtım gereksinimleri, Avalonia ise çapraz platform UI bağımlılıkları getiriyor. OBS Studio, Replay Buffer, ses senkronizasyonu ve NVENC'i ayrı uygulama olarak sağlıyor. Yerel Windows Graphics Capture ve DXGI Desktop Duplication alternatifleri yakalama karelerini sağlar. Bunlarla ayrı bir replay motoru için kodlayıcı, ses, zamanlama ve mux altyapısı gerekir. ClipBridge paketinde OBS, libobs veya ayrı bir FFmpeg ikilisi bulunmaz.

Donanım doğrulaması için gerçek bir oyunda F8, arka arkaya klip, ses senkronu, NVENC, FPS etkisi ve anti-cheat davranışı ölçülmelidir. CI'deki OBS WebSocket testleri protokolü doğrular.
