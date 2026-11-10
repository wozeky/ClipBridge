using System.Diagnostics;
using System.IO;
using System.Windows;
using RollClip.Core;

namespace RollClip.App;

public partial class MainWindow
{
    private DateTimeOffset _nextQuotaCheck;

    private async void StorageUsage_Click(object sender, RoutedEventArgs args) => await RefreshStorageUsageAsync();

    private async Task RefreshStorageUsageAsync()
    {
        if (_store is not { } store)
            return;
        try
        {
            var stats = await Task.Run(() =>
            {
                var clips = store.Snapshot();
                var local = clips.Select(clip => new FileInfo(clip.FilePath)).Where(file => file.Exists).ToArray();
                var thumbnails = DirectoryBytes(Path.Combine(_root, "thumbnails"), "*", nested: false);
                var replay = DirectoryBytes(Path.Combine(_root, "replay"), "*", nested: true);
                var staging = DirectoryBytes(_clipsPath, "*.partial", nested: false);
                var exports = DirectoryBytes(Path.Combine(_clipsPath, "Paylaşım"), "*.partial", nested: false);
                return (Count: local.Length, Bytes: local.Sum(file => file.Length), thumbnails, Temporary: replay + staging + exports);
            }, _shutdown.Token);
            StorageStatus.Text = T($"{stats.Count} klip · {stats.Bytes / 1_000_000d:F1} MB · Küçük resim {stats.thumbnails / 1_000_000d:F1} MB · Geçici {stats.Temporary / 1_000_000d:F1} MB",
                $"{stats.Count} clips · {stats.Bytes / 1_000_000d:F1} MB · Thumbnails {stats.thumbnails / 1_000_000d:F1} MB · Temporary {stats.Temporary / 1_000_000d:F1} MB");
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { StorageStatus.Text = error.Message; }
    }

    private static long DirectoryBytes(string path, string pattern, bool nested) => Directory.Exists(path)
        ? new DirectoryInfo(path).EnumerateFiles(pattern, new EnumerationOptions
        {
            RecurseSubdirectories = nested, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint
        }).Sum(file => file.Exists ? file.Length : 0)
        : 0;

    private async void ClearThumbnails_Click(object sender, RoutedEventArgs args)
    {
        try
        {
            await Task.Run(() => VideoThumbnail.ClearAsync(Path.Combine(_root, "thumbnails"), _shutdown.Token), _shutdown.Token);
            await RefreshStorageUsageAsync();
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { StorageStatus.Text = error.Message; }
    }

    private async Task EnforceStorageLimitAsync()
    {
        _nextQuotaCheck = DateTimeOffset.UtcNow.AddMinutes(2);
        if (_store is not { } store || !_archiveGate.Wait(0))
            return;
        _galleryMutating = true;
        RefreshButtons();
        try
        {
            var settings = _settings;
            var (removed, bytes) = await Task.Run(async () =>
            {
                var clips = store.Snapshot();
                var files = clips.Where(clip => File.Exists(clip.FilePath)).ToArray();
                var used = files.Sum(clip => new FileInfo(clip.FilePath).Length);
                var removed = 0;
                long bytes = 0;
                foreach (var clip in files.OrderBy(clip => clip.CreatedAt))
                {
                    _shutdown.Token.ThrowIfCancellationRequested();
                    var current = Volatile.Read(ref _settings);
                    if (used <= settings.QuotaBytes || Volatile.Read(ref _armed) || Volatile.Read(ref _captureBusy) || !current.AutoCleanup || current.QuotaBytes != settings.QuotaBytes)
                        break;
                    if (clip.Favorite || clip.BackupFolder is not { } archive || clip.BackupCopiedAt is null ||
                        clip.PendingArchiveFolder is not null || (current.KeepUnshared && clip.State != UploadState.Uploaded) ||
                        clip.State is UploadState.Queued or UploadState.Uploading || _queue?.IsActive(clip.Id) == true ||
                        IsInside(_clipsPath, archive) || IsInside(archive, _clipsPath))
                        continue;
                    try
                    {
                        var file = new FileInfo(clip.FilePath);
                        var originalLength = file.Length;
                        var originalTime = file.LastWriteTimeUtc;
                        if (!await ClipArchive.VerifyAsync(archive, clip, _shutdown.Token))
                            continue;
                        var deleted = store.DeleteLocal(clip.Id, _clipsPath, out var deferred, item =>
                        {
                            file.Refresh();
                            var latest = Volatile.Read(ref _settings);
                            return latest.AutoCleanup && latest.QuotaBytes == settings.QuotaBytes &&
                                !item.Favorite && (!latest.KeepUnshared || item.State == UploadState.Uploaded) &&
                                item.BackupFolder == archive && item.BackupCopiedAt == clip.BackupCopiedAt &&
                                item.PendingArchiveFolder is null && file.Exists && file.Length == originalLength &&
                                file.LastWriteTimeUtc == originalTime && !Volatile.Read(ref _armed) && !Volatile.Read(ref _captureBusy);
                        }, keepHistory: true);
                        if (deleted)
                        {
                            removed++;
                            if (deferred is null)
                            {
                                used -= originalLength;
                                bytes += originalLength;
                            }
                            else
                                Trace.WriteLine("An approved cleanup could not remove the staged file: " + deferred);
                        }
                    }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                    { Trace.WriteLine(error.Message); }
                }
                return (removed, bytes);
            }, _shutdown.Token);
            if (removed > 0)
            {
                RefreshLibrary();
                StorageStatus.Text = T($"{removed} eski klip kaldırıldı · {bytes / 1_000_000d:F1} MB boşaltıldı.",
                    $"{removed} old clips removed · {bytes / 1_000_000d:F1} MB freed.");
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { StorageStatus.Text = error.Message; }
        finally
        {
            _galleryMutating = false;
            _archiveGate.Release();
            RefreshButtons();
        }
    }
}
