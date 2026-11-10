using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Media.Imaging;

namespace HalerithAddonClient.Windows;

public partial class AboutWindow : Window
{
    private static readonly string[] IconResourceNames =
    [
        "HalerithAddonClient.HalerithAddonClient.png",
        "HalerithAddonClient.HalerithAddonClient.ico",
    ];

    public AboutWindow(string clientVersion)
    {
        InitializeComponent();
        AppWindowIcon.Apply(this);
        _versionText.Text = $"Version {clientVersion}";
        LoadIconImage();
    }

    private void LoadIconImage()
    {
        foreach (var resourceName in IconResourceNames)
        {
            try
            {
                using var stream = typeof(AboutWindow).Assembly.GetManifestResourceStream(resourceName);
                if (stream is null)
                    continue;

                _appIconImage.Source = new Bitmap(stream);
                return;
            }
            catch
            {
                // Try the next embedded icon format.
            }
        }
    }

    private void Website_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => OpenUrl("https://halerith.com");

    private void Privacy_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => OpenUrl("https://halerith.com/privacy/");

    private static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true,
            });
        }
        catch
        {
            // Link navigation is best-effort.
        }
    }
}
