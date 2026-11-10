using System.Diagnostics;
using System.IO;
using System.Media;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace RollClip.App;

public static class UiSounds
{
    public static readonly DependencyProperty CueProperty = DependencyProperty.RegisterAttached(
        "Cue", typeof(string), typeof(UiSounds), new PropertyMetadata(null));

    public static string? GetCue(DependencyObject element) => (string?)element.GetValue(CueProperty);
    public static void SetCue(DependencyObject element, string value) => element.SetValue(CueProperty, value);

    private const int CacheLimit = 128;
    private static readonly Dictionary<string, SoundPlayer> Players = new(StringComparer.Ordinal);
    private static bool _available;
    internal static bool Enabled { get; set; } = true;

    internal static void Register()
    {
        _available = true;
        EventManager.RegisterClassHandler(typeof(ButtonBase), ButtonBase.ClickEvent, new RoutedEventHandler(ButtonClicked), true);
        RegisterPointer(typeof(ComboBox));
        RegisterPointer(typeof(ComboBoxItem));
        RegisterPointer(typeof(ListBoxItem));
        RegisterPointer(typeof(Slider));
        RegisterPointer(typeof(ScrollBar));
        foreach (var type in new[] { typeof(ComboBox), typeof(ListBox), typeof(Slider), typeof(ScrollBar) })
            EventManager.RegisterClassHandler(type, Keyboard.PreviewKeyDownEvent, new KeyEventHandler(KeyboardAction), true);
    }

    private static void RegisterPointer(Type type) =>
        EventManager.RegisterClassHandler(type, UIElement.PreviewMouseLeftButtonDownEvent, new MouseButtonEventHandler(PointerAction), true);

    private static void ButtonClicked(object sender, RoutedEventArgs args)
    {
        if (sender is not ButtonBase button || args.OriginalSource != button || !button.IsEnabled
            || button is RepeatButton || button.TemplatedParent is ComboBox)
            return;

        var state = button is ToggleButton toggle ? (toggle.IsChecked == true ? "/on" : "/off") : "";
        Play("button/" + Identity(button) + state);
    }

    private static void PointerAction(object sender, MouseButtonEventArgs args)
    {
        if (sender is not Control { IsEnabled: true } control)
            return;
        switch (control)
        {
            case ComboBox { IsDropDownOpen: true }:
                return;
            case ListBoxItem or ComboBoxItem:
                for (var child = args.OriginalSource as DependencyObject; child is Visual && child != control;
                     child = VisualTreeHelper.GetParent(child))
                    if (child is ButtonBase)
                        return;
                if (ItemsControl.ItemsControlFromItemContainer(control) is { } owner)
                    Play("option/" + Identity(owner) + "/" + owner.ItemContainerGenerator.IndexFromContainer(control));
                return;
            case ScrollBar { TemplatedParent: Slider }:
                return;
        }
        Play("adjust/" + Identity(control));
    }

    private static void KeyboardAction(object sender, KeyEventArgs args)
    {
        var key = args.Key == Key.System ? args.SystemKey : args.Key;
        if (sender is not Control { IsEnabled: true } control || args.IsRepeat)
            return;
        if (key is Key.Left or Key.Right or Key.Up or Key.Down or Key.Home or Key.End or Key.PageUp or Key.PageDown
            || control is ComboBox && key == Key.F4
            || control is ComboBox { IsDropDownOpen: true } && key is Key.Enter or Key.Escape)
            Play("adjust/" + Identity(control));
    }

    private static string Identity(FrameworkElement element) => GetCue(element)
        ?? (string.IsNullOrEmpty(element.Name) ? element.GetType().Name : element.Name);

    internal static void Play(string cue)
    {
        if (!_available || !Enabled)
            return;
        try
        {
            if (!Players.TryGetValue(cue, out var player))
            {
                if (Players.Count == CacheLimit)
                {
                    var oldest = Players.First();
                    oldest.Value.Stop();
                    oldest.Value.Stream?.Dispose();
                    oldest.Value.Dispose();
                    Players.Remove(oldest.Key);
                }
                var wave = CreateWave(cue);
                player = new SoundPlayer(wave);
                try { player.Load(); }
                catch
                {
                    player.Dispose();
                    wave.Dispose();
                    throw;
                }
                Players.Add(cue, player);
            }
            player.Play();
        }
        catch (Exception error) when (error is InvalidOperationException or TimeoutException or IOException
                                      or System.ComponentModel.Win32Exception)
        {
            Debug.WriteLine($"RollClip UI sound: {error.Message}");
        }
    }

    private static MemoryStream CreateWave(string cue)
    {
        uint hash = 2166136261;
        foreach (var character in cue)
            hash = unchecked((hash ^ character) * 16777619);
        const int rate = 22050;
        var length = rate * (42 + (int)(hash % 22)) / 1000;
        var first = 320 + hash % 451;
        var second = first * (cue.EndsWith("/off", StringComparison.Ordinal) ? .71 : 1.18 + (hash >> 16) % 7 * .08);
        var wave = new MemoryStream(44 + length * 2);
        using var writer = new BinaryWriter(wave, System.Text.Encoding.ASCII, leaveOpen: true);
        writer.Write("RIFF"u8);
        writer.Write(36 + length * 2);
        writer.Write("WAVEfmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(rate);
        writer.Write(rate * 2);
        writer.Write((short)2);
        writer.Write((short)16);
        writer.Write("data"u8);
        writer.Write(length * 2);
        for (var i = 0; i < length; i++)
        {
            var seconds = (double)i / rate;
            var fraction = (double)i / length;
            var envelope = Math.Min(1, seconds * 350) * Math.Pow(1 - fraction, 2);
            var note = .72 * Math.Sin(Math.Tau * first * seconds) + .28 * Math.Sin(Math.Tau * second * seconds);
            writer.Write((short)(note * envelope * 3000));
        }
        wave.Position = 0;
        return wave;
    }

    internal static void VerifyWaves()
    {
        using var on = CreateWave("button/InterfaceSounds/on");
        using var off = CreateWave("button/InterfaceSounds/off");
        using var player = new SoundPlayer(on);
        player.Load();
        if (on.ToArray().AsSpan().SequenceEqual(off.ToArray()))
            throw new InvalidOperationException("UI sound variants are identical.");
    }

    internal static void Dispose()
    {
        foreach (var player in Players.Values)
        {
            player.Stop();
            player.Stream?.Dispose();
            player.Dispose();
        }
        Players.Clear();
        _available = false;
    }
}
