namespace HalerithAddonClient.Core.ContentSearch;

/// <summary>
/// SQLite-backed content tracking for user-created local instances.
/// </summary>
public static class LocalInstanceContentStore
{
    public static ContentManifest Load(string localInstanceId)
    {
        var manifest = new ContentManifest();
        foreach (var row in AppStorageDatabase.LoadLocalInstanceContentInventory(localInstanceId))
        {
            if (string.IsNullOrWhiteSpace(row.Source) || string.IsNullOrWhiteSpace(row.SourceId))
                continue;

            manifest.Items.Add(ToManifestItem(row));
        }

        return manifest;
    }

    public static void Save(string localInstanceId, string instanceDirectory, ContentManifest manifest)
    {
        AppStorageDatabase.SaveLocalInstanceContentInventory(
            localInstanceId,
            ContentInventoryBuilder.FromLocalManifest(manifest, instanceDirectory));
    }

    public static void AddOrUpdateItem(
        string localInstanceId,
        string instanceDirectory,
        ContentSearchResult searchResult,
        ContentVersionResult version)
    {
        var subfolder = ContentDownloader.GetSubfolder(searchResult.ContentType);
        var localPath = Path.Combine(instanceDirectory, subfolder, version.Filename);

        AppStorageDatabase.UpsertLocalInstanceContentItem(
            localInstanceId,
            new ContentInventoryItem
            {
                ContentType = searchResult.ContentType,
                Source = searchResult.Source,
                SourceId = searchResult.Id,
                Slug = searchResult.Slug,
                Name = searchResult.Name,
                IconUrl = searchResult.IconUrl,
                SourceVersionId = version.SourceVersionId,
                VersionName = version.VersionName,
                VersionNumber = version.VersionNumber ?? "",
                DownloadFilename = version.Filename,
                FileHash = version.FileHash ?? "",
                McVersion = version.McVersion ?? "",
                Loader = version.Loader ?? "",
                DownloadUrl = version.DownloadUrl ?? "",
                LocalPath = localPath,
                InstalledAtUtc = DateTime.UtcNow.ToString("o"),
            });
    }

    public static bool RemoveItem(string localInstanceId, string source, string sourceId)
        => AppStorageDatabase.RemoveLocalInstanceContentItem(localInstanceId, source, sourceId);

    public static HashSet<string> GetInstalledKeys(string localInstanceId)
        => AppStorageDatabase.GetLocalInstanceInstalledContentKeys(localInstanceId);

    private static ContentManifestItem ToManifestItem(ContentInventoryItem row) => new()
    {
        Source = row.Source,
        SourceId = row.SourceId,
        Slug = row.Slug,
        Name = row.Name,
        ContentType = row.ContentType,
        IconUrl = row.IconUrl,
        InstalledVersion = new ContentManifestVersion
        {
            SourceVersionId = row.SourceVersionId,
            VersionName = row.VersionName,
            Filename = row.DownloadFilename,
            FileHash = row.FileHash,
            McVersion = row.McVersion,
            Loader = row.Loader,
            DownloadUrl = row.DownloadUrl,
            InstalledAt = row.InstalledAtUtc,
        }
    };
}
