using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace ClipBridge.Core;

public sealed class CaptureException(string message) : Exception(message);

public sealed class ReplayRecorder : IAsyncDisposable
{
    private readonly string _ffmpeg;
    private readonly string _directory;
    private readonly Process _process;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Queue<Segment> _segments = new();
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task _readSegments;
    private readonly Task<string> _errors;
    private readonly int _seconds;
    private long _lastSegment;

    public ReplayRecorder(string ffmpeg, string cache, int seconds, IEnumerable<string> inputs,
        IEnumerable<string> encoding)
    {
        _ffmpeg = ffmpeg;
        _seconds = seconds;
        _directory = Directory.CreateDirectory(Path.Combine(cache, Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            _process = Launch(ffmpeg, _directory,
                ["-hide_banner", "-loglevel", "error", "-nostdin", ..inputs, ..encoding,
                "-f", "segment", "-segment_format", "mpegts", "-segment_time", "1", "-segment_time_delta", "0.009",
                "-reset_timestamps", "1", "-segment_list", "pipe:1", "-segment_list_type", "csv", "part%08d.ts"],
                stdout: true);
        }
        catch
        {
            Directory.Delete(_directory);
            throw;
        }
        _errors = Task.Run(() => ReadErrorsAsync(_process));
        _readSegments = Task.Run(IndexSegmentsAsync);
    }

    public Task Completion => _readSegments;

    public Task WaitUntilReadyAsync(CancellationToken cancellationToken) => _ready.Task.WaitAsync(cancellationToken);

    private async Task IndexSegmentsAsync()
    {
        try
        {
            while (await _process.StandardOutput.ReadLineAsync() is { } line)
            {
                var segment = Segment.Parse(line);
                await _gate.WaitAsync();
                try
                {
                    _lastSegment = Stopwatch.GetTimestamp();
                    _segments.Enqueue(segment);
                    while (_segments.TryPeek(out var first) && first.End <= segment.End - _seconds + 0.05)
                    {
                        File.Delete(Path.Combine(_directory, first.File));
                        _segments.Dequeue();
                    }
                }
                finally { _gate.Release(); }
                _ready.TrySetResult();
            }
            await _process.WaitForExitAsync();
            throw new CaptureException(await _errors is { Length: > 0 } error ? error : "Windows görüntü akışı durdu.");
        }
        catch (Exception error)
        {
            _ready.TrySetException(error);
            throw;
        }
    }

    public async Task<string> SaveAsync(string destination, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_segments.Count == 0)
                throw new CaptureException("İlk kayıt parçasının tamamlanmasını bekleyin.");
            if (Stopwatch.GetElapsedTime(_lastSegment) > TimeSpan.FromSeconds(4))
                throw new CaptureException("Pencereden yeni görüntü gelmiyor. Pencereyi geri açın.");
            var manifest = new StringBuilder("ffconcat version 1.0\n");
            foreach (var part in _segments)
            {
                manifest.Append("file ").Append(part.File).Append('\n');
                manifest.Append("duration ").Append((part.End - part.Start).ToString("F6", CultureInfo.InvariantCulture))
                    .Append('\n');
            }
            await File.WriteAllTextAsync(Path.Combine(_directory, "clip.ffconcat"), manifest.ToString(), cancellationToken);

            var temporary = destination + ".partial";
            try
            {
                using var mux = Launch(_ffmpeg, _directory,
                    ["-hide_banner", "-loglevel", "error", "-nostdin", "-n", "-f", "concat", "-safe", "1",
                    "-i", "clip.ffconcat", "-map", "0:v:0", "-map", "0:a?", "-c", "copy",
                    "-movflags", "+faststart", "-f", "mp4", temporary], stdout: false);
                var errors = ReadErrorsAsync(mux);
                try { await mux.WaitForExitAsync(cancellationToken); }
                catch (OperationCanceledException)
                {
                    await TerminateAsync(mux);
                    throw;
                }
                if (mux.ExitCode != 0 || Mp4.ReadDuration(temporary) is null)
                    throw new CaptureException("MP4 kaydedilemedi. " + await errors);
                File.Move(temporary, destination);
                return destination;
            }
            finally
            {
                if (File.Exists(temporary))
                    File.Delete(temporary);
            }
        }
        finally { _gate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        await TerminateAsync(_process);
        try { await _readSegments; }
        catch (Exception error) when (error is CaptureException or IOException or UnauthorizedAccessException) { }
        try { await _ready.Task; }
        catch (Exception error) when (error is CaptureException or IOException or UnauthorizedAccessException) { }
        _process.Dispose();
        await _gate.WaitAsync();
        try { Directory.Delete(_directory, recursive: true); }
        finally { _gate.Release(); }
    }

    private static Process Launch(string executable, string directory, IEnumerable<string> arguments, bool stdout)
    {
        var info = new ProcessStartInfo(executable)
        {
            WorkingDirectory = directory, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = stdout, RedirectStandardError = true
        };
        foreach (var argument in arguments)
            info.ArgumentList.Add(argument);
        return Process.Start(info) ?? throw new CaptureException("FFmpeg başlatılamadı.");
    }

    private static async Task TerminateAsync(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) when (process.HasExited) { }
        await process.WaitForExitAsync();
    }

    private static async Task<string> ReadErrorsAsync(Process process)
    {
        var messages = new Queue<string>();
        while (await process.StandardError.ReadLineAsync() is { } line)
            if (!string.IsNullOrWhiteSpace(line))
            {
                messages.Enqueue(line.Length <= 400 ? line : line[..400]);
                if (messages.Count > 9)
                    messages.Dequeue();
            }
        return string.Join(Environment.NewLine, messages);
    }

    private sealed record Segment(string File, double Start, double End)
    {
        public static Segment Parse(string line)
        {
            var fields = line.Split(',');
            if (fields is not [var file, var start, var end] || file.Length != 15 ||
                !file.StartsWith("part", StringComparison.Ordinal) || !file.EndsWith(".ts", StringComparison.Ordinal) ||
                !int.TryParse(file.AsSpan(4, 8), NumberStyles.None, CultureInfo.InvariantCulture, out _) ||
                !double.TryParse(start, CultureInfo.InvariantCulture, out var startTime) ||
                !double.TryParse(end, CultureInfo.InvariantCulture, out var endTime) ||
                !double.IsFinite(startTime) || !double.IsFinite(endTime) || endTime <= startTime)
                throw new CaptureException("Kayıt parçasının zaman bilgisi okunamadı.");
            return new Segment(file, startTime, endTime);
        }
    }
}
