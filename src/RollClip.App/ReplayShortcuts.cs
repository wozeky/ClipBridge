using System.Windows;
using RollClip.Core;

namespace RollClip.App;

public partial class MainWindow
{
    private const int MaxShortcuts = 8;
    private readonly Dictionary<int, ReplayShortcut> _activeShortcuts = [];
    private readonly List<ReplayShortcut> _shortcutDraft = [];
    private int _activeDefaultDuration = ReplayDurations.Default;

    private sealed record ReplayShortcut(int Key, bool Ctrl, bool Shift, int Seconds)
    {
        public uint Modifiers => 0x4000u | (Ctrl ? 2u : 0u) | (Shift ? 4u : 0u);
        public string Label => (Ctrl ? "Ctrl+" : "") + (Shift ? "Shift+" : "") +
            (Key >= 0x70 ? $"F{Key - 0x6F}" : ((char)Key).ToString());
        public override string ToString() => $"{Label} → {Seconds} s";
    }

    private sealed record KeyOption(int Code)
    {
        public override string ToString() => Code >= 0x70 ? $"F{Code - 0x6F}" : ((char)Code).ToString();
    }

    private void InitializeShortcuts()
    {
        ShortcutKey.ItemsSource = Enumerable.Range(0x75, 7).Concat(Enumerable.Range('A', 26)).Select(code => new KeyOption(code)).ToArray();
        ShortcutLength.ItemsSource = ReplayDurations.Options;
        _shortcutDraft.AddRange((_settings.Shortcuts ?? [])
            .Where(item => item is not null && ReplayDurations.Options.Contains(item.Seconds) &&
                (item.Key is >= 0x75 and <= 0x7B || ((item.Ctrl || item.Shift) && item.Key is >= 'A' and <= 'Z')))
            .Take(MaxShortcuts));
        RefreshShortcutList(-1);
    }

    private void RefreshShortcutList(int selected)
    {
        ShortcutList.ItemsSource = _shortcutDraft.ToArray();
        ShortcutList.SelectedIndex = selected;
        if (selected < 0)
        {
            ShortcutKey.SelectedIndex = 0;
            ShortcutLength.SelectedItem = ReplayDurations.Default;
            ShortcutCtrl.IsChecked = ShortcutShift.IsChecked = false;
        }
    }

    private void ShortcutList_Changed(object sender, RoutedEventArgs args)
    {
        if (ShortcutList.SelectedItem is not ReplayShortcut item)
            return;
        ShortcutKey.SelectedItem = ShortcutKey.Items.OfType<KeyOption>().FirstOrDefault(key => key.Code == item.Key);
        ShortcutLength.SelectedItem = item.Seconds;
        ShortcutCtrl.IsChecked = item.Ctrl;
        ShortcutShift.IsChecked = item.Shift;
    }

    private void NewShortcut_Click(object sender, RoutedEventArgs args) => RefreshShortcutList(-1);

    private void ApplyShortcut_Click(object sender, RoutedEventArgs args)
    {
        if (ShortcutKey.SelectedItem is not KeyOption key || ShortcutLength.SelectedItem is not int seconds)
            return;
        var item = new ReplayShortcut(key.Code, ShortcutCtrl.IsChecked == true, ShortcutShift.IsChecked == true, seconds);
        var index = ShortcutList.SelectedIndex;
        var primary = ReplayKeyChoice.SelectedIndex is >= 0 and <= 5 ? 0x75 + ReplayKeyChoice.SelectedIndex : 0x77;
        if ((!item.Ctrl && !item.Shift && (item.Key < 0x70 || item.Key == primary)) ||
            _shortcutDraft.Where((_, position) => position != index).Any(other => other.Key == item.Key && other.Modifiers == item.Modifiers))
        {
            ShortcutStatus.Text = T("Tuş çakışıyor veya harf için Ctrl/Shift gerekli.", "Shortcut conflicts or the letter requires Ctrl/Shift.");
            return;
        }
        if (index < 0)
        {
            if (_shortcutDraft.Count >= MaxShortcuts)
            {
                ShortcutStatus.Text = T("En fazla 8 ek kısayol ekleyebilirsiniz.", "You can add up to 8 extra shortcuts.");
                return;
            }
            index = _shortcutDraft.Count;
            _shortcutDraft.Add(item);
        }
        else
            _shortcutDraft[index] = item;
        RefreshShortcutList(index);
        ShortcutStatus.Text = T("Değişiklik için Ayarları kaydet’e basın.", "Click Save settings to keep the change.");
    }

    private void RemoveShortcut_Click(object sender, RoutedEventArgs args)
    {
        if (ShortcutList.SelectedIndex < 0)
            return;
        _shortcutDraft.RemoveAt(ShortcutList.SelectedIndex);
        RefreshShortcutList(-1);
        ShortcutStatus.Text = T("Değişiklik için Ayarları kaydet’e basın.", "Click Save settings to keep the change.");
    }

    private void RegisterExtraShortcuts(bool armed)
    {
        if (_windowSource is not { } source)
            return;
        foreach (var id in _activeShortcuts.Keys)
            UnregisterHotKey(source.Handle, id);
        _activeShortcuts.Clear();
        if (!armed)
            return;
        var failed = new List<string>();
        var idToRegister = ReplayHotkey + 1;
        foreach (var item in _settings.Shortcuts ?? [])
        {
            if (RegisterHotKey(source.Handle, idToRegister, item.Modifiers, (uint)item.Key))
                _activeShortcuts.Add(idToRegister++, item);
            else
                failed.Add(item.Label);
        }
        ShortcutStatus.Text = failed.Count == 0 ? "" : T("Kullanılamayan kısayollar: ", "Unavailable shortcuts: ") + string.Join(", ", failed);
    }
}
