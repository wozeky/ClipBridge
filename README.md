# ClipBridge

Windows 11 x64 için açık kaynak oyun klibi kaydetme ve paylaşma uygulaması.

## Windows derlemesi

[Windows x64 Actions](https://github.com/wozeky/ClipBridge/actions/workflows/windows.yml) sayfasındaki başarılı bir çalıştırmanın **Artifacts** bölümünden `ClipBridge-win-x64` arşivini indirin. Arşivin tamamını açıp `ClipBridge.exe` dosyasını çalıştırın.

## Geliştirme

.NET 10 SDK ile `dotnet build ClipBridge.slnx` ve `dotnet test ClipBridge.slnx` çalıştırın. Windows paketi için workflow'daki publish komutunu kullanın.

Bu ilk aşama masaüstü penceresini ve derleme altyapısını içerir.

## MP4 paylaşımı

`MP4 içe aktar` yerel arşive bir kopya alır. Otomatik yüklemeyi Ayarlar'dan etkinleştirebilir veya seçilen klibi tek tek yükleyebilirsiniz. Paylaşım bağlantısı arşivde kalır ve ayara göre panoya kopyalanır. Bir hata oluşursa videonun yerel kopyası korunur. Servisi değiştirip başarısız bir klibi yeniden sıraya alabilirsiniz.

Varsayılan Litterbox sağlayıcısı 24 saatlik, doğrudan bir video bağlantısı döndürür. Gofile, dosyanın paylaşım sayfasını döndürür. Bağlantıyı alan kişiler yüklenen klibe erişebilir. Servislerin geçici depolama ve erişim kuralları uygulanır.

Sentetik bir MP4'le zamanlama ölçmek için `dotnet run --project tools/ClipBridge.Bench -- test.mp4 litterbox` çalıştırın. `gofile` ikinci seçenektir. Araç gerçek yükleme yapar. Gövde yazımı (istemci ölçümü), yanıta kadar geçen ek süre ve bağlantının hazır oluşunu ayrı raporlar. Litterbox için ayrıca ilk video baytlarına kadar geçen süreyi ve indirilen dosyanın bütünlüğünü kontrol eder.
