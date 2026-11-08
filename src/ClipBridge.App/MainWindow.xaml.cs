using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
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
    private readonly string? _smokeMarker;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly HttpClient _http = new(new HttpClientHandler { AllowAutoRedirect = false })
    {
        Timeout = TimeSpan.FromHours(6),
        MaxResponseContentBufferSize = 128 * 1024
    };
    private const int ReplayHotkey = 1;
    private const int WmHotkey = 0x0312;
    private WindowsCapture? _capture;
    private HwndSource? _windowSource;
    private bool _hotkeyRegistered;
    private bool _armed;
    private bool _captureBusy;
    private ClipStore? _store;
    private UploadQueue? _queue;
    private Forms.NotifyIcon? _tray;
    private ClipSettings _settings = new();
    private bool _exit;
    private bool _busy;
    private Guid? _progressId;
    private Guid? _thumbnailId;
    private bool _refreshingLibrary;

    public MainWindow()
    {
        InitializeComponent();
        if (Environment.GetCommandLineArgs() is [_, "--smoke-test" or "--capture-smoke", var marker])
        {
            _smokeMarker = marker;
            var smokeCapture = Environment.GetCommandLineArgs()[1] == "--capture-smoke";
            _root = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(marker))!, "clipbridge-smoke-" + Guid.NewGuid().ToString("N"));
            _clipsPath = Path.Combine(_root, "videos");
            ContentRendered += smokeCapture ? OnSmokeCaptureRendered : OnSmokeRendered;
        }
        Loaded += OnLoaded;
    }

    private void OnSmokeRendered(object? sender, EventArgs args)
    {
        if (_store is null || _queue is null || Library.Items.Count != 0 || !IsLoaded)
        {
            Application.Current.Shutdown(1);
            return;
        }
        File.WriteAllText(_smokeMarker!, "ClipBridge window and library initialized");
        Application.Current.Shutdown();
    }

    private async void OnSmokeCaptureRendered(object? sender, EventArgs args)
    {
        ContentRendered -= OnSmokeCaptureRendered;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var redraw = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        redraw.Tick += (_, _) => StatusMessage.Text = DateTime.UtcNow.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);
        redraw.Start();
        int result = 0;
        try
        {
            await using var capture = new WindowsCapture();
            await capture.StartAsync(new WindowInteropHelper(this).Handle, _root, 3, false, false, timeout.Token);
            await Task.Delay(3500, timeout.Token);
            var clip = await capture.SaveAsync(Path.Combine(_clipsPath, "window.mp4"), timeout.Token);
            var duration = Mp4.ReadDuration(clip) ?? throw new CaptureException("No window capture MP4.");
            File.WriteAllLines(_smokeMarker!, [$"{capture.Source} · {capture.Encoder} · {duration.TotalSeconds:F2}s", clip]);
        }
        catch (Exception error) when (IsReplayError(error))
        {
            File.WriteAllText(_smokeMarker!, error.Message);
            result = 1;
        }
        finally
        {
            redraw.Stop();
            Application.Current.Shutdown(result);
        }
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
            AutoFallback.IsChecked = _settings.AutoFallback;
            StartWithWindows.IsChecked = _smokeMarker is null && IsStartupEnabled();
            ReplayLength.ItemsSource = ReplayDurations.Options;
            ReplayLength.SelectedItem = _settings.ReplaySeconds;
            DesktopAudio.IsChecked = _settings.DesktopAudio;
            Microphone.IsChecked = _settings.Microphone;
            GameName.Text = _settings.Game ?? "";
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
            RefreshLibrary();
            if (_smokeMarker is not null)
                return;
            CreateTrayIcon();
            _windowSource = PresentationSource.FromVisual(this) as HwndSource;
            _windowSource?.AddHook(WindowMessage);
            RefreshWindows_Click(this, new RoutedEventArgs());
            _ = RunQueueAsync(_queue);
            if (Environment.GetCommandLineArgs().Contains("--tray"))
                Hide();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException
            or FormatException or System.Security.SecurityException)
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

    private nint WindowMessage(nint window, int message, nint parameter, nint data, ref bool handled)
    {
        if (message == WmHotkey && parameter == ReplayHotkey && _armed)
        {
            handled = true;
            _ = SaveReplayAsync();
        }
        return 0;
    }

    private void RefreshWindows_Click(object sender, RoutedEventArgs args)
    {
        var previous = (CaptureTarget.SelectedItem as WindowsCapture.CaptureWindow)?.Handle;
        var windows = WindowsCapture.Windows(_windowSource?.Handle ?? 0);
        CaptureTarget.ItemsSource = windows;
        CaptureTarget.SelectedItem = windows.FirstOrDefault(window => window.Handle == previous);
    }

    private async void Arm_Click(object sender, RoutedEventArgs args)
    {
        if (_captureBusy || _store is null)
            return;
        if (_capture is { } active)
        {
            _capture = null;
            SetReplayArmed(false);
            SetCaptureBusy(true);
            try
            {
                await active.DisposeAsync();
                StatusMessage.Text = "Pencere kaydı durdu.";
            }
            catch (Exception error) when (IsReplayError(error))
            {
                ShowReplayStatus(error.Message, Forms.ToolTipIcon.Warning);
            }
            finally { SetCaptureBusy(false); }
            return;
        }
        if (CaptureTarget.SelectedItem is not WindowsCapture.CaptureWindow target)
        {
            StatusMessage.Text = "Ayarlar'dan kaydedilecek oyun penceresini seçin.";
            return;
        }
        if (!SaveSettings())
            return;

        SetCaptureBusy(true);
        var capture = new WindowsCapture();
        try
        {
            StatusMessage.Text = "Windows pencere kaydı başlatılıyor…";
            await capture.StartAsync(target.Handle, Path.Combine(_root, "replay"), _settings.ReplaySeconds,
                _settings.DesktopAudio, _settings.Microphone, _shutdown.Token);
            _capture = capture;
            SetReplayArmed(true);
            StatusMessage.Text = $"Kayıt hazır · {capture.Source} · {capture.Encoder} · hedef {_settings.ReplaySeconds} sn. " +
                (_hotkeyRegistered ? "Klip için F8." : "Klip al düğmesini kullanın.");
            _ = WatchCaptureAsync(capture);
        }
        catch (Exception error) when (IsReplayError(error))
        {
            if (!_shutdown.IsCancellationRequested)
                ShowReplayStatus(ReplayErrorMessage(error), Forms.ToolTipIcon.Warning);
        }
        finally { SetCaptureBusy(false); }
    }

    private async Task WatchCaptureAsync(WindowsCapture capture)
    {
        try { await capture.Completion; }
        catch (Exception error) when (IsReplayError(error))
        {
            if (_capture != capture)
                return;
            _capture = null;
            SetReplayArmed(false);
            ShowReplayStatus("Pencere kaydı durdu. " + ReplayErrorMessage(error), Forms.ToolTipIcon.Warning);
            try { await capture.DisposeAsync(); }
            catch (Exception cleanupError) when (IsReplayError(cleanupError)) { }
        }
    }

    private async void SaveReplay_Click(object sender, RoutedEventArgs args) => await SaveReplayAsync();

    private async Task SaveReplayAsync()
    {
        if (!_armed || _captureBusy || _store is null || _capture is not { } capture)
            return;
        SetCaptureBusy(true);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(25));
            var id = Guid.NewGuid();
            var name = "Klip " + DateTime.Now.ToString("yyyy-MM-dd HH-mm-ss", CultureInfo.InvariantCulture);
            var destination = Path.Combine(_clipsPath, $"{name}-{id:N}.mp4");
            var path = await capture.SaveAsync(destination, timeout.Token);
            await AddClipAsync(path, name, id, _settings.Game);
            if (!IsVisible)
                _tray?.ShowBalloonTip(1500, "ClipBridge", StatusMessage.Text, Forms.ToolTipIcon.Info);
        }
        catch (Exception error) when (IsReplayError(error))
        {
            if (!_shutdown.IsCancellationRequested)
                ShowReplayStatus(ReplayErrorMessage(error), Forms.ToolTipIcon.Warning);
        }
        finally { SetCaptureBusy(false); }
    }

    private void SetReplayArmed(bool armed)
    {
        _armed = armed;
        ArmButton.Content = armed ? "Kaydı durdur" : "Kaydı başlat";
        if (_windowSource is not null)
        {
            if (armed && !_hotkeyRegistered)
                _hotkeyRegistered = RegisterHotKey(_windowSource.Handle, ReplayHotkey, 0x4000, 0x77);
            else if (!armed && _hotkeyRegistered)
            {
                UnregisterHotKey(_windowSource.Handle, ReplayHotkey);
                _hotkeyRegistered = false;
            }
        }
        SaveReplayButton.IsEnabled = !_captureBusy && _armed;
    }

    private void SetCaptureBusy(bool busy)
    {
        _captureBusy = busy;
        ArmButton.IsEnabled = !busy;
        SaveReplayButton.IsEnabled = !busy && _armed;
        if (!busy && _exit && _shutdown.IsCancellationRequested)
            _ = Dispatcher.InvokeAsync(Close);
    }

    private static bool IsReplayError(Exception error) => error is CaptureException or COMException
        or OperationCanceledException or IOException or UnauthorizedAccessException or Win32Exception
        or ArgumentException or InvalidOperationException;

    private static string ReplayErrorMessage(Exception error) => error switch
    {
        OperationCanceledException => "Kayıt işlemi zaman aşımına uğradı.",
        _ => error.Message
    };

    private void ShowReplayStatus(string message, Forms.ToolTipIcon icon)
    {
        StatusMessage.Text = message;
        if (!IsVisible)
            _tray?.ShowBalloonTip(2500, "ClipBridge", message, icon);
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(nint handle, int id, uint modifiers, uint key);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(nint handle, int id);

    private async Task AddClipAsync(string path, string name, Guid id, string? game = null)
    {
        var info = new FileInfo(path);
        var duration = await Task.Run(() => Mp4.ReadDuration(path), _shutdown.Token);
        var clip = new Clip(id, path, name, DateTimeOffset.UtcNow, info.Length, duration, Game: game, Provider: _settings.Provider,
            State: _settings.AutoUpload ? UploadState.Queued : UploadState.Local, FallbackProvider: _settings.FallbackProvider);
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

    private void SaveSettings_Click(object sender, RoutedEventArgs args) => SaveSettings();

    private bool SaveSettings()
    {
        if (!int.TryParse(SpeedLimit.Text, NumberStyles.None, CultureInfo.InvariantCulture, out int limit) ||
            limit is < 0 or > 100000)
        {
            StatusMessage.Text = "Hız sınırı için 0–100000 arasında bir sayı girin.";
            return false;
        }
        try
        {
            var seconds = ReplayLength.SelectedItem as int? ?? ReplayDurations.Default;
            var game = GameName.Text.Trim();
            var settings = new ClipSettings(ProviderChoice.SelectedIndex == 1 ? Provider.Gofile : Provider.Litterbox,
                AutoUpload.IsChecked == true, AutoCopy.IsChecked == true, limit, seconds,
                game.Length == 0 ? null : game, AutoFallback.IsChecked == true, DesktopAudio.IsChecked == true,
                Microphone.IsChecked == true);
            AtomicJson.Write(Path.Combine(_root, "settings.json"), settings);
            Volatile.Write(ref _settings, settings);
            using var startup = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
            if (StartWithWindows.IsChecked == true)
                startup.SetValue("ClipBridge", $"\"{Environment.ProcessPath}\" --tray");
            else
                startup.DeleteValue("ClipBridge", throwOnMissingValue: false);
            StatusMessage.Text = _armed ? "Ayarlar kaydedildi. Ses ve süre değişiklikleri sonraki kayıtta uygulanır."
                : "Ayarlar kaydedildi.";
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException
            or FormatException)
        {
            StatusMessage.Text = "Ayarlar kaydedilirken hata oluştu. " + error.Message;
            return false;
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
        _refreshingLibrary = true;
        try
        {
            Library.ItemsSource = rows;
            Library.SelectedItem = rows.FirstOrDefault(row => row.Clip.Id == select);
        }
        finally { _refreshingLibrary = false; }
        UpdateSelection();
    }

    private Clip? SelectedClip => (Library.SelectedItem as ClipRow)?.Clip;

    private void Library_SelectionChanged(object sender, RoutedEventArgs args)
    {
        if (!_refreshingLibrary)
            UpdateSelection();
    }

    private void UpdateSelection()
    {
        RefreshButtons();
        var selected = SelectedClip;
        if (_thumbnailId == selected?.Id)
            return;
        _thumbnailId = selected?.Id;
        Thumbnail.Source = null;
        Thumbnail.Visibility = Visibility.Collapsed;
        if (selected is not null)
            _ = LoadThumbnailAsync(selected);
    }

    private async Task LoadThumbnailAsync(Clip clip)
    {
        // Rapid selection changes do not launch a shell thumbnail worker for every row.
        await Task.Delay(180);
        if (_thumbnailId != clip.Id || _shutdown.IsCancellationRequested)
            return;
        var image = await VideoThumbnail.LoadAsync(clip.FilePath);
        if (_thumbnailId == clip.Id && image is not null && !_shutdown.IsCancellationRequested)
        {
            Thumbnail.Source = image;
            Thumbnail.Visibility = Visibility.Visible;
        }
    }

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
            if (_queue?.Enqueue(clip.Id, _settings.Provider, _settings.FallbackProvider) is not null)
                StatusMessage.Text = "Klip yükleme sırasına alındı.";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            StatusMessage.Text = "Kuyruk kaydedilemedi. " + error.Message;
        }
    }

    private void CreateTrayIcon()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("ClipBridge'i aç", null, (_, _) => ShowWindow());
        menu.Items.Add("Klip al (F8)", null, async (_, _) => await SaveReplayAsync());
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
        if (!args.Cancel && _captureBusy)
        {
            args.Cancel = true;
            _exit = true;
            _shutdown.Cancel();
            Hide();
        }
        base.OnClosing(args);
        if (!args.Cancel && _windowSource is not null)
        {
            SetReplayArmed(false);
            _windowSource.RemoveHook(WindowMessage);
        }
    }

    protected override void OnClosed(EventArgs args)
    {
        _shutdown.Cancel();
        var capture = _capture;
        _capture = null;
        if (capture is not null)
        {
            try { Task.Run(async () => await capture.DisposeAsync()).GetAwaiter().GetResult(); }
            catch (Exception error) when (IsReplayError(error)) { }
        }
        _tray?.ContextMenuStrip?.Dispose();
        _tray?.Dispose();
        _http.Dispose();
        base.OnClosed(args);
    }

    private sealed record ClipSettings(Provider Provider = Provider.Litterbox, bool AutoUpload = false,
        bool AutoCopy = true, int SpeedLimit = 0, int ReplaySeconds = ReplayDurations.Default,
        string? Game = null, bool AutoFallback = false, bool DesktopAudio = true, bool Microphone = false)
    {
        [System.Text.Json.Serialization.JsonIgnore]
        public Provider? FallbackProvider => AutoFallback
            ? (Provider == Provider.Litterbox ? Provider.Gofile : Provider.Litterbox) : null;
    }

    private sealed record ClipRow(Clip Clip)
    {
        public string Title => Clip.Game is null ? Clip.Name : $"{Clip.Game} · {Clip.Name}";
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
