using System.Collections.Specialized;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Documents;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using HalerithAddonClient.Core;
using HalerithAddonClient.Core.ContentSearch;
using HalerithAddonClient.Controls;
using HalerithAddonClient.Services;
using HalerithAddonClient.ViewModels;

namespace HalerithAddonClient.Windows;

/// <summary>
/// Code-behind for the main application window.
///
/// Responsibilities limited to:
///   • creating and binding <see cref="MainViewModel"/>;
///   • wiring UI-layer actions that require a Window reference;
///   • loading the window icon.
///
/// </summary>
public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private readonly string        _clientVersion;
    private readonly string?       _preferredServerPublicId;
    private bool _startupFallbackUpdateChecked;
    private HalerithSelfUpdateRunner.PreparedUpdate? _preparedUpdate;
    private bool _upgradeInProgress;

    private readonly DispatcherTimer _serverMotdTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private bool _serverMotdPaused;
    private static readonly Regex MotdUrlRegex = new(@"https?://[^\s<>""]+", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // ══════════════════════════════════════════════════════════════════════
    //  Construction
    // ══════════════════════════════════════════════════════════════════════

    public MainWindow(string clientVersion, string? preferredServerPublicId = null)
    {
        _clientVersion = clientVersion;
        _preferredServerPublicId = preferredServerPublicId;

        _vm = new MainViewModel();
        _vm.ReviewModpackFilesAsync = (review, token) => ModpackFileReviewDialog.ShowReviewAsync(this, review, token);
        DataContext = _vm;

        _vm.RequestManagedAccessCheck += OnRequestManagedAccessCheck;
        _vm.RequestMediumConfidenceUpdateConfirmation += OnRequestMediumConfidenceUpdateConfirmation;

        // When all remote servers disappear after an authentication reset, promote
        // the first local instance so the main view stays populated.
        _vm.RequestSelectFirstLocalInstance += OnRequestSelectFirstLocalInstance;

        // Auto-scroll the console whenever a new line is added.
        _vm.ConsoleLines.CollectionChanged += OnConsoleLinesChanged;
        _serverMotdTimer.Tick += ServerMotdTimer_Tick;

        // Update launch button color and account selector when server selection changes.
        _vm.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(MainViewModel.SelectedServer))
            {
                UpdateLaunchButtonState();

                // When a remote server is selected, deselect local instance
                // and switch back to server content.
                if (_vm.SelectedServer is not null)
                {
                    _localInstanceListBox.SelectedItem = null;
                    ShowServerContent();
                }
                else if (_selectedLocalInstance is null)
                {
                    RefreshAccountSelector();
                }

                RefreshServerMotdTimer();
            }
            else if (args.PropertyName == nameof(MainViewModel.IsLaunchEnabled))
            {
                UpdateLaunchButtonState();
            }
            else if (args.PropertyName == nameof(MainViewModel.IsServerMotdVisible))
            {
                RenderServerMotdText();
                RefreshServerMotdTimer();
            }
            else if (args.PropertyName == nameof(MainViewModel.ServerMotdText))
            {
                RenderServerMotdText();
            }
            else if (args.PropertyName == nameof(MainViewModel.IsSyncing))
            {
                if (!_vm.IsSyncing)
                    UpdateAllServerRuntimeBuildStates();
                UpdateLaunchButtonState();
            }
        };

        InitializeComponent();
        Title = "Halerith Addon Client";
        UpdateLaunchButtonState();
        RefreshAccountSelector();
        RenderServerMotdText();

        // Launch button color class is set by UpdateLaunchButtonClass()
        // based on the selected server's installer (success or curseforge).

        AppWindowIcon.Apply(this);
    }

    private void OnRequestSelectFirstLocalInstance(object? sender, EventArgs e)
    {
        if (_localInstanceListBox.ItemsSource is IEnumerable<LocalInstanceItemViewModel> items)
        {
            var first = items.FirstOrDefault();
            if (first is not null)
            {
                _localInstanceListBox.SelectedItem = first;
                return;
            }
        }

        _localInstanceListBox.SelectedItem = null;
    }

    private async void OnRequestMediumConfidenceUpdateConfirmation(
        object? sender,
        (IReadOnlyList<SyncAction> Actions, TaskCompletionSource<IReadOnlyList<SyncAction>?> Tcs) e)
    {
        try
        {
            var dialog = new MediumConfidenceUpdateDialog(e.Actions);
            var result = await dialog.ShowDialog<bool?>(this);
            e.Tcs.TrySetResult(result == true ? dialog.SelectedActions : null);
        }
        catch (Exception ex)
        {
            e.Tcs.TrySetException(ex);
        }
    }

    // ══════════════════════════════════════════════════════════════════════
    //  Lifetime events
    // ══════════════════════════════════════════════════════════════════════

    protected override async void OnOpened(EventArgs e)
    {
        base.OnOpened(e);

        using var eulaClient = new HalerithEulaClient();
        var requiredEula = await eulaClient.ResolveRequiredClientDocumentAsync();
        await eulaClient.ReconcileDeletedAcceptanceAsync(requiredEula);

        // ── EULA gate: block until accepted, close if declined ────
        if (!EulaStore.IsAcceptedForDocument(requiredEula))
        {
            bool accepted = await ShowEulaOverlayAsync(requiredEula);
            if (!accepted)
            {
                Close();
                return;
            }
        }

        // Best-effort sync for both newly-accepted and previously-accepted
        // local EULA state. This backfills server-side acceptance records for
        // clients that accepted before remote EULA tracking existed.
        var acceptedEula = EulaStore.GetAcceptedDocumentForUpload(requiredEula);
        if (acceptedEula is not null)
            await eulaClient.PushClientAcceptanceAsync(acceptedEula);

        _ = PreparePostEulaUpdateAsync();

        // Load the server list then, once a server is selected, kick off the
        // first content refresh.  Both operations are kicked off here so they
        // run after the window is fully rendered (avoids visual jank).
        _ = InitialLoadAsync();
        StartGuidedTips();
    }

    private async Task PreparePostEulaUpdateAsync()
    {
        if (_startupFallbackUpdateChecked)
            return;

        _startupFallbackUpdateChecked = true;

        try
        {
            var appConfig = await Task.Run(AppConfigStore.LoadAppConfig);
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
            var prepared = await HalerithSelfUpdateRunner.TryPrepareAsync(
                _clientVersion,
                appConfig,
                relaunchArgs: null,
                FileLogger.Info,
                cts.Token);

            if (prepared is null)
                return;

            _preparedUpdate = prepared;
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                _upgradeButton.Content = $"Upgrade to {prepared.Version}";
                _upgradeButton.IsVisible = true;
                _upgradeButton.IsEnabled = true;
            });
            await StartPreparedUpgradeAsync();
        }
        catch (Exception ex)
        {
            FileLogger.Warn($"Post-EULA Halerith update check failed: {ex.Message}");
        }
    }

    private async void UpgradeButton_Click(object? sender, RoutedEventArgs e)
        => await StartPreparedUpgradeAsync();

    private async Task StartPreparedUpgradeAsync()
    {
        if (_preparedUpdate is null || _upgradeInProgress || !IsVisible)
            return;

        var usesSparkle = HalerithAddonClient.Core.Platform.PlatformServices.Current.Updates.UsesExternalUpdateFeed;
        _upgradeInProgress = true;
        _upgradeButton.IsEnabled = false;

        try
        {
            if (!await ClientUpgradeCountdownWindow.ConfirmAsync(this, _preparedUpdate.Version))
                return;

            _upgradeButton.Content = "Upgrading...";
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
            var started = await HalerithSelfUpdateRunner.InstallPreparedAsync(_preparedUpdate, cts.Token);
            if (!started)
                return;

            FileLogger.Info($"Halerith client upgrade started for {_preparedUpdate.Version}.");

            if (usesSparkle)
            {
                // Sparkle owns and presents its native update session. Keep the
                // Halerith window available and let the user initiate another
                // check if the native updater is dismissed.
                return;
            }

            await Task.Delay(500);
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                desktop.Shutdown();
        }
        catch (Exception ex)
        {
            FileLogger.Warn($"Halerith client upgrade failed: {ex}");

            await new MessageBoxWindow(
                "Upgrade Failed",
                $"The Halerith Addon Client upgrade could not be started.\n\n{ex.Message}",
                MessageBoxButton.OK,
                MessageBoxImage.Error).ShowDialog(this);
        }
        finally
        {
            RestoreUpgradeButton();
        }
    }

    private void RestoreUpgradeButton()
    {
        _upgradeInProgress = false;
        _upgradeButton.IsEnabled = true;
        _upgradeButton.Content = _preparedUpdate is null
            ? "Upgrade"
            : $"Upgrade to {_preparedUpdate.Version}";
    }

    /// <summary>
    /// Shows the EULA overlay on top of the main window and waits for user
    /// action.  Returns <see langword="true"/> if accepted.
    /// </summary>
    private Task<bool> ShowEulaOverlayAsync(EulaDocument document)
    {
        var tcs = new TaskCompletionSource<bool>();
        var overlay = new EulaOverlay(document);

        // The MainWindow root content is a Grid — add the overlay on top.
        if (Content is Grid rootGrid)
        {
            // Span all columns so the overlay covers the entire window.
            Grid.SetColumnSpan(overlay, rootGrid.ColumnDefinitions.Count);
            rootGrid.Children.Add(overlay);
        }

        overlay.Accepted += (_, _) =>
        {
            EulaStore.RecordAcceptance(document);
            if (overlay.Parent is Panel panel)
                panel.Children.Remove(overlay);
            tcs.TrySetResult(true);
        };

        overlay.Declined += (_, _) =>
        {
            if (overlay.Parent is Panel panel)
                panel.Children.Remove(overlay);
            tcs.TrySetResult(false);
        };

        return tcs.Task;
    }

    // ══════════════════════════════════════════════════════════════════════
    //  Activity console auto-scroll
    // ══════════════════════════════════════════════════════════════════════

    private void OnConsoleLinesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action != NotifyCollectionChangedAction.Add) return;
        // Both consoles (server + local) share the same ConsoleLines collection.
        // Scroll whichever is currently visible to the bottom.
        if (_consoleListBox.Parent is ScrollViewer sv)
            sv.ScrollToEnd();
        if (_localConsoleListBox.Parent is ScrollViewer lsv)
            lsv.ScrollToEnd();
    }

    // ── Console context-menu handlers ──────────────────────────────────

    private async void ConsoleCopyLine_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is not MenuItem mi) return;

        var line = mi.CommandParameter as string
            ?? ((mi.Parent as ContextMenu)?.PlacementTarget as TextBlock)?.Text
            ?? "";

        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is not null)
            await clipboard.SetTextAsync(line);
    }

    private async void ConsoleCopyAll_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var allText = string.Join(Environment.NewLine, _vm.ConsoleLines);

        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is not null)
            await clipboard.SetTextAsync(allText);
    }

    private void RefreshServerMotdTimer()
    {
        _serverMotdTimer.Stop();
        _serverMotdPaused = false;

        if (_vm.IsServerMotdVisible && _vm.SelectedServer is not null)
            _serverMotdTimer.Start();
    }

    private void ServerMotdTimer_Tick(object? sender, EventArgs e)
    {
        if (_serverMotdPaused)
            return;

        _vm.TickSelectedServerMotd();
        if (!_vm.IsServerMotdVisible)
            _serverMotdTimer.Stop();
    }

    private void ServerMotdStrip_PointerEntered(object? sender, Avalonia.Input.PointerEventArgs e)
    {
        _serverMotdPaused = true;
        _serverMotdTimer.Stop();
    }

    private void ServerMotdStrip_PointerExited(object? sender, Avalonia.Input.PointerEventArgs e)
    {
        _serverMotdPaused = false;
        if (_vm.IsServerMotdVisible && _vm.SelectedServer is not null)
            _serverMotdTimer.Start();
    }

    private void ServerMotdStrip_GotFocus(object? sender, RoutedEventArgs e)
    {
        _serverMotdPaused = true;
        _serverMotdTimer.Stop();
    }

    private void ServerMotdStrip_LostFocus(object? sender, RoutedEventArgs e)
    {
        _serverMotdPaused = false;
        if (_vm.IsServerMotdVisible && _vm.SelectedServer is not null)
            _serverMotdTimer.Start();
    }

    private void ServerMotdClose_Click(object? sender, RoutedEventArgs e)
    {
        _serverMotdTimer.Stop();
        _vm.HideServerMotd();
    }

    private void ServerMotdUrl_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { Tag: string url } || string.IsNullOrWhiteSpace(url))
            return;

        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo(url)
            {
                UseShellExecute = true,
            };
            System.Diagnostics.Process.Start(psi);
        }
        catch (Exception ex)
        {
            _vm.AppendConsole($"Could not open MOTD link: {ex.Message}");
        }
    }

    private void RenderServerMotdText()
    {
        if (_serverMotdTextBlock is null)
            return;

        _serverMotdTextBlock.Text = "";
        _serverMotdTextBlock.Inlines = new InlineCollection();
        var inlines = _serverMotdTextBlock.Inlines;

        var text = _vm.ServerMotdText;
        if (string.IsNullOrWhiteSpace(text))
            return;

        var index = 0;
        foreach (Match match in MotdUrlRegex.Matches(text))
        {
            if (match.Index > index)
                inlines.Add(text[index..match.Index]);

            var matchedText = match.Value;
            var url = matchedText.TrimEnd('.', ',', ';', ')', ']');
            var trailing = matchedText[url.Length..];

            var link = new HyperlinkButton
            {
                Content = url,
                Tag = url,
            };
            link.Classes.Add("motd-inline-link");
            link.Click += ServerMotdUrl_Click;
            inlines.Add(link);

            if (!string.IsNullOrEmpty(trailing))
                inlines.Add(trailing);

            index = match.Index + match.Length;
        }

        if (index < text.Length)
            inlines.Add(text[index..]);
    }

    private void ContentProjectLinkButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string url } || string.IsNullOrWhiteSpace(url))
            return;

        try
        {
            var uri = HttpsUrlPolicy.RequireHttpOrHttpsAbsoluteUri(url, "Project URL");
            var psi = new System.Diagnostics.ProcessStartInfo(uri.ToString()) { UseShellExecute = true };
            System.Diagnostics.Process.Start(psi);
            e.Handled = true;
        }
        catch (Exception ex)
        {
            _vm.AppendConsole($"Could not open item link: {ex.Message}");
        }
    }

    private async Task InitialLoadAsync()
    {
        // Populate local instances immediately so they're visible while
        // the remote servers are still loading their metadata.
        PopulateLocalInstances();
        await RefreshMinecraftSessionsOnStartupAsync();
        RefreshAccountHeadsOnStartup();

        await _vm.LoadServersAsync();
        ApplyConfiguredDefaultInstanceSelection();
        if (!string.IsNullOrWhiteSpace(_preferredServerPublicId))
        {
            var preferred = _vm.Servers.FirstOrDefault(server =>
                string.Equals(server.ServerPublicId, _preferredServerPublicId, StringComparison.OrdinalIgnoreCase));
            if (preferred is not null)
            {
                _localInstanceListBox.SelectedItem = null;
                _serverListBox.SelectedItem = preferred;
                _vm.SelectedServer = preferred;
            }
        }
        UpdateAllServerRuntimeBuildStates();
        // Refresh every server in sequence so the sidebar is fully populated on launch.
        await _vm.LoadAllServersAsync();
        UpdateAllServerRuntimeBuildStates();

        // Check local instance content for updates (after server manifests are done).
        await CheckAllLocalInstanceContentAsync();

    }

    /// <summary>
    /// Checks all local instances for content updates on launch.
    /// </summary>
    private async Task CheckAllLocalInstanceContentAsync()
    {
        if (_localInstanceListBox.ItemsSource is not IEnumerable<LocalInstanceItemViewModel> items)
            return;

        foreach (var item in items.ToList())
        {
            try
            {
                await RefreshLocalInstanceContentAsync(item);
            }
            catch
            {
                // Non-critical — don't block startup for a failed content check.
            }
        }
    }

    // ══════════════════════════════════════════════════════════════════════
    //  Launch button style class
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Swaps the Launch button between the <c>success</c> (green) and
    /// <c>curseforge</c> (orange) style classes defined in Shared.axaml.
    /// The theme styles handle normal / hover / pressed states so we
    /// no longer need PointerEntered/Exited handlers.
    /// </summary>
    private void UpdateLaunchButtonClass()
    {
        UpdateLaunchButtonState();
    }

    // ══════════════════════════════════════════════════════════════════════
    //  servers.dat population for Default-mode launches
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Writes servers.dat entries for the launched server plus all related
    /// servers in its group (primary + secondaries).  Connection info comes
    /// from the authenticated health response that was cached on each
    /// <see cref="ServerItemViewModel"/> during the last refresh.
    /// </summary>
    private Task PopulateServersDatAsync(ServerItemViewModel server, string gameDir)
    {
        try
        {
            // Build the list of server IDs in this group (primary + secondaries).
            var host = server.Host;
            var serverGroup = host.GetServerGroup(server.ServerPublicId);
            var groupIds = new HashSet<string>(
                serverGroup.Select(s => s.ServerPublicId),
                StringComparer.OrdinalIgnoreCase);

            // Walk every VM that belongs to this group and pull connection
            // info from the in-memory ServerConfig populated by health.
            foreach (var vm in _vm.Servers)
            {
                if (!groupIds.Contains(vm.ServerPublicId)) continue;

                try
                {
                    string? gameHost = vm.Server.GameHost;
                    int?    port = vm.Server.Port;

                    if (string.IsNullOrWhiteSpace(gameHost)) continue;

                    var injectedAddress = ServersDatHelper.EnsureServerEntry(
                        gameDataPath: gameDir,
                        gameHost: gameHost,
                        port: port ?? 25565,
                        serverName: vm.Server.ServerName,
                        previousAddress: vm.Server.LastInjectedServerAddress);
                    if (!string.IsNullOrWhiteSpace(injectedAddress))
                    {
                        vm.Server.LastInjectedServerAddress = injectedAddress;
                        AppStorageDatabase.SaveLastInjectedServerAddress(vm.HostId, vm.ServerPublicId, injectedAddress);
                    }
                }
                catch
                {
                    // Non-fatal — skip this group server
                }
            }
        }
        catch
        {
            // servers.dat is non-critical — don't block the launch
        }

        return Task.CompletedTask;
    }

    // ══════════════════════════════════════════════════════════════════════
    //  Stream piping for game process output
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Reads lines from <paramref name="reader"/> and writes them to
    /// <paramref name="writer"/> until the stream ends.  Used to pipe
    /// Minecraft stdout/stderr into the instance log file.
    /// </summary>
    private static async Task PipeStreamAsync(
        System.IO.StreamReader reader,
        System.IO.StreamWriter writer,
        Action<string>? onLine = null)
    {
        string? line;
        while ((line = await reader.ReadLineAsync()) is not null)
        {
            await writer.WriteLineAsync(line);
            await writer.FlushAsync();
            onLine?.Invoke(line);
        }
    }
}
