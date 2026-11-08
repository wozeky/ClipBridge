using System.Net.WebSockets;
using System.Text.Json;
using ClipBridge.Core;

internal static class ObsSmoke
{
    public static async Task<int> RunAsync(string outputDirectory)
    {
        var password = Environment.GetEnvironmentVariable("CLIPBRIDGE_OBS_SMOKE_PASSWORD");
        if (string.IsNullOrEmpty(password))
        {
            Console.Error.WriteLine("CLIPBRIDGE_OBS_SMOKE_PASSWORD is missing.");
            return 2;
        }

        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(75));
        var replay = new ObsReplay();
        try
        {
            await replay.StartAsync(password, outputDirectory, 15, stop.Token);
            var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int index = 0; index < 2; index++)
            {
                await Task.Delay(TimeSpan.FromSeconds(index == 0 ? 6 : 2), stop.Token);
                var path = await replay.SaveAsync(password, outputDirectory, stop.Token);
                var info = new FileInfo(path);
                var duration = Mp4.ReadDuration(path);
                if (!files.Add(path) || info.Length == 0 || duration is null || duration.Value.TotalSeconds < 1)
                    throw new InvalidDataException("OBS did not save a distinct, nonempty replay MP4.");
                Console.WriteLine($"{info.Name}: {duration.Value.TotalSeconds:F2} s, {info.Length} bytes");
            }
            return 0;
        }
        catch (Exception error) when (error is ObsException or WebSocketException or OperationCanceledException
            or JsonException or IOException)
        {
            Console.Error.WriteLine(error.Message);
            return 1;
        }
    }
}
