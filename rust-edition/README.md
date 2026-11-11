# RollClip Rust

RollClip'in Windows 11 x64 için Rust ön sürümü. Ana ekran, klip arşivi, düzenleme ve ayarlar mevcut RollClip tasarımını temel alır. Uygulama FFmpeg ve FFprobe ile birlikte dağıtılır. Kullanıcı verileri `%LOCALAPPDATA%\RollClipRust` altında saklanır. C# sürümünden bir klip almak için MP4 dosyasını içe aktarın.

1. Pencereyi seçip **Kaydı başlat** düğmesine basın. Tampon dolunca **F8** ile seçili süreyi kaydedin. Ayarlarda WGC, GDI veya DXGI ve F6–F11 seçilebilir.
2. **Klip arşivi** bölümünden kaydedilen veya içe aktarılan MP4 dosyasını açın. Arama, favori, sıralama, küçük resimli ızgara ve liste kullanılabilir.
3. **Kes ve paylaş** bölümünde klip aralığını ve boyut hedefini seçin. Belirli bir kareyi önizleyebilir, yer imi ekleyebilir veya orijinali Windows oynatıcısında açabilirsiniz.
4. İsterseniz klibi Litterbox veya Gofile'a gönderin. Otomatik yükleme varsayılan olarak kapalıdır. Bekleyen yüklemeler yeniden açılışta devam eder. Yedek klasörü seçilirse klipler yerel klasöre kopyalanır.

Rust sürümü grafit yüzeyler ve sabit üst gezinme kullanır. Ekran geçişleri kayıt sırasında ve Windows animasyonları kapalıyken devre dışıdır. Editör kare önizlemesi ve sistem oynatıcısı sunar. Ses için tek bir DirectShow aygıtı seçilebilir. WPF sürümünün hareketli arka planı, arayüz sesleri, tepsi davranışı, işlem bazlı WASAPI sesi, ayrı mikrofon izi ve çoklu kısayollar ilk Rust ön sürümünde yer almıyor.

Windows'ta geliştirme için `cargo build --locked --release` komutunu bu klasörde çalıştırın. FFmpeg ve FFprobe'u paket içindeki `ffmpeg/bin` dizinine veya PATH'e yerleştirin. WGC için `gfxcapture` filtresi olan paket gerekir. Windows iş akışı paket, sentetik replay, kırpma, arşiv, pencere açılışı ve kurulum/kaldırmayı doğrular.
