using HalerithAddonClient.Core.ContentSearch;

namespace HalerithAddonClient.Core;

/// <summary>
/// Local content inventory row persisted to SQLite for either a server instance
/// or a local instance. This represents files that exist locally, not remote
/// manifest-only desired state.
/// </summary>
public sealed class ContentInventoryItem
{
    public string PackId { get; set; } = "";
    public string PackRevision { get; set; } = "";
    public string PackFileHash { get; set; } = "";
    public string ContentType { get; set; } = ContentTypes.Mod;
    public string Source { get; set; } = "";
    public string SourceId { get; set; } = "";
    public string Slug { get; set; } = "";
    public string Name { get; set; } = "";
    public string? IconUrl { get; set; }
    public string SourceVersionId { get; set; } = "";
    public string VersionName { get; set; } = "";
    public string VersionNumber { get; set; } = "";
    public string DownloadFilename { get; set; } = "";
    public string FileHash { get; set; } = "";
    public string McVersion { get; set; } = "";
    public string Loader { get; set; } = "";
    public string DownloadUrl { get; set; } = "";
    public string LocalPath { get; set; } = "";
    public string InstalledAtUtc { get; set; } = "";
}

public static class ContentInventoryBuilder
{
    public static List<ContentInventoryItem> FromServerPlan(SyncPlan plan, RuntimeConfig config)
    {
        var items = new List<ContentInventoryItem>();

        foreach (var action in plan.Actions)
        {
            if (action.Kind == SyncActionKind.Download)
                continue;

            if (action.LocalFile is not { } localFile || string.IsNullOrWhiteSpace(localFile.FullPath))
                continue;

            if (!File.Exists(localFile.FullPath))
                continue;

            items.Add(new ContentInventoryItem
            {
                PackId = action.BundledFile?.PackId ?? "",
                PackRevision = action.BundledFile?.Revision ?? "",
                PackFileHash = action.BundledFile?.InstalledHash ?? "",
                ContentType = action.ContentType,
                Source = action.ManifestMod?.VersionSource ?? action.PackManifestItem?.VersionSource ?? "",
                SourceId = action.ManifestMod?.SourceId ?? action.PackManifestItem?.SourceId ?? "",
                Slug = action.ManifestMod?.Slug ?? action.PackManifestItem?.Slug ?? "",
                Name = action.ManifestMod?.Name ?? action.PackManifestItem?.Name ?? localFile.Stem,
                IconUrl = action.ManifestMod?.IconUrl ?? action.PackManifestItem?.IconUrl,
                VersionName = action.InstalledVersion,
                VersionNumber = action.ManifestMod?.VersionNumber ?? action.PackManifestItem?.VersionNumber ?? "",
                DownloadFilename = localFile.FileName,
                FileHash = action.ManifestMod?.DownloadHash?.Hash ?? action.PackManifestItem?.DownloadHash?.Hash ?? "",
                LocalPath = localFile.FullPath,
                InstalledAtUtc = "",
            });
        }

        return items;
    }

    public static List<ContentInventoryItem> FromLocalPlan(LocalContentPlan plan, string instanceDirectory)
    {
        var items = new List<ContentInventoryItem>();

        foreach (var planItem in plan.Items)
        {
            // An unavailable standalone CurseForge item must remain in inventory so a later key can recover it.
            var retainCurseForgeItem = planItem.PackMember is null
                && planItem.ManifestItem?.Source.Equals("curseforge", StringComparison.OrdinalIgnoreCase) == true;
            if (planItem.Kind == SyncActionKind.Download && !retainCurseForgeItem)
                continue;

            if (planItem.UntrackedFilePath is { Length: > 0 } untrackedPath)
            {
                if (!File.Exists(untrackedPath))
                    continue;

                items.Add(new ContentInventoryItem
                {
                    ContentType = planItem.ContentType,
                    Name = Path.GetFileNameWithoutExtension(untrackedPath),
                    DownloadFilename = Path.GetFileName(untrackedPath),
                    LocalPath = untrackedPath,
                });
                continue;
            }

            if (planItem.ManifestItem is not { } manifestItem)
                continue;

            var subfolder = ContentDownloader.GetSubfolder(manifestItem.ContentType);
            var localPath = Path.Combine(instanceDirectory, subfolder, manifestItem.InstalledVersion.Filename);
            if (!File.Exists(localPath) && !retainCurseForgeItem)
                continue;

            items.Add(new ContentInventoryItem
            {
                ContentType = manifestItem.ContentType,
                Source = manifestItem.Source,
                SourceId = manifestItem.SourceId,
                Slug = manifestItem.Slug,
                Name = manifestItem.Name,
                IconUrl = manifestItem.IconUrl,
                SourceVersionId = manifestItem.InstalledVersion.SourceVersionId,
                VersionName = manifestItem.InstalledVersion.VersionName,
                VersionNumber = "",
                DownloadFilename = manifestItem.InstalledVersion.Filename,
                FileHash = manifestItem.InstalledVersion.FileHash ?? "",
                McVersion = manifestItem.InstalledVersion.McVersion ?? "",
                Loader = manifestItem.InstalledVersion.Loader ?? "",
                DownloadUrl = manifestItem.InstalledVersion.DownloadUrl ?? "",
                LocalPath = localPath,
                InstalledAtUtc = manifestItem.InstalledVersion.InstalledAt,
            });
        }

        return items;
    }

    public static List<ContentInventoryItem> FromLocalManifest(ContentManifest manifest, string instanceDirectory)
    {
        var items = new List<ContentInventoryItem>();

        foreach (var manifestItem in manifest.Items)
        {
            var subfolder = ContentDownloader.GetSubfolder(manifestItem.ContentType);
            var localPath = Path.Combine(instanceDirectory, subfolder, manifestItem.InstalledVersion.Filename);
            if (!File.Exists(localPath)
                && !manifestItem.Source.Equals("curseforge", StringComparison.OrdinalIgnoreCase))
                continue;

            items.Add(new ContentInventoryItem
            {
                ContentType = manifestItem.ContentType,
                Source = manifestItem.Source,
                SourceId = manifestItem.SourceId,
                Slug = manifestItem.Slug,
                Name = manifestItem.Name,
                IconUrl = manifestItem.IconUrl,
                SourceVersionId = manifestItem.InstalledVersion.SourceVersionId,
                VersionName = manifestItem.InstalledVersion.VersionName,
                VersionNumber = "",
                DownloadFilename = manifestItem.InstalledVersion.Filename,
                FileHash = manifestItem.InstalledVersion.FileHash ?? "",
                McVersion = manifestItem.InstalledVersion.McVersion ?? "",
                Loader = manifestItem.InstalledVersion.Loader ?? "",
                DownloadUrl = manifestItem.InstalledVersion.DownloadUrl ?? "",
                LocalPath = localPath,
                InstalledAtUtc = manifestItem.InstalledVersion.InstalledAt,
            });
        }

        return items;
    }
}
