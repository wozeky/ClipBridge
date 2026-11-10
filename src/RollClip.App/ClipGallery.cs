using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using RollClip.Core;

namespace RollClip.App;

public partial class MainWindow
{
    private readonly Dictionary<Image, CancellationTokenSource> _thumbnailRequests = [];
    private IReadOnlyList<ClipRow> _galleryRows = [];
    private Guid? _gallerySelection;
    private int _galleryCount;
    private bool _galleryMutating;

    private void InitializeGallery()
    {
        _refreshingLibrary = true;
        GalleryDate.ItemsSource = new[] { T("Tüm tarihler", "All dates"), T("Son 7 gün", "Last 7 days"), T("Son 30 gün", "Last 30 days") };
        GalleryUpload.ItemsSource = new[] { T("Tüm yüklemeler", "All uploads"), T("Yerel", "Local"), T("Sırada", "Queued"),
            T("Yükleniyor", "Uploading"), T("Yüklendi", "Uploaded"), T("Başarısız", "Failed"), T("İptal", "Cancelled") };
        GallerySort.ItemsSource = new[] { T("En yeni", "Newest"), T("En eski", "Oldest"), T("En uzun", "Longest"), T("En büyük", "Largest") };
        GalleryDate.SelectedIndex = GalleryUpload.SelectedIndex = GallerySort.SelectedIndex = 0;
        _refreshingLibrary = false;
    }

    private void RefreshLibrary(Guid? select = null)
    {
        var clips = _store?.Snapshot() ?? [];
        _galleryCount = clips.Count;
        var oldGame = GalleryGame.SelectedIndex > 0 ? GalleryGame.SelectedItem as string : null;
        var games = clips.Select(clip => clip.Game).OfType<string>().Distinct(StringComparer.CurrentCultureIgnoreCase)
            .Order(StringComparer.CurrentCultureIgnoreCase).Prepend(T("Tüm oyunlar", "All games")).ToArray();
        if (oldGame is not null && !games.Contains(oldGame, StringComparer.CurrentCultureIgnoreCase))
            oldGame = null;
        var search = GallerySearch.Text.Trim();
        var after = GalleryDate.SelectedIndex switch { 1 => DateTimeOffset.UtcNow.AddDays(-7),
            2 => DateTimeOffset.UtcNow.AddDays(-30), _ => DateTimeOffset.MinValue };
        var filtered = clips.Where(clip => clip.CreatedAt >= after && (!GalleryFavorites.IsChecked.GetValueOrDefault() || clip.Favorite) &&
            (oldGame is null || string.Equals(clip.Game, oldGame, StringComparison.CurrentCultureIgnoreCase)) &&
            (GalleryUpload.SelectedIndex <= 0 || (int)clip.State == GalleryUpload.SelectedIndex - 1) &&
            clip.Name.Contains(search, StringComparison.CurrentCultureIgnoreCase));
        var sorted = GallerySort.SelectedIndex switch
        {
            1 => filtered.OrderBy(clip => clip.CreatedAt),
            2 => filtered.OrderByDescending(clip => clip.Duration),
            3 => filtered.OrderByDescending(clip => clip.Bytes),
            _ => filtered
        };
        var rows = sorted.Select(clip => new ClipRow(clip, _settings.English)).ToArray();
        var selectedId = select ?? _gallerySelection;
        var columns = GalleryGrid.IsChecked == true ? 3 : 1;
        _refreshingLibrary = true;
        try
        {
            if (GalleryGame.ItemsSource is not string[] existing || !existing.SequenceEqual(games))
            {
                GalleryGame.ItemsSource = games;
                GalleryGame.SelectedIndex = oldGame is null ? 0 : Math.Max(0, Array.IndexOf(games, oldGame));
            }
            if (Library.ItemsSource is not ClipRow[][] lines || lines.FirstOrDefault()?.Length != Math.Min(columns, rows.Length)
                || !_galleryRows.SequenceEqual(rows))
                Library.ItemsSource = rows.Chunk(columns).ToArray();
            _galleryRows = rows;
            var position = Array.FindIndex(rows, row => row.Clip.Id == selectedId);
            _gallerySelection = position >= 0 ? selectedId : null;
            Library.SelectedIndex = position < 0 ? -1 : position / columns;
            if (select is not null && Library.SelectedItem is { } item && Library.IsVisible)
                Library.ScrollIntoView(item);
        }
        finally { _refreshingLibrary = false; }
        GalleryEmpty.Visibility = rows.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        GalleryEmpty.Text = _galleryCount == 0 ? T("Henüz klip yok. Klip al veya bir MP4’ü buraya bırak.", "No clips yet. Capture one or drop an MP4 here.")
            : T("Bu filtrelerle eşleşen klip yok.", "No clips match these filters.");
        UpdateGallerySelection();
    }

    private Clip? SelectedClip => _gallerySelection is { } id ? _store?.Find(id) : null;

    private void GalleryFilter_Changed(object sender, RoutedEventArgs args)
    {
        if (!_refreshingLibrary && _store is not null)
            RefreshLibrary();
    }

    private void Library_SelectionChanged(object sender, RoutedEventArgs args)
    {
        if (!_refreshingLibrary && Library.SelectedItem is ClipRow[] line)
        {
            if (!line.Any(row => row.Clip.Id == _gallerySelection))
                _gallerySelection = line[0].Clip.Id;
            UpdateGallerySelection();
        }
    }

    private void UpdateGallerySelection()
    {
        RefreshButtons();
        var row = _galleryRows.FirstOrDefault(item => item.Clip.Id == _gallerySelection);
        GallerySelection.Text = row is null ? "" : string.Join(" · ", new[] { row.Title, row.ShareLabel }.OfType<string>());
    }

    private void GalleryCard_Click(object sender, RoutedEventArgs args)
    {
        if (sender is FrameworkElement { DataContext: ClipRow row })
        {
            _gallerySelection = row.Clip.Id;
            var line = Library.Items.OfType<ClipRow[]>().FirstOrDefault(items => items.Contains(row));
            Library.SelectedItem = line;
            UpdateGallerySelection();
        }
    }

    private async void GalleryCard_Open(object sender, MouseButtonEventArgs args)
    {
        if (!_galleryMutating && sender is FrameworkElement { DataContext: ClipRow row } && _store?.Find(row.Clip.Id) is { } clip)
        {
            args.Handled = true;
            await EditClipAsync(clip);
        }
    }

    private void GalleryImage_Loaded(object sender, RoutedEventArgs args) => UpdateGalleryImage((Image)sender);
    private void GalleryImage_Unloaded(object sender, RoutedEventArgs args) => CancelGalleryImage((Image)sender);
    private void GalleryImage_ContextChanged(object sender, DependencyPropertyChangedEventArgs args) => UpdateGalleryImage((Image)sender);
    private void GalleryImage_VisibilityChanged(object sender, DependencyPropertyChangedEventArgs args) => UpdateGalleryImage((Image)sender);

    private void CancelGalleryImage(Image image)
    {
        if (_thumbnailRequests.Remove(image, out var previous))
            previous.Cancel();
        image.Source = null;
    }

    private async void UpdateGalleryImage(Image image)
    {
        CancelGalleryImage(image);
        if (!image.IsLoaded || !image.IsVisible || image.DataContext is not ClipRow row || _shutdown.IsCancellationRequested)
            return;
        using var request = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
        _thumbnailRequests[image] = request;
        try
        {
            await Task.Delay(120, request.Token);
            while (_captureBusy)
                await Task.Delay(250, request.Token);
            var thumbnail = await VideoThumbnail.LoadAsync(row.Clip, Path.Combine(_root, "thumbnails"), request.Token);
            if (!request.IsCancellationRequested)
                image.Source = thumbnail;
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or Win32Exception or ArgumentException or InvalidOperationException or NotSupportedException)
        { Trace.WriteLine(error.Message); }
        finally
        {
            if (_thumbnailRequests.TryGetValue(image, out var current) && current == request)
                _thumbnailRequests.Remove(image);
        }
    }

    private async void Favorite_Click(object sender, RoutedEventArgs args)
    {
        if (SelectedClip is not { } clip || _store is not { } store || _galleryMutating)
            return;
        _galleryMutating = true;
        RefreshButtons();
        try
        {
            await Task.Run(() => store.Change(clip.Id, current => current with { Favorite = !current.Favorite }));
            RefreshLibrary(clip.Id);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { StatusMessage.Text = error.Message; }
        finally { _galleryMutating = false; RefreshButtons(); }
    }

    private void ShowLocation_Click(object sender, RoutedEventArgs args)
    {
        if (SelectedClip is not { } clip)
            return;
        try
        {
            var info = new ProcessStartInfo("explorer.exe") { UseShellExecute = false };
            info.ArgumentList.Add("/select," + clip.FilePath);
            using var explorer = Process.Start(info);
        }
        catch (Win32Exception error) { StatusMessage.Text = error.Message; }
    }

    private async void DeleteClip_Click(object sender, RoutedEventArgs args)
    {
        if (_editing || _galleryMutating || SelectedClip is not { } clip || _store is not { } store || _queue?.IsActive(clip.Id) == true)
            return;
        var message = T($"“{clip.Name}” yerel arşivden silinsin mi? {clip.Bytes / 1_000_000d:F1} MB",
            $"Delete “{clip.Name}” from the local library? {clip.Bytes / 1_000_000d:F1} MB");
        if (MessageBox.Show(this, message, "RollClip", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
            return;
        if (!_archiveGate.Wait(0))
        {
            StatusMessage.Text = T("Yedekleme tamamlanınca yeniden deneyin.", "Try again after the backup finishes.");
            return;
        }
        _galleryMutating = true;
        RefreshButtons();
        try
        {
            var (deleted, staged) = await Task.Run(() => (store.DeleteLocal(clip.Id, _clipsPath, out var path), path));
            StatusMessage.Text = deleted ? staged is null ? T("Klip silindi.", "Clip deleted.") : T("Klip arşivden çıkarıldı. Dosya henüz silinemedi.", "Clip removed. The staged file could not be deleted.")
                : T("Klip etkin bir işte kullanılıyor veya yerel arşivin dışında.", "The clip is busy or outside the managed library.");
            RefreshLibrary();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { StatusMessage.Text = error.Message; }
        finally
        {
            _galleryMutating = false;
            _archiveGate.Release();
            RefreshButtons();
        }
    }
}
