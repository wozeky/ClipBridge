using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using ClipBridge.Core;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace ClipBridge.App;

internal sealed class WindowsCapture : IAsyncDisposable
{
    private ReplayRecorder? _recorder;
    private readonly List<AudioFeed> _audio = [];
    public string Encoder { get; private set; } = "";
    public Task Completion => _recorder!.Completion;

    public async Task StartAsync(nint window, string cache, int seconds, bool desktopAudio, bool microphone,
        CancellationToken cancellationToken)
    {
        var executable = Path.Combine(AppContext.BaseDirectory, "ffmpeg", "bin", "ffmpeg.exe");
        if (!File.Exists(executable))
            throw new CaptureException("FFmpeg bulunamadı. GitHub Actions'taki tam ClipBridge ZIP paketini kullanın.");
        if (!IsWindow(window) || IsIconic(window))
            throw new CaptureException("Açık ve küçültülmemiş bir pencere seçin.");

        string? hardwareError = null;
        foreach (var encoder in new[] { "h264_nvenc", "h264_mf" })
        {
            using var startup = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            startup.CancelAfter(TimeSpan.FromSeconds(12));
            try
            {
                if (desktopAudio)
                    _audio.Add(new AudioFeed(new WasapiLoopbackCapture()));
                if (microphone)
                    _audio.Add(new AudioFeed(new WasapiCapture()));

                var handle = unchecked((ulong)(nuint)window).ToString(CultureInfo.InvariantCulture);
                var inputs = new List<string>
                {
                    "-thread_queue_size", "64", "-f", "lavfi",
                    "-i", $"gfxcapture=hwnd={handle}:max_framerate=60:width=1920:height=1080:resize_mode=scale_aspect:display_border=1"
                };
                foreach (var feed in _audio)
                    inputs.AddRange(feed.Arguments);

                var args = new List<string> { "-map", "0:v:0" };
                if (encoder == "h264_nvenc")
                    args.AddRange(["-c:v", encoder, "-preset", "p4", "-tune", "ll", "-rc", "cbr"]);
                else
                    args.AddRange(["-vf", "hwdownload,format=bgra,format=nv12", "-c:v", encoder, "-hw_encoding", "0"]);
                args.AddRange(["-r", "60", "-fps_mode", "cfr", "-b:v", "12M", "-maxrate", "14M", "-bufsize", "14M",
                    "-g", "60", "-bf", "0", "-force_key_frames", "expr:gte(t,n_forced)"]);
                if (_audio.Count == 1)
                    args.AddRange(["-map", "1:a:0", "-c:a", "aac", "-b:a", "160k", "-ar", "48000"]);
                if (_audio.Count == 2)
                    args.AddRange(["-filter_complex", "[1:a][2:a]amix=inputs=2:duration=longest[mix]", "-map", "[mix]",
                        "-c:a", "aac", "-b:a", "160k", "-ar", "48000"]);

                _recorder = new ReplayRecorder(executable, cache, seconds, inputs, args);
                await _recorder.WaitUntilReadyAsync(startup.Token);
                Encoder = encoder == "h264_nvenc" ? "NVIDIA NVENC" : "Media Foundation";
                return;
            }
            catch (Exception error) when (error is CaptureException or OperationCanceledException)
            {
                await DisposeAsync();
                cancellationToken.ThrowIfCancellationRequested();
                var details = error is OperationCanceledException
                    ? "İlk kareler zamanında gelmedi. Seçilen pencereyi açık tutun."
                    : error.Message;
                if (hardwareError is not null)
                    throw new CaptureException($"Windows kaydı başlatılamadı. NVENC: {hardwareError} Media Foundation: {details}");
                hardwareError = details;
            }
            catch
            {
                await DisposeAsync();
                throw;
            }
        }
    }

    public Task<string> SaveAsync(string destination, CancellationToken cancellationToken) =>
        _recorder!.SaveAsync(destination, cancellationToken);

    public async ValueTask DisposeAsync()
    {
        if (_recorder is { } recorder)
        {
            _recorder = null;
            await recorder.DisposeAsync();
        }
        foreach (var feed in _audio)
            await feed.DisposeAsync();
        _audio.Clear();
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

    private sealed class AudioFeed : IAsyncDisposable
    {
        private readonly WasapiCapture _capture;
        private readonly NamedPipeServerStream _pipe;
        private readonly BufferedWaveProvider _buffer;
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _pump;

        public AudioFeed(WasapiCapture capture)
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
                var name = "clipbridge-audio-" + Guid.NewGuid().ToString("N");
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
                _pump = Task.Run(PumpAsync);
            }
            catch
            {
                capture.Dispose();
                throw;
            }
        }

        public string[] Arguments { get; }

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
                _capture.Dispose();
                _stop.Dispose();
            }
        }
    }

    private delegate bool EnumWindowsProc(nint handle, nint parameter);

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, nint parameter);
    [DllImport("user32.dll")] private static extern bool IsWindow(nint window);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(nint window, StringBuilder text, int capacity);
}
