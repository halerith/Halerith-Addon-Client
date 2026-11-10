namespace HalerithAddonClient.Core;

/// <summary>
/// Reads and writes the client app configuration from the SQLite storage file.
/// </summary>
public static class AppConfigStore
{
    public static void EnsureRootDefaults()
    {
        AppStorageDatabase.EnsureCreated();
    }

    public static void MaintainStorage(Action<string>? log = null)
    {
        AppStorageDatabase.MaintainStorage(log);
        ImageCacheService.CleanupUnreferencedImages(log);
    }

    public static AppConfig LoadAppConfig()
    {
        return AppStorageDatabase.LoadAppConfig();
    }

    public static void SaveAppConfig(AppConfig config)
    {
        AppStorageDatabase.SaveAppConfig(config);
    }

    public static void ValidateForRuntime(RuntimeConfig config)
    {
        NormalizeDefaults(config);
        if (!string.IsNullOrWhiteSpace(config.TempFolder))
            Directory.CreateDirectory(config.TempFolder);
    }

    private static void NormalizeDefaults(RuntimeConfig config)
    {
        config.ApiBaseUrl = (config.ApiBaseUrl ?? "").Trim().TrimEnd('/');
        config.HostId = (config.HostId ?? "").Trim();
        config.ServerPublicId = (config.ServerPublicId ?? "").Trim();
        config.Username = (config.Username ?? "").Trim();
        config.MinecraftUuid = MinecraftProfileClient.NormalizeUuid(config.MinecraftUuid) ?? "";
        config.MinecraftAccessToken = (config.MinecraftAccessToken ?? "").Trim();
        config.HalerithAccessToken = (config.HalerithAccessToken ?? "").Trim();
        config.HalerithAccessTokenExpiresAtUtc = (config.HalerithAccessTokenExpiresAtUtc ?? "").Trim();
        config.GameDataPath = (config.GameDataPath ?? "").Trim();
        config.ModsFolder = (config.ModsFolder ?? "").Trim();
        config.ResourcePacksFolder = (config.ResourcePacksFolder ?? "").Trim();
        config.ShadersFolder = (config.ShadersFolder ?? "").Trim();
        config.IgnoreRules ??= [];
        config.LocalPinnedItems ??= [];

        var appData = LauncherPaths.GetAppDataDirectory();

        if (string.IsNullOrWhiteSpace(config.TempFolder))
            config.TempFolder = Path.Combine(appData, "Temp");
    }
}
