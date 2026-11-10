using System.Diagnostics;

namespace RollClip.Core;

public static class ClipExport
{
    public const long DiscordBytes = 19_000_000;
    public static IReadOnlyList<(string Label, long Bytes)> SizePresets { get; } =
        Array.AsReadOnly(new[] { ("19 MB · Discord", DiscordBytes), ("10 MB", 10_000_000L),
            ("50 MB", 50_000_000L), ("100 MB", 100_000_000L), ("500 MB", 500_000_000L) });

    private const int AudioRate = 96_000;
    private const int MinimumVideoRate = 550_000;

    internal static int VideoBudget(double seconds, long bytes)
    {
        if (!double.IsFinite(seconds) || seconds < 0.25)
            throw new ArgumentOutOfRangeException(nameof(seconds));
        return (int)Math.Clamp(bytes * 0.95 * 8 / seconds - AudioRate, 0, 8_000_000);
    }

    public static async Task<string> CreateAsync(string ffmpeg, string source, string destination, double start, double end,
        bool discord, bool mixMicrophone, bool mute, CancellationToken cancellationToken, string encoder = "h264_mf", long? maxBytes = null, IProgress<double>? progress = null,
        bool copyWholeClip = false)
    {
        if (!double.IsFinite(start) || start < 0 || !double.IsFinite(end) || end - start < 0.25)
            throw new ArgumentException("En az 0,25 saniyelik bir aralık seçin.");
        var seconds = end - start;
        var limit = maxBytes ?? DiscordBytes;
        if (limit <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxBytes));
        var rate = discord ? VideoBudget(seconds, limit) : 8_000_000;
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".partial";
        try
        {
            if (copyWholeClip && !discord && !mixMicrophone && start == 0 &&
                Mp4.ReadDuration(source) is { } sourceDuration && Math.Abs(sourceDuration.TotalSeconds - end) <= 0.005)
            {
                var copyArgs = new List<string> { "-i", source, "-map", "0:v:0" };
                if (!mute)
                    copyArgs.AddRange(["-map", "0:a:0?"]);
                copyArgs.AddRange(["-c", "copy", "-movflags", "+faststart", "-f", "mp4", temporary]);
                await RunAsync(ffmpeg, copyArgs, seconds, progress, cancellationToken);
                VerifyDuration(temporary, seconds);
                cancellationToken.ThrowIfCancellationRequested();
                File.Move(temporary, destination);
                progress?.Report(1);
                return destination;
            }

            for (var attempt = 0; attempt < 3 && rate >= MinimumVideoRate; attempt++)
            {
                var height = discord ? rate switch { < 1_100_000 => 480, < 2_800_000 => 720, _ => 1080 } : 1080;
                var width = height * 16 / 9;
                var args = new List<string> { "-ss",
                    FormattableString.Invariant($"{start:F3}"), "-i", source, "-t", FormattableString.Invariant($"{seconds:F3}"),
                    "-map", "0:v:0", "-vf", $"scale=w='min(iw,{width})':h='min(ih,{height})':force_original_aspect_ratio=decrease:force_divisible_by=2,format=yuv420p",
                    "-c:v", encoder, "-b:v", rate.ToString(System.Globalization.CultureInfo.InvariantCulture), "-threads", "2" };
                if (encoder == "h264_mf")
                    args.AddRange(["-hw_encoding", "0"]);
                if (discord)
                    args.AddRange(["-r", "30"]);
                if (mute)
                    args.Add("-an");
                else
                {
                    if (mixMicrophone)
                        args.AddRange(["-filter_complex", "[0:a:0][0:a:1]amix=inputs=2:duration=first[a]", "-map", "[a]"]);
                    else
                        args.AddRange(["-map", "0:a:0?"]);
                    args.AddRange(["-c:a", "aac", "-b:a", "96k"]);
                }
                args.AddRange(["-movflags", "+faststart", "-f", "mp4", temporary]);

                await RunAsync(ffmpeg, args, seconds, progress, cancellationToken);
                VerifyDuration(temporary, seconds);
                cancellationToken.ThrowIfCancellationRequested();
                var size = new FileInfo(temporary).Length;
                if (!discord || size <= limit)
                {
                    File.Move(temporary, destination);
                    progress?.Report(1);
                    return destination;
                }
                rate = (int)(rate * (limit / (double)size) * 0.9);
            }
            throw new IOException($"Bu aralık {limit / 1_000_000} MB sınırına sığmadı. Daha kısa kesin veya bağlantıyı paylaşın.");
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }
    private static void VerifyDuration(string file, double seconds)
    {
        if (Mp4.ReadDuration(file) is not { } duration || Math.Abs(duration.TotalSeconds - seconds) > 0.5)
            throw new IOException("Dışa aktarılan MP4 eksik veya okunamıyor.");
    }

    private static async Task RunAsync(string ffmpeg, IEnumerable<string> arguments, double seconds,
        IProgress<double>? progress, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var info = new ProcessStartInfo(ffmpeg)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true
        };
        foreach (var argument in new[] { "-hide_banner", "-loglevel", "error", "-nostdin", "-y", "-progress", "pipe:1" }.Concat(arguments))
            info.ArgumentList.Add(argument);
        using var process = Process.Start(info) ?? throw new IOException("Video dönüştürücü başlatılamadı.");
        if (OperatingSystem.IsWindows())
            try { process.PriorityClass = ProcessPriorityClass.BelowNormal; }
            catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException)
            { Trace.WriteLine(error.Message); }
        var errors = process.StandardError.ReadToEndAsync();
        var report = ReadProgressAsync(process, seconds, progress);
        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
            await process.WaitForExitAsync();
            await Task.WhenAll(report, errors);
            throw;
        }

        await report;
        var errorText = await errors;
        if (process.ExitCode != 0)
            throw new IOException("Klip dışa aktarılamadı. " + errorText.Trim());
    }

    private static async Task ReadProgressAsync(Process process, double seconds, IProgress<double>? progress)
    {
        const string prefix = "out_time_us=";
        while (await process.StandardOutput.ReadLineAsync() is { } line)
            if (line.StartsWith(prefix, StringComparison.Ordinal) &&
                long.TryParse(line.AsSpan(prefix.Length), System.Globalization.CultureInfo.InvariantCulture, out var elapsed))
                progress?.Report(Math.Clamp(elapsed / 1_000_000d / seconds, 0, 0.99));
    }
}
