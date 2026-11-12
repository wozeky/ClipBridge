using System.IO;
using System.Text.Json;
using System.Windows;
using RollClip.Core;
using Forms = System.Windows.Forms;

namespace RollClip.App;

public partial class MainWindow
{
    private Dictionary<string, CaptureProfile> _gameProfiles = new(StringComparer.OrdinalIgnoreCase);
    private string? _activeGameLabel;
    private string T(string turkish, string english) => EnglishUi.Choose(_settings.English, turkish, english);
    private string GameProfilesPath => Path.Combine(_root, "game-profiles.json");
    private CaptureMethod SelectedCaptureMethod => CaptureMethodChoice.SelectedIndex is >= 0 and <= 3
        ? (CaptureMethod)CaptureMethodChoice.SelectedIndex : CaptureMethod.Auto;
    private bool HasAutomaticGame => _settings.AutoGame || _gameProfiles.Values.Any(profile => profile.AutoStart);

    private void LoadGameProfiles()
    {
        try
        {
            if (AtomicJson.Read<Dictionary<string, CaptureProfile>>(GameProfilesPath) is { } saved)
                _gameProfiles = new(saved, StringComparer.OrdinalIgnoreCase);
            RefreshGameProfiles();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            StatusMessage.Text = EnglishUi.Choose(_settings.English, "Oyun profilleri okunamadı.", "Could not read game profiles.");
        }
    }

    private CaptureProfile ProfileFor(nint window)
    {
        var process = WindowsCapture.ProcessName(window);
        if (process is not null && _gameProfiles.TryGetValue(process, out var profile))
            return profile;
        return new CaptureProfile(_settings.Quality, _settings.ReplaySeconds, _settings.DesktopAudio,
            _settings.Microphone, _settings.GameOnlyAudio, _settings.Method, _settings.Game, Audio: _settings.Audio);
    }

    private bool CanStartAutomatically(nint window)
    {
        var process = WindowsCapture.ProcessName(window);
        if (process is null || CaptureTargets.IsExcluded(process))
            return false;
        if (_gameProfiles.TryGetValue(process, out var profile))
            return profile.AutoStart && profile.Method != CaptureMethod.Dxgi;
        return _settings.Method != CaptureMethod.Dxgi && _settings.AutoGame &&
            string.Equals(process, _settings.AutoGameProcess, StringComparison.OrdinalIgnoreCase);
    }

    private void RefreshGameProfiles(string? selected = null)
    {
        GameProfilesChoice.ItemsSource = _gameProfiles.Keys.Order(StringComparer.CurrentCultureIgnoreCase).ToArray();
        GameProfilesChoice.SelectedItem = selected;
    }

    private void SaveGameProfile_Click(object sender, RoutedEventArgs args)
    {
        var target = CaptureTarget.SelectedItem as WindowsCapture.CaptureWindow;
        var process = WindowsCapture.ProcessName(target?.Handle ?? 0);
        if (process is null || CaptureTargets.IsExcluded(process))
        {
            StatusMessage.Text = EnglishUi.Choose(_settings.English, "Önce ana ekrandan bir oyun penceresi seçin.", "Select a game window on the home screen first.");
            return;
        }
        var quality = RecordingQualityChoice.SelectedIndex is >= 0 and <= 2
            ? (RecordingQuality)RecordingQualityChoice.SelectedIndex : RecordingQuality.High;
        var label = string.IsNullOrWhiteSpace(GameName.Text) ? target!.Title : GameName.Text.Trim();
        var updated = new Dictionary<string, CaptureProfile>(_gameProfiles, StringComparer.OrdinalIgnoreCase)
        {
            [process] = new(quality, ReplayLength.SelectedItem as int? ?? ReplayDurations.Default,
                DesktopAudio.IsChecked == true, Microphone.IsChecked == true, GameOnlyAudio.IsChecked == true,
                SelectedCaptureMethod, label, AutoGame.IsChecked == true, ReadAudioControls())
        };
        StoreGameProfiles(updated, process);
    }

    private void LoadGameProfile_Click(object sender, RoutedEventArgs args)
    {
        if (GameProfilesChoice.SelectedItem is not string process || !_gameProfiles.TryGetValue(process, out var profile))
            return;
        RecordingQualityChoice.SelectedIndex = (int)profile.Quality;
        ReplayLength.SelectedItem = profile.Seconds;
        DesktopAudio.IsChecked = profile.DesktopAudio;
        Microphone.IsChecked = profile.Microphone;
        GameOnlyAudio.IsChecked = profile.GameOnlyAudio;
        AutoGame.IsChecked = profile.AutoStart;
        CaptureMethodChoice.SelectedIndex = (int)profile.Method;
        GameName.Text = profile.Label ?? "";
        _ = LoadAudioControlsAsync(profile.Audio);
        StatusMessage.Text = EnglishUi.Choose(_settings.English, $"Profil yüklendi · {process}", $"Profile loaded · {process}");
    }

    private void RemoveGameProfile_Click(object sender, RoutedEventArgs args)
    {
        if (GameProfilesChoice.SelectedItem is not string process)
            return;
        var updated = new Dictionary<string, CaptureProfile>(_gameProfiles, StringComparer.OrdinalIgnoreCase);
        updated.Remove(process);
        StoreGameProfiles(updated);
    }

    private void StoreGameProfiles(Dictionary<string, CaptureProfile> updated, string? selected = null)
    {
        try
        {
            AtomicJson.Write(GameProfilesPath, updated);
            _gameProfiles = updated;
            RefreshGameProfiles(selected);
            StatusMessage.Text = EnglishUi.Choose(_settings.English, "Oyun profilleri kaydedildi.", "Game profiles saved.");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            StatusMessage.Text = EnglishUi.Choose(_settings.English, "Oyun profilleri kaydedilemedi.", "Could not save game profiles.");
        }
    }

    // Windows sağ alt köşe bildirimleri (balloon/toast) kaldırıldı; durum yalnızca uygulama içinde gösterilir.
    private void NotifyStatus(string message, Forms.ToolTipIcon icon = Forms.ToolTipIcon.Info)
    {
        _ = icon;
        if (!string.IsNullOrWhiteSpace(message))
            StatusMessage.Text = message;
    }

    private void NotifyClipSaved(bool test)
    {
        var text = EnglishUi.Choose(_settings.English, "MP4 hazır. Klip diske kaydedildi.", "MP4 ready. Clip saved to disk.");
        StatusMessage.Text = text;
        if (!test && _settings.SaveSound)
            UiSounds.PlayClipSaved();
    }
}
