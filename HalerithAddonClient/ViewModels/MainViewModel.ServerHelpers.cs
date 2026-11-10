using HalerithAddonClient.Core;
using Avalonia.Threading;
using HalerithAddonClient.Services;

namespace HalerithAddonClient.ViewModels;

public sealed partial class MainViewModel
{
    // ══════════════════════════════════════════════════════════════════════
    //  Helpers — server list population
    // ══════════════════════════════════════════════════════════════════════

    private async Task PopulateServersAsync(AppConfig config)
    {
        var selectedAccountUuid = AccountStore.GetDefaultAccountId();
        List<ServerConfig> Visible(HostConfig host) => DemoMode.Enabled
            ? DemoMode.VisibleServers(host.Servers)
            : host.VisibleServersFor(selectedAccountUuid);
        var previousItems = Servers
            .ToDictionary(
                s => ServerStateKey(s.HostId, s.ServerPublicId),
                s => s,
                StringComparer.OrdinalIgnoreCase);

        // Determine which hosts have multiple servers so ShowHostLabel can be set.
        var multiServerHostIds = config.Hosts
            .Where(h => Visible(h).Count > 1)
            .Select(h => h.Id)
            .ToHashSet(StringComparer.Ordinal);

        ServerItemViewModel? restore = null;

        // Build the flat list, then sort alphabetically by display name.
        var items = new List<ServerItemViewModel>();
        foreach (var host in config.Hosts)
        {
            foreach (var server in Visible(host))
            {
                var item = new ServerItemViewModel(host, server)
                {
                    ShowHostLabel = multiServerHostIds.Contains(host.Id)
                };

                if (previousItems.TryGetValue(ServerStateKey(host.Id, server.ServerPublicId), out var previous))
                    CopyTransientServerState(previous, item);

                if (!item.HasCachedData)
                    await LoadCachedServerContentAsync(item);

                items.Add(item);
            }
        }

        items.Sort((a, b) =>
        {
            var comparison = string.Compare(DemoMode.Enabled ? a.Server.ServerName : a.DisplayName,
                DemoMode.Enabled ? b.Server.ServerName : b.DisplayName, StringComparison.OrdinalIgnoreCase);
            return comparison != 0 || !DemoMode.Enabled ? comparison
                : string.Compare(a.ServerPublicId, b.ServerPublicId, StringComparison.OrdinalIgnoreCase);
        });
        for (var i = 0; i < items.Count; i++) items[i].DemoIndex = i;

        // Preserve the latest selection, including changes made while cache reads were awaiting.
        var prevHostId = _selectedServer?.HostId;
        var prevServerPublicId = _selectedServer?.ServerPublicId;
        Servers.Clear();
        foreach (var item in items)
        {
            Servers.Add(item);

            if (item.HostId   == prevHostId &&
                item.ServerPublicId == prevServerPublicId)
                restore = item;
        }

        // Restore selection or fall through to auto-select in LoadServersAsync.
        if (restore is not null)
        {
            SelectedServer = restore;
        }
        else if (prevServerPublicId is not null)
        {
            SelectedServer = Servers.FirstOrDefault();
        }
    }

    private static string ServerStateKey(string hostId, string serverPublicId) =>
        $"{hostId.Trim()}|{serverPublicId.Trim()}";

    private ServerItemViewModel? FindServerByIdentity(string? hostId, string? serverPublicId)
    {
        if (string.IsNullOrWhiteSpace(hostId) || string.IsNullOrWhiteSpace(serverPublicId))
            return null;

        return Servers.FirstOrDefault(s =>
            string.Equals(s.HostId, hostId, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(s.ServerPublicId, serverPublicId, StringComparison.OrdinalIgnoreCase));
    }

    private bool IsSelectedServerIdentity(ServerItemViewModel? server)
        => server is not null && IsSelectedServerIdentity(server.HostId, server.ServerPublicId);

    private async Task RefreshServerHealthVisualsAsync(
        ApiClient apiClient,
        HostConfig host,
        HealthResponse health,
        CancellationToken ct)
    {
        foreach (var entry in HealthServers(health))
        {
            if (string.IsNullOrWhiteSpace(entry.ServerPublicId))
                continue;

            var serverConfig = host.Servers.FirstOrDefault(s =>
                string.Equals(s.ServerPublicId, entry.ServerPublicId, StringComparison.OrdinalIgnoreCase));
            if (serverConfig is null)
                continue;

            serverConfig.PowerStatus = ServerPowerStates.Normalize(entry.PowerStatus);

            var iconPath = await Task.Run(() => ImageCacheService.ResolveServerIconPathAsync(
                apiClient,
                host.Id,
                serverConfig.ServerPublicId,
                serverConfig.ServerName,
                entry.Icon,
                downloadedPath => ApplyServerIconPath(host.Id, serverConfig.ServerPublicId, downloadedPath),
                ct));

            serverConfig.ServerIconLocalPath = iconPath ?? "";
            ApplyServerHealthVisuals(host.Id, serverConfig.ServerPublicId, serverConfig.PowerStatus, iconPath);
        }
    }

    private static IEnumerable<HealthServerEntry> HealthServers(HealthResponse health)
    {
        if (health.Servers.Count > 0)
            return health.Servers;

        return health.Environments
            .SelectMany(environment => environment.Servers)
            .Where(server => !string.IsNullOrWhiteSpace(server.ServerPublicId));
    }

    private void ApplyServerIconPath(string hostId, string serverPublicId, string path)
        => ApplyServerHealthVisuals(hostId, serverPublicId, null, path);

    private void ApplyServerHealthVisuals(string hostId, string serverPublicId, string? powerStatus, string? iconPath)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => ApplyServerHealthVisuals(hostId, serverPublicId, powerStatus, iconPath));
            return;
        }

        var vm = Servers.FirstOrDefault(s =>
            string.Equals(s.HostId, hostId, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(s.ServerPublicId, serverPublicId, StringComparison.OrdinalIgnoreCase));
        if (vm is null)
            return;

        if (!string.IsNullOrWhiteSpace(powerStatus))
        {
            vm.Server.PowerStatus = ServerPowerStates.Normalize(powerStatus);
            vm.RefreshPowerStatus();
        }

        if (iconPath is not null)
            vm.LoadServerIcon(iconPath);

        if (ReferenceEquals(vm, _selectedServer))
        {
            OnPropertyChanged(nameof(IsLaunchEnabled));
            OnPropertyChanged(nameof(LauncherNotConfiguredReason));
        }
    }

    private bool IsSelectedServerIdentity(string? hostId, string? serverPublicId)
        => _selectedServer is not null &&
           !string.IsNullOrWhiteSpace(hostId) &&
           !string.IsNullOrWhiteSpace(serverPublicId) &&
           string.Equals(_selectedServer.HostId, hostId, StringComparison.OrdinalIgnoreCase) &&
           string.Equals(_selectedServer.ServerPublicId, serverPublicId, StringComparison.OrdinalIgnoreCase);

    private void ReselectCurrentServerByIdentity(string? hostId, string? serverPublicId)
    {
        ServerItemViewModel? current = null;
        if (!string.IsNullOrWhiteSpace(hostId) && !string.IsNullOrWhiteSpace(serverPublicId))
        {
            current = Servers.FirstOrDefault(s =>
                string.Equals(s.HostId, hostId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(s.ServerPublicId, serverPublicId, StringComparison.OrdinalIgnoreCase));
        }

        current ??= _selectedServer is not null
            ? Servers.FirstOrDefault(s =>
                string.Equals(s.HostId, _selectedServer.HostId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(s.ServerPublicId, _selectedServer.ServerPublicId, StringComparison.OrdinalIgnoreCase))
            : null;

        current ??= Servers.FirstOrDefault();

        if (current is not null && !ReferenceEquals(_selectedServer, current))
        {
            SelectedServer = current;
            return;
        }

        if (current is null && _selectedServer is not null)
            SelectedServer = null;
    }

    private static void CopyTransientServerState(ServerItemViewModel source, ServerItemViewModel target)
    {
        target.CachedItems.Clear();
        foreach (var item in source.CachedItems)
            target.CachedItems.Add(item);

        target.CachedPendingActions = source.CachedPendingActions;
        target.CachedFailedActions  = source.CachedFailedActions;
        target.CachedTotalItems     = source.CachedTotalItems;
        target.CachedStatusText     = source.CachedStatusText;
        target.CachedManifest       = source.CachedManifest;
        target.CachedPlan           = source.CachedPlan;
        target.HasCachedData        = source.HasCachedData;
        target.LastSyncAt           = source.LastSyncAt;
        target.UpdateStatus         = source.UpdateStatus;
        target.IsOnline             = source.IsOnline;
        target.IsRefreshing         = source.IsRefreshing;
        target.RuntimeBuildStatus   = source.RuntimeBuildStatus;
        target.MotdStripText        = source.MotdStripText;
        target.MotdStripRemainingSeconds = source.MotdStripRemainingSeconds;
        target.IsMotdStripDismissed = source.IsMotdStripDismissed;
    }

    /// <summary>
    /// Clears transient cache and status data for a server item so stale content
    /// does not remain visible after auth or connectivity failures.
    /// </summary>
    private void ClearServerCache(ServerItemViewModel server)
    {
        server.CachedItems.Clear();
        server.CachedPendingActions = 0;
        server.CachedFailedActions = 0;
        server.CachedTotalItems = 0;
        server.CachedStatusText = "";
        server.CachedManifest = null;
        server.CachedPlan = null;
        server.HasCachedData = false;
        server.IsRefreshing = false;
        server.RuntimeBuildStatus = ServerRuntimeBuildStatus.Unknown;
        server.MotdStripText = "";
        server.MotdStripRemainingSeconds = 0;
        server.IsMotdStripDismissed = true;
    }

    private ServerItemViewModel? GetPrimaryOrSelf(ServerItemViewModel? server)
    {
        if (server is null)
            return null;

        return server.IsSecondary
            ? FindPrimaryServer(server) ?? server
            : server;
    }

    private void MarkServerAuthenticationProblem(ServerItemViewModel? server, string message, bool appendConsole)
    {
        var target = GetPrimaryOrSelf(server);
        if (target is null)
        {
            if (appendConsole)
                AppendConsole($"[Server] {message}");
            return;
        }

        target.IsOnline = false;
        target.UpdateStatus = ServerUpdateStatus.UpdateFailed;
        target.CachedStatusText = message;

        if (!target.IsSecondary)
            MirrorPrimaryToSecondaries(target);

        if (appendConsole)
            AppendConsole($"[{target.DisplayName}] {message}");
    }

    private static async Task LoadCachedServerContentAsync(ServerItemViewModel server)
    {
        try
        {
            var inventory = await Task.Run(() => AppStorageDatabase.LoadServerContentInventory(server.HostId, server.ServerPublicId));
            if (inventory.Count == 0)
                return;

            server.CachedItems.Clear();
            foreach (var item in inventory)
                server.CachedItems.Add(new ContentItemViewModel(item, isLocal: false));

            server.CachedTotalItems = inventory.Count;
            server.CachedPendingActions = 0;
            server.CachedFailedActions = 0;
            server.CachedStatusText = "Pending refresh";
            server.CachedManifest = null;
            server.CachedPlan = null;
            server.HasCachedData = true;
            server.UpdateStatus = ServerUpdateStatus.Pending;
        }
        catch (Exception ex)
        {
            LauncherLog.Write($"Failed to load cached server content for {server.DisplayName}: {ex}");
        }
    }

    /// <summary>
    /// Marks the active Minecraft account as needing authentication, removes
    /// remote servers from the sidebar, and moves selection to the next valid target.
    /// </summary>
    private async Task InvalidateAuthenticationAsync(ServerItemViewModel? server, bool showStatusNote)
    {
        const string note = "Minecraft authentication is no longer valid. Sign in with your Minecraft account again.";
        await Task.CompletedTask;

        if (showStatusNote)
            StatusText = note;

        MarkServerAuthenticationProblem(server, note, appendConsole: true);

        if (server is null)
            return;

        var target = GetPrimaryOrSelf(server) ?? server;
        ClearServerCache(target);

        var removedServers = Servers
            .Where(s => string.Equals(s.HostId, server.HostId, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (removedServers.Count == 0)
            return;

        var removedSelected = removedServers.Any(s => ReferenceEquals(s, _selectedServer));

        foreach (var removed in removedServers)
        {
            removed.IsOnline = false;
            removed.UpdateStatus = ServerUpdateStatus.UpdateFailed;
            ClearServerCache(removed);
            Servers.Remove(removed);
        }

        if (removedSelected)
            SelectedServer = Servers.FirstOrDefault();

        if (Servers.Count == 0)
            RequestSelectFirstLocalInstance?.Invoke(this, EventArgs.Empty);
    }

    // ══════════════════════════════════════════════════════════════════════
    //  Helpers — secondary server mirroring
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Finds the primary <see cref="ServerItemViewModel"/> for a secondary server.
    /// Returns null if the primary is not found in the <see cref="Servers"/> list.
    /// </summary>
    private ServerItemViewModel? FindPrimaryServer(ServerItemViewModel secondary)
    {
        if (!secondary.IsSecondary) return null;
        return Servers.FirstOrDefault(s =>
            s.HostId == secondary.HostId &&
            string.Equals(s.ServerPublicId, secondary.PrimaryServerPublicId, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Copies cached data (items, status, pips) from each primary server to all
    /// of its secondaries.  Called after all primary servers have been loaded.
    /// </summary>
    private void MirrorPrimaryToSecondaries()
    {
        foreach (var secondary in Servers.Where(s => s.IsSecondary))
        {
            var primary = FindPrimaryServer(secondary);
            if (primary is null) continue;
            MirrorCacheFromPrimary(primary, secondary);
        }

        OnPropertyChanged(nameof(IsActivityBarVisible));
    }

    private void MirrorPrimaryToSecondaries(ServerItemViewModel primary)
    {
        var selectedWasMirrored = false;
        foreach (var secondary in GetSecondariesOf(primary))
        {
            MirrorCacheFromPrimary(primary, secondary);
            if (ReferenceEquals(_selectedServer, secondary))
                selectedWasMirrored = true;
        }

        OnPropertyChanged(nameof(IsActivityBarVisible));
        OnPropertyChanged(nameof(IsLaunchEnabled));

        if (selectedWasMirrored)
            DisplayCachedContent();
    }

    /// <summary>
    /// Copies cached data from a primary server to a single secondary.
    /// </summary>
    private static void MirrorCacheFromPrimary(ServerItemViewModel primary, ServerItemViewModel secondary)
    {
        secondary.AlignRuntimeVersionFrom(primary.Server);

        if (primary.HasCachedData)
        {
            secondary.CachedItems.Clear();
            foreach (var item in primary.CachedItems)
                secondary.CachedItems.Add(item);
            secondary.CachedPendingActions = primary.CachedPendingActions;
            secondary.CachedFailedActions  = primary.CachedFailedActions;
            secondary.CachedTotalItems     = primary.CachedTotalItems;
            secondary.CachedStatusText     = primary.CachedStatusText;
            secondary.CachedManifest       = primary.CachedManifest;
            secondary.CachedPlan           = primary.CachedPlan;
            secondary.HasCachedData        = primary.HasCachedData;
        }

        secondary.UpdateStatus = primary.UpdateStatus;
        secondary.IsOnline     = primary.IsOnline;
        secondary.IsRefreshing = primary.IsRefreshing;
        secondary.RuntimeBuildStatus = primary.RuntimeBuildStatus;
    }

    /// <summary>
    /// Returns all secondary servers that mirror the given primary.
    /// </summary>
    private IEnumerable<ServerItemViewModel> GetSecondariesOf(ServerItemViewModel primary)
    {
        return Servers.Where(s =>
            s.IsSecondary &&
            s.HostId == primary.HostId &&
            string.Equals(s.PrimaryServerPublicId, primary.ServerPublicId, StringComparison.OrdinalIgnoreCase));
    }

    private HostConfig? FindCurrentHost(ServerItemViewModel? server)
    {
        if (server is null) return null;
        return _appConfig?.Hosts.FirstOrDefault(h =>
                   string.Equals(h.Id, server.HostId, StringComparison.OrdinalIgnoreCase))
               ?? server.Host;
    }

    private ServerConfig? FindCurrentServer(ServerItemViewModel? server)
    {
        if (server is null) return null;
        return FindCurrentHost(server)?.Servers.FirstOrDefault(s =>
            string.Equals(s.ServerPublicId, server.ServerPublicId, StringComparison.OrdinalIgnoreCase));
    }

    private static void CopyRuntimeVersion(ServerConfig source, ServerConfig target)
    {
        target.MinecraftVersion = source.MinecraftVersion;
        target.Loader = source.Loader;
        target.LoaderVersion = source.LoaderVersion;
    }

    // ══════════════════════════════════════════════════════════════════════
    //  Helpers — display cached content
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Restores the grid, summary counts, and status bar from the selected
    /// server's cache.  Called when the user clicks a different server so the
    /// UI updates instantly without a network round-trip.
    /// </summary>
    private void DisplayCachedContent()
    {
        if (_selectedServer is null || !_selectedServer.HasCachedData)
        {
            ContentItems.Clear();
            bool refreshing = _selectedServer?.IsRefreshing ?? false;
            SetStatusSilent(refreshing ? "Refreshing…" : "Select Refresh to load");
            TotalItems     = 0;
            PendingActions = 0;
            FailedActions  = 0;
            LastSyncText   = refreshing ? "Refreshing…"
                           : (_selectedServer?.LastSyncText ?? "Pending");
            OnPropertyChanged(nameof(IsActivityBarVisible));
            OnPropertyChanged(nameof(IsActivityBarIndeterminate));
            OnPropertyChanged(nameof(ActivityBarProgress));
            (RefreshCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            RefreshSelectedServerMotdDisplay();
            return;
        }
        ContentItems.ReplaceWith(SortContentItems(_selectedServer.CachedItems));
        TotalItems     = _selectedServer.CachedTotalItems;
        PendingActions = _selectedServer.CachedPendingActions;
        FailedActions  = _selectedServer.CachedFailedActions;
        SetStatusSilent(_selectedServer.IsRefreshing ? "Refreshing…" : _selectedServer.CachedStatusText);
        LastSyncText   = _selectedServer.IsRefreshing ? "Refreshing…" : _selectedServer.LastSyncText;
        OnPropertyChanged(nameof(IsActivityBarVisible));
        OnPropertyChanged(nameof(IsActivityBarIndeterminate));
        OnPropertyChanged(nameof(ActivityBarProgress));
        (RefreshCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();

        // Restore the manifest and plan so Sync Now targets the correct server.
        _manifest = _selectedServer.CachedManifest;
        _plan     = _selectedServer.CachedPlan;

        OnPropertyChanged(nameof(IsApplyEnabled));
        OnPropertyChanged(nameof(IsLaunchEnabled));
        OnPropertyChanged(nameof(PendingActionsForeground));
        OnPropertyChanged(nameof(SelectedServerTargetSubtitle));
        OnPropertyChanged(nameof(SelectedServerHasVersionChange));
        OnPropertyChanged(nameof(SelectedServerSubtitleIsVisible));
        RefreshSelectedServerMotdDisplay();
    }

    // ══════════════════════════════════════════════════════════════════════
    //  Helpers — content item population
    // ══════════════════════════════════════════════════════════════════════

    private void PopulateContentItems(SyncPlan plan)
    {
        ContentItems.ReplaceWith(ContentItemViewModel.OrderForDisplay(plan.Actions.Select(action => new ContentItemViewModel(action))));
    }

    private static IEnumerable<ContentItemViewModel> SortContentItems(IEnumerable<ContentItemViewModel> items)
        => ContentItemViewModel.OrderForDisplay(items);

    /// <summary>Projects one verified manual pack file into the existing local snapshot.</summary>
    public async Task RecordManualServerPackImportAsync(ServerItemViewModel server, ContentItemViewModel row, string installedPath)
    {
        if (!ReferenceEquals(_selectedServer, server) || server.IsRefreshing || IsSyncing || _config is null)
            return;
        var owner = server.IsSecondary ? FindPrimaryServer(server) ?? server : server;
        var index = owner.CachedItems.IndexOf(row);
        if (index < 0 || owner.CachedPlan is null || row.Action is not { } action
            || !owner.CachedPlan.Actions.Contains(action))
            return;

        ServerPackManualImport.MarkInstalled(action, installedPath);
        owner.CachedItems[index] = new ContentItemViewModel(action);
        OnPropertyChanged(nameof(IsLaunchEnabled));
        OnPropertyChanged(nameof(LauncherNotConfiguredReason));
        var missing = owner.CachedPlan.Actions.Count(item => item.Kind == SyncActionKind.Missing);
        owner.CachedStatusText = owner.CachedPendingActions > 0
            ? $"[{owner.DisplayName}] Ready — {owner.CachedPendingActions} pending action(s)"
            : missing > 0 ? $"[{owner.DisplayName}] Ready — {missing} missing item(s)"
            : $"[{owner.DisplayName}] Up to date";

        if (owner.IsSecondary)
            DisplayCachedContent();
        else
        {
            MirrorPrimaryToSecondaries(owner);
            if (ReferenceEquals(_selectedServer, owner)) DisplayCachedContent();
        }

        var config = _config;
        await Task.Run(() => PersistServerContentInventory(owner, owner.CachedPlan, config));
    }

    private static SyncPlan BuildServerPlanFromLocalInventory(
        ServerItemViewModel server,
        RuntimeConfig config,
        ManifestResponse manifest)
    {
        var inventory = VerifiedServerContentScanner.Scan(server.HostId, server.ServerPublicId, config);
        return ServerContentPlanner.Build(manifest, inventory, config);
    }

    private static void PersistServerContentInventory(ServerItemViewModel server, SyncPlan plan, RuntimeConfig config)
    {
        try
        {
            var inventory = ContentInventoryBuilder.FromServerPlan(plan, config);
            AppStorageDatabase.SaveServerContentInventory(server.HostId, server.ServerPublicId, inventory);
        }
        catch (Exception ex)
        {
            LauncherLog.Write($"Failed to persist server content inventory for {server.DisplayName}: {ex}");
        }
    }

    // ══════════════════════════════════════════════════════════════════════
    //  Helpers — RuntimeConfig assembly
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Rebuilds <see cref="_config"/> from the loaded <see cref="_appConfig"/>
    /// and the currently selected server.  A no-op when either is null.
    /// </summary>
    private void RebuildConfig()
    {
        if (_appConfig is null || _selectedServer is null)
        {
            _config = null;
            return;
        }

        var host = FindCurrentHost(_selectedServer);
        _config = _appConfig.ToRuntimeConfig(
            host,
            _selectedServer.ServerPublicId);
    }

    // ══════════════════════════════════════════════════════════════════════
    //  Helpers — ApiClient factory
    // ══════════════════════════════════════════════════════════════════════

    private ApiClient CreateApiClient(RuntimeConfig config)
    {
        var launcherTrace = LauncherLog.TraceAction();
        void Trace(string message)
        {
            launcherTrace(message);
#if HALERITH_DEBUG_BUILD
            AppendConsole(message);
#endif
        }

        return new ApiClient(config, traceLog: Trace);
    }
}
