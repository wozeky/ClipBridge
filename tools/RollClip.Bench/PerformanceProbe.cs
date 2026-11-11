using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using RollClip.Core;

internal static class PerformanceProbe
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private static object Measure(Action work, int samples)
    {
        work(); // Warm up JIT and caches; deliberately excluded from measurements.
        var times = new List<double>();
        var allocated = GC.GetTotalAllocatedBytes(true);
        for (var i = 0; i < samples; i++)
        {
            var start = Stopwatch.GetTimestamp();
            work();
            times.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
        }
        return new { samples, minMs = times.Min(), medianMs = times.Order().ElementAt(times.Count / 2),
            p95Ms = times.Order().ElementAt((int)Math.Ceiling(times.Count * 0.95) - 1),
            allocatedBytesPerOperation = (GC.GetTotalAllocatedBytes(true) - allocated) / samples };
    }

    public static async Task<int> RunAsync(string ffmpeg, string directory)
    {
        Directory.CreateDirectory(directory);
        var archive = new List<object>();
        foreach (var count in new[] { 100, 1000, 5000 })
        {
            var rows = Enumerable.Range(0, count).Select(i => new Clip(Guid.NewGuid(),
                Path.Combine(directory, $"fixture-{i}.mp4"), $"Synthetic clip {i}", DateTimeOffset.UtcNow.AddMinutes(-i),
                10_000_000, TimeSpan.FromSeconds(60), Game: "Synthetic", Bookmarks: [1, 10, 20])).ToArray();
            var path = Path.Combine(directory, $"archive-{count}.json");
            AtomicJson.Write(path, rows); // Fixture setup is not part of per-operation timing.
            var store = new ClipStore(path);
            var id = rows[0].Id;
            archive.Add(new { count, jsonBytes = new FileInfo(path).Length,
                load = Measure(() => GC.KeepAlive(new ClipStore(path)), 10),
                snapshot = Measure(() => GC.KeepAlive(store.Snapshot()), 40),
                toggleFavorite = Measure(() => store.Change(id, clip => clip with { Favorite = !clip.Favorite }), 20) });
        }

        using var stop = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var encoder = OperatingSystem.IsWindows() ? "h264_mf" : "libx264";
        var encoding = new List<string> { "-map", "0:v:0", "-map", "1:a:0", "-map", "2:a:0", "-c:v", encoder,
            "-b:v", "4M", "-g", "30", "-bf", "0", "-force_key_frames", "expr:gte(t,n_forced)", "-c:a", "aac", "-b:a", "160k" };
        if (OperatingSystem.IsWindows()) encoding.AddRange(["-hw_encoding", "0"]);
        var readyAt = Stopwatch.GetTimestamp();
        object recording;
        var saves = new List<object>();
        var exports = new List<object>();
        await using (var recorder = new ReplayRecorder(ffmpeg, directory, 6,
            ["-re", "-f", "lavfi", "-i", "testsrc2=size=1280x720:rate=30",
             "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000",
             "-f", "lavfi", "-i", "sine=frequency=880:sample_rate=48000"], encoding))
        {
            await recorder.WaitUntilReadyAsync(stop.Token);
            var startupMs = Stopwatch.GetElapsedTime(readyAt).TotalMilliseconds;
            var first = recorder.SampleProcessUsage();
            var started = Stopwatch.GetTimestamp();
            var peak = first.WorkingSetBytes;
            var readings = 0;
            for (var i = 0; i < 40; i++)
            {
                await Task.Delay(250, stop.Token);
                peak = Math.Max(peak, recorder.SampleProcessUsage().WorkingSetBytes);
                readings++;
            }
            var last = recorder.SampleProcessUsage();
            var wall = Stopwatch.GetElapsedTime(started).TotalSeconds;
            recording = new { startupMs, sampleSeconds = wall, readings,
                cpuOneCorePercent = (last.CpuTime - first.CpuTime).TotalSeconds / wall * 100,
                cpuWholeMachinePercent = (last.CpuTime - first.CpuTime).TotalSeconds / wall * 100 / Environment.ProcessorCount,
                peakSampledWorkingSetMiB = peak / 1048576d, bufferedSeconds = recorder.BufferedDuration?.TotalSeconds };
            for (var i = 0; i < 5; i++)
            {
                var destination = Path.Combine(directory, $"perf-save-{i}.mp4");
                var start = Stopwatch.GetTimestamp();
                await recorder.SaveAsync(destination, stop.Token);
                var elapsed = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                var duration = Mp4.ReadDuration(destination)?.TotalSeconds;
                if (duration is null or < 5 or > 7) throw new InvalidDataException("Invalid saved duration.");
                saves.Add(new { elapsedMs = elapsed, seconds = duration, bytes = new FileInfo(destination).Length });
                await Task.Delay(1000, stop.Token);
            }
        }
        // Export after capture stops: these are isolated operation timings, not concurrent game load.
        for (var i = 0; i < 3; i++)
        {
            var output = Path.Combine(directory, $"perf-export-{i}.mp4");
            var start = Stopwatch.GetTimestamp();
            await ClipExport.CreateAsync(ffmpeg, Path.Combine(directory, "perf-save-0.mp4"), output,
                0.25, 5.25, discord: true, mixMicrophone: true, mute: false, stop.Token, encoder: encoder);
            var elapsed = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            var duration = Mp4.ReadDuration(output)?.TotalSeconds;
            if (duration is null or < 4.5 or > 5.5 || new FileInfo(output).Length > ClipExport.DiscordBytes)
                throw new InvalidDataException("Invalid exported duration/size.");
            exports.Add(new { elapsedMs = elapsed, seconds = duration, bytes = new FileInfo(output).Length });
        }
        var report = new { environment = new { os = RuntimeInformation.OSDescription,
            framework = RuntimeInformation.FrameworkDescription, logicalProcessors = Environment.ProcessorCount,
            commit = Environment.GetEnvironmentVariable("GITHUB_SHA"), cpu = Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") },
            scope = "Synthetic 720p30 + two generated audio tracks; software H.264. No physical microphone, Discord, real game, GPU or WPF hover measurements. Archive timings include durable disk flushes. Shared runner, no baseline comparison.",
            archive, recording, saves, exports };
        var result = JsonSerializer.Serialize(report, Json);
        await File.WriteAllTextAsync(Path.Combine(directory, "performance.json"), result);
        Console.WriteLine(result);
        return 0;
    }
}
