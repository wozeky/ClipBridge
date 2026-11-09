using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using RollClip.Core;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace RollClip.App;

public partial class MainWindow : Window
{
    private const string ProductName = "RollClip";
    private const string LegacyProductName = "ClipBridge";
    private const string StartupKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private readonly string _root = ResolveDataFolder(Environment.SpecialFolder.LocalApplicationData);
    private readonly string _clipsPath = ResolveDataFolder(Environment.SpecialFolder.MyVideos);
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
    private readonly ReplayDiagnostics _diagnostics = new();
    private HwndSource? _windowSource;
    private bool _hotkeyRegistered;
    private int _activeReplayKey = 8;
    private Forms.ToolStripItem? _traySave;
    private string ReplayKeyLabel => $"F{_activeReplayKey}";
    private bool _armed;
    private readonly System.Windows.Threading.DispatcherTimer _bufferTimer = new()
    {
        Interval = TimeSpan.FromSeconds(1)
    };
    private bool _captureBusy;
    private ClipStore? _store;
    private readonly SemaphoreSlim _archiveGate = new(1, 1);
    private bool _archiveBulkRunning;
    private UploadQueue? _queue;
    private Forms.NotifyIcon? _tray;
    private ClipSettings _settings = new();
    private bool _exit;
    private bool _busy;
    private bool _recovering;
    private Guid? _progressId;
    private Guid? _thumbnailId;
    private bool _refreshingLibrary;

    private static string ResolveDataFolder(Environment.SpecialFolder folder)
    {
        var parent = Environment.GetFolderPath(folder);
        var current = Path.Combine(parent, ProductName);
        var legacy = Path.Combine(parent, LegacyProductName);
        return Directory.Exists(current) || !Directory.Exists(legacy) ? current : legacy;
    }

    public MainWindow()
    {
        InitializeComponent();
        Activated += (_, _) => UpdateInterfaceMotion();
        Deactivated += (_, _) => StopInterfaceMotion();
        IsVisibleChanged += (_, _) => UpdateInterfaceMotion();
        StateChanged += (_, _) => UpdateInterfaceMotion();
        SystemParameters.StaticPropertyChanged += OnSystemParametersChanged;
        if (Environment.GetCommandLineArgs() is [_, "--smoke-test" or "--capture-smoke" or "--material-smoke", var marker])
        {
            _smokeMarker = marker;
            var smokeCapture = Environment.GetCommandLineArgs()[1] == "--capture-smoke";
            _root = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(marker))!, "rollclip-smoke-" + Guid.NewGuid().ToString("N"));
            _clipsPath = Path.Combine(_root, "videos");
            ContentRendered += smokeCapture ? OnSmokeCaptureRendered : _materialSmoke ? OnMaterialSmokeRendered : OnSmokeRendered;
        }
        _bufferTimer.Tick += (_, _) => RefreshCaptureStatus();
        Loaded += OnLoaded;
    }

    private string _materialView = "home";
    private TimeSpan _materialPosition;
    private bool _movieReady;
    private bool _movieFailed;
    private bool _moviePlaying;
    private bool CanAnimateInterface => (_smokeMarker is null || _materialSmoke) && !_captureBusy && !_armed
        && (_materialSmoke || (IsVisible && IsActive && WindowState != WindowState.Minimized
        && SystemParameters.ClientAreaAnimation && RenderCapability.Tier != 0));

    private void OnSystemParametersChanged(object? sender, PropertyChangedEventArgs args) =>
        Dispatcher.InvokeAsync(UpdateInterfaceMotion);

    private void UpdateInterfaceMotion()
    {
        if (_armed || (!_materialSmoke && !_settings.Motion))
        {
            StopInterfaceMotion();
            if (ChromeMovie.Source is not null)
            {
                CloseChromeMovie();
                _materialPosition = TimeSpan.Zero;
            }
            return;
        }
        if (_movieFailed || !CanAnimateInterface)
        {
            StopInterfaceMotion();
            return;
        }
        if (ChromeMovie.Source is null && !_materialSmoke && File.Exists(MaterialPath))
        {
            ChromeMovie.Source = new Uri(MaterialPath);
            ChromeMovie.Visibility = Visibility.Visible;
            ChromeMovie.Pause();
        }
        if (!_movieReady || _moviePlaying)
            return;

        ChromeMovie.Play();
        _moviePlaying = true;
        var pulse = new DoubleAnimation(.55, .85, TimeSpan.FromSeconds(2.4))
            { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever };
        Timeline.SetDesiredFrameRate(pulse, 30);
        HomeGlow.BeginAnimation(OpacityProperty, pulse);
    }

    private void CloseChromeMovie()
    {
        ChromeMovie.Close();
        ChromeMovie.Source = null;
        ChromeMovie.Visibility = Visibility.Collapsed;
        _movieReady = false;
    }

    private void StopInterfaceMotion()
    {
        if (_moviePlaying) { ChromeMovie.Pause(); _moviePlaying = false; }
        HomeGlow.BeginAnimation(OpacityProperty, null);
        ClearPanelAnimations();
    }

    private void Chrome_Opened(object sender, RoutedEventArgs args)
    {
        _movieReady = true;
        if (_materialPosition > TimeSpan.Zero)
        {
            ChromeMovie.Position = _materialPosition;
            _materialPosition = TimeSpan.Zero;
        }
        UpdateInterfaceMotion();
    }

    private void Chrome_Ended(object sender, RoutedEventArgs args)
    {
        ChromeMovie.Position = TimeSpan.Zero;
        _moviePlaying = false;
        UpdateInterfaceMotion();
    }

    private void Chrome_Failed(object sender, ExceptionRoutedEventArgs args)
    {
        _movieFailed = true;
        _moviePlaying = false;
        CloseChromeMovie();
        // The same material's poster remains visible. Recording never depends on the UI decoder.
    }

    private void ClearPanelAnimations()
    {
        foreach (var panel in new[] { HomePanel, SettingsPanel, LibraryPanel })
        {
            panel.BeginAnimation(OpacityProperty, null);
            ((TranslateTransform)panel.RenderTransform).BeginAnimation(TranslateTransform.YProperty, null);
        }
    }

    private void ShowPanel(string panel)
    {
        var target = panel switch
        {
            "settings" => SettingsPanel,
            "library" => LibraryPanel,
            _ => HomePanel
        };
        if (target.Visibility == Visibility.Visible)
            return;

        SetMaterialView(target == HomePanel ? "home" : "overlay");
        ClearPanelAnimations();
        HomePanel.Visibility = target == HomePanel ? Visibility.Visible : Visibility.Collapsed;
        SettingsPanel.Visibility = target == SettingsPanel ? Visibility.Visible : Visibility.Collapsed;
        LibraryPanel.Visibility = target == LibraryPanel ? Visibility.Visible : Visibility.Collapsed;
        if (!CanAnimateInterface)
            return;

        var duration = TimeSpan.FromMilliseconds(190);
        var fade = new DoubleAnimation(.55, 1, duration)
            { DecelerationRatio = .8, FillBehavior = FillBehavior.Stop };
        var slide = new DoubleAnimation(7, 0, duration)
            { DecelerationRatio = .8, FillBehavior = FillBehavior.Stop };
        Timeline.SetDesiredFrameRate(fade, 30);
        Timeline.SetDesiredFrameRate(slide, 30);
        target.BeginAnimation(OpacityProperty, fade);
        ((TranslateTransform)target.RenderTransform).BeginAnimation(TranslateTransform.YProperty, slide);
    }

    private string MaterialPath => Path.Combine(AppContext.BaseDirectory, "Assets",
        _materialView == "home" ? "ChromeLoop.mp4" : "ChromeLoopOverlay.mp4");

    private void SetMaterialView(string view)
    {
        if (_materialView == view) return;
        _materialView = view;
        var poster = view == "home" ? "ChromePoster.png" : "ChromePosterOverlay.png";
        ChromePoster.Source = new BitmapImage(new Uri($"pack://application:,,,/Assets/{poster}"));
        if (_smokeMarker is not null || _movieFailed)
            return;
        if (ChromeMovie.Source is not null)
        {
            _materialPosition = ChromeMovie.Position;
            StopInterfaceMotion();
            CloseChromeMovie();
        }
        UpdateInterfaceMotion();
    }

    private void Home_Click(object sender, RoutedEventArgs args) => ShowPanel("home");
    private void Library_Click(object sender, RoutedEventArgs args) => ShowPanel("library");
    private void Settings_Click(object sender, RoutedEventArgs args) => ShowPanel("settings");
    private void Minimize_Click(object sender, RoutedEventArgs args) => WindowState = WindowState.Minimized;
    private void Maximize_Click(object sender, RoutedEventArgs args) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void Close_Click(object sender, RoutedEventArgs args) => Close();
    private void QuickSettings_Click(object sender, RoutedEventArgs args) => SaveSettings();
    private void Motion_Click(object sender, RoutedEventArgs args)
    {
        if (SaveSettings()) UpdateInterfaceMotion();
    }

    private void OnSmokeRendered(object? sender, EventArgs args)
    {
        ContentRendered -= OnSmokeRendered;
        try
        {
            if (_store is null || _queue is null || Library.Items.Count != 0 || !IsLoaded)
                throw new InvalidOperationException("Startup or isolated library failed.");
            if (SaveReplayButton.IsEnabled || OpenButton.IsEnabled || CopyButton.IsEnabled || QuickUploadButton.IsEnabled)
                throw new InvalidOperationException("Unavailable actions must be disabled.");
            if (ChromeMovie.Source is not null || _moviePlaying)
                throw new InvalidOperationException("Smoke mode must not run decorative video.");

            void ExportPng(string suffix, double width = 1000, double height = 750)
            {
                // Keep a real HWND and fit inside the hosted display. Detaching a video/glass
                // tree drops inherited templates; oversizing it clips at the HWND boundary.
                Width = width;
                Height = height;
                UpdateLayout();
                RootSurface.UpdateLayout();
                var actualWidth = (int)Math.Round(RootSurface.ActualWidth);
                var actualHeight = (int)Math.Round(RootSurface.ActualHeight);
                if (Math.Abs(actualWidth-width) > 2 || Math.Abs(actualHeight-height) > 2)
                    throw new InvalidOperationException("Hosted display cannot fit the requested UI test size.");
                var screenshot = new RenderTargetBitmap(actualWidth, actualHeight, 96, 96, PixelFormats.Pbgra32);
                screenshot.Render(RootSurface);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(screenshot));
                using var output = File.Create(Path.ChangeExtension(_smokeMarker!, suffix + ".png"));
                encoder.Save(output);
            }
            ShowPanel("home");
            ExportPng("");
            if (!CaptureTarget.IsVisible || !ReplayLength.IsVisible || HomePanel.ActualHeight <= 0)
                throw new InvalidOperationException("Home controls are not visible.");
            if (CaptureTarget.Template.FindName("DropDownToggle", CaptureTarget) is not FrameworkElement toggle
                || toggle.ActualWidth < CaptureTarget.ActualWidth - 3)
                throw new InvalidOperationException("The source selector surface does not fill its hit target.");
            SettingsNav.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            ExportPng("settings");
            SettingsScroll.ScrollToBottom();
            UpdateLayout();
            ExportPng("archive");
            SettingsScroll.ScrollToTop();
            if (!ProviderChoice.IsVisible || !MotionEnabled.IsVisible || !RecordingQualityChoice.IsVisible
                || !ReplayKeyChoice.IsVisible || !ArchiveFolderBox.IsVisible || SettingsPanel.HasAnimatedProperties || SettingsPanel.RenderTransform.HasAnimatedProperties)
                throw new InvalidOperationException("Settings navigation failed.");
            ReplayKeyChoice.SelectedIndex = 3; // F9
            RecordingQualityChoice.SelectedIndex = (int)RecordingQuality.Light;
            var backupFolder = Path.Combine(_root, "backup");
            ArchiveFolderBox.Text = backupFolder;
            if (!SaveSettings() || SaveReplayButton.Content as string != "F9"
                || AtomicJson.Read<ClipSettings>(Path.Combine(_root, "settings.json")) is not
                    { ReplayKey: 9, Quality: RecordingQuality.Light, Motion: false } saved
                || saved.ArchiveFolder != backupFolder || !ArchiveExistingButton.IsEnabled)
                throw new InvalidOperationException("Recording controls did not persist or update the hotkey label.");
            ReplayKeyChoice.SelectedIndex = 2;
            RecordingQualityChoice.SelectedIndex = 0;
            ArchiveFolderBox.Text = "";
            if (!SaveSettings())
                throw new InvalidOperationException("Could not restore default settings in the isolated smoke profile.");
            StatusMessage.Text = "";
            ShowPanel("library");
            ExportPng("library");
            if (!Library.IsVisible || Library.ActualHeight <= 0 || !RecoverClipsButton.IsVisible)
                throw new InvalidOperationException("Library navigation failed.");
            ShowPanel("home");
            ExportPng("compact", 900, 700);
            ExportPng("wide", 1000, 700);
            DiagnosticsButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            if (!Clipboard.GetText().Contains("Tanılama raporu", StringComparison.Ordinal))
                throw new InvalidOperationException("Diagnostics clipboard failed.");
            File.WriteAllText(_smokeMarker!, "PASS: native WPF home/settings/library, 3 window ratios, disabled actions, motion isolation, persisted profile and shortcut, diagnostics clipboard.");
            Application.Current.Shutdown();
        }
        catch (Exception error)
        {
            File.WriteAllText(_smokeMarker!, "FAIL: " + error);
            Application.Current.Shutdown(1);
        }
    }

    private async void OnSmokeCaptureRendered(object? sender, EventArgs args)
    {
        ContentRendered -= OnSmokeCaptureRendered;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var label = new System.Windows.Controls.TextBlock
        {
            FontSize = 40, HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center, Foreground = System.Windows.Media.Brushes.Black
        };
        var sample = new System.Windows.Controls.Border
        {
            Background = System.Windows.Media.Brushes.Gold, Child = label
        };
        Content = sample;
        var redraw = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        redraw.Tick += (_, _) =>
        {
            var now = DateTime.UtcNow;
            label.Text = now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);
            sample.Background = now.Millisecond < 500 ? System.Windows.Media.Brushes.Gold : System.Windows.Media.Brushes.Cyan;
        };
        redraw.Start();
        int result = 0;
        try
        {
            await using var capture = new WindowsCapture();
            await capture.StartAsync(new WindowInteropHelper(this).Handle, _root, 3, false, false, timeout.Token, RecordingQuality.Light);
            using var app = Process.GetCurrentProcess();
            var started = Stopwatch.GetTimestamp();
            var appStart = app.TotalProcessorTime;
            var recorderStart = capture.SampleRecorderUsage();
            await Task.Delay(5000, timeout.Token);
            var recorderEnd = capture.SampleRecorderUsage();
            app.Refresh();
            var appElapsed = app.TotalProcessorTime - appStart;
            var appWorkingSet = app.WorkingSet64;
            var sampleSeconds = Stopwatch.GetElapsedTime(started).TotalSeconds;
            var machineCpuSeconds = sampleSeconds * Environment.ProcessorCount;
            var recorderCpu = (recorderEnd.CpuTime - recorderStart.CpuTime).TotalSeconds / machineCpuSeconds;
            var appCpu = appElapsed.TotalSeconds / machineCpuSeconds;
            var clip = await capture.SaveAsync(Path.Combine(_clipsPath, "window.mp4"), timeout.Token);
            var duration = Mp4.ReadDuration(clip) ?? throw new CaptureException("No window capture MP4.");
            File.WriteAllLines(_smokeMarker!,
                [$"{capture.Source} · {capture.Encoder} · {capture.FormatLabel} · {duration.TotalSeconds:F2}s",
                 clip, capture.FallbackDetails,
                 FormattableString.Invariant($"Hosted capture sample {sampleSeconds:F1}s, {Environment.ProcessorCount} logical CPUs"),
                 FormattableString.Invariant($"RollClip CPU {appCpu:P1} · working set {appWorkingSet / 1048576d:F1} MiB"),
                 FormattableString.Invariant($"FFmpeg CPU {recorderCpu:P1} · working set {recorderEnd.WorkingSetBytes / 1048576d:F1} MiB")]);
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
            if (_smokeMarker is null)
            {
                try { StartWithWindows.IsChecked = ReadAndMigrateStartup(); }
                catch (Exception error) when (error is UnauthorizedAccessException or System.Security.SecurityException or IOException)
                {
                    StatusMessage.Text = "Windows başlangıç ayarı güncellenemedi. " + error.Message;
                }
            }
            ReplayLength.ItemsSource = ReplayDurations.Options;
            ReplayLength.SelectedItem = _settings.ReplaySeconds;
            DesktopAudio.IsChecked = _settings.DesktopAudio;
            Microphone.IsChecked = _settings.Microphone;
            GameName.Text = _settings.Game ?? "";
            MotionEnabled.IsChecked = _settings.Motion;
            ArchiveFolderBox.Text = _settings.ArchiveFolder ?? "";
            RefreshArchiveControls();
            RecordingQualityChoice.SelectedIndex = Enum.IsDefined(_settings.Quality) ? (int)_settings.Quality : 0;
            ReplayKeyChoice.ItemsSource = Enumerable.Range(6, 6).Select(key => $"F{key}").ToArray();
            ReplayKeyChoice.SelectedIndex = _settings.ReplayKey is >= 6 and <= 11 ? _settings.ReplayKey - 6 : 2;
            UpdateReplayKeyLabel();
            _store = new ClipStore(Path.Combine(_root, "library.json"));
            _queue = new UploadQueue(_store,
                [new FreeUploadProvider(_http, Provider.Litterbox), new FreeUploadProvider(_http, Provider.Gofile)],
                () => Volatile.Read(ref _settings).SpeedLimit);
            _queue.Changed += clip =>
            {
                var changedAt = Stopwatch.GetTimestamp();
                _ = Dispatcher.InvokeAsync(() => OnClipChanged(clip, changedAt));
            };
            _queue.Progress += (id, percent) => _ = Dispatcher.InvokeAsync(() =>
            {
                if (_progressId != id)
                    return;
                UploadProgress.Value = percent;
                if (percent >= 100)
                    StatusMessage.Text = "Dosya gönderildi. Servisten bağlantı bekleniyor…";
            });
            RefreshLibrary();
            if (_smokeMarker is not null)
                return;
            UpdateInterfaceMotion();
            CreateTrayIcon();
            _windowSource = PresentationSource.FromVisual(this) as HwndSource;
            _windowSource?.AddHook(WindowMessage);
            RefreshWindows_Click(this, new RoutedEventArgs());
            _ = RunQueueAsync(_queue);
            if (_settings.ArchiveFolder is { } archiveFolder)
            {
                var pending = _store.Snapshot().Where(clip => clip.PendingArchiveFolder == archiveFolder).ToArray();
                if (pending.Length > 0)
                    _ = BackupClipsAsync(pending, archiveFolder);
            }
            if (Environment.GetCommandLineArgs().Contains("--tray"))
                Hide();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException
            or FormatException or System.Security.SecurityException)
        {
            _diagnostics.CaptureFailed(error);
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
            _ = SaveReplayAsync(viaHotkey: true);
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
        if (_captureBusy || _recovering || _store is null)
            return;
        if (_capture is { } active)
        {
            _capture = null;
            SetReplayArmed(false);
            SetCaptureBusy(true);
            try
            {
                await active.DisposeAsync();
                _diagnostics.CaptureStopped();
                StatusMessage.Text = "Pencere kaydı durdu.";
            }
            catch (Exception error) when (IsReplayError(error))
            {
                _diagnostics.CaptureFailed(error);
                ShowReplayStatus(error.Message, Forms.ToolTipIcon.Warning);
            }
            finally { SetCaptureBusy(false); }
            return;
        }
        _diagnostics.BeginCapture();
        if (CaptureTarget.SelectedItem is not WindowsCapture.CaptureWindow target)
        {
            _diagnostics.CaptureFailed(new CaptureException("Açık ve küçültülmemiş bir pencere seçin."));
            StatusMessage.Text = "Oyun yakalama bölümünden bir pencere seçin.";
            CaptureTarget.Focus();
            return;
        }
        if (!SaveSettings())
        {
            _diagnostics.CaptureFailed(new CaptureException("Kayıt ayarları kaydedilemedi."));
            return;
        }

        SetCaptureBusy(true);
        var capture = new WindowsCapture();
        try
        {
            StatusMessage.Text = "Windows pencere kaydı başlatılıyor…";
            await capture.StartAsync(target.Handle, Path.Combine(_root, "replay"), _settings.ReplaySeconds,
                _settings.DesktopAudio, _settings.Microphone, _shutdown.Token, _settings.Quality);
            _capture = capture;
            _diagnostics.CaptureStarted(capture.Source, capture.Encoder, capture.FallbackDetails, capture.VideoFormat);
            SetReplayArmed(true);
            StatusMessage.Text = $"Kayıt hazır · {capture.Source} · {capture.Encoder} · {capture.FormatLabel} · hedef {_settings.ReplaySeconds} sn. " +
                (_hotkeyRegistered ? $"Klip için {ReplayKeyLabel}." : "Klip al düğmesini kullanın.");
            _ = WatchCaptureAsync(capture);
        }
        catch (Exception error) when (IsReplayError(error))
        {
            _diagnostics.CaptureFailed(error, capture.FallbackDetails);
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
            _diagnostics.CaptureFailed(error);
            SetReplayArmed(false);
            ShowReplayStatus("Pencere kaydı durdu. " + ReplayErrorMessage(error), Forms.ToolTipIcon.Warning);
            try { await capture.DisposeAsync(); }
            catch (Exception cleanupError) when (IsReplayError(cleanupError)) { }
        }
    }

    private async void SaveReplay_Click(object sender, RoutedEventArgs args) => await SaveReplayAsync();

    private async Task SaveReplayAsync(bool viaHotkey = false)
    {
        if (_captureBusy)
        {
            if (viaHotkey)
                ShowReplayStatus("Önceki klip hâlâ hazırlanıyor.", Forms.ToolTipIcon.Info);
            return;
        }
        if (!_armed || _store is null || _capture is not { } capture)
            return;
        var saveStartedAt = Stopwatch.GetTimestamp();
        _diagnostics.BeginSave(viaHotkey, ReplayKeyLabel);
        var mp4Ready = false;
        SetCaptureBusy(true);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(25));
            var id = Guid.NewGuid();
            var name = "Klip " + DateTime.Now.ToString("yyyy-MM-dd HH-mm-ss", CultureInfo.InvariantCulture);
            var destination = Path.Combine(_clipsPath, $"{name}-{id:N}.mp4");
            var path = await capture.SaveAsync(destination, timeout.Token);
            _diagnostics.Mp4Ready(id, saveStartedAt, Stopwatch.GetTimestamp());
            mp4Ready = true;
            ShowReplayStatus("MP4 hazır. Klip diske kaydedildi.", Forms.ToolTipIcon.Info);
            await AddClipAsync(path, name, id, _settings.Game);
        }
        catch (Exception error) when (IsReplayError(error))
        {
            _diagnostics.SaveFailed(error, mp4Ready);
            if (!_shutdown.IsCancellationRequested)
                ShowReplayStatus(mp4Ready ? "MP4 diskte, arşive eklenemedi. Klip klasörünü tarayın."
                    : ReplayErrorMessage(error), Forms.ToolTipIcon.Warning);
        }
        finally { SetCaptureBusy(false); }
    }

    private void SetReplayArmed(bool armed)
    {
        _armed = armed;
        UpdateReplayKeyLabel();
        ArmButton.Content = armed ? "Kaydı durdur" : "Kaydı başlat";
        if (_windowSource is not null)
        {
            if (armed && !_hotkeyRegistered)
                _hotkeyRegistered = RegisterHotKey(_windowSource.Handle, ReplayHotkey, 0x4000, (uint)(0x70 + _activeReplayKey - 1));
            else if (!armed && _hotkeyRegistered)
            {
                UnregisterHotKey(_windowSource.Handle, ReplayHotkey);
                _hotkeyRegistered = false;
            }
        }
        SaveReplayButton.IsEnabled = !_captureBusy && _armed;
        if (armed)
            _bufferTimer.Start();
        else
            _bufferTimer.Stop();
        RefreshCaptureStatus();
        UpdateInterfaceMotion();
    }

    private void UpdateReplayKeyLabel()
    {
        _activeReplayKey = _settings.ReplayKey is >= 6 and <= 11 ? _settings.ReplayKey : 8;
        SaveReplayButton.Content = ReplayKeyLabel;
        System.Windows.Automation.AutomationProperties.SetName(SaveReplayButton, $"Klip al · {ReplayKeyLabel}");
        if (_traySave is not null)
            _traySave.Text = $"Klip al ({ReplayKeyLabel})";
    }

    private void RefreshCaptureStatus()
    {
        var status = !_armed ? "Kayıt kapalı" : _capture?.BufferedDuration is { } duration
            ? $"Hazır · Tampon: {Math.Floor(duration.TotalSeconds):0} sn"
            : "Yeni görüntü gelmiyor";
        if (_armed && !_hotkeyRegistered)
            status += $" · {ReplayKeyLabel} kullanılamıyor";
        if (CaptureStatus.Text != status)
            CaptureStatus.Text = status;
        var trayText = "RollClip · " + status;
        if (_tray is not null && _tray.Text != trayText)
            _tray.Text = trayText;
    }

    private void SetCaptureBusy(bool busy)
    {
        _captureBusy = busy;
        if (busy)
            StopInterfaceMotion();
        UpdateInterfaceMotion();
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
            _tray?.ShowBalloonTip(2500, "RollClip", message, icon);
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
            State: _settings.AutoUpload ? UploadState.Queued : UploadState.Local, FallbackProvider: _settings.FallbackProvider,
            PendingArchiveFolder: _settings.ArchiveFolder);
        _store!.Add(clip);
        _diagnostics.ClipChanged(clip, Stopwatch.GetTimestamp());
        RefreshLibrary(clip.Id);
        StatusMessage.Text = _settings.AutoUpload ? "Klip kaydedildi, yükleme sıraya alındı." : "Klip yerel arşive kaydedildi.";
        if (_settings.AutoUpload)
            _queue?.Signal();
        if (clip.PendingArchiveFolder is { } folder)
            _ = BackupClipsAsync([clip], folder);
    }

    private void OnClipChanged(Clip clip, long changedAt)
    {
        _diagnostics.ClipChanged(clip, changedAt);
        RefreshLibrary();
        if (clip.State == UploadState.Uploading)
        {
            _progressId = clip.Id;
            UploadProgress.Value = 0;
            UploadProgress.Visibility = Visibility.Visible;
            StatusMessage.Text = "Yerel MP4 hazır. Yükleniyor…";
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
            var lifetime = clip.Provider == Provider.Litterbox ? " · Litterbox: 24 saat" : "";
            StatusMessage.Text = (copied ? "Bağlantı panoya kopyalandı" : "Paylaşım bağlantısı hazır") + timing + lifetime;
            if (!IsVisible)
                _tray?.ShowBalloonTip(2500, "RollClip", StatusMessage.Text, Forms.ToolTipIcon.Info);
        }
        else if (clip.Error is not null)
            StatusMessage.Text = clip.Error + (clip.NextAttempt is null ? "" : " Otomatik yeniden denenecek.");
    }

    private void ChooseArchiveFolder_Click(object sender, RoutedEventArgs args)
    {
        var dialog = new OpenFolderDialog { Title = "Drive ile eşitlenen arşiv klasörünü seçin" };
        if (dialog.ShowDialog(this) == true)
        {
            ArchiveFolderBox.Text = dialog.FolderName;
            RefreshArchiveControls();
        }
    }

    private void ClearArchiveFolder_Click(object sender, RoutedEventArgs args)
    {
        ArchiveFolderBox.Text = "";
        RefreshArchiveControls();
    }

    private void RefreshArchiveControls()
    {
        var saved = _settings.ArchiveFolder ?? "";
        var changed = !string.Equals(saved, ArchiveFolderBox.Text, StringComparison.Ordinal);
        ArchiveState.Text = changed ? "Değişikliği uygulamak için ayarları kaydedin."
            : saved.Length > 0 ? "Otomatik kopya açık" : "Otomatik kopya kapalı";
        OpenArchiveButton.IsEnabled = !changed && saved.Length > 0;
        ArchiveExistingButton.IsEnabled = OpenArchiveButton.IsEnabled && !_archiveBulkRunning;
    }

    private void OpenArchiveFolder_Click(object sender, RoutedEventArgs args)
    {
        if (_settings.ArchiveFolder is { } folder)
            OpenPath(folder);
    }

    private async void ArchiveExisting_Click(object sender, RoutedEventArgs args)
    {
        if (_archiveBulkRunning || _store is null || _settings.ArchiveFolder is not { } folder)
            return;
        _archiveBulkRunning = true;
        ArchiveExistingButton.IsEnabled = false;
        try { await BackupClipsAsync(_store.Snapshot(), folder); }
        finally
        {
            _archiveBulkRunning = false;
            RefreshArchiveControls();
        }
    }

    private async Task BackupClipsAsync(IReadOnlyList<Clip> clips, string folder)
    {
        var token = _shutdown.Token;
        try
        {
            await _archiveGate.WaitAsync(token);
            try
            {
                if (_settings.ArchiveFolder != folder)
                    return;
                ArchiveStatus.Text = "Klipler arşiv klasörüne kopyalanıyor…";
                var (copied, existing, failed, reason) = await Task.Run(async () =>
                {
                    var copied = 0;
                    var existing = 0;
                    var failed = 0;
                    string? reason = null;
                    foreach (var clip in clips)
                    {
                        if (Volatile.Read(ref _settings).ArchiveFolder != folder)
                            break;
                        try
                        {
                            var wasCopied = await ClipArchive.CopyAsync(folder, clip, token);
                            if (clip.PendingArchiveFolder == folder)
                                _store?.Change(clip.Id, current => current.PendingArchiveFolder == folder
                                    ? current with { PendingArchiveFolder = null } : null);
                            if (wasCopied)
                                copied++;
                            else
                                existing++;
                        }
                        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException)
                        {
                            failed++;
                            reason ??= error is DirectoryNotFoundException
                                ? "Arşiv klasörünü ve Drive bağlantısını kontrol edin."
                                : error is FileNotFoundException ? "Yerel klip dosyası bulunamadı."
                                : error is UnauthorizedAccessException ? "Dosyaya veya klasöre erişim yok."
                                : "Dosya kopyalanamadı. Boş alanı ve mevcut kopyayı kontrol edin.";
                        }
                    }
                    return (copied, existing, failed, reason);
                }, token);
                if (_settings.ArchiveFolder != folder)
                    return;
                ArchiveStatus.Text = clips.Count == 0 ? "Yedeklenecek klip yok."
                    : $"{copied} kopyalandı · {existing} doğrulandı · {failed} başarısız. {reason}".TrimEnd();
                if (failed > 0)
                    StatusMessage.Text = "Bazı klipler yedeklenemedi. Ayarlardan yeniden deneyebilirsiniz.";
            }
            finally { _archiveGate.Release(); }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
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
                Microphone.IsChecked == true, MotionEnabled.IsChecked == true,
                RecordingQualityChoice.SelectedIndex is >= 0 and <= 2
                    ? (RecordingQuality)RecordingQualityChoice.SelectedIndex : RecordingQuality.High,
                ReplayKeyChoice.SelectedIndex is >= 0 and <= 5 ? ReplayKeyChoice.SelectedIndex + 6 : 8,
                string.IsNullOrWhiteSpace(ArchiveFolderBox.Text) ? null : ArchiveFolderBox.Text);
            AtomicJson.Write(Path.Combine(_root, "settings.json"), settings);
            if (_settings.ArchiveFolder != settings.ArchiveFolder)
                ArchiveStatus.Text = "";
            Volatile.Write(ref _settings, settings);
            RefreshArchiveControls();
            if (!_armed)
                UpdateReplayKeyLabel();
            UpdateInterfaceMotion();
            if (_smokeMarker is null)
                SaveStartupPreference(StartWithWindows.IsChecked == true);
            StatusMessage.Text = _armed ? "Ayarlar kaydedildi. Kayıt ve kısayol ayarları sonraki kayıtta uygulanır."
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

    private static bool ReadAndMigrateStartup()
    {
        using var startup = Registry.CurrentUser.OpenSubKey(StartupKey, writable: true);
        if (startup is null)
            return false;
        if (startup.GetValue(LegacyProductName) is string)
        {
            if (startup.GetValue(ProductName) is not string)
                SetStartupCommand(startup);
            startup.DeleteValue(LegacyProductName, throwOnMissingValue: false);
        }
        return startup.GetValue(ProductName) is string;
    }

    private static void SaveStartupPreference(bool enabled)
    {
        using var startup = Registry.CurrentUser.CreateSubKey(StartupKey);
        if (enabled)
            SetStartupCommand(startup);
        else
            startup.DeleteValue(ProductName, throwOnMissingValue: false);
        startup.DeleteValue(LegacyProductName, throwOnMissingValue: false);
    }

    private static void SetStartupCommand(RegistryKey startup)
    {
        var exe = Environment.ProcessPath ?? throw new IOException("Uygulama yolu bulunamadı.");
        startup.SetValue(ProductName, $"\"{exe}\" --tray");
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
        QuickUploadButton.IsEnabled = UploadButton.IsEnabled;
    }

    private void Open_Click(object sender, RoutedEventArgs args)
    {
        if (SelectedClip is { } clip)
            OpenPath(clip.FilePath);
    }

    private void Folder_Click(object sender, RoutedEventArgs args) => OpenPath(_clipsPath);

    private async void RecoverClips_Click(object sender, RoutedEventArgs args)
    {
        if (_busy || _store is not { } store)
            return;
        if (_armed || _captureBusy)
        {
            StatusMessage.Text = "Klip klasörünü taramak için önce kaydı durdurun.";
            return;
        }
        _busy = _recovering = true;
        ArmButton.IsEnabled = false;
        RecoverClipsButton.IsEnabled = false;
        try
        {
            StatusMessage.Text = "Klip klasörü taranıyor…";
            var added = await Task.Run(() => store.RecoverLocalClips(_clipsPath, _shutdown.Token));
            RefreshLibrary();
            StatusMessage.Text = added == 0 ? "Arşiv dışında yeni klip bulunamadı."
                : $"{added} klip yerel arşive eklendi.";
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            StatusMessage.Text = "Klip klasörü taranamadı. " + error.Message;
        }
        finally
        {
            _busy = _recovering = false;
            ArmButton.IsEnabled = true;
            RecoverClipsButton.IsEnabled = true;
        }
    }

    private void OpenPath(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Win32Exception error) { StatusMessage.Text = "Dosya veya klasör açılamadı. " + error.Message; }
    }

    private void Copy_Click(object sender, RoutedEventArgs args) =>
        StatusMessage.Text = TryCopy(SelectedClip?.ShareUrl) ? "Bağlantı panoya kopyalandı." : "Panoya erişilemedi.";

    private void CopyDiagnostics_Click(object sender, RoutedEventArgs args)
    {
        var version = typeof(MainWindow).Assembly.GetName().Version ?? new Version(0, 0, 0);
        StatusMessage.Text = TryCopy(_diagnostics.BuildReport(version, _hotkeyRegistered, ReplayKeyLabel))
            ? "Tanılama raporu panoya kopyalandı." : "Tanılama raporu kopyalanamadı.";
    }

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
        menu.Items.Add("RollClip'i aç", null, (_, _) => ShowWindow());
        _traySave = menu.Items.Add($"Klip al ({ReplayKeyLabel})", null, async (_, _) => await SaveReplayAsync());
        menu.Items.Add("Çıkış", null, (_, _) => { _exit = true; Close(); });
        _tray = new Forms.NotifyIcon { Icon = System.Drawing.SystemIcons.Application, Text = "RollClip",
            ContextMenuStrip = menu, Visible = true };
        _tray.DoubleClick += (_, _) => ShowWindow();
        RefreshCaptureStatus();
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
        SystemParameters.StaticPropertyChanged -= OnSystemParametersChanged;
        StopInterfaceMotion();
        ChromeMovie.Close();
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
        string? Game = null, bool AutoFallback = false, bool DesktopAudio = true, bool Microphone = false, bool Motion = false,
        RecordingQuality Quality = RecordingQuality.High, int ReplayKey = 8, string? ArchiveFolder = null)
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
        public string? ShareLabel => Clip.ShareUrl is { } url && Clip.Provider == Provider.Litterbox
            ? $"Litterbox · 24 saatlik bağlantı · {url}" : Clip.ShareUrl;
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
