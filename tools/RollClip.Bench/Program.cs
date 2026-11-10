using System.Diagnostics;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using RollClip.Core;

if (args is ["replay-smoke", var executable, var outputDirectory])
{
    Directory.CreateDirectory(outputDirectory);
    using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    await using var recorder = new ReplayRecorder(executable, outputDirectory, 3,
        ["-re", "-f", "lavfi", "-i", "testsrc2=size=320x180:rate=30", "-f", "lavfi", "-i", "sine=frequency=440",
            "-f", "lavfi", "-i", "sine=frequency=880"],
        ["-map", "0:v:0", "-map", "1:a:0", "-map", "2:a:0", "-c:v", OperatingSystem.IsWindows() ? "mpeg2video" : "libx264", "-c:a", "aac", "-g", "30",
        "-bf", "0", "-force_key_frames", "expr:gte(t,n_forced)"]);
    await recorder.WaitUntilReadyAsync(stop.Token);
    await Task.Delay(4500, stop.Token);
    if (recorder.BufferedDuration is not { TotalSeconds: >= 2.9 and < 3.25 })
        throw new InvalidDataException("Replay buffer did not report a fresh three-second window.");
    for (var i = 0; i < 2; i++)
    {
        var clip = await recorder.SaveAsync(Path.Combine(outputDirectory, $"replay-{i}.mp4"), stop.Token);
        if (Mp4.ReadDuration(clip) is not { TotalSeconds: >= 2.9 and < 3.25 } duration)
            throw new InvalidDataException("Replay duration is outside the rolling-buffer interval.");
        Console.WriteLine($"{clip} · {duration.TotalSeconds:F2}s");
        await Task.Delay(1200, stop.Token);
    }
    return 0;
}

if (args is ["export-smoke", var ffmpeg, var source, var output])
{
    using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(45));
    await ClipExport.CreateAsync(ffmpeg, source, output, 0.25, 1.75, discord: true,
        mixMicrophone: true, mute: false, stop.Token, OperatingSystem.IsWindows() ? "h264_mf" : "libx264");
    if (new FileInfo(output).Length > ClipExport.DiscordBytes ||
        Mp4.ReadDuration(output) is not { TotalSeconds: >= 1.4 and <= 1.65 })
        throw new InvalidDataException("Discord export did not meet the size or trim target.");
    Console.WriteLine($"Discord MP4 validated: {new FileInfo(output).Length} bytes.");
    return 0;
}

if (args is not [var path, var choice] || !File.Exists(path) || Mp4.ReadDuration(path) is null
    || !Enum.TryParse<Provider>(choice, true, out var provider) || !Enum.IsDefined(provider))
{
    Console.Error.WriteLine("Kullanım: RollClip.Bench <herkese açık test.mp4> <litterbox|gofile>");
    return 2;
}

using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
{
    Timeout = TimeSpan.FromMinutes(15),
    MaxResponseContentBufferSize = 128 * 1024
};
var dataDirectory = Directory.CreateTempSubdirectory("rollclip-bench-");
var watch = Stopwatch.StartNew();
try
{
    using var stop = new CancellationTokenSource(TimeSpan.FromMinutes(15));
    var databasePath = Path.Combine(dataDirectory.FullName, "library.json");
    var store = new ClipStore(databasePath);
    var clip = new Clip(Guid.NewGuid(), Path.GetFullPath(path), "Synthetic test", DateTimeOffset.UtcNow,
        new FileInfo(path).Length, Mp4.ReadDuration(path));
    store.Add(clip);
    var queue = new UploadQueue(store, [new FreeUploadProvider(client, provider)], () => 0);
    var completion = new TaskCompletionSource<Clip>(TaskCreationOptions.RunContinuationsAsynchronously);
    queue.Changed += updated =>
    {
        if (updated.State is UploadState.Uploaded or UploadState.Failed)
            completion.TrySetResult(updated);
    };
    queue.Enqueue(clip.Id, provider);
    var worker = queue.RunAsync(stop.Token);
    try { await completion.Task.WaitAsync(stop.Token); }
    finally
    {
        await stop.CancelAsync();
        try { await worker; }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
    }
    var saved = new ClipStore(databasePath).Find(clip.Id);
    if (saved is not { State: UploadState.Uploaded, ShareUrl: { } shareUrl, Timing: { } timing })
        throw new UploadException(saved?.Error ?? "Paylaşım bağlantısı arşivden okunamadı.");
    var result = new UploadResult(new Uri(shareUrl), timing);
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
        archivePersisted = true,
        shareUrl = result.ShareUrl
    }));
    return matches == false ? 1 : 0;
}
catch (Exception error) when (error is HttpRequestException or UploadException or OperationCanceledException)
{
    Console.Error.WriteLine(error.Message);
    return 1;
}
finally
{
    dataDirectory.Delete(recursive: true);
}
