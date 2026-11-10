using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using HalerithAddonClient.Core;

namespace HalerithAddonClient.ViewModels;

/// <summary>
/// Represents one server entry in the left-sidebar server list.
/// Wraps a <see cref="HostConfig"/> + <see cref="ServerConfig"/> pair and tracks
/// transient display state (online indicator, last-sync timestamp) that is updated
/// by <see cref="MainViewModel"/> after each health check and successful sync.
/// </summary>
public sealed class ServerItemViewModel : INotifyPropertyChanged
{
    // ── Static palette (mirrors DarkTheme.axaml) ──────────────────────────
    private static readonly ISolidColorBrush MutedBrush   = new SolidColorBrush(Color.FromRgb(0x64, 0x74, 0x8B)); // TextMutedBrush
    private static readonly ISolidColorBrush SuccessBrush = new SolidColorBrush(Color.FromRgb(0x10, 0xB9, 0x81)); // SuccessBrush
    private static readonly ISolidColorBrush WarningBrush = new SolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B)); // WarningBrush
    private static readonly ISolidColorBrush ErrorBrush   = new SolidColorBrush(Color.FromRgb(0xF8, 0x71, 0x71)); // Softer red for offline state

    private bool      _isOnline;
    private bool      _isRefreshing;
    private DateTime? _lastSyncAt;
    private ServerRuntimeBuildStatus _runtimeBuildStatus = ServerRuntimeBuildStatus.Unknown;
    private ServerUpdateStatus _updateStatus = ServerUpdateStatus.Pending;
    private ManifestResponse? _cachedManifest;
    private Bitmap? _serverIconImage;

    // ── Construction ───────────────────────────────────────────────────────

    public ServerItemViewModel(HostConfig host, ServerConfig server)
    {
        Host   = host;
        Server = server;
        LoadCachedServerIcon();
    }

    // ── Identity ───────────────────────────────────────────────────────────

    public HostConfig   Host   { get; }
    public ServerConfig Server { get; }

    public string HostId   => Host.Id;
    public string ServerPublicId => Server.ServerPublicId;

    // ── Secondary server linkage ──────────────────────────────────────────

    /// <summary>Whether this server is a secondary that mirrors a primary.</summary>
    public bool IsSecondary => Server.IsSecondary;

    /// <summary>UUID of the primary server (only meaningful when <see cref="IsSecondary"/> is true).</summary>
    public string PrimaryServerPublicId => Server.PrimaryServerPublicId;

    /// <summary>Client runtime target follows the server, including when a pack is attached.</summary>
    public string MinecraftVersion => CachedManifest?.Server?.MinecraftVersion ?? Server.MinecraftVersion;

    /// <summary>Loader selected by the server.</summary>
    public string Loader => CachedManifest?.Server?.Loader ?? Server.Loader;

    /// <summary>Exact loader version required for this instance.</summary>
    public string LoaderVersion => CachedManifest?.Server?.LoaderVersion ?? Server.LoaderVersion;

    // ── Display ────────────────────────────────────────────────────────────

    /// <summary>
    /// Primary label shown in the sidebar item: the server name, falling back
    /// to the server UUID when no name is stored.
    /// </summary>
    public int DemoIndex { get; set; }
    public string DisplayName => DemoMode.ServerName(Server, DemoIndex);

    /// <summary>
    /// Secondary label: environment name, falling back to the fixed manager scope.
    /// </summary>
    public string HostLabel =>
        string.IsNullOrWhiteSpace(Server.EnvironmentName)
            ? Host.GetDisplayName()
            : Server.EnvironmentName;

    /// <summary>
    /// Shows the manager scope label only when it adds useful context - i.e.
    /// when the manager scope has more than one server so pure server names
    /// would be ambiguous.
    /// MainViewModel sets this after populating the Servers collection.
    /// </summary>
    public bool ShowHostLabel { get; set; }

    public Bitmap? ServerIconImage
    {
        get => _serverIconImage;
        private set
        {
            if (ReferenceEquals(_serverIconImage, value)) return;
            _serverIconImage = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasServerIconImage));
            OnPropertyChanged(nameof(HasServerIconFallback));
        }
    }

    public bool HasServerIconImage => ServerIconImage is not null;

    public bool HasServerIconFallback => !HasServerIconImage;

    public string ServerInitials => BuildServerInitials(DisplayName);

    public string PowerStatus => ServerPowerStates.Normalize(Server.PowerStatus);

    public string PowerStatusText => PowerStatus switch
    {
        ServerPowerStates.Online => "Online",
        ServerPowerStates.Restarting => "Restarting",
        ServerPowerStates.Offline => "Offline",
        _ => "Unknown",
    };

    public string MinecraftPillText =>
        string.IsNullOrWhiteSpace(MinecraftVersion) ? "MC Unknown" : $"MC {MinecraftVersion}";

    public string LoaderPillText
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Loader))
                return "Vanilla";

            return string.IsNullOrWhiteSpace(LoaderVersion)
                ? Loader
                : $"{Loader} {LoaderVersion}";
        }
    }

    public ISolidColorBrush PowerStatusForeground => PowerStatus switch
    {
        ServerPowerStates.Online => SuccessBrush,
        ServerPowerStates.Restarting => WarningBrush,
        ServerPowerStates.Offline => ErrorBrush,
        _ => MutedBrush,
    };

    public ISolidColorBrush PowerStatusBackground => PowerStatus switch
    {
        ServerPowerStates.Online => new SolidColorBrush(Color.FromArgb(0x1F, 0x10, 0xB9, 0x81)),
        ServerPowerStates.Restarting => new SolidColorBrush(Color.FromArgb(0x1F, 0xF5, 0x9E, 0x0B)),
        ServerPowerStates.Offline => new SolidColorBrush(Color.FromArgb(0x1F, 0xF8, 0x71, 0x71)),
        _ => new SolidColorBrush(Color.FromArgb(0x1F, 0x64, 0x74, 0x8B)),
    };

    // ── Online state ───────────────────────────────────────────────────────

    /// <summary>
    /// Whether the host was reachable on the most recent health check.
    /// <see cref="MainViewModel"/> updates this after each /health call.
    /// </summary>
    public bool IsOnline
    {
        get => _isOnline;
        set
        {
            if (_isOnline == value) return;
            _isOnline = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(OnlineTooltip));
            OnPropertyChanged(nameof(StatusDotBrush));
        }
    }

    /// <summary>
    /// True while <see cref="MainViewModel"/> is actively loading or syncing
    /// this server.  Drives the yellow "refreshing" state of the sidebar dot.
    /// Reset to false when the operation completes (success or failure).
    /// </summary>
    public bool IsRefreshing
    {
        get => _isRefreshing;
        set
        {
            if (_isRefreshing == value) return;
            _isRefreshing = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(OnlineTooltip));
            OnPropertyChanged(nameof(StatusDotBrush));
            OnPropertyChanged(nameof(LastSyncText));
        }
    }

    /// <summary>Tooltip shown on the online indicator dot.</summary>
    public string OnlineTooltip =>
        IsRefreshing ? "Refreshing…"
        : _runtimeBuildStatus == ServerRuntimeBuildStatus.Building ? "Building…"
        : _runtimeBuildStatus is ServerRuntimeBuildStatus.NotBuilt or ServerRuntimeBuildStatus.Unknown ? "Not Built"
        : _updateStatus == ServerUpdateStatus.UpdateFailed ? "Update failed"
        : PowerStatus == ServerPowerStates.Offline ? "Offline"
        : PowerStatus == ServerPowerStates.Restarting ? "Restarting"
        : IsOnline ? "Online"
        : "Offline";

    /// <summary>
    /// Brush for the sidebar status indicator dot.
    /// Yellow while refreshing or when updates are waiting,
    /// green when online and up to date, grey when offline.
    /// </summary>
    public ISolidColorBrush StatusDotBrush =>
        IsRefreshing ? WarningBrush
        : _runtimeBuildStatus == ServerRuntimeBuildStatus.Building ? WarningBrush
        : _runtimeBuildStatus is ServerRuntimeBuildStatus.NotBuilt or ServerRuntimeBuildStatus.Unknown ? MutedBrush
        : _updateStatus == ServerUpdateStatus.UpdateFailed ? ErrorBrush
        : PowerStatus == ServerPowerStates.Offline ? ErrorBrush
        : PowerStatus == ServerPowerStates.Restarting ? WarningBrush
        : _updateStatus == ServerUpdateStatus.UpdatesWaiting ? WarningBrush
        : IsOnline ? SuccessBrush
        : MutedBrush;

    // ── Last sync / update status ─────────────────────────────────────────

    /// <summary>
    /// Tracks whether the Built-In runtime for this server is available.
    /// Non-Built-In launchers remain <see cref="ServerRuntimeBuildStatus.Ready"/>.
    /// </summary>
    public ServerRuntimeBuildStatus RuntimeBuildStatus
    {
        get => _runtimeBuildStatus;
        set
        {
            if (_runtimeBuildStatus == value) return;
            _runtimeBuildStatus = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(LastSyncText));
            OnPropertyChanged(nameof(StatusDotBrush));
        }
    }

    /// <summary>
    /// UTC time of the most recent successful sync for this server.
    /// Null when no sync has been performed in the current session.
    /// </summary>
    public DateTime? LastSyncAt
    {
        get => _lastSyncAt;
        set
        {
            if (_lastSyncAt == value) return;
            _lastSyncAt = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(LastSyncText));
        }
    }

    /// <summary>
    /// Tracks the overall update status for this server after the most recent
    /// check or sync operation.  Drives <see cref="LastSyncText"/>.
    /// </summary>
    public ServerUpdateStatus UpdateStatus
    {
        get => _updateStatus;
        set
        {
            if (_updateStatus == value) return;
            _updateStatus = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(LastSyncText));
            OnPropertyChanged(nameof(StatusDotBrush));
        }
    }

    /// <summary>
    /// Human-readable update status shown in the sidebar and the main status card.
    /// </summary>
    public string LastSyncText
    {
        get
        {
            if (_isRefreshing) return "Refreshing…";
            if (_runtimeBuildStatus == ServerRuntimeBuildStatus.Building) return "Building…";
            if (_updateStatus == ServerUpdateStatus.UpdateFailed) return "Update Failed";
            if (_runtimeBuildStatus is ServerRuntimeBuildStatus.NotBuilt or ServerRuntimeBuildStatus.Unknown) return "Not Built";
            if (IsSecondary) return "Secondary";

            return _updateStatus switch
            {
                ServerUpdateStatus.Pending        => "Pending",
                ServerUpdateStatus.UpdatesWaiting => "Updates Available",
                ServerUpdateStatus.NoUpdates      => "Up to Date",
                ServerUpdateStatus.UpdateFailed   => "Update Failed",
                _                                 => "Pending",
            };
        }
    }

    // ── Per-server content cache ───────────────────────────────────────────

    /// <summary>
    /// Content items from the most recent full refresh for this server.
    /// Populated by <see cref="MainViewModel.LoadContentAsync"/> and
    /// displayed immediately when this server is selected (no extra network call).
    /// </summary>
    public List<ContentItemViewModel> CachedItems      { get; } = [];
    public int    CachedPendingActions { get; set; }
    public int    CachedFailedActions  { get; set; }
    public int    CachedTotalItems     { get; set; }
    public string CachedStatusText     { get; set; } = "";

    public string MotdStripText { get; set; } = "";
    public int MotdStripRemainingSeconds { get; set; }
    public bool IsMotdStripDismissed { get; set; } = true;

    /// <summary>
    /// The manifest returned by the API for this server during the last refresh.
    /// Restored into <see cref="MainViewModel._manifest"/> when the user switches
    /// to this server so that Sync Now uses the correct server's content list.
    /// </summary>
    public ManifestResponse? CachedManifest
    {
        get => _cachedManifest;
        set
        {
            if (ReferenceEquals(_cachedManifest, value)) return;
            _cachedManifest = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(MinecraftVersion));
            OnPropertyChanged(nameof(Loader));
            OnPropertyChanged(nameof(LoaderVersion));
            OnPropertyChanged(nameof(MinecraftPillText));
            OnPropertyChanged(nameof(LoaderPillText));
            OnPropertyChanged(nameof(RuntimeVersionComparison));
            OnPropertyChanged(nameof(HasRuntimeVersionChange));
            OnPropertyChanged(nameof(RuntimeVersionChangeKind));
            OnPropertyChanged(nameof(InstalledRuntimeDisplay));
            OnPropertyChanged(nameof(TargetRuntimeDisplay));
        }
    }

    public RuntimeVersionComparison RuntimeVersionComparison =>
        HalerithAddonClient.Core.RuntimeVersionComparison.Compare(
            RuntimeVersionInfo.From(Server),
            CachedManifest is { } manifest && ModpackInstallationRequirements.RuntimeTarget(manifest) is { } target ? RuntimeVersionInfo.From(target) : null);

    public bool HasRuntimeVersionChange => RuntimeVersionComparison.HasChange;

    public RuntimeVersionChangeKind RuntimeVersionChangeKind => RuntimeVersionComparison.ChangeKind;

    public string InstalledRuntimeDisplay => RuntimeVersionComparison.InstalledDisplay;

    public string TargetRuntimeDisplay =>
        HasRuntimeVersionChange ? RuntimeVersionComparison.TargetDisplay : "";

    public bool AlignRuntimeVersionFrom(ServerConfig source)
    {
        var changed = false;

        if (!string.Equals(Server.MinecraftVersion, source.MinecraftVersion, StringComparison.Ordinal))
        {
            Server.MinecraftVersion = source.MinecraftVersion;
            changed = true;
        }

        if (!string.Equals(Server.Loader, source.Loader, StringComparison.Ordinal))
        {
            Server.Loader = source.Loader;
            changed = true;
        }

        if (!string.Equals(Server.LoaderVersion, source.LoaderVersion, StringComparison.Ordinal))
        {
            Server.LoaderVersion = source.LoaderVersion;
            changed = true;
        }

        if (!changed)
            return false;

        OnPropertyChanged(nameof(MinecraftVersion));
        OnPropertyChanged(nameof(Loader));
        OnPropertyChanged(nameof(LoaderVersion));
        OnPropertyChanged(nameof(MinecraftPillText));
        OnPropertyChanged(nameof(LoaderPillText));
        OnPropertyChanged(nameof(RuntimeVersionComparison));
        OnPropertyChanged(nameof(HasRuntimeVersionChange));
        OnPropertyChanged(nameof(RuntimeVersionChangeKind));
        OnPropertyChanged(nameof(InstalledRuntimeDisplay));
        OnPropertyChanged(nameof(TargetRuntimeDisplay));
        return true;
    }

    /// <summary>
    /// The sync plan computed for this server during the last refresh.
    /// Restored into <see cref="MainViewModel._plan"/> when the user switches
    /// to this server so that Sync Now applies the correct actions.
    /// </summary>
    public SyncPlan? CachedPlan { get; set; }

    /// <summary>True once this server has been loaded at least once this session.</summary>
    public bool HasCachedData { get; set; }

    public void RefreshPowerStatus()
    {
        OnPropertyChanged(nameof(PowerStatus));
        OnPropertyChanged(nameof(PowerStatusText));
        OnPropertyChanged(nameof(PowerStatusForeground));
        OnPropertyChanged(nameof(PowerStatusBackground));
        OnPropertyChanged(nameof(StatusDotBrush));
        OnPropertyChanged(nameof(OnlineTooltip));
    }

    public void LoadServerIcon(string? path)
    {
        Server.ServerIconLocalPath = path ?? "";
        _ = LoadServerIconAsync(path, ++_iconGeneration);
    }

    private int _iconGeneration;

    private async Task LoadServerIconAsync(string? path, int generation)
    {
        var image = await Task.Run(() =>
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path)) return null;
                using var stream = File.OpenRead(path);
                return Bitmap.DecodeToWidth(stream, 64);
            }
            catch { return null; }
        });
        if (generation == _iconGeneration) ServerIconImage = image;
        else image?.Dispose();
    }

    private async void LoadCachedServerIcon()
    {
        var generation = ++_iconGeneration;
        try
        {
            var path = await Task.Run(() => ImageCacheService.ResolveCachedServerIconPath(
                HostId, ServerPublicId, Server.ServerIconSha256));
            if (generation == _iconGeneration) await LoadServerIconAsync(path, generation);
        }
        catch { /* A cache failure does not prevent the server from loading. */ }
    }

    private static string BuildServerInitials(string name)
    {
        var parts = name
            .Split([' ', '-', '_', '.', ':'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(p => p.Length > 0)
            .ToList();

        var initials = parts.Count switch
        {
            >= 3 => string.Concat(parts.Take(3).Select(p => char.ToUpperInvariant(p[0]))),
            2 => string.Concat(parts.Select(p => char.ToUpperInvariant(p[0]))),
            1 => new string(parts[0].Where(char.IsLetterOrDigit).Take(3).Select(char.ToUpperInvariant).ToArray()),
            _ => "SRV",
        };

        return string.IsNullOrWhiteSpace(initials) ? "SRV" : initials;
    }

    // ── INotifyPropertyChanged ─────────────────────────────────────────────

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>
/// Tracks the update status of a server after the most recent check or sync.
/// </summary>
public enum ServerUpdateStatus
{
    /// <summary>Server has not been checked yet this session.</summary>
    Pending,

    /// <summary>Server was checked and has pending updates to apply.</summary>
    UpdatesWaiting,

    /// <summary>Server is fully up to date — no updates needed.</summary>
    NoUpdates,

    /// <summary>The last update attempt failed.</summary>
    UpdateFailed,
}

/// <summary>
/// Build status of the server's Built-In runtime environment.
/// </summary>
public enum ServerRuntimeBuildStatus
{
    /// <summary>Not yet checked.</summary>
    Unknown,

    /// <summary>Built-In runtime has not been built.</summary>
    NotBuilt,

    /// <summary>Built-In runtime is currently being built.</summary>
    Building,

    /// <summary>Runtime is available, or the server uses an external launcher.</summary>
    Ready,
}
