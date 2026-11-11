using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using RollClip.Core;

namespace RollClip.App;

public partial class DiscordShareDialog : Window
{
    private sealed record Channel(string Name, string Secret);
    private readonly string _path = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RollClip", "discord-channels.json");
    private List<Channel> _channels = [];
    private readonly bool _english;
    internal Uri? Endpoint { get; private set; }

    internal DiscordShareDialog(bool english)
    {
        InitializeComponent();
        _english = english;
        if (english)
        {
            Title = Heading.Text = "Send to Discord";
            Description.Text = "Choose a server channel. The file is posted under the channel webhook's identity.";
            Send.Content = "Send MP4";
            Remove.Content = "Remove channel";
            AddLabel.Text = "Add channel";
            Help.Text = "Discord → Server Settings → Integrations → Webhooks. Create a webhook for the channel and enter its URL below.";
            NameLabel.Text = "Channel name (example: My server · #clips)";
            AddressLabel.Text = "Discord webhook URL";
            Add.Content = "Save channel";
            DirectHelp.Text = "For a friend or group DM, copy the file and finish sending it in Discord.";
            Direct.Content = "Open Discord and copy MP4";
            Cancel.Content = "Cancel";
        }

        try
        {
            _channels = AtomicJson.Read<List<Channel>>(_path) ?? [];
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            Status.Text = T("Kanal listesi okunamadı.", "Channel list could not be read.");
            Add.IsEnabled = false;
        }
        Refresh();
    }

    private string T(string tr, string en) => EnglishUi.Choose(_english, tr, en);

    private void Refresh()
    {
        Channels.ItemsSource = null;
        Channels.ItemsSource = _channels;
        Channels.SelectedIndex = _channels.Count - 1;
    }

    private void ChannelChanged(object sender, SelectionChangedEventArgs args) =>
        Send.IsEnabled = Remove.IsEnabled = Channels.SelectedItem is Channel;

    private void Add_Click(object sender, RoutedEventArgs args)
    {
        var name = ChannelName.Text.Trim();
        if (name.Length == 0 || !DiscordWebhook.TryReadAddress(Address.Password, out var endpoint))
        {
            Status.Text = T("Kanal adını ve geçerli bir Discord webhook adresini gir.", "Enter a channel name and a valid Discord webhook URL.");
            return;
        }
        if (_channels.Count >= 30 || _channels.Any(item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            Status.Text = T("Farklı bir kanal adı kullan veya bir kayıt kaldır.", "Use a different channel name or remove an existing entry.");
            return;
        }
        try
        {
            var bytes = Encoding.UTF8.GetBytes(endpoint!.AbsoluteUri);
            try
            {
                var secret = Convert.ToBase64String(ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser));
                Save([.. _channels, new Channel(name, secret)]);
            }
            finally { CryptographicOperations.ZeroMemory(bytes); }
            ChannelName.Clear();
            Address.Clear();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or CryptographicException)
        {
            Status.Text = T("Kanal bilgisi saklanamadı.", "Channel details could not be saved.");
        }
    }

    private void Remove_Click(object sender, RoutedEventArgs args)
    {
        if (Channels.SelectedItem is not Channel channel)
            return;
        try { Save(_channels.Where(item => item != channel).ToList()); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Status.Text = T("Kanal kaydı kaldırılamadı.", "Channel entry could not be removed.");
        }
    }

    private void Save(List<Channel> channels)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        AtomicJson.Write(_path, channels);
        _channels = channels;
        Status.Text = "";
        Refresh();
    }

    private void Send_Click(object sender, RoutedEventArgs args)
    {
        if (Channels.SelectedItem is not Channel channel)
            return;
        try
        {
            var bytes = ProtectedData.Unprotect(Convert.FromBase64String(channel.Secret), null, DataProtectionScope.CurrentUser);
            try
            {
                if (DiscordWebhook.TryReadAddress(Encoding.UTF8.GetString(bytes), out var endpoint))
                {
                    Endpoint = endpoint;
                    DialogResult = true;
                    return;
                }
            }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        catch (Exception error) when (error is CryptographicException or FormatException)
        {
            // The stored URL is private and is never shown in an error.
        }
        Status.Text = T("Kanal adresi açılamadı. Kanalı kaldırıp yeniden ekle.", "Channel address could not be opened. Remove the channel and add it again.");
    }

    private void Direct_Click(object sender, RoutedEventArgs args) => DialogResult = true;
}
