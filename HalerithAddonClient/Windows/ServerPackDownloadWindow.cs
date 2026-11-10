using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using HalerithAddonClient.Core;

namespace HalerithAddonClient.Windows;

public sealed class ServerPackDownloadWindow : Window
{
    public string? InstalledPath { get; private set; }

    public ServerPackDownloadWindow(SyncAction action, RuntimeConfig config, Func<bool> stillCurrent)
    {
        Title = "Manual Download";
        Width = 620; Height = 350; CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
        SystemDecorations = SystemDecorations.Full;
        Background = Application.Current?.FindResource("SurfaceBrush") as IBrush
            ?? new SolidColorBrush(Color.FromRgb(0x1A, 0x1D, 0x27));
        var name = action.ManifestMod?.Name ?? action.PackManifestItem?.Name ?? "Pack content";
        var filename = action.ManifestMod?.DownloadFilename ?? action.PackManifestItem?.DownloadFilename ?? "";
        var page = ServerPackManualImport.DownloadPage(action);
        var primary = Application.Current?.FindResource("TextPrimaryBrush") as IBrush ?? Brushes.White;
        var secondary = Application.Current?.FindResource("TextSecondaryBrush") as IBrush ?? Brushes.LightGray;
        var accent = Application.Current?.FindResource("AccentBlueBrush") as IBrush ?? Brushes.DodgerBlue;
        var panel = new StackPanel { Margin = new Thickness(24), Spacing = 14 };
        panel.Children.Add(new TextBlock { Text = "Manual Download", FontSize = 18, FontWeight = FontWeight.SemiBold, Foreground = primary });
        panel.Children.Add(new TextBlock { Text = name, TextWrapping = TextWrapping.Wrap, Foreground = primary });
        panel.Children.Add(new TextBlock { Text = "Download the exact file from CurseForge, then select it here.", TextWrapping = TextWrapping.Wrap, Foreground = secondary });
        panel.Children.Add(new TextBlock { Text = filename, TextWrapping = TextWrapping.Wrap, FontWeight = FontWeight.SemiBold, Foreground = primary });
        var open = new Button { Content = "Download from CurseForge", IsEnabled = page is not null, HorizontalAlignment = HorizontalAlignment.Left };
        var browse = new Button { Content = "Install", HorizontalAlignment = HorizontalAlignment.Left };
        browse.Classes.Add("accent");
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        actions.Children.Add(open); actions.Children.Add(browse);
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = secondary };
        var note = new TextBlock { Text = "Third-party downloads for this item have been disabled by its developer on CurseForge.",
            FontSize = 12, TextWrapping = TextWrapping.Wrap, Foreground = secondary };
        var help = new TextBlock { Text = "About CurseForge project distribution", FontSize = 12,
            Foreground = accent, TextDecorations = TextDecorations.Underline,
            Cursor = new Cursor(StandardCursorType.Hand) };
        var close = new Button { Content = "Close", HorizontalAlignment = HorizontalAlignment.Right };
        panel.Children.Add(actions); panel.Children.Add(status); panel.Children.Add(note); panel.Children.Add(help); panel.Children.Add(close);
        Content = new ScrollViewer { Content = panel };
        var busy = false;
        Closing += (_, e) => { if (busy) e.Cancel = true; };
        close.Click += (_, _) => Close(false);
        open.Click += (_, _) =>
        {
            try { if (page is not null) System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(page.ToString()) { UseShellExecute = true }); }
            catch (Exception error) { status.Text = error.Message; }
        };
        help.PointerPressed += (_, _) =>
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                "https://support.curseforge.com/support/solutions/articles/9000207877-project-distribution-toggle") { UseShellExecute = true }); }
            catch (Exception error) { status.Text = error.Message; }
        };
        browse.Click += async (_, _) =>
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "Select the downloaded CurseForge file", AllowMultiple = false });
            if (files.FirstOrDefault()?.TryGetLocalPath() is not { } path) return;
            busy = true; browse.IsEnabled = open.IsEnabled = close.IsEnabled = false;
            status.Text = "Verifying and installing…";
            try
            {
                if (!stillCurrent()) throw new InvalidOperationException("The selected server or pack changed. Refresh and try again.");
                InstalledPath = await ServerPackManualImport.ImportAsync(action, config, path);
                busy = false; Close(true);
            }
            catch (Exception error) { status.Text = "File not installed. " + error.Message; }
            finally { busy = false; browse.IsEnabled = open.IsEnabled = close.IsEnabled = true; }
        };
    }
}
