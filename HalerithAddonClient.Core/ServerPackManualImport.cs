namespace HalerithAddonClient.Core;

/// <summary>Installs user-selected bytes for an exact, distribution-restricted CurseForge assignment.</summary>
public static class ServerPackManualImport
{
    public static Uri? DownloadPage(SyncAction action)
    {
        if (!IsEligible(action)) return null;
        var projectId = action.ManifestMod?.SourceId ?? action.PackManifestItem?.SourceId ?? "";
        var fileId = action.ManifestMod?.SourceVersionId ?? action.PackManifestItem?.SourceVersionId ?? "";
        var slug = action.ManifestMod?.Slug ?? action.PackManifestItem?.Slug ?? "";
        if (!long.TryParse(projectId, out _) || !long.TryParse(fileId, out _)
            || slug.Length == 0 || !slug.All(c => char.IsAsciiLetterOrDigit(c) || c == '-')) return null;
        var category = action.ContentType switch
        {
            ContentTypes.Mod => "mc-mods",
            ContentTypes.ResourcePack => "texture-packs",
            ContentTypes.Shader => "shaders",
            _ => null,
        };
        return category is null ? null : new Uri($"https://www.curseforge.com/minecraft/{category}/{slug}/files/{fileId}");
    }

    public static async Task<string> ImportAsync(SyncAction action, RuntimeConfig config, string selectedFile, CancellationToken ct = default)
    {
        if (action.Kind != SyncActionKind.Missing || !IsEligible(action))
            throw new InvalidOperationException("This item no longer requires a manual download. Refresh before importing it.");
        var hash = SafeFileHelpers.RequireExpectedHash(action.ManifestMod?.DownloadHash ?? action.PackManifestItem?.DownloadHash, "CurseForge file");
        var filename = SafeFileHelpers.RequireSafeFileName(action.ManifestMod?.DownloadFilename ?? action.PackManifestItem?.DownloadFilename ?? "", "CurseForge filename");
        var expectedSize = action.ManifestMod?.DownloadSizeBytes ?? action.PackManifestItem?.DownloadSizeBytes;
        var folder = action.ContentType switch
        {
            ContentTypes.ResourcePack => config.ResourcePacksFolder,
            ContentTypes.Shader => config.ShadersFolder,
            _ => config.ModsFolder,
        };
        if (string.IsNullOrWhiteSpace(folder))
            throw new InvalidOperationException("Configure this server's local content folder before importing the file.");
        await using (var input = File.OpenRead(selectedFile))
        {
            if (input.Length < 1 || input.Length > SyncExecutor.MaxOrdinaryDownloadBytes
                || (expectedSize is > 0 && input.Length != expectedSize))
                throw new InvalidDataException("The selected file does not match the required CurseForge file.");
        }
        try
        {
            await SafeFileHelpers.VerifyExpectedHashAsync(selectedFile, hash, ct);
        }
        catch (InvalidOperationException error)
        {
            throw new InvalidDataException("The selected file does not match the required CurseForge file.", error);
        }
        Directory.CreateDirectory(folder);
        var target = SafeFileHelpers.CombineUnderDirectory(folder, filename);
        var previous = target;
        var local = action.LocalFile;
        var sourceId = action.ManifestMod?.SourceId ?? action.PackManifestItem?.SourceId ?? "";
        if (local is not null && sourceId.Length > 0
            && string.Equals(local.Source, "curseforge", StringComparison.OrdinalIgnoreCase)
            && string.Equals(local.SourceId, sourceId, StringComparison.Ordinal)
            && string.Equals(Path.GetDirectoryName(Path.GetFullPath(local.FullPath)), Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            previous = local.FullPath;
        var stage = SafeFileHelpers.CombineUnderDirectory(folder, filename + "." + Guid.NewGuid().ToString("N") + ".tmp");
        var backup = SafeFileHelpers.CombineUnderDirectory(folder, filename + "." + Guid.NewGuid().ToString("N") + ".backup");
        try
        {
            await using (var input = File.OpenRead(selectedFile))
            await using (var output = File.Create(stage))
                await input.CopyToAsync(output, ct);
            await SafeFileHelpers.VerifyExpectedHashAsync(stage, hash, ct);
            ct.ThrowIfCancellationRequested();
            VerifiedContentCommit.Commit(stage, target, previous, backup);
        }
        finally
        {
            if (File.Exists(stage)) File.Delete(stage);
        }
        return target;
    }

    /// <summary>Updates the cached plan only after ImportAsync committed the exact file.</summary>
    public static void MarkInstalled(SyncAction action, string installedPath)
    {
        if (action.Kind != SyncActionKind.Missing || !IsEligible(action))
            throw new InvalidOperationException("This item no longer requires a manual download.");
        var filename = SafeFileHelpers.RequireSafeFileName(action.ManifestMod?.DownloadFilename ?? action.PackManifestItem?.DownloadFilename ?? "", "CurseForge filename");
        if (!File.Exists(installedPath) || !string.Equals(Path.GetFileName(installedPath), filename, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The installed CurseForge file is no longer available at its expected path.");

        action.LocalFile = new LocalModFile
        {
            FileName = filename,
            FullPath = installedPath,
            Stem = Path.GetFileNameWithoutExtension(filename),
            Source = action.ManifestMod?.VersionSource ?? action.PackManifestItem?.VersionSource ?? "",
            SourceId = action.ManifestMod?.SourceId ?? action.PackManifestItem?.SourceId ?? "",
            Slug = action.ManifestMod?.Slug ?? action.PackManifestItem?.Slug ?? "",
            InventoryVersionName = action.TargetVersion,
        };
        action.InstalledVersion = action.TargetVersion;
        action.Kind = SyncActionKind.UpToDate;
    }

    public static bool IsEligible(SyncAction action) =>
        action.ManifestMod is { Unavailable: true, ManualDownloadAllowed: true, VersionSource: "curseforge" }
        || action.PackManifestItem is { Unavailable: true, ManualDownloadAllowed: true, VersionSource: "curseforge" };
}
