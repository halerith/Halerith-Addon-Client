using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using HalerithAddonClient.Core;
using HalerithAddonClient.Core.Platform;

namespace HalerithAddonClient.Windows;

public sealed class ClientUpgradeCountdownWindow : Window
{
    private readonly CancellationTokenSource _cancel = new();
    private readonly TextBlock _countdown;
    private readonly Button _cancelButton;

    public ClientUpgradeCountdownWindow(string version)
    {
        Title = "Halerith Addon Client update";
        Width = 440;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        AppWindowIcon.Apply(this);

        _countdown = new TextBlock { FontSize = 15, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };
        _cancelButton = new Button { Content = "Cancel", MinWidth = 90, HorizontalAlignment = HorizontalAlignment.Right };
        _cancelButton.Click += (_, _) => Cancel();
        Content = new StackPanel
        {
            Margin = new Thickness(24),
            Spacing = 16,
            Children =
            {
                new TextBlock
                {
                    Text = $"Halerith Addon Client {version} is available."
                        + (PlatformServices.Current.Updates.UsesExternalUpdateFeed ? "" : " The update has been downloaded and is ready to install."),
                    FontSize = 14,
                    TextWrapping = TextWrapping.Wrap,
                },
                _countdown,
                _cancelButton,
            },
        };
        KeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            e.Handled = true;
            Cancel();
        };
        Closed += (_, _) => _cancel.Cancel();
    }

    private void Cancel()
    {
        _cancel.Cancel();
        Close(false);
    }

    protected override async void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        _cancelButton.Focus();
        var proceed = await ClientUpgradeCountdown.RunAsync(seconds =>
        {
            var action = PlatformServices.Current.Updates.UsesExternalUpdateFeed
                ? "Opening the macOS updater" : "Launching upgrade";
            _countdown.Text = $"{action} in {seconds} second{(seconds == 1 ? "" : "s")}…";
        }, _cancel.Token);
        if (proceed && IsVisible) Close(true);
    }

    public static async Task<bool> ConfirmAsync(Window owner, string version, CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested || !owner.IsVisible) return false;
        var window = new ClientUpgradeCountdownWindow(version);
        using var registration = cancellationToken.Register(() => Dispatcher.UIThread.Post(() =>
        {
            if (window.IsVisible) window.Cancel();
        }));
        return await window.ShowDialog<bool>(owner) && !cancellationToken.IsCancellationRequested && owner.IsVisible;
    }
}
