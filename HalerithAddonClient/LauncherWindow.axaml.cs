using System.Diagnostics;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using HalerithAddonClient.Controls;
using HalerithAddonClient.Core;
using HalerithAddonClient.Services;
using HalerithAddonClient.Windows;

namespace HalerithAddonClient;

public partial class LauncherWindow : Window
{
    private const string InvalidAuthenticationMessage =
        "Minecraft authentication is no longer valid. Sign in with your Minecraft account again.";
    private const string InconclusiveAuthenticationFailureMessage =
        "Authentication failed. Check your system time and try signing in again.";
    private const double EulaDialogWidth = 920;
    private const double EulaDialogHeight = 680;

    private readonly AppConfig _appConfig;
    private readonly string _clientVersion;
    private bool _workStarted;

    // Tracks the current step name so error messages are always context-aware.
    private string _currentStep = "Initialising";

    // User-initiated cancellation (Cancel button).
    private readonly CancellationTokenSource _userCancelCts = new();

    // Cancelled when the user changes server during the countdown, triggering a restart.
    private CancellationTokenSource _restartCts = new();

    // Guards against re-entrant handling while populating the dropdowns programmatically.
    private bool _suppressDropdownChange;

    // Set to true once the countdown completes; prevents further selection changes.
    private bool _selectionLocked;

    // Set while launcher mode is asking whether it should build a missing runtime.
    private TaskCompletionSource<bool>? _buildPromptTcs;

    private bool _windowClosed;
    private bool _workflowCompleted;
    private bool _cancellationSourcesDisposed;

    public LauncherWindow(AppConfig appConfig)
    {
        _appConfig = appConfig;
        _clientVersion = GetVersion();
        InitializeComponent();

        Closing += (_, _) => RequestLaunchCancellation();
        Closed += (_, _) =>
        {
            _windowClosed = true;
            RequestLaunchCancellation();
            TryDisposeCancellationSources();
        };

        InitialiseDropdowns();

        AppWindowIcon.Apply(this);
    }

    // ──────────────────────────────────────────────────────────────────
    //  Dropdown population
    // ──────────────────────────────────────────────────────────────────

    private void InitialiseDropdowns()
    {
        _suppressDropdownChange = true;
        try
        {
            PopulateServerCombo(_appConfig.EnsureManagerScope());
        }
        finally
        {
            _suppressDropdownChange = false;
        }
    }

    /// <summary>
    /// Refills the server combo for <paramref name="host"/> and pre-selects its default server.
    /// Always runs under suppression so the SelectionChanged handler is bypassed.
    /// Optionally fires <see cref="TriggerRestart"/> after the selection is set.
    /// </summary>
    private void PopulateServerCombo(HostConfig host, bool triggerRestartAfter = false)
    {
        var prevSuppress = _suppressDropdownChange;
        _suppressDropdownChange = true;
        try
        {
            ServerComboBox.Items.Clear();
            var availableServers = DemoMode.Enabled ? DemoMode.VisibleServers(host.Servers) : host.VisibleServersFor(AccountStore.GetDefaultAccountId());
            foreach (var srv in availableServers)
            {
                var item = new ComboBoxItem
                {
                    Content = DemoMode.ServerName(srv, availableServers.IndexOf(srv))
                };
                item.DataContext = srv;
                ServerComboBox.Items.Add(item);
            }

            var defaultIdx = DemoMode.Enabled ? (availableServers.Count > 0 ? 0 : -1) : availableServers.FindIndex(s => s.ServerPublicId == host.DefaultServerPublicId);
            ServerComboBox.SelectedIndex = defaultIdx >= 0 ? defaultIdx : (availableServers.Count > 0 ? 0 : -1);
            if (DemoMode.Enabled) ServerComboBox.IsEnabled = false;
        }
        finally
        {
            _suppressDropdownChange = prevSuppress;
        }

        if (triggerRestartAfter)
            TriggerRestart();
    }

    private void LockDropdowns()
    {
        _selectionLocked = true;
        ServerComboBox.IsEnabled = false;
    }

    // ──────────────────────────────────────────────────────────────────
    //  Dropdown selection helpers
    // ──────────────────────────────────────────────────────────────────

    private HostConfig GetSelectedHost()
        => _appConfig.EnsureManagerScope();

    private ServerConfig? GetSelectedServer()
        => (ServerComboBox.SelectedItem as ComboBoxItem)?.DataContext as ServerConfig;

    private string? GetSelectedServerPublicId()
        => GetSelectedServer()?.ServerPublicId;

    private static string ResolveSelectedMinecraftUuid()
    {
        var selected = AccountStore.GetDefaultAccountId();

        var accounts = AccountStore.LoadAll();
        if (string.IsNullOrWhiteSpace(selected) && accounts.Count == 1)
            selected = accounts[0].Uuid;

        return MinecraftProfileClient.NormalizeUuid(selected ?? "") ?? "";
    }

    // ──────────────────────────────────────────────────────────────────
    //  Dropdown event handlers
    // ──────────────────────────────────────────────────────────────────

    private void ServerComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressDropdownChange || _selectionLocked) return;
        TriggerRestart();
    }

    /// <summary>
    /// Signals that the server selection changed. During the countdown window this
    /// cancels the delay and causes <see cref="RunAsync"/> to return <c>true</c> so the
    /// outer loop restarts the workflow with the new selection.  Before or after the
    /// countdown the signal is a no-op (pre-countdown: detected at countdown entry;
    /// post-countdown: dropdowns are locked so this method is never reached).
    /// </summary>
    private void TriggerRestart()
    {
        if (!_restartCts.IsCancellationRequested)
            _restartCts.Cancel();
    }

    // ──────────────────────────────────────────────────────────────────
    //  Startup — kick off work after the window has been rendered once
    // ──────────────────────────────────────────────────────────────────

    private void Window_Opened(object? sender, EventArgs e)
    {
        if (_workStarted) return;
        _workStarted = true;

        // Small delay so the window is visibly painted before heavy work begins.
        Dispatcher.UIThread.InvokeAsync(async () =>
        {
            try
            {
                await Task.Delay(80, _userCancelCts.Token);

                using var eulaClient = new HalerithEulaClient();
                var requiredEula = await eulaClient.ResolveRequiredClientDocumentAsync();
                await eulaClient.ReconcileDeletedAcceptanceAsync(requiredEula, _userCancelCts.Token);
                _userCancelCts.Token.ThrowIfCancellationRequested();

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
                _userCancelCts.Token.ThrowIfCancellationRequested();

                if (await TryRunHalerithClientUpdateAsync())
                    return;

                bool restart;
                do
                {
                    restart = await RunAsync();
                }
                while (restart && !_userCancelCts.IsCancellationRequested);
            }
            catch (OperationCanceledException) when (_userCancelCts.IsCancellationRequested)
            {
                // Closing the launcher or pressing Cancel ends the workflow silently.
            }
            finally
            {
                _workflowCompleted = true;
                TryDisposeCancellationSources();
            }
        });
    }

    /// <summary>
    /// Shows the EULA overlay on top of the launcher card and waits for user
    /// action.  Returns <see langword="true"/> if accepted, <see langword="false"/>
    /// if declined.
    /// </summary>
    private async Task<bool> ShowEulaOverlayAsync(EulaDocument document)
    {
        var overlay = new EulaOverlay(document);
        var size = GetEulaDialogSize();
        var dialog = new Window
        {
            Title = "End User License Agreement",
            Width = size.Width,
            Height = size.Height,
            MinWidth = Math.Min(760, size.Width),
            MinHeight = Math.Min(560, size.Height),
            CanResize = false,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = overlay,
        };

        overlay.Accepted += (_, _) =>
        {
            EulaStore.RecordAcceptance(document);
            dialog.Close(true);
        };

        overlay.Declined += (_, _) =>
        {
            dialog.Close(false);
        };

        var accepted = await dialog.ShowDialog<bool?>(this);
        return accepted == true;
    }

    private Size GetEulaDialogSize()
    {
        var screen = Screens.ScreenFromWindow(this);
        if (screen is null)
            return new Size(EulaDialogWidth, EulaDialogHeight);

        var scaling = screen.Scaling <= 0 ? 1 : screen.Scaling;
        var maxWidth = Math.Max(520, (screen.WorkingArea.Width / scaling) - 32);
        var maxHeight = Math.Max(520, (screen.WorkingArea.Height / scaling) - 32);
        return new Size(
            Math.Min(EulaDialogWidth, maxWidth),
            Math.Min(EulaDialogHeight, maxHeight));
    }

    // ──────────────────────────────────────────────────────────────────
    //  Main workflow  (returns true = restart with new server)
    // ──────────────────────────────────────────────────────────────────

    private async Task<bool> RunAsync()
    {
        // Fresh restart signal for this run.
        _restartCts.Dispose();
        _restartCts = new CancellationTokenSource();

        // Reset progress bar for a clean slate on restart.
        SyncProgress.IsIndeterminate = true;
        SyncProgress.Value = 0;

        try
        {
            // ── Resolve active manager scope + server ─────────────────
            Step("Initialising");

            var host = GetSelectedHost() ?? _appConfig.ActiveHost;
            if (host is null || !host.IsEnrolled)
            {
                ShowError("No configured server is ready to connect. Please open Halerith Addon Client to finish setup.");
                return false;
            }

            // Use whatever server is currently selected in the dropdown.
            var serverPublicId = DemoMode.Enabled ? DemoMode.VisibleServers(host.Servers).FirstOrDefault()?.ServerPublicId : GetSelectedServerPublicId();
            if (DemoMode.Enabled && string.IsNullOrEmpty(serverPublicId))
            {
                if (Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
                {
                    var main = new HalerithAddonClient.Windows.MainWindow(_clientVersion);
                    desktop.MainWindow = main;
                    main.Show();
                    Close();
                }
                return false;
            }
            if (string.IsNullOrWhiteSpace(serverPublicId))
            {
                ShowError("Select a server before launching.");
                return false;
            }
            var serverConfig = host.Servers.FirstOrDefault(s =>
                string.Equals(s.ServerPublicId, serverPublicId, StringComparison.OrdinalIgnoreCase));
            await RefreshLauncherAccountSessionAsync();
            var config = _appConfig.ToRuntimeConfig(host, serverPublicId);

            // ── Launcher configuration gate ──────────────────────────────
            // If the selected launcher mode isn't properly configured, abort
            // early with a clear message instead of doing network work and then
            // failing at launch time.
            // apiCts: short API calls (health, manifest, updates).
            // Combines user-cancel with a tight timeout appropriate for JSON round-trips.
            using var timeoutCts = new CancellationTokenSource(
                TimeSpan.FromSeconds(Math.Clamp(config.StartupTimeoutSeconds, 10, 300)));
            using var apiCts = CancellationTokenSource.CreateLinkedTokenSource(
                _userCancelCts.Token, timeoutCts.Token);

            // cts: file sync phase only — user-cancel only, no timeout.
            // _downloadHttpClient.Timeout (10 min) enforces per-download limits;
            // capping this with StartupTimeoutSeconds would abort large file transfers.
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(
                _userCancelCts.Token);

            using var apiClient = CreateApiClient(config);

            // ── Health check (server discovery + update check) ────────
            Step("Connecting to server");
            HealthResponse health;
            try
            {
                health = await apiClient.GetHostHealthAsync(apiCts.Token, _clientVersion);
            }
            catch
            {
                throw;
            }

            if (!health.Ok)
            {
                if (health.Code == 1010 || apiClient.LastStatusCode == System.Net.HttpStatusCode.Unauthorized)
                    throw new InvalidOperationException(await ResolveAuthenticationFailureMessageAsync(host, config.StartupTimeoutSeconds, apiCts.Token));

                throw new InvalidOperationException(health.Message ?? health.Error ?? "Health check failed.");
            }

            // Refresh local server list from health response.
            if (ServerRosterSync.ApplyHealth(host, health))
                AppConfigStore.SaveAppConfig(_appConfig);

            serverConfig = host.Servers.FirstOrDefault(s =>
                string.Equals(s.ServerPublicId, serverPublicId, StringComparison.OrdinalIgnoreCase));
            if (serverConfig is null || (!DemoMode.Enabled &&
                !host.VisibleServersFor(config.MinecraftUuid).Any(s =>
                    string.Equals(s.ServerPublicId, serverPublicId, StringComparison.OrdinalIgnoreCase))))
                throw new InvalidOperationException("Selected server is no longer available.");

            config = _appConfig.ToRuntimeConfig(host, serverPublicId);

            UpdateCoordinator.CleanupStaleUpdateFolders(config);

            // ── Fetch manifest ────────────────────────────────────────
            Step("Fetching manifest");
            var manifest = await apiClient.GetManifestAsync(new ManifestRequest
            {
                ClientVersion = _clientVersion
            }, apiCts.Token);

            if (!manifest.Ok || !manifest.Authorized)
            {
                if (manifest.Code == 1010 || apiClient.LastStatusCode == System.Net.HttpStatusCode.Unauthorized)
                    throw new InvalidOperationException(await ResolveAuthenticationFailureMessageAsync(host, config.StartupTimeoutSeconds, apiCts.Token));
                throw new InvalidOperationException(manifest.Message ?? manifest.Error ?? "Manifest request failed.");
            }

            ManagedServerAccessGate.RecordManifest(manifest, config.MinecraftUuid);

            // ── Countdown before applying ─────────────────────────────
            // Manifest is ready.  Give the user a moment to change server before we
            // commit to applying.  Any selection change during this window triggers a restart.
            //
            // If the selection already changed while we were doing network calls above,
            // restart immediately rather than starting a countdown.
            if (_restartCts.IsCancellationRequested && !_userCancelCts.IsCancellationRequested)
                return true;

            {
                var countdownSecs = Math.Clamp(_appConfig.LauncherCountdownSeconds, 0, 30);
                using var countdownCts = CancellationTokenSource.CreateLinkedTokenSource(
                    _restartCts.Token, _userCancelCts.Token);

                for (int i = countdownSecs; i > 0; i--)
                {
                    Dispatcher.UIThread.Invoke(() => StatusText.Text = $"Applying in {i}…");
                    try
                    {
                        await Task.Delay(1000, countdownCts.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        if (_userCancelCts.IsCancellationRequested) throw;
                        // Selection changed - restart with the new server.
                        return true;
                    }
                }
            }

            // Lock the dropdowns — no further changes are accepted.
            Dispatcher.UIThread.Invoke(LockDropdowns);

            var promotion = RuntimeVersionPromoter.ApplyManifestTarget(serverConfig, ModpackInstallationRequirements.RuntimeTarget(manifest));
            if (promotion is not null)
            {
                host.AlignSecondaryRuntimeVersions();
                AppConfigStore.SaveAppConfig(_appConfig);
                config = _appConfig.ToRuntimeConfig(host, serverPublicId);
                Log(promotion.ConsoleMessage);
            }

            // ── Scan local files + build sync plan ────────────────────
            Step("Scanning local files");

            var capturedConfig = config;
            var capturedManifest = manifest;

            var plan = await Task.Run(() => BuildSyncPlan(capturedManifest, capturedConfig), cts.Token);

            if (ServerLaunchReadiness.HasRequiredManualFiles(plan))
                return await PromptOpenClientAsync("Required CurseForge files need manual installation before launching.", serverPublicId);
            if (ServerLaunchReadiness.RequiresModpackReview(plan))
                return await PromptOpenClientAsync("This modpack needs file choices before it can be applied.", serverPublicId);

            var actionCount = plan.Actions.Count(a =>
                a.Kind is SyncActionKind.Download or SyncActionKind.Replace or SyncActionKind.Remove);

            var canSync = false;

            if (actionCount > 0 && !_appConfig.AllowAutoUpdate)
            {
                return await PromptOpenClientAsync("Server content needs an update, but automatic updates are disabled.", serverPublicId);
            }
            else if (actionCount > 0)
            {
                Step("Checking managed server access");
                var gate = await ManagedServerAccessGate.EnsureAsync(
                    capturedConfig, serverPublicId, ResolveSelectedMinecraftUuid(), cts.Token);
                canSync = gate.Allowed;

                if (!canSync)
                {
                    return await PromptOpenClientAsync(
                        string.IsNullOrWhiteSpace(gate.Message) ? "Server content needs an update before launching." : gate.Message,
                        serverPublicId);
                }
            }

            var forcedPackPending = capturedManifest.Modpack?.ForceConfig == true
                && ModpackOverlayService.Review(capturedManifest.Modpack, capturedConfig.GameDataPath, capturedConfig.ModpackFilePolicy).HasWork;
            if (forcedPackPending && (!canSync || !plan.Actions.Any(a => a.Modpack is not null && a.Kind == SyncActionKind.Download)))
            {
                return await PromptOpenClientAsync("This server requires its modpack files before launching.", serverPublicId);
            }

            SyncExecutionResult? syncResult = null;
            if (canSync)
            {
                // ── Execute sync (only when there is actually work to do) ─
                if (actionCount > 0)
                {
                    var mediumUpdates = SyncPlanConfirmation.GetMediumConfidenceUpdates(plan);
                    if (mediumUpdates.Count > 0)
                    {
                        Step("Reviewing updates");
                        var dialog = new MediumConfidenceUpdateDialog(mediumUpdates);
                        var confirmResult = await dialog.ShowDialog<bool?>(this);
                        if (confirmResult != true)
                        {
                            Close();
                            return false;
                        }

                        plan = SyncPlanConfirmation.WithoutUnselectedMediumConfidenceUpdates(plan, dialog.SelectedActions);
                        actionCount = plan.Actions.Count(a =>
                            a.Kind is SyncActionKind.Download or SyncActionKind.Replace or SyncActionKind.Remove);
                        if (actionCount == 0)
                        {
                            Close();
                            return false;
                        }
                    }

                    Step($"Updating {actionCount} file(s)");
                    SyncProgress.IsIndeterminate = false;
                    SyncProgress.Value = 0;
                    SyncProgress.Maximum = actionCount;

                    // Count completed sync actions (Download/Replace/Remove) via log lines.
                    // SyncExecutor logs one "Downloading:" / "Copying:" line per action.
                    int done = 0;
                    void ProgressLog(string msg)
                    {
                        // Only advance the bar for lines that correspond to an active sync action.
                        if (msg.StartsWith("Downloading:", StringComparison.OrdinalIgnoreCase) ||
                            msg.StartsWith("Removing:", StringComparison.OrdinalIgnoreCase) ||
                            msg.StartsWith("ERROR:", StringComparison.OrdinalIgnoreCase))
                        {
                            var current = Interlocked.Increment(ref done);
                            Dispatcher.UIThread.InvokeAsync(() => SyncProgress.Value = Math.Min(current, actionCount));
                        }
                        Log(msg);
                    }

                    var startedAt = DateTime.UtcNow;
                    var result = await new SyncExecutor(apiClient, capturedConfig, ProgressLog)
                        .ExecuteAsync(plan, cts.Token);
                    syncResult = result;

                    if (forcedPackPending && result.FailedPackages.Any(item => item.ContentItemId == capturedManifest.Modpack!.Id))
                    {
                        return await PromptOpenClientAsync("Required modpack files could not be applied.", serverPublicId);
                    }

                    LauncherLogger.Info($"Sync done — downloaded={result.Downloaded}, replaced={result.Replaced}, " +
                        $"removed={result.Removed}, failed={result.Failed}, ignored={result.Ignored}, " +
                        $"current={result.UpToDate}", EventIds.Launcher.SyncCompleted);

                    // ── Report results (always enabled) ──────────────────
                    {
                        Step("Sending report");
                        try
                        {
                            await apiClient.SendReportAsync(new ReportRequest
                            {
                                ClientVersion = _clientVersion,
                                Mode = "sync",
                                Status = result.Status,
                                StartedAt = startedAt.ToString("O"),
                                CompletedAt = DateTime.UtcNow.ToString("O"),
                                Details = SyncReportDetails.From(result)
                            // Use CancellationToken.None — cts may be cancelled if a download
                            // timed out, but we still want the report to reach the server.
                            }, CancellationToken.None);
                        }
                        catch (Exception ex)
                        {
                            // Report failure is non-fatal — still launch the game.
                            LauncherLogger.Warn($"Report skipped: {ex.Message}", EventIds.Launcher.Warning);
                        }
                    }

                    if (result.Failed > 0)
                        return await PromptOpenClientAsync("Some server content could not be updated.", serverPublicId);
                }
                else
                {
                    Step("Up to date");
                    LauncherLogger.Info("Nothing to sync — all files are up to date.", EventIds.Launcher.NothingToSync);
                }
            }
            else
            {
                Step(actionCount > 0 ? "Skipping update" : "Up to date");
                LauncherLogger.Info(
                    actionCount > 0
                        ? "Sync skipped — update not allowed for current access state."
                        : "Nothing to sync — all files are up to date.",
                    EventIds.Launcher.NothingToSync);
            }

            var finalPlan = await Task.Run(() => BuildSyncPlan(capturedManifest, capturedConfig), cts.Token);
            if (ServerLaunchReadiness.HasRequiredManualFiles(finalPlan))
                return await PromptOpenClientAsync("Required CurseForge files need manual installation before launching.", serverPublicId);
            if (ServerLaunchReadiness.RequiresModpackReview(finalPlan))
                return await PromptOpenClientAsync("This modpack needs file choices before it can be applied.", serverPublicId);
            if (!ServerLaunchReadiness.CanLaunchAfterSync(finalPlan, syncResult))
                return await PromptOpenClientAsync("Server content still needs an update before launching.", serverPublicId);

            // Recheck the selected Minecraft account before managed launch.
            var launchGate = await ManagedServerAccessGate.EnsureAsync(
                    capturedConfig, serverPublicId, ResolveSelectedMinecraftUuid(), cts.Token);
            if (!launchGate.Allowed)
            {
                ShowError(string.IsNullOrWhiteSpace(launchGate.Message)
                    ? "Cannot launch - refresh your server list in the main client."
                    : launchGate.Message);
                return false;
            }

            // ── Launch the game client ────────────────────────────────
            Step("Launching — please wait");
            SyncProgress.IsIndeterminate = true; // animated bar while launching

            // ── Ensure servers.dat is up to date ──────────────────────
            // Inject servers.dat entries from the health-refreshed server group.
            var serverGroup = host.GetServerGroup(serverPublicId);
            foreach (var groupServer in serverGroup)
            {
                try
                {
                    if (!string.IsNullOrWhiteSpace(groupServer.GameHost))
                    {
                        var injectedAddress = ServersDatHelper.EnsureServerEntry(
                            gameDataPath: capturedConfig.GameDataPath,
                            gameHost: groupServer.GameHost,
                            port: groupServer.Port ?? 25565,
                            serverName: groupServer.ServerName,
                            previousAddress: groupServer.LastInjectedServerAddress,
                            log: Log);
                        if (!string.IsNullOrWhiteSpace(injectedAddress))
                        {
                            groupServer.LastInjectedServerAddress = injectedAddress;
                            AppStorageDatabase.SaveLastInjectedServerAddress(host.Id, groupServer.ServerPublicId, injectedAddress);
                        }
                    }
                    else
                    {
                        Log($"ServersDat: skipped '{groupServer.ServerName}' — health did not include connection info.");
                    }
                }
                catch (Exception ex)
                {
                    Log($"ServersDat: failed to add '{groupServer.ServerName}': {ex.Message}");
                }
            }

            // LaunchBuiltInAsync monitors stdout and closes the window
            // once Minecraft is confirmed running.
            _userCancelCts.Token.ThrowIfCancellationRequested();
            await LaunchBuiltInAsync(capturedConfig, serverConfig, capturedManifest);
            return false;
        }
        catch (OperationCanceledException)
        {
            if (_userCancelCts.IsCancellationRequested)
            {
                // User clicked Cancel — close silently with no error dialog.
                Dispatcher.UIThread.Invoke(Close);
            }
            else
            {
                ShowError($"Operation timed out during: {_currentStep}. Check your connection and try again.");
            }
            return false;
        }
        catch (ConfigurationIncompleteException ex)
        {
            // Server is missing required path configuration (e.g. Mods folder).
            // Show the friendly guidance message without wrapping it in the
            // generic "Error during '<step>'" prefix.
            ShowError(ex.Message);
            return false;
        }
        catch (Exception ex)
        {
            ShowError($"Error during '{_currentStep}': {ex.Message}");
            return false;
        }
    }

    private async Task RefreshLauncherAccountSessionAsync()
    {
        var accounts = AccountStore.LoadAll();
        var accountIds = new[] { AccountStore.GetDefaultAccountId() }
            .Select(id => MinecraftProfileClient.NormalizeUuid(id) ?? "")
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var accountId in accountIds)
        {
            var account = accounts.FirstOrDefault(candidate => string.Equals(
                MinecraftProfileClient.NormalizeUuid(candidate.Uuid),
                accountId,
                StringComparison.OrdinalIgnoreCase));

            if (account is null)
                throw new InvalidOperationException("The selected Minecraft account is no longer configured. Open the main client and choose an account.");

            if (string.IsNullOrWhiteSpace(account.MsRefreshToken))
            {
                if (string.IsNullOrWhiteSpace(account.McAccessToken) || account.IsExpired)
                    throw new InvalidOperationException(InvalidAuthenticationMessage);
                continue;
            }

            Step("Refreshing authentication — please wait");
            try
            {
                using var authService = new MicrosoftAuthService();
                var refreshed = await authService.RefreshAsync(account, _userCancelCts.Token);
                AccountStore.Save(refreshed);
                LauncherLogger.Info($"Minecraft session refreshed for {refreshed.Gamertag} before server discovery.", EventIds.Launcher.StepStarted);
            }
            catch (Exception ex) when (!string.IsNullOrWhiteSpace(account.McAccessToken) && !account.IsExpired)
            {
                LauncherLogger.Warn($"Proactive Minecraft session refresh failed; continuing with the current valid session: {ex.Message}", EventIds.Launcher.Warning);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(InvalidAuthenticationMessage, ex);
            }
        }
    }

    // ──────────────────────────────────────────────────────────────────
    //  Runtime launcher (mirrors MainWindow.LaunchDefaultAsync)
    // ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Resolves the game environment, authenticates the Minecraft account,
    /// and launches the JVM process directly — the same pipeline as
    /// <c>MainWindow.LaunchDefaultAsync</c>.
    /// </summary>
    private async Task<bool> LaunchBuiltInAsync(RuntimeConfig config, ServerConfig? serverConfig, ManifestResponse manifest)
    {
        if (manifest.Modpack is not null)
        {
            Step("Checking required modpack content");
            await Task.Run(async () =>
            {
                var inventory = VerifiedServerContentScanner.Scan(config.HostId, config.ServerPublicId, config);
                var contentPlan = ServerContentPlanner.Build(manifest, inventory, config);
                await ModpackInstallationRequirements.AssertReadyAsync(manifest, contentPlan, config, _userCancelCts.Token);
            }, _userCancelCts.Token);
        }
        var mcVersion = serverConfig?.MinecraftVersion ?? "";
        var loader = serverConfig?.Loader ?? "";
        var loaderVersion = serverConfig?.LoaderVersion ?? "";

        if (string.IsNullOrWhiteSpace(mcVersion))
            throw new InvalidOperationException("Server version unknown — refresh to retrieve server info.");

        // ── Resolve/build game environment ───────────────────────────
        Step("Resolving game environment — please wait");

        var runtimeRoot = System.IO.Path.Combine(LauncherPaths.GetAppDataDirectory(), "Runtime");
        using var db = new RuntimeDatabase(runtimeRoot);
        using var builder = new GameEnvironmentBuilder(database: db);

        var report = await builder.CheckReadinessAsync(mcVersion, loader, loaderVersion, _userCancelCts.Token);
        if (!report.IsReady)
        {
            var built = await PromptAndBuildRuntimeAsync(
                mcVersion,
                loader,
                loaderVersion,
                report);
            if (!built)
                return false;

            report = await builder.CheckReadinessAsync(mcVersion, loader, loaderVersion, _userCancelCts.Token);
            if (!report.IsReady)
                throw new InvalidOperationException($"Game environment still missing {report.MissingCount} item(s) after build.");
        }

        await builder.RecordInstanceAsync(report, _userCancelCts.Token);

        // ── Resolve Minecraft account ────────────────────────────────
        Step("Resolving account — please wait");

        var accounts = AccountStore.LoadAll();
        MinecraftAccount? account = null;
        var accountId = AccountStore.GetDefaultAccountId();

        if (!string.IsNullOrWhiteSpace(accountId))
        {
            account = accounts.FirstOrDefault(a =>
                string.Equals(a.Uuid, accountId, StringComparison.OrdinalIgnoreCase));
        }

        account ??= accounts.Count == 1 ? accounts[0] : null;

        if (account is null)
            throw new InvalidOperationException("No Minecraft account configured. Open the main client and add an account.");

        // Refresh expired token
        if (account.IsExpired)
        {
            Step("Refreshing authentication — please wait");
            using var authService = new MicrosoftAuthService();
            account = await authService.RefreshAsync(account, _userCancelCts.Token);
            AccountStore.Save(account);
        }

        var username = account.Gamertag ?? "Player";

        var env = await builder.ResolveEnvironmentAsync(mcVersion, loader, loaderVersion);

        // Use the server-specific instance folder (same as MainWindow)
        var serverPublicId = serverConfig?.ServerPublicId ?? "default";
        var gameDir = System.IO.Path.Combine(
            LauncherPaths.GetAppDataDirectory(), "Runtime", "instances", serverPublicId);
        System.IO.Directory.CreateDirectory(gameDir);

        // ── Write servers.dat into the instance game directory ────────
        if (!string.IsNullOrWhiteSpace(serverConfig?.GameHost))
        {
            var injectedAddress = ServersDatHelper.EnsureServerEntry(
                gameDataPath: gameDir,
                gameHost: serverConfig.GameHost,
                port: serverConfig.Port ?? 25565,
                serverName: serverConfig.ServerName,
                previousAddress: serverConfig.LastInjectedServerAddress,
                log: Log);
            if (!string.IsNullOrWhiteSpace(injectedAddress))
            {
                serverConfig.LastInjectedServerAddress = injectedAddress;
                AppStorageDatabase.SaveLastInjectedServerAddress(config.HostId, serverConfig.ServerPublicId, injectedAddress);
            }
        }

        // ── Build JVM launch plan and start process ────────��─────────
        Step("Starting Minecraft — please wait");

        if (!MinecraftLauncher.TryBuildDefaultLaunchPlan(env, gameDir, username, out var plan, out var message, account, serverConfig))
            throw new InvalidOperationException($"Could not build launch plan: {message}");

        _userCancelCts.Token.ThrowIfCancellationRequested();
        LauncherLogger.Info($"Launching runtime: {plan.FileName}", EventIds.Launcher.GameLaunched);

        var psi = new ProcessStartInfo
        {
            FileName = plan.FileName,
            Arguments = plan.Arguments,
            WorkingDirectory = plan.WorkingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        using var proc = Process.Start(psi);
        if (proc is null)
            throw new InvalidOperationException("Failed to start the game process.");

        // Startup output is a signal, not proof that a pack cannot crash later.
        // A slow startup remains unconfirmed rather than being called running.
        var gameReady = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellationReg = _userCancelCts.Token.Register(() =>
        {
            try
            {
                if (!proc.HasExited)
                    proc.Kill(entireProcessTree: true);
            }
            catch
            {
                // The process may have exited between the check and the kill.
            }

            gameReady.TrySetCanceled(_userCancelCts.Token);
        });

        // Also detect if the process exits before we see the trigger —
        // means the game crashed during startup.
        proc.EnableRaisingEvents = true;
        proc.Exited += (_, _) => gameReady.TrySetResult(false);
        if (proc.HasExited) gameReady.TrySetResult(false);

        async Task MonitorStream(System.IO.StreamReader reader)
        {
            try
            {
                while (await reader.ReadLineAsync() is { } line)
                {
                    if (line.Contains("Sound engine started", StringComparison.OrdinalIgnoreCase))
                    {
                        gameReady.TrySetResult(true);
                        return;
                    }
                }
            }
            catch { /* stream closed */ }
        }

        _ = MonitorStream(proc.StandardOutput);
        _ = MonitorStream(proc.StandardError);

        var confirmed = await GameStartupMonitor.WaitForConfirmationAsync(gameReady.Task, TimeSpan.FromSeconds(45),
            () => Dispatcher.UIThread.Post(() => Step("Minecraft is still starting. Waiting for startup confirmation.")));

        // A normal launcher close after confirmed startup must not kill Minecraft.
        cancellationReg.Dispose();

        if (confirmed && !proc.HasExited)
        {
            LauncherLogger.Info("Game startup detected.", EventIds.Launcher.GameLaunched);
        }
        else if (proc.HasExited)
        {
            LauncherLogger.Warn($"Game process exited with code {proc.ExitCode} before confirming startup.", EventIds.Launcher.Warning);
            ShowError("Minecraft exited before startup completed. Check the game logs for details.");
            return false;
        }
        else
        {
            ShowError("Minecraft startup could not be confirmed. Check the game logs for details.");
            return false;
        }

        Dispatcher.UIThread.Invoke(Close);
        return true;
    }

    private async Task<bool> PromptAndBuildRuntimeAsync(
        string mcVersion,
        string loader,
        string loaderVersion,
        ReadinessReport report)
    {
        var target = new RuntimeVersionInfo(mcVersion, loader, loaderVersion).Display;
        var missing = report.MissingCount;

        _buildPromptTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        Dispatcher.UIThread.Invoke(() =>
        {
            StatusText.Text = $"Runtime not built for {target}. {missing} item(s) missing.";
            SyncProgress.IsIndeterminate = false;
            SyncProgress.Minimum = 0;
            SyncProgress.Maximum = Math.Max(missing, 1);
            SyncProgress.Value = 0;
            BuildButton.IsVisible = true;
            BuildButton.IsEnabled = true;
            BuildButton.Content = "Build & Continue";
            CancelButton.Content = "Close";
            CancelButton.IsEnabled = true;
        });

        var shouldBuild = await _buildPromptTcs.Task;
        _buildPromptTcs = null;

        if (!shouldBuild)
            return false;

        Dispatcher.UIThread.Invoke(() =>
        {
            BuildButton.IsEnabled = false;
            CancelButton.Content = "Cancel";
            StatusText.Text = $"Building {target}...";
        });

        var existing = ActiveBuildTracker.Get(mcVersion, loader, loaderVersion);
        if (existing is not null && !existing.BuildTask.IsCompleted)
        {
            await PollLauncherBuildProgressAsync(existing);
            if (!string.IsNullOrWhiteSpace(existing.Error))
                throw new InvalidOperationException(existing.Error);

            Dispatcher.UIThread.Invoke(() =>
            {
                BuildButton.IsVisible = false;
                SyncProgress.Value = SyncProgress.Maximum;
                StatusText.Text = "Build complete. Launching...";
            });

            return true;
        }
        if (existing is not null)
        {
            ActiveBuildTracker.Remove(mcVersion, loader, loaderVersion);
        }

        var buildTask = Task.Run(() => RunLauncherBuildPipelineAsync(
            mcVersion, loader, loaderVersion, _userCancelCts.Token));
        var state = ActiveBuildTracker.Register(mcVersion, loader, loaderVersion, buildTask);

        try
        {
            await PollLauncherBuildProgressAsync(state);
            await buildTask;

            if (!string.IsNullOrWhiteSpace(state.Error))
                throw new InvalidOperationException(state.Error);

            Dispatcher.UIThread.Invoke(() =>
            {
                BuildButton.IsVisible = false;
                SyncProgress.Value = SyncProgress.Maximum;
                StatusText.Text = "Build complete. Launching...";
            });

            return true;
        }
        finally
        {
            if (state.BuildTask.IsCompleted)
                ActiveBuildTracker.Remove(mcVersion, loader, loaderVersion);
        }
    }

    private async Task PollLauncherBuildProgressAsync(ActiveBuildTracker.BuildState state)
    {
        while (!state.BuildTask.IsCompleted)
        {
            await Task.Delay(250, _userCancelCts.Token);
            Dispatcher.UIThread.Invoke(() =>
            {
                SyncProgress.IsIndeterminate = false;
                SyncProgress.Maximum = Math.Max(state.Total, 1);
                SyncProgress.Value = state.Completed;
                StatusText.Text = string.IsNullOrWhiteSpace(state.Label) ? "Building..." : state.Label;
            });
        }

        await state.BuildTask;

        Dispatcher.UIThread.Invoke(() =>
        {
            SyncProgress.IsIndeterminate = false;
            SyncProgress.Maximum = Math.Max(state.Total, 1);
            SyncProgress.Value = state.Completed;
            StatusText.Text = string.IsNullOrWhiteSpace(state.Label) ? "Build complete." : state.Label;
        });
    }

    private static async Task RunLauncherBuildPipelineAsync(
        string mcVersion,
        string loader,
        string loaderVersion,
        CancellationToken ct)
    {
        ActiveBuildTracker.BuildState? state = null;
        for (var i = 0; i < 20 && state is null; i++)
        {
            state = ActiveBuildTracker.Get(mcVersion, loader, loaderVersion);
            if (state is null)
                await Task.Delay(10, ct);
        }

        if (state is null) return;

        try
        {
            CleanPartialLoaderInstall(loader, loaderVersion);

            var runtimeRoot = System.IO.Path.Combine(LauncherPaths.GetAppDataDirectory(), "Runtime");
            using var db = new RuntimeDatabase(runtimeRoot);
            using var builder = new GameEnvironmentBuilder(database: db);

            var report = await builder.CheckReadinessAsync(mcVersion, loader, loaderVersion, ct);
            if (report.IsReady)
            {
                await builder.RecordInstanceAsync(report, ct);
                state.Completed = Math.Max(state.Total, 1);
                state.Label = "Already ready";
                return;
            }

            state.Total = Math.Max(report.MissingCount, 1);

            var progress = new Progress<(int completed, int total, string label)>(p =>
            {
                state.Completed = p.completed;
                state.Total = Math.Max(p.total, 1);
                state.Label = string.IsNullOrWhiteSpace(p.label)
                    ? $"{p.completed}/{p.total}"
                    : $"{p.completed}/{p.total} {p.label}";
            });

            await builder.BuildAsync(report, progress, ct);

            state.Label = "Recording instance...";
            var finalReport = await builder.CheckReadinessAsync(mcVersion, loader, loaderVersion, ct);
            await builder.RecordInstanceAsync(finalReport, ct);

            state.Completed = state.Total;
            state.Label = "Complete";
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            state.Label = "Cancelled";
            throw;
        }
        catch (Exception ex)
        {
            state.Error = ex.Message;
            state.Label = $"Build failed: {ex.Message}";
            throw;
        }
    }

    private static void CleanPartialLoaderInstall(string loader, string loaderVersion)
    {
        var loaderLower = loader.ToLowerInvariant();
        if (loaderLower is not ("forge" or "neoforge") || string.IsNullOrWhiteSpace(loaderVersion))
            return;

        try
        {
            using var installer = new ForgeInstallerService();
            if (installer.IsPartialInstall(loaderLower, loaderVersion))
            {
                installer.CleanInstall(loaderLower, loaderVersion);
                FileLogger.Info($"[Build] Cleaned partial {loader} install for {loaderVersion}");
            }
        }
        catch (Exception ex)
        {
            FileLogger.Warn($"[Build] Failed to clean partial install: {ex.Message}");
        }
    }

    // ──────────────────────────────────────────────────────────────────
    //  Helpers
    // ──────────────────────────────────────────────────────────────────

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        CancelButton.IsEnabled = false;
        RequestLaunchCancellation();
        Close();
    }

    private void RequestLaunchCancellation()
    {
        if (_cancellationSourcesDisposed)
            return;

        _buildPromptTcs?.TrySetResult(false);

        if (!_userCancelCts.IsCancellationRequested)
            _userCancelCts.Cancel();

        if (!_restartCts.IsCancellationRequested)
            _restartCts.Cancel();
    }

    private void TryDisposeCancellationSources()
    {
        if (_cancellationSourcesDisposed || !_windowClosed || !_workflowCompleted)
            return;

        _cancellationSourcesDisposed = true;
        _userCancelCts.Dispose();
        _restartCts.Dispose();
    }

    private void BuildButton_Click(object sender, RoutedEventArgs e)
    {
        _buildPromptTcs?.TrySetResult(true);
    }

    private void Step(string name)
    {
        _currentStep = name;
        Dispatcher.UIThread.Invoke(() => StatusText.Text = $"{name}…");
        LauncherLogger.Info($"[Step] {name}", EventIds.Launcher.StepStarted);
    }

    private async Task<bool> TryRunHalerithClientUpdateAsync()
    {
        Step("Checking for client update");

        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(
            _userCancelCts.Token,
            timeoutCts.Token);
        var started = await HalerithSelfUpdateRunner.TryRunAsync(
            _clientVersion,
            _appConfig,
            relaunchArgs: "-launch",
            message => LauncherLogger.Info(message, EventIds.Launcher.StepStarted),
            cts.Token,
            (prepared, token) => ClientUpgradeCountdownWindow.ConfirmAsync(this, prepared.Version, token));

        if (!started)
            return false;

        LauncherLogger.Info("Halerith client update started after EULA acceptance.", EventIds.Launcher.StepStarted);

        if (HalerithAddonClient.Core.Platform.PlatformServices.Current.Updates.UsesExternalUpdateFeed)
        {
            Step("Update is ready in the macOS updater");
            return true;
        }

        Step("Installing client update");
        await Task.Delay(500, _userCancelCts.Token);
        Close();
        return true;
    }

    private void ShowError(string message)
    {
        LauncherLogger.Error($"[Error] {message}", EventIds.Launcher.Error);
        Dispatcher.UIThread.InvokeAsync(() =>
        {
            StatusText.Text = message;
            SyncProgress.IsIndeterminate = false;
            SyncProgress.Value = 0;

            // Close the window after a brief delay so the error is visible
            Task.Delay(3000).ContinueWith(_ => Close());
        });
    }

    private async Task<bool> PromptOpenClientAsync(string reason, string serverPublicId)
    {
        Step(reason);
        LauncherLogger.Warn($"Launch paused: {reason}", EventIds.Launcher.Warning);
        var open = await new MessageBoxWindow(
            "Action needed before launch",
            reason + " Open Halerith Addon Client to resolve this, or close the launcher.",
            MessageBoxButton.YesNo, MessageBoxImage.Information,
            "Open Client", "Close").ShowDialog(this);
        if (open && Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
        {
            var main = new HalerithAddonClient.Windows.MainWindow(_clientVersion, serverPublicId);
            desktop.MainWindow = main;
            main.Show();
        }
        Close();
        return false;
    }

    private static SyncPlan BuildSyncPlan(ManifestResponse manifest, RuntimeConfig config)
    {
        var plan = SyncPlanner.BuildPlan(
            manifest, LocalModScanner.Scan(config.ModsFolder), config.IgnoreRules,
            config.LocalPinnedItems, config.GameDataPath, config.ModpackFilePolicy);
        if (!string.IsNullOrWhiteSpace(config.ResourcePacksFolder))
            plan.Actions.AddRange(SyncPlanner.BuildResourcePackPlan(
                manifest, config.ResourcePacksFolder, config.IgnoreRules, config.LocalPinnedItems).Actions);
        if (!string.IsNullOrWhiteSpace(config.ShadersFolder))
            plan.Actions.AddRange(SyncPlanner.BuildShaderPlan(
                manifest, config.ShadersFolder, config.IgnoreRules, config.LocalPinnedItems).Actions);
        return plan;
    }

    private static ApiClient CreateApiClient(RuntimeConfig rc)
    {
        return new ApiClient(rc, traceLog: LauncherLog.TraceAction());
    }

    private async Task<string> ResolveAuthenticationFailureMessageAsync(
        HostConfig host,
        int startupTimeoutSeconds,
        CancellationToken ct)
    {
        var skewCheck = await ClockSkewDetector.CheckAsync(
            host.ApiBaseUrl,
            startupTimeoutSeconds,
            _clientVersion,
            host.IgnoreCertificateErrors,
            ct);

        if (skewCheck.HasClockSkewWarning)
            return skewCheck.WarningMessage!;

        if (!skewCheck.ProbeSucceeded)
            return InconclusiveAuthenticationFailureMessage;

        return InvalidAuthenticationMessage;
    }

    // ── Logging ───────────────────────────────────────────────────────

    private static readonly EventLogger LauncherLogger = new("Launcher", enabled: true);

    internal static void ConfigureLogging(bool enabled) => LauncherLogger.SetEnabled(enabled);

    private static void Log(string message)
        => LauncherLogger.Info(message, EventIds.Launcher.Started);

    // ── Version ───────────────────────────────────────────────────────

    /// <summary>
    /// Returns the version of the executing assembly (HalerithAddonClient.exe).
    /// Since the launcher is now embedded in the main client, the assembly
    /// version is always the correct version to report to the server.
    /// </summary>
    private static string GetVersion()
    {
        var asm = Assembly.GetExecutingAssembly();
        return asm.GetCustomAttribute<AssemblyFileVersionAttribute>()?.Version
            ?? asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? asm.GetName().Version?.ToString()
            ?? "1.0.0";
    }
}
