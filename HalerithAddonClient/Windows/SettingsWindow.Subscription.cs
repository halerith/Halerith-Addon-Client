using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Interactivity;
using HalerithAddonClient.Core;
using HalerithAddonClient.Services;

namespace HalerithAddonClient.Windows;

public partial class SettingsWindow
{
    private bool _populatingSubscriptionAccounts;
    private string? _subscriptionReviewUuid;
    private int _subscriptionRefreshVersion;
    public bool SubscriptionAccountsModified { get; private set; }

    private void PopulateSubscriptionAccounts(string? selectUuid = null)
    {
        _populatingSubscriptionAccounts = true;
        try
        {
            var accounts = AccountStore.LoadAll();
            _subscriptionAccountBox.ItemsSource = BuildAccountPickerItems(accounts, accounts.Count > 0);
            var uuid = selectUuid ?? _subscriptionReviewUuid ?? AccountStore.GetDefaultAccountId();
            var selected = accounts.FirstOrDefault(account =>
                string.Equals(account.Uuid, uuid, StringComparison.OrdinalIgnoreCase)) ?? accounts.FirstOrDefault();
            _subscriptionReviewUuid = selected?.Uuid;
            SelectAccountPickerItem(_subscriptionAccountBox, selected);
        }
        finally { _populatingSubscriptionAccounts = false; }
        ClearAddActionSelectionLater(_subscriptionAccountBox);
    }

    private async void SubscriptionAccount_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_populatingSubscriptionAccounts) return;
        if (_subscriptionAccountBox.SelectedItem is AccountPickerItem { IsAdd: true })
        {
            PopulateSubscriptionAccounts();
            _subscriptionAccountBox.IsDropDownOpen = false;
            await AddSubscriptionAccountAsync();
            return;
        }
        _subscriptionRefreshVersion++;
        if (GetSelectedAccount(_subscriptionAccountBox) is not { } account)
        {
            _subscriptionReviewUuid = null;
            _subscriptionStatus.Text = "Select a Minecraft account";
            _subscriptionDetails.Text = "";
            return;
        }
        _subscriptionReviewUuid = account.Uuid;
        await RefreshSubscriptionStatusAsync();
    }

    private async void SubscriptionAccountAdd_Click(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        _subscriptionAccountBox.IsDropDownOpen = false;
        await AddSubscriptionAccountAsync();
    }

    private async Task AddSubscriptionAccountAsync()
    {
        var firstAccount = AccountStore.LoadAll().Count == 0;
        var account = await new MicrosoftLoginWindow().ShowDialog<MinecraftAccount?>(this);
        if (account is null) return;
        if (firstAccount)
            AccountStore.SetDefaultAccountId(account.Uuid);
        SubscriptionAccountsModified = true;
        PopulateSubscriptionAccounts(account.Uuid);
        await RefreshSubscriptionStatusAsync();
    }

    private void SubscriptionAccountDelete_Click(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is not Button { Tag: AccountPickerItem { Account: { } account } }) return;
        AccountStore.Remove(account.Uuid);
        ManagedServerAccessGate.Forget(account.Uuid);
        if (string.Equals(AccountStore.GetDefaultAccountId(), account.Uuid, StringComparison.OrdinalIgnoreCase))
            AccountStore.SetDefaultAccountId(AccountStore.LoadAll().FirstOrDefault()?.Uuid ?? "");
        SubscriptionAccountsModified = true;
        _subscriptionReviewUuid = null;
        _subscriptionRefreshVersion++;
        PopulateSubscriptionAccounts();
        _ = RefreshSubscriptionStatusAsync();
    }

    private void SubscriptionAccount_DropDownOpened(object? sender, EventArgs e)
    {
        if (AccountStore.LoadAll().Count > 0) return;
        _populatingSubscriptionAccounts = true;
        try
        {
            _subscriptionAccountBox.ItemsSource = new[] { AccountPickerItem.AddAccount() };
            _subscriptionAccountBox.SelectedItem = null;
        }
        finally { _populatingSubscriptionAccounts = false; }
        ClearAddActionSelectionLater(_subscriptionAccountBox);
    }

    private void SubscriptionAccount_DropDownClosed(object? sender, EventArgs e)
    {
        if (AccountStore.LoadAll().Count > 0) return;
        _populatingSubscriptionAccounts = true;
        try
        {
            _subscriptionAccountBox.SelectedItem = null;
            _subscriptionAccountBox.ItemsSource = Array.Empty<AccountPickerItem>();
        }
        finally { _populatingSubscriptionAccounts = false; }
    }

    private async void SubscriptionRefresh_Click(object? sender, RoutedEventArgs e) =>
        await RefreshSubscriptionStatusAsync();

    private async Task RefreshSubscriptionStatusAsync()
    {
        var refreshVersion = ++_subscriptionRefreshVersion;
        if (GetSelectedAccount(_subscriptionAccountBox) is not { } account)
        {
            _subscriptionStatus.Text = "Select a Minecraft account";
            _subscriptionDetails.Text = "";
            _subscriptionError.Text = "";
            return;
        }

        var expectedUuid = MinecraftProfileClient.NormalizeUuid(account.Uuid);
        if (expectedUuid is null)
        {
            _subscriptionStatus.Text = "Subscription status unavailable";
            _subscriptionDetails.Text = "";
            return;
        }
        _subscriptionRefreshButton.IsEnabled = false;
        _subscriptionError.Text = "";
        if (ManagedServerAccessGate.CachedFor(expectedUuid) is { } cached)
            ShowSubscriptionStatus(cached, ManagedAccessDisplayStore.Load(expectedUuid)?.FreeServerName);
        else
        {
            _subscriptionStatus.Text = "Checking status…";
            _subscriptionDetails.Text = "";
        }
        try
        {
            if (account.IsExpired || string.IsNullOrWhiteSpace(account.McAccessToken))
            {
                using var auth = new MicrosoftAuthService();
                account = await auth.RefreshAsync(account);
                AccountStore.Save(account);
                if (refreshVersion != _subscriptionRefreshVersion) return;
                PopulateSubscriptionAccounts(account.Uuid);
            }

            var config = new RuntimeConfig
            {
                ApiBaseUrl = ManagerApiDefaults.ApiBaseUrl,
                MinecraftUuid = expectedUuid,
                MinecraftAccessToken = account.McAccessToken,
                StartupTimeoutSeconds = 30
            };
            using var api = new ApiClient(config);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(35));
            var health = await api.GetHostHealthWithMinecraftAsync(
                account.McAccessToken, expectedUuid, account.Gamertag, timeout.Token, _clientVersion);
            if (refreshVersion != _subscriptionRefreshVersion) return;
            if (!health.Ok || !string.Equals(
                    MinecraftProfileClient.NormalizeUuid(health.Minecraft?.Uuid), expectedUuid,
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(health.Message ?? health.Error ??
                    "Halerith could not verify this Minecraft account.");

            ManagedServerAccessGate.Record(health);
            var access = health.FreeAccess;
            if (access?.Ok != true)
            {
                _subscriptionStatus.Text = "Subscription status unavailable";
                _subscriptionDetails.Text = "";
                return;
            }

            var eligible = health.Environments.Where(environment => environment.AccessActive)
                .SelectMany(environment => environment.Servers).Where(server => server.AccessActive)
                .GroupBy(server => server.ServerPublicId, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First()).ToList();
            var selected = eligible.FirstOrDefault(server => access.ServerPublicIds.Contains(
                server.ServerPublicId, StringComparer.OrdinalIgnoreCase));
            ShowSubscriptionStatus(access, selected?.Name);
        }
        catch (Exception ex)
        {
            if (refreshVersion != _subscriptionRefreshVersion) return;
            var lastKnown = ManagedServerAccessGate.CachedFor(expectedUuid);
            if (lastKnown is not null)
            {
                ShowSubscriptionStatus(lastKnown, ManagedAccessDisplayStore.Load(expectedUuid)?.FreeServerName);
                _subscriptionError.Text = "Could not refresh status. Showing saved subscription information.";
            }
            else
            {
                _subscriptionStatus.Text = "Subscription status unavailable";
                _subscriptionDetails.Text = "";
                _subscriptionError.Text = ex.Message;
            }
        }
        finally { if (refreshVersion == _subscriptionRefreshVersion) _subscriptionRefreshButton.IsEnabled = true; }
    }

    private void ShowSubscriptionStatus(ManagedAccessStatusResponse access, string? freeServerName)
    {
        _subscriptionStatus.Text = access.Mode switch
        {
            "paid" when access.GrantSource == "manager" => "Manager Subscription",
            "paid" => "Subscribed",
            "trial" or "new" => "Trial",
            "free" => "Free",
            _ => "Subscription status unavailable"
        };
        var details = new List<string>();
        if (access.Mode == "free" && !string.IsNullOrWhiteSpace(freeServerName))
            details.Add($"Free server: {freeServerName}");
        if (access.Mode == "new")
            details.Add("Trial starts with your first managed sync.");
        if (access.Mode == "trial" && DateTimeOffset.TryParse(access.TrialEndsAt, out var trialEnd))
            details.Add($"Trial ends: {trialEnd.ToLocalTime():d}");
        if (access.Mode == "paid" && DateTimeOffset.TryParse(access.PaidAccessEndsAt, out var paidEnd))
            details.Add($"Current period ends: {paidEnd.ToLocalTime():d}");
        if (access.Mode == "free" && DateTimeOffset.TryParse(access.NextChangeAt, out var nextChange))
            details.Add($"Next Free server change: {nextChange.ToLocalTime():d}");
        if (access.Mode == "free" && access.CanChangeNow)
            details.Add("Free server choice can be changed now.");
        if (!string.IsNullOrWhiteSpace(access.LinkedHalerithEmail))
            details.Add($"Linked to Halerith account: {access.LinkedHalerithEmail}");
        _subscriptionDetails.Text = string.Join(Environment.NewLine, details);
    }

    private void SubscriptionLicensingPortal_Click(object? sender, RoutedEventArgs e) =>
        OpenSubscriptionPortal("https://addon.halerith.com/licensing");

    private void SubscriptionConfigurationPortal_Click(object? sender, RoutedEventArgs e) =>
        OpenSubscriptionPortal("https://addon.halerith.com/client");

    private void OpenSubscriptionPortal(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { _subscriptionError.Text = $"Could not open your browser. Visit {url}"; }
    }
}
