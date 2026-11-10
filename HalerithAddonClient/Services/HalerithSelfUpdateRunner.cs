using HalerithAddonClient.Core;
using HalerithAddonClient.Core.Platform;

namespace HalerithAddonClient.Services;

public static class HalerithSelfUpdateRunner
{
    public sealed record PreparedUpdate(
        HealthLauncherInfo Launcher,
        StagedUpdatePackage? Package,
        string? RelaunchArgs)
    {
        public string Version => string.IsNullOrWhiteSpace(Launcher.Version)
            ? "new version"
            : Launcher.Version?.Trim() ?? "new version";
    }

    public static async Task<PreparedUpdate?> TryPrepareAsync(
        string clientVersion,
        AppConfig? appConfig,
        string? relaunchArgs,
        Action<string>? log,
        CancellationToken ct)
    {
#if HALERITH_SANDBOX_BUILD
        log?.Invoke("Self-update is disabled in the sandbox build.");
        return null;
#else
        try
        {
            log?.Invoke("Checking Halerith update feed.");

            using var service = new HalerithUpdateService(new HttpClient
            {
                Timeout = TimeSpan.FromMinutes(10),
            });

            var launcher = await service.CheckAsync(clientVersion, ct).ConfigureAwait(false);
            var runtime = CreateUpdateRuntimeConfig(appConfig);

            if (launcher is not { UpdateAvailable: true, FileAvailable: true } ||
                string.IsNullOrWhiteSpace(launcher.DownloadUrl))
            {
                // Also clears any orphaned package from an older update check.
                await service.PrepareUpdateAsync(runtime, launcher, ct, relaunchArgs)
                    .ConfigureAwait(false);
                log?.Invoke("No Halerith client update available.");
                return null;
            }

            var version = launcher.Version ?? "new version";
            if (PlatformServices.Current.Updates.UsesExternalUpdateFeed)
            {
                // Sparkle owns its download cache; clear any non-Sparkle staging.
                await service.PrepareUpdateAsync(runtime, launcher, ct, relaunchArgs)
                    .ConfigureAwait(false);
                log?.Invoke($"Halerith client update available through Sparkle: {version}.");
                return new PreparedUpdate(launcher, null, relaunchArgs);
            }

            var package = await service.PrepareUpdateAsync(runtime, launcher, ct, relaunchArgs)
                .ConfigureAwait(false);

            if (package is null)
            {
                log?.Invoke("Halerith client update could not be prepared.");
                return null;
            }

            log?.Invoke($"Halerith client update {version} downloaded and ready.");
            return new PreparedUpdate(launcher, package, relaunchArgs);
        }
        catch (OperationCanceledException)
        {
            log?.Invoke("Halerith update preparation cancelled.");
            return null;
        }
        catch (Exception ex)
        {
            log?.Invoke($"Halerith update preparation failed: {ex.Message}");
            FileLogger.Warn($"Halerith update preparation failed: {ex}");
            return null;
        }
#endif
    }

    public static Task<bool> InstallPreparedAsync(PreparedUpdate update, CancellationToken ct)
    {
        if (PlatformServices.Current.Updates.UsesExternalUpdateFeed)
            return PlatformServices.Current.Updates.CheckForUpdatesAsync(update.Launcher, update.RelaunchArgs, ct);

        return update.Package is null
            ? Task.FromResult(false)
            : PlatformServices.Current.Updates.InstallAsync(update.Package, ct);
    }

    public static async Task<bool> TryRunAsync(
        string clientVersion,
        AppConfig? appConfig,
        string? relaunchArgs,
        Action<string>? log,
        CancellationToken ct,
        Func<PreparedUpdate, CancellationToken, Task<bool>> confirmUpgrade)
    {
        try
        {
            // Keep the caller's UI context for its approval window. Preparation
            // finishes before the countdown; cancellation resumes normal launch.
            var prepared = await TryPrepareAsync(clientVersion, appConfig, relaunchArgs, log, ct);
            if (prepared is null || !await confirmUpgrade(prepared, ct))
                return false;
            ct.ThrowIfCancellationRequested();
            var version = prepared.Version;
            var started = await InstallPreparedAsync(prepared, ct);

            log?.Invoke(started
                ? $"Halerith update helper started for {version}."
                : "Halerith update helper did not start.");

            return started;
        }
        catch (OperationCanceledException)
        {
            log?.Invoke("Halerith update check cancelled.");
            return false;
        }
        catch (Exception ex)
        {
            log?.Invoke($"Halerith update check failed: {ex.Message}");
            FileLogger.Warn($"Halerith update check failed: {ex}");
            return false;
        }
    }

    private static RuntimeConfig CreateUpdateRuntimeConfig(AppConfig? appConfig)
    {
        var appData = LauncherPaths.GetAppDataDirectory();

        return new RuntimeConfig
        {
            TempFolder = Path.Combine(appData, "Temp"),
            StartupTimeoutSeconds = appConfig?.StartupTimeoutSeconds ?? 60,
            EnableEventLogging = appConfig?.EnableEventLogging ?? false,
        };
    }
}
