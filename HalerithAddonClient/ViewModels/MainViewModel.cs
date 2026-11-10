using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using HalerithAddonClient.Core;
using HalerithAddonClient.Services;

namespace HalerithAddonClient.ViewModels;

/// <summary>
/// Primary ViewModel for <c>Windows/MainWindow.xaml</c>.
///
/// Owns the server sidebar, content list and update pipeline. Managed access
/// is read for the selected Minecraft account from server responses.
/// </summary>
public sealed partial class MainViewModel : INotifyPropertyChanged
{
    // ══════════════════════════════════════════════════════════════════════
    //  Private fields
    // ══════════════════════════════════════════════════════════════════════

    private readonly string _clientVersion;

    private AppConfig?      _appConfig;
    private RuntimeConfig? _config;

    /// <summary>The current <see cref="RuntimeConfig"/> for the selected server. Used by MainWindow for launching.</summary>
    public RuntimeConfig? CurrentConfig => _config;
    private ManifestResponse? _manifest;
    private SyncPlan?       _plan;

    // ── Backing stores for notifying properties ───────────────────────────
    private ServerItemViewModel? _selectedServer;
    private bool   _isSyncing;
    private double _syncProgress;
    private bool   _isBuildActivity;
    private double _buildActivityProgress;
    private bool   _isUpdating;
    private double _updateProgress;
    private string _statusText = "Starting up…";
    private int    _totalItems;
    private int    _pendingActions;
    private int    _failedActions;
    private string _lastSyncText = "Pending";
    private string _serverMotdText = "";
    private bool   _isServerMotdVisible;
    private bool   _hasLoadedServers;

    private const int ServerMotdDisplaySeconds = 5;
    private static readonly Regex MinecraftColorCodeRegex = new(@"(?:§|\\u00a7|\\u00A7)[0-9A-FK-ORa-fk-or]", RegexOptions.Compiled);
    private static readonly Regex WhitespaceRegex = new(@"\s+", RegexOptions.Compiled);

    // ══════════════════════════════════════════════════════════════════════
    //  Construction
    // ══════════════════════════════════════════════════════════════════════

    public MainViewModel()
    {
        var assembly = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
        _clientVersion = assembly.GetCustomAttribute<AssemblyFileVersionAttribute>()?.Version
            ?? assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString()
            ?? "1.0.0";

        // Avalonia handles thread-safe collection updates automatically.
        // The _consoleLock is still used in AppendConsole() to prevent
        // concurrent modifications to ConsoleLines during rapid updates.

        // Commands
        RefreshCommand = new AsyncRelayCommand(
            () => LoadContentAsync(),
            () => !IsSyncing && SelectedServer is { IsRefreshing: false });

        UpdateCommand = new AsyncRelayCommand(
            () => RunUpdateAsync(),
            () => IsApplyEnabled);

    }

    // ══════════════════════════════════════════════════════════════════════
    //  Public properties — server sidebar
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>Flat list of all servers across all hosts, shown in the left sidebar.</summary>
    public ObservableCollection<ServerItemViewModel> Servers { get; } = [];

    /// <summary>
    /// The server entry currently selected in the sidebar.
    /// Changing this triggers a content refresh for the new server.
    /// </summary>
    public ServerItemViewModel? SelectedServer
    {
        get => _selectedServer;
        set
        {
            if (_selectedServer == value) return;
            if (_selectedServer is not null)
                _selectedServer.PropertyChanged -= SelectedServer_PropertyChanged;

            _selectedServer = value;
            if (_selectedServer is not null)
                _selectedServer.PropertyChanged += SelectedServer_PropertyChanged;

            OnPropertyChanged();
            OnPropertyChanged(nameof(SelectedServerDisplayName));
            OnPropertyChanged(nameof(HasSelectedServer));
            OnPropertyChanged(nameof(SelectedServerSubtitle));
            OnPropertyChanged(nameof(SelectedServerTargetSubtitle));
            OnPropertyChanged(nameof(SelectedServerHasVersionChange));
            OnPropertyChanged(nameof(SelectedServerSubtitleIsVisible));
            OnPropertyChanged(nameof(IsLaunchEnabled));
            OnPropertyChanged(nameof(IsSelectedServerAllowed));
            OnPropertyChanged(nameof(IsApplyEnabled));
            OnPropertyChanged(nameof(IsActivityBarVisible));
            RefreshSelectedServerMotdDisplay();
            RefreshManagedAccessVisualState();
            (RefreshCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            // Rebuild config for the new server and display cached content immediately —
            // no network call.  Use the Refresh button or startup to hit the API.
            RebuildConfig();
            DisplayCachedContent();
        }
    }

    private void SelectedServer_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!ReferenceEquals(sender, _selectedServer))
            return;

        if (e.PropertyName is nameof(ServerItemViewModel.IsRefreshing))
        {
            OnPropertyChanged(nameof(IsActivityBarVisible));
            OnPropertyChanged(nameof(IsActivityBarIndeterminate));
            OnPropertyChanged(nameof(ActivityBarProgress));
            (RefreshCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            (UpdateCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        }

        if (e.PropertyName is nameof(ServerItemViewModel.PowerStatus)
            or nameof(ServerItemViewModel.PowerStatusText)
            or nameof(ServerItemViewModel.PowerStatusForeground)
            or nameof(ServerItemViewModel.PowerStatusBackground))
        {
            OnPropertyChanged(nameof(IsLaunchEnabled));
            OnPropertyChanged(nameof(LauncherNotConfiguredReason));
        }
    }

    /// <summary>Headline shown in the main toolbar (server name).</summary>
    public string SelectedServerDisplayName =>
        _selectedServer?.DisplayName ?? (_hasLoadedServers ? "No server selected" : "Loading...");

    public bool HasSelectedServer => _selectedServer is not null;

    private bool HasLoadedServers
    {
        get => _hasLoadedServers;
        set
        {
            if (_hasLoadedServers == value) return;
            _hasLoadedServers = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SelectedServerDisplayName));
        }
    }

    /// <summary>Secondary toolbar label — installed/local MC version and loader info.</summary>
    public string SelectedServerSubtitle
    {
        get
        {
            if (_selectedServer is null) return "";
            return _selectedServer.InstalledRuntimeDisplay;
        }
    }

    /// <summary>Manifest-advertised MC version and loader info when it differs from local storage.</summary>
    public string SelectedServerTargetSubtitle
    {
        get
        {
            if (_selectedServer is null) return "";
            return _selectedServer.TargetRuntimeDisplay;
        }
    }

    /// <summary>True when the latest manifest advertises a different MC/loader target than local storage.</summary>
    public bool SelectedServerHasVersionChange =>
        _selectedServer?.HasRuntimeVersionChange == true;

    /// <summary>
    /// Hides the subtitle TextBlock when there is nothing to show.
    /// Formerly a WPF DataTrigger on SelectedServerSubtitle == "".
    /// </summary>
    public bool SelectedServerSubtitleIsVisible =>
        !string.IsNullOrEmpty(SelectedServerSubtitle)
        || !string.IsNullOrEmpty(SelectedServerTargetSubtitle);

    // ══════════════════════════════════════════════════════════════════════
    //  Public properties — content area
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>Items displayed in the main DataGrid.</summary>
    public ContentItemCollection ContentItems { get; } = [];

    public string ServerMotdText
    {
        get => _serverMotdText;
        private set
        {
            if (_serverMotdText == value) return;
            _serverMotdText = value;
            OnPropertyChanged();
        }
    }

    public bool IsServerMotdVisible
    {
        get => _isServerMotdVisible;
        private set
        {
            if (_isServerMotdVisible == value) return;
            _isServerMotdVisible = value;
            OnPropertyChanged();
        }
    }

    public void HideServerMotd()
    {
        if (_selectedServer is not null)
        {
            _selectedServer.IsMotdStripDismissed = true;
            _selectedServer.MotdStripRemainingSeconds = 0;
        }

        RefreshSelectedServerMotdDisplay();
    }

    public void TickSelectedServerMotd()
    {
        if (_selectedServer is null || !IsServerMotdVisible)
            return;

        if (_selectedServer.MotdStripRemainingSeconds <= 0)
        {
            HideServerMotd();
            return;
        }

        _selectedServer.MotdStripRemainingSeconds--;
        if (_selectedServer.MotdStripRemainingSeconds <= 0)
            HideServerMotd();
    }

    public void RefreshSelectedServerMotdDisplay()
    {
        var server = _selectedServer;
        var show = server is not null
            && !server.IsMotdStripDismissed
            && server.MotdStripRemainingSeconds > 0
            && !string.IsNullOrWhiteSpace(server.MotdStripText);

        ServerMotdText = show ? server!.MotdStripText : "";
        IsServerMotdVisible = show;
    }

    private void ArmServerMotd(ServerItemViewModel? server)
    {
        var motd = NormalizeMotd(server?.Server.Motd);
        if (server is null)
        {
            RefreshSelectedServerMotdDisplay();
            return;
        }

        server.MotdStripText = motd;
        server.IsMotdStripDismissed = string.IsNullOrWhiteSpace(motd);
        server.MotdStripRemainingSeconds = string.IsNullOrWhiteSpace(motd) ? 0 : ServerMotdDisplaySeconds;

        if (ReferenceEquals(server, _selectedServer) || IsSelectedServerIdentity(server))
            RefreshSelectedServerMotdDisplay();
    }

    private void ArmServerMotdsForHost(string hostId, ServerItemViewModel? except = null)
    {
        foreach (var server in Servers.Where(s =>
                     string.Equals(s.HostId, hostId, StringComparison.OrdinalIgnoreCase)))
        {
            if (except is not null && ReferenceEquals(server, except))
                continue;

            ArmServerMotd(server);
        }
    }

    private static string NormalizeMotd(string? motd)
    {
        if (string.IsNullOrWhiteSpace(motd))
            return "";

        var text = motd
            .Replace("\\:", ":")
            .Replace("\\n", " ")
            .Replace("\r", " ")
            .Replace("\n", " ")
            .Replace("\t", " ")
            .Trim();

        text = MinecraftColorCodeRegex.Replace(text, "");
        text = WhitespaceRegex.Replace(text, " ");
        return text.Trim();
    }

    /// <summary>Whether a refresh or update operation is in progress.</summary>
    public bool IsSyncing
    {
        get => _isSyncing;
        private set
        {
            if (_isSyncing == value) return;
            _isSyncing = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsActivityBarVisible));
            OnPropertyChanged(nameof(IsApplyEnabled));
            OnPropertyChanged(nameof(IsLaunchEnabled));
            (RefreshCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            (UpdateCommand    as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        }
    }

    /// <summary>Progress 0–100; drives the ProgressBar during sync.</summary>
    public double SyncProgress
    {
        get => _syncProgress;
        private set
        {
            if (_syncProgress == value) return;
            _syncProgress = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ActivityBarProgress));
        }
    }

    /// <summary>
    /// True while the selected server is doing refresh/update work.
    /// Covers both full sync operations and selected-row refresh state restored from cache.
    /// </summary>
    public bool IsActivityBarVisible =>
        IsSyncing || _selectedServer?.IsRefreshing == true || _isBuildActivity;

    public bool IsIdleStatusSummaryVisible => !IsActivityBarVisible;

    /// <summary>True while the activity bar should animate instead of showing a determinate value.</summary>
    public bool IsActivityBarIndeterminate =>
        !_isBuildActivity && (IsSyncing || _selectedServer?.IsRefreshing == true);

    /// <summary>Visible progress value for the activity divider. Idle is always empty.</summary>
    public double ActivityBarProgress =>
        _isBuildActivity ? _buildActivityProgress : IsActivityBarVisible ? SyncProgress : 0;

    public void SetBuildActivityProgress(double? progress)
    {
        var nextActive = progress.HasValue;
        var nextProgress = progress.GetValueOrDefault();
        if (_isBuildActivity == nextActive && Math.Abs(_buildActivityProgress - nextProgress) < 0.01)
            return;

        _isBuildActivity = nextActive;
        _buildActivityProgress = nextProgress;
        OnPropertyChanged(nameof(IsActivityBarVisible));
    }

    /// <summary>Whether a self-update download is in progress.</summary>
    public bool IsUpdating
    {
        get => _isUpdating;
        private set
        {
            if (_isUpdating == value) return;
            _isUpdating = value;
            OnPropertyChanged();
        }
    }

    /// <summary>Progress 0–100 for the update download bar.</summary>
    public double UpdateProgress
    {
        get => _updateProgress;
        private set { if (_updateProgress != value) { _updateProgress = value; OnPropertyChanged(); } }
    }

    /// <summary>Current operation text shown in the bottom status bar.</summary>
    public string StatusText
    {
        get => _statusText;
        internal set
        {
            if (_statusText == value) return;
            _statusText = value;
            OnPropertyChanged();
            AppendConsole(value);
        }
    }

    /// <summary>
    /// Updates the status bar text without writing to the activity console.
    /// Used when restoring cached state (e.g. switching between servers) where
    /// the message has already been logged and echoing it again is noise.
    /// </summary>
    private void SetStatusSilent(string value)
    {
        if (_statusText == value) return;
        _statusText = value;
        OnPropertyChanged(nameof(StatusText));
    }

    /// <summary>Lines written to the activity console (capped at 2000 entries).</summary>
    public ObservableCollection<string> ConsoleLines { get; } = [];

    /// <summary>
    /// Lock object used to serialise cross-thread access to <see cref="ConsoleLines"/>.
    /// In Avalonia, this coordinates with the _consoleLock in AppendConsole() to prevent
    /// concurrent modifications during rapid updates from background threads.
    /// </summary>
    private readonly object _consoleLock = new();

    /// <summary>
    /// Exposes the console lock so diagnostic snapshots can safely copy
    /// <see cref="ConsoleLines"/> without racing with <see cref="AppendConsole"/>.
    /// </summary>
    internal object ConsoleLock => _consoleLock;

    /// <summary>Total content items in the last manifest.</summary>
    public int TotalItems
    {
        get => _totalItems;
        private set { if (_totalItems != value) { _totalItems = value; OnPropertyChanged(); } }
    }

    /// <summary>Actions that require work (Download + Replace + Remove) in the current plan.</summary>
    public int PendingActions
    {
        get => _pendingActions;
        private set
        {
            if (_pendingActions == value) return;
            _pendingActions = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsApplyEnabled));
            OnPropertyChanged(nameof(IsLaunchEnabled));
            OnPropertyChanged(nameof(PendingActionsForeground));
            (UpdateCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        }
    }

    /// <summary>
    /// Green when no pending actions, warning orange when updates are waiting.
    /// Consistent with the local instance panel's pending colour logic.
    /// </summary>
    public ISolidColorBrush PendingActionsForeground =>
        _pendingActions == 0 ? _chipSuccessBrush : _chipWarningBrush;

    public int FailedActions
    {
        get => _failedActions;
        private set
        {
            if (_failedActions == value) return;
            _failedActions = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(FailedActionsForeground));
        }
    }

    public ISolidColorBrush FailedActionsForeground =>
        _failedActions == 0 ? _chipSuccessBrush : _chipErrorBrush;

    /// <summary>Human-readable time of the last successful update or refresh.</summary>
    public string LastSyncText
    {
        get => _lastSyncText;
        private set { if (_lastSyncText != value) { _lastSyncText = value; OnPropertyChanged(); } }
    }

    // ══════════════════════════════════════════════════════════════════════
    //  Public properties — licensing
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// True when the Update button should be enabled.
    /// Enabled when the selected Minecraft account may sync pending server content.
    /// Bound directly to the Apply button's <c>IsEnabled</c>.
    /// </summary>
    public bool IsApplyEnabled
    {
        get
        {
            try   { return !IsSyncing && _pendingActions > 0 && IsSelectedServerAllowed; }
            catch { return false; }
        }
    }

    public bool IsTrialMode => IsAccessPillTrialState();

    /// <summary>
    /// Whether the Launch button is enabled. Requires a selected server,
    /// current local content, and a launcher mode that is properly configured.
    /// Server power state is informational and does not prevent launching.
    /// </summary>
    public bool IsLaunchEnabled
    {
        get
        {
            if (IsSyncing || _selectedServer is null || _config is null || !IsSelectedServerAllowed) return false;
            if (_selectedServer.CachedPendingActions > 0) return false;
            if (_selectedServer.CachedPlan is { } plan && ServerLaunchReadiness.HasRequiredManualFiles(plan)) return false;
            return LaunchService.IsLauncherConfigured(_config, _selectedServer.Server, out _);
        }
    }

    /// <summary>
    /// User-facing reason why the Launch button is disabled, or empty when enabled.
    /// Used by <c>MainWindow</c> to show a status message when the user tries to act.
    /// </summary>
    public string LauncherNotConfiguredReason
    {
        get
        {
            if (_selectedServer is null || _config is null) return "No server selected.";
            if (!IsSelectedServerAllowed) return "This server is no longer in the available roster. Refresh the server list.";
            if (_selectedServer.CachedPendingActions > 0) return "Updates are pending. Update before launching.";
            if (_selectedServer.CachedPlan is { } plan && ServerLaunchReadiness.HasRequiredManualFiles(plan))
                return "Required CurseForge files need manual installation before launching.";
            LaunchService.IsLauncherConfigured(_config, _selectedServer.Server, out var reason);
            return reason;
        }
    }

    public void RefreshLaunchState()
    {
        OnPropertyChanged(nameof(IsLaunchEnabled));
        (RefreshCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (UpdateCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
    }

    // ── Managed access display ────────────────────────────────────────────

    private static readonly ISolidColorBrush _chipInactiveBg  = new SolidColorBrush(Color.FromRgb(0x3B, 0x2E, 0x0A));
    private static readonly ISolidColorBrush _chipSuccessBrush = new SolidColorBrush(Color.FromRgb(0x10, 0xB9, 0x81)); // SuccessBrush
    private static readonly ISolidColorBrush _chipWarningBrush = new SolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B)); // WarningBrush
    private static readonly ISolidColorBrush _chipErrorBrush   = new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44)); // DangerBrush
    private static readonly ISolidColorBrush _statusBarBg = new SolidColorBrush(Color.FromRgb(0x1A, 0x1D, 0x27)); // SurfaceBrush
    private static readonly ISolidColorBrush _statusBarTrialBg = new SolidColorBrush(Color.FromRgb(0x3B, 0x2E, 0x0A));
    private static readonly ISolidColorBrush _statusBarBorder = new SolidColorBrush(Color.FromRgb(0x2D, 0x31, 0x49)); // BorderColorBrush
    private static readonly ISolidColorBrush _statusBarTrialBorder = new SolidColorBrush(Color.FromRgb(0x8C, 0x35, 0x00));

    public bool IsAccessPillVisible =>
        ManagedServerAccessGate.CachedFor(ResolveAccessPillAccountUuid())?.Mode is "new" or "trial" or "free";

    public string AccessPillText =>
        ManagedServerAccessGate.CachedFor(ResolveAccessPillAccountUuid())?.Mode == "free" ? "Free" : "Trial";

    public ISolidColorBrush AccessPillBackground => _chipInactiveBg;

    public ISolidColorBrush AccessPillBorder => _chipWarningBrush;

    public ISolidColorBrush AccessPillForeground => _chipWarningBrush;

    private bool IsAccessPillTrialState()
        => ManagedServerAccessGate.CachedFor(ResolveAccessPillAccountUuid())?.Mode is "new" or "trial";

    public bool IsSelectedServerAllowed => _selectedServer?.Server.IsAvailable ?? true;

    private string ResolveAccessPillAccountUuid()
    {
        var accounts = AccountStore.LoadAll();
        if (accounts.Count == 0)
            return "";

        var selectedId = AccountStore.GetDefaultAccountId();
        if (string.IsNullOrWhiteSpace(selectedId) && accounts.Count == 1)
            selectedId = accounts[0].Uuid;

        var match = accounts.FirstOrDefault(a =>
            string.Equals(MinecraftProfileClient.NormalizeUuid(a.Uuid), MinecraftProfileClient.NormalizeUuid(selectedId ?? ""), StringComparison.OrdinalIgnoreCase));

        return MinecraftProfileClient.NormalizeUuid((match ?? accounts[0]).Uuid) ?? "";
    }

    public ISolidColorBrush ConsoleStatusBarBackground =>
        IsTrialMode ? _statusBarTrialBg : _statusBarBg;

    public ISolidColorBrush ConsoleStatusBarBorder =>
        IsTrialMode ? _statusBarTrialBorder : _statusBarBorder;

    public void RefreshManagedAccessVisualState()
    {
        OnPropertyChanged(nameof(IsTrialMode));
        OnPropertyChanged(nameof(IsAccessPillVisible));
        OnPropertyChanged(nameof(IsSelectedServerAllowed));
        OnPropertyChanged(nameof(IsLaunchEnabled));
        OnPropertyChanged(nameof(LauncherNotConfiguredReason));
        OnPropertyChanged(nameof(IsApplyEnabled));
        OnPropertyChanged(nameof(AccessPillText));
        OnPropertyChanged(nameof(AccessPillBackground));
        OnPropertyChanged(nameof(AccessPillBorder));
        OnPropertyChanged(nameof(AccessPillForeground));
        OnPropertyChanged(nameof(ConsoleStatusBarBackground));
        OnPropertyChanged(nameof(ConsoleStatusBarBorder));
        (UpdateCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
    }

    // ══════════════════════════════════════════════════════════════════════
    //  Commands
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>Reloads manifest and rebuilds the content plan.</summary>
    public ICommand RefreshCommand { get; }

    /// <summary>
    /// Executes the update plan.  Internally checks <see cref="IsApplyEnabled"/>
    /// and sets <see cref="StatusText"/> to an explanatory message when the
    /// license gate blocks execution.
    /// </summary>
    public ICommand UpdateCommand { get; }

    // ══════════════════════════════════════════════════════════════════════
    //  Events
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Raised when the user clicks the "Settings" hyperlink inside the license
    /// chip.  The argument is the tab index to navigate to (always 2 = Licensing).
    /// The code-behind subscribes and opens SettingsWindow at the correct tab.
    /// </summary>

    /// <summary>
    /// Raised when the selected remote host becomes invalid and the main window
    /// should promote the first local instance, if one exists.
    /// </summary>
    public event EventHandler? RequestSelectFirstLocalInstance;

    // ══════════════════════════════════════════════════════════════════════
    //  Public methods
    // ══════════════════════════════════════════════════════════════════════

}
