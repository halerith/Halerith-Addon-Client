using HalerithAddonClient.Core;

namespace HalerithAddonClient.Services;

public static class FallbackSelfUpdateRunner
{
    public static async Task<bool> TryRunAsync(
        string clientVersion,
        AppConfig? appConfig,
        string? relaunchArgs,
        Action<string>? log,
        CancellationToken ct)
    {
        try
        {
            log?.Invoke("Checking Halerith fallback update feed.");

            using var service = new HalerithFallbackUpdateService(new HttpClient
            {
                Timeout = TimeSpan.FromMinutes(10),
            });

            var launcher = await service.CheckAsync(clientVersion, ct).ConfigureAwait(false);
            if (launcher is not { UpdateAvailable: true, FileAvailable: true } ||
                string.IsNullOrWhiteSpace(launcher.DownloadUrl))
            {
                log?.Invoke("No Halerith fallback client update available.");
                return false;
            }

            var version = launcher.Version ?? "new version";
            log?.Invoke($"Halerith fallback client update available: {version}.");

            var runtime = CreateUpdateRuntimeConfig(appConfig);
            var started = await service.RunUpdateIfAvailableAsync(runtime, launcher, ct, relaunchArgs)
                .ConfigureAwait(false);

            log?.Invoke(started
                ? $"Halerith fallback update helper started for {version}."
                : "Halerith fallback update helper did not start.");

            return started;
        }
        catch (OperationCanceledException)
        {
            log?.Invoke("Halerith fallback update check cancelled.");
            return false;
        }
        catch (Exception ex)
        {
            log?.Invoke($"Halerith fallback update check failed: {ex.Message}");
            FileLogger.Warn($"Halerith fallback update check failed: {ex}");
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
