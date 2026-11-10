using System.IO;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using RollClip.Core;

namespace RollClip.App;

public partial class MainWindow
{
    private readonly DispatcherTimer _backgroundTimer = new() { Interval = TimeSpan.FromSeconds(4) };
    private bool _backgroundRunning;
    private readonly Queue<int> _pendingReplays = new(3);
    private bool _autoGamePaused;
    private bool _editing;
    private string? _pendingImportFolder;
    private DateTimeOffset? _pendingImportSince;
    private DateTimeOffset _nextAutoCapture;
    private bool _displayedExpired;
    private readonly Dictionary<string, DateTimeOffset> _watchRetryAfter = new(StringComparer.OrdinalIgnoreCase);

    private void UpdateImportFolderLabel()
    {
        _pendingImportFolder = _settings.ImportFolder;
        _pendingImportSince = _settings.ImportSince;
        ImportFolderLabel.Text = _pendingImportFolder ?? T("Klasör izleme kapalı", "Folder watching off");
        ImportFolderLabel.ToolTip = _pendingImportFolder;
    }

    private void WatchFolder_Click(object sender, RoutedEventArgs args)
    {
        var dialog = new OpenFolderDialog { Title = "Kayıt uygulamanızın klip klasörünü seçin" };
        if (dialog.ShowDialog(this) != true)
            return;
        if (IsInside(dialog.FolderName, _clipsPath) || IsInside(_clipsPath, dialog.FolderName))
        {
            StatusMessage.Text = "RollClip’in arşivini içermeyen harici bir klip klasörü seçin.";
            return;
        }
        if (!string.Equals(dialog.FolderName, _settings.ImportFolder, StringComparison.OrdinalIgnoreCase))
        {
            _pendingImportFolder = dialog.FolderName;
            _pendingImportSince = DateTimeOffset.UtcNow;
        }
        if (!SaveSettings())
            UpdateImportFolderLabel();
    }

    private void StopWatching_Click(object sender, RoutedEventArgs args)
    {
        _pendingImportFolder = null;
        _pendingImportSince = null;
        if (!SaveSettings())
            UpdateImportFolderLabel();
    }

    private static bool IsInside(string path, string folder)
    {
        var relative = Path.GetRelativePath(folder, path);
        return !Path.IsPathRooted(relative) && relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    private async void Import_Click(object sender, RoutedEventArgs args)
    {
        if (_busy || _store is null)
            return;
        var dialog = new OpenFileDialog { Filter = "MP4 video (*.mp4)|*.mp4", CheckFileExists = true, Multiselect = true };
        if (dialog.ShowDialog(this) == true)
            await ImportFilesAsync(dialog.FileNames);
    }

    private void Import_DragOver(object sender, DragEventArgs args)
    {
        args.Effects = !_busy && _store is not null && DroppedFiles(args).Length > 0
            ? DragDropEffects.Copy : DragDropEffects.None;
        args.Handled = true;
    }

    private async void Import_Drop(object sender, DragEventArgs args)
    {
        args.Handled = true;
        await ImportFilesAsync(DroppedFiles(args));
    }

    private static string[] DroppedFiles(DragEventArgs args) =>
        args.Data.GetData(DataFormats.FileDrop) is string[] files
            ? files.Where(file => Path.GetExtension(file).Equals(".mp4", StringComparison.OrdinalIgnoreCase)).ToArray() : [];

    private async Task ImportFilesAsync(string[] paths, bool watched = false)
    {
        if (_busy || _store is null || paths.Length == 0)
            return;
        _busy = true;
        try
        {
            var imported = 0;
            var failed = 0;
            var known = _store.Snapshot().Select(clip => clip.ImportKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var path in paths)
            {
                _shutdown.Token.ThrowIfCancellationRequested();
                string? key = null;
                try
                {
                    key = ImportIdentity(new FileInfo(path));
                    if (watched && !known.Add(key))
                        continue;
                    var id = Guid.NewGuid();
                    var destination = Path.Combine(_clipsPath, id.ToString("N") + ".mp4");
                    var temporary = destination + ".partial";
                    try
                    {
                        await using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                            65536, FileOptions.Asynchronous | FileOptions.SequentialScan))
                        await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                            65536, FileOptions.Asynchronous))
                            await input.CopyToAsync(output, _shutdown.Token);
                        if (await Task.Run(() => Mp4.ReadDuration(temporary), _shutdown.Token) is null)
                            throw new InvalidDataException("MP4 tamamlanmamış veya okunamıyor.");
                        File.Move(temporary, destination);
                    }
                    finally
                    {
                        if (File.Exists(temporary))
                            File.Delete(temporary);
                    }
                    await AddClipAsync(destination, Path.GetFileNameWithoutExtension(path), id,
                        allowUpload: !watched, importKey: key);
                    imported++;
                }
                catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException)
                {
                    failed++;
                    if (watched && key is not null)
                        _watchRetryAfter[key] = DateTimeOffset.UtcNow.AddMinutes(1);
                }
            }
            if (!watched)
            {
                StatusMessage.Text = failed == 0 ? $"{imported} klip içe aktarıldı."
                    : $"{imported} klip alındı. {failed} MP4 okunamadı veya hâlâ yazılıyor.";
                if (imported > 0)
                    ShowPanel("library");
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
        finally { _busy = false; }
    }

    private static string ImportIdentity(FileInfo file) =>
        FormattableString.Invariant($"{file.FullName}|{file.Length}|{file.LastWriteTimeUtc.Ticks}");

    private async void BackgroundTick(object? sender, EventArgs args)
    {
        if (_backgroundRunning || _shutdown.IsCancellationRequested)
            return;
        _backgroundRunning = true;
        try
        {
            var expired = SelectedClip?.LinkExpired(DateTimeOffset.UtcNow) == true;
            if (_displayedExpired != expired)
            {
                _displayedExpired = expired;
                RefreshLibrary();
            }
            if (_capture is not null && !_captureBusy && !WindowsCapture.WindowExists(_activeCaptureWindow))
            {
                await ToggleCaptureAsync(automatic: true);
                ShowReplayStatus("Oyun penceresi kapandı. Otomatik kayıt seçiliyse yeniden açılmasını bekliyor.",
                    System.Windows.Forms.ToolTipIcon.Info);
            }
            if (HasAutomaticGame && !_autoGamePaused && !_captureBusy && _capture is null &&
                !_recovering && DateTimeOffset.UtcNow >= _nextAutoCapture)
            {
                var windows = WindowsCapture.Windows(_windowSource?.Handle ?? 0);
                var foreground = WindowsCapture.ForegroundWindow;
                var target = windows.FirstOrDefault(window => window.Handle == foreground && CanStartAutomatically(window.Handle))
                    ?? windows.FirstOrDefault(window => CanStartAutomatically(window.Handle));
                if (target is not null)
                {
                    _nextAutoCapture = DateTimeOffset.UtcNow.AddSeconds(20);
                    CaptureTarget.ItemsSource = windows;
                    CaptureTarget.SelectedItem = target;
                    await ToggleCaptureAsync(automatic: true);
                }
            }
            if (_settings.AutoUpdates && !_armed && !_captureBusy && DateTimeOffset.UtcNow >= _nextUpdateCheck)
                _ = CheckForUpdateAsync(manual: false);
            if (DateTimeOffset.UtcNow >= _nextQuotaCheck && _settings.AutoCleanup && _settings.QuotaBytes > 0 &&
                !_editing && !_galleryMutating && !_captureBusy && !_armed)
                await EnforceStorageLimitAsync();
            if (_busy || _store is null || _settings.ImportFolder is not { } folder || _settings.ImportSince is not { } since)
                return;
            var known = _store.Snapshot().Select(clip => clip.ImportKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var (key, retryAt) in _watchRetryAfter.ToArray())
                if (retryAt > DateTimeOffset.UtcNow)
                    known.Add(key);
                else
                    _watchRetryAfter.Remove(key);
            var files = await Task.Run(() =>
            {
                var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true,
                    AttributesToSkip = FileAttributes.ReparsePoint };
                if (!Directory.Exists(folder))
                    return Array.Empty<string>();
                var now = DateTime.UtcNow;
                return new DirectoryInfo(folder).EnumerateFiles("*.mp4", options)
                    .Where(file => file.LastWriteTimeUtc >= since.UtcDateTime &&
                        file.LastWriteTimeUtc <= now.AddSeconds(-3) && !known.Contains(ImportIdentity(file)))
                    .Take(8).Select(file => file.FullName).ToArray();
            }, _shutdown.Token);
            if (string.Equals(folder, _settings.ImportFolder, StringComparison.OrdinalIgnoreCase))
                await ImportFilesAsync(files, watched: true);
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            ImportFolderLabel.ToolTip = "Klip klasörü geçici olarak okunamadı. Yeniden denenecek.";
        }
        finally { _backgroundRunning = false; }
    }

    private async void TestClip_Click(object sender, RoutedEventArgs args) => await SaveReplayAsync(test: true);

    private async Task EditClipAsync(Clip clip)
    {
        if (_editing || _galleryMutating)
            return;
        if (!File.Exists(clip.FilePath))
        {
            StatusMessage.Text = "Yerel video bulunamadı.";
            return;
        }
        _editing = true;
        try
        {
            ShowWindow();
            var editor = new ClipEditor(clip, Path.Combine(_clipsPath, "Paylaşım"), _settings.English,
                bookmarks => _store?.Change(clip.Id, current => current with { Bookmarks = bookmarks })) { Owner = this };
            if (editor.ShowDialog() == true && editor.SavedPath is { } path)
            {
                if (editor.ReplaceRequested && _store is { } store && _archiveGate.Wait(0))
                {
                    try
                    {
                        var replaced = await Task.Run(() => store.ReplaceLocal(clip.Id, path, _clipsPath, editor.SavedBookmarks,
                            _settings.ArchiveFolder));
                        if (replaced is not null)
                        {
                            RefreshLibrary(replaced.Id);
                            StatusMessage.Text = T("Yerel orijinal değiştirildi.", "Local original replaced.");
                            if (replaced.PendingArchiveFolder is { } folder)
                                _ = BackupClipsAsync([replaced], folder);
                            return;
                        }
                    }
                    finally { _archiveGate.Release(); }
                }
                var destination = Path.Combine(_clipsPath, $"Kırpılmış klip-{editor.SavedId:N}.mp4");
                File.Move(path, destination);
                await AddClipAsync(destination, clip.Name + " · Kırpılmış", editor.SavedId, clip.Game, allowUpload: false, bookmarks: editor.SavedBookmarks);
                if (editor.ReplaceRequested)
                    StatusMessage.Text = T("Orijinal kullanımda veya korumalı. Düzenleme ayrı klip olarak kaydedildi.",
                        "The original is busy or protected. The edit was saved as a separate clip.");
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
        catch (Exception error) when (IsReplayError(error))
        {
            StatusMessage.Text = "Klip düzenleyici işlemi tamamlanamadı. " + error.Message;
        }
        finally { _editing = false; }
    }
}
