using System.Globalization;

namespace RollClip.Core;

public static class ClipArchive
{
    public static async Task<bool> CopyAsync(string archiveFolder, Clip clip, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Directory.Exists(archiveFolder))
            throw new DirectoryNotFoundException("Yedek klasörüne erişilemiyor.");

        var date = clip.CreatedAt.UtcDateTime;
        var directory = Path.Combine(archiveFolder, date.ToString("yyyy", CultureInfo.InvariantCulture),
            date.ToString("MM", CultureInfo.InvariantCulture));
        var label = string.Concat((clip.Game ?? clip.Name).Take(48)
            .Select(c => char.IsLetterOrDigit(c) || c is ' ' or '-' or '_' ? c : '_')).Trim();
        var destination = Path.Combine(directory, FormattableString.Invariant($"{date:yyyy-MM-dd_HH-mm-ss}Z_{label}_{clip.Id:N}.mp4"));
        if (string.Equals(Path.GetFullPath(clip.FilePath), Path.GetFullPath(destination),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            return false;

        await using var source = new FileStream(clip.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (source.Length == 0)
            throw new IOException("Boş klip yedeklenemiyor.");
        if (File.Exists(destination))
        {
            if (await MatchesAsync(source, destination, cancellationToken))
                return false;
            throw new IOException("Yedek klasöründe aynı isimde farklı bir dosya var. Mevcut dosya korundu.");
        }

        Directory.CreateDirectory(directory);
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".partial";
        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                65536, FileOptions.Asynchronous))
            {
                await source.CopyToAsync(output, cancellationToken);
                await output.FlushAsync(cancellationToken);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, destination);
            return true;
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    private static async Task<bool> MatchesAsync(FileStream source, string destination, CancellationToken token)
    {
        await using var archived = new FileStream(destination, FileMode.Open, FileAccess.Read, FileShare.Read,
            65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (archived.Length != source.Length)
            return false;

        var sourceBuffer = new byte[65536];
        var archiveBuffer = new byte[65536];
        for (var remaining = source.Length; remaining > 0;)
        {
            var count = (int)Math.Min(sourceBuffer.Length, remaining);
            await source.ReadExactlyAsync(sourceBuffer.AsMemory(0, count), token);
            await archived.ReadExactlyAsync(archiveBuffer.AsMemory(0, count), token);
            if (!sourceBuffer.AsSpan(0, count).SequenceEqual(archiveBuffer.AsSpan(0, count)))
                return false;
            remaining -= count;
        }
        return true;
    }
}
