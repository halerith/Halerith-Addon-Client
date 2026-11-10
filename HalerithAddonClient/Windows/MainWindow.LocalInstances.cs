using System.IO;
using Avalonia.Controls;
using Avalonia.Media;
using HalerithAddonClient.Core;
using HalerithAddonClient.Core.ContentSearch;
using HalerithAddonClient.Services;
using HalerithAddonClient.ViewModels;

namespace HalerithAddonClient.Windows;

public partial class MainWindow
{
    // ══════════════════════════════════════════════════════════════════════
    //  Local instances — sidebar section
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>Currently selected local instance (null when a server is selected).</summary>
    private LocalInstanceItemViewModel? _selectedLocalInstance;
    private LocalInstanceItemViewModel? _localLaunchStateItem;

    /// <summary>
    /// Populates the Local Instances sidebar list and checks build readiness
    /// for each instance.
    /// </summary>
    private void PopulateLocalInstances()
    {
        var config = AppConfigStore.LoadAppConfig();
        var instances = config.LocalInstances;
        var selectedId = _selectedLocalInstance?.InstanceId;
        var existingItems = (_localInstanceListBox.ItemsSource as IEnumerable<LocalInstanceItemViewModel>)
            ?.GroupBy(i => i.InstanceId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase)
            ?? new Dictionary<string, LocalInstanceItemViewModel>(StringComparer.OrdinalIgnoreCase);

        _localInstancesHeader.IsVisible = true;
        _localInstanceListBox.IsVisible = true;

        var items = new List<LocalInstanceItemViewModel>();
        var itemsToCheck = new List<LocalInstanceItemViewModel>();
        foreach (var instance in instances.OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (existingItems.TryGetValue(instance.Id, out var item))
            {
                var runtimeChanged = item.UpdateInstance(instance);
                if (!item.HasCachedData)
                    LoadCachedLocalInstanceContent(item);

                items.Add(item);

                if (runtimeChanged || item.BuildStatus == LocalInstanceBuildStatus.Unknown)
                    itemsToCheck.Add(item);

                continue;
            }

            item = new LocalInstanceItemViewModel(instance);
            LoadCachedLocalInstanceContent(item);
            items.Add(item);
            itemsToCheck.Add(item);
        }

        // Wire selection changed
        _localInstanceListBox.SelectionChanged -= LocalInstanceListBox_SelectionChanged;
        _localInstanceListBox.ItemsSource = items;
        _localInstanceListBox.SelectionChanged += LocalInstanceListBox_SelectionChanged;

        var selected = items.FirstOrDefault(i =>
            string.Equals(i.InstanceId, selectedId, StringComparison.OrdinalIgnoreCase));
        _selectedLocalInstance = selected;
        _localInstanceListBox.SelectedItem = selected;
        UpdateLocalInstanceSelectionState(selected);

        if (selected is not null && _localInstancePanel.IsVisible)
            ShowLocalInstanceContent(selected);
        else if (selected is null && _localInstancePanel.IsVisible)
            ShowServerContent();

        // Check build readiness only for new or runtime-changed instances.
        if (itemsToCheck.Count > 0)
            _ = CheckLocalInstanceReadinessAsync(itemsToCheck);
    }

    private void LoadCachedLocalInstanceContent(LocalInstanceItemViewModel item)
    {
        try
        {
            var inventory = AppStorageDatabase.LoadLocalInstanceContentInventory(item.InstanceId);
            if (inventory.Count == 0)
                return;

            item.CachedItems.Clear();
            item.CachedItems.AddRange(SortContentRows(inventory.Select(row => new ContentItemViewModel(row, isLocal: true))));
            item.CachedTotalItems = inventory.Count;
            item.CachedPendingActions = 0;
            item.CachedStatusText = "Pending refresh";
            item.CachedPlan = null;
            item.HasCachedData = true;
            item.UpdateStatus = LocalInstanceUpdateStatus.Pending;
        }
        catch (Exception ex)
        {
            LauncherLog.Write($"Failed to load cached local content for {item.DisplayName}: {ex}");
        }
    }

    private static IEnumerable<ContentItemViewModel> SortContentRows(IEnumerable<ContentItemViewModel> rows)
        => ContentItemViewModel.OrderForDisplay(rows);

    /// <summary>
    /// Keeps the local-instance row selection state in sync with the sidebar selection.
    /// </summary>
    private void UpdateLocalInstanceSelectionState(LocalInstanceItemViewModel? selected)
    {
        if (_localInstanceListBox.ItemsSource is not IEnumerable<LocalInstanceItemViewModel> items)
            return;

        foreach (var item in items)
            item.IsSelected = ReferenceEquals(item, selected);
    }

    private bool SelectLocalInstanceById(string? instanceId)
    {
        if (string.IsNullOrWhiteSpace(instanceId)
            || _localInstanceListBox.ItemsSource is not IEnumerable<LocalInstanceItemViewModel> items)
            return false;

        var match = items.FirstOrDefault(i =>
            string.Equals(i.InstanceId, instanceId, StringComparison.OrdinalIgnoreCase));
        if (match is null)
            return false;

        _localInstanceListBox.SelectedItem = match;
        return true;
    }

    private bool ApplyConfiguredDefaultInstanceSelection()
    {
        if (DemoMode.Enabled) return false;
        var config = AppConfigStore.LoadAppConfig();
        if (!string.Equals(config.DefaultInstanceKind, AppConfig.DefaultInstanceKindLocal, StringComparison.OrdinalIgnoreCase))
            return false;

        if (!SelectLocalInstanceById(config.DefaultInstanceId))
            return false;

        _vm.SelectedServer = null;
        return true;
    }

    /// <summary>
    /// Checks whether each local instance's game environment has been built.
    /// </summary>
    private static async Task CheckLocalInstanceReadinessAsync(List<LocalInstanceItemViewModel> items)
    {
        foreach (var item in items)
        {
            try
            {
                // Check if there is an active build first
                var activeBuild = ActiveBuildTracker.Get(
                    item.Instance.MinecraftVersion,
                    item.Instance.Loader,
                    item.Instance.LoaderVersion);

                if (activeBuild is not null && !activeBuild.BuildTask.IsCompleted)
                {
                    item.BuildStatus = LocalInstanceBuildStatus.Building;
                    continue;
                }

                var runtimeRoot = System.IO.Path.Combine(LauncherPaths.GetAppDataDirectory(), "Runtime");
                using var db = new RuntimeDatabase(runtimeRoot);
                if (db.InstanceExists(item.Instance.MinecraftVersion, item.Instance.Loader, item.Instance.LoaderVersion))
                {
                    item.BuildStatus = LocalInstanceBuildStatus.Ready;
                    continue;
                }

                using var builder = new GameEnvironmentBuilder(database: db);
                var report = await builder.CheckReadinessAsync(
                    item.Instance.MinecraftVersion,
                    item.Instance.Loader,
                    item.Instance.LoaderVersion);

                if (report.IsReady)
                    await builder.RecordInstanceAsync(report);

                item.BuildStatus = report.IsReady
                    ? LocalInstanceBuildStatus.Ready
                    : LocalInstanceBuildStatus.NotBuilt;
            }
            catch
            {
                item.BuildStatus = LocalInstanceBuildStatus.NotBuilt;
            }
        }
    }

    /// <summary>
    /// When a local instance is selected, deselect the server list and
    /// show the local instance content view.
    /// </summary>
    private void LocalInstanceListBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_localInstanceListBox.SelectedItem is LocalInstanceItemViewModel selected)
        {
            _selectedLocalInstance = selected;
            UpdateLocalInstanceSelectionState(selected);

            // Deselect the server list so only one selection is active.
            _serverListBox.SelectedItem = null;

            // Show local instance content in the main area.
            ShowLocalInstanceContent(selected);
            _ = RefreshLocalInstanceContentAsync(selected);
        }
        else
        {
            _selectedLocalInstance = null;
            UpdateLocalInstanceSelectionState(null);
        }
    }

    private async void ServerSettingsGearButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        e.Handled = true;

        if (sender is not Button button || button.DataContext is not ServerItemViewModel item)
            return;

        var config = AppConfigStore.LoadAppConfig();
        var host = config.Hosts.FirstOrDefault(h =>
            string.Equals(h.Id, item.HostId, StringComparison.OrdinalIgnoreCase));
        var server = host?.Servers.FirstOrDefault(s =>
            string.Equals(s.ServerPublicId, item.ServerPublicId, StringComparison.OrdinalIgnoreCase));

        if (host is null || server is null)
        {
            _vm.AppendConsole("Server settings could not be opened because the server is no longer available.");
            return;
        }

        var dialog = new ServerSettingsWindow(host, server);
        var saved = await dialog.ShowDialog<bool?>(this) == true;

        if (saved)
        {
            host.AlignSecondaryRuntimeVersions();
            AppConfigStore.SaveAppConfig(config);
            await _vm.LoadServersAsync();
            UpdateAllServerRuntimeBuildStates();
        }
        else if (dialog.BuildChanged)
        {
            UpdateAllServerRuntimeBuildStates();
        }
    }

    private async void LocalInstanceSettingsGearButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        e.Handled = true;

        if (sender is not Button button || button.DataContext is not LocalInstanceItemViewModel item)
            return;

        var config = AppConfigStore.LoadAppConfig();
        var instance = config.LocalInstances.FirstOrDefault(i =>
            string.Equals(i.Id, item.InstanceId, StringComparison.OrdinalIgnoreCase));

        if (instance is null)
        {
            _vm.AppendConsole("Local instance settings could not be opened because the instance is no longer available.");
            PopulateLocalInstances();
            return;
        }

        if (item.IsRefreshing)
        {
            _vm.AppendConsole("Wait for this instance's content operation to finish before opening settings.");
            return;
        }

        var dialog = new LocalInstanceEditWindow(instance);
        await dialog.ShowDialog<object?>(this);

        if (dialog.Saved)
            AppConfigStore.SaveAppConfig(config);

        if (dialog.Saved || dialog.BuildChanged || dialog.Deleted)
        {
            PopulateLocalInstances();
            UpdateLocalLaunchButtonState();
        }
    }

    private async void LocalInstanceAddButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        e.Handled = true;

        var dialog = new AddLocalInstanceWindow();
        var result = await dialog.ShowDialog<LocalInstance?>(this);

        if (result is null)
            return;

        // Pack creation already persisted this instance before installing content.
        // Upsert also supports blank instances without replaying a stale config snapshot.
        AppStorageDatabase.SaveLocalInstance(result);

        PopulateLocalInstances();
        if (_localInstanceListBox.ItemsSource is IEnumerable<LocalInstanceItemViewModel> items)
        {
            var added = items.FirstOrDefault(i =>
                string.Equals(i.InstanceId, result.Id, StringComparison.OrdinalIgnoreCase));
            if (added is not null)
                _localInstanceListBox.SelectedItem = added;
        }
    }

    /// <summary>
    /// Opens Settings from the sidebar button and refreshes local instances on close.
    /// </summary>
    private async void SidebarSettingsButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var config = AppConfigStore.LoadAppConfig();

        var settingsWin = new SettingsWindow(
            config,
            _clientVersion,
            sessionHostId:   _vm.SelectedServer?.HostId,
            sessionServerPublicId: _vm.SelectedServer?.ServerPublicId,
            subscriptionAccountUuid: CurrentAccountForSettings()?.Uuid);

        var saved = await settingsWin.ShowDialog<bool?>(this) == true;
        if (settingsWin.SubscriptionAccountsModified)
            RefreshAccountSelectorForSettings();

        // Always refresh local instances — they may have been added/removed/built
        // even if the user cancelled the settings dialog.
        if (settingsWin.LocalInstancesChanged)
        {
            PopulateLocalInstances();
        }

        if (saved || settingsWin.SubscriptionAccountsModified)
        {
            await _vm.LoadServersAsync();
            await _vm.LoadAllServersAsync();
        }

        _vm.RefreshManagedAccessVisualState();
    }

    /// <summary>
    /// Switches the main content area to show the local instance details view.
    /// Restores cached content data if this instance has been checked before.
    /// </summary>
    private void ShowLocalInstanceContent(LocalInstanceItemViewModel item)
    {
        _serverContentPanel.IsVisible = false;
        _localInstancePanel.IsVisible = true;

        _localInstanceName.Text = item.DisplayName;
        _localInstanceInitials.Text = item.InstanceInitials;
        _localInstanceIconPanel.DataContext = item;
        _ = item.LoadProjectIconAsync();
        _localMinecraftPill.Text = item.MinecraftPillText;
        _localLoaderPill.Text = item.LoaderPillText;
        WatchLocalLaunchState(item);
        UpdateLocalLaunchButtonState();

        // Restore cached content data if the instance has been checked
        if (item.HasCachedData)
        {
            _localAddonGrid.ItemsSource = item.CachedItems;
            _localUpdateButton.IsEnabled = CanRunLocalUpdateAll(item);
            SetLocalSummary(item.CachedTotalItems, item.CachedPendingActions, item.CachedFailedActions, item.CachedStatusText, busy: item.IsRefreshing);
        }
        else
        {
            _localAddonGrid.ItemsSource  = null;
            _localUpdateButton.IsEnabled = false;
            SetLocalSummary(0, 0, 0, item.IsRefreshing ? "Refreshing..." : "Select Refresh to load", busy: item.IsRefreshing);
        }

        RefreshLocalAccountSelector();
    }

    private void SetLocalSummary(int totalItems, int pendingActions, int failedActions, string statusText, bool busy)
    {
        _localTotalItems.Text = totalItems.ToString();
        _localPendingActions.Text = pendingActions.ToString();
        _localFailedActions.Text = failedActions.ToString();
        _localStatusText.Text = string.IsNullOrWhiteSpace(statusText) ? "Ready" : statusText;
        _localIdleCountsPanel.IsVisible = !busy;
        _localProgressBar.IsVisible = busy;

        _localPendingActions.Foreground = pendingActions > 0
            ? new SolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B))
            : new SolidColorBrush(Color.FromRgb(0x10, 0xB9, 0x81));

        _localFailedActions.Foreground = failedActions > 0
            ? new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44))
            : new SolidColorBrush(Color.FromRgb(0x10, 0xB9, 0x81));
    }

    private static bool CanRunLocalUpdateAll(LocalInstanceItemViewModel item)
    {
        if (item.CachedPendingActions <= 0 || item.IsRefreshing)
            return false;

        return true;
    }

    /// <summary>
    /// Switches back to the server content view (called when a server is selected).
    /// </summary>
    private void ShowServerContent()
    {
        _localInstancePanel.IsVisible = false;
        _serverContentPanel.IsVisible = true;
        WatchLocalLaunchState(null);
        _selectedLocalInstance = null;
        RefreshAccountSelector();
        UpdateLocalLaunchButtonState();
    }

    private void WatchLocalLaunchState(LocalInstanceItemViewModel? item)
    {
        if (_localLaunchStateItem is not null)
            _localLaunchStateItem.PropertyChanged -= LocalLaunchStateItem_PropertyChanged;

        _localLaunchStateItem = item;

        if (_localLaunchStateItem is not null)
            _localLaunchStateItem.PropertyChanged += LocalLaunchStateItem_PropertyChanged;
    }

    private void LocalLaunchStateItem_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(LocalInstanceItemViewModel.BuildStatus)
            or nameof(LocalInstanceItemViewModel.IsLaunchable))
        {
            UpdateLocalLaunchButtonState();
        }
    }

    /// <summary>
    /// Refreshes the content list for the selected local instance by checking
    /// installed items against Modrinth/CurseForge for available updates.
    /// </summary>
    private async void LocalRefreshButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_selectedLocalInstance is null) return;
        await RefreshLocalInstanceContentAsync(_selectedLocalInstance);
    }

    /// <summary>
    /// Applies pending content updates for the selected local instance.
    /// Downloads newer versions, removes old files, then re-checks for freshness.
    /// </summary>
    private async void LocalUpdateButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_selectedLocalInstance is null) return;

        var item = _selectedLocalInstance;
        var plan = item.CachedPlan;
        if (plan is null || plan.PendingActions == 0) return;

        var inst = item.Instance;

        item.IsRefreshing = true;
        _localUpdateButton.IsEnabled = false;
        _localRefreshButton.IsEnabled = false;
        SetLocalSummary(item.CachedTotalItems, item.CachedPendingActions, item.CachedFailedActions, "Applying updates...", busy: true);
        _localProgressBar.IsIndeterminate = false;
        _localProgressBar.Maximum = 100;
        _localProgressBar.Value = 0;
        _vm.AppendConsole($"[{item.DisplayName}] Applying updates…");

        var failedActions = 0;
        try
        {
            var planner = new LocalContentPlanner { ApplyPackAsync = (pack, ct) => ApplyLocalPackAsync(item, pack, ct) };
            var result = await planner.ApplyPlanAsync(
                inst.Id,
                inst.GetInstanceDirectory(),
                plan,
                progress: (completed, total) =>
                {
                    Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    {
                        var pct = total > 0 ? (double)completed / total * 100 : 0;
                        _localProgressBar.Value = pct;
                    });
                });

            failedActions = result.Failed;
            item.CachedFailedActions = failedActions;
            _vm.AppendConsole(result.Failed > 0
                ? $"[{item.DisplayName}] Updated {result.Updated}, {result.Failed} failed"
                : $"[{item.DisplayName}] {result.Updated} item(s) updated");
        }
        catch (Exception ex)
        {
            failedActions = 1;
            item.CachedFailedActions = failedActions;
            _vm.AppendConsole($"[{item.DisplayName}] Update failed: {ex.Message}");
        }
        finally
        {
            _localProgressBar.IsVisible = false;
            _localProgressBar.IsIndeterminate = false;
            _localProgressBar.Value = 0;
            _localRefreshButton.IsEnabled = true;
            item.IsRefreshing = false;
        }

        // Re-check for freshness after applying updates
        await RefreshLocalInstanceContentAsync(item, failedActions);
    }

    /// <summary>
    /// Deletes a single local content item — removes the file from disk
    /// and the entry from SQLite, then refreshes the grid.
    /// </summary>
    private async void LocalDeleteContentButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_selectedLocalInstance is null) return;
        if (sender is not Avalonia.Controls.Button btn) return;
        if (btn.DataContext is not ContentItemViewModel vm) return;

        var item = _selectedLocalInstance;
        var inst = item.Instance;
        var planItem = vm.LocalPlanItem;

        try
        {
            var pack = Core.LocalModpacks.LocalModpackStore.Load(inst.GetInstanceDirectory());
            if (pack is not null && (planItem?.Pack is not null || planItem?.PackMember is not null || planItem?.IsBundledFile == true
                || pack.Members.Any(m => m.Project.Id.Length > 0 && m.Project.Id == planItem?.ManifestItem?.SourceId && m.Project.Source == planItem.ManifestItem.Source)))
                throw new InvalidOperationException("Use the modpack row or Edit pack member to remove pack-owned content.");
            // Delete the file from disk
            if (planItem?.UntrackedFilePath is { } untrackedPath && File.Exists(untrackedPath))
            {
                File.Delete(untrackedPath);
            }
            else if (planItem?.ManifestItem is { } manifestItem)
            {
                var subfolder = ContentDownloader.GetSubfolder(manifestItem.ContentType);
                var filePath = Path.Combine(inst.GetInstanceDirectory(), subfolder, manifestItem.InstalledVersion.Filename);
                if (File.Exists(filePath))
                    File.Delete(filePath);

                LocalInstanceContentStore.RemoveItem(
                    inst.Id,
                    manifestItem.Source,
                    manifestItem.SourceId);
            }

            _vm.AppendConsole($"[{item.DisplayName}] Removed {vm.Name}");
        }
        catch (Exception ex)
        {
            _vm.AppendConsole($"[{item.DisplayName}] Failed to remove {vm.Name}: {ex.Message}");
            return;
        }

        // Refresh the grid to reflect changes
        await RefreshLocalInstanceContentAsync(item);
    }

    /// <summary>
    /// Updates a single local content item to the latest version, then refreshes.
    /// </summary>
    private async void LocalUpdateSingleButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_selectedLocalInstance is null) return;
        if (sender is not Avalonia.Controls.Button btn) return;
        if (btn.DataContext is not ContentItemViewModel vm) return;

        var planItem = vm.LocalPlanItem;
        if (planItem?.Pack is not null)
        {
            try { await ManageLocalPackAsync(_selectedLocalInstance, update: true); }
            catch (Exception error) { _vm.AppendConsole(error.Message); }
            return;
        }
        if (planItem?.PackMember is { } packMember && planItem.LatestVersionResult is not null)
        {
            try { await new Core.LocalModpacks.LocalPackInstaller().ChangeMemberAsync(_selectedLocalInstance.Instance.Id, _selectedLocalInstance.Instance.GetInstanceDirectory(), packMember.Path, planItem.LatestVersionResult, true); }
            catch (Exception error) { _vm.AppendConsole(error.Message); }
            await RefreshLocalInstanceContentAsync(_selectedLocalInstance);
            return;
        }
        if (planItem?.LatestVersionResult is null || planItem.ManifestItem is null) return;

        var item = _selectedLocalInstance;
        var inst = item.Instance;
        var manifestItem = planItem.ManifestItem;


        btn.IsEnabled = false;

        try
        {
            _vm.AppendConsole($"[{item.DisplayName}] Updating {vm.Name}…");

            var subfolder = ContentDownloader.GetSubfolder(manifestItem.ContentType);
            var oldPath = Path.Combine(inst.GetInstanceDirectory(), subfolder, manifestItem.InstalledVersion.Filename);

            // Download and verify the new version before removing the old file.
            var searchResult = new ContentSearchResult
            {
                Id = manifestItem.SourceId,
                Source = manifestItem.Source,
                Name = manifestItem.Name,
                Slug = manifestItem.Slug,
                ContentType = manifestItem.ContentType,
                IconUrl = manifestItem.IconUrl,
            };

            await ContentDownloader.DownloadAndTrackAsync(
                inst.Id,
                inst.GetInstanceDirectory(),
                searchResult,
                planItem.LatestVersionResult,
                oldPath);

            _vm.AppendConsole($"[{item.DisplayName}] Updated {vm.Name} — {planItem.InstalledVersion} → {planItem.LatestVersionResult.VersionName}");
        }
        catch (Exception ex)
        {
            _vm.AppendConsole($"[{item.DisplayName}] Failed to update {vm.Name}: {ex.Message}");
            btn.IsEnabled = true;
            return;
        }

        // Refresh the grid to reflect changes
        await RefreshLocalInstanceContentAsync(item);
    }

    /// <summary>
    /// Checks an individual local instance's content for updates and populates
    /// the DataGrid, status card, sidebar indicators, and activity console.
    /// </summary>
    private async Task RefreshLocalInstanceContentAsync(LocalInstanceItemViewModel item, int failedActions = 0)
    {
        if (item.IsRefreshing)
            return;

        var inst = item.Instance;
        var isActivePanel = _selectedLocalInstance == item;

        item.IsRefreshing = true;
        item.UpdateStatus = LocalInstanceUpdateStatus.Pending;

        if (isActivePanel)
        {
            _localRefreshButton.IsEnabled = false;
            _localUpdateButton.IsEnabled = false;
            SetLocalSummary(item.CachedTotalItems, item.CachedPendingActions, item.CachedFailedActions, "Refreshing...", busy: true);
            _localProgressBar.IsIndeterminate = true;
        }

        try
        {
            var planner = new LocalContentPlanner();
            var plan = await Task.Run(() => planner.BuildPlanAsync(
                inst.Id,
                inst.GetInstanceDirectory(),
                inst.MinecraftVersion,
                inst.Loader));

            // Build view models for the DataGrid
            var viewModels = ContentItemViewModel
                .OrderForDisplay(plan.Items.Select(pi => new ContentItemViewModel(pi)))
                .ToList();

            // Cache on the item for tab switching
            item.CachedItems.Clear();
            item.CachedItems.AddRange(viewModels);
            item.CachedTotalItems = plan.TotalItems;
            item.CachedPendingActions = plan.PendingActions;
            item.CachedFailedActions = failedActions;
            item.CachedPlan = plan;
            item.HasCachedData = true;

            await Task.Run(() => AppStorageDatabase.SaveLocalInstanceContentInventory(
                inst.Id,
                ContentInventoryBuilder.FromLocalPlan(plan, inst.GetInstanceDirectory())));

            // Update sidebar status
            if (plan.PendingActions > 0)
            {
                item.UpdateStatus = LocalInstanceUpdateStatus.UpdatesWaiting;
                item.CachedStatusText = "Updates Available";
            }
            else
            {
                item.UpdateStatus = LocalInstanceUpdateStatus.NoUpdates;
                item.CachedStatusText = plan.HasUnavailableProvider ? "Provider unavailable" : "Up to Date";
            }

            var statusMsg = plan.PendingActions > 0
                ? $"[{item.DisplayName}] Ready — {plan.PendingActions} pending action(s)"
                : plan.HasUnavailableProvider ? $"[{item.DisplayName}] Provider unavailable — add a CurseForge API key to check updates"
                : $"[{item.DisplayName}] Up to date";

            // Update the panel if this instance is still selected
            if (_selectedLocalInstance == item)
            {
                _localAddonGrid.ItemsSource = viewModels;
                _localUpdateButton.IsEnabled = CanRunLocalUpdateAll(item);
                SetLocalSummary(plan.TotalItems, plan.PendingActions, failedActions, statusMsg, busy: false);
            }

            _vm.AppendConsole(statusMsg);
        }
        catch
        {
            item.UpdateStatus = LocalInstanceUpdateStatus.UpdateFailed;
            item.CachedStatusText = "Check Failed";
            item.CachedFailedActions = Math.Max(failedActions, 1);

            _vm.AppendConsole($"[{item.DisplayName}] Failed to refresh");
            if (_selectedLocalInstance == item)
                SetLocalSummary(item.CachedTotalItems, item.CachedPendingActions, item.CachedFailedActions, "Check Failed", busy: false);
        }
        finally
        {
            item.IsRefreshing = false;

            if (_selectedLocalInstance == item)
            {
                _localRefreshButton.IsEnabled = true;
                _localProgressBar.IsVisible = false;
                _localProgressBar.IsIndeterminate = false;
                _localProgressBar.Value = 0;
                _localIdleCountsPanel.IsVisible = true;
            }
        }
    }

    /// <summary>
    /// Opens the Add Content window for the selected local instance.
    /// </summary>
    private async void AddContentButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        LocalInstanceItemViewModel? item = null;

        if (sender is Avalonia.Controls.Button btn && btn.DataContext is LocalInstanceItemViewModel vm)
            item = vm;

        item ??= _selectedLocalInstance;
        if (item is null) return;

        var inst = item.Instance;
        var window = new AddContentWindow(
            inst.Id,
            inst.GetInstanceDirectory(),
            inst.MinecraftVersion,
            inst.Loader);

        await window.ShowDialog(this);

        // Refresh the content panel so newly added mods are visible immediately.
        await RefreshLocalInstanceContentAsync(item);
    }

    /// <summary>
    /// Launches the selected local instance using the same built-in launch
    /// pipeline as <see cref="LaunchDefaultAsync"/>.
    /// </summary>
    private async void LocalLaunchButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_selectedLocalInstance is null) return;

        if (await HandleLocalBuildActionAsync(_selectedLocalInstance))
            return;

        var launchSessionStarted = false;
        var inst = _selectedLocalInstance.Instance;

        if (string.IsNullOrWhiteSpace(inst.MinecraftVersion))
        {
            _vm.AppendConsole("MC version unknown.");
            UpdateLocalLaunchButtonState();
            return;
        }

        try
        {
            var account = await EnsureLaunchAccountAsync(
                _localAccountComboBox,
                GetSelectedLocalAccount,
                ApplyLocalLaunchAccount,
                RefreshLocalAccountSelector);
            if (account is null)
            {
                _vm.AppendConsole("Launch cancelled - Microsoft sign-in is required.");
                UpdateLocalLaunchButtonState();
                return;
            }

            _localLaunchStarting = true;
            SetLocalLaunchingButtonState();

            var username = account.Gamertag ?? "Player";

            _vm.AppendConsole("Preparing to launch...");

            var runtimeRoot = System.IO.Path.Combine(LauncherPaths.GetAppDataDirectory(), "Runtime");
            using var db = new RuntimeDatabase(runtimeRoot);
            using var builder = new GameEnvironmentBuilder(database: db);
            var report = await builder.CheckReadinessAsync(
                inst.MinecraftVersion, inst.Loader, inst.LoaderVersion);

            if (!report.IsReady)
            {
                _vm.AppendConsole($"Not built — {report.MissingCount} item(s) missing. Build it in Settings → Local tab.");
                UpdateLocalLaunchButtonState();
                return;
            }

            await builder.RecordInstanceAsync(report);

            var env = await builder.ResolveEnvironmentAsync(
                inst.MinecraftVersion, inst.Loader, inst.LoaderVersion);
            var gameDir = inst.GetInstanceDirectory();
            System.IO.Directory.CreateDirectory(gameDir);

            // If the user specified a server address, write servers.dat
            if (!string.IsNullOrWhiteSpace(inst.ServerAddress))
            {
                WriteLocalInstanceServersDat(inst.ServerAddress, gameDir);
            }

            // Pass the local instance's Java settings through a lightweight ServerConfig shim.
            var javaShim = new ServerConfig
            {
                JavaMode        = inst.JavaMode,
                JavaMinMemoryMb = inst.JavaMinMemoryMb,
                JavaMaxMemoryMb = inst.JavaMaxMemoryMb,
                JavaGarbageCollector = inst.JavaGarbageCollector,
                JavaExtraArgs   = inst.JavaExtraArgs,
                JavaCustomArgs  = inst.JavaCustomArgs,
            };

            if (MinecraftLauncher.TryBuildDefaultLaunchPlan(env, gameDir, username, out var plan, out var message, account, javaShim))
            {
                var logsDir = System.IO.Path.Combine(gameDir, "logs");
                System.IO.Directory.CreateDirectory(logsDir);
                var processOutputLogPath = System.IO.Path.Combine(logsDir, "launcher-output.log");

                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = plan.FileName,
                    Arguments = plan.Arguments,
                    WorkingDirectory = plan.WorkingDirectory,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };

                var proc = System.Diagnostics.Process.Start(psi);
                if (proc is not null)
                {
                    var sessionTarget = BuiltInGameSessionTarget.LocalInstance(inst.Id);
                    BuiltInGameSessionStore.Save(new BuiltInGameSessionRecord(
                        sessionTarget,
                        proc.Id,
                        gameDir,
                        inst.MinecraftVersion,
                        inst.Loader,
                        inst.LoaderVersion,
                        BuiltInGameSessionState.Launching,
                        DateTime.UtcNow,
                        BuiltInGameSessionStore.TryGetProcessStartedAtUtc(proc)));
                    launchSessionStarted = true;
                    SetLocalLaunchingButtonState();
                    StartBuiltInGameSessionMonitor(sessionTarget, proc, processOutputLogPath);
                }

                _vm.AppendConsole($"Launched {plan.Description}");

                // Record last launched timestamp.
                inst.LastLaunchedAtUtc = DateTime.UtcNow;
                var config = AppConfigStore.LoadAppConfig();
                var match = config.LocalInstances.FirstOrDefault(i => i.Id == inst.Id);
                if (match is not null)
                {
                    match.LastLaunchedAtUtc = inst.LastLaunchedAtUtc;
                    AppConfigStore.SaveAppConfig(config);
                }
            }
            else
            {
                _vm.AppendConsole(message ?? "Launch plan failed.");
                UpdateLocalLaunchButtonState();
            }
        }
        catch (Exception ex)
        {
            _vm.AppendConsole($"Launch failed: {ex.Message}");
            UpdateLocalLaunchButtonState();
        }
        finally
        {
            _localLaunchStarting = false;
            if (!launchSessionStarted)
                UpdateLocalLaunchButtonState();
        }
    }

    /// <summary>
    /// Writes a servers.dat entry in the instance directory for the
    /// user-specified server address so the server appears in the
    /// Minecraft multiplayer list on first launch.
    /// </summary>
    private static void WriteLocalInstanceServersDat(string serverAddress, string gameDir)
    {
        // Parse host:port
        var parts = serverAddress.Split(':', 2);
        var host = parts[0];
        var port = parts.Length > 1 && int.TryParse(parts[1], out var p) ? p : 25565;

        ServersDatHelper.EnsureServerEntry(gameDir, host, port, "Auto-Connect");
    }
}
