using System.Diagnostics;

namespace HalerithAddonClient.Core.Platform;

public interface IPlatformPaths
{
    string AppDataDirectory { get; }
    string CacheDirectory { get; }
    string ImageCacheDirectory { get; }
    string LogsDirectory { get; }
    string TempDirectory { get; }
    string RuntimeDirectory { get; }
    string InstallDirectory { get; }
    string CurrentExecutablePath { get; }
    string MainExecutablePath { get; }
}

internal sealed class DefaultPlatformPaths : IPlatformPaths
{
#if HALERITH_SANDBOX_BUILD
    private const string AppFolderName = "Halerith Addon Client Sandbox";
#else
    private const string AppFolderName = "Halerith Addon Client";
#endif

    public string AppDataDirectory { get; } = ResolveAppDataDirectory();
    public string CacheDirectory => Path.Combine(AppDataDirectory, "Cache");
    public string ImageCacheDirectory => Path.Combine(CacheDirectory, "Images");
    public string LogsDirectory => Path.Combine(AppDataDirectory, "Logs");
    public string TempDirectory => Path.Combine(AppDataDirectory, "Temp");
    public string RuntimeDirectory => Path.Combine(AppDataDirectory, "Runtime");
    public string InstallDirectory => AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    public string CurrentExecutablePath =>
        Process.GetCurrentProcess().MainModule?.FileName
        ?? throw new InvalidOperationException("Unable to determine current executable path.");

    public string MainExecutablePath
    {
        get
        {
            if (OperatingSystem.IsMacOS())
                return ResolveMacMainExecutablePath();
            if (OperatingSystem.IsLinux())
                return Path.Combine(InstallDirectory, "HalerithAddonClient");

            return Path.Combine(InstallDirectory, "HalerithAddonClient.exe");
        }
    }

    private static string ResolveAppDataDirectory()
    {
        if (OperatingSystem.IsMacOS())
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrWhiteSpace(home))
                return Path.Combine(home, "Library", "Application Support", AppFolderName);
        }

        if (OperatingSystem.IsLinux())
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrWhiteSpace(home))
                return Path.Combine(home, ".halerith-addon-client");
        }

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(localAppData))
            return Path.Combine(localAppData, AppFolderName);

        return Path.Combine(AppContext.BaseDirectory, AppFolderName);
    }

    private string ResolveMacMainExecutablePath()
    {
        var current = CurrentExecutablePath;
        var contentsIndex = current.IndexOf(".app/Contents/MacOS/", StringComparison.OrdinalIgnoreCase);
        if (contentsIndex >= 0)
            return current;

        return Path.Combine(InstallDirectory, "HalerithAddonClient");
    }
}
