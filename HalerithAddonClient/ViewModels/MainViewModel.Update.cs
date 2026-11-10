using Avalonia.Threading;
using HalerithAddonClient.Core;
using HalerithAddonClient.Services;

namespace HalerithAddonClient.ViewModels;

public sealed partial class MainViewModel
{
    public Func<ModpackFileReview, CancellationToken, Task<bool>>? ReviewModpackFilesAsync { get; set; }

    public async Task ReviewPackFilesAsync()
    {
        if (IsSyncing || _config is null || _manifest?.Modpack is not { } pack || _plan is null
            || ReviewModpackFilesAsync is null || pack.ForceConfig || _config.ModpackFilePolicy != ModpackFilePolicies.Review) return;
        var plan = _plan;
        var config = _config;
        pack = plan.Actions.First(a => a.Modpack is not null).Modpack!;
        var review = await Task.Run(() => ModpackOverlayService.Review(pack, config.GameDataPath, config.ModpackFilePolicy, reopen: true));
        if (!await ReviewModpackFilesAsync(review, default) || _plan != plan) return;
        var action = plan.Actions.First(a => a.Modpack is not null);
        action.ApprovedModpackReview = review;
        action.Kind = SyncActionKind.Download;
        action.ModpackStatus = "Apply pending";
        PendingActions = plan.Actions.Count(a => a.Kind is SyncActionKind.Download or SyncActionKind.Replace or SyncActionKind.Remove);
        if (_selectedServer is not null) _selectedServer.CachedPendingActions = PendingActions;
        PopulateContentItems(plan);
        StatusText = "Pack file choices are ready. Sync to apply them.";
    }
    // ══════════════════════════════════════════════════════════════════════
    //  Update execution
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Event raised when the update flow needs the selected Minecraft account checked.
    /// </summary>
    public event EventHandler<TaskCompletionSource<ManagedAccessCheckResult>>? RequestManagedAccessCheck;
    public event EventHandler<(IReadOnlyList<SyncAction> Actions, TaskCompletionSource<IReadOnlyList<SyncAction>?> Tcs)>? RequestMediumConfidenceUpdateConfirmation;

    private async Task RunUpdateAsync()
    {
        var gateHandler = RequestManagedAccessCheck;
        if (gateHandler is null)
        {
            StatusText = "Managed access check is not ready";
            return;
        }

        var tcs = new TaskCompletionSource<ManagedAccessCheckResult>();
        gateHandler.Invoke(this, tcs);
        var gate = await tcs.Task;

        if (!gate.Allowed)
        {
            StatusText = string.IsNullOrWhiteSpace(gate.Message)
                ? "Update cancelled"
                : gate.Message;
            return;
        }

        if (_config is null || _plan is null || IsSyncing) return;

        var syncingServer = _selectedServer;

        ServerItemViewModel? actualSyncServer = syncingServer;
        if (syncingServer is { IsSecondary: true })
        {
            actualSyncServer = FindPrimaryServer(syncingServer);
            if (actualSyncServer is null)
            {
                StatusText = "Cannot update — primary server not found";
                return;
            }
        }

        if (actualSyncServer is null) return;

        var syncConfig = actualSyncServer == syncingServer
            ? _config
            : _appConfig?.ToRuntimeConfig(actualSyncServer.Host, actualSyncServer.ServerPublicId) ?? _config;
        var syncManifest = actualSyncServer == syncingServer
            ? _manifest
            : actualSyncServer.CachedManifest ?? _manifest;
        var syncPlan = actualSyncServer == syncingServer
            ? _plan
            : actualSyncServer.CachedPlan ?? _plan;

        if (syncPlan is null) return;

        var mediumUpdates = SyncPlanConfirmation.GetMediumConfidenceUpdates(syncPlan);
        if (mediumUpdates.Count > 0)
        {
            var confirmTcs = new TaskCompletionSource<IReadOnlyList<SyncAction>?>();
            RequestMediumConfidenceUpdateConfirmation?.Invoke(this, (mediumUpdates, confirmTcs));
            var selectedMediumUpdates = await confirmTcs.Task;
            if (selectedMediumUpdates is null)
            {
                StatusText = "Update cancelled";
                return;
            }

            syncPlan = SyncPlanConfirmation.WithoutUnselectedMediumConfidenceUpdates(syncPlan, selectedMediumUpdates);
        }

        IsSyncing = true;
        SyncProgress = 0;
        LastSyncText = "Updating…";

        if (syncingServer is not null)
            syncingServer.IsRefreshing = true;
        if (actualSyncServer != syncingServer)
            actualSyncServer.IsRefreshing = true;
        OnPropertyChanged(nameof(IsActivityBarVisible));

        var startedAt = DateTime.UtcNow;
        var result = new SyncExecutionResult();
        string? fatalError = null;
        string? fatalErrorCode = null;
        bool authenticatedForReport = false;

        using var apiClient = CreateApiClient(syncConfig);
        using var cts = new CancellationTokenSource();

        try
        {
            StatusText = "Applying changes…";
            LauncherLog.Write("RunUpdateAsync starting.");

            var updateHealth = await apiClient.GetHostHealthAsync(cts.Token, clientVersion: _clientVersion);
            if (!updateHealth.Ok)
            {
                if (IsAuthenticationInvalidResponse(updateHealth, apiClient))
                {
                    await HandleAuthenticationFailureAsync(actualSyncServer ?? syncingServer, syncConfig, showStatusNote: true);
                    return;
                }

                StatusText = updateHealth.Message ?? updateHealth.Error ?? "Health check failed";
                if (actualSyncServer is not null)
                {
                    actualSyncServer.IsOnline = false;
                    actualSyncServer.UpdateStatus = ServerUpdateStatus.UpdateFailed;
                }
                if (syncingServer is not null && syncingServer != actualSyncServer)
                {
                    syncingServer.IsOnline = false;
                    syncingServer.UpdateStatus = ServerUpdateStatus.UpdateFailed;
                }
                return;
            }

            if (!ManagedServerAccessGate.HasAuthorizedServer(updateHealth, syncConfig.MinecraftUuid, actualSyncServer.ServerPublicId))
            {
                StatusText = "This server is not available to the selected Minecraft account. Refresh the server list.";
                return;
            }

            authenticatedForReport = true;

            if (actualSyncServer is not null)
                actualSyncServer.IsOnline = true;
            ManagedServerAccessGate.Record(updateHealth);
            RefreshManagedAccessVisualState();
            if (syncingServer is not null && syncingServer != actualSyncServer)
                syncingServer.IsOnline = true;

            var healthServer = actualSyncServer ?? syncingServer;
            var healthHost = healthServer is null ? null : FindCurrentHost(healthServer);
            if (healthHost is not null)
            {
                var healthChanged = ServerRosterSync.ApplyHealth(healthHost, updateHealth);
                await RefreshServerHealthVisualsAsync(apiClient, healthHost, updateHealth, cts.Token);
                if (healthChanged && _appConfig is not null)
                    await Task.Run(() => AppConfigStore.SaveAppConfig(_appConfig));
            }

            var totalWork = syncPlan.Actions.Count(a =>
                a.Kind is SyncActionKind.Download or SyncActionKind.Replace or SyncActionKind.Remove);
            var completed = 0;

            void ProgressLog(string msg)
            {
                LauncherLog.Write(msg);
                if (totalWork > 0)
                {
                    completed++;
                    SyncProgress = Math.Min(95, completed * 95.0 / totalWork);
                }
                StatusText = msg;
            }

            result = await new SyncExecutor(apiClient, syncConfig, ProgressLog)
                { ReviewModpackFilesAsync = ReviewModpackFilesAsync }
                .ExecuteAsync(syncPlan, cts.Token);

            StatusText = result.Failed > 0
                ? $"Update completed with {result.Failed} error(s)"
                : $"Update complete — downloaded: {result.Downloaded}, replaced: {result.Replaced}, removed: {result.Removed}";
            FailedActions = result.Failed;

            var syncStatus = result.Failed > 0
                ? ServerUpdateStatus.UpdateFailed
                : ServerUpdateStatus.NoUpdates;

            if (actualSyncServer is not null)
            {
                actualSyncServer.LastSyncAt = DateTime.UtcNow;
                actualSyncServer.UpdateStatus = syncStatus;
            }

            if (syncingServer is not null && syncingServer != actualSyncServer)
            {
                syncingServer.LastSyncAt = DateTime.UtcNow;
                syncingServer.UpdateStatus = syncStatus;
            }

            foreach (var sec in GetSecondariesOf(actualSyncServer ?? syncingServer!))
            {
                sec.LastSyncAt = DateTime.UtcNow;
                sec.UpdateStatus = syncStatus;
            }

            if (_selectedServer == syncingServer)
                LastSyncText = syncingServer?.LastSyncText ?? "Up to Date";

            var rescanServer = actualSyncServer ?? syncingServer;
            if (rescanServer is not null && syncConfig is not null && syncManifest is not null)
            {
                await RefreshLocalAsync(rescanServer, syncConfig, syncManifest, result.Failed);
                foreach (var sec in GetSecondariesOf(rescanServer))
                    MirrorCacheFromPrimary(rescanServer, sec);
            }

            SyncProgress = 100;
        }
        catch (Exception ex)
        {
            fatalError = ex.Message;
            fatalErrorCode = SyncFailureCodes.FromException(ex);
            LauncherLog.Write("RunUpdateAsync failure: " + ex);
            StatusText = $"Update failed: {ex.Message}";
            FailedActions = Math.Max(FailedActions, 1);
            if (syncingServer is not null)
                syncingServer.UpdateStatus = ServerUpdateStatus.UpdateFailed;
            if (actualSyncServer is not null && actualSyncServer != syncingServer)
                actualSyncServer.UpdateStatus = ServerUpdateStatus.UpdateFailed;
        }
        finally
        {
            if (authenticatedForReport)
            {
                try
                {
                    SetStatusSilent("Sending report…");
                    await apiClient.SendReportAsync(new ReportRequest
                    {
                        ClientVersion = _clientVersion,
                        Mode = "sync",
                        Status = fatalError is not null ? "failed" : result.Status,
                        StartedAt = startedAt.ToString("O"),
                        CompletedAt = DateTime.UtcNow.ToString("O"),
                        Details = SyncReportDetails.From(result, fatalErrorCode)
                    }, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    LauncherLog.Write("Report warning: " + ex.Message);
                }
            }

            if (syncingServer is not null)
                syncingServer.IsRefreshing = false;
            if (actualSyncServer is not null && actualSyncServer != syncingServer)
                actualSyncServer.IsRefreshing = false;

            var primaryToMirror = actualSyncServer ?? syncingServer;
            if (primaryToMirror is { IsSecondary: true })
                primaryToMirror = FindPrimaryServer(primaryToMirror) ?? primaryToMirror;
            if (primaryToMirror is not null && !primaryToMirror.IsSecondary)
                MirrorPrimaryToSecondaries(primaryToMirror);

            OnPropertyChanged(nameof(IsActivityBarVisible));
            IsSyncing = false;
            SyncProgress = 0;

            if (_selectedServer is { IsSecondary: true } || _selectedServer == primaryToMirror)
                DisplayCachedContent();
        }
    }

    // ══════════════════════════════════════════════════════════════════════
    //  Post-update local rescan
    // ══════════════════════════════════════════════════════════════════════

    private async Task RefreshLocalAsync(
        ServerItemViewModel syncedServer,
        RuntimeConfig syncConfig,
        ManifestResponse syncManifest,
        int failedActions)
    {
        try
        {
            var isActiveServer = _selectedServer == syncedServer;

            if (isActiveServer)
                StatusText = "Refreshing file list…";

            var plan = await Task.Run(() =>
            {
                return BuildServerPlanFromLocalInventory(syncedServer, syncConfig, syncManifest);
            });

            var totalItems = plan.Actions.Count;
            var pendingActions = plan.Actions.Count(a =>
                a.Kind is SyncActionKind.Download or SyncActionKind.Replace or SyncActionKind.Remove);

            var serverLabel = syncedServer.DisplayName;
            var statusMsg = pendingActions > 0
                ? $"[{serverLabel}] Ready — {pendingActions} pending action(s)"
                : $"[{serverLabel}] Up to date";

            syncedServer.CachedItems.Clear();
            foreach (var item in ContentItemViewModel.OrderForDisplay(plan.Actions.Select(action => new ContentItemViewModel(action))))
                syncedServer.CachedItems.Add(item);
            syncedServer.CachedPendingActions = pendingActions;
            syncedServer.CachedFailedActions = failedActions;
            syncedServer.CachedTotalItems = totalItems;
            syncedServer.CachedStatusText = statusMsg;
            syncedServer.CachedManifest = syncManifest;
            syncedServer.CachedPlan = plan;
            syncedServer.HasCachedData = true;

            await Task.Run(() => PersistServerContentInventory(syncedServer, plan, syncConfig));

            syncedServer.UpdateStatus = pendingActions > 0
                ? ServerUpdateStatus.UpdatesWaiting
                : ServerUpdateStatus.NoUpdates;

            if (_selectedServer == syncedServer)
            {
                _plan = plan;
                _manifest = syncManifest;
                ContentItems.ReplaceWith(SortContentItems(syncedServer.CachedItems));
                TotalItems = totalItems;
                PendingActions = pendingActions;
                FailedActions = failedActions;
                StatusText = statusMsg;
                LastSyncText = syncedServer.LastSyncText;
                OnPropertyChanged(nameof(IsApplyEnabled));
                OnPropertyChanged(nameof(IsLaunchEnabled));
            }
        }
        catch (Exception ex)
        {
            LauncherLog.Write("RefreshLocalAsync failure: " + ex);
        }
    }

}
