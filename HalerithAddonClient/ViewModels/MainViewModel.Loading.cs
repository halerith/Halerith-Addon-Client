using HalerithAddonClient.Core;
using HalerithAddonClient.Services;
using System.Net;

namespace HalerithAddonClient.ViewModels;

public sealed partial class MainViewModel
{
    private const string InvalidAuthenticationMessage =
        "Minecraft authentication is no longer valid. Sign in with your Minecraft account again.";
    private const string InconclusiveAuthenticationFailureMessage =
        "Authentication failed. Check your system time and try signing in again.";
    private string _lastRosterRefreshFailure = "";

    /// <summary>
    /// Loads <see cref="AppConfig"/> from app storage, syncs the manager
    /// rosters from health, then populates the sidebar.
    /// </summary>
    public async Task LoadServersAsync()
    {
        HasLoadedServers = false;

        try
        {
            StatusText = "Loading configuration…";
            await Task.Run(() => _appConfig = AppConfigStore.LoadAppConfig());

            if (_appConfig is null) return;

            _lastRosterRefreshFailure = "";
            var configChanged = await RefreshHostRostersBeforePopulateAsync(_appConfig);
            if (configChanged)
                await Task.Run(() => AppConfigStore.SaveAppConfig(_appConfig));

            await PopulateServersAsync(_appConfig);

            if (DemoMode.Enabled) SelectedServer = Servers.FirstOrDefault();

            if (SelectedServer is null
                && Servers.Count > 0
                && !string.Equals(_appConfig.DefaultInstanceKind, AppConfig.DefaultInstanceKindLocal, StringComparison.OrdinalIgnoreCase))
            {
                var defaultServer = Servers.FirstOrDefault(s =>
                    string.Equals(s.ServerPublicId, _appConfig.DefaultInstanceId, StringComparison.OrdinalIgnoreCase))
                    ?? Servers.FirstOrDefault(s =>
                        string.Equals(s.ServerPublicId, s.Host.DefaultServerPublicId, StringComparison.OrdinalIgnoreCase));

                SelectedServer = defaultServer ?? Servers[0];
            }

            var emptyOrFailureStatus = ResolveRemoteEmptyStateText(_lastRosterRefreshFailure, Servers.Count);
            if (!string.IsNullOrWhiteSpace(emptyOrFailureStatus))
                StatusText = emptyOrFailureStatus;
        }
        catch (Exception ex)
        {
            StatusText = $"Configuration error: {ex.Message}";
        }
        finally
        {
            HasLoadedServers = true;
            RefreshManagedAccessVisualState();
        }
    }

    private async Task<bool> RefreshHostRostersBeforePopulateAsync(AppConfig config)
    {
        var changed = false;
        var enrolledHosts = config.Hosts.Where(h => h.IsEnrolled).ToList();

        foreach (var host in enrolledHosts)
        {
            var runtime = config.ToRuntimeConfig(host, host.DefaultServerPublicId);
            if (!runtime.IsEnrolled)
                continue;

            try
            {
                AppConfigStore.ValidateForRuntime(runtime);

                using var apiClient = CreateApiClient(runtime);
                using var cts = new CancellationTokenSource(
                    TimeSpan.FromSeconds(Math.Clamp(runtime.StartupTimeoutSeconds, 10, 300)));

                var health = await apiClient.GetHostHealthAsync(cts.Token, clientVersion: _clientVersion);
                if (!health.Ok)
                {
                    if (IsAuthenticationInvalidResponse(health, apiClient))
                    {
                        changed |= await HandleHostAuthenticationFailureAsync(host, runtime);
                    }
                    else
                    {
                        var failure = ResolveHealthFailureMessage(health, apiClient);
                        _lastRosterRefreshFailure = failure;
                        AppendConsole($"[{host.GetDisplayName()}] {failure}");
                    }

                    continue;
                }

                ManagedServerAccessGate.Record(health);
                RefreshManagedAccessVisualState();

                if (ServerRosterSync.ApplyHealth(host, health))
                    changed = true;

                await RefreshServerHealthVisualsAsync(apiClient, host, health, cts.Token);
            }
            catch (Exception ex)
            {
                LauncherLog.Write($"Host roster refresh skipped for {host.GetDisplayName()}: {ex.Message}");
            }
        }

        return changed;
    }

    private static string ResolveRemoteEmptyStateText(string lastRosterRefreshFailure, int serverCount)
    {
        if (AccountStore.LoadAll().Count == 0)
            return "No accounts configured";

        if (!string.IsNullOrWhiteSpace(lastRosterRefreshFailure))
            return lastRosterRefreshFailure;

        return serverCount == 0 ? "No servers configured" : "";
    }

    private static string ResolveHealthFailureMessage(ApiResponseBase response, ApiClient apiClient)
    {
        var message = response.Message?.Trim();
        if (!string.IsNullOrWhiteSpace(message))
            return message;

        var error = response.Error?.Trim();
        if (!string.IsNullOrWhiteSpace(error))
            return $"Health check failed ({error})";

        if (apiClient.LastStatusCode.HasValue)
            return $"Health check failed (HTTP {(int)apiClient.LastStatusCode.Value})";

        return "Health check failed";
    }

    private async Task<bool> HandleHostAuthenticationFailureAsync(HostConfig host, RuntimeConfig config)
    {
        var skewCheck = await CheckClockSkewAsync(config);
        if (skewCheck.HasClockSkewWarning)
        {
            StatusText = skewCheck.WarningMessage!;
            AppendConsole($"[{host.GetDisplayName()}] {skewCheck.WarningMessage}");
            return false;
        }

        if (skewCheck.ProbeSucceeded)
        {
            AppendConsole($"[{host.GetDisplayName()}] {InvalidAuthenticationMessage}");
            return true;
        }

        AppendConsole($"[{host.GetDisplayName()}] {InconclusiveAuthenticationFailureMessage}");
        return false;
    }

    public async Task LoadAllServersAsync()
    {
        if (IsSyncing || _appConfig is null) return;

        var selectedHostId = _selectedServer?.HostId;
        var selectedServerPublicId = _selectedServer?.ServerPublicId;

        try
        {
            var activeServer = _selectedServer;
            var effectiveActive = activeServer;
            if (activeServer is { IsSecondary: true } && _appConfig is not null)
            {
                var host = activeServer.Host;
                var primaryId = activeServer.PrimaryServerPublicId;
                effectiveActive = Servers.FirstOrDefault(s =>
                                      s.HostId == host.Id &&
                                      string.Equals(s.ServerPublicId, primaryId, StringComparison.OrdinalIgnoreCase))
                                  ?? activeServer;
            }

            var effectiveActiveHostId = effectiveActive?.HostId;
            var effectiveActiveServerPublicId = effectiveActive?.ServerPublicId;

            if (effectiveActive is not null && !effectiveActive.IsSecondary)
                await LoadContentAsync(silent: false);

            foreach (var server in Servers.ToList())
            {
                if (server.IsSecondary) continue;
                if (string.Equals(server.HostId, effectiveActiveHostId, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(server.ServerPublicId, effectiveActiveServerPublicId, StringComparison.OrdinalIgnoreCase))
                    continue;

                await LoadServerSilentlyAsync(server);
            }

            MirrorPrimaryToSecondaries();
        }
        finally
        {
            ReselectCurrentServerByIdentity(selectedHostId, selectedServerPublicId);

            foreach (var server in Servers)
                server.IsRefreshing = false;

            IsSyncing = false;
            SyncProgress = 0;
            DisplayCachedContent();
            OnPropertyChanged(nameof(IsActivityBarVisible));
            OnPropertyChanged(nameof(IsActivityBarIndeterminate));
            OnPropertyChanged(nameof(ActivityBarProgress));
        }
    }

    private async Task LoadServerSilentlyAsync(ServerItemViewModel server)
    {
        if (_appConfig is null || server.IsSecondary) return;

        var host = FindCurrentHost(server);
        var config = _appConfig.ToRuntimeConfig(host, server.ServerPublicId);
        if (config is null) return;

        server.IsRefreshing = true;
        OnPropertyChanged(nameof(IsActivityBarVisible));
        MirrorPrimaryToSecondaries(server);

        if (IsSelectedServerIdentity(server))
        {
            IsSyncing = true;
            LastSyncText = "Refreshing…";
        }

        try
        {
            AppConfigStore.ValidateForRuntime(config);

            if (!config.IsEnrolled)
            {
                MarkServerAuthenticationProblem(server, InvalidAuthenticationMessage, appendConsole: true);
                return;
            }

            using var apiClient = CreateApiClient(config);
            using var cts = new CancellationTokenSource(
                TimeSpan.FromSeconds(Math.Clamp(config.StartupTimeoutSeconds, 10, 300)));

            HealthResponse health;
            try
            {
                health = await apiClient.GetHostHealthAsync(cts.Token, clientVersion: _clientVersion);
            }
            catch
            {
                server.IsOnline = false;
                AppendConsole($"[{server.DisplayName}] Offline (health check failed)");
                return;
            }

            if (!health.Ok)
            {
                if (IsAuthenticationInvalidResponse(health, apiClient))
                {
                    await HandleAuthenticationFailureAsync(server, config, showStatusNote: false);
                    return;
                }

                server.IsOnline = false;
                AppendConsole($"[{server.DisplayName}] Offline (server error)");
                return;
            }

            ManagedServerAccessGate.Record(health);
            RefreshManagedAccessVisualState();

            server.IsOnline = true;

            host = FindCurrentHost(server);
            if (host is not null && ServerRosterSync.ApplyHealth(host, health))
            {
                await RefreshServerHealthVisualsAsync(apiClient, host, health, cts.Token);
                await Task.Run(() => AppConfigStore.SaveAppConfig(_appConfig!));
                var previousHostId = server.HostId;
                var previousServerPublicId = server.ServerPublicId;
                await PopulateServersAsync(_appConfig!);
                var refreshedServer = Servers.FirstOrDefault(s =>
                    string.Equals(s.HostId, previousHostId, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(s.ServerPublicId, previousServerPublicId, StringComparison.OrdinalIgnoreCase));
                if (refreshedServer is null)
                {
                    AppendConsole($"[{previousServerPublicId}] No longer available");
                    return;
                }

                server = refreshedServer;
                server.IsRefreshing = true;
                OnPropertyChanged(nameof(IsActivityBarVisible));
                host = FindCurrentHost(server);
                config = _appConfig.ToRuntimeConfig(host, server.ServerPublicId);
            }
            else if (host is not null)
            {
                await RefreshServerHealthVisualsAsync(apiClient, host, health, cts.Token);
            }

            if (host is not null)
                ArmServerMotdsForHost(host.Id, except: server);

            var manifest = await apiClient.GetManifestAsync(new ManifestRequest
            {
                ClientVersion = _clientVersion
            }, cts.Token);

            if (!manifest.Ok || !manifest.Authorized)
            {
                if (IsAuthenticationInvalidResponse(manifest, apiClient))
                {
                    await HandleAuthenticationFailureAsync(server, config, showStatusNote: false);
                }
                else
                {
                    server.UpdateStatus = ServerUpdateStatus.UpdateFailed;
                    server.CachedStatusText = manifest.Message ?? manifest.Error ?? "Manifest request failed";
                    AppendConsole($"[{server.DisplayName}] Refresh failed: {server.CachedStatusText}");
                }

                return;
            }

            ManagedServerAccessGate.RecordManifest(manifest, config.MinecraftUuid);
            RefreshManagedAccessVisualState();

            var currentServer = FindCurrentServer(server);
            var promotion = RuntimeVersionPromoter.ApplyManifestTarget(currentServer, ModpackInstallationRequirements.RuntimeTarget(manifest));
            if (promotion is not null && _appConfig is not null)
            {
                host?.AlignSecondaryRuntimeVersions();
                await Task.Run(() => AppConfigStore.SaveAppConfig(_appConfig));
                if (currentServer is not null && !ReferenceEquals(currentServer, server.Server))
                    CopyRuntimeVersion(currentServer, server.Server);
                config = _appConfig.ToRuntimeConfig(host, server.ServerPublicId);
                AppendConsole($"[{server.DisplayName}] {promotion.ConsoleMessage}");

                if (IsSelectedServerIdentity(server))
                {
                    RebuildConfig();
                    OnPropertyChanged(nameof(SelectedServerSubtitle));
                    OnPropertyChanged(nameof(SelectedServerTargetSubtitle));
                    OnPropertyChanged(nameof(SelectedServerHasVersionChange));
                    OnPropertyChanged(nameof(SelectedServerSubtitleIsVisible));
                    OnPropertyChanged(nameof(IsLaunchEnabled));
                }
            }

            var plan = await Task.Run(() =>
            {
                return BuildServerPlanFromLocalInventory(server, config, manifest);
            });

            var totalItems = plan.Actions.Count;
            var pendingActions = plan.Actions.Count(a =>
                a.Kind is SyncActionKind.Download or SyncActionKind.Replace or SyncActionKind.Remove);
            var statusMsg = pendingActions > 0
                ? $"[{server.DisplayName}] Ready — {pendingActions} pending action(s)"
                : plan.Actions.Any(a => a.Kind == SyncActionKind.Missing)
                    ? $"[{server.DisplayName}] Ready — {plan.Actions.Count(a => a.Kind == SyncActionKind.Missing)} missing item(s)"
                    : $"[{server.DisplayName}] Up to date";

            server.CachedItems.Clear();
            foreach (var item in ContentItemViewModel.OrderForDisplay(plan.Actions.Select(action => new ContentItemViewModel(action))))
                server.CachedItems.Add(item);
            server.CachedPendingActions = pendingActions;
            server.CachedFailedActions = 0;
            server.CachedTotalItems = totalItems;
            server.CachedStatusText = statusMsg;
            server.CachedManifest = manifest;
            server.CachedPlan = plan;
            server.HasCachedData = true;

            await Task.Run(() => PersistServerContentInventory(server, plan, config));

            server.UpdateStatus = pendingActions > 0
                ? ServerUpdateStatus.UpdatesWaiting
                : ServerUpdateStatus.NoUpdates;

            ArmServerMotd(server);
            AppendConsole(statusMsg);
        }
        catch (Exception ex)
        {
            LauncherLog.Write($"LoadServerSilentlyAsync({server.DisplayName}) failure: {ex}");
            server.IsOnline = false;
            server.UpdateStatus = ServerUpdateStatus.UpdateFailed;
            AppendConsole($"[{server.DisplayName}] Failed to refresh");
        }
        finally
        {
            server.IsRefreshing = false;
            OnPropertyChanged(nameof(IsActivityBarVisible));
            MirrorPrimaryToSecondaries(server);

            if (IsSelectedServerIdentity(server))
                DisplayCachedContent();
        }
    }

    public async Task LoadContentAsync(bool silent = false)
    {
        var requestedServer = SelectedServer;
        if (requestedServer is null || _config is null) return;
        if (!silent && IsSyncing) return;

        if (requestedServer.IsSecondary)
        {
            var primary = FindPrimaryServer(requestedServer);
            if (primary is not null)
            {
                var requestedHostId = requestedServer.HostId;
                var requestedServerPublicId = requestedServer.ServerPublicId;
                var primaryHostId = primary.HostId;
                var primaryServerPublicId = primary.ServerPublicId;

                await LoadServerContentAsync(primary, silent: true);

                // A refresh can rebuild Servers, so never mirror from or restore
                // view models captured before the refresh. Resolve the current
                // instances by identity instead.
                var currentPrimary = FindServerByIdentity(primaryHostId, primaryServerPublicId);
                var currentSecondary = FindServerByIdentity(requestedHostId, requestedServerPublicId);
                if (currentPrimary is not null)
                    MirrorPrimaryToSecondaries(currentPrimary);

                if (!silent && currentSecondary is not null &&
                    IsSelectedServerIdentity(requestedHostId, requestedServerPublicId))
                {
                    DisplayCachedContent();
                    ArmServerMotd(currentSecondary);
                }
            }
            return;
        }

        await LoadServerContentAsync(requestedServer, silent);
    }

    private async Task LoadServerContentAsync(ServerItemViewModel initialServer, bool silent)
    {
        if (!silent && IsSyncing) return;

        ServerItemViewModel? loadingServer = initialServer;
        var requestedHostId = loadingServer?.HostId;
        var requestedServerPublicId = loadingServer?.ServerPublicId;
        RuntimeConfig? refreshConfig = _config;
        bool IsStillActive() => IsSelectedServerIdentity(requestedHostId, requestedServerPublicId);

        if (!silent && IsStillActive())
        {
            IsSyncing = true;
            SyncProgress = 0;
            LastSyncText = "Refreshing…";

            TotalItems = 0;
            PendingActions = 0;
            FailedActions = 0;
        }

        if (loadingServer is not null)
        {
            loadingServer.IsRefreshing = true;
            OnPropertyChanged(nameof(IsActivityBarVisible));
            if (!loadingServer.IsSecondary)
                MirrorPrimaryToSecondaries(loadingServer);
        }

        try
        {
            await Task.Run(() => _appConfig = AppConfigStore.LoadAppConfig());

            if (_appConfig is not null)
            {
                await PopulateServersAsync(_appConfig);

                if (!string.IsNullOrWhiteSpace(requestedHostId) && !string.IsNullOrWhiteSpace(requestedServerPublicId))
                {
                    loadingServer = Servers.FirstOrDefault(s =>
                        string.Equals(s.HostId, requestedHostId, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(s.ServerPublicId, requestedServerPublicId, StringComparison.OrdinalIgnoreCase));
                }

                if (loadingServer is not null)
                {
                    loadingServer.IsRefreshing = true;
                    OnPropertyChanged(nameof(IsActivityBarVisible));
                    if (!loadingServer.IsSecondary)
                        MirrorPrimaryToSecondaries(loadingServer);
                }

                refreshConfig = loadingServer is not null
                    ? _appConfig.ToRuntimeConfig(FindCurrentHost(loadingServer), loadingServer.ServerPublicId)
                    : null;

                RebuildConfig();

                if (refreshConfig is not null)
                {
                    AppConfigStore.ValidateForRuntime(refreshConfig);
                    LauncherLog.Configure(refreshConfig.EnableEventLogging);
                }
            }

            if (refreshConfig is null || !refreshConfig.IsEnrolled)
            {
                var authenticationMessage = refreshConfig is null
                    ? "No server configured."
                    : InvalidAuthenticationMessage;

                if (loadingServer is not null)
                    MarkServerAuthenticationProblem(loadingServer, authenticationMessage, appendConsole: true);

                if (!silent && IsStillActive())
                {
                    StatusText = refreshConfig is null
                        ? "No server configured"
                        : "Sign in with your Minecraft account again";
                }
                return;
            }

            if (!silent && IsStillActive()) SyncProgress = 10;

            LauncherLog.Write("LoadContentAsync: health check starting.");

            using var apiClient = CreateApiClient(refreshConfig);
            using var cts = new CancellationTokenSource(
                TimeSpan.FromSeconds(Math.Clamp(refreshConfig.StartupTimeoutSeconds, 10, 300)));

            HealthResponse health;
            try
            {
                health = await apiClient.GetHostHealthAsync(cts.Token, clientVersion: _clientVersion);
            }
            catch (Exception ex)
            {
                if (!silent && IsStillActive()) StatusText = $"API unreachable: {ex.Message}";
                if (loadingServer is not null) loadingServer.IsOnline = false;
                return;
            }

            if (!health.Ok)
            {
                if (IsAuthenticationInvalidResponse(health, apiClient))
                {
                    await HandleAuthenticationFailureAsync(loadingServer, refreshConfig, showStatusNote: !silent && IsStillActive());
                }
                else if (!silent && IsStillActive())
                {
                    StatusText = health.Message ?? health.Error ?? "Health check failed";
                }

                if (loadingServer is not null) loadingServer.IsOnline = false;
                return;
            }

            if (loadingServer is not null) loadingServer.IsOnline = true;
            ManagedServerAccessGate.Record(health);
            RefreshManagedAccessVisualState();
            if (!silent && IsStillActive()) SyncProgress = 25;

            var healthHost = _appConfig?.Hosts.FirstOrDefault(h =>
                string.Equals(h.Id, loadingServer?.HostId, StringComparison.OrdinalIgnoreCase));
            if (healthHost is not null && ServerRosterSync.ApplyHealth(healthHost, health))
            {
                await RefreshServerHealthVisualsAsync(apiClient, healthHost, health, cts.Token);
                await Task.Run(() => AppConfigStore.SaveAppConfig(_appConfig!));
                await PopulateServersAsync(_appConfig!);
                if (loadingServer is not null)
                {
                    var previousHostId = loadingServer.HostId;
                    var previousServerPublicId = loadingServer.ServerPublicId;
                    loadingServer = Servers.FirstOrDefault(s =>
                        string.Equals(s.HostId, previousHostId, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(s.ServerPublicId, previousServerPublicId, StringComparison.OrdinalIgnoreCase));
                    if (loadingServer is null)
                    {
                        if (!silent)
                            StatusText = "Selected server is no longer available.";
                        return;
                    }
                }

                refreshConfig = loadingServer is not null
                    ? _appConfig!.ToRuntimeConfig(FindCurrentHost(loadingServer), loadingServer.ServerPublicId)
                    : null;
                if (refreshConfig is null)
                    return;
            }
            else if (healthHost is not null)
            {
                await RefreshServerHealthVisualsAsync(apiClient, healthHost, health, cts.Token);
            }

            if (healthHost is not null)
                ArmServerMotdsForHost(healthHost.Id, except: loadingServer);

            if (!silent && IsStillActive()) StatusText = "Requesting manifest…";
            var manifest = await apiClient.GetManifestAsync(new ManifestRequest
            {
                ClientVersion = _clientVersion
            }, cts.Token);

            if (!manifest.Ok || !manifest.Authorized)
            {
                if (IsAuthenticationInvalidResponse(manifest, apiClient))
                {
                    await HandleAuthenticationFailureAsync(loadingServer, refreshConfig, showStatusNote: !silent && IsStillActive());
                }
                else
                {
                    if (loadingServer is not null) loadingServer.UpdateStatus = ServerUpdateStatus.UpdateFailed;
                    var message = manifest.Message ?? manifest.Error ?? "Manifest request failed";
                    if (!silent && IsStillActive()) StatusText = message;
                    AppendConsole($"[{loadingServer?.DisplayName ?? "Server"}] Refresh failed: {message}");
                }

                return;
            }

            ManagedServerAccessGate.RecordManifest(manifest, refreshConfig.MinecraftUuid);
            RefreshManagedAccessVisualState();

            LauncherLog.Write($"Manifest received: {manifest.ModCount} mods, {manifest.ResourcePackCount} resource packs, {manifest.ShaderCount} shaders", EventIds.Client.ManifestReceived);

            if (!silent && IsStillActive()) SyncProgress = 50;

            var currentServer = FindCurrentServer(loadingServer);
            var promotion = RuntimeVersionPromoter.ApplyManifestTarget(currentServer, ModpackInstallationRequirements.RuntimeTarget(manifest));
            if (promotion is not null && _appConfig is not null)
            {
                FindCurrentHost(loadingServer)?.AlignSecondaryRuntimeVersions();
                await Task.Run(() => AppConfigStore.SaveAppConfig(_appConfig));
                if (currentServer is not null && loadingServer is not null && !ReferenceEquals(currentServer, loadingServer.Server))
                    CopyRuntimeVersion(currentServer, loadingServer.Server);
                refreshConfig = _appConfig.ToRuntimeConfig(FindCurrentHost(loadingServer), loadingServer?.ServerPublicId);
                if (refreshConfig is null)
                    return;

                if (IsStillActive())
                    RebuildConfig();
                AppendConsole($"[{loadingServer?.DisplayName ?? "Server"}] {promotion.ConsoleMessage}");
                OnPropertyChanged(nameof(SelectedServerSubtitle));
                OnPropertyChanged(nameof(SelectedServerTargetSubtitle));
                OnPropertyChanged(nameof(SelectedServerHasVersionChange));
                OnPropertyChanged(nameof(SelectedServerSubtitleIsVisible));
                OnPropertyChanged(nameof(IsLaunchEnabled));
            }

            if (!silent && IsStillActive()) StatusText = "Scanning local files…";
            var capturedConfig = refreshConfig;
            var capturedPlanServer = loadingServer;
            if (capturedPlanServer is null)
                return;

            var plan = await Task.Run(() =>
            {
                return BuildServerPlanFromLocalInventory(capturedPlanServer, capturedConfig, manifest);
            });

            var totalItems = plan.Actions.Count;
            var pendingActions = plan.Actions.Count(a =>
                a.Kind is SyncActionKind.Download or SyncActionKind.Replace or SyncActionKind.Remove);
            var serverLabel = loadingServer?.DisplayName ?? "Server";
            var statusMsg = pendingActions > 0
                ? $"[{serverLabel}] Ready — {pendingActions} pending action(s)"
                : plan.Actions.Any(a => a.Kind == SyncActionKind.Missing)
                    ? $"[{serverLabel}] Ready — {plan.Actions.Count(a => a.Kind == SyncActionKind.Missing)} missing item(s)"
                    : $"[{serverLabel}] Up to date";

            if (!silent && IsStillActive())
            {
                _manifest = manifest;
                _plan = plan;

                SyncProgress = 100;
                TotalItems = totalItems;
                PendingActions = pendingActions;
                FailedActions = 0;
                StatusText = statusMsg;
                ArmServerMotd(loadingServer);
                OnPropertyChanged(nameof(IsApplyEnabled));
                OnPropertyChanged(nameof(IsLaunchEnabled));
            }

            if (loadingServer is not null)
            {
                loadingServer.CachedItems.Clear();
                foreach (var item in ContentItemViewModel.OrderForDisplay(plan.Actions.Select(action => new ContentItemViewModel(action))))
                    loadingServer.CachedItems.Add(item);
                loadingServer.CachedPendingActions = pendingActions;
                loadingServer.CachedFailedActions = 0;
                loadingServer.CachedTotalItems = totalItems;
                loadingServer.CachedStatusText = statusMsg;
                loadingServer.CachedManifest = manifest;
                loadingServer.CachedPlan = plan;
                loadingServer.HasCachedData = true;

                await Task.Run(() => PersistServerContentInventory(loadingServer, plan, capturedConfig));

                loadingServer.UpdateStatus = pendingActions > 0
                    ? ServerUpdateStatus.UpdatesWaiting
                    : ServerUpdateStatus.NoUpdates;

                ArmServerMotd(loadingServer);

                if (!loadingServer.IsSecondary)
                    MirrorPrimaryToSecondaries(loadingServer);

                if (IsStillActive())
                {
                    OnPropertyChanged(nameof(SelectedServerTargetSubtitle));
                    OnPropertyChanged(nameof(SelectedServerHasVersionChange));
                    OnPropertyChanged(nameof(SelectedServerSubtitleIsVisible));
                }
            }
        }
        catch (ConfigurationIncompleteException ex)
        {
            LauncherLog.Write("LoadContentAsync configuration incomplete: " + ex.Message);
            if (!silent && IsStillActive()) StatusText = ex.Message;
            if (loadingServer is not null)
            {
                loadingServer.UpdateStatus = ServerUpdateStatus.UpdateFailed;
                if (!loadingServer.IsSecondary)
                    MirrorPrimaryToSecondaries(loadingServer);
            }
        }
        catch (Exception ex)
        {
            LauncherLog.Write("LoadContentAsync failure: " + ex);
            if (!silent && IsStillActive()) StatusText = $"Refresh failed: {ex.Message}";
            if (loadingServer is not null)
            {
                loadingServer.IsOnline = false;
                loadingServer.UpdateStatus = ServerUpdateStatus.UpdateFailed;
                if (!loadingServer.IsSecondary)
                    MirrorPrimaryToSecondaries(loadingServer);
            }
        }
        finally
        {
            if (loadingServer is not null)
            {
                loadingServer.IsRefreshing = false;
                OnPropertyChanged(nameof(IsActivityBarVisible));
                if (!loadingServer.IsSecondary)
                    MirrorPrimaryToSecondaries(loadingServer);
            }

            if (!silent)
            {
                IsSyncing = false;
                SyncProgress = 0;
            }

            if (!silent && IsStillActive())
            {
                LastSyncText = loadingServer?.LastSyncText ?? "Pending";
                DisplayCachedContent();
            }
        }
    }

    private static bool IsAuthenticationInvalidResponse(ApiResponseBase response, ApiClient apiClient)
    {
        return response.Code == 1010
            || apiClient.LastStatusCode == HttpStatusCode.Unauthorized
            || string.Equals(response.Error, "http_401", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<ClockSkewCheckResult> CheckClockSkewAsync(RuntimeConfig config)
    {
        using var cts = new CancellationTokenSource(
            TimeSpan.FromSeconds(Math.Clamp(config.StartupTimeoutSeconds, 10, 300)));

        return await ClockSkewDetector.CheckAsync(
            config.ApiBaseUrl,
            config.StartupTimeoutSeconds,
            _clientVersion,
            config.IgnoreCertificateErrors,
            cts.Token);
    }

    private async Task HandleAuthenticationFailureAsync(
        ServerItemViewModel? server,
        RuntimeConfig config,
        bool showStatusNote)
    {
        var skewCheck = await CheckClockSkewAsync(config);
        if (skewCheck.HasClockSkewWarning)
        {
            var warning = skewCheck.WarningMessage!;
            MarkServerAuthenticationProblem(server, warning, appendConsole: true);

            if (showStatusNote)
                StatusText = warning;
            return;
        }

        if (skewCheck.ProbeSucceeded)
        {
            await InvalidateAuthenticationAsync(server, showStatusNote);
            return;
        }

        MarkServerAuthenticationProblem(server, InconclusiveAuthenticationFailureMessage, appendConsole: true);

        if (showStatusNote)
            StatusText = InconclusiveAuthenticationFailureMessage;
    }
}
