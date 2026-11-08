using System.Net;
using System.Text;
using ClipBridge.Core;

namespace ClipBridge.Tests;

public sealed class UploadTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("clipbridge-tests-").FullName;

    [Theory]
    [InlineData(Provider.Litterbox, "https://litter.catbox.moe/demo.mp4")]
    [InlineData(Provider.Gofile, "https://gofile.io/d/demo")]
    public async Task UploadSendsOriginalBytesAndReturnsValidatedLink(Provider provider, string link)
    {
        var data = Encoding.UTF8.GetBytes("CLIP_PAYLOAD");
        var path = Path.Combine(_directory, "private filename.mp4");
        await File.WriteAllBytesAsync(path, data, CancellationToken.None);
        using var client = new HttpClient(new MockHttp(async request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            var body = await request.Content!.ReadAsStringAsync(CancellationToken.None);
            Assert.Contains("CLIP_PAYLOAD", body);
            Assert.Contains("clipbridge.mp4", body);
            Assert.DoesNotContain("private filename", body);
            if (provider == Provider.Litterbox)
            {
                Assert.Equal("litterbox.catbox.moe", request.RequestUri!.Host);
                Assert.Contains("fileToUpload", body);
                Assert.Contains("24h", body);
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(link + "\n") };
            }
            Assert.Equal("https://upload.gofile.io/uploadfile", request.RequestUri!.AbsoluteUri);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(System.Text.Json.JsonSerializer.Serialize(new
                {
                    status = "ok", data = new { downloadPage = link, guestToken = "sensitive" }
                }))
            };
        }));

        var result = await new FreeUploadProvider(client, provider).UploadAsync(path, 0, null, CancellationToken.None);

        Assert.Equal(link, result.ShareUrl.AbsoluteUri);
        Assert.InRange(result.Timing.BodyWritten, TimeSpan.Zero, result.Timing.LinkReady);
    }

    [Theory]
    [InlineData("http://litter.catbox.moe/demo.mp4")]
    [InlineData("https://litter.catbox.moe.example.com/demo.mp4")]
    [InlineData("https://litter.catbox.moe:444/demo.mp4")]
    public async Task RejectsUntrustedLinks(string link)
    {
        var clip = await CreateLocalClip();
        using var client = new HttpClient(new MockHttp(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(link)
        })));
        await Assert.ThrowsAsync<UploadException>(() =>
            new FreeUploadProvider(client, Provider.Litterbox).UploadAsync(clip.FilePath, 0, null, CancellationToken.None));
    }

    [Fact]
    public async Task PermanentFailurePreservesTheLocalFileAndCanBeRetriedWithAnotherProvider()
    {
        var clip = await CreateLocalClip();
        var database = Path.Combine(_directory, "library.json");
        var store = new ClipStore(database);
        store.Add(clip);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var failed = new TaskCompletionSource<Clip>(TaskCreationOptions.RunContinuationsAsynchronously);
        var succeeded = new TaskCompletionSource<Clip>(TaskCreationOptions.RunContinuationsAsynchronously);
        var shareLink = new Uri("https://gofile.io/d/demo");
        var queue = new UploadQueue(store,
        [
            new StubProvider(Provider.Litterbox, () => throw new UploadException("Test failure")),
            new StubProvider(Provider.Gofile, () => new UploadResult(shareLink, new UploadTiming(default, default)))
        ], () => 0);
        queue.Changed += item =>
        {
            if (item.State == UploadState.Failed) failed.TrySetResult(item);
            if (item.State == UploadState.Uploaded) succeeded.TrySetResult(item);
        };
        queue.Enqueue(clip.Id, Provider.Litterbox);
        var worker = queue.RunAsync(stop.Token);
        await failed.Task.WaitAsync(stop.Token);
        Assert.True(File.Exists(clip.FilePath));
        queue.Enqueue(clip.Id, Provider.Gofile);
        await succeeded.Task.WaitAsync(stop.Token);

        var restored = new ClipStore(database).Find(clip.Id);
        Assert.Equal(shareLink.AbsoluteUri, restored?.ShareUrl);
        Assert.Equal(Provider.Gofile, restored?.Provider);
        Assert.True(File.Exists(clip.FilePath));
        await stop.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => worker);
    }

    [Fact]
    public async Task RestartKeepsWaitingItemsAndRecoversInterruptedUploadsWithoutRepeatingThem()
    {
        var clip = await CreateLocalClip();
        var database = Path.Combine(_directory, "library.json");
        var store = new ClipStore(database);
        store.Add(clip with { State = UploadState.Uploading });
        var queued = clip with { Id = Guid.NewGuid(), State = UploadState.Queued };
        store.Add(queued);
        var recovered = new ClipStore(database);
        Assert.Equal(UploadState.Failed, recovered.Find(clip.Id)?.State);
        Assert.Equal(UploadState.Queued, recovered.Find(queued.Id)?.State);
        Assert.True(File.Exists(clip.FilePath));
    }

    private async Task<Clip> CreateLocalClip()
    {
        var path = Path.Combine(_directory, "test.mp4");
        await File.WriteAllBytesAsync(path, [1, 2, 3]);
        return new Clip(Guid.NewGuid(), path, "test", DateTimeOffset.UtcNow, 3, null);
    }

    private sealed class MockHttp(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => send(request);
    }

    private sealed class StubProvider(Provider id, Func<UploadResult> result) : IUploadProvider
    {
        public Provider Id => id;

        public Task<UploadResult> UploadAsync(string path, int limitKibPerSecond, IProgress<double>? progress,
            CancellationToken cancellationToken) => Task.FromResult(result());
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
