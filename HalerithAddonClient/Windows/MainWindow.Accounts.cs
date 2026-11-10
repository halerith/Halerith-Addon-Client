using Avalonia.Controls;
using Avalonia.Threading;
using HalerithAddonClient.Core;
using HalerithAddonClient.Services;
using HalerithAddonClient.ViewModels;

namespace HalerithAddonClient.Windows;

public partial class MainWindow
{
    // ══════════════════════════════════════════════════════════════════════
    //  Account selector (remote servers + local instances)
    // ══════════════════════════════════════════════════════════════════════

    private bool _suppressAccountSelection;
    private List<MinecraftAccount> _accounts = [];

    private MinecraftAccount? CurrentAccountForSettings() =>
        GetSelectedAccount(_localAccountPanel.IsVisible ? _localAccountComboBox : _accountComboBox);

    private void RefreshAccountSelectorForSettings()
    {
        if (_localAccountPanel.IsVisible)
            RefreshLocalAccountSelector();
        else
            RefreshAccountSelector();
    }

    /// <summary>Refreshes the account selector for the selected server, or the global default when no server is selected.</summary>
    private void RefreshAccountSelector()
    {
        var server = _vm.SelectedServer;

        _accounts = AccountStore.LoadAll();

        _accountSidebarPanel.IsVisible = true;
        _accountPanel.IsVisible = true;
        _localAccountPanel.IsVisible = false;
        PopulateAccountComboBox(server);
    }

    private void PopulateAccountComboBox(ServerItemViewModel? server)
    {
        _suppressAccountSelection = true;
        try
        {
            _accountComboBox.ItemsSource = BuildAccountPickerItems(includeAddAction: _accounts.Count > 0);
            _accountComboBox.IsEnabled = true;

            // Select the per-server account, falling back to global default
            var selectedId = AccountStore.GetDefaultAccountId();

            var match = _accounts.FirstOrDefault(a =>
                string.Equals(a.Uuid, selectedId, StringComparison.OrdinalIgnoreCase));
            SelectAccountPickerItem(_accountComboBox, match ?? _accounts.FirstOrDefault());
        }
        finally
        {
            _suppressAccountSelection = false;
        }

        ClearAddActionSelectionLater(_accountComboBox);
    }

    private async void AddAccountButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => await AddServerAccountAsync();

    private async void AccountAddMenuButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        e.Handled = true;
        _accountComboBox.IsDropDownOpen = false;
        await AddServerAccountAsync();
    }

    private void AccountComboBox_DropDownOpened(object? sender, EventArgs e)
        => OpenAccountPicker(_accountComboBox);

    private void AccountComboBox_DropDownClosed(object? sender, EventArgs e)
        => CloseAccountPicker(_accountComboBox);

    private async Task AddServerAccountAsync()
    {
        var isFirstAccount = AccountStore.LoadAll().Count == 0;
        var loginWindow = new MicrosoftLoginWindow();
        var account = await loginWindow.ShowDialog<MinecraftAccount?>(this);

        if (account is null)
            return;

        CompleteMinecraftAccountTip();

        RefreshAccountHead(account, refreshIfStale: true);

        // If there is no global default yet, make this account the default.
        if (string.IsNullOrWhiteSpace(AccountStore.GetDefaultAccountId()))
            AccountStore.SetDefaultAccountId(account.Uuid);

        // Set as the per-server selection
        AccountStore.SetDefaultAccountId(account.Uuid);

        RefreshAccountSelector();
        await _vm.LoadServersAsync();
        if (isFirstAccount)
        {
            await _vm.LoadAllServersAsync();
            UpdateAllServerRuntimeBuildStates();
        }
        _vm.RefreshManagedAccessVisualState();
    }

    private void RemoveAccountButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (GetSelectedAccount(_accountComboBox) is not { } selected) return;

        RemoveServerAccount(selected);
    }

    private void AccountDeleteMenuButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is Button { Tag: AccountPickerItem { Account: { } account } })
            RemoveServerAccount(account);
    }

    private void RemoveServerAccount(MinecraftAccount selected)
    {
        AccountStore.Remove(selected.Uuid);
        ManagedServerAccessGate.Forget(selected.Uuid);

        // Clear per-server references to this account
        if (_vm.SelectedServer?.Server.SelectedAccountId == selected.Uuid)
        {
            _vm.SelectedServer.Server.SelectedAccountId = "";
            SaveServerAccountSelection(_vm.SelectedServer, "");
        }

        RefreshAccountSelector();
        _vm.RefreshManagedAccessVisualState();
    }

    private async void AccountComboBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressAccountSelection) return;

        if (_accountComboBox.SelectedItem is AccountPickerItem { IsAdd: true })
        {
            _suppressAccountSelection = true;
            SelectAccountPickerItem(_accountComboBox, GetSelectedServerAccount());
            _suppressAccountSelection = false;
            _accountComboBox.IsDropDownOpen = false;
            _ = AddServerAccountAsync();
            return;
        }

        // Remote manager auth uses the active/default Minecraft account.
        if (GetSelectedAccount(_accountComboBox) is { } selected)
        {
            AccountStore.SetDefaultAccountId(selected.Uuid);
            await _vm.LoadServersAsync();
            RefreshAccountSelector();
        }
    }

    /// <summary>
    /// Refreshes the local-instance account selector.
    /// Called when a local instance is selected in the sidebar.
    /// </summary>
    private void RefreshLocalAccountSelector()
    {
        _accounts = AccountStore.LoadAll();
        _accountSidebarPanel.IsVisible = true;
        _accountPanel.IsVisible = false;
        _localAccountPanel.IsVisible = true;
        PopulateLocalAccountComboBox();
    }

    private void PopulateLocalAccountComboBox()
    {
        _suppressAccountSelection = true;
        try
        {
            _localAccountComboBox.ItemsSource = BuildAccountPickerItems(includeAddAction: _accounts.Count > 0);
            _localAccountComboBox.IsEnabled = true;

            // Select per-instance account, falling back to global default
            var selectedId = _selectedLocalInstance?.Instance.SelectedAccountId;
            if (string.IsNullOrWhiteSpace(selectedId))
                selectedId = AccountStore.GetDefaultAccountId();

            var match = _accounts.FirstOrDefault(a =>
                string.Equals(a.Uuid, selectedId, StringComparison.OrdinalIgnoreCase));
            SelectAccountPickerItem(_localAccountComboBox, match ?? _accounts.FirstOrDefault());
        }
        finally
        {
            _suppressAccountSelection = false;
        }

        ClearAddActionSelectionLater(_localAccountComboBox);
    }

    private void LocalAccountComboBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressAccountSelection) return;

        if (_localAccountComboBox.SelectedItem is AccountPickerItem { IsAdd: true })
        {
            _suppressAccountSelection = true;
            SelectAccountPickerItem(_localAccountComboBox, GetSelectedLocalAccount());
            _suppressAccountSelection = false;
            _localAccountComboBox.IsDropDownOpen = false;
            _ = AddLocalAccountAsync();
            return;
        }

        // Persist the per-instance selection
        if (GetSelectedAccount(_localAccountComboBox) is { } selected && _selectedLocalInstance is not null)
        {
            _selectedLocalInstance.Instance.SelectedAccountId = selected.Uuid;

            // Save selected account to app storage.
            var config = AppConfigStore.LoadAppConfig();
            var match = config.LocalInstances.FirstOrDefault(i => i.Id == _selectedLocalInstance.Instance.Id);
            if (match is not null)
            {
                match.SelectedAccountId = selected.Uuid;
                AppConfigStore.SaveAppConfig(config);
            }
        }
    }

    private async void LocalAddAccountButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => await AddLocalAccountAsync();

    private async void LocalAccountAddMenuButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        e.Handled = true;
        _localAccountComboBox.IsDropDownOpen = false;
        await AddLocalAccountAsync();
    }

    private void LocalAccountComboBox_DropDownOpened(object? sender, EventArgs e)
        => OpenAccountPicker(_localAccountComboBox);

    private void LocalAccountComboBox_DropDownClosed(object? sender, EventArgs e)
        => CloseAccountPicker(_localAccountComboBox);

    private async Task AddLocalAccountAsync()
    {
        var isFirstAccount = AccountStore.LoadAll().Count == 0;
        var loginWindow = new MicrosoftLoginWindow();
        var account = await loginWindow.ShowDialog<MinecraftAccount?>(this);

        if (account is null)
            return;

        CompleteMinecraftAccountTip();

        RefreshAccountHead(account, refreshIfStale: true);

        // If there is no global default yet, make this account the default.
        if (string.IsNullOrWhiteSpace(AccountStore.GetDefaultAccountId()))
            AccountStore.SetDefaultAccountId(account.Uuid);

        // Set as the per-instance selection
        if (_selectedLocalInstance is not null)
        {
            _selectedLocalInstance.Instance.SelectedAccountId = account.Uuid;
            var config = AppConfigStore.LoadAppConfig();
            var inst = config.LocalInstances.FirstOrDefault(i => i.Id == _selectedLocalInstance.Instance.Id);
            if (inst is not null)
            {
                inst.SelectedAccountId = account.Uuid;
                AppConfigStore.SaveAppConfig(config);
            }
        }

        RefreshLocalAccountSelector();
        if (isFirstAccount)
        {
            await _vm.LoadServersAsync();
            await _vm.LoadAllServersAsync();
            UpdateAllServerRuntimeBuildStates();
        }
        _vm.RefreshManagedAccessVisualState();
    }

    private void LocalRemoveAccountButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (GetSelectedAccount(_localAccountComboBox) is not { } selected) return;

        RemoveLocalAccount(selected);
    }

    private void LocalAccountDeleteMenuButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is Button { Tag: AccountPickerItem { Account: { } account } })
            RemoveLocalAccount(account);
    }

    private void RemoveLocalAccount(MinecraftAccount selected)
    {
        AccountStore.Remove(selected.Uuid);
        ManagedServerAccessGate.Forget(selected.Uuid);

        // Clear per-instance reference to this account
        if (_selectedLocalInstance?.Instance.SelectedAccountId == selected.Uuid)
        {
            _selectedLocalInstance.Instance.SelectedAccountId = "";
            var config = AppConfigStore.LoadAppConfig();
            var inst = config.LocalInstances.FirstOrDefault(i => i.Id == _selectedLocalInstance.Instance.Id);
            if (inst is not null)
            {
                inst.SelectedAccountId = "";
                AppConfigStore.SaveAppConfig(config);
            }
        }

        RefreshLocalAccountSelector();
        _vm.RefreshManagedAccessVisualState();
    }

    private List<AccountPickerItem> BuildAccountPickerItems(bool includeAddAction)
    {
        var items = _accounts
            .OrderBy(a => a.Gamertag, StringComparer.OrdinalIgnoreCase)
            .Select(AccountPickerItem.ForAccount)
            .ToList();

        if (includeAddAction)
            items.Add(AccountPickerItem.AddAccount());

        return items;
    }

    private void RefreshAccountHeadsOnStartup()
    {
        _accounts = AccountStore.LoadAll();
        foreach (var account in _accounts)
            RefreshAccountHead(account, refreshIfStale: true);
    }

    private async Task RefreshMinecraftSessionsOnStartupAsync()
    {
        var accounts = AccountStore.LoadAll();
        if (accounts.Count == 0)
            return;

        using var authService = new MicrosoftAuthService();
        foreach (var account in accounts)
        {
            if (string.IsNullOrWhiteSpace(account.MsRefreshToken))
            {
                FileLogger.Warn($"Minecraft session refresh skipped for {account.Gamertag}: no refresh token is stored.");
                continue;
            }

            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                var refreshed = await authService.RefreshAsync(account, cts.Token);
                AccountStore.Save(refreshed);
                FileLogger.Info($"Minecraft session refreshed for {refreshed.Gamertag} during startup.");
            }
            catch (Exception ex)
            {
                FileLogger.Warn($"Minecraft session refresh failed for {account.Gamertag}: {ex.Message}");
                _vm.AppendConsole($"[{DemoMode.PlayerName(account.Gamertag)}] Could not refresh the Minecraft session; the existing session will be used if still valid.");
            }
        }

        _accounts = AccountStore.LoadAll();
        RefreshAccountSelector();
        if (_selectedLocalInstance is not null)
            RefreshLocalAccountSelector();
    }

    private void RefreshAccountHead(MinecraftAccount account, bool refreshIfStale)
    {
        ImageCacheService.ResolveMinecraftHeadPath(
            account,
            _ => Dispatcher.UIThread.Post(() =>
            {
                if (_accountPanel?.IsVisible == true)
                    RefreshAccountSelector();
                if (_localAccountPanel?.IsVisible == true)
                    RefreshLocalAccountSelector();
            }),
            refreshIfStale);
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

    private void OpenAccountPicker(ComboBox comboBox)
    {
        if (_accounts.Count > 0)
            return;

        _suppressAccountSelection = true;
        try
        {
            comboBox.ItemsSource = new[] { AccountPickerItem.AddAccount() };
            comboBox.SelectedItem = null;
        }
        finally
        {
            _suppressAccountSelection = false;
        }

        ClearAddActionSelectionLater(comboBox);
    }

    private void CloseAccountPicker(ComboBox comboBox)
    {
        if (_accounts.Count > 0)
            return;

        _suppressAccountSelection = true;
        try
        {
            comboBox.SelectedItem = null;
            comboBox.ItemsSource = Array.Empty<AccountPickerItem>();
        }
        finally
        {
            _suppressAccountSelection = false;
        }
    }

    private MinecraftAccount? GetSelectedServerAccount()
    {
        var selectedId = GetSelectedAccount(_accountComboBox)?.Uuid ?? AccountStore.GetDefaultAccountId();

        var account = _accounts.FirstOrDefault(a =>
            string.Equals(a.Uuid, selectedId, StringComparison.OrdinalIgnoreCase));

        account ??= GetSelectedAccount(_accountComboBox);
        account ??= _accounts.Count == 1 ? _accounts[0] : null;
        return account;
    }

    private async Task<MinecraftAccount?> EnsureLaunchAccountAsync(
        ComboBox comboBox,
        Func<MinecraftAccount?> resolveAccount,
        Action<MinecraftAccount> applySelection,
        Action refreshSelector)
    {
        _accounts = AccountStore.LoadAll();
        var account = resolveAccount();

        if (account is null || string.IsNullOrWhiteSpace(MinecraftProfileClient.NormalizeUuid(account.Uuid)))
            return await PromptForLaunchAccountAsync(applySelection, refreshSelector);

        if (string.IsNullOrWhiteSpace(account.McAccessToken) || account.IsExpired)
        {
            if (!string.IsNullOrWhiteSpace(account.MsRefreshToken))
            {
                try
                {
                    _vm.AppendConsole("Refreshing authentication...");
                    using var authService = new MicrosoftAuthService();
                    account = await authService.RefreshAsync(account);
                    AccountStore.Save(account);
                    _accounts = AccountStore.LoadAll();
                    SelectAccountPickerItem(comboBox, account);
                    applySelection(account);
                    refreshSelector();
                    return account;
                }
                catch (Exception ex)
                {
                    _vm.AppendConsole($"Token refresh failed: {ex.Message}. Please sign in again.");
                }
            }
            else
            {
                _vm.AppendConsole("Minecraft sign-in has expired. Please sign in again.");
            }

            return await PromptForLaunchAccountAsync(applySelection, refreshSelector);
        }

        return account;
    }

    private async Task<MinecraftAccount?> PromptForLaunchAccountAsync(
        Action<MinecraftAccount> applySelection,
        Action refreshSelector)
    {
        var loginWindow = new MicrosoftLoginWindow();
        var account = await loginWindow.ShowDialog<MinecraftAccount?>(this);

        if (account is null)
            return null;

        CompleteMinecraftAccountTip();

        RefreshAccountHead(account, refreshIfStale: true);

        if (string.IsNullOrWhiteSpace(AccountStore.GetDefaultAccountId()))
            AccountStore.SetDefaultAccountId(account.Uuid);

        applySelection(account);
        _accounts = AccountStore.LoadAll();
        refreshSelector();
        _vm.RefreshManagedAccessVisualState();
        return account;
    }

    private void ApplyServerLaunchAccount(MinecraftAccount account)
    {
        AccountStore.SetDefaultAccountId(account.Uuid);
    }

    private void ApplyLocalLaunchAccount(MinecraftAccount account)
    {
        if (_selectedLocalInstance is null)
            return;

        _selectedLocalInstance.Instance.SelectedAccountId = account.Uuid;

        var config = AppConfigStore.LoadAppConfig();
        var match = config.LocalInstances.FirstOrDefault(i => i.Id == _selectedLocalInstance.Instance.Id);
        if (match is not null)
        {
            match.SelectedAccountId = account.Uuid;
            AppConfigStore.SaveAppConfig(config);
        }
    }

    private string ResolveSelectedManagedMinecraftUuid()
    {
        _accounts = AccountStore.LoadAll();

        var selectedId = GetSelectedAccount(_accountComboBox)?.Uuid ?? AccountStore.GetDefaultAccountId();
        if (string.IsNullOrWhiteSpace(selectedId) && _accounts.Count == 1)
            selectedId = _accounts[0].Uuid;

        return MinecraftProfileClient.NormalizeUuid(selectedId ?? "") ?? "";
    }

    private static void SaveServerAccountSelection(ServerItemViewModel server, string accountId)
    {
        try
        {
            var config = AppConfigStore.LoadAppConfig();
            var host = config.Hosts.FirstOrDefault(h =>
                string.Equals(h.Id, server.HostId, StringComparison.OrdinalIgnoreCase));
            var match = host?.Servers.FirstOrDefault(s =>
                string.Equals(s.ServerPublicId, server.ServerPublicId, StringComparison.OrdinalIgnoreCase));
            if (match is null)
                return;

            match.SelectedAccountId = accountId ?? "";
            AppConfigStore.SaveAppConfig(config);
        }
        catch
        {
            // The in-memory selection still applies for this session.
        }
    }

    private MinecraftAccount? GetSelectedLocalAccount()
    {
        var selectedId = _selectedLocalInstance?.Instance.SelectedAccountId;
        if (string.IsNullOrWhiteSpace(selectedId))
            selectedId = AccountStore.GetDefaultAccountId();

        var account = _accounts.FirstOrDefault(a =>
            string.Equals(a.Uuid, selectedId, StringComparison.OrdinalIgnoreCase));

        account ??= GetSelectedAccount(_localAccountComboBox);
        account ??= _accounts.Count == 1 ? _accounts[0] : null;
        return account;
    }

}
