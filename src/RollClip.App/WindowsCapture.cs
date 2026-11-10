using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using RollClip.Core;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace RollClip.App;

internal sealed class WindowsCapture : IAsyncDisposable
{
    private ReplayRecorder? _recorder;
    private readonly List<AudioFeed> _audio = [];
    public string Encoder { get; private set; } = "";
    public string Source { get; private set; } = "";
    public string FallbackDetails { get; private set; } = "";
    public (int Width, int Height, int Fps) VideoFormat { get; private set; }
    public string FormatLabel => FormattableString.Invariant(
        $"{VideoFormat.Width}x{VideoFormat.Height} @ {VideoFormat.Fps} FPS");
    public bool SeparateMicrophone { get; private set; }
    public Task Completion => WatchCompletionAsync(_recorder!.Completion, _audio.Select(feed => feed.Failure).ToArray());

    private static async Task WatchCompletionAsync(Task recorder, Task[] failures)
    {
        await await Task.WhenAny(failures.Prepend(recorder));
    }
    public TimeSpan? BufferedDuration => _recorder?.BufferedDuration;

    public async Task StartAsync(nint window, string cache, int seconds, bool desktopAudio, bool microphone,
        CancellationToken cancellationToken, RecordingQuality quality = RecordingQuality.High, bool gameOnlyAudio = false)
    {
        var executable = Path.Combine(AppContext.BaseDirectory, "ffmpeg", "bin", "ffmpeg.exe");
        if (!File.Exists(executable))
            throw new CaptureException("FFmpeg bulunamadı. GitHub Actions'taki tam RollClip ZIP paketini kullanın.");
        if (!IsWindow(window) || IsIconic(window))
            throw new CaptureException("Açık ve küçültülmemiş bir pencere seçin.");

        var errors = new List<string>();
        var handle = unchecked((ulong)(nuint)window).ToString(CultureInfo.InvariantCulture);
        var wgcUnavailable = false;
        foreach (var (graphicsCapture, encoder) in new[]
        {
            (true, "h264_nvenc"), (true, "h264_amf"), (true, "h264_mf"), (false, "h264_mf")
        })
        {
            if (graphicsCapture && wgcUnavailable)
                continue;
            var software = encoder == "h264_mf";
            var (width, height, fps, mbps, maxMbps) = RecordingFormat.Select(quality, software);
            var bitrate = FormattableString.Invariant($"{mbps}M");
            var maxRate = FormattableString.Invariant($"{maxMbps}M");
            var fpsArg = fps.ToString(CultureInfo.InvariantCulture);
            using var startup = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            startup.CancelAfter(TimeSpan.FromSeconds(12));
            try
            {
                if (gameOnlyAudio)
                {
                    var game = new ProcessLoopbackCapture(ProcessId(window));
                    _audio.Add(new AudioFeed(game));
                    await Task.Run(game.StartRecording, startup.Token);
                }
                else if (desktopAudio)
                    _audio.Add(new AudioFeed(new WasapiLoopbackCapture()));
                if (microphone)
                    _audio.Add(new AudioFeed(new WasapiCapture()));

                // Video packets can own raw frames. Keep this queue short independently of audio buffering.
                var inputs = new List<string> { "-thread_queue_size", "8" };
                if (graphicsCapture)
                    inputs.AddRange(["-f", "lavfi", "-i",
                        FormattableString.Invariant(
                            $"gfxcapture=hwnd={handle}:max_framerate={fps}:width={width}:height={height}:resize_mode=scale_aspect:display_border=1")]);
                else
                    inputs.AddRange(["-f", "gdigrab", "-framerate", fpsArg, "-draw_mouse", "0", "-i", $"hwnd={handle}"]);
                foreach (var feed in _audio)
                    inputs.AddRange(feed.Arguments);

                var args = new List<string> { "-map", "0:v:0" };
                if (encoder == "h264_nvenc")
                    args.AddRange(["-c:v", encoder, "-preset", "p4", "-tune", "ll", "-rc", "cbr"]);
                else if (encoder == "h264_amf")
                    args.AddRange(["-c:v", encoder, "-usage", "lowlatency", "-quality", "speed", "-rc", "cbr"]);
                else
                {
                    var filter = graphicsCapture ? "hwdownload,format=bgra,format=nv12"
                        : FormattableString.Invariant(
                            $"scale={width}:{height}:flags=bilinear:force_original_aspect_ratio=decrease:force_divisible_by=2,pad={width}:{height}:(ow-iw)/2:(oh-ih)/2,format=nv12");
                    args.AddRange(["-vf", filter, "-c:v", encoder, "-hw_encoding", "0"]);
                }
                args.AddRange(["-r", fpsArg, "-fps_mode", "cfr", "-b:v", bitrate, "-maxrate", maxRate, "-bufsize", maxRate,
                    "-g", fpsArg, "-bf", "0", "-force_key_frames", "expr:gte(t,n_forced)"]);
                for (var index = 1; index <= _audio.Count; index++)
                    args.AddRange(["-map", FormattableString.Invariant($"{index}:a:0")]);
                if (_audio.Count > 0)
                    args.AddRange(["-c:a", "aac", "-b:a", "160k", "-ar", "48000"]);
                SeparateMicrophone = _audio.Count == 2;

                _recorder = new ReplayRecorder(executable, cache, seconds, inputs, args);
                await _recorder.WaitUntilReadyAsync(startup.Token);
                Source = graphicsCapture ? "Windows Graphics Capture" : "Windows GDI";
                VideoFormat = (width, height, fps);
                Encoder = encoder switch
                {
                    "h264_nvenc" => "NVIDIA NVENC",
                    "h264_amf" => "AMD AMF",
                    _ => "Media Foundation"
                };
                return;
            }
            catch (Exception error) when (error is CaptureException or OperationCanceledException)
            {
                await DisposeAsync();
                cancellationToken.ThrowIfCancellationRequested();
                var details = error is OperationCanceledException
                    ? "İlk kareler zamanında gelmedi. Seçilen pencereyi açık tutun."
                    : error.Message;
                if (graphicsCapture && details.Contains("Failed to create D3D11VA device", StringComparison.Ordinal))
                    wgcUnavailable = true;
                var source = graphicsCapture ? "WGC" : "GDI";
                errors.Add($"{source}/{encoder} · {details}");
                FallbackDetails = string.Join(Environment.NewLine, errors);
            }
            catch
            {
                await DisposeAsync();
                throw;
            }
        }
        throw new CaptureException("Windows kaydı başlatılamadı.\n" + string.Join("\n", errors));
    }

    public (TimeSpan CpuTime, long WorkingSetBytes) SampleRecorderUsage() => _recorder!.SampleProcessUsage();

    public Task<string> SaveAsync(string destination, CancellationToken cancellationToken) =>
        _recorder!.SaveAsync(destination, cancellationToken);

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_recorder is { } recorder)
            {
                _recorder = null;
                await recorder.DisposeAsync();
            }
        }
        finally
        {
            foreach (var feed in _audio)
                await feed.DisposeAsync();
            _audio.Clear();
        }
    }

    public static IReadOnlyList<CaptureWindow> Windows(nint excluded)
    {
        var windows = new List<CaptureWindow>();
        EnumWindows((handle, _) =>
        {
            if (handle == excluded || !IsWindowVisible(handle) || IsIconic(handle))
                return true;
            var title = new StringBuilder(512);
            if (GetWindowText(handle, title, title.Capacity) > 0)
                windows.Add(new CaptureWindow(handle, title.ToString()));
            return true;
        }, 0);
        return windows.OrderBy(window => window.Title, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    internal sealed record CaptureWindow(nint Handle, string Title);

    private static uint ProcessId(nint window)
    {
        GetWindowThreadProcessId(window, out var processId);
        return processId;
    }

    public static string? ProcessName(nint window)
    {
        var processId = window == 0 ? 0 : ProcessId(window);
        if (processId == 0)
            return null;
        try
        {
            using var process = Process.GetProcessById((int)processId);
            return process.ProcessName;
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or Win32Exception)
        {
            return null;
        }
    }

    private sealed class AudioFeed : IAsyncDisposable
    {
        private readonly IWaveIn _capture;
        private readonly NamedPipeServerStream _pipe;
        private readonly BufferedWaveProvider _buffer;
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _pump;

        private readonly TaskCompletionSource _failure = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public AudioFeed(IWaveIn capture)
        {
            _capture = capture;
            try
            {
                var format = capture.WaveFormat;
                var isFloat = format.Encoding == WaveFormatEncoding.IeeeFloat
                    || format is WaveFormatExtensible ext && ext.SubFormat == new Guid("00000003-0000-0010-8000-00aa00389b71");
                var input = (isFloat, format.BitsPerSample) switch
                {
                    (true, 32) => "f32le", (false, 16) => "s16le", (false, 24) => "s24le", (false, 32) => "s32le",
                    _ => throw new CaptureException("Windows ses aygıtının PCM biçimi desteklenmiyor.")
                };
                var name = "rollclip-audio-" + Guid.NewGuid().ToString("N");
                _buffer = new BufferedWaveProvider(format)
                {
                    BufferDuration = TimeSpan.FromMilliseconds(400), DiscardOnBufferOverflow = true, ReadFully = true
                };
                _pipe = new NamedPipeServerStream(name, PipeDirection.Out, 1, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                Arguments = ["-thread_queue_size", "64", "-analyzeduration", "1", "-probesize", "32", "-f", input, "-ar",
                    format.SampleRate.ToString(CultureInfo.InvariantCulture), "-ac",
                    format.Channels.ToString(CultureInfo.InvariantCulture), "-i", @"\\.\pipe\" + name];
                capture.DataAvailable += OnData;
                capture.RecordingStopped += OnRecordingStopped;
                _pump = Task.Run(PumpAsync);
            }
            catch
            {
                capture.Dispose();
                throw;
            }
        }

        public string[] Arguments { get; }
        public Task Failure => _failure.Task;

        private void OnRecordingStopped(object? sender, StoppedEventArgs args)
        {
            if (args.Exception is { } error && !_stop.IsCancellationRequested)
                _failure.TrySetException(new CaptureException("Ses kaynağı durdu. " + error.Message));
        }

        private void OnData(object? sender, WaveInEventArgs args) => _buffer.AddSamples(args.Buffer, 0, args.BytesRecorded);

        private async Task PumpAsync()
        {
            try
            {
                await _pipe.WaitForConnectionAsync(_stop.Token);
                _capture.StartRecording();
                var format = _buffer.WaveFormat;
                var clock = Stopwatch.StartNew();
                var samples = new byte[(format.SampleRate / 50 + 1) * format.BlockAlign];
                long written = 0;
                using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(10));
                while (await timer.WaitForNextTickAsync(_stop.Token))
                {
                    var pending = (long)(clock.Elapsed.TotalSeconds * format.SampleRate) - written;
                    while (pending > 0)
                    {
                        var count = (int)Math.Min(pending, samples.Length / format.BlockAlign);
                        var bytes = count * format.BlockAlign;
                        _buffer.Read(samples, 0, bytes);
                        await _pipe.WriteAsync(samples.AsMemory(0, bytes), _stop.Token);
                        written += count;
                        pending -= count;
                    }
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
            catch (IOException) when (!_pipe.IsConnected || _stop.IsCancellationRequested) { }
            catch (Exception error) when (error is IOException or COMException or InvalidOperationException or TimeoutException)
            {
                if (!_stop.IsCancellationRequested)
                    _failure.TrySetException(new CaptureException("Ses kaynağı başlatılamadı. " + error.Message));
            }
            finally { _pipe.Dispose(); }
        }

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            try { await _pump; }
            catch (Exception error) when (error is IOException or COMException or InvalidOperationException) { }
            finally
            {
                _capture.DataAvailable -= OnData;
                _capture.RecordingStopped -= OnRecordingStopped;
                _capture.Dispose();
                _stop.Dispose();
            }
        }
    }

    private delegate bool EnumWindowsProc(nint handle, nint parameter);

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, nint parameter);
    [DllImport("user32.dll")] private static extern bool IsWindow(nint window);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(nint window, StringBuilder text, int capacity);
}
