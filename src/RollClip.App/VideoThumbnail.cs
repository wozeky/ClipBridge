using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Media.Imaging;
using RollClip.Core;

namespace RollClip.App;

internal static class VideoThumbnail
{
    private static readonly SemaphoreSlim Decoder = new(1);
    private static readonly string Ffmpeg = Path.Combine(AppContext.BaseDirectory, "ffmpeg", "bin", "ffmpeg.exe");

    public static Task<BitmapSource?> LoadAsync(Clip clip, string cache, CancellationToken token) =>
        Task.Run(() => LoadCoreAsync(clip, cache, filmstrip: false, token), token);

    public static Task<BitmapSource?> LoadFilmstripAsync(Clip clip, string cache, CancellationToken token) =>
        Task.Run(() => LoadCoreAsync(clip, cache, filmstrip: true, token), token);

    private static async Task<BitmapSource?> LoadCoreAsync(Clip clip, string cache, bool filmstrip, CancellationToken token)
    {
        var source = new FileInfo(clip.FilePath);
        if (!source.Exists)
            return null;
        var size = source.Length;
        var modified = source.LastWriteTimeUtc;
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            FormattableString.Invariant($"{source.FullName}|{size}|{modified.Ticks}|4"))));
        var clipPrefix = clip.Id.ToString("N") + "-";
        var prefix = filmstrip ? clipPrefix + "strip-" : clipPrefix;
        var output = Path.Combine(cache, prefix + fingerprint + ".jpg");
        var invalid = output + ".invalid";
        if (ReadBitmap(output) is { } existing)
            return existing;
        if (RecentlyFailed(invalid) || !File.Exists(Ffmpeg))
            return null;

        await Decoder.WaitAsync(token);
        try
        {
            if (ReadBitmap(output) is { } cached)
                return cached;
            if (RecentlyFailed(invalid))
                return null;
            Directory.CreateDirectory(cache);
            var temporary = output + ".partial";
            try
            {
                var seconds = clip.Duration?.TotalSeconds ?? 0;
                var seek = filmstrip ? 0 : Math.Min(3, seconds * 0.15);
                var filter = filmstrip
                    ? FormattableString.Invariant($"fps={8 / Math.Max(0.25, seconds):F6}:start_time=0,scale=112:64:force_original_aspect_ratio=decrease:force_divisible_by=2,pad=112:64:(ow-iw)/2:(oh-ih)/2,tile=8x1")
                    : "scale=480:270:force_original_aspect_ratio=decrease:force_divisible_by=2,pad=480:270:(ow-iw)/2:(oh-ih)/2";
                var start = new ProcessStartInfo(Ffmpeg)
                {
                    UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true
                };
                string[] arguments = ["-hide_banner", "-loglevel", "error", "-nostdin", "-y", "-threads", "1",
                    "-ss", FormattableString.Invariant($"{seek:F3}"), "-i", source.FullName, "-map", "0:v:0",
                    "-vf", filter,
                    "-filter_threads", "1", "-frames:v", "1", "-threads", "1", "-f", "mjpeg", temporary];
                foreach (var argument in arguments)
                    start.ArgumentList.Add(argument);
                using var process = Process.Start(start) ?? throw new IOException("FFmpeg could not start.");
                try { process.PriorityClass = ProcessPriorityClass.BelowNormal; }
                catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException)
                { Trace.WriteLine(error.Message); }
                var stderr = process.StandardError.ReadToEndAsync();
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(TimeSpan.FromSeconds(8));
                try { await process.WaitForExitAsync(timeout.Token); }
                catch (OperationCanceledException)
                {
                    try { process.Kill(entireProcessTree: true); }
                    catch (InvalidOperationException) { }
                    await process.WaitForExitAsync();
                    if (token.IsCancellationRequested)
                        token.ThrowIfCancellationRequested();
                    Trace.WriteLine("Thumbnail decode timed out.");
                    return null;
                }

                var detail = await stderr;
                var image = process.ExitCode == 0 ? ReadBitmap(temporary) : null;
                token.ThrowIfCancellationRequested();
                source.Refresh();
                if (!source.Exists || source.Length != size || source.LastWriteTimeUtc != modified)
                    return null;
                if (image is not null)
                {
                    File.Move(temporary, output, overwrite: true);
                    File.Delete(invalid);
                }
                else
                {
                    Trace.WriteLine(detail);
                    File.WriteAllText(invalid, "");
                }
                foreach (var old in Directory.EnumerateFiles(cache, prefix + "*"))
                    if (old != output && old != invalid && (filmstrip || !Path.GetFileName(old).StartsWith(clipPrefix + "strip-", StringComparison.Ordinal)))
                        File.Delete(old);
                return image;
            }
            finally { File.Delete(temporary); }
        }
        finally { Decoder.Release(); }
    }

    private static bool RecentlyFailed(string marker) =>
        File.Exists(marker) && DateTime.UtcNow - File.GetLastWriteTimeUtc(marker) < TimeSpan.FromMinutes(30);

    public static async Task ClearAsync(string cache, CancellationToken token)
    {
        await Decoder.WaitAsync(token);
        try
        {
            if (!Directory.Exists(cache) || (File.GetAttributes(cache) & FileAttributes.ReparsePoint) != 0)
                return;
            foreach (var file in Directory.EnumerateFiles(cache))
            {
                token.ThrowIfCancellationRequested();
                var name = Path.GetFileName(file);
                if (name.Length >= 37 && Guid.TryParseExact(name.AsSpan(0, 32), "N", out _) && name[32] == '-' &&
                    (name.EndsWith(".jpg", StringComparison.Ordinal) || name.EndsWith(".jpg.invalid", StringComparison.Ordinal) ||
                        name.EndsWith(".jpg.partial", StringComparison.Ordinal)) &&
                    (File.GetAttributes(file) & FileAttributes.ReparsePoint) == 0)
                    File.Delete(file);
            }
        }
        finally { Decoder.Release(); }
    }

    private static BitmapSource? ReadBitmap(string path)
    {
        if (!File.Exists(path))
            return null;
        try
        {
            using var stream = File.OpenRead(path);
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad);
            var image = decoder.Frames[0];
            image.Freeze();
            return image;
        }
        catch (Exception error) when (error is IOException or NotSupportedException or FileFormatException)
        {
            Trace.WriteLine(error.Message);
            return null;
        }
    }
}
