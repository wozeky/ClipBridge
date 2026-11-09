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
            if (new FileInfo(destination).Length == source.Length)
                return false;
            throw new IOException("Yedek klasöründe aynı isimde farklı boyutta bir dosya var.");
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
}
