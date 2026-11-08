using System.Diagnostics;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using ClipBridge.Core;

if (args is not [var path, var choice] || !File.Exists(path) || Mp4.ReadDuration(path) is null
    || !Enum.TryParse<Provider>(choice, true, out var provider) || !Enum.IsDefined(provider))
{
    Console.Error.WriteLine("Kullanım: ClipBridge.Bench <herkese açık test.mp4> <litterbox|gofile>");
    return 2;
}

using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
{
    Timeout = TimeSpan.FromMinutes(15),
    MaxResponseContentBufferSize = 128 * 1024
};
var watch = Stopwatch.StartNew();
try
{
    var result = await new FreeUploadProvider(client, provider).UploadAsync(path, 0, null, CancellationToken.None);
    double? playbackReadyMs = null;
    bool? matches = null;
    if (provider == Provider.Litterbox)
    {
        using var probe = new HttpRequestMessage(HttpMethod.Get, result.ShareUrl);
        probe.Headers.Range = new RangeHeaderValue(0, 1023);
        using var response = await client.SendAsync(probe, HttpCompletionOption.ResponseHeadersRead);
        if (response.IsSuccessStatusCode && response.Content.Headers.ContentType?.MediaType == "video/mp4")
        {
            await using var range = await response.Content.ReadAsStreamAsync();
            var buffer = new byte[1024];
            if (await range.ReadAsync(buffer) > 0)
                playbackReadyMs = watch.Elapsed.TotalMilliseconds;
        }
        if (playbackReadyMs.HasValue)
        {
            await using var original = File.OpenRead(path);
            await using var downloaded = await client.GetStreamAsync(result.ShareUrl);
            var originalHash = await SHA256.HashDataAsync(original);
            var downloadedHash = await SHA256.HashDataAsync(downloaded);
            matches = originalHash.AsSpan().SequenceEqual(downloadedHash);
        }
    }
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        provider = provider.ToString(),
        bytes = new FileInfo(path).Length,
        bodyWrittenMs = result.Timing.BodyWritten.TotalMilliseconds,
        responseWaitMs = result.Timing.ResponseWait.TotalMilliseconds,
        linkReadyMs = result.Timing.LinkReady.TotalMilliseconds,
        firstVideoBytesMs = playbackReadyMs,
        downloadMatches = matches,
        shareUrl = result.ShareUrl
    }));
    return matches == false ? 1 : 0;
}
catch (Exception error) when (error is HttpRequestException or UploadException or OperationCanceledException)
{
    Console.Error.WriteLine(error.Message);
    return 1;
}
