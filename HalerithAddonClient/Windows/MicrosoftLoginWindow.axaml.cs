using Avalonia.Controls;
using Avalonia.Input;
using HalerithAddonClient.Core;

namespace HalerithAddonClient.Windows;

/// <summary>
/// Modal dialog that runs the Microsoft OAuth device code flow.
///
/// Usage:
///   var loginWindow = new MicrosoftLoginWindow();
///   var account = await loginWindow.ShowDialog&lt;MinecraftAccount?&gt;(ownerWindow);
///   // account is null if the user closed the window or an error occurred.
/// </summary>
public partial class MicrosoftLoginWindow : Window
{
    private CancellationTokenSource? _cts;

    public MicrosoftLoginWindow()
    {
        InitializeComponent();
    }

    protected override async void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        await RunDeviceCodeFlowAsync();
    }

    protected override void OnClosed(EventArgs e)
    {
        _cts?.Cancel();
        _cts?.Dispose();
        base.OnClosed(e);
    }

    // ══════════════════════════════════════════════════════════════════════
    //  Device code flow
    // ══════════════════════════════════════════════════════════════════════

    private async Task RunDeviceCodeFlowAsync()
    {
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        try
        {
            using var authService = new MicrosoftAuthService();

            // Step 1: Request device code
            _statusText.Text = "Requesting login code…";
            var deviceCode = await authService.RequestDeviceCodeAsync(ct);

            // Display the code and URL
            _codeText.Text = deviceCode.UserCode;
            _urlText.Text = deviceCode.VerificationUri;
            _statusText.Text = "Waiting for you to enter the code…";

            // Step 2: Poll for token
            var msToken = await authService.PollForTokenAsync(deviceCode, ct);

            // Steps 3–7: Exchange chain
            _statusText.Text = "Authenticating with Xbox Live…";
            var account = await authService.ExchangeAsync(msToken, ct);

            _statusText.Text = $"Signed in as {DemoMode.PlayerName(account.Gamertag)}";

            // Persist the account
            AccountStore.Save(account);

            // Brief pause so the user sees the success message
            await Task.Delay(800, ct);

            Close(account);
        }
        catch (OperationCanceledException)
        {
            // User closed the window — no action needed
        }
        catch (TimeoutException ex)
        {
            ShowError(ex.Message);
        }
        catch (Exception ex)
        {
            ShowError($"Login failed: {ex.Message}");
        }
    }

    private void ShowError(string message)
    {
        _statusText.Text = "";
        _errorText.Text = message;
        _errorText.IsVisible = true;
    }

    // ══════════════════════════════════════════════════════════════════════
    //  UI event handlers
    // ══════════════════════════════════════════════════════════════════════

    private async void CopyButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(_codeText.Text);
            _copyButton.Content = "Copied!";
            await Task.Delay(1500);
            _copyButton.Content = "Copy Code";
        }
    }

    private void UrlText_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var url = _urlText.Text;
        if (string.IsNullOrWhiteSpace(url)) return;

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true,
            });
        }
        catch { /* non-critical */ }
    }
}
