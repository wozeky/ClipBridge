# RollClip

**Klibi al, kes, Discord’a yapıştır.** RollClip, Windows 11 x64 için ücretsiz, MIT lisanslı bir oyun klipçisidir. Hesap açmadan F8 ile klip alın veya mevcut MP4’leri sürükleyin. Orijinal klip bilgisayarınızda kalır. Yakalama için OBS kurulumu gerekmez. RollClip, Windows yakalama API’lerini ve paketle gelen FFmpeg’i kullanır.

## İndir

- **[Windows yükleyicisi](https://github.com/wozeky/RollClip/releases/download/v0.4.7/RollClip-Setup-win-x64.exe)** · Türkçe/İngilizce, kullanıcı hesabına kurulum ve Başlat menüsü kısayolu.
- **[Taşınabilir Windows ZIP](https://github.com/wozeky/RollClip/releases/download/v0.4.7/RollClip-win-x64.zip)** · ZIP’in tamamını çıkarıp `RollClip.exe` dosyasını çalıştırın.

SHA-256 dosyaları [Release sayfasında](https://github.com/wozeky/RollClip/releases/tag/v0.4.7) bulunur. **Ayarlar → Dil** üzerinden Türkçe ve İngilizce temel arayüz arasında geçebilirsiniz. Dili kaydedip uygulamayı yeniden açın. Bazı hata ve tanılama metinleri Türkçe kalır.

## Ekran görüntüleri

Görüntüler, Windows üzerinde çalışan RollClip uygulamasından alınmıştır.

![RollClip ana ekranı](https://github.com/wozeky/RollClip/releases/download/v0.4.7/RollClip-Ana-Ekran.png)

| Ayarlar | Klip arşivi |
| --- | --- |
| ![RollClip ayarları](https://github.com/wozeky/RollClip/releases/download/v0.4.7/RollClip-Ayarlar.png) | ![RollClip klip arşivi](https://github.com/wozeky/RollClip/releases/download/v0.4.7/RollClip-Klip-Galerisi.png) |

## ClipBridge'den geçiş

Eski uygulamadan sistem tepsisindeki **Çıkış** ile çıkın ve yeni paketi açıp
`RollClip.exe` dosyasını çalıştırın. RollClip klasörü yoksa mevcut ClipBridge
veri ve video klasörleri yerinde kullanılır. Böylece arşiv yolları, ayarlar,
klipler ve bekleyen yüklemeler korunur. Yeni kurulumlar RollClip klasörlerini
kullanır. İlk açılış eski Windows başlangıç kaydını RollClip'e taşır.

## Discord’a klip gönder

1. F8 ile bir klip alın veya MP4’ü RollClip’e sürükleyip bırakın. **Ayarlar → Klip kaynağı** bölümünden ShadowPlay veya başka bir kayıt uygulamasının klasörünü de izleyebilirsiniz. Yeni, tamamlanmış MP4’ler yerel arşive alınır. İzlenen klasörden gelen klipler otomatik yüklenmez.
2. **Kes ve paylaş** ekranında klibi izleyin ve başlangıç/bitiş noktasını seçin. **Paylaşım dosyasını kopyala** seçilen boyut sınırına uygun MP4’ü panoya alır. Varsayılan Discord ön ayarı 19 MB, diğer hedefler 10/50/100/500 MB. Alıcının veya hizmetin dosya sınırına göre seçin. Zaman çizelgesindeki yer imleri arşivde saklanır ve bir tıklamayla ilgili ana gider. Discord’da `Ctrl+V` ile ekleyin. Zaten sığıyorsa ve düzenleme istemediyseniz orijinal MP4 doğrudan kopyalanır. Hazırlanan Discord kopyaları **Videolar → RollClip → Paylaşım** klasöründe kalır.
3. Uzun klipler için **Bağlantı oluştur** seçeneğini kullanabilirsiniz. Yalnızca istediğiniz kesiti yüklemek için önce **Kırpılmış kopyayı kaydet** deyin. Litterbox bağlantılarında arşiv bitiş saatini gösterir ve yeni bağlantı üretebilir.

Kütüphane ve ayarlar `%LOCALAPPDATA%\RollClip` altında saklanır. Uygulama pencere kapatıldığında tepsiye iner. Tepsideki **Çıkış** RollClip’i kapatır. Bekleyen yüklemeler sonraki açılışta devam eder. Yerel video dosyaları korunur.

## F8 ile replay

1. Oyunu açın. RollClip'de **Pencereleri yenile** ile oyun penceresini seçin.
2. Ayarlarda **Yalnız seçili oyunun sesi** ile oyun işleminin sesini alın. **Sistem sesi** bütün varsayılan çıkışı kaydeder. Mikrofonu ayrıca açarsanız ikinci ses izi olarak saklanır ve paylaşırken dahil edilebilir.
3. **Kaydı başlat** düğmesine basın. İsterseniz oyun penceresi seçiliyken **Seçilen oyun açılınca kaydı başlat** ayarını kaydedin. **Test klibi al ve kontrol et** seçeneği, otomatik yükleme açık olsa bile yerel bir klip açar. Tampon doldukça daha uzun klip alabilirsiniz.
4. **F8** ile son tamamlanan parçalar MP4'e kaydolur. Kesim yaklaşık bir saniyelik parçalara göre yapılır. Otomatik yükleme açıksa bağlantı arşive eklenir ve pano ayarı açıksa panoya kopyalanır.
5. **Kaydı durdur** ile geçici tamponu kapatın. Kalıcı MP4 dosyaları Videolar altındaki `RollClip` klasöründe kalır.

**Ayarlar → Kayıt kalitesi** bölümünde **Yüksek (1080p60)**, **Dengeli (1080p30)** veya **Hafif (720p30)** seçin. **Klip kısayolu** için F6–F11 kullanılabilir. Varsayılan F8’dir. İki ayar da sonraki kayıt oturumunda uygulanır.

Yakalama yalnızca seçilen pencereyi hedefler. Kayıt, seçilen profil için sırasıyla NVIDIA NVENC ve AMD AMF donanım kodlamasını dener. Yazılımsal Media Foundation yedeği 720p30 kullanır. Windows Graphics Capture başlatılamazsa aynı pencere için Windows GDI denenir. GDI, donanım hızlandırmalı bazı oyunlarda boş görüntü verebilir. Kullanılan yakalama yöntemi, kodlayıcı ve kayıt biçimi durum satırında görünür.

## Oyun profilleri ve yakalama

Ana ekrandan oyun penceresini seçin. Kalite, süre, ses, etiket, yöntem ve otomatik başlatma ayarlarını belirleyip **Ayarlar → Seçili oyun için kaydet** deyin. Profil işlem adına bağlıdır ve ilgili oyun kaydı başlarken genel ayarların üzerine uygulanır. Bilinen başlatıcılar ve anti-cheat süreçleri pencere listesinden çıkarılır.

- **Otomatik** pencere için WGC’yi, gerekirse GDI’ı dener. **WGC** ve **GDI** yalnızca seçili pencereye yöneliktir.
- **DXGI** ekran 0’ın tamamını kaydeder. Başlamadan önce onay ister ve otomatik başlatma için kullanılmaz.
- NVENC, AMF ve Intel QSV denenir. Donanım kodlama açılmazsa yazılımsal Media Foundation kullanılır. Video SDR olarak kaydedilir.
- Klip diske yazılınca isteğe bağlı bir ses çalar.

## v0.4.7 ile gelenler

Ana ekran, ayarlar ve klip galerisi yenilendi. Sistem tepsisi sadeleştirildi, klip galerisinde hızlı önizleme ve Catbox paylaşımı eklendi. Kısayollar tuşa basarak atanabilir ve kayıt açıkken güncellenir.

## v0.4.6 ile gelenler

Klip editöründeki **Discord'a gönder** menüsünden birden fazla sunucu kanalı kaydedebilirsiniz. Discord'da kanal için webhook oluşturup adresi bir kez RollClip'e ekleyin. Dosya seçtiğiniz kanala webhook adına gönderilir. Webhook adresleri Windows DPAPI ile korunur. Arkadaş ve grup sohbetleri için **Discord'u aç ve MP4'ü kopyala** seçeneğini kullanıp gönderimi Discord'da tamamlayın.

## v0.4.5 ile gelenler

Ana ekran kartları, Ayarlar ve Klip arşivi başlıklarında Manrope SemiBold kullanılır. Küçük metinler Segoe UI Variable ile gösterilir. Manrope uygulamayla birlikte gelir.

## v0.4.4 ile gelenler

Ana ekran, ayarlar, klip galerisi, editör ve sistem tepsisindeki kontroller kısa etkileşim sesleri çalar. Açma ve kapama tonları ayrıdır. **Ayarlar → Görünüm → Arayüz sesleri** ile kapatılabilir.

## v0.4.3 ile gelenler

Ana ekrandaki Paylaşım simgesi kartın içinde daha ortaya alındı.

## v0.4.2 ile gelenler

Ana ekrandaki Paylaşım simgesinin metalik yüzeyi yumuşatıldı.

## v0.4.1 ile gelenler

Pencere kontrolleri, galeri filtreleri, favoriler ve araç çubuğu Microsoft Fluent vektör simgelerine geçti. Pencere büyütüldüğünde geri yükleme simgesi gösterilir.

## v0.4.0 ile gelenler

- **Galeri:** Grid/liste, isim araması, oyun/tarih/yükleme/favori filtreleri ve tarih/süre/boyut sıralaması. FFmpeg küçük resimleri yalnızca görünen kartlar için üretir ve dosya değişmediği sürece tekrar kullanır.
- **Editör:** Seçili aralığı oynatın, önizleme sesini değiştirin ve gerçek FFmpeg ilerlemesini izleyin. Hassas kesimler yeniden kodlanır. Tam aralıkta uygun ses seçenekleriyle stream copy kullanılır. Paylaşılmış veya yedeklenmiş klipler için yerel orijinali değiştirme devre dışıdır.
- **Kayıt:** Mevcut tuşa ek olarak Ctrl/Shift ve klip süresi içeren en fazla sekiz kısayol ekleyin. Tampon, kayıttaki en uzun seçili süreyi hedefler. Arka arkaya gelen en fazla üç istek bekleyebilir. Sıradaki klip, işlenmeye başladığı anda elde olan tamponu kullanır.
- **Ses:** Mikrofon aygıtı ve oyun/sistem ile mikrofon seviyeleri genel ayarda veya oyun profilinde saklanır. Oyun sesi modu başka uygulamaların sesine otomatik geçmez. Kayıt sırasında ses aygıtı hata verirse ilgili iz sessiz kalabilir.
- **Paylaşım ve depolama:** Yükleme iptal edilebilir ve hız/süre tahmini gösterilir. Otomatik temizleme isteğe bağlıdır. Favoriler korunur. Klip dosyasının ayrı yedekle içeriği doğrulanır ve temizleme sonrası arşiv ile paylaşım geçmişi kalır. Doğrulama, Drive bulut senkronizasyonunu onaylamaz.
- **Güncellemeler:** Ayarlarda sürümü görün, GitHub sürümlerini kontrol edin ve sürüm notlarını tarayıcıda açın. Otomatik kontrol ve ön sürümleri dahil etme ayrı tercihlerdir.

Windows CI temel WPF yerleşimini sınar. Farklı ekran ölçekleri, global kısayol kayıtları, WASAPI aygıt geçişi, gerçek oyun FPS etkisi ve tam ekran bildirimleri fiziksel Windows’ta ayrıca sınanmalıdır. Bildirimler için Windows sistem tepsisi kullanılır. Özel bildirim konumu, canlı ses ölçer, ayrı Discord izi ve duraklatılabilir transfer bu sürümde bulunmuyor.

## Google Drive arşivi

1. Google Drive for desktop uygulamasında eşitlenen **Drive’ım** klasöründe bir RollClip arşiv klasörü oluşturun.
2. RollClip’te **Ayarlar → Uzun süreli arşiv → Klasör seç** ile bu klasörü seçin ve ayarları kaydedin.
3. Yeni klipler arka planda yıl ve ay klasörlerine kopyalanır. Bekleyen otomatik kopyalar, aynı klasör seçiliyken RollClip yeniden açıldığında tekrar denenir.
4. **Klasörü aç** ile yerel kopyalara ulaşın. Var olan klipler veya başarısız kopyalar için **Eski klipleri yedekle** düğmesini kullanın. Aynı adlı bir dosya varsa içerik doğrulanır ve mevcut dosyanın üzerine yazılmaz.

RollClip yerel kopyayı oluşturur. Drive’a yüklemeyi Google Drive for desktop yapar. Eşitleme tamamlanınca dosyayı Drive web arayüzünde kontrol edin. Paylaşım bağlantısının sona ermesi arşivdeki MP4’ü etkilemez.

![RollClip uzun süreli arşiv ayarları](https://github.com/wozeky/RollClip/releases/download/v0.4.7/RollClip-Arsiv.png)

## Oyun içi doğrulama

Kıyaslama için aynı çözünürlük, FPS, klip süresi ve bağlantıyla ShadowPlay, Moments, Medal ve RollClip’i deneyin. Valorant, CS2 ve bir GPU ağırlıklı oyunda şu değerleri kaydedin:

- F8’den diskte oynatılabilir dosyaya ve Discord’da izlenebilir klibe kadar geçen süre.
- Kaçırılan klipler, boş görüntü ve ses senkronu. Arka arkaya iki F8, Alt+Tab ve oyunu kapatıp açmayı da deneyin.
- Kayıt kapalı ve açıkken yüzde birlik düşük FPS, 95. yüzdelik kare süresi ve süreçlerin toplam CPU/GPU kullanımı.

Windows CI masaüstü yakalamayı, Discord çıktısını ve yükleyicinin sessiz kurulum/kaldırma akışını sınar. Donanım kodlama, seçili oyun sesi ve Discord’da gerçek oynatma için fiziksel Windows doğrulaması gerekir.

## Tanılama raporu

Kayıt denemesi bittikten sonra **Tanılama raporunu kopyala** düğmesine basın. Rapor bu oturumdaki son kayıt denemesini, son kaydedilen klibi ve paylaşım durumunu gösterir. MP4 hazır olduktan sonraki süre, varsa yükleme sırasını ve yeniden denemeleri de içerir. Rapor yerelde hazırlanır. Ham hata çıktısı yerine bilinen hata türlerini gösterir ve kişisel yolları, pencere adlarını veya paylaşım bağlantılarını içermez.

## Yükleme sağlayıcıları

| Servis | Ücretsiz otomasyon | Paylaşım ve sınırlar |
| --- | --- | --- |
| [Litterbox](https://litterbox.catbox.moe/) | Hesapsız, multipart API | Varsayılan. Doğrudan HTTPS video bağlantısı, 24 saatlik saklama, RollClip üst sınırı 1 GB. |
| [Gofile](https://gofile.io/api) | Hesapsız yükleme sentetik bir videoyla doğrulandı | Paylaşım sayfası bağlantısı. Ücretsiz saklama ve erişim koşulları değişebilir. |
| [Catbox](https://catbox.moe/faq.php) | Hesapsız API | 200 MB, anonim dosyalarda iki yıl erişimsizlikten sonra silme. Geçici paylaşım için Litterbox seçildi. |
| [Pixeldrain](https://pixeldrain.com/api) | API anahtarı gerekir | Ücretsiz indirme kotası, önizleme ve saklama kuralları uygulanır. |
| [Streamable](https://streamable.com/pricing) | Ücretsiz plan mevcut. Ücretsiz API erişimi doğrulanamadı. | 250 MB, 10 dakika, 90 gün saklama, reklamlı oynatma. |

Paylaşım bağlantısına sahip kişiler yüklenen klibe erişebilir. İsteğe bağlı sağlayıcı geçişi yalnızca kesin dosya veya kota reddinde devreye girer. Ağ kesintilerinde aynı sağlayıcı sınırlı sayıda yeniden denenir. Aktarım tek dosyalık kuyrukla çalışır ve hız sınırı KiB/sn olarak ayarlanabilir. Discord önizlemesi, sağlayıcının ve Discord'un erişim durumuna bağlıdır. Türkiye'deki erişim ve gecikme yerel bağlantıdan ölçülmelidir.

## Geliştirme ve doğrulama

.NET 10 SDK ile `dotnet build RollClip.slnx` ve `dotnet test RollClip.slnx` çalıştırın. [Windows workflow'u](.github/workflows/windows.yml) testleri, self-contained win-x64 yayını ve WPF penceresi açılış kontrolünü çalıştırır.

`dotnet run --project tools/RollClip.Bench -- test.mp4 litterbox` komutu gerçek bir yükleme yapar. `gofile` ikinci seçenektir. Paylaşabileceğiniz sentetik MP4 kullanın. Araç gövdenin istemcide yazılmasını, sunucu yanıtı beklemesini ve bağlantının hazır oluşunu ayrı ölçer. Paylaşım bağlantısını geçici bir yerel arşivden yeniden okur. Litterbox için ilk video baytlarına kadar geçen süreyi ve dosya bütünlüğünü de kontrol eder. Gofile paylaşım sayfası için izlenebilirlik ölçümü yoktur.

Kayıt mimarisi ve kaynak incelemesi için [Windows kayıt motoru notlarına](docs/recording.md) bakın. Windows CI, sentetik görüntü ve sesle ardışık MP4 dışa aktarmayı sınar. Ayrıca barındırılan Windows oturumunda gerçek pencere yakalama adımı çalıştırılır. Bu adımın sonucunu workflow logundan kontrol edin.

Gerçek oyunda F8, NVENC, FPS etkisi, ses senkronu ve anti-cheat davranışı fiziksel Windows testinde ölçülmelidir.

## Siyah krom arayüz

Ana ekran, sağlanan hareketli referansın panel oranlarını kullanır. Pencereyi üstteki kapsülden seçin; soldaki **15 / 30 / 60 / 120 / 180** seçeneklerinden süreyi belirleyin. **Kayıt** kartındaki anahtarla kaydı başlatın ve **F8** ile klip alın. Alttaki kapsülden arşive ve ayarlara ulaşabilirsiniz. Paylaşım kartındaki **Otomatik** anahtarı yeni kliplerin yüklenmesini açar. Krom yükleme düğmesi arşivde seçili klibi yükler.

Yeni kurulumlarda arka plan sabittir. **Ayarlar → Görünüm → Hareketli krom arka plan** ile hareket açılabilir. RollClip arka plandayken hareket duraklar. Kayıt başladığında veya hareket kapatıldığında video çözücüsü kapanır ve aynı krom materyalin sabit görüntüsü kullanılır. [Referans ölçümleri, uygulama ayrıntıları ve bilinen sınırlar](docs/ui-reference.md).
