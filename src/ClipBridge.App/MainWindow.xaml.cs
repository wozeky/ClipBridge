using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using ClipBridge.Core;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace ClipBridge.App;

public partial class MainWindow : Window
{
    private readonly string _root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ClipBridge");
    private readonly string _clipsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
        "ClipBridge");
    private readonly CancellationTokenSource _shutdown = new();
    private readonly HttpClient _http = new(new HttpClientHandler { AllowAutoRedirect = false })
    {
        Timeout = TimeSpan.FromHours(6),
        MaxResponseContentBufferSize = 128 * 1024
    };
    private ClipStore? _store;
    private UploadQueue? _queue;
    private Forms.NotifyIcon? _tray;
    private ClipSettings _settings = new();
    private bool _exit;
    private bool _busy;
    private Guid? _progressId;

    public MainWindow()
    {
        InitializeComponent();
        if (Environment.GetCommandLineArgs() is [_, "--smoke-test", var marker])
        {
            ContentRendered += (_, _) =>
            {
                File.WriteAllText(marker, "ClipBridge window rendered");
                Application.Current.Shutdown();
            };
            return;
        }
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs args)
    {
        Loaded -= OnLoaded;
        try
        {
            if (Path.IsPathFullyQualified(_clipsPath) is false)
                throw new IOException("Windows video klasörü bulunamadı.");
            Directory.CreateDirectory(_clipsPath);
            _settings = AtomicJson.Read<ClipSettings>(Path.Combine(_root, "settings.json")) ?? new ClipSettings();
            ProviderChoice.SelectedIndex = _settings.Provider == Provider.Gofile ? 1 : 0;
            SpeedLimit.Text = _settings.SpeedLimit.ToString(CultureInfo.InvariantCulture);
            AutoUpload.IsChecked = _settings.AutoUpload;
            AutoCopy.IsChecked = _settings.AutoCopy;
            StartWithWindows.IsChecked = IsStartupEnabled();
            _store = new ClipStore(Path.Combine(_root, "library.json"));
            _queue = new UploadQueue(_store,
                [new FreeUploadProvider(_http, Provider.Litterbox), new FreeUploadProvider(_http, Provider.Gofile)],
                () => Volatile.Read(ref _settings).SpeedLimit);
            _queue.Changed += clip => _ = Dispatcher.InvokeAsync(() => OnClipChanged(clip));
            _queue.Progress += (id, percent) => _ = Dispatcher.InvokeAsync(() =>
            {
                if (_progressId == id)
                    UploadProgress.Value = percent;
            });
            CreateTrayIcon();
            RefreshLibrary();
            _ = RunQueueAsync(_queue);
            if (Environment.GetCommandLineArgs().Contains("--tray"))
                Hide();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            StatusMessage.Text = "Klip arşivi veya ayarlar açılamadı. " + error.Message;
            RefreshButtons();
        }
    }

    private async Task RunQueueAsync(UploadQueue queue)
    {
        try { await Task.Run(() => queue.RunAsync(_shutdown.Token)); }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            StatusMessage.Text = "Yükleme kuyruğu arşivi güncelleyemedi. " + error.Message;
        }
    }

    private async void Import_Click(object sender, RoutedEventArgs args)
    {
        if (_busy || _store is null)
            return;
        var dialog = new OpenFileDialog { Filter = "MP4 video (*.mp4)|*.mp4", CheckFileExists = true };
        if (dialog.ShowDialog(this) != true)
            return;

        _busy = true;
        try
        {
            var id = Guid.NewGuid();
            var destination = Path.Combine(_clipsPath, id.ToString("N") + ".mp4");
            var temporary = destination + ".partial";
            StatusMessage.Text = "MP4 arşive kopyalanıyor…";
            try
            {
                await using (var input = new FileStream(dialog.FileName, FileMode.Open, FileAccess.Read, FileShare.Read,
                    65536, FileOptions.Asynchronous | FileOptions.SequentialScan))
                await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    65536, FileOptions.Asynchronous))
                {
                    if (input.Length == 0)
                        throw new InvalidDataException("Seçilen dosya boş.");
                    await input.CopyToAsync(output, _shutdown.Token);
                }
                File.Move(temporary, destination);
            }
            finally
            {
                if (File.Exists(temporary))
                    File.Delete(temporary);
            }
            await AddClipAsync(destination, Path.GetFileNameWithoutExtension(dialog.FileName), id);
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            StatusMessage.Text = "MP4 arşive eklenemedi. " + error.Message;
        }
        finally { _busy = false; }
    }

    private async Task AddClipAsync(string path, string name, Guid id)
    {
        var info = new FileInfo(path);
        var duration = await Task.Run(() => Mp4.ReadDuration(path), _shutdown.Token);
        var clip = new Clip(id, path, name, DateTimeOffset.UtcNow, info.Length, duration, Provider: _settings.Provider,
            State: _settings.AutoUpload ? UploadState.Queued : UploadState.Local);
        _store!.Add(clip);
        RefreshLibrary(clip.Id);
        StatusMessage.Text = _settings.AutoUpload ? "Klip kaydedildi, yükleme sıraya alındı." : "Klip yerel arşive kaydedildi.";
        if (_settings.AutoUpload)
            _queue?.Signal();
    }

    private void OnClipChanged(Clip clip)
    {
        RefreshLibrary();
        if (clip.State == UploadState.Uploading)
        {
            _progressId = clip.Id;
            UploadProgress.Value = 0;
            UploadProgress.Visibility = Visibility.Visible;
        }
        else if (_progressId == clip.Id)
        {
            _progressId = null;
            UploadProgress.Visibility = Visibility.Collapsed;
        }

        if (clip.State == UploadState.Uploaded)
        {
            var copied = _settings.AutoCopy && TryCopy(clip.ShareUrl);
            var timing = clip.Timing is null ? "" : $" · {clip.Timing.LinkReady.TotalSeconds:F1} sn";
            StatusMessage.Text = (copied ? "Bağlantı panoya kopyalandı" : "Paylaşım bağlantısı hazır") + timing;
            if (!IsVisible)
                _tray?.ShowBalloonTip(2500, "ClipBridge", StatusMessage.Text, Forms.ToolTipIcon.Info);
        }
        else if (clip.Error is not null)
            StatusMessage.Text = clip.Error + (clip.NextAttempt is null ? "" : " Otomatik yeniden denenecek.");
    }

    private void SaveSettings_Click(object sender, RoutedEventArgs args)
    {
        if (!int.TryParse(SpeedLimit.Text, NumberStyles.None, CultureInfo.InvariantCulture, out int limit) ||
            limit is < 0 or > 100000)
        {
            StatusMessage.Text = "Hız sınırı için 0–100000 arasında bir sayı girin.";
            return;
        }
        try
        {
            var settings = new ClipSettings(ProviderChoice.SelectedIndex == 1 ? Provider.Gofile : Provider.Litterbox,
                AutoUpload.IsChecked == true, AutoCopy.IsChecked == true, limit);
            AtomicJson.Write(Path.Combine(_root, "settings.json"), settings);
            Volatile.Write(ref _settings, settings);
            using var startup = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
            if (StartWithWindows.IsChecked == true)
                startup.SetValue("ClipBridge", $"\"{Environment.ProcessPath}\" --tray");
            else
                startup.DeleteValue("ClipBridge", throwOnMissingValue: false);
            StatusMessage.Text = "Ayarlar kaydedildi.";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            StatusMessage.Text = "Ayarlar kaydedilirken hata oluştu. " + error.Message;
        }
    }

    private static bool IsStartupEnabled()
    {
        using var startup = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        return startup?.GetValue("ClipBridge") is string;
    }

    private void RefreshLibrary(Guid? select = null)
    {
        select ??= SelectedClip?.Id;
        var rows = _store?.Snapshot().Select(clip => new ClipRow(clip)).ToArray() ?? [];
        Library.ItemsSource = rows;
        Library.SelectedItem = rows.FirstOrDefault(row => row.Clip.Id == select);
        RefreshButtons();
    }

    private Clip? SelectedClip => (Library.SelectedItem as ClipRow)?.Clip;

    private void Library_SelectionChanged(object sender, RoutedEventArgs args) => RefreshButtons();

    private void RefreshButtons()
    {
        OpenButton.IsEnabled = SelectedClip is not null;
        CopyButton.IsEnabled = SelectedClip?.ShareUrl is not null;
        UploadButton.IsEnabled = _queue is not null && SelectedClip?.State is UploadState.Local or UploadState.Failed;
    }

    private void Open_Click(object sender, RoutedEventArgs args)
    {
        if (SelectedClip is { } clip)
            OpenPath(clip.FilePath);
    }

    private void Folder_Click(object sender, RoutedEventArgs args) => OpenPath(_clipsPath);

    private void OpenPath(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Win32Exception error) { StatusMessage.Text = "Dosya veya klasör açılamadı. " + error.Message; }
    }

    private void Copy_Click(object sender, RoutedEventArgs args) =>
        StatusMessage.Text = TryCopy(SelectedClip?.ShareUrl) ? "Bağlantı panoya kopyalandı." : "Panoya erişilemedi.";

    private static bool TryCopy(string? text)
    {
        if (text is null)
            return false;
        try { Clipboard.SetText(text); return true; }
        catch (ExternalException) { return false; }
    }

    private void Upload_Click(object sender, RoutedEventArgs args)
    {
        if (SelectedClip is not { } clip)
            return;
        try
        {
            if (_queue?.Enqueue(clip.Id, _settings.Provider) is not null)
                StatusMessage.Text = "Klip yükleme sırasına alındı.";
        }
        catch (IOException error) { StatusMessage.Text = "Kuyruk kaydedilemedi. " + error.Message; }
    }

    private void CreateTrayIcon()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("ClipBridge'i aç", null, (_, _) => ShowWindow());
        menu.Items.Add("Çıkış", null, (_, _) => { _exit = true; Close(); });
        _tray = new Forms.NotifyIcon { Icon = System.Drawing.SystemIcons.Application, Text = "ClipBridge",
            ContextMenuStrip = menu, Visible = true };
        _tray.DoubleClick += (_, _) => ShowWindow();
    }

    private void ShowWindow()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    protected override void OnClosing(CancelEventArgs args)
    {
        if (!_exit && _tray is not null)
        {
            args.Cancel = true;
            Hide();
        }
        base.OnClosing(args);
    }

    protected override void OnClosed(EventArgs args)
    {
        _shutdown.Cancel();
        _tray?.ContextMenuStrip?.Dispose();
        _tray?.Dispose();
        _http.Dispose();
        base.OnClosed(args);
    }

    private sealed record ClipSettings(Provider Provider = Provider.Litterbox, bool AutoUpload = false,
        bool AutoCopy = true, int SpeedLimit = 0);

    private sealed record ClipRow(Clip Clip)
    {
        public string Date => Clip.CreatedAt.ToLocalTime().ToString("dd.MM.yyyy HH:mm");
        public string Duration => Clip.Duration?.ToString(@"m\:ss") ?? "—";
        public string Size => $"{Clip.Bytes / 1_000_000d:F1} MB";
        public string Status => Clip.State switch
        {
            UploadState.Local => "Yerel",
            UploadState.Queued => Clip.NextAttempt is null ? "Sırada" : "Yeniden denenecek",
            UploadState.Uploading => "Yükleniyor",
            UploadState.Uploaded => "Bağlantı hazır",
            _ => Clip.Error ?? "Yüklenemedi"
        };
    }
}
