using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using RollClip.Core;

namespace RollClip.App;

public partial class MainWindow
{
    private const string ReleasePage = "https://github.com/wozeky/RollClip/releases";
    private static readonly Version InstalledVersion = typeof(MainWindow).Assembly.GetName().Version ?? new Version(0, 0, 0);
    private bool _checkingUpdates;
    private string? _availableTag;
    private DateTimeOffset _nextUpdateCheck;

    private async void CheckUpdate_Click(object sender, RoutedEventArgs args) => await CheckForUpdateAsync(manual: true);

    private void OpenRelease_Click(object sender, RoutedEventArgs args)
    {
        var url = _availableTag is null ? ReleasePage : ReleasePage + "/tag/" + Uri.EscapeDataString(_availableTag);
        OpenPath(url);
    }

    private async Task CheckForUpdateAsync(bool manual)
    {
        if (_checkingUpdates || _shutdown.IsCancellationRequested)
            return;
        _nextUpdateCheck = DateTimeOffset.UtcNow.AddHours(8);
        _checkingUpdates = true;
        CheckUpdateButton.IsEnabled = false;
        if (manual)
            UpdateStatus.Text = T("GitHub sürümleri kontrol ediliyor…", "Checking GitHub releases…");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/repos/wozeky/RollClip/releases?per_page=50");
            request.Headers.UserAgent.ParseAdd("RollClip/" + InstalledVersion.ToString(3));
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            response.EnsureSuccessStatusCode();
            await response.Content.LoadIntoBufferAsync(2_000_000, timeout.Token);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
            var includePreviews = manual ? IncludePreviews.IsChecked == true : _settings.IncludePreviews;
            var available = ReleaseFeed.LatestNewerTag(document.RootElement, InstalledVersion, includePreviews);
            var changed = _availableTag != available;
            _availableTag = available;
            UpdateStatus.Text = available is null ? T("Daha yeni sürüm bulunamadı.", "No newer release found.") :
                T($"Yeni sürüm hazır · {available}", $"New release available · {available}");
            if (available is not null && (changed || manual))
                NotifyStatus(UpdateStatus.Text);
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
        catch (Exception error) when (error is HttpRequestException or OperationCanceledException or JsonException or
            InvalidOperationException or KeyNotFoundException or System.IO.IOException)
        {
            Trace.WriteLine(error.Message);
            UpdateStatus.Text = T("Şu anda güncelleme kontrol edilemiyor.", "Could not check for updates right now.");
        }
        finally
        {
            _checkingUpdates = false;
            CheckUpdateButton.IsEnabled = true;
        }
    }
}
