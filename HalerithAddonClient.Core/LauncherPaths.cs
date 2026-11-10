using HalerithAddonClient.Core.Platform;

namespace HalerithAddonClient.Core;

public static class LauncherPaths
{
    public static string GetInstallDirectory()
        => PlatformServices.Current.Paths.InstallDirectory;

    public static string GetCurrentExecutablePath()
        => PlatformServices.Current.Paths.CurrentExecutablePath;

    public static string GetMainExecutablePath(string? installDirectory = null)
    {
        if (string.IsNullOrWhiteSpace(installDirectory))
            return PlatformServices.Current.Paths.MainExecutablePath;

        return OperatingSystem.IsWindows()
            ? Path.Combine(installDirectory, "HalerithAddonClient.exe")
            : Path.Combine(installDirectory, "HalerithAddonClient");
    }

    /// <summary>
    /// Returns the per-user application data folder used for logs, temp files, etc.
    /// </summary>
    public static string GetAppDataDirectory()
        => PlatformServices.Current.Paths.AppDataDirectory;

    public static string GetCacheDirectory()
        => PlatformServices.Current.Paths.CacheDirectory;

    public static string GetImageCacheDirectory()
        => PlatformServices.Current.Paths.ImageCacheDirectory;
}
