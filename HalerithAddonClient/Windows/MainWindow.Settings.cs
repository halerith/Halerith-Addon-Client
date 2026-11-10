using HalerithAddonClient.Core;
using HalerithAddonClient.Services;

namespace HalerithAddonClient.Windows;

public partial class MainWindow
{
    // ══════════════════════════════════════════════════════════════════════
    //  Settings and managed access
    // ══════════════════════════════════════════════════════════════════════

    private void SettingsButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => OpenSettingsWindow();

    /// <summary>
    /// Opens <see cref="SettingsWindow"/>. After the dialog closes with
    /// result == true, the server list and content are reloaded so
    /// changes take effect immediately.
    /// </summary>
    private async void OpenSettingsWindow()
    {
        var config = AppConfigStore.LoadAppConfig();

        var settingsWin = new SettingsWindow(
            config,
            _clientVersion,
            sessionHostId: _vm.SelectedServer?.HostId,
            sessionServerPublicId: _vm.SelectedServer?.ServerPublicId,
            subscriptionAccountUuid: CurrentAccountForSettings()?.Uuid);

        var saved = await settingsWin.ShowDialog<bool?>(this) == true;
        if (settingsWin.SubscriptionAccountsModified)
            RefreshAccountSelectorForSettings();
        if (saved || settingsWin.SubscriptionAccountsModified)
        {
            PopulateLocalInstances();
            await _vm.LoadServersAsync();
            ApplyConfiguredDefaultInstanceSelection();
            UpdateAllServerRuntimeBuildStates();
            await _vm.LoadAllServersAsync();
            UpdateAllServerRuntimeBuildStates();
        }

        if (settingsWin.LocalInstancesChanged)
            PopulateLocalInstances();

        _vm.RefreshManagedAccessVisualState();
    }

    // ══════════════════════════════════════════════════════════════════════
    //  Managed access check for content updates
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Checks the selected Minecraft account before a managed content update.
    /// </summary>
    private async void OnRequestManagedAccessCheck(object? sender, TaskCompletionSource<ManagedAccessCheckResult> completion)
    {
        try
        {
            var server = _vm.SelectedServer;
            var config = _vm.CurrentConfig;
            var gate = server is null || config is null
                ? ManagedAccessCheckResult.Block("Select a server before updating.")
                : await ManagedServerAccessGate.EnsureAsync(
                    config, server.ServerPublicId, ResolveSelectedManagedMinecraftUuid());
            _vm.RefreshManagedAccessVisualState();
            completion.TrySetResult(gate);
        }
        catch (Exception)
        {
            completion.TrySetResult(ManagedAccessCheckResult.Block("Cannot update - please check your connection and try again."));
        }
    }
}
