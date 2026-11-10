using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using RollClip.Core;

namespace RollClip.App;

public partial class ClipEditor : Window
{
    private readonly Clip _clip;
    private readonly string _folder;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(125) };
    private CancellationTokenSource? _export;
    private double _duration;
    private bool _updating;
    private bool _playing;
    private bool _mediaReady;
    private string? _discordPath;
    private readonly Action<double[]> _storeBookmarks;
    private readonly bool _english;
    private double[] _bookmarks;
    private bool _refreshingBookmarks;

    internal ClipEditor(Clip clip, string folder, bool english, Action<double[]> storeBookmarks)
    {
        InitializeComponent();
        _clip = clip;
        _folder = folder;
        _english = english;
        _storeBookmarks = storeBookmarks;
        _bookmarks = clip.Bookmarks ?? [];
        if (english)
            EnglishUi.Apply(this);
        foreach (var preset in ClipExport.SizePresets)
            ExportSizeChoice.Items.Add(new ComboBoxItem { Content = preset.Label, Tag = preset.Bytes });
        ExportSizeChoice.SelectedIndex = 0;
        _duration = clip.Duration?.TotalSeconds ?? 0;
        IncludeMic.Visibility = clip.SeparateMicrophone ? Visibility.Visible : Visibility.Collapsed;
        Timeline.Maximum = _duration;
        RefreshBookmarks();
        StartBox.Text = "0";
        EndBox.Text = Seconds(_duration);
        Preview.Source = new Uri(clip.FilePath);
        Preview.Play();
        _timer.Tick += (_, _) =>
        {
            if (!_mediaReady || Timeline.IsMouseCaptureWithin)
                return;
            _updating = true;
            Timeline.Value = Preview.Position.TotalSeconds;
            PositionText.Text = $"{Seconds(Timeline.Value)} sn";
            _updating = false;
        };
        _timer.Start();
    }

    private static string Seconds(double value) => value.ToString("0.###", CultureInfo.CurrentCulture);
    private void Preview_Opened(object sender, RoutedEventArgs args)
    {
        _mediaReady = true;
        Preview.Pause();
        if (_duration <= 0 && Preview.NaturalDuration.HasTimeSpan)
        {
            _duration = Preview.NaturalDuration.TimeSpan.TotalSeconds;
            Timeline.Maximum = _duration;
            EndBox.Text = Seconds(_duration);
            DrawBookmarkRail();
        }
        PlayButton.IsEnabled = true;
    }

    private void Preview_Failed(object sender, ExceptionRoutedEventArgs args)
    {
        _mediaReady = false;
        PlayButton.IsEnabled = false;
        Feedback.Text = EnglishUi.Choose(_english, "Windows bu videonun önizlemesini açamadı. Saniyeleri girerek dışa aktarmayı deneyebilirsiniz.", "Windows could not preview this video. Enter the time range to try exporting.");
    }

    private void Preview_Ended(object sender, RoutedEventArgs args)
    {
        SetPlaying(false);
        Preview.Position = TimeSpan.Zero;
    }

    private void Play_Click(object sender, RoutedEventArgs args) => SetPlaying(!_playing);
    private void SetPlaying(bool playing)
    {
        _playing = playing;
        if (playing)
            Preview.Play();
        else
            Preview.Pause();
        PlayButton.Content = playing ? EnglishUi.Choose(_english, "Duraklat", "Pause") : EnglishUi.Choose(_english, "Oynat", "Play");
    }

    private void Timeline_Changed(object sender, RoutedPropertyChangedEventArgs<double> args)
    {
        if (!_updating && _mediaReady)
            Preview.Position = TimeSpan.FromSeconds(args.NewValue);
    }

    private void AddBookmark_Click(object sender, RoutedEventArgs args)
    {
        if (_duration <= 0 || _bookmarks.Length >= 100)
            return;
        var at = Math.Round(Timeline.Value, 2);
        if (!_bookmarks.Any(value => Math.Abs(value - at) < 0.05))
            StoreBookmarks([.. _bookmarks.Append(at).Order()]);
    }

    private void RemoveBookmark_Click(object sender, RoutedEventArgs args)
    {
        if (BookmarkChoice.SelectedIndex is var index && index >= 0 && index < _bookmarks.Length)
            StoreBookmarks([.. _bookmarks.Where((_, item) => item != index)]);
    }

    private void StoreBookmarks(double[] bookmarks)
    {
        try
        {
            _storeBookmarks(bookmarks);
            _bookmarks = bookmarks;
            RefreshBookmarks();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Feedback.Text = EnglishUi.Choose(_english, "Yer imleri kaydedilemedi.", "Could not save bookmarks.");
        }
    }

    private void RefreshBookmarks()
    {
        _refreshingBookmarks = true;
        BookmarkChoice.ItemsSource = _bookmarks.Select(value => $"{Seconds(value)} s").ToArray();
        _refreshingBookmarks = false;
        DrawBookmarkRail();
    }

    private void Bookmark_Changed(object sender, SelectionChangedEventArgs args)
    {
        if (!_refreshingBookmarks && BookmarkChoice.SelectedIndex is var index && index >= 0 && index < _bookmarks.Length)
            Timeline.Value = _bookmarks[index];
    }

    private void BookmarkRail_Resized(object sender, SizeChangedEventArgs args)
    {
        if (_bookmarks is not null)
            DrawBookmarkRail();
    }

    private void DrawBookmarkRail()
    {
        BookmarkRail.Children.Clear();
        if (_duration <= 0)
            return;
        foreach (var time in _bookmarks)
        {
            var mark = new Button { Width = 9, Height = 14, Padding = new Thickness(0), Background = Brushes.Chartreuse,
                ToolTip = $"{Seconds(time)} s", Focusable = false };
            mark.Click += (_, _) => Timeline.Value = time;
            Canvas.SetLeft(mark, Math.Clamp(time / _duration, 0, 1) * Math.Max(0, BookmarkRail.ActualWidth - mark.Width));
            BookmarkRail.Children.Add(mark);
        }
    }

    private void SetStart_Click(object sender, RoutedEventArgs args) => StartBox.Text = Seconds(Timeline.Value);
    private void SetEnd_Click(object sender, RoutedEventArgs args) => EndBox.Text = Seconds(Timeline.Value);
    private void OpenCopy_Click(object sender, RoutedEventArgs args)
    {
        if (_discordPath is null)
            return;
        try { Process.Start(new ProcessStartInfo(_discordPath) { UseShellExecute = true }); }
        catch (Exception error) when (error is IOException or Win32Exception)
        {
            Feedback.Text = "Paylaşım kopyası açılamadı. " + error.Message;
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs args) => _export?.Cancel();
    private async void Discord_Click(object sender, RoutedEventArgs args) => await ExportAsync(discord: true);
    private async void Save_Click(object sender, RoutedEventArgs args) => await ExportAsync(discord: false);

    internal string? SavedPath { get; private set; }
    internal Guid SavedId { get; private set; }
    internal double[] SavedBookmarks { get; private set; } = [];

    private async Task ExportAsync(bool discord)
    {
        if (_export is not null)
            return;
        if (!double.TryParse(StartBox.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out var start) ||
            !double.TryParse(EndBox.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out var end) ||
            !double.IsFinite(start) || !double.IsFinite(end) || start < 0 || end > _duration + 0.001 || end - start < 0.25)
        {
            Feedback.Text = EnglishUi.Choose(_english, "Klip içinde en az 0,25 saniyelik geçerli bir aralık seçin.", "Select at least 0.25 seconds within the clip.");
            return;
        }

        using var cancellation = new CancellationTokenSource();
        _export = cancellation;
        DiscordButton.IsEnabled = SaveButton.IsEnabled = false;
        CancelButton.IsEnabled = true;
        if (_mediaReady)
            SetPlaying(false);
        var id = Guid.NewGuid();
        try
        {
            var source = new FileInfo(_clip.FilePath);
            var limit = (ExportSizeChoice.SelectedItem as ComboBoxItem)?.Tag as long? ?? ClipExport.DiscordBytes;
            var copy = discord && start == 0 && Math.Abs(end - _duration) < 0.001 &&
                !_clip.SeparateMicrophone && Mute.IsChecked != true && source.Length <= limit;
            var destination = copy ? source.FullName : Path.Combine(_folder, $"{(discord ? "Discord" : "Kırpılmış klip")}-{id:N}.mp4");
            if (!copy)
            {
                Feedback.Text = EnglishUi.Choose(_english, "Video hazırlanıyor…", "Preparing video…");
                Directory.CreateDirectory(_folder);
                var ffmpeg = Path.Combine(AppContext.BaseDirectory, "ffmpeg", "bin", "ffmpeg.exe");
                await ClipExport.CreateAsync(ffmpeg, _clip.FilePath, destination, start, Math.Min(end, _duration),
                    discord, _clip.SeparateMicrophone && IncludeMic.IsChecked == true, Mute.IsChecked == true, cancellation.Token, maxBytes: limit);
            }
            if (discord)
            {
                _discordPath = destination;
                OpenCopyButton.IsEnabled = true;
                Clipboard.SetFileDropList(new StringCollection { destination });
                Feedback.Text = EnglishUi.Choose(_english, "Dosya panoda. Discord'da Ctrl+V ile ekleyebilirsiniz.", "File copied. Press Ctrl+V in Discord to attach it.");
            }
            else
            {
                SavedPath = destination;
                SavedId = id;
                SavedBookmarks = [.. _bookmarks.Where(value => value >= start && value <= end).Select(value => value - start)];
                Feedback.Text = EnglishUi.Choose(_english, "Kırpılmış kopya kaydedildi.", "Trimmed copy saved.");
            }
        }
        catch (OperationCanceledException) { Feedback.Text = EnglishUi.Choose(_english, "Dışa aktarma iptal edildi.", "Export cancelled."); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ExternalException)
        {
            Feedback.Text = "Klip hazırlanamadı. " + error.Message;
        }
        finally
        {
            _export = null;
            DiscordButton.IsEnabled = SaveButton.IsEnabled = true;
            CancelButton.IsEnabled = false;
        }
        if (SavedPath is not null)
            DialogResult = true;
    }

    protected override void OnClosing(CancelEventArgs args)
    {
        if (_export is not null)
        {
            _export.Cancel();
            args.Cancel = true;
        }
        base.OnClosing(args);
    }

    protected override void OnClosed(EventArgs args)
    {
        _timer.Stop();
        Preview.Close();
        base.OnClosed(args);
    }
}
