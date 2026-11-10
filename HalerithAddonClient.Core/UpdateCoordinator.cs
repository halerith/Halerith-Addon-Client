using System.IO.Compression;
using System.Formats.Tar;
using HalerithAddonClient.Core.Platform;

namespace HalerithAddonClient.Core;

public static class UpdateCoordinator
{
    public delegate Task UpdatePackageDownloader(string url, string destinationPath, CancellationToken ct, long? maxBytes);

    private const string UpdateRootFolderName = "SelfUpdate";
    private const string StagingFolderName = "staging";
    private const long MaxUpdatePackageBytes = 100L * 1024L * 1024L;
    private static readonly TimeSpan CleanupAge = TimeSpan.FromHours(6);

    public static string GetUpdateRootPath(RuntimeConfig config)
        => Path.Combine(config.TempFolder, UpdateRootFolderName);

    public static string GetStagingRootPath(RuntimeConfig config)
        => Path.Combine(GetUpdateRootPath(config), StagingFolderName);

    public static void CleanupStaleUpdateFolders(RuntimeConfig config, Action<string>? log = null)
        => CleanupStaleUpdateFoldersCore(config.TempFolder, log);

    private static void CleanupStaleUpdateFoldersCore(string tempFolder, Action<string>? log)
    {
        var updateRoot = Path.Combine(tempFolder, UpdateRootFolderName);
        if (!Directory.Exists(updateRoot))
            return;

        try
        {
            foreach (var packagePath in Directory.EnumerateFiles(updateRoot, "*.zip", SearchOption.TopDirectoryOnly))
                TryDeleteFileIfOld(packagePath, log);
            foreach (var packagePath in Directory.EnumerateFiles(updateRoot, "*.tgz", SearchOption.TopDirectoryOnly))
                TryDeleteFileIfOld(packagePath, log);
            foreach (var packagePath in Directory.EnumerateFiles(updateRoot, "*.deb", SearchOption.TopDirectoryOnly))
                TryDeleteFileIfOld(packagePath, log);
            foreach (var packagePath in Directory.EnumerateFiles(updateRoot, "*.dmg", SearchOption.TopDirectoryOnly))
                TryDeleteFileIfOld(packagePath, log);

            var stagingRoot = Path.Combine(updateRoot, StagingFolderName);
            if (!Directory.Exists(stagingRoot))
                return;

            foreach (var dir in Directory.EnumerateDirectories(stagingRoot))
                TryDeleteDirectoryIfOld(dir, log);
        }
        catch (Exception ex)
        {
            log?.Invoke($"Update cleanup warning: {ex.Message}");
        }
    }

    public static Task<bool> RunHelperIfNeededAsync(UpdatePackageDownloader downloadPackageAsync, RuntimeConfig config, HealthLauncherInfo? launcher, string downloadUrl, CancellationToken ct, string? relaunchArgs = null)
        => RunHelperIfNeededAsyncCore(downloadPackageAsync, config.TempFolder, launcher, downloadUrl, ct, relaunchArgs);

    public static Task<StagedUpdatePackage?> StageUpdateAsync(
        UpdatePackageDownloader downloadPackageAsync,
        RuntimeConfig config,
        HealthLauncherInfo? launcher,
        string downloadUrl,
        CancellationToken ct,
        string? relaunchArgs = null)
        => StageUpdateAsyncCore(downloadPackageAsync, config.TempFolder, launcher, downloadUrl, ct, relaunchArgs);

    private static async Task<bool> RunHelperIfNeededAsyncCore(UpdatePackageDownloader downloadPackageAsync, string tempFolder, HealthLauncherInfo? update, string downloadUrl, CancellationToken ct, string? relaunchArgs = null)
    {
        if (update is null || !update.UpdateAvailable || !update.FileAvailable)
        {
            // No update available — any leftover staging content is orphaned.
            // Clean it up unconditionally (not age-gated) so it doesn't accumulate.
            CleanupUpdateRootUnconditionally(tempFolder);
            return false;
        }

        if (PlatformServices.Current.Updates.UsesExternalUpdateFeed)
        {
            CleanupUpdateRootUnconditionally(tempFolder);
            return await PlatformServices.Current.Updates.CheckForUpdatesAsync(update, relaunchArgs, ct)
                .ConfigureAwait(false);
        }

        var stagedPackage = await StageUpdateAsyncCore(
            downloadPackageAsync,
            tempFolder,
            update,
            downloadUrl,
            ct,
            relaunchArgs).ConfigureAwait(false);

        return stagedPackage is not null
            && await PlatformServices.Current.Updates.InstallAsync(stagedPackage, ct).ConfigureAwait(false);
    }

    private static async Task<StagedUpdatePackage?> StageUpdateAsyncCore(
        UpdatePackageDownloader downloadPackageAsync,
        string tempFolder,
        HealthLauncherInfo? update,
        string downloadUrl,
        CancellationToken ct,
        string? relaunchArgs = null)
    {
        if (update is null || !update.UpdateAvailable || !update.FileAvailable)
        {
            CleanupUpdateRootUnconditionally(tempFolder);
            return null;
        }

        if (PlatformServices.Current.Updates.UsesExternalUpdateFeed)
        {
            // Sparkle owns download staging on macOS.
            CleanupUpdateRootUnconditionally(tempFolder);
            return null;
        }

        CleanupStaleUpdateFoldersCore(tempFolder, log: null);

        var updateRoot = Path.Combine(tempFolder, UpdateRootFolderName);
        var stagingRoot = Path.Combine(updateRoot, StagingFolderName);
        Directory.CreateDirectory(updateRoot);
        Directory.CreateDirectory(stagingRoot);

        var stageId = DateTime.UtcNow.ToString("yyyyMMddHHmmss") + "-" + Guid.NewGuid().ToString("N");
        var stageDir = Path.Combine(stagingRoot, stageId);
        Directory.CreateDirectory(stageDir);

        var packageExtension = ResolvePackageExtension(update);
        if (OperatingSystem.IsLinux() && packageExtension != ".deb")
            throw new InvalidDataException("Linux updates require a signed DEB package.");
        var packageFileName = ResolvePackageFileName(update, packageExtension);
        var packagePath = Path.Combine(updateRoot, packageFileName);
        await downloadPackageAsync(downloadUrl, packagePath, ct, MaxUpdatePackageBytes).ConfigureAwait(false);
        SafeFileHelpers.RequireExpectedHash(update.Sha256, "client update package");
        await SafeFileHelpers.VerifyExpectedHashAsync(packagePath, update.Sha256, ct).ConfigureAwait(false);

        if (OperatingSystem.IsWindows())
        {
            if (packageExtension != ".zip" || string.IsNullOrWhiteSpace(update.Version))
                throw new InvalidDataException("A Windows update requires a versioned ZIP package.");
            WindowsUpdateTrust.ExtractPackage(packagePath, stageDir);
            using var verified = WindowsUpdateTrust.VerifyAndLock(stageDir, update.Version,
                System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version?.ToString());
        }
        else
            ExtractUpdatePackage(packagePath, stageDir, packageExtension);
        if (OperatingSystem.IsLinux())
        {
            if (packageExtension != ".deb" || update.LinuxManifest is null || update.LinuxSignature is null || update.Version is null)
                throw new InvalidDataException("A signed Linux release is required.");
            var manifest = System.Text.Encoding.UTF8.GetBytes(update.LinuxManifest);
            var signature = Convert.FromBase64String(update.LinuxSignature);
            LinuxUpdateTrust.Verify(Path.Combine(stageDir, packageFileName), manifest, signature, update.Version,
                System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version?.ToString());
            await File.WriteAllBytesAsync(Path.Combine(stageDir, "release.json"), manifest, ct);
            await File.WriteAllBytesAsync(Path.Combine(stageDir, "release.json.sig"), signature, ct);
        }
        TryDeletePackage(packagePath);

        return new StagedUpdatePackage(
            stageDir,
            Environment.ProcessId.ToString(),
            relaunchArgs,
            update.Version);
    }

    private static void ExtractUpdatePackage(string packagePath, string stageDir, string packageExtension)
    {
        switch (packageExtension.ToLowerInvariant())
        {
            case ".zip":
                ZipFile.ExtractToDirectory(packagePath, stageDir, overwriteFiles: true);
                return;

            case ".tgz":
            case ".tar.gz":
                using (var file = File.OpenRead(packagePath))
                using (var gzip = new GZipStream(file, CompressionMode.Decompress))
                {
                    TarFile.ExtractToDirectory(gzip, stageDir, overwriteFiles: true);
                }
                return;

            case ".deb":
                File.Copy(packagePath, Path.Combine(stageDir, Path.GetFileName(packagePath)), overwrite: true);
                return;

            default:
                throw new NotSupportedException($"Update package extension '{packageExtension}' is not supported.");
        }
    }

    private static string ResolvePackageExtension(HealthLauncherInfo update)
    {
        var packageType = (update.PackageType ?? "").Trim().TrimStart('.').ToLowerInvariant();
        if (packageType is "zip" or "dmg" or "tgz" or "deb")
            return "." + packageType;

        var filename = update.Filename;
        if (!string.IsNullOrWhiteSpace(filename))
        {
            var extension = Path.GetExtension(filename.Trim()).ToLowerInvariant();
            if (extension is ".zip" or ".dmg" or ".tgz" or ".deb")
                return extension;
        }

        return PlatformServices.Current.Updates.ExpectedPackageExtension;
    }

    private static string ResolvePackageFileName(HealthLauncherInfo update, string packageExtension)
    {
        if (!string.IsNullOrWhiteSpace(update.Filename))
        {
            var filename = SafeFileHelpers.RequireSafeFileName(update.Filename, "client update filename");
            if (string.Equals(Path.GetExtension(filename), packageExtension, StringComparison.OrdinalIgnoreCase))
                return filename;
        }

        return "client-update" + packageExtension;
    }

    /// <summary>
    /// Unconditionally removes the entire SelfUpdate folder (staging dirs and any
    /// leftover zip files) when the server confirms no update is available.
    /// Unlike <see cref="CleanupStaleUpdateFoldersCore"/>, this is not age-gated —
    /// if there is no pending update, all staged content is definitively orphaned.
    /// </summary>
    private static void CleanupUpdateRootUnconditionally(string tempFolder)
    {
        var updateRoot = Path.Combine(tempFolder, UpdateRootFolderName);
        if (!Directory.Exists(updateRoot))
            return;

        try
        {
            Directory.Delete(updateRoot, recursive: true);
        }
        catch
        {
            // Best effort — don't let cleanup failure surface to the caller.
        }
    }

    private static void TryDeletePackage(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // Best effort only.
        }
    }

    private static void TryDeleteFileIfOld(string path, Action<string>? log)
    {
        try
        {
            var info = new FileInfo(path);
            if (DateTime.UtcNow - info.LastWriteTimeUtc < CleanupAge)
                return;

            info.IsReadOnly = false;
            info.Delete();
        }
        catch (Exception ex)
        {
            log?.Invoke($"Cleanup skipped for file '{path}': {ex.Message}");
        }
    }

    private static void TryDeleteDirectoryIfOld(string path, Action<string>? log)
    {
        try
        {
            var info = new DirectoryInfo(path);
            var referenceTime = info.LastWriteTimeUtc > info.CreationTimeUtc ? info.LastWriteTimeUtc : info.CreationTimeUtc;
            if (DateTime.UtcNow - referenceTime < CleanupAge)
                return;

            Directory.Delete(path, recursive: true);
        }
        catch (Exception ex)
        {
            log?.Invoke($"Cleanup skipped for folder '{path}': {ex.Message}");
        }
    }
}
