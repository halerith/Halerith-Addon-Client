namespace HalerithAddonClient.Core.ContentSearch;

using HalerithAddonClient.Core;

/// <summary>
/// Utility for downloading content items to local instance folders and tracking them in SQLite.
/// </summary>
public static class ContentDownloader
{
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(60),
        DefaultRequestHeaders =
        {
            { "User-Agent", ProviderContacts.ModrinthUserAgent }
        }
    };

    /// <summary>
    /// Downloads a content item to the appropriate subfolder within the instance directory
    /// and updates the content manifest.
    /// </summary>
    /// <param name="instanceDirectory">Root directory of the instance (e.g., Runtime/instances/{guid}).</param>
    /// <param name="searchResult">Search result containing project metadata.</param>
    /// <param name="version">Version result containing download details.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Full path to the downloaded file.</returns>
    /// <exception cref="InvalidOperationException">Thrown if the version has no download URL.</exception>
    public static async Task<string> DownloadAndTrackAsync(
        string localInstanceId,
        string instanceDirectory,
        ContentSearchResult searchResult,
        ContentVersionResult version,
        string? replaceExistingPath = null,
        CancellationToken ct = default)
    {
        var pack = LocalModpacks.LocalModpackStore.Load(instanceDirectory);
        if (pack?.Members.Any(m => !m.Overridden && m.Project.Source == searchResult.Source && m.Project.Id == searchResult.Id) == true)
            throw new InvalidOperationException("Enable the pack member's version override before changing this addon.");
        if (string.IsNullOrEmpty(version.DownloadUrl))
            throw new InvalidOperationException($"No download URL for {searchResult.Name}");

        var expectedHash = SafeFileHelpers.RequireExpectedHash(
            version.FileHash,
            $"{searchResult.Source} download for {searchResult.Name}");

        var downloadUri = HttpsUrlPolicy.RequireHttpsAbsoluteUri(version.DownloadUrl, $"Download URL for {searchResult.Name}");

        // Determine target subfolder based on content type
        var subfolder = GetSubfolder(searchResult.ContentType);
        var targetDir = Path.Combine(instanceDirectory, subfolder);
        Directory.CreateDirectory(targetDir);

        var safeFilename = SafeFileHelpers.RequireSafeFileName(version.Filename, "Download filename");
        version.Filename = safeFilename;
        var targetPath = SafeFileHelpers.CombineUnderDirectory(targetDir, safeFilename);
        var tempPath = SafeFileHelpers.CreateDownloadTempPath(targetDir, safeFilename);

        try
        {
            // Download file
            using (var response = await Http.GetAsync(downloadUri, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                response.EnsureSuccessStatusCode();

                await using var stream = await response.Content.ReadAsStreamAsync(ct);
                await using var file = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                await stream.CopyToAsync(file, ct);
            }

            await SafeFileHelpers.VerifyExpectedHashAsync(tempPath, expectedHash, ct);
            File.Move(tempPath, targetPath, overwrite: true);

            if (ShouldDeleteReplacedFile(replaceExistingPath, targetDir, targetPath))
            {
                File.Delete(replaceExistingPath!);
            }

            // Update SQLite inventory to track the installed item.
            LocalInstanceContentStore.AddOrUpdateItem(localInstanceId, instanceDirectory, searchResult, version);
        }
        finally
        {
            try
            {
                if (File.Exists(tempPath))
                    File.Delete(tempPath);
            }
            catch
            {
                // Non-fatal cleanup failure.
            }
        }

        return targetPath;
    }

    /// <summary>
    /// Maps content type to the standard Minecraft instance subfolder.
    /// </summary>
    public static string GetSubfolder(string contentType) => contentType switch
    {
        ContentTypes.Mod => "mods",
        ContentTypes.ResourcePack => "resourcepacks",
        ContentTypes.Shader => "shaderpacks",
        _ => "mods"
    };

    private static bool ShouldDeleteReplacedFile(string? replaceExistingPath, string targetDir, string targetPath)
    {
        if (string.IsNullOrWhiteSpace(replaceExistingPath))
            return false;

        var deletePath = Path.GetFullPath(replaceExistingPath);
        var newPath = Path.GetFullPath(targetPath);
        if (string.Equals(deletePath, newPath, StringComparison.OrdinalIgnoreCase))
            return false;

        var allowedRoot = Path.GetFullPath(targetDir)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;

        if (!deletePath.StartsWith(allowedRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Refusing to delete replaced content outside the instance content folder.");

        return File.Exists(deletePath);
    }
}
