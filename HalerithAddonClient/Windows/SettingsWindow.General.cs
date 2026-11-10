using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using HalerithAddonClient.Core;
using HalerithAddonClient.Core.ContentSearch;
using HalerithAddonClient.GuidedTips;
using HalerithAddonClient.Services;

namespace HalerithAddonClient.Windows;

public partial class SettingsWindow
{
    private bool _resetCurseForgeKey;

    // ══════════════════════════════════════════════════════════════════════
    //  Load (initialise named controls from AppConfig)
    // ══════════════════════════════════════════════════════════════════════

    private void LoadFromConfig()
    {
        var config = _vm.Config;

        if (_startupTimeoutBox != null)
            _startupTimeoutBox.Text = Math.Clamp(config.StartupTimeoutSeconds, 10, 300).ToString();

        if (_launcherCountdownBox != null)
            _launcherCountdownBox.Text = Math.Clamp(config.LauncherCountdownSeconds, 0, 30).ToString();

        if (_enableFileLoggingCheckBox != null)
            _enableFileLoggingCheckBox.IsChecked = config.EnableEventLogging;

        if (_keepBackupsCheckBox != null)
        {
            _keepBackupsCheckBox.IsChecked = config.KeepBackups;
            _keepBackupsCheckBox.IsCheckedChanged += KeepBackupsCheckBox_Changed;
        }

        if (_backupMaxFilesBox != null)
        {
            _backupMaxFilesBox.Text = config.BackupMaxFilesPerFolder.ToString();
            _backupMaxFilesBox.IsEnabled = config.KeepBackups;
        }

        _curseForgeKeyRow.IsVisible = !CurseForgeApiKeyStore.HasEmbeddedKey;
        if (_curseForgeKeyRow.IsVisible)
        {
            var hasUserKey = !string.IsNullOrWhiteSpace(CurseForgeApiKeyStore.LoadUserKey());
            _curseForgeKeyBox.Watermark = hasUserKey
                ? "Key saved; enter a replacement"
                : "Enter your CurseForge API key";
            _resetCurseForgeKeyButton.IsVisible = hasUserKey;
        }

        if (_allowAutoUpdateCheckBox != null)
            _allowAutoUpdateCheckBox.IsChecked = config.AllowAutoUpdate;

        if (_guidedTipsCheckBox != null)
            _guidedTipsCheckBox.IsChecked = config.GuidedTipsEnabled;

        PopulateDefaultAccountComboBox();
        PopulateDefaultServerComboBox();
    }

    private void ResetCurseForgeKey_Click(object? sender, RoutedEventArgs e)
    {
        _resetCurseForgeKey = true;
        _curseForgeKeyBox.Text = "";
        _curseForgeKeyBox.Watermark = "Key will be removed when you Save";
        _resetCurseForgeKeyButton.IsVisible = false;
    }

    private void SaveCurseForgeKey()
    {
        if (!_curseForgeKeyRow.IsVisible) return;

        if (!string.IsNullOrWhiteSpace(_curseForgeKeyBox.Text))
            CurseForgeApiKeyStore.SaveUserKey(_curseForgeKeyBox.Text);
        else if (_resetCurseForgeKey)
            CurseForgeApiKeyStore.ClearUserKey();
    }

    // ══════════════════════════════════════════════════════════════════════
    //  Application tab — default instance picker
    // ══════════════════════════════════════════════════════════════════════

    private void PopulateDefaultServerComboBox()
    {
        if (_defaultServerComboBox == null) return;

        _defaultServerComboBox.Items.Clear();
        _defaultServerComboBox.IsEnabled = false;
        var host = _vm.Config.EnsureManagerScope();

        if (DemoMode.Enabled)
        {
            var visible = DemoMode.VisibleServers(host.Servers);
            for (var i = 0; i < visible.Count; i++)
                _defaultServerComboBox.Items.Add(new DefaultInstanceItem(AppConfig.DefaultInstanceKindServer,
                    visible[i].ServerPublicId, DemoMode.ServerName(visible[i], i)));
            _defaultServerComboBox.SelectedIndex = visible.Count > 0 ? 0 : -1;
            _defaultServerComboBox.IsEnabled = visible.Count > 1;
            return;
        }

        var servers = host.VisibleServersFor(AccountStore.GetDefaultAccountId())
            .OrderBy(s => s.ServerName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var localInstances = _vm.Config.LocalInstances
            .OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (servers.Count == 0 && localInstances.Count == 0)
        {
            return;
        }

        foreach (var s in servers)
        {
            var label = string.IsNullOrWhiteSpace(s.ServerName) ? s.ServerPublicId : s.ServerName;
            _defaultServerComboBox.Items.Add(new DefaultInstanceItem(
                AppConfig.DefaultInstanceKindServer,
                s.ServerPublicId,
                $"Server - {label}"));
        }

        foreach (var instance in localInstances)
        {
            var label = string.IsNullOrWhiteSpace(instance.Name) ? instance.Id : instance.Name;
            _defaultServerComboBox.Items.Add(new DefaultInstanceItem(
                AppConfig.DefaultInstanceKindLocal,
                instance.Id,
                $"Local - {label}"));
        }

        var selectedIdx = 0;
        var selectedKind = string.Equals(_vm.Config.DefaultInstanceKind, AppConfig.DefaultInstanceKindLocal, StringComparison.OrdinalIgnoreCase)
            ? AppConfig.DefaultInstanceKindLocal
            : AppConfig.DefaultInstanceKindServer;
        var selectedId = string.IsNullOrWhiteSpace(_vm.Config.DefaultInstanceId)
            ? host.DefaultServerPublicId
            : _vm.Config.DefaultInstanceId;

        for (var i = 0; i < _defaultServerComboBox.Items.Count; i++)
        {
            if (_defaultServerComboBox.Items[i] is DefaultInstanceItem item
                && string.Equals(item.Kind, selectedKind, StringComparison.OrdinalIgnoreCase)
                && string.Equals(item.Id, selectedId, StringComparison.OrdinalIgnoreCase))
            {
                selectedIdx = i;
                break;
            }
        }

        _defaultServerComboBox.SelectedIndex = selectedIdx;
        _defaultServerComboBox.IsEnabled = _defaultServerComboBox.Items.Count > 1;
    }

    private void _DefaultServerComboBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    { }

    // ══════════════════════════════════════════════════════════════════════
    //  Application tab — default launcher account
    // ══════════════════════════════════════════════════════════════════════

    private bool _suppressDefaultAccountSelection;

    private void PopulateDefaultAccountComboBox()
    {
        if (_defaultAccountComboBox == null) return;

        _suppressDefaultAccountSelection = true;
        try
        {
            var accounts = AccountStore.LoadAll();
            _defaultAccountComboBox.ItemsSource = BuildAccountPickerItems(accounts, includeAddAction: accounts.Count > 0);

            var defaultId = AccountStore.GetDefaultAccountId();
            var match = accounts.FirstOrDefault(a =>
                string.Equals(a.Uuid, defaultId, StringComparison.OrdinalIgnoreCase));

            var selected = match ?? accounts.FirstOrDefault();
            SelectAccountPickerItem(_defaultAccountComboBox, selected);
            if (selected is not null
                && !string.Equals(defaultId, selected.Uuid, StringComparison.OrdinalIgnoreCase))
            {
                AccountStore.SetDefaultAccountId(selected.Uuid);
            }

            _defaultAccountComboBox.IsEnabled = true;
        }
        finally
        {
            _suppressDefaultAccountSelection = false;
        }

        ClearAddActionSelectionLater(_defaultAccountComboBox);
    }

    private async void _DefaultAccountComboBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressDefaultAccountSelection) return;

        if (_defaultAccountComboBox?.SelectedItem is AccountPickerItem { IsAdd: true })
        {
            _suppressDefaultAccountSelection = true;
            SelectAccountPickerItem(_defaultAccountComboBox, GetDefaultAccount());
            _suppressDefaultAccountSelection = false;
            _defaultAccountComboBox.IsDropDownOpen = false;
            _ = AddDefaultAccountAsync();
            return;
        }

        if (GetSelectedAccount(_defaultAccountComboBox!) is { } selected)
        {
            AccountStore.SetDefaultAccountId(selected.Uuid);
            await RefreshDefaultServersForSelectedAccountAsync();
        }
    }

    private async void AddDefaultAccountButton_Click(object? sender, RoutedEventArgs e)
        => await AddDefaultAccountAsync();

    private async void DefaultAccountAddMenuButton_Click(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        _defaultAccountComboBox.IsDropDownOpen = false;
        await AddDefaultAccountAsync();
    }

    private void DefaultAccountComboBox_DropDownOpened(object? sender, EventArgs e)
    {
        var accounts = AccountStore.LoadAll();
        if (accounts.Count > 0)
            return;

        _suppressDefaultAccountSelection = true;
        try
        {
            _defaultAccountComboBox.ItemsSource = new[] { AccountPickerItem.AddAccount() };
            _defaultAccountComboBox.SelectedItem = null;
        }
        finally
        {
            _suppressDefaultAccountSelection = false;
        }

        ClearAddActionSelectionLater(_defaultAccountComboBox);
    }

    private void DefaultAccountComboBox_DropDownClosed(object? sender, EventArgs e)
    {
        var accounts = AccountStore.LoadAll();
        if (accounts.Count > 0)
            return;

        _suppressDefaultAccountSelection = true;
        try
        {
            _defaultAccountComboBox.SelectedItem = null;
            _defaultAccountComboBox.ItemsSource = Array.Empty<AccountPickerItem>();
        }
        finally
        {
            _suppressDefaultAccountSelection = false;
        }
    }

    private async Task AddDefaultAccountAsync()
    {
        var loginWindow = new MicrosoftLoginWindow();
        var account = await loginWindow.ShowDialog<MinecraftAccount?>(this);

        if (account is null) return;

        GuidedTipProgressStore.Complete("main.minecraft-account.v1");

        ImageCacheService.ResolveMinecraftHeadPath(
            account,
            _ => Dispatcher.UIThread.Post(PopulateDefaultAccountComboBox),
            refreshIfStale: true);

        var accounts = AccountStore.LoadAll();
        if (accounts.Count == 1)
            AccountStore.SetDefaultAccountId(account.Uuid);

        PopulateDefaultAccountComboBox();
        await RefreshDefaultServersForSelectedAccountAsync();

        _suppressDefaultAccountSelection = true;
        SelectAccountPickerItem(_defaultAccountComboBox!, account);
        _suppressDefaultAccountSelection = false;

        AccountStore.SetDefaultAccountId(account.Uuid);
        await RefreshDefaultServersForSelectedAccountAsync();
    }

    private async Task RefreshDefaultServersForSelectedAccountAsync()
    {
        if (_defaultServerComboBox is null)
            return;

        var config = _vm.Config;
        var host = config.EnsureManagerScope();

        if (!host.IsEnrolled)
        {
            PopulateDefaultServerComboBox();
            return;
        }

        _defaultServerComboBox.IsEnabled = false;
        SetInfoStatus("Refreshing server list...");

        try
        {
            var runtime = config.ToRuntimeConfig(host, host.DefaultServerPublicId);
            AppConfigStore.ValidateForRuntime(runtime);

            using var apiClient = new ApiClient(runtime);
            using var cts = new CancellationTokenSource(
                TimeSpan.FromSeconds(Math.Clamp(runtime.StartupTimeoutSeconds, 10, 300)));

            var health = await apiClient.GetHostHealthAsync(cts.Token, _clientVersion);
            if (!health.Ok)
            {
                var message = health.Message?.Trim();
                if (string.IsNullOrWhiteSpace(message))
                    message = health.Error?.Trim();
                if (string.IsNullOrWhiteSpace(message) && apiClient.LastStatusCode.HasValue)
                    message = $"HTTP {(int) apiClient.LastStatusCode.Value}";

                SetErrorStatus(string.IsNullOrWhiteSpace(message)
                    ? "Could not refresh server list."
                    : $"Could not refresh server list: {message}");
            }
            else
            {
                if (ServerRosterSync.ApplyHealth(host, health))
                    AppConfigStore.SaveAppConfig(config);

                SetInfoStatus("Server list refreshed.");
            }
        }
        catch (Exception ex)
        {
            SetErrorStatus($"Could not refresh server list: {ex.Message}");
        }
        finally
        {
            PopulateDefaultServerComboBox();
        }
    }

    private void RemoveDefaultAccountButton_Click(object? sender, RoutedEventArgs e)
    {
        if (GetSelectedAccount(_defaultAccountComboBox!) is not { } selected) return;

        RemoveDefaultAccount(selected);
    }

    private void DefaultAccountDeleteMenuButton_Click(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is Button { Tag: AccountPickerItem { Account: { } account } })
            RemoveDefaultAccount(account);
    }

    private void RemoveDefaultAccount(MinecraftAccount selected)
    {
        AccountStore.Remove(selected.Uuid);
        ManagedServerAccessGate.Forget(selected.Uuid);

        if (string.Equals(AccountStore.GetDefaultAccountId(), selected.Uuid, StringComparison.OrdinalIgnoreCase))
        {
            var remaining = AccountStore.LoadAll();
            AccountStore.SetDefaultAccountId(remaining.FirstOrDefault()?.Uuid ?? "");
        }

        PopulateDefaultAccountComboBox();
        PopulateDefaultServerComboBox();
    }

    private static List<AccountPickerItem> BuildAccountPickerItems(IEnumerable<MinecraftAccount> accounts, bool includeAddAction)
    {
        var items = accounts
            .OrderBy(a => a.Gamertag, StringComparer.OrdinalIgnoreCase)
            .Select(AccountPickerItem.ForAccount)
            .ToList();

        if (includeAddAction)
            items.Add(AccountPickerItem.AddAccount());

        return items;
    }

    private static MinecraftAccount? GetSelectedAccount(ComboBox comboBox)
        => comboBox.SelectedItem is AccountPickerItem { Account: { } account } ? account : null;

    private static void SelectAccountPickerItem(ComboBox comboBox, MinecraftAccount? account)
    {
        if (comboBox.ItemsSource is not IEnumerable<AccountPickerItem> items)
        {
            comboBox.SelectedItem = null;
            return;
        }

        comboBox.SelectedItem = account is null
            ? null
            : items.FirstOrDefault(i => string.Equals(i.Uuid, account.Uuid, StringComparison.OrdinalIgnoreCase));

        if (comboBox.SelectedItem is AccountPickerItem { IsAdd: true })
            comboBox.SelectedItem = null;
    }

    private static void ClearAddActionSelectionLater(ComboBox comboBox)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (comboBox.SelectedItem is AccountPickerItem { IsAdd: true })
                comboBox.SelectedItem = null;
        }, DispatcherPriority.Background);
    }

    private static MinecraftAccount? GetDefaultAccount()
    {
        var accounts = AccountStore.LoadAll();
        var defaultId = AccountStore.GetDefaultAccountId();
        return accounts.FirstOrDefault(a => string.Equals(a.Uuid, defaultId, StringComparison.OrdinalIgnoreCase))
            ?? accounts.FirstOrDefault();
    }

    private void KeepBackupsCheckBox_Changed(object? sender, RoutedEventArgs e)
    {
        var isChecked = _keepBackupsCheckBox?.IsChecked == true;
        if (_backupMaxFilesBox != null)
        {
            _backupMaxFilesBox.IsEnabled = isChecked;
            if (isChecked && _backupMaxFilesBox.Text is "0" or "" or null)
                _backupMaxFilesBox.Text = "20";
        }
    }

    // ══════════════════════════════════════════════════════════════════════
    //  Footer — Save / Cancel
    // ══════════════════════════════════════════════════════════════════════

    private void SaveButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_saveButton != null)
            _saveButton.IsEnabled = false;

        try
        {
            var config = _vm.Config;

            if (_startupTimeoutBox != null)
                config.StartupTimeoutSeconds =
                    int.TryParse(_startupTimeoutBox.Text, out var t) ? Math.Clamp(t, 10, 300) : 60;

            if (_launcherCountdownBox != null)
                config.LauncherCountdownSeconds =
                    int.TryParse(_launcherCountdownBox.Text, out var c) ? Math.Clamp(c, 0, 30) : 3;

            if (_enableFileLoggingCheckBox != null)
                config.EnableEventLogging = _enableFileLoggingCheckBox.IsChecked == true;

            if (_allowAutoUpdateCheckBox != null)
                config.AllowAutoUpdate = _allowAutoUpdateCheckBox.IsChecked == true;

            if (_guidedTipsCheckBox != null)
            {
                config.GuidedTipsEnabled = _guidedTipsCheckBox.IsChecked == true;
                GuidedTipProgressStore.NotifyEnabledChanged(config.GuidedTipsEnabled);
            }

            if (_keepBackupsCheckBox != null)
                config.KeepBackups = _keepBackupsCheckBox.IsChecked == true;

            if (_backupMaxFilesBox != null)
                config.BackupMaxFilesPerFolder =
                    int.TryParse(_backupMaxFilesBox.Text, out var bm) ? Math.Max(bm, 0) : 20;

            config.DefaultHostId = ManagerApiDefaults.ManagerScopeId;
            var hostConfig = config.EnsureManagerScope();
            if (!DemoMode.Enabled && _defaultServerComboBox?.SelectedItem is DefaultInstanceItem selectedInstance)
            {
                config.DefaultInstanceKind = selectedInstance.Kind;
                config.DefaultInstanceId = selectedInstance.Id;

                if (string.Equals(selectedInstance.Kind, AppConfig.DefaultInstanceKindServer, StringComparison.OrdinalIgnoreCase))
                    hostConfig.DefaultServerPublicId = selectedInstance.Id;
            }

            _vm.SaveCommand.Execute(null);
        }
        catch (Exception ex)
        {
            SetErrorStatus(ex.Message);
            if (_saveButton != null)
                _saveButton.IsEnabled = true;
        }
    }

    private void CancelButton_Click(object? sender, RoutedEventArgs e)
        => _vm.CancelCommand.Execute(null);
}
