using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Media;
using HalerithAddonClient.Core;
using HalerithAddonClient.Core.ContentSearch;

namespace HalerithAddonClient.ViewModels;

/// <summary>
/// Represents one local instance entry in the left-sidebar instance list.
/// Wraps a <see cref="LocalInstance"/> and tracks transient display state
/// (build status, content update status) that is updated by the main window.
///
/// Mirrors the pattern used by <see cref="ServerItemViewModel"/> so that
/// local instances behave consistently with remote servers in the sidebar.
/// </summary>
public sealed class LocalInstanceItemViewModel : INotifyPropertyChanged
{
    // ── Static palette (mirrors DarkTheme.axaml) ──────────────────────────
    private static readonly ISolidColorBrush MutedBrush   = new SolidColorBrush(Color.FromRgb(0x64, 0x74, 0x8B));
    private static readonly ISolidColorBrush SuccessBrush = new SolidColorBrush(Color.FromRgb(0x10, 0xB9, 0x81));
    private static readonly ISolidColorBrush WarningBrush = new SolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B));

    private LocalInstanceBuildStatus _buildStatus = LocalInstanceBuildStatus.Unknown;
    private bool _isRefreshing;
    private bool _isSelected;
    private LocalInstanceUpdateStatus _updateStatus = LocalInstanceUpdateStatus.Pending;
    private LocalInstance _instance;
    private int _iconGeneration;
    public Avalonia.Media.Imaging.Bitmap? InstanceIconImage { get; private set; }
    public bool HasInstanceIcon => InstanceIconImage is not null;
    public bool HasInstanceIconFallback => !HasInstanceIcon;

    public async Task LoadProjectIconAsync()
    {
        var generation = ++_iconGeneration;
        Avalonia.Media.Imaging.Bitmap? image = null;
        try
        {
            var pack = await Task.Run(() => Core.LocalModpacks.LocalModpackStore.Load(Instance.GetInstanceDirectory()));
            if (pack is not null)
            {
                var url = pack.IconUrl;
                if (string.IsNullOrWhiteSpace(url) && pack.ProjectId.Length > 0)
                    url = await Task.Run(() => AppStorageDatabase.LoadImageCacheRecord(ImageCacheService.BuildContentIconCacheKey(pack.Provider, pack.ProjectId, null, ContentTypes.Modpack, "")!)?.Url);
                image = await HalerithAddonClient.Services.ContentIconLoader.LoadAsync(pack.Provider, pack.ProjectId, null, ContentTypes.Modpack, url);
            }
        }
        catch { } // A missing or unavailable logo must not prevent opening the instance.
        if (generation != _iconGeneration) return;
        InstanceIconImage = image;
        OnPropertyChanged(nameof(InstanceIconImage));
        OnPropertyChanged(nameof(HasInstanceIcon));
        OnPropertyChanged(nameof(HasInstanceIconFallback));
    }

    // ── Construction ───────────────────────────────────────────────────────

    public LocalInstanceItemViewModel(LocalInstance instance)
    {
        _instance = instance;
    }

    // ── Identity ───────────────────────────────────────────────────────────

    public LocalInstance Instance => _instance;

    public string InstanceId => Instance.Id;

    public bool UpdateInstance(LocalInstance instance)
    {
        var runtimeChanged =
            !string.Equals(_instance.MinecraftVersion, instance.MinecraftVersion, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(_instance.Loader, instance.Loader, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(_instance.LoaderVersion, instance.LoaderVersion, StringComparison.OrdinalIgnoreCase);

        _instance = instance;
        OnPropertyChanged(nameof(Instance));
        OnPropertyChanged(nameof(InstanceId));
        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(VersionLabel));
        OnPropertyChanged(nameof(InstanceInitials));
        OnPropertyChanged(nameof(MinecraftPillText));
        OnPropertyChanged(nameof(LoaderPillText));

        if (runtimeChanged)
        {
            ClearCachedContent();
            BuildStatus = LocalInstanceBuildStatus.Unknown;
            UpdateStatus = LocalInstanceUpdateStatus.Pending;
        }

        return runtimeChanged;
    }

    public void ClearCachedContent()
    {
        CachedItems.Clear();
        CachedTotalItems = 0;
        CachedPendingActions = 0;
        CachedFailedActions = 0;
        CachedStatusText = "";
        CachedPlan = null;
        HasCachedData = false;
    }

    // ── Display ────────────────────────────────────────────────────────────

    /// <summary>Primary label: the user-chosen instance name.</summary>
    public string DisplayName =>
        string.IsNullOrWhiteSpace(Instance.Name) ? Instance.Id : Instance.Name;

    /// <summary>Secondary label: version summary (e.g. "1.21.4 — Fabric 0.16.9").</summary>
    public string VersionLabel => Instance.VersionSummary;

    public string InstanceInitials => BuildInitials(DisplayName);

    public string MinecraftPillText =>
        string.IsNullOrWhiteSpace(Instance.MinecraftVersion) ? "MC Unknown" : $"MC {Instance.MinecraftVersion}";

    public string LoaderPillText
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Instance.Loader))
                return "Vanilla";

            return string.IsNullOrWhiteSpace(Instance.LoaderVersion)
                ? Instance.Loader
                : $"{Instance.Loader} {Instance.LoaderVersion}";
        }
    }

    // ── Build status ───────────────────────────────────────────────────────

    /// <summary>
    /// Tracks whether the game environment for this instance is built and ready.
    /// </summary>
    public LocalInstanceBuildStatus BuildStatus
    {
        get => _buildStatus;
        set
        {
            if (_buildStatus == value) return;
            _buildStatus = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(StatusText));
            OnPropertyChanged(nameof(StatusDotBrush));
            OnPropertyChanged(nameof(IsLaunchable));
        }
    }

    /// <summary>True when the instance can be launched (environment is built).</summary>
    public bool IsLaunchable => _buildStatus == LocalInstanceBuildStatus.Ready;

    /// <summary>
    /// True when this instance is the active selection in the sidebar.
    /// Used for small UI affordances that should only appear on the selected row.
    /// </summary>
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            OnPropertyChanged();
        }
    }

    // ── Content update status (mirrors ServerItemViewModel) ───────────────

    /// <summary>
    /// True while a content refresh (update check) is in progress for this instance.
    /// </summary>
    public bool IsRefreshing
    {
        get => _isRefreshing;
        set
        {
            if (_isRefreshing == value) return;
            _isRefreshing = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(StatusText));
            OnPropertyChanged(nameof(StatusDotBrush));
        }
    }

    /// <summary>
    /// Content update status after the most recent check.
    /// </summary>
    public LocalInstanceUpdateStatus UpdateStatus
    {
        get => _updateStatus;
        set
        {
            if (_updateStatus == value) return;
            _updateStatus = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(StatusText));
            OnPropertyChanged(nameof(StatusDotBrush));
        }
    }

    /// <summary>Human-readable status shown in the sidebar under the instance name.</summary>
    public string StatusText
    {
        get
        {
            // Build status takes priority when not built
            if (_buildStatus is LocalInstanceBuildStatus.NotBuilt)
                return "Not Built";
            if (_buildStatus is LocalInstanceBuildStatus.Building)
                return "Building…";
            if (_buildStatus is LocalInstanceBuildStatus.Unknown)
                return "Checking…";

            // Once built, show content update status
            if (_isRefreshing) return "Refreshing…";

            return _updateStatus switch
            {
                LocalInstanceUpdateStatus.Pending        => "Pending",
                LocalInstanceUpdateStatus.UpdatesWaiting => "Updates Available",
                LocalInstanceUpdateStatus.NoUpdates      => "Up to Date",
                LocalInstanceUpdateStatus.UpdateFailed   => "Update Failed",
                _                                        => "Pending",
            };
        }
    }

    /// <summary>
    /// Brush for the sidebar status indicator dot.
    /// Yellow = refreshing/updates waiting/building, green = no updates/ready, grey = other.
    /// </summary>
    public ISolidColorBrush StatusDotBrush
    {
        get
        {
            if (_isRefreshing || _buildStatus == LocalInstanceBuildStatus.Building)
                return WarningBrush;

            if (_buildStatus is LocalInstanceBuildStatus.NotBuilt or LocalInstanceBuildStatus.Unknown)
                return MutedBrush;

            return _updateStatus switch
            {
                LocalInstanceUpdateStatus.UpdatesWaiting => WarningBrush,
                LocalInstanceUpdateStatus.NoUpdates      => SuccessBrush,
                LocalInstanceUpdateStatus.UpdateFailed   => MutedBrush,
                _                                        => MutedBrush,
            };
        }
    }

    // ── Per-instance content cache (mirrors ServerItemViewModel) ──────────

    /// <summary>Cached content items from the most recent refresh.</summary>
    public List<ContentItemViewModel> CachedItems { get; } = [];

    /// <summary>Cached total item count.</summary>
    public int CachedTotalItems { get; set; }

    /// <summary>Cached pending action count.</summary>
    public int CachedPendingActions { get; set; }

    /// <summary>Cached failed action count from the most recent update.</summary>
    public int CachedFailedActions { get; set; }

    /// <summary>Cached status text for the status bar.</summary>
    public string CachedStatusText { get; set; } = "";

    /// <summary>The content plan from the last refresh.</summary>
    public LocalContentPlan? CachedPlan { get; set; }

    /// <summary>True once this instance has been checked at least once this session.</summary>
    public bool HasCachedData { get; set; }

    // ── INotifyPropertyChanged ─────────────────────────────────────────────

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    private static string BuildInitials(string name)
    {
        var parts = name
            .Split([' ', '-', '_', '.', ':'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(part => part.Length > 0)
            .ToList();

        var initials = parts.Count switch
        {
            >= 3 => string.Concat(parts.Take(3).Select(part => char.ToUpperInvariant(part[0]))),
            2 => string.Concat(parts.Select(part => char.ToUpperInvariant(part[0]))),
            1 => new string(parts[0].Where(char.IsLetterOrDigit).Take(3).Select(char.ToUpperInvariant).ToArray()),
            _ => "LOC",
        };

        return string.IsNullOrWhiteSpace(initials) ? "LOC" : initials;
    }
}

/// <summary>
/// Build status of a local instance's game environment.
/// </summary>
public enum LocalInstanceBuildStatus
{
    /// <summary>Not yet checked.</summary>
    Unknown,

    /// <summary>Environment has not been built.</summary>
    NotBuilt,

    /// <summary>Build is in progress.</summary>
    Building,

    /// <summary>Environment is fully built and ready to launch.</summary>
    Ready,
}

/// <summary>
/// Content update status of a local instance after the most recent check.
/// </summary>
public enum LocalInstanceUpdateStatus
{
    /// <summary>Not yet checked.</summary>
    Pending,

    /// <summary>Updates are available for one or more items.</summary>
    UpdatesWaiting,

    /// <summary>All items are up to date.</summary>
    NoUpdates,

    /// <summary>The last update attempt failed.</summary>
    UpdateFailed,
}
