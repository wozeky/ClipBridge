using System.Globalization;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ClipBridge.Core;

public sealed class ObsException(string message) : Exception(message);

public sealed class ObsReplay
{
    private const string ProfileName = "ClipBridge";
    private readonly Func<string, CancellationToken, Task<ObsSession>> _connect;

    public ObsReplay() : this(ObsSession.ConnectAsync) { }

    internal ObsReplay(Func<string, CancellationToken, Task<ObsSession>> connect) => _connect = connect;

    public async Task StartAsync(string password, string directory, int seconds, CancellationToken cancellationToken)
    {
        if (!ReplayDurations.Options.Contains(seconds))
            throw new ArgumentOutOfRangeException(nameof(seconds));
        using var session = await _connect(password, cancellationToken);
        await VerifyProfileAsync(session, cancellationToken);
        var section = await GetParameterAsync(session, "Output", "Mode", cancellationToken) == "Advanced"
            ? "AdvOut" : "SimpleOutput";
        var duration = seconds.ToString(CultureInfo.InvariantCulture);
        var buffer = await session.RequestAsync("GetReplayBufferStatus", null, cancellationToken);
        var active = buffer.GetProperty("outputActive").GetBoolean();
        if (active)
        {
            var recorded = await session.RequestAsync("GetRecordDirectory", null, cancellationToken);
            var existing = recorded.GetProperty("recordDirectory").GetString();
            var existingDuration = await GetParameterAsync(session, section, "RecRBTime", cancellationToken);
            if (!SamePath(existing, directory) || existingDuration != duration)
                throw new ObsException("Süreyi ve klasörü değiştirmek için OBS'deki Replay Buffer'ı durdurup tekrar başlatın.");
            return;
        }

        if (!bool.TryParse(await GetParameterAsync(session, section, "RecRB", cancellationToken), out var enabled)
            || !enabled)
            throw new ObsException("OBS'nin Çıkış ayarlarında Replay Buffer'ı etkinleştirin.");

        await session.RequestAsync("SetRecordDirectory", new { recordDirectory = directory }, cancellationToken);
        await SetParameterAsync(session, section, "RecRBTime", duration, cancellationToken);
        await SetParameterAsync(session, section, "RecRBSize", "512", cancellationToken);
        await session.RequestAsync("StartReplayBuffer", null, cancellationToken);
    }

    public async Task<string> SaveAsync(string password, string directory, CancellationToken cancellationToken)
    {
        using var session = await _connect(password, cancellationToken);
        await VerifyProfileAsync(session, cancellationToken);
        var output = await session.RequestAsync("GetRecordDirectory", null, cancellationToken);
        if (!SamePath(output.GetProperty("recordDirectory").GetString(), directory))
            throw new ObsException("OBS kayıt klasörü değişmiş. Replay Buffer ayarlarını yeniden uygulayın.");

        var result = await session.RequestReplaySaveAsync(cancellationToken);
        if (!Path.IsPathFullyQualified(result) || !SamePath(Path.GetDirectoryName(result), directory)
            || !string.Equals(Path.GetExtension(result), ".mp4", StringComparison.OrdinalIgnoreCase))
            throw new ObsException("OBS, ClipBridge klasöründe bir MP4 oluşturmadı. OBS'de MP4 kayıt biçimini seçin.");
        return result;
    }

    private static async Task VerifyProfileAsync(ObsSession session, CancellationToken cancellationToken)
    {
        var profile = await session.RequestAsync("GetProfileList", null, cancellationToken);
        if (profile.GetProperty("currentProfileName").GetString() != ProfileName)
            throw new ObsException("OBS'de ClipBridge adlı ayrı bir profil oluşturup seçin.");
    }

    private static async Task<string?> GetParameterAsync(ObsSession session, string section, string name,
        CancellationToken cancellationToken)
    {
        var response = await session.RequestAsync("GetProfileParameter",
            new { parameterCategory = section, parameterName = name }, cancellationToken);
        return response.GetProperty("parameterValue").GetString() ??
            response.GetProperty("defaultParameterValue").GetString();
    }

    private static Task SetParameterAsync(ObsSession session, string section, string name, string value,
        CancellationToken cancellationToken) =>
        session.RequestAsync("SetProfileParameter", new
        {
            parameterCategory = section, parameterName = name, parameterValue = value
        }, cancellationToken);

    private static bool SamePath(string? left, string? right) =>
        left is not null && right is not null &&
        string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}

internal sealed class ObsSession(WebSocket socket) : IDisposable
{
    private const int OutputEvents = 1 << 6;

    public static async Task<ObsSession> ConnectAsync(string password, CancellationToken cancellationToken)
    {
        var client = new ClientWebSocket();
        client.Options.AddSubProtocol("obswebsocket.json");
        client.Options.Proxy = null;
        try
        {
            await client.ConnectAsync(new Uri("ws://127.0.0.1:4455"), cancellationToken);
            var session = new ObsSession(client);
            await session.IdentifyAsync(password, cancellationToken);
            return session;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    internal async Task IdentifyAsync(string password, CancellationToken cancellationToken)
    {
        var hello = await ReceiveAsync(cancellationToken);
        if (hello.GetProperty("op").GetInt32() != 0)
            throw new ObsException("OBS WebSocket 5 bağlantısı kurulamadı.");
        var data = hello.GetProperty("d");
        if (!data.TryGetProperty("authentication", out var auth))
            throw new ObsException("OBS WebSocket ayarlarında parola doğrulamasını etkinleştirin.");

        var secret = Hash(password + auth.GetProperty("salt").GetString());
        var authentication = Hash(secret + auth.GetProperty("challenge").GetString());
        await SendAsync(new { op = 1, d = new { rpcVersion = 1, authentication, eventSubscriptions = OutputEvents } },
            cancellationToken);
        var identified = await ReceiveAsync(cancellationToken);
        if (identified.GetProperty("op").GetInt32() != 2)
            throw new ObsException("OBS WebSocket kimlik doğrulaması başarısız.");
    }

    public async Task<JsonElement> RequestAsync(string request, object? requestData, CancellationToken cancellationToken)
    {
        var id = await SendRequestAsync(request, requestData, cancellationToken);
        while (true)
        {
            var message = await ReceiveAsync(cancellationToken);
            if (TryReadResponse(message, id, request, out var response))
                return response;
        }
    }

    public async Task<string> RequestReplaySaveAsync(CancellationToken cancellationToken)
    {
        const string request = "SaveReplayBuffer";
        var id = await SendRequestAsync(request, null, cancellationToken);
        string? path = null;
        bool accepted = false;
        while (!accepted || path is null)
        {
            var message = await ReceiveAsync(cancellationToken);
            if (TryReadResponse(message, id, request, out _))
                accepted = true;
            if (message.GetProperty("op").GetInt32() == 5)
            {
                var data = message.GetProperty("d");
                if (data.GetProperty("eventType").GetString() == "ReplayBufferSaved")
                    path = data.GetProperty("eventData").GetProperty("savedReplayPath").GetString();
            }
        }
        return path;
    }

    private async Task<string> SendRequestAsync(string request, object? requestData, CancellationToken cancellationToken)
    {
        var id = Guid.NewGuid().ToString("N");
        var data = new Dictionary<string, object> { ["requestType"] = request, ["requestId"] = id };
        if (requestData is not null)
            data["requestData"] = requestData;
        await SendAsync(new { op = 6, d = data }, cancellationToken);
        return id;
    }

    private static bool TryReadResponse(JsonElement message, string id, string request, out JsonElement response)
    {
        response = default;
        if (message.GetProperty("op").GetInt32() != 7)
            return false;
        var data = message.GetProperty("d");
        if (data.GetProperty("requestId").GetString() != id)
            return false;
        var status = data.GetProperty("requestStatus");
        if (!status.GetProperty("result").GetBoolean())
            throw new ObsException($"OBS {request} isteğini reddetti ({status.GetProperty("code").GetInt32()}).");
        data.TryGetProperty("responseData", out response);
        return true;
    }

    private static string Hash(string input) => Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(input)));

    private Task SendAsync<T>(T message, CancellationToken cancellationToken) =>
        socket.SendAsync(new ArraySegment<byte>(JsonSerializer.SerializeToUtf8Bytes(message)),
            WebSocketMessageType.Text, endOfMessage: true, cancellationToken);

    private async Task<JsonElement> ReceiveAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];
        using var message = new MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
                throw new ObsException("OBS bağlantıyı kapattı. OBS'yi ve WebSocket parolasını kontrol edin.");
            if (result.MessageType != WebSocketMessageType.Text || message.Length + result.Count > 65536)
                throw new ObsException("OBS WebSocket yanıtı geçersiz.");
            message.Write(buffer, 0, result.Count);
        } while (!result.EndOfMessage);

        using var json = JsonDocument.Parse(message.GetBuffer().AsMemory(0, (int)message.Length));
        return json.RootElement.Clone();
    }

    public void Dispose() => socket.Dispose();
}
