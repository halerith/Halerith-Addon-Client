using HalerithAddonClient.Core;

namespace HalerithAddonClient.Services;

internal static class RuntimeBuildCoordinator
{
    public static ActiveBuildTracker.BuildState StartOrGetBuild(
        string mcVersion,
        string loader,
        string loaderVersion)
    {
        var existing = ActiveBuildTracker.Get(mcVersion, loader, loaderVersion);
        if (existing is not null && !existing.BuildTask.IsCompleted)
            return existing;

        if (existing is not null)
            ActiveBuildTracker.Remove(mcVersion, loader, loaderVersion);

        var startGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var buildTask = Task.Run(async () =>
        {
            await startGate.Task.ConfigureAwait(false);
            await RunBuildPipelineAsync(mcVersion, loader, loaderVersion).ConfigureAwait(false);
        });

        var state = ActiveBuildTracker.Register(mcVersion, loader, loaderVersion, buildTask);
        startGate.SetResult(true);
        return state;
    }

    internal static async Task RunBuildPipelineAsync(string mcVersion, string loader, string loaderVersion)
    {
        var state = ActiveBuildTracker.Get(mcVersion, loader, loaderVersion);
        if (state is null) return;

        try
        {
            CleanPartialLoaderInstall(loader, loaderVersion);

            var runtimeRoot = Path.Combine(LauncherPaths.GetAppDataDirectory(), "Runtime");
            using var db = new RuntimeDatabase(runtimeRoot);
            using var builder = new GameEnvironmentBuilder(database: db);

            var report = await builder.CheckReadinessAsync(mcVersion, loader, loaderVersion);
            if (report.IsReady)
            {
                await builder.RecordInstanceAsync(report);
                state.Completed = Math.Max(state.Total, 1);
                state.Total = Math.Max(state.Total, 1);
                state.Label = "Already ready";
                return;
            }

            state.Total = Math.Max(report.MissingCount, 1);

            var progress = new Progress<(int completed, int total, string label)>(p =>
            {
                state.Completed = p.completed;
                state.Total = Math.Max(p.total, 1);
                state.Label = string.IsNullOrWhiteSpace(p.label)
                    ? $"{p.completed}/{p.total}"
                    : $"{p.completed}/{p.total} {p.label}";
            });

            await builder.BuildAsync(report, progress);

            state.Label = "Recording instance...";
            var finalReport = await builder.CheckReadinessAsync(mcVersion, loader, loaderVersion);
            await builder.RecordInstanceAsync(finalReport);

            state.Total = Math.Max(state.Total, 1);
            state.Completed = state.Total;
            state.Label = "Complete";
        }
        catch (Exception ex)
        {
            state.Error = ex.Message;
            state.Label = $"Build failed: {ex.Message}";
        }
    }

    private static void CleanPartialLoaderInstall(string loader, string loaderVersion)
    {
        var loaderLower = loader.Trim().ToLowerInvariant();
        if (loaderLower is not ("forge" or "neoforge") || string.IsNullOrWhiteSpace(loaderVersion))
            return;

        try
        {
            using var installer = new ForgeInstallerService();
            if (installer.IsPartialInstall(loaderLower, loaderVersion))
            {
                installer.CleanInstall(loaderLower, loaderVersion);
                FileLogger.Info($"[Build] Cleaned partial {loader} install for {loaderVersion}");
            }
        }
        catch (Exception ex)
        {
            FileLogger.Warn($"[Build] Failed to clean partial install: {ex.Message}");
        }
    }
}
