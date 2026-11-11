using System.Net;
using RollClip.Core;
using Xunit;

namespace RollClip.Tests;

public class DiscordWebhookTests
{
    private const string Address = "https://discord.com/api/webhooks/123456789012345678/fake_test_webhook_token";

    [Theory]
    [InlineData("https://discord.com.attacker.example/api/webhooks/123456789012345678/fake_test_webhook_token")]
    [InlineData("http://discord.com/api/webhooks/123456789012345678/fake_test_webhook_token")]
    [InlineData(Address + "?thread_id=123456789012345678")]
    public void RejectsNonCanonicalWebhookAddresses(string address)
    {
        Assert.False(DiscordWebhook.TryReadAddress(address, out _));
    }

    [Fact]
    public async Task PostsMp4AndUsesTheConfirmedMessageId()
    {
        using var handler = new WebhookHandler();
        using var client = new HttpClient(handler);
        using var video = new MemoryStream([0, 1, 2]);
        Assert.True(DiscordWebhook.TryReadAddress(Address, out var endpoint));
        var link = await DiscordWebhook.SendAsync(client, endpoint!, video, CancellationToken.None);
        Assert.Equal("https://discord.com/channels/123/456/789", link.AbsoluteUri);
        Assert.Equal(2, handler.Calls);
    }

    private sealed class WebhookHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Calls++;
            if (request.Method == HttpMethod.Get)
            {
                Assert.Equal(Address, request.RequestUri!.AbsoluteUri);
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"guild_id":"123"}""") };
            }
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal(Address + "?wait=true", request.RequestUri!.AbsoluteUri);
            var content = Assert.IsType<MultipartFormDataContent>(request.Content);
            var file = Assert.Single(content, part => part.Headers.ContentDisposition?.Name?.Trim('"') == "files[0]");
            Assert.Equal("rollclip.mp4", file.Headers.ContentDisposition!.FileName!.Trim('"'));
            Assert.Equal("video/mp4", file.Headers.ContentType!.MediaType);
            Assert.Equal(new byte[] { 0, 1, 2 }, await file.ReadAsByteArrayAsync(token));
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"channel_id":"456","id":"789"}""") };
        }
    }
}
