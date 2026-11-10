namespace HalerithAddonClient.Core;

public sealed class SyncExecutor
{
    private readonly ApiClient _apiClient;
    private readonly RuntimeConfig _config;
    private readonly Action<string> _log;
    private readonly ContentFileOperations? _fileOperations;
    public Func<ModpackFileReview, CancellationToken, Task<bool>>? ReviewModpackFilesAsync { get; init; }
    // Current manifests do not always supply sizes. Bound ordinary content even then.
    internal const long MaxOrdinaryDownloadBytes = 1024L * 1024L * 1024L;

    public SyncExecutor(ApiClient apiClient, RuntimeConfig config, Action<string> log)
        : this(apiClient, config, log, null)
    {
    }

    /// <summary>
    /// Creates a new sync executor.
    /// </summary>
    /// <param name="apiClient">API client used for file downloads.</param>
    /// <param name="config">Runtime configuration for the target server.</param>
    /// <param name="log">Callback for logging sync progress.</param>
    internal SyncExecutor(ApiClient apiClient, RuntimeConfig config, Action<string> log, ContentFileOperations? fileOperations)
    {
        _apiClient = apiClient;
        _config = config;
        _log = log;
        _fileOperations = fileOperations;
    }

    public async Task<SyncExecutionResult> ExecuteAsync(SyncPlan plan, CancellationToken ct)
    {
        var result = new SyncExecutionResult();

        foreach (var action in plan.Actions)
        {
            ct.ThrowIfCancellationRequested();
            // Bundled rows describe ownership; only the pack action may change these files.
            if (action.BundledFile is not null) continue;
            try
            {
                switch (action.Kind)
                {
                    case SyncActionKind.Missing:
                        _log($"Missing: {action.ManifestMod?.Name ?? action.PackManifestItem?.Name}. Existing files are kept.");
                        break;
                    case SyncActionKind.UpToDate:
                        result.UpToDate++;
                        result.UpToDatePackages.Add(CreateAuditItem(action));
                        _log($"Current: {GetDisplayName(action)}");
                        break;
                    case SyncActionKind.Ignore:
                        result.Ignored++;
                        result.IgnoredPackages.Add(CreateAuditItem(action));
                        _log($"Ignored: {action.LocalFile?.FileName ?? GetDisplayName(action)}");
                        break;
                    case SyncActionKind.Download:
                        if (action.Modpack is not null)
                        {
                            var overlay = new ModpackOverlayService(_apiClient, _config, _log) { ReviewFilesAsync = ReviewModpackFilesAsync };
                            if (action.ApprovedModpackReview is { } approved)
                                await overlay.ApplyReviewedAsync(action.Modpack, approved, ct).ConfigureAwait(false);
                            else await overlay.ApplyAsync(action.Modpack, ct).ConfigureAwait(false);
                            result.Downloaded++;
                            result.DownloadedPackages.Add(CreateAuditItem(action));
                            break;
                        }
                        await DownloadNewAsync(action, result, ct).ConfigureAwait(false);
                        break;
                    case SyncActionKind.Replace:
                        await ReplaceAsync(action, result, ct).ConfigureAwait(false);
                        break;
                    case SyncActionKind.Remove:
                        RemoveExtra(action, result);
                        break;
                }
            }
            catch (OperationCanceledException)
            {
                // Cancellation is not a per-item failure — propagate so the caller can abort cleanly.
                throw;
            }
            catch (Exception ex)
            {
                if (action.Modpack is not null) ModpackOverlayService.RecordFailure(action.Modpack, _config.GameDataPath);
                // Record as a failed item and continue so the rest of the plan still runs.
                var failedItem = CreateAuditItem(action);
                failedItem.FailureCode = SyncFailureCodes.FromException(ex);
                result.Failed++;
                result.FailedPackages.Add(failedItem);
                _log($"ERROR: Failed to process {GetDisplayName(action)}: {ex.Message}");
            }
        }

        return result;
    }

    private async Task DownloadNewAsync(SyncAction action, SyncExecutionResult result, CancellationToken ct)
    {
        var filename = SafeFileHelpers.RequireSafeFileName(GetDownloadFilename(action), "Manifest download filename");
        var downloadUrl = GetDownloadUrl(action);
        var targetFolder = GetTargetFolder(action);
        Directory.CreateDirectory(targetFolder);

        var tempFile = SafeFileHelpers.CreateDownloadTempPath(targetFolder, filename);
        var targetFile = SafeFileHelpers.CombineUnderDirectory(targetFolder, filename);

        _log($"Downloading: {GetDisplayName(action)} -> {filename}");
        try
        {
            var expectedHash = GetRequiredDownloadHash(action);
            await _apiClient.DownloadFileAsync(downloadUrl, tempFile, ct, DownloadLimit(action)).ConfigureAwait(false);
            await SafeFileHelpers.VerifyExpectedHashAsync(tempFile, expectedHash, ct).ConfigureAwait(false);
            var backup = BackupPath(targetFolder, targetFile);
            VerifiedContentCommit.Commit(tempFile, targetFile, targetFile, backup, _fileOperations, _log);
            if (backup is not null) PruneBackupFolder(Path.GetDirectoryName(backup)!);
        }
        finally
        {
            TryDeleteTempFile(tempFile);
        }

        result.Downloaded++;
        result.DownloadedPackages.Add(CreateAuditItem(action));
    }

    private async Task ReplaceAsync(SyncAction action, SyncExecutionResult result, CancellationToken ct)
    {
        var filename = SafeFileHelpers.RequireSafeFileName(GetDownloadFilename(action), "Manifest download filename");
        var downloadUrl = GetDownloadUrl(action);
        var local = action.LocalFile ?? throw new InvalidOperationException("LocalFile missing on Replace action.");
        var targetFolder = GetTargetFolder(action);
        Directory.CreateDirectory(targetFolder);
        var tempFile = SafeFileHelpers.CreateDownloadTempPath(targetFolder, filename);
        var targetFile = SafeFileHelpers.CombineUnderDirectory(targetFolder, filename);

        try
        {
            var expectedHash = GetRequiredDownloadHash(action);
            var packManaged = action.ManifestMod?.PackManaged == true || action.PackManifestItem?.PackManaged == true;
            var renaming = !string.Equals(Path.GetFullPath(local.FullPath), Path.GetFullPath(targetFile), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
            if (packManaged && renaming && File.Exists(targetFile))
                throw new InvalidOperationException("The pack filename is already occupied. Review the duplicate file before retrying.");
            var reuse = false;
            if (packManaged && renaming && File.Exists(local.FullPath))
            {
                try
                {
                    await SafeFileHelpers.VerifyExpectedHashAsync(local.FullPath, expectedHash, ct).ConfigureAwait(false);
                    reuse = true;
                }
                catch (InvalidOperationException) { /* Different bytes need the approved download. */ }
            }
            if (reuse) File.Copy(local.FullPath, tempFile, overwrite: false);
            else await _apiClient.DownloadFileAsync(downloadUrl, tempFile, ct, DownloadLimit(action)).ConfigureAwait(false);
            await SafeFileHelpers.VerifyExpectedHashAsync(tempFile, expectedHash, ct).ConfigureAwait(false);
            var backup = BackupPath(targetFolder, local.FullPath);
            VerifiedContentCommit.Commit(tempFile, targetFile, local.FullPath, backup, _fileOperations, _log);
            if (backup is not null) PruneBackupFolder(Path.GetDirectoryName(backup)!);
        }
        finally
        {
            TryDeleteTempFile(tempFile);
        }

        result.Replaced++;
        result.ReplacedPackages.Add(CreateAuditItem(action));
    }

    private string? BackupPath(string folder, string original)
    {
        if (!_config.KeepBackups || !File.Exists(original)) return null;
        var backupFolder = Path.Combine(folder, "_backup");
        Directory.CreateDirectory(backupFolder);
        var filename = SafeFileHelpers.RequireSafeFileName(Path.GetFileName(original), "Local file name");
        return SafeFileHelpers.CombineUnderDirectory(backupFolder, $"{DateTime.UtcNow:yyyyMMddHHmmssfff}_{Guid.NewGuid():N}_{filename}");
    }

    internal static long DownloadLimit(SyncAction action)
    {
        var expected = action.ManifestMod?.DownloadSizeBytes ?? action.PackManifestItem?.DownloadSizeBytes;
        if (expected is < 0 || expected > MaxOrdinaryDownloadBytes)
            throw new InvalidOperationException("This content file exceeds the supported 1 GB download limit.");
        return expected is > 0 ? expected.Value : MaxOrdinaryDownloadBytes;
    }

    private void RemoveExtra(SyncAction action, SyncExecutionResult result)
    {
        var local = action.LocalFile ?? throw new InvalidOperationException("LocalFile missing on Remove action.");
        var targetFolder = GetTargetFolder(action);

        if (_config.KeepBackups)
        {
            var removedFolder = Path.Combine(targetFolder, "_removed");
            Directory.CreateDirectory(removedFolder);
            File.Move(local.FullPath, Path.Combine(removedFolder, $"{DateTime.UtcNow:yyyyMMddHHmmss}_{local.FileName}"), overwrite: true);
            PruneBackupFolder(removedFolder);
        }
        else
        {
            File.Delete(local.FullPath);
        }

        result.Removed++;
        result.RemovedPackages.Add(CreateAuditItem(action));
    }

    /// <summary>
    /// If <see cref="RuntimeConfig.BackupMaxFilesPerFolder"/> is greater than zero,
    /// delete the oldest files in the folder until the count is at or below the limit.
    /// </summary>
    private void PruneBackupFolder(string folder)
    {
        var max = _config.BackupMaxFilesPerFolder;
        if (max <= 0) return; // 0 = unlimited

        try
        {
            var files = new DirectoryInfo(folder)
                .GetFiles()
                .OrderByDescending(f => f.CreationTimeUtc)
                .ToList();

            if (files.Count <= max) return;

            foreach (var old in files.Skip(max))
            {
                try { old.Delete(); }
                catch (Exception ex) { _log($"Failed to delete old backup file: {ex.Message}"); }
            }
        }
        catch
        {
            // Directory enumeration failure is non-fatal.
        }
    }

    /// <summary>
    /// Returns the local folder where the action's file should be placed.
    /// All items — including custom uploads — route to the standard content folders.
    /// </summary>
    private string GetTargetFolder(SyncAction action) =>
        action.ContentType switch
        {
            ContentTypes.ResourcePack => _config.ResourcePacksFolder,
            ContentTypes.Shader       => _config.ShadersFolder,
            _                         => _config.ModsFolder
        };

    private static string GetDownloadFilename(SyncAction action) =>
        action.ManifestMod?.DownloadFilename
        ?? action.PackManifestItem?.DownloadFilename
        ?? throw new InvalidOperationException("No download filename available on action.");

    private static string GetDownloadUrl(SyncAction action) =>
        action.ManifestMod?.DownloadUrl
        ?? action.PackManifestItem?.DownloadUrl
        ?? throw new InvalidOperationException("No download URL available on action.");

    private static ManifestHashInfo? GetExpectedDownloadHash(SyncAction action) =>
        action.ManifestMod?.DownloadHash
        ?? action.PackManifestItem?.DownloadHash;

    private static ManifestHashInfo GetRequiredDownloadHash(SyncAction action) =>
        SafeFileHelpers.RequireExpectedHash(
            GetExpectedDownloadHash(action),
            $"Manifest download for {GetDisplayName(action)}");

    private static string GetDisplayName(SyncAction action) =>
        action.Modpack?.VersionName
        ?? action.ManifestMod?.Name
        ?? action.PackManifestItem?.Name
        ?? action.LocalFile?.Stem
        ?? action.LocalFile?.FileName
        ?? "(unknown)";

    private static SyncActionAuditItem CreateAuditItem(SyncAction action)
    {
        return new SyncActionAuditItem
        {
            ContentItemId = action.ManifestMod?.ModId.ToString()
                ?? action.PackManifestItem?.ContentItemId
                ?? action.Modpack?.Id
                ?? string.Empty,
            Slug = action.ManifestMod?.Slug ?? action.PackManifestItem?.Slug ?? action.Modpack?.ProjectId ?? string.Empty,
        };
    }

    private static void TryDeleteTempFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // Non-fatal cleanup failure.
        }
    }
}
