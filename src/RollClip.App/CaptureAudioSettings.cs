using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using NAudio.CoreAudioApi;
using RollClip.Core;

namespace RollClip.App;

public partial class MainWindow
{
    private int _deviceRefresh;
    private CaptureAudio _loadedAudio = new();

    private sealed record InputDevice(string? Id, string Label)
    {
        public override string ToString() => Label;
    }

    private CaptureAudio ReadAudioControls() => new(
        MicrophoneDevice.SelectedItem is InputDevice input ? input.Id : _loadedAudio.MicrophoneId,
        (int)SystemGain.Value, (int)MicrophoneGain.Value);

    private async void RefreshMicrophones_Click(object sender, RoutedEventArgs args) =>
        await LoadAudioControlsAsync(ReadAudioControls());

    private async Task LoadAudioControlsAsync(CaptureAudio? preferences)
    {
        _loadedAudio = preferences ?? new CaptureAudio();
        SystemGain.Value = Math.Clamp(_loadedAudio.SystemPercent, 0, 200);
        MicrophoneGain.Value = Math.Clamp(_loadedAudio.MicrophonePercent, 0, 200);
        var selectedId = _loadedAudio.MicrophoneId;
        var refresh = ++_deviceRefresh;
        var pending = new InputDevice(selectedId, T("Ses aygıtları okunuyor…", "Reading audio devices…"));
        MicrophoneDevice.ItemsSource = new[] { pending };
        MicrophoneDevice.SelectedIndex = 0;
        try
        {
            var active = await Task.Run(() =>
            {
                using var enumerator = new MMDeviceEnumerator();
                var devices = new List<InputDevice>();
                foreach (var endpoint in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
                    using (endpoint)
                        devices.Add(new InputDevice(endpoint.ID, endpoint.FriendlyName));
                return devices;
            }, _shutdown.Token);
            if (refresh != _deviceRefresh || _shutdown.IsCancellationRequested)
                return;
            var available = active.Count;
            active.Insert(0, new InputDevice(null, T("Varsayılan mikrofon", "Default microphone")));
            if (selectedId is not null && !active.Any(item => item.Id == selectedId))
                active.Add(new InputDevice(selectedId, T("Seçilen mikrofon bağlı değil", "Selected microphone disconnected")));
            MicrophoneDevice.ItemsSource = active;
            MicrophoneDevice.SelectedItem = active.First(item => item.Id == selectedId);
            AudioDeviceStatus.Text = available == 0 ? T("Etkin mikrofon bulunamadı.", "No active microphone.") :
                T($"{available} etkin mikrofon", $"{available} active microphone(s)");
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
        catch (Exception error) when (error is COMException or InvalidOperationException)
        {
            Trace.WriteLine(error.Message);
            AudioDeviceStatus.Text = T("Ses aygıtları okunamadı.", "Could not read audio devices.");
        }
    }
}
