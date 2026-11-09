using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace RollClip.App;

public partial class MainWindow
{
    private readonly bool _materialSmoke = Environment.GetCommandLineArgs().Contains("--material-smoke");

    private async void OnMaterialSmokeRendered(object? sender, EventArgs args)
    {
        ContentRendered -= OnMaterialSmokeRendered;
        try
        {
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            ChromeMovie.MediaOpened += (_, _) => ready.TrySetResult();
            ChromeMovie.MediaFailed += (_, error) => ready.TrySetException(error.ErrorException);
            ChromeMovie.Source = new Uri(Path.Combine(AppContext.BaseDirectory, "Assets", "ChromeLoop.mp4"));
            ChromeMovie.Pause();
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(15));
            if (!ChromeMovie.HasVideo || !ChromeMovie.NaturalDuration.HasTimeSpan
                || ChromeMovie.NaturalDuration.TimeSpan.TotalSeconds < 9.9)
                throw new InvalidOperationException("The material decoder did not open the 10-second loop.");
            Width = 1000;
            Height = 750;
            UpdateLayout();
            using var process = Process.GetCurrentProcess();
            await Task.Delay(700);
            var before = MaterialPixels();
            ExportMaterialScreen("first");
            // Keep expensive software screenshot renders outside the usage sample.
            await Task.Delay(200);
            process.Refresh();
            var started = Stopwatch.GetTimestamp();
            var cpu = process.TotalProcessorTime;
            await Task.Delay(2200);
            process.Refresh();
            var elapsed = Stopwatch.GetElapsedTime(started).TotalSeconds;
            var machineCpu = (process.TotalProcessorTime - cpu).TotalSeconds / elapsed / Environment.ProcessorCount;
            var activeMemory = process.WorkingSet64 / 1048576d;
            var after = MaterialPixels();
            ExportMaterialScreen("second");
            var changed = before.Zip(after).Count(pair => Math.Abs(pair.First - pair.Second) > 12);
            if (changed < 150)
                throw new InvalidOperationException("Media position advanced but rendered chrome pixels did not change.");
            StopInterfaceMotion();
            await Task.Delay(150);
            var position = ChromeMovie.Position;
            await Task.Delay(400);
            if (_moviePlaying || Math.Abs((ChromeMovie.Position-position).TotalMilliseconds) > 150 || HomeGlow.HasAnimatedProperties)
                throw new InvalidOperationException($"Decorative animation did not pause: playing={_moviePlaying}, delta={(ChromeMovie.Position-position).TotalMilliseconds:F0}ms, pulse={HomeGlow.HasAnimatedProperties}.");
            process.Refresh();
            var pausedStart = process.TotalProcessorTime;
            var pausedAt = Stopwatch.GetTimestamp();
            await Task.Delay(1200);
            process.Refresh();
            var pausedCpu = (process.TotalProcessorTime-pausedStart).TotalSeconds / Stopwatch.GetElapsedTime(pausedAt).TotalSeconds / Environment.ProcessorCount;
            File.WriteAllText(_smokeMarker!, FormattableString.Invariant(
                $"PASS: video decoded, {changed} material bytes changed, pause stopped playback and pulse. Hosted sample {elapsed:F1}s: CPU {machineCpu:P1}, working set {activeMemory:F1} MiB, paused CPU {pausedCpu:P1}; WPF tier {RenderCapability.Tier >> 16}. Not a game/GPU benchmark."));
            Application.Current.Shutdown();
        }
        catch (Exception error)
        {
            File.WriteAllText(_smokeMarker!, "Material probe failed: " + error);
            Application.Current.Shutdown(1);
        }
    }

    private byte[] MaterialPixels()
    {
        var bitmap = new RenderTargetBitmap(480, 360, 38.4, 38.4, PixelFormats.Pbgra32);
        bitmap.Render(ChromeScene);
        var bytes = new byte[480 * 360 * 4];
        bitmap.CopyPixels(bytes, 480 * 4, 0);
        return bytes;
    }

    private void ExportMaterialScreen(string suffix)
    {
        var bitmap = new RenderTargetBitmap((int)RootSurface.ActualWidth, (int)RootSurface.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(RootSurface);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(Path.ChangeExtension(_smokeMarker!, suffix + ".png"));
        encoder.Save(output);
    }
}
