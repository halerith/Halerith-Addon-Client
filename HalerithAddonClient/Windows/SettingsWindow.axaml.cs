using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using HalerithAddonClient;              // AppTheme, LauncherLog
using HalerithAddonClient.Controls;
using HalerithAddonClient.Core;
using HalerithAddonClient.ViewModels;

namespace HalerithAddonClient.Windows;

/// <summary>
/// Code-behind for the Settings tabbed dialog.
///
/// Responsibilities:
///   • Creating and binding <see cref="SettingsViewModel"/>.
///   • Populating the Application tab named controls and reading them on save.
///   • Populating the Application tab named controls and reading them on save.
///   • Showing managed access for the selected Minecraft account.
///   • Hosting the Save / Cancel / Submit Logs footer actions.
///
/// </summary>
public partial class SettingsWindow : Window
{
    // ══════════════════════════════════════════════════════════════════════
    //  Static constants
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Zero-based index of the Subscription tab in the settings <see cref="TabControl"/>.
    /// Used to refresh the Subscription tab when it is selected.
    /// </summary>
    public const int SubscriptionTabIndex = 2;

    private void HelpLink_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string url }) return;
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            _helpLinkError.Text = "";
        }
        catch
        {
            _helpLinkError.Text = $"Could not open your browser. Visit {url}";
        }
    }

    // ══════════════════════════════════════════════════════════════════════
    //  Instance fields
    // ══════════════════════════════════════════════════════════════════════

    private readonly SettingsViewModel _vm;
    private readonly string  _clientVersion;
    private readonly string? _sessionHostId;
    private readonly string? _sessionServerPublicId;

    private string _footerStatusText = "";
    private CancellationTokenSource? _copyFeedbackCts;

    // ══════════════════════════════════════════════════════════════════════
    //  Public surface
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// The saved <see cref="AppConfig"/>.
    /// Inspect after window closes with result == true.
    /// </summary>
    public AppConfig Config => _vm.Config;

    /// <summary>
    /// Local instances are now managed from the main sidebar per-instance gear.
    /// Kept for older callers that refresh local instances after Settings closes.
    /// </summary>
    public bool LocalInstancesChanged => false;

    // ══════════════════════════════════════════════════════════════════════
    //  Construction
    // ══════════════════════════════════════════════════════════════════════

    public SettingsWindow(AppConfig config, string clientVersion,
                          string? sessionHostId  = null,
                          string? sessionServerPublicId = null,
                          string? subscriptionAccountUuid = null)
    {
        _clientVersion   = clientVersion;
        _sessionHostId   = sessionHostId;
        _sessionServerPublicId = sessionServerPublicId;

        _vm = new SettingsViewModel(config);
        _vm.SaveAdditionalSettings = SaveCurseForgeKey;

        // Subscribe to VM close requests before InitializeComponent so we never
        // miss an event that fires during construction.
        _vm.RequestSave   += OnVmRequestSave;
        _vm.RequestCancel += OnVmRequestCancel;

        DataContext = _vm;
        InitializeComponent();
        SwapApplicationAndEnvironmentTabs();
        PopulateSubscriptionAccounts(subscriptionAccountUuid);

        Title = "Halerith Addon Client — Settings";

        AppWindowIcon.Apply(this);

        LoadFromConfig();
        Opened += (_, _) => StartGuidedTips();

        // Set the About tab version text now that InitializeComponent has run.
        _versionText.Text = $"Version {clientVersion}";

        // Set the EULA acceptance status.
        var eulaStamp = EulaStore.GetAcceptedStamp();
        if (!string.IsNullOrEmpty(eulaStamp))
        {
            // Stamp format: YYYYMMDD-X.Y.Z — parse date portion for display.
            var dashIdx = eulaStamp.IndexOf('-');
            var datePart = dashIdx >= 8 ? eulaStamp[..dashIdx] : eulaStamp;
            string displayDate = datePart;
            if (datePart.Length == 8 &&
                DateTime.TryParseExact(datePart, "yyyyMMdd", null,
                    System.Globalization.DateTimeStyles.None, out var dt))
            {
                displayDate = dt.ToString("MMMM d, yyyy");
            }
            var versionPart = dashIdx >= 0 && dashIdx < eulaStamp.Length - 1
                ? eulaStamp[(dashIdx + 1)..]
                : "";
            _eulaStatusText.Text = $"Accepted on {displayDate} (v{versionPart})";
        }
        else
        {
            _eulaStatusText.Text = "Not yet accepted";
        }
    }

    // ══════════════════════════════════════════════════════════════════════
    //  Public navigation
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Navigates to the tab at <paramref name="index"/> (zero-based).
    /// Opens a specific settings tab (e.g. <see cref="SubscriptionTabIndex"/> = 2).
    /// </summary>
    /// <param name="index">Zero-based tab index to select.</param>
    public void NavigateToTab(int index)
    {
        if (_tabControl is not null && index < _tabControl.Items.Count)
            _tabControl.SelectedIndex = index;
    }

    /// <summary>
    /// <summary>Zero-based index of the Environments tab.</summary>
    private const int InstancesTabIndex = 1;

    private void SwapApplicationAndEnvironmentTabs()
    {
        if (_tabControl.Items.Count < 2)
            return;

        var environmentsTab = _tabControl.Items[0];
        var applicationTab = _tabControl.Items[1];
        _tabControl.Items.RemoveAt(1);
        _tabControl.Items.RemoveAt(0);
        _tabControl.Items.Insert(0, applicationTab);
        _tabControl.Items.Insert(1, environmentsTab);
        _tabControl.SelectedIndex = 0;
    }

    private void TabControl_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        // Only handle top-level tab switches (ignore nested SelectionChanged bubbling).
        if (e.Source != _tabControl) return;

        if (_tabControl?.SelectedIndex == SubscriptionTabIndex)
            _ = RefreshSubscriptionStatusAsync();

        // Lazy-load Environments tab on first visit
        if (_tabControl?.SelectedIndex == InstancesTabIndex && !_instancesLoaded)
        {
            LoadInstances();
        }
    }

    // ══════════════════════════════════════════════════════════════════════
    //  VM event handlers
    // ══════════════════════════════════════════════════════════════════════

    private void OnVmRequestSave()
    {
        SetSuccessStatus("Settings saved.");
        Close(true);
    }

    private void OnVmRequestCancel()
    {
        Close(false);
    }

    // ══════════════════════════════════════════════════════════════════════
    //  Inner types
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>Item for the Default Instance ComboBox in the Application tab.</summary>
    private sealed record DefaultInstanceItem(string Kind, string Id, string Name)
    {
        public override string ToString() => Name;
    }

    /// <summary>Display row bound to the Environments tab DataGrid.</summary>
    public sealed class InstanceRow
    {
        public long   DbId             { get; init; }
        public string MinecraftVersion { get; init; } = "";
        public string Loader           { get; init; } = "";
        public string LoaderVersion    { get; init; } = "";
        public long   SizeBytes        { get; init; }
        public bool   InUse            { get; init; }
        public string InUseReason      { get; init; } = "";
        public string RuntimeRoot      { get; init; } = "";
        public int    FileCount        { get; init; }

        /// <summary>True for partial/failed builds that exist on disk but not in the DB.</summary>
        public bool   IsIncomplete     { get; init; }

        /// <summary>Disk path to the incomplete cache directory (only set when <see cref="IsIncomplete"/>).</summary>
        public string IncompletePath   { get; init; } = "";

        public string LoaderDisplay
        {
            get
            {
                if (string.IsNullOrWhiteSpace(Loader))
                    return "Vanilla";

                var name = char.ToUpper(Loader[0]) + Loader[1..];
                return string.IsNullOrWhiteSpace(LoaderVersion) ? name : $"{name} {LoaderVersion}";
            }
        }
        public string SizeDisplay   => SizeBytes switch
        {
            < 1024                => $"{SizeBytes} B",
            < 1024 * 1024         => $"{SizeBytes / 1024.0:F1} KB",
            < 1024L * 1024 * 1024 => $"{SizeBytes / (1024.0 * 1024):F1} MB",
            _                     => $"{SizeBytes / (1024.0 * 1024 * 1024):F2} GB",
        };
        public string StatusDisplay => InUse ? "In Use" : IsIncomplete ? "Incomplete" : "Unused";
    }

}
