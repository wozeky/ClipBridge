using System.Diagnostics;

namespace RollClip.Core;

public static class ClipExport
{
    public const long DiscordBytes = 19_000_000;
    private const int AudioRate = 96_000;
    private const int MinimumVideoRate = 550_000;

    internal static int VideoBudget(double seconds, long bytes)
    {
        if (!double.IsFinite(seconds) || seconds < 0.25)
            throw new ArgumentOutOfRangeException(nameof(seconds));
        return (int)Math.Clamp(bytes * 0.95 * 8 / seconds - AudioRate, 0, 8_000_000);
    }

    public static async Task<string> CreateAsync(string ffmpeg, string source, string destination, double start, double end,
        bool discord, bool mixMicrophone, bool mute, CancellationToken cancellationToken, string encoder = "h264_mf")
    {
        if (!double.IsFinite(start) || start < 0 || !double.IsFinite(end) || end - start < 0.25)
            throw new ArgumentException("En az 0,25 saniyelik bir aralık seçin.");
        var seconds = end - start;
        var rate = discord ? VideoBudget(seconds, DiscordBytes) : 8_000_000;
        var temporary = destination + ".partial";
        try
        {
            for (var attempt = 0; attempt < 3 && rate >= MinimumVideoRate; attempt++)
            {
                var height = discord ? rate switch { < 1_100_000 => 480, < 2_800_000 => 720, _ => 1080 } : 1080;
                var width = height * 16 / 9;
                var args = new List<string> { "-hide_banner", "-loglevel", "error", "-nostdin", "-y", "-ss",
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

                var info = new ProcessStartInfo(ffmpeg) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
                foreach (var arg in args)
                    info.ArgumentList.Add(arg);
                using var process = Process.Start(info) ?? throw new IOException("Video dönüştürücü başlatılamadı.");
                if (OperatingSystem.IsWindows())
                    try { process.PriorityClass = ProcessPriorityClass.BelowNormal; }
                    catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException) { }
                var errors = process.StandardError.ReadToEndAsync();
                try { await process.WaitForExitAsync(cancellationToken); }
                catch (OperationCanceledException)
                {
                    if (!process.HasExited)
                        try { process.Kill(entireProcessTree: true); }
                        catch (InvalidOperationException) { }
                    await process.WaitForExitAsync();
                    throw;
                }
                var errorText = await errors;
                if (process.ExitCode != 0 || Mp4.ReadDuration(temporary) is not { } duration ||
                    Math.Abs(duration.TotalSeconds - seconds) > 0.5)
                    throw new IOException("Klip dışa aktarılamadı. " + errorText.Trim());
                cancellationToken.ThrowIfCancellationRequested();
                var size = new FileInfo(temporary).Length;
                if (!discord || size <= DiscordBytes)
                {
                    File.Move(temporary, destination);
                    return destination;
                }
                rate = (int)(rate * (DiscordBytes / (double)size) * 0.9);
            }
            throw new IOException("Bu aralık 19 MB'a yeterli kalitede sığmadı. Daha kısa kesin veya bağlantıyı paylaşın.");
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }
}
