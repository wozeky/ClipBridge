using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RollClip.Core;

public static partial class DiscordWebhook
{
    private const long MaxBytes = 20 * 1024 * 1024;

    [GeneratedRegex(@"\Ahttps://discord\.com/api/webhooks/[0-9]{17,20}/[A-Za-z0-9_-]{20,200}/?\z",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AddressPattern();

    public static bool TryReadAddress(string input, out Uri? endpoint)
    {
        endpoint = null;
        var address = input.Trim();
        return AddressPattern().IsMatch(address) && Uri.TryCreate(address.TrimEnd('/'), UriKind.Absolute, out endpoint);
    }

    public static async Task<Uri> SendAsync(HttpClient client, Uri endpoint, Stream video, CancellationToken cancellationToken)
    {
        if (!TryReadAddress(endpoint.OriginalString, out var address))
            throw new IOException("Invalid Discord webhook.");
        if (video.Length is <= 0 or > MaxBytes)
            throw new IOException("The Discord attachment must be between 1 byte and 20 MiB.");

        using var metadata = await client.GetAsync(address, cancellationToken);
        EnsureSuccess(metadata);
        using var metadataBody = await metadata.Content.ReadAsStreamAsync(cancellationToken);
        using var webhook = await JsonDocument.ParseAsync(metadataBody, cancellationToken: cancellationToken);
        if (webhook.RootElement.ValueKind != JsonValueKind.Object ||
            !TryReadId(webhook.RootElement, "guild_id", out var guild))
            throw new IOException("Discord did not confirm the server for this webhook.");

        using var form = new MultipartFormDataContent();
        var file = new StreamContent(video);
        file.Headers.ContentType = new MediaTypeHeaderValue("video/mp4");
        form.Add(file, "files[0]", "rollclip.mp4");
        form.Add(new StringContent("""{"allowed_mentions":{"parse":[]}}""", Encoding.UTF8, "application/json"), "payload_json");
        using var response = await client.PostAsync(new UriBuilder(address!) { Query = "wait=true" }.Uri, form, cancellationToken);
        EnsureSuccess(response);
        using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var result = await JsonDocument.ParseAsync(body, cancellationToken: cancellationToken);
        var root = result.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !TryReadId(root, "channel_id", out var channel) || !TryReadId(root, "id", out var message))
            throw new IOException("Discord did not confirm a message link. Check the channel before retrying.");
        return new Uri($"https://discord.com/channels/{guild}/{channel}/{message}");
    }

    private static void EnsureSuccess(HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode)
            throw new IOException(response.StatusCode switch
            {
                HttpStatusCode.NotFound or HttpStatusCode.Unauthorized => "Discord webhook was deleted or is invalid.",
                HttpStatusCode.Forbidden => "Discord denied access to this channel.",
                HttpStatusCode.RequestEntityTooLarge => "Discord rejected the attachment size.",
                HttpStatusCode.TooManyRequests => "Discord rate limit reached. Try again shortly.",
                _ => $"Discord rejected the upload (HTTP {(int)response.StatusCode})."
            });
    }

    private static bool TryReadId(JsonElement root, string name, out ulong id)
    {
        id = 0;
        return root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            && ulong.TryParse(value.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out id) && id != 0;
    }
}
