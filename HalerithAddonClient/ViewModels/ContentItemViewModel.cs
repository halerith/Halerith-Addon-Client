using System.ComponentModel;
using Avalonia.Media.Imaging;
using Avalonia.Media;
using Avalonia.Threading;
using HalerithAddonClient.Core;
using HalerithAddonClient.Core.ContentSearch;

namespace HalerithAddonClient.ViewModels;

/// <summary>
/// Wraps a <see cref="SyncAction"/> or <see cref="LocalContentPlanItem"/> for display
/// in the content DataGrid.  Maps the action kind to a human-readable status, coloured
/// badge, installed version string, and action label.  All properties are read-only — the item
/// is rebuilt whenever the sync plan is rebuilt.
/// </summary>
public sealed class ContentItemViewModel : INotifyPropertyChanged
{
    // ── Static palette (mirrors DarkTheme.xaml) ────────────────────────────

    private static readonly ISolidColorBrush SuccessBrush  = new SolidColorBrush(Color.FromRgb(0x10, 0xB9, 0x81)); // #10B981
    private static readonly ISolidColorBrush WarningBrush  = new SolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B)); // #F59E0B
    private static readonly ISolidColorBrush DangerBrush   = new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44)); // #EF4444
    private static readonly ISolidColorBrush DarkDangerBrush = new SolidColorBrush(Color.FromRgb(0x99, 0x1B, 0x1B)); // #991B1B
    private static readonly ISolidColorBrush MutedBrush    = new SolidColorBrush(Color.FromRgb(0x64, 0x74, 0x8B)); // #64748B
    private static readonly ISolidColorBrush OrangeBrush   = new SolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B)); // #F59E0B
    private static readonly ISolidColorBrush WhiteBrush    = new SolidColorBrush(Color.FromRgb(0xF8, 0xFA, 0xFC)); // TextPrimary
    private static readonly ISolidColorBrush DarkTextBrush = new SolidColorBrush(Color.FromRgb(0x0F, 0x11, 0x17)); // Background

    // ── Construction (server sync action) ─────────────────────────────────

    public ContentItemViewModel(SyncAction action)
    {
        Action = action;

        // ── Name ──────────────────────────────────────────────────────────
        Name = action.Modpack is not null ? (string.IsNullOrWhiteSpace(action.Modpack.Name) ? action.Modpack.VersionName : action.Modpack.Name) : action.ManifestMod?.Name
            ?? action.PackManifestItem?.Name
            ?? action.LocalFile?.FileName
            ?? "";
        ProjectUrl = action.Modpack is not null ? BuildProjectUrl(action.Modpack.Provider, "modpack", action.Modpack.ProjectId) : BuildProjectUrl(
            action.ManifestMod?.VersionSource ?? action.PackManifestItem?.VersionSource,
            action.ContentType,
            action.ManifestMod?.Slug ?? action.PackManifestItem?.Slug);
        if (action.Modpack is null) ResolveIcon(
            action.ManifestMod?.VersionSource ?? action.PackManifestItem?.VersionSource,
            action.ManifestMod?.SourceId ?? action.PackManifestItem?.SourceId,
            action.ManifestMod?.Slug ?? action.PackManifestItem?.Slug,
            action.ContentType,
            action.ManifestMod?.IconUrl ?? action.PackManifestItem?.IconUrl);

        // ── Content type ──────────────────────────────────────────────────
        ContentTypeDisplay = ContentTypes.ToDisplay(action.ContentType);

        Version = action.InstalledVersion;
        var manual = action.Kind == SyncActionKind.Missing && ServerPackManualImport.IsEligible(action);
        TargetVersionTooltip = manual && !string.IsNullOrWhiteSpace(action.TargetVersion)
            ? "Manager requires " + action.TargetVersion
            : action.Kind is SyncActionKind.Download or SyncActionKind.Replace
            ? action.TargetVersion
            : action.Kind == SyncActionKind.Missing ? action.Reason ?? "" : "";

        // ── Status badge ──────────────────────────────────────────────────
        (StatusText, StatusBadgeBackground, StatusBadgeForeground) = action.Kind switch
        {
            SyncActionKind.Missing when manual => ("Manual", new SolidColorBrush(Color.FromRgb(0x25, 0x63, 0xEB)), WhiteBrush),
            SyncActionKind.Missing  => ("Missing",    WarningBrush, DarkTextBrush),
            SyncActionKind.UpToDate => ("Current",    SuccessBrush, WhiteBrush),
            SyncActionKind.Replace  => ("Update",     WarningBrush, DarkTextBrush),
            SyncActionKind.Download => ("Download",   WarningBrush, DarkTextBrush),
            SyncActionKind.Remove   => ("Remove",     DarkDangerBrush, WhiteBrush),
            SyncActionKind.Ignore   => ("Ignored",    MutedBrush,   WhiteBrush),
            _                       => ("Unknown",    MutedBrush,   WhiteBrush),
        };

        // ── Action column ─────────────────────────────────────────────────
        ActionText = action.Kind switch
        {
            SyncActionKind.UpToDate => "—",
            SyncActionKind.Download => "Download",
            SyncActionKind.Replace  => "Update",
            SyncActionKind.Remove   => "Remove",
            SyncActionKind.Ignore   => "Ignore",
            _                       => "",
        };
        ActionForeground = action.Kind == SyncActionKind.Replace &&
                           action.MatchConfidence == SyncMatchConfidence.Medium
            ? OrangeBrush
            : WhiteBrush;
        if (action.Modpack is not null)
        {
            StatusText = action.ModpackStatus ?? StatusText;
            (StatusBadgeBackground, StatusBadgeForeground) = StatusText switch
            {
                "Current" => (SuccessBrush, WhiteBrush),
                "Failed" => (DangerBrush, WhiteBrush),
                "Ignored" => (MutedBrush, WhiteBrush),
                _ => (WarningBrush, DarkTextBrush)
            };
            Version = string.IsNullOrWhiteSpace(action.InstalledVersion) ? "Not applied" : action.InstalledVersion;
            if (action.TargetVersion != action.InstalledVersion) Version += " → " + action.TargetVersion;
            TargetVersionTooltip = action.Reason ?? "";
        }
        if (action.BundledFile is { } bundled)
        {
            StatusText = bundled.Exists ? "Current" : "Pending";
            StatusBadgeBackground = bundled.Exists ? SuccessBrush : MutedBrush;
            TargetVersionTooltip = action.Reason ?? "";
            ActionText = "—";
        }
    }

    // ── Construction (local content plan item) ────────────────────────────

    /// <summary>
    /// Creates a view-model row from a <see cref="LocalContentPlanItem"/> so that
    /// local instances share the same DataGrid columns as server content.
    /// </summary>
    public ContentItemViewModel(LocalContentPlanItem planItem)
    {
        IsLocal = true;
        LocalPlanItem = planItem;

        Name = planItem.Name;
        ProjectUrl = BuildProjectUrl(
            planItem.ManifestItem?.Source,
            planItem.ContentType,
            planItem.ManifestItem?.Slug);
        ResolveIcon(
            planItem.ManifestItem?.Source,
            planItem.ManifestItem?.SourceId,
            planItem.ManifestItem?.Slug,
            planItem.ContentType,
            planItem.ManifestItem?.IconUrl);
        ContentTypeDisplay = ContentTypes.ToDisplay(planItem.ContentType);
        Version = planItem.InstalledVersion;
        TargetVersionTooltip = planItem.Kind is SyncActionKind.Download or SyncActionKind.Replace
            ? planItem.LatestVersion
            : "";

        (StatusText, StatusBadgeBackground, StatusBadgeForeground) = planItem.Kind switch
        {
            SyncActionKind.UpToDate => ("Current",    SuccessBrush, WhiteBrush),
            SyncActionKind.Replace  => ("Update",     WarningBrush, DarkTextBrush),
            SyncActionKind.Download => ("Download",   WarningBrush, DarkTextBrush),
            SyncActionKind.Remove   => ("Remove",     DarkDangerBrush, WhiteBrush),
            SyncActionKind.Ignore   => ("Ignored",    MutedBrush,   WhiteBrush),
            _                       => ("Unknown",    MutedBrush,   WhiteBrush),
        };

        ActionText = planItem.Kind switch
        {
            SyncActionKind.UpToDate => "—",
            SyncActionKind.Download => "Download",
            SyncActionKind.Replace  => "Update",
            SyncActionKind.Remove   => "Remove",
            SyncActionKind.Ignore   => "Ignore",
            _                       => "",
        };
        ActionForeground = WhiteBrush;

        HasUpdate = planItem.Kind == SyncActionKind.Replace;
        if (planItem.Status is { } status) StatusText = status;
        if (planItem.Kind == SyncActionKind.Missing) StatusText = "Missing";
        if (StatusText == "Failed") StatusBadgeBackground = DangerBrush;
        if (StatusText == "Missing") StatusBadgeBackground = WarningBrush;
        if (StatusText == "Provider unavailable")
        {
            StatusBadgeBackground = WarningBrush;
            StatusBadgeForeground = DarkTextBrush;
        }
        if (planItem.Pack is not null)
        {
            HasUpdate = planItem.LatestVersionResult is not null;
            ProjectUrl = BuildProjectUrl(planItem.Pack.Provider, ContentTypes.Modpack, planItem.Pack.ProjectId);
            TargetVersionTooltip = planItem.Pack.Members.Any(m => m.Enabled && m.Missing)
                ? "Some pack files are unavailable. You can still build and launch, but Minecraft may report missing dependencies. Reapply to retry."
                : "Manage the pack release, bundled files and individual content overrides from this row.";
        }
    }

    // ── Construction (cached SQLite inventory row) ───────────────────────

    /// <summary>
    /// Creates a pending row from locally persisted content inventory. These
    /// rows represent what is installed locally before a remote refresh layers
    /// current manifest status over the top.
    /// </summary>
    public ContentItemViewModel(ContentInventoryItem inventory, bool isLocal)
    {
        IsLocal = isLocal;
        LocalPlanItem = isLocal ? BuildLocalPlanItem(inventory) : null;

        Name = !string.IsNullOrWhiteSpace(inventory.Name)
            ? inventory.Name
            : Path.GetFileNameWithoutExtension(inventory.LocalPath);
        ProjectUrl = BuildProjectUrl(inventory.Source, inventory.ContentType, inventory.Slug);
        ResolveIcon(
            inventory.Source,
            inventory.SourceId,
            inventory.Slug,
            inventory.ContentType,
            inventory.IconUrl);
        ContentTypeDisplay = ContentTypes.ToDisplay(inventory.ContentType);
        Version = inventory.VersionName;
        TargetVersionTooltip = "";

        StatusText = "Pending";
        StatusBadgeBackground = MutedBrush;
        StatusBadgeForeground = WhiteBrush;
        ActionText = "—";
        ActionForeground = WhiteBrush;
        HasUpdate = false;
    }

    // ── Public properties ──────────────────────────────────────────────────

    /// <summary>The underlying sync action this row represents (null for local content).</summary>
    public SyncAction? Action { get; }

    /// <summary>Display name (from manifest item or local filename).</summary>
    public string Name { get; }

    /// <summary>Project page URL for this content item, when it can be derived safely.</summary>
    public string? ProjectUrl { get; }

    /// <summary>True when the row can open a project page in the browser.</summary>
    public bool HasProjectUrl => !string.IsNullOrWhiteSpace(ProjectUrl);

    public Bitmap? IconImage { get; private set; }

    public bool HasIconImage => IconImage is not null;

    /// <summary>Human-readable content type, e.g. "Mod", "Resource Pack".</summary>
    public string ContentTypeDisplay { get; }

    /// <summary>
    /// Status badge label: "Current", "Update", "Download", "Remove", or "Ignored".
    /// </summary>
    public string StatusText { get; }

    public string StatusSortKey => $"{GetStatusSortRank(StatusText):D3}:{StatusText}";

    /// <summary>Version string shown in the Installed Version column.  May be empty.</summary>
    public string Version { get; }

    /// <summary>Version that will be downloaded when the action is Download or Update.</summary>
    public string TargetVersionTooltip { get; }

    /// <summary>Tooltip value for controls that should stay quiet when no target version exists.</summary>
    public string? TargetVersionTip => string.IsNullOrWhiteSpace(TargetVersionTooltip) ? null : TargetVersionTooltip;

    /// <summary>Action label shown in the Action column: "Download", "Update", "Remove", etc.</summary>
    public string ActionText { get; }

    public ISolidColorBrush ActionForeground { get; }

    /// <summary>Background brush for the rounded status badge.</summary>
    public ISolidColorBrush StatusBadgeBackground { get; }

    /// <summary>Foreground brush for the text inside the status badge.</summary>
    public ISolidColorBrush StatusBadgeForeground { get; }

    // ── Local-instance-specific ───────────────────────────────────────────

    /// <summary>True when this row represents a local content item (not a server sync action).</summary>
    public bool IsLocal { get; }
    public bool IsModpack => Action?.Modpack is not null || LocalPlanItem?.Pack is not null;
    public bool HasLocalPackControls => IsLocal && (IsModpack || LocalPlanItem?.PackMember is not null || LocalPlanItem?.IsBundledFile == true);
    public bool HasServerMissingPackControls => !IsLocal && Action is { Kind: SyncActionKind.Missing }
        && ServerPackManualImport.IsEligible(Action);
    public bool CanDeleteLocalItem => IsLocal && !HasLocalPackControls;
    public bool IsLocalPackOverride => LocalPlanItem?.PackMember?.Overridden == true;
    public bool CanReviewModpack => Action?.CanReviewModpack == true;
    public IBrush RowBackground => IsModpack ? new SolidColorBrush(Color.FromArgb(32, 235, 193, 65))
        : Action?.BundledFile is not null || LocalPlanItem?.PackMember is not null || LocalPlanItem?.IsBundledFile == true ? new SolidColorBrush(Color.FromArgb(16, 235, 193, 65)) : Brushes.Transparent;

    /// <summary>True when a newer version is available for this local item.</summary>
    public bool HasUpdate { get; }

    /// <summary>The underlying plan item (non-null only for local content).</summary>
    public LocalContentPlanItem? LocalPlanItem { get; }

    public event PropertyChangedEventHandler? PropertyChanged;

    public static IEnumerable<ContentItemViewModel> OrderForDisplay(IEnumerable<ContentItemViewModel> items)
        => items.OrderBy(i => i.IsModpack ? 0 : 1).ThenBy(i => GetStatusSortRank(i.StatusText))
                .ThenBy(i => i.StatusText, StringComparer.OrdinalIgnoreCase)
                .ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase);

    public static IEnumerable<ContentItemViewModel> OrderForColumn(IEnumerable<ContentItemViewModel> items, string column, bool descending)
    {
        var rows = items.ToList();
        string Key(ContentItemViewModel item) => column switch
        {
            "ContentTypeDisplay" => item.ContentTypeDisplay, "StatusSortKey" => item.StatusSortKey,
            "Version" => item.Version, _ => item.Name
        };
        var ordinary = rows.Where(i => !i.IsModpack);
        var sorted = descending ? ordinary.OrderByDescending(Key, StringComparer.OrdinalIgnoreCase) : ordinary.OrderBy(Key, StringComparer.OrdinalIgnoreCase);
        return rows.Where(i => i.IsModpack).Concat(sorted);
    }

    private static int GetStatusSortRank(string statusText)
        => statusText.Trim() switch
        {
            "Failed" => -1,
            "Download" => 0,
            "Update" => 1,
            "Reapply" => 2,
            "Remove" => 3,
            "Ignored" => 4,
            "Pending" => 5,
            "Unknown" => 6,
            "Current" => 100,
            _ => 50,
        };

    private void ResolveIcon(string? source, string? sourceId, string? slug, string? contentType, string? iconUrl)
    {
        _ = ResolveIconAsync(source, sourceId, slug, contentType, iconUrl);
    }

    private async Task ResolveIconAsync(string? source, string? sourceId, string? slug, string? contentType, string? iconUrl)
    {
        var image = await Services.ContentIconLoader.LoadAsync(source, sourceId, slug, contentType, iconUrl).ConfigureAwait(false);
        if (image is null) return;
        Dispatcher.UIThread.Post(() =>
        {
            IconImage = image;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IconImage)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasIconImage)));
        });
    }

    private static LocalContentPlanItem BuildLocalPlanItem(ContentInventoryItem inventory)
    {
        var name = !string.IsNullOrWhiteSpace(inventory.Name)
            ? inventory.Name
            : Path.GetFileNameWithoutExtension(inventory.LocalPath);

        var planItem = new LocalContentPlanItem
        {
            Kind = SyncActionKind.UpToDate,
            ContentType = inventory.ContentType,
            Name = name,
            InstalledVersion = inventory.VersionName,
            LatestVersion = inventory.VersionName,
        };

        if (string.IsNullOrWhiteSpace(inventory.Source) ||
            string.IsNullOrWhiteSpace(inventory.SourceId))
        {
            planItem.UntrackedFilePath = inventory.LocalPath;
            return planItem;
        }

        planItem.ManifestItem = new ContentManifestItem
        {
            Source = inventory.Source,
            SourceId = inventory.SourceId,
            Slug = inventory.Slug,
            Name = name,
            ContentType = inventory.ContentType,
            IconUrl = inventory.IconUrl,
            InstalledVersion = new ContentManifestVersion
            {
                VersionName = inventory.VersionName,
                Filename = inventory.DownloadFilename,
                FileHash = inventory.FileHash,
                InstalledAt = inventory.InstalledAtUtc,
            },
        };

        return planItem;
    }

    private static string? BuildProjectUrl(string? source, string contentType, string? slug)
    {
        if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(slug))
            return null;

        var escapedSlug = Uri.EscapeDataString(slug.Trim());

        return source.Trim().ToLowerInvariant() switch
        {
            "modrinth" => contentType switch
            {
                ContentTypes.ResourcePack => $"https://modrinth.com/resourcepack/{escapedSlug}",
                ContentTypes.Shader => $"https://modrinth.com/shader/{escapedSlug}",
                ContentTypes.Modpack => $"https://modrinth.com/modpack/{escapedSlug}",
                _ => $"https://modrinth.com/mod/{escapedSlug}",
            },
            "curseforge" => contentType switch
            {
                ContentTypes.ResourcePack => $"https://www.curseforge.com/minecraft/texture-packs/{escapedSlug}",
                ContentTypes.Shader => $"https://www.curseforge.com/minecraft/shaders/{escapedSlug}",
                ContentTypes.Modpack => $"https://www.curseforge.com/minecraft/modpacks/{escapedSlug}",
                _ => $"https://www.curseforge.com/minecraft/mc-mods/{escapedSlug}",
            },
            _ => null,
        };
    }
}
