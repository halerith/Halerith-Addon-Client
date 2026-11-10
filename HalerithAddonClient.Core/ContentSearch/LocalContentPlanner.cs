namespace HalerithAddonClient.Core.ContentSearch;

using HalerithAddonClient.Core;
using HalerithAddonClient.Core.LocalModpacks;

/// <summary>
/// Checks locally-installed content items from SQLite
/// against Modrinth/CurseForge APIs to determine update status.
///
/// Produces <see cref="SyncAction"/>-compatible items that can be displayed in the
/// same DataGrid used for server content, enabling a consistent UI experience.
/// </summary>
public sealed partial class LocalContentPlanner
{
    public Func<LocalModpack, CancellationToken, Task<bool>>? ApplyPackAsync { get; init; }
    private readonly ModrinthSearchService _modrinth = new();
    private readonly CurseForgeSearchService _curseForge = new();

    /// <summary>
    /// Scans the instance folder and builds a sync plan by comparing installed
    /// manifest items against the latest versions on their source platforms.
    /// Also detects untracked files not in the manifest.
    /// </summary>
    public async Task<LocalContentPlan> BuildPlanAsync(
        string localInstanceId,
        string instanceDirectory,
        string mcVersion,
        string loader,
        Action<string>? log = null,
        CancellationToken ct = default)
    {
        var plan = new LocalContentPlan();
        var manifest = LocalInstanceContentStore.Load(localInstanceId);
        var pack = LocalModpackStore.Load(instanceDirectory);

        log?.Invoke($"Loaded local content inventory: {manifest.Items.Count} tracked items");

        // Prune inventory entries whose files no longer exist on disk.
        // This keeps SQLite in sync with reality (e.g. user manually
        // deleted a mod, or the file was removed outside the client).
        var removed = manifest.Items.RemoveAll(item =>
        {
            // Keep CurseForge inventory visible so a missing file can be recovered after a key is restored.
            if (item.Source.Equals("curseforge", StringComparison.OrdinalIgnoreCase))
                return false;
            var subfolder = ContentDownloader.GetSubfolder(item.ContentType);
            var filePath = Path.Combine(instanceDirectory, subfolder, item.InstalledVersion.Filename);
            if (!File.Exists(filePath))
            {
                log?.Invoke($"Removing stale inventory entry: {item.Name} (file missing)");
                return true;
            }
            return false;
        });

        if (removed > 0)
        {
            LocalInstanceContentStore.Save(localInstanceId, instanceDirectory, manifest);
            log?.Invoke($"Pruned {removed} stale entry/entries from inventory");
        }

        // Track filenames from manifest to detect untracked files later
        var trackedFilenames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (pack is not null) await AddPackRows(plan, pack, instanceDirectory, trackedFilenames, ct);

        foreach (var item in manifest.Items)
        {
            ct.ThrowIfCancellationRequested();
            if (pack?.Members.Any(m => !m.Overridden && m.Project.Id == item.SourceId && m.Project.Source == item.Source) == true) continue;

            trackedFilenames.Add(item.InstalledVersion.Filename);
            var installedFileExists = File.Exists(Path.Combine(instanceDirectory,
                ContentDownloader.GetSubfolder(item.ContentType), item.InstalledVersion.Filename));

            try
            {
                var service = GetService(item.Source);
                var curseForgeItem = item.Source.Equals("curseforge", StringComparison.OrdinalIgnoreCase);
                var installedPath = Path.Combine(instanceDirectory,
                    ContentDownloader.GetSubfolder(item.ContentType), item.InstalledVersion.Filename);
                if (curseForgeItem && installedFileExists && !string.IsNullOrWhiteSpace(item.InstalledVersion.FileHash))
                {
                    try { await SafeFileHelpers.VerifyExpectedHashAsync(installedPath, item.InstalledVersion.FileHash, ct); }
                    catch (InvalidOperationException) { installedFileExists = false; }
                }
                if (service is null || !service.IsAvailable)
                {
                    if (curseForgeItem)
                    {
                        plan.Items.Add(new LocalContentPlanItem
                        {
                            Kind = installedFileExists ? SyncActionKind.UpToDate : SyncActionKind.Missing,
                            Status = "Provider unavailable",
                            ContentType = item.ContentType,
                            Name = item.Name,
                            InstalledVersion = installedFileExists ? item.InstalledVersion.VersionName : "",
                            LatestVersion = item.InstalledVersion.VersionName,
                            ManifestItem = item,
                        });
                    }
                    else
                        plan.Items.Add(CreateUpToDateItem(item));
                    continue;
                }

                // Only apply loader filter for mods
                var loaderFilter = item.ContentType == ContentTypes.Mod ? loader : null;

                log?.Invoke($"Checking {item.Name}…");

                var versions = await service.GetVersionsAsync(
                    item.SourceId, mcVersion, loaderFilter, ct);

                if (versions.Count == 0)
                {
                    // Retain a missing CurseForge item if the provider cannot resolve it yet.
                    if (curseForgeItem && !installedFileExists)
                        plan.Items.Add(CreateMissingItem(item));
                    else
                        plan.Items.Add(CreateUpToDateItem(item));
                    continue;
                }

                var latest = versions[0]; // newest first

                if (latest.SourceVersionId == item.InstalledVersion.SourceVersionId)
                {
                    if (curseForgeItem && !installedFileExists)
                        plan.Items.Add(new LocalContentPlanItem
                        {
                            Kind = SyncActionKind.Download,
                            ContentType = item.ContentType,
                            Name = item.Name,
                            InstalledVersion = "",
                            LatestVersion = latest.VersionName,
                            LatestVersionResult = latest,
                            ManifestItem = item,
                        });
                    else
                        plan.Items.Add(CreateUpToDateItem(item));
                }
                else
                {
                    // Newer version available
                    plan.Items.Add(new LocalContentPlanItem
                    {
                        Kind = SyncActionKind.Replace,
                        ContentType = item.ContentType,
                        Name = item.Name,
                        InstalledVersion = item.InstalledVersion.VersionName,
                        LatestVersion = latest.VersionName,
                        LatestVersionResult = latest,
                        ManifestItem = item,
                    });
                }
            }
            catch (Exception ex)
            {
                log?.Invoke($"  Error checking {item.Name}: {ex.Message}");
                if (item.Source.Equals("curseforge", StringComparison.OrdinalIgnoreCase) && !installedFileExists)
                    plan.Items.Add(CreateMissingItem(item));
                else
                    plan.Items.Add(CreateUpToDateItem(item));
            }
        }

        // Scan for untracked files
        if (pack is not null)
            foreach (var row in plan.Items.Where(i => i.ManifestItem is not null && i.PackMember is null))
                row.PackMember = pack.Members.FirstOrDefault(m => m.Overridden && m.Project.Id == row.ManifestItem!.SourceId && m.Project.Source == row.ManifestItem.Source);
        ScanForUntrackedFiles(instanceDirectory, "mods", ContentTypes.Mod, trackedFilenames, plan);
        ScanForUntrackedFiles(instanceDirectory, "resourcepacks", ContentTypes.ResourcePack, trackedFilenames, plan);
        ScanForUntrackedFiles(instanceDirectory, "shaderpacks", ContentTypes.Shader, trackedFilenames, plan);

        log?.Invoke($"Plan complete: {plan.TotalItems} items, {plan.PendingActions} pending");

        return plan;
    }

    /// <summary>
    /// Applies pending updates by downloading the latest versions and updating the manifest.
    /// </summary>
    /// <param name="progress">
    /// Optional callback invoked after each item with (completed, total) counts
    /// so the caller can drive a progress bar.
    /// </param>
    public async Task<LocalSyncResult> ApplyPlanAsync(
        string localInstanceId,
        string instanceDirectory,
        LocalContentPlan plan,
        Action<string>? log = null,
        Action<int, int>? progress = null,
        CancellationToken ct = default)
    {
        var result = new LocalSyncResult();
        int totalWork = plan.Items.Count(i => i.Kind is SyncActionKind.Replace or SyncActionKind.Download);
        int completed = 0;

        foreach (var item in plan.Items)
        {
            ct.ThrowIfCancellationRequested();
            if (item.Pack is { } pack)
            {
                if (item.Kind is SyncActionKind.Replace or SyncActionKind.Download)
                {
                    if (ApplyPackAsync is null || !await ApplyPackAsync(pack, ct)) result.Failed++;
                    else result.Updated++;
                    // A release can change the runtime and reset overrides. Rebuild the plan
                    // before applying any ordinary updates selected against the old runtime.
                    return result;
                }
                continue;
            }
            if (item.PackMember is { Overridden: false } || item.IsBundledFile) continue;
            if (item.PackMember is { Overridden: true } member && item.Kind == SyncActionKind.Replace && item.LatestVersionResult is not null)
            {
                try { await new LocalPackInstaller(log).ChangeMemberAsync(localInstanceId, instanceDirectory, member.Path, item.LatestVersionResult, true, ct); result.Updated++; }
                catch (Exception ex) when (ex is not OperationCanceledException) { log?.Invoke(ex.Message); result.Failed++; }
                continue;
            }

            if (item.Kind == SyncActionKind.UpToDate || item.Kind == SyncActionKind.Ignore)
            {
                result.UpToDate++;
                continue;
            }

            if (item.Kind == SyncActionKind.Replace && item.LatestVersionResult is not null && item.ManifestItem is not null)
            {
                try
                {
                    log?.Invoke($"Updating {item.Name}…");

                    var subfolder = ContentDownloader.GetSubfolder(item.ContentType);
                    var oldPath = Path.Combine(instanceDirectory, subfolder, item.ManifestItem.InstalledVersion.Filename);

                    // Download and verify the new version before removing the old file.
                    var searchResult = new ContentSearchResult
                    {
                        Id = item.ManifestItem.SourceId,
                        Source = item.ManifestItem.Source,
                        Name = item.ManifestItem.Name,
                        Slug = item.ManifestItem.Slug,
                        ContentType = item.ManifestItem.ContentType,
                        IconUrl = item.ManifestItem.IconUrl,
                    };

                    await ContentDownloader.DownloadAndTrackAsync(
                        localInstanceId,
                        instanceDirectory,
                        searchResult,
                        item.LatestVersionResult,
                        oldPath,
                        ct);

                    log?.Invoke($"  Updated {item.Name} — {item.InstalledVersion} → {item.LatestVersionResult.VersionName}");
                    result.Updated++;
                    completed++;
                    progress?.Invoke(completed, totalWork);
                }
                catch (Exception ex)
                {
                    log?.Invoke($"  Failed to update {item.Name}: {ex.Message}");
                    result.Failed++;
                    completed++;
                    progress?.Invoke(completed, totalWork);
                }
            }
            else if (item.Kind == SyncActionKind.Remove)
            {
                // Untracked files — leave them alone, just count
                result.Untracked++;
            }
        }

        return result;
    }

    private IContentSearchService? GetService(string source) => source.ToLowerInvariant() switch
    {
        "modrinth" => _modrinth,
        "curseforge" => _curseForge,
        _ => null
    };

    private static LocalContentPlanItem CreateUpToDateItem(ContentManifestItem item) => new()
    {
        Kind = SyncActionKind.UpToDate,
        ContentType = item.ContentType,
        Name = item.Name,
        InstalledVersion = item.InstalledVersion.VersionName,
        LatestVersion = item.InstalledVersion.VersionName,
        ManifestItem = item,
    };

    private static LocalContentPlanItem CreateMissingItem(ContentManifestItem item) => new()
    {
        Kind = SyncActionKind.Missing,
        ContentType = item.ContentType,
        Name = item.Name,
        InstalledVersion = "",
        LatestVersion = item.InstalledVersion.VersionName,
        ManifestItem = item,
    };

    private static void ScanForUntrackedFiles(
        string instanceDirectory,
        string subfolder,
        string contentType,
        HashSet<string> trackedFilenames,
        LocalContentPlan plan)
    {
        var dir = Path.Combine(instanceDirectory, subfolder);
        if (!Directory.Exists(dir)) return;

        var pattern = contentType == ContentTypes.Mod ? "*.jar" : "*.*";
        foreach (var file in Directory.EnumerateFiles(dir, pattern))
        {
            var fileName = Path.GetFileName(file);
            if (!trackedFilenames.Contains(fileName))
            {
                plan.Items.Add(new LocalContentPlanItem
                {
                    Kind = SyncActionKind.Remove, // "Untracked" display
                    ContentType = contentType,
                    Name = fileName,
                    InstalledVersion = "",
                    LatestVersion = "",
                    UntrackedFilePath = file,
                });
            }
        }
    }
}

/// <summary>
/// Result of scanning an instance's content for updates.
/// </summary>
public sealed class LocalContentPlan
{
    public List<LocalContentPlanItem> Items { get; set; } = [];

    public int TotalItems => Items.Count;
    public int PendingActions => Items.Count(i => (i.PackMember is null || i.PackMember.Overridden) && i.Kind is (SyncActionKind.Replace or SyncActionKind.Download));
    public bool HasUnavailableProvider => Items.Any(i => i.Status == "Provider unavailable");
}

/// <summary>
/// A single item in the local content plan — analogous to <see cref="SyncAction"/>
/// but for locally-managed content.
/// </summary>
public sealed class LocalContentPlanItem
{
    public LocalModpack? Pack { get; set; }
    public LocalPackMember? PackMember { get; set; }
    public bool IsBundledFile { get; set; }
    public string? Status { get; set; }
    public SyncActionKind Kind { get; set; }
    public string ContentType { get; set; } = ContentTypes.Mod;
    public string Name { get; set; } = "";
    public string InstalledVersion { get; set; } = "";
    public string LatestVersion { get; set; } = "";

    /// <summary>Set when Kind is Replace — the newer version to download.</summary>
    public ContentVersionResult? LatestVersionResult { get; set; }

    /// <summary>The manifest item this plan item refers to (null for untracked).</summary>
    public ContentManifestItem? ManifestItem { get; set; }

    /// <summary>Full path for untracked files.</summary>
    public string? UntrackedFilePath { get; set; }
}

/// <summary>
/// Result of applying a local content plan.
/// </summary>
public sealed class LocalSyncResult
{
    public int Updated { get; set; }
    public int UpToDate { get; set; }
    public int Failed { get; set; }
    public int Untracked { get; set; }

    public string Status => Failed == 0 ? "success" : Updated > 0 ? "partial" : "failed";
}
