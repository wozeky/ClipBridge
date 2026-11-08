using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace ClipBridge.Core;

public interface IUploadProvider
{
    Provider Id { get; }
    Task<UploadResult> UploadAsync(string path, int limitKibPerSecond, IProgress<double>? progress,
        CancellationToken cancellationToken);
}

public sealed class UploadException(string message, bool retryable = false, TimeSpan? retryAfter = null,
    bool rejectedForCapacity = false)
    : Exception(message)
{
    public bool Retryable { get; } = retryable;
    public TimeSpan? RetryAfter { get; } = retryAfter;
    public bool RejectedForCapacity { get; } = rejectedForCapacity;
}

public sealed class FreeUploadProvider(HttpClient http, Provider provider) : IUploadProvider
{
    public Provider Id => provider;
    public const long LitterboxMaximumBytes = 1_000_000_000;

    public async Task<UploadResult> UploadAsync(string path, int limitKibPerSecond,
        IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var isLitterbox = Id == Provider.Litterbox;
        var endpoint = isLitterbox
            ? "https://litterbox.catbox.moe/resources/internals/api.php"
            : "https://upload.gofile.io/uploadfile";
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (file.Length == 0)
            throw new UploadException("Dosya boş.");
        if (isLitterbox && file.Length > LitterboxMaximumBytes)
            throw new UploadException("Litterbox'ın 1 GB dosya sınırı aşıldı.", rejectedForCapacity: true);

        // Use a neutral name for the upload. Imported filenames stay on the local device.
        using var multipart = new MultipartFormDataContent();
        if (isLitterbox)
        {
            multipart.Add(new StringContent("fileupload"), "reqtype");
            multipart.Add(new StringContent("24h"), "time");
        }
        var watch = Stopwatch.StartNew();
        var video = new VideoContent(file, Math.Max(0, limitKibPerSecond), progress, watch);
        video.Headers.ContentType = new MediaTypeHeaderValue("video/mp4");
        multipart.Add(video, isLitterbox ? "fileToUpload" : "file", "clipbridge.mp4");

        using var response = await http.PostAsync(endpoint, multipart, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            int status = (int)response.StatusCode;
            var retry = response.Headers.RetryAfter;
            var wait = retry?.Delta ?? (retry?.Date - DateTimeOffset.UtcNow);
            throw new UploadException($"{Id} HTTP {status} döndürdü.",
                status is 408 or 429 or >= 500 and <= 599, wait, rejectedForCapacity: status == 413);
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        var link = isLitterbox ? ParseLitterbox(body) : ParseGofile(body);
        return new UploadResult(link, new UploadTiming(video.BodyWrittenAt ?? watch.Elapsed, watch.Elapsed));
    }

    private static Uri ParseLitterbox(string body) => ValidateLink(body.Trim(), "litter.catbox.moe", "/");

    private static Uri ParseGofile(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            var status = root.GetProperty("status").GetString();
            if (status != "ok")
                throw new UploadException("Gofile yüklemeyi kabul etmedi.",
                    status is "error-notAvail" or "error-rateLimit", rejectedForCapacity: status == "error-limits");
            return ValidateLink(root.GetProperty("data").GetProperty("downloadPage").GetString(),
                "gofile.io", "/d/");
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new UploadException("Gofile yanıtı okunamadı.");
        }
    }

    private static Uri ValidateLink(string? value, string host, string prefix)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps
            || !uri.IsDefaultPort || uri.Host != host || uri.UserInfo.Length != 0
            || !uri.AbsolutePath.StartsWith(prefix, StringComparison.Ordinal)
            || uri.AbsolutePath.Length <= prefix.Length || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            throw new UploadException("Servis geçerli bir paylaşım bağlantısı döndürmedi.");
        return uri;
    }

    private sealed class VideoContent(FileStream input, int limitKibPerSecond, IProgress<double>? progress, Stopwatch transfer)
        : HttpContent
    {
        public TimeSpan? BodyWrittenAt { get; private set; }

        protected override bool TryComputeLength(out long length)
        {
            length = input.Length;
            return true;
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            SerializeToStreamAsync(stream, context, CancellationToken.None);

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context,
            CancellationToken cancellationToken)
        {
            input.Position = 0;
            var pacing = Stopwatch.StartNew();
            var lastReport = TimeSpan.Zero;
            var blockSize = limitKibPerSecond > 0 ? (int)Math.Clamp(limitKibPerSecond * 256L, 1024L, 65536L) : 65536;
            var buffer = new byte[blockSize];
            long sent = 0;
            int read;
            while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
            {
                if (limitKibPerSecond > 0)
                {
                    var wait = TimeSpan.FromSeconds((sent + read) / (limitKibPerSecond * 1024d)) - pacing.Elapsed;
                    if (wait > TimeSpan.Zero)
                        await Task.Delay(wait, cancellationToken);
                }
                await stream.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                sent += read;
                if (pacing.Elapsed - lastReport >= TimeSpan.FromMilliseconds(200) || sent == input.Length)
                {
                    lastReport = pacing.Elapsed;
                    progress?.Report(100d * sent / input.Length);
                }
            }
            BodyWrittenAt = transfer.Elapsed;
        }
    }
}
