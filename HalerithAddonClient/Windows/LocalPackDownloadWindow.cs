using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using HalerithAddonClient.Core;
using HalerithAddonClient.Core.LocalModpacks;

namespace HalerithAddonClient.Windows;

public sealed class LocalPackDownloadWindow : Window
{
    public LocalPackDownloadWindow(LocalInstance instance, LocalPackMember member)
    {
        Title = "Download pack content"; Width = 620; Height = 380; MinWidth = 440; MinHeight = 320;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
        var panel = new StackPanel { Margin = new Thickness(24), Spacing = 14 };
        panel.Children.Add(new TextBlock { Text = member.Project.Name, FontSize = 18, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(new TextBlock { Text = "Download the exact file below from the provider, then select it here. The Client verifies it before installing.", TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(new TextBlock { Text = member.Version.Filename, TextWrapping = TextWrapping.Wrap, FontWeight = FontWeight.SemiBold });
        var open = new Button { Content = member.Project.Source == "curseforge" ? "Open CurseForge download page" : "Open Modrinth download page" };
        var browse = new Button { Content = "Browse for downloaded file…" };
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var hasVerifiedMetadata = member.Path.Length > 0 && !string.IsNullOrWhiteSpace(member.Version.FileHash);
        browse.IsEnabled = hasVerifiedMetadata;
        if (!hasVerifiedMetadata)
            status.Text = member.Project.Source == "curseforge"
                ? "The file page can be opened from its pack IDs, but the Client needs a CurseForge API key to obtain the filename and hash before it can verify a selected file."
                : "The Client needs verified file metadata before it can install a selected file.";
        var close = new Button { Content = "Close", HorizontalAlignment = HorizontalAlignment.Right };
        panel.Children.Add(open); panel.Children.Add(browse); panel.Children.Add(status); panel.Children.Add(close);
        Content = new ScrollViewer { Content = panel };
        var busy = false;
        Closing += (_, e) => { if (busy) e.Cancel = true; };
        close.Click += (_, _) => Close(false);
        open.Click += (_, _) =>
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(LocalPackInstaller.DownloadPage(member).ToString()) { UseShellExecute = true }); }
            catch (Exception error) { status.Text = error.Message; }
        };
        browse.Click += async (_, _) =>
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "Select the downloaded pack file", AllowMultiple = false });
            if (files.FirstOrDefault()?.TryGetLocalPath() is not { } path) return;
            busy = true; browse.IsEnabled = open.IsEnabled = close.IsEnabled = false; status.Text = "Verifying and installing…";
            try
            {
                await Task.Run(() => new LocalPackInstaller().ImportMemberAsync(instance.Id, instance.GetInstanceDirectory(), member, path));
                busy = false; Close(true);
            }
            catch (Exception error) { status.Text = "File not installed. " + error.Message; }
            finally { busy = false; browse.IsEnabled = open.IsEnabled = close.IsEnabled = true; }
        };
    }
}
