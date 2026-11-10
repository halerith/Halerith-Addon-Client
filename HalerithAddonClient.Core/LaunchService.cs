namespace HalerithAddonClient.Core;

public enum LaunchMode
{
    Direct
}

public sealed class LaunchPlan
{
    public LaunchMode Mode { get; init; }
    public string FileName { get; init; } = "";
    public string Arguments { get; init; } = "";
    public string WorkingDirectory { get; init; } = "";
    public string Description { get; init; } = "";
}

public static class LaunchService
{
    public static bool IsLauncherConfigured(RuntimeConfig config, ServerConfig? server, out string reason)
    {
        var mcVersion = server?.MinecraftVersion ?? "";
        if (string.IsNullOrWhiteSpace(mcVersion))
        {
            reason = "Server version unknown - refresh to retrieve server info.";
            return false;
        }

        var runtimeRoot = Path.Combine(LauncherPaths.GetAppDataDirectory(), "Runtime");
        if (!File.Exists(AppStorageDatabase.StoragePath))
        {
            reason = "Game environment not built. Open Settings -> Instance tab and click Build.";
            return false;
        }

        try
        {
            using var db = new RuntimeDatabase(runtimeRoot);
            var loader = server?.Loader ?? "";
            var loaderVersion = server?.LoaderVersion ?? "";
            if (!db.InstanceExists(mcVersion, loader, loaderVersion))
            {
                reason = "Game environment not built for this server. Open Settings -> Instance tab and click Build.";
                return false;
            }
        }
        catch
        {
            reason = "Unable to check game environment status.";
            return false;
        }

        reason = "";
        return true;
    }
}
