namespace HalerithAddonClient.Core;

/// <summary>
/// Builds a verified local content snapshot for a server from SQLite inventory
/// plus the files that currently exist in the configured content folders.
/// </summary>
public sealed class VerifiedServerContentInventory
{
    public List<LocalModFile> Mods { get; } = [];
    public List<LocalModFile> ResourcePacks { get; } = [];
    public List<LocalModFile> Shaders { get; } = [];
}

public static class VerifiedServerContentScanner
{
    public static VerifiedServerContentInventory Scan(
        string hostId,
        string serverPublicId,
        RuntimeConfig config)
    {
        if (string.IsNullOrWhiteSpace(config.ModsFolder))
            throw new ConfigurationIncompleteException(
                "This server has no Mods folder set. Open its settings and choose a Game Data folder to populate the content paths.");

        var rows = AppStorageDatabase.LoadServerContentInventory(hostId, serverPublicId);

        var snapshot = new VerifiedServerContentInventory();
        snapshot.Mods.AddRange(Merge(rows, ContentTypes.Mod, config.ModsFolder, "*.jar"));
        snapshot.ResourcePacks.AddRange(Merge(rows, ContentTypes.ResourcePack, config.ResourcePacksFolder, "*.zip"));
        snapshot.Shaders.AddRange(Merge(rows, ContentTypes.Shader, config.ShadersFolder, "*.zip"));
        return snapshot;
    }

    private static List<LocalModFile> Merge(
        IReadOnlyList<ContentInventoryItem> rows,
        string contentType,
        string folder,
        string searchPattern)
    {
        var files = new List<LocalModFile>();
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var row in rows.Where(r => string.Equals(r.ContentType, contentType, StringComparison.OrdinalIgnoreCase)))
        {
            if (string.IsNullOrWhiteSpace(row.LocalPath) || !File.Exists(row.LocalPath))
                continue;

            if (!string.IsNullOrWhiteSpace(folder) && !IsUnderFolder(row.LocalPath, folder))
                continue;

            files.Add(FromInventory(row));
            seenPaths.Add(Path.GetFullPath(row.LocalPath));
        }

        if (!string.IsNullOrWhiteSpace(folder) && Directory.Exists(folder))
        {
            foreach (var path in Directory.EnumerateFiles(folder, searchPattern, SearchOption.TopDirectoryOnly))
            {
                var fullPath = Path.GetFullPath(path);
                if (seenPaths.Contains(fullPath))
                    continue;

                files.Add(FromFile(path));
            }
        }

        return files
            .OrderBy(x => x.FileName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static LocalModFile FromInventory(ContentInventoryItem item)
    {
        var fileName = !string.IsNullOrWhiteSpace(item.DownloadFilename)
            ? item.DownloadFilename
            : Path.GetFileName(item.LocalPath);

        return new LocalModFile
        {
            FileName = fileName,
            FullPath = item.LocalPath,
            Stem = Path.GetFileNameWithoutExtension(fileName),
            Source = item.Source,
            SourceId = item.SourceId,
            Slug = item.Slug,
            InventoryName = item.Name,
            InventoryVersionName = item.VersionName,
            InventoryVersionNumber = item.VersionNumber,
            InventoryFileHash = item.FileHash,
        };
    }

    private static LocalModFile FromFile(string path)
    {
        var fileName = Path.GetFileName(path);
        return new LocalModFile
        {
            FileName = fileName,
            FullPath = path,
            Stem = Path.GetFileNameWithoutExtension(fileName),
        };
    }

    private static bool IsUnderFolder(string path, string folder)
    {
        try
        {
            var fullPath = Path.GetFullPath(path);
            var fullFolder = Path.GetFullPath(folder)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;

            return fullPath.StartsWith(fullFolder, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }
}
