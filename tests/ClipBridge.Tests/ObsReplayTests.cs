using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ClipBridge.Core;

namespace ClipBridge.Tests;

public sealed class ObsReplayTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task WaitsForBothAcceptedRequestAndSavedEvent(bool eventFirst)
    {
        var folder = Path.GetFullPath(Path.GetTempPath());
        var path = Path.Combine(folder, "clip.mp4");
        using var socket = new ObsSocket(folder, path, eventFirst);
        var replay = new ObsReplay(async (password, token) =>
        {
            var session = new ObsSession(socket);
            await session.IdentifyAsync(password, token);
            return session;
        });

        var saved = await replay.SaveAsync("local-secret", folder, CancellationToken.None);

        Assert.Equal(path, saved);
        Assert.Equal(["GetProfileList", "GetRecordDirectory", "SaveReplayBuffer"], socket.Requests);
    }

    [Fact]
    public async Task RejectsUnexpectedOutputPath()
    {
        var folder = Path.Combine(Path.GetTempPath(), "configured");
        using var socket = new ObsSocket(folder, Path.Combine(Path.GetTempPath(), "outside.mp4"));
        var replay = new ObsReplay((_, _) => Task.FromResult(new ObsSession(socket)));
        socket.SkipHello();

        await Assert.ThrowsAsync<ObsException>(() => replay.SaveAsync("local-secret", folder, CancellationToken.None));
    }

    [Fact]
    public async Task StartsReplayWithSelectedDurationAndMemorySetting()
    {
        var folder = Path.Combine(Path.GetTempPath(), "configured");
        using var socket = new ObsSocket(folder, Path.Combine(folder, "clip.mp4"));
        var replay = new ObsReplay((_, _) => Task.FromResult(new ObsSession(socket)));
        socket.SkipHello();

        await replay.StartAsync("local-secret", folder, 180, CancellationToken.None);

        Assert.Equal("180", socket.Parameters["RecRBTime"]);
        Assert.Equal("512", socket.Parameters["RecRBSize"]);
        Assert.Equal("StartReplayBuffer", socket.Requests.Last());
    }

    private sealed class ObsSocket : WebSocket
    {
        private readonly Queue<byte[]> _messages = new();
        private readonly string _directory;
        private readonly string _savePath;
        private readonly bool _eventFirst;

        public List<string> Requests { get; } = [];
        public Dictionary<string, string> Parameters { get; } = [];

        public ObsSocket(string directory, string savePath, bool eventFirst = false)
        {
            _directory = directory;
            _savePath = savePath;
            _eventFirst = eventFirst;
            Enqueue(new { op = 0, d = new { authentication = new { salt = "salt", challenge = "challenge" } } });
        }

        public void SkipHello() => _messages.Clear();

        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage,
            CancellationToken cancellationToken)
        {
            using var json = JsonDocument.Parse(buffer.AsMemory());
            var root = json.RootElement;
            var data = root.GetProperty("d");
            if (root.GetProperty("op").GetInt32() == 1)
            {
                static string Digest(string value) => Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
                Assert.Equal(Digest(Digest("local-secretsalt") + "challenge"), data.GetProperty("authentication").GetString());
                Assert.Equal(64, data.GetProperty("eventSubscriptions").GetInt32());
                Enqueue(new { op = 2, d = new { negotiatedRpcVersion = 1 } });
                return Task.CompletedTask;
            }

            var request = data.GetProperty("requestType").GetString()!;
            var id = data.GetProperty("requestId").GetString();
            Requests.Add(request);
            object? response = request switch
            {
                "GetProfileList" => new { currentProfileName = "ClipBridge" },
                "GetRecordDirectory" => new { recordDirectory = _directory },
                "GetReplayBufferStatus" => new { outputActive = false },
                "GetProfileParameter" => new
                {
                    parameterValue = data.GetProperty("requestData").GetProperty("parameterName").GetString() == "Mode"
                        ? "Advanced" : "true",
                    defaultParameterValue = (string?)null
                },
                _ => null
            };
            if (request == "SetProfileParameter")
            {
                var config = data.GetProperty("requestData");
                Assert.Equal("AdvOut", config.GetProperty("parameterCategory").GetString());
                Parameters.Add(config.GetProperty("parameterName").GetString()!,
                    config.GetProperty("parameterValue").GetString()!);
            }
            var output = new
            {
                op = 5, d = new { eventType = "ReplayBufferSaved", eventData = new { savedReplayPath = _savePath } }
            };
            if (request == "SaveReplayBuffer" && _eventFirst)
                Enqueue(output);
            Enqueue(new
            {
                op = 7, d = new { requestId = id, requestStatus = new { result = true, code = 100 }, responseData = response }
            });
            if (request == "SaveReplayBuffer" && !_eventFirst)
                Enqueue(output);
            return Task.CompletedTask;
        }

        private void Enqueue<T>(T value) => _messages.Enqueue(JsonSerializer.SerializeToUtf8Bytes(value));

        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
        {
            var data = _messages.Dequeue();
            data.CopyTo(buffer.AsSpan());
            return Task.FromResult(new WebSocketReceiveResult(data.Length, WebSocketMessageType.Text, endOfMessage: true));
        }

        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => WebSocketState.Open;
        public override string SubProtocol => "obswebsocket.json";
        public override void Abort() { }
        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription,
            CancellationToken cancellationToken) => Task.CompletedTask;
        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription,
            CancellationToken cancellationToken) => Task.CompletedTask;
        public override void Dispose() { }
    }
}
