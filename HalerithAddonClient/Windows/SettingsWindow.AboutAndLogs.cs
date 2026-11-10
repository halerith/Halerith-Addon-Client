using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using HalerithAddonClient;
using HalerithAddonClient.Controls;
using HalerithAddonClient.Core;
using HalerithAddonClient.ViewModels;

namespace HalerithAddonClient.Windows;

public partial class SettingsWindow
{
    private bool _dataRequestOpening;
    // ══════════════════════════════════════════════════════════════════════
    //  About tab — hyperlink navigation
    // ══════════════════════════════════════════════════════════════════════

    private void HalerithLink_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo { FileName = "https://halerith.com", UseShellExecute = true }); }
        catch { }
    }

    private void PrivacyPolicyLink_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo { FileName = "https://halerith.com/privacy/", UseShellExecute = true }); }
        catch { }
    }

    private async void DataRequestLink_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_dataRequestOpening) return;
        _dataRequestOpening = true;
        DataRequestStatusText.IsVisible = true;
        DataRequestStatusText.Text = "Preparing your verified data request...";
        try
        {
            using var client = new PrivacyPortalClient();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(120));
            var url = await client.CreatePortalUrlAsync(timeout.Token);
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps
                || uri.Host != "halerith.com" || uri.AbsolutePath != "/privacy/data-request/")
                throw new InvalidOperationException("The verified data link is unavailable. Please try again or contact privacy@halerith.com.");
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
            DataRequestStatusText.IsVisible = false;
        }
        catch (Exception ex)
        {
            DataRequestStatusText.Text = ex is InvalidOperationException ? ex.Message : "The verified data link is unavailable. Please try again or contact privacy@halerith.com.";
        }
        finally { _dataRequestOpening = false; }
    }

    /// <summary>
    /// Opens a read-only EULA overlay on top of the SettingsWindow.
    /// The Accept/Decline buttons still work but are informational.
    /// </summary>
    private void ViewEula_Click(object? sender, RoutedEventArgs e)
    {
        var overlay = EulaOverlay.CreateReadOnly();

        if (Content is Grid rootGrid)
        {
            Grid.SetRowSpan(overlay, rootGrid.RowDefinitions.Count);
            rootGrid.Children.Add(overlay);
        }

        overlay.Closed += (_, _) =>
        {
            if (overlay.Parent is Panel panel)
                panel.Children.Remove(overlay);
        };
    }

    // ══════════════════════════════════════════════════════════════════════
    //  Application tab — Submit Logs
    // ══════════════════════════════════════════════════════════════════════

    private async void SubmitLogs_Click(object? sender, RoutedEventArgs e)
    {
        var instanceRoot = System.IO.Path.Combine(LauncherPaths.GetAppDataDirectory(), "Runtime", "instances");
        static bool ValidId(string id) => !string.IsNullOrWhiteSpace(id)
            && id is not ("." or "..")
            && id.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) < 0
            && !id.Contains('/') && !id.Contains('\\');
        var instances = _vm.Config.EnsureManagerScope().Servers
            .Where(s => ValidId(s.ServerPublicId))
            .Select(s => new GameLogInstanceOption(s.ServerPublicId,
                "Server - " + (string.IsNullOrWhiteSpace(s.ServerName) ? s.ServerPublicId : s.ServerName),
                System.IO.Path.Combine(instanceRoot, s.ServerPublicId)))
            .Concat(_vm.Config.LocalInstances
                .Where(i => ValidId(i.Id))
                .Select(i => new GameLogInstanceOption(i.Id,
                    "Local - " + (string.IsNullOrWhiteSpace(i.Name) ? i.Id : i.Name),
                    System.IO.Path.Combine(instanceRoot, i.Id))))
            .Where(i => System.IO.Directory.Exists(System.IO.Path.Combine(i.Directory, "logs"))
                || System.IO.Directory.Exists(System.IO.Path.Combine(i.Directory, "crash-reports")))
            .ToList();
        var preferredId = _sessionServerPublicId ?? _vm.Config.DefaultInstanceId;
        var detailsDialog = new HalerithAddonClient.LogSubmitDialog(instances, preferredId);
        var result = await detailsDialog.ShowDialog<bool?>(this);
        if (result != true) return;

        var userNotes = detailsDialog.Notes;
        var attachmentPath = detailsDialog.AttachmentPath;
        var gameLogDirectory = detailsDialog.GameLogDirectory;

        if (_submitLogsButton != null)
            _submitLogsButton.IsEnabled = false;

        if (_submitLogsStatus != null)
            _submitLogsStatus.Text = "Collecting diagnostic logs...";

        DiagnosticPackageWorkspace? package = null;
        try
        {
            package = new DiagnosticPackageWorkspace();

            await Task.Run(() =>
            {
                try
                {
                    var eventLogText = EventLogger.ExportRecentEntries(hours: 24);
                    package.WriteGeneratedText("diagnostics.txt", eventLogText);
                }
                catch { }
            });

            if (gameLogDirectory is not null)
            {
                if (_submitLogsStatus != null)
                    _submitLogsStatus.Text = "Collecting selected Minecraft logs...";
                await Task.Run(() => package.AddGameLogs(gameLogDirectory));
            }

            try
            {
                package.WriteGeneratedText("halerith-services.txt", _vm.ServiceTrustDiagnosticsText);
            }
            catch { }

            if (_submitLogsStatus != null)
                _submitLogsStatus.Text = "Collecting system summary...";

            await Task.Run(() =>
            {
                try
                {
                    var output = HalerithAddonClient.Core.Platform.PlatformServices.Current.Diagnostics
                        .GetSystemSummaryAsync()
                        .GetAwaiter()
                        .GetResult();
                    package.WriteGeneratedText("sysinfo.txt", output);
                }
                catch { }
            });

            if (_submitLogsStatus != null)
                _submitLogsStatus.Text = "Compressing logs…";

            await Task.Run(() =>
            {
                package.CreateZip(attachmentPath);
            });

            if (_submitLogsStatus != null)
                _submitLogsStatus.Text = "Uploading…";

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

            var uploadResult = await DiagnosticUploadClient.UploadAsync(
                package.ZipPath, package.ZipName, userNotes, cts.Token);

            if (_submitLogsStatus != null)
            {
                _submitLogsStatus.Text = uploadResult.Success
                    ? "Logs submitted successfully."
                    : $"Upload failed: {uploadResult.ErrorMessage ?? "unknown error"}";
            }

            if (uploadResult.Success)
                LauncherLog.Write("Diagnostic logs submitted.", EventIds.Client.DiagnosticSubmitted);
        }
        catch (Exception ex)
        {
            if (_submitLogsStatus != null)
                _submitLogsStatus.Text = $"Failed: {ex.Message}";
        }
        finally
        {
            if (_submitLogsButton != null)
                _submitLogsButton.IsEnabled = true;

            package?.Dispose();
        }
    }

    // ══════════════════════════════════════════════════════════════════════
    //  Footer status helpers
    // ══════════════════════════════════════════════════════════════════════

    private void SetFooterStatus(string message, IBrush brush, bool clickable = false)
    {
        _copyFeedbackCts?.Cancel();
        _copyFeedbackCts = null;
        _footerStatusText = message;

        if (_footerStatusLabel != null)
        {
            _footerStatusLabel.Text = message;
            _footerStatusLabel.Foreground = brush;
            _footerStatusLabel.Cursor = clickable
                ? new Cursor(StandardCursorType.Hand)
                : new Cursor(StandardCursorType.Arrow);
        }
    }

    private void SetInfoStatus(string message) => SetFooterStatus(message, AppTheme.TextMuted);
    private void SetSuccessStatus(string message) => SetFooterStatus(message, AppTheme.StatusOk);
    private void SetErrorStatus(string message) => SetFooterStatus(message, AppTheme.StatusError, clickable: true);

    private async void FooterStatus_Click(object? sender, PointerReleasedEventArgs e)
    {
        var text = _footerStatusText;
        if (string.IsNullOrWhiteSpace(text)) return;

        try
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard != null)
                await clipboard.SetTextAsync(text);
        }
        catch
        {
            return;
        }

        _copyFeedbackCts?.Cancel();
        var cts = new CancellationTokenSource();
        _copyFeedbackCts = cts;

        if (_footerStatusLabel != null)
        {
            _footerStatusLabel.Text = "Copied to clipboard";
            _footerStatusLabel.Foreground = AppTheme.StatusIdle;
            _footerStatusLabel.Cursor = new Cursor(StandardCursorType.Arrow);
        }

        try { await Task.Delay(1000, cts.Token); }
        catch (OperationCanceledException) { return; }

        if (_footerStatusLabel != null)
        {
            _footerStatusLabel.Text = text;
            _footerStatusLabel.Foreground = AppTheme.StatusError;
            _footerStatusLabel.Cursor = new Cursor(StandardCursorType.Hand);
        }

        _copyFeedbackCts = null;
    }
}
