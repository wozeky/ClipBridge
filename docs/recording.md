# Windows kayıt motoru

## Kaynak incelemesi

- [Capcove kayıt oturumu](https://github.com/xacnio/capcove/blob/master/src-tauri/src/recording/capture_session.rs), Windows Graphics Capture ile pencere seçip kare akışını kodlayıcıya iletir. [Replay tamponu](https://github.com/xacnio/capcove/blob/master/src-tauri/src/recording/replay_buffer.rs), anahtar kareden başlayan parçaları tutar ve süre doldukça çıkarır.
- [Snag FFmpeg kodu](https://github.com/focatdev/snag/blob/main/internal/ffmpeg/ffmpeg.go) donanım kodlayıcıları ve sürücü hatalarını ayrı ele alır. [WASAPI kodu](https://github.com/focatdev/snag/blob/main/internal/wasapi/process_windows.go) süreç bazlı ses yakalama örneğidir.
- [Windows Graphics Capture](https://learn.microsoft.com/en-us/windows/apps/develop/media-authoring-processing/screen-capture) pencereyi Windows üzerinden yakalar. [FFmpeg gfxcapture kaynağı](https://www.ffmpeg.org/doxygen/8.1/vsrc__gfxcapture_8c_source.html) pencere tanıtıcısı, kare hızı ve ölçekleme ayarlarını tanımlar. [FFmpeg segment muxer](https://github.com/FFmpeg/FFmpeg/blob/a35157abb9/libavformat/segment.c) kapanan parça için CSV satırını yazar ve akışı boşaltır.

RollClip bu desenleri kendi C# akışıyla uygular. Yukarıdaki klip uygulamalarından kaynak kod alınmadı.

## Uygulama

`WindowsCapture` görünür ve küçültülmemiş pencereleri listeler. Kullanıcı bir pencere seçince paketle gelen FFmpeg, bu pencerenin HWND değerini Windows Graphics Capture'a iletir. En-boy oranı korunarak ölçeklenir. Önce 1080p60 için H.264 NVENC, ardından AMD AMF denenir. Donanım kodlama açılmazsa yazılımsal Media Foundation 720p30 kullanır. WGC ile kayıt açılmazsa aynı HWND için GDI ve Media Foundation denenir. WGC aygıtı açılamazsa aynı hatayı verecek diğer WGC denemeleri atlanır. Ham video kuyruğu sekiz paketle sınırlıdır. GDI, bazı oyunlarda donanımla çizilen kareleri okuyamayabilir. Kullanılan yöntem ve kayıt biçimi durum satırında gösterilir. Oyun sürecine kod yüklenmez.

`ReplayRecorder` yaklaşık bir saniyelik anahtar kareli MPEG-TS parçaları kullanır. Yalnızca FFmpeg'in tamamlandı diye bildirdiği parçalar kayda alınır. Zaman aralığının dışına düşen geçici parçalar silinir. F8, eldeki parçaları yeniden kodlamadan MP4 olarak birleştirir. Dışa aktarma bitmeden yerel arşive veya yükleme kuyruğuna giriş açılmaz. Gecikme ve süre sapması, son parçanın kapanması ve anahtar kare konumuna bağlıdır.

Sistem sesi ve isteğe bağlı mikrofon, NAudio üzerinden Windows WASAPI ile alınır ve yerel adlandırılmış kanallardan FFmpeg'e aktarılır. İkisi seçilirse tek AAC izinde birleştirilir. Sistem sesi tüm varsayılan çıkışı kapsar. Oyuna özel ses ayrımı ve 1440p ayrı geliştirme işleridir.

## Dağıtım ve doğrulama

ZIP, sabitlenmiş ve SHA-256 doğrulanmış [FFmpeg-Builds LGPL sürümünü](https://github.com/FFmpeg-Builds/FFmpeg-Builds/releases/tag/autobuild-2026-11-08-12-35) ayrı bir çalıştırılabilir dosya olarak içerir. İlgili kaynak ve derleme bağlantıları paket içindeki `ffmpeg/SOURCE.txt` dosyasındadır. NAudio lisansı `licenses/NAudio.txt` altındadır.

CI üç ayrı kontrol yapar. WPF pencere ve arşiv açılışı, sentetik sesli görüntüden art arda iki tampon klibi ve barındırılan Windows masaüstünde bir uygulama penceresinin yakalanması. NVENC, oyun sesinin senkronu ve gerçek oyun performansı için fiziksel donanım testi gerekir.
