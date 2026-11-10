namespace HalerithAddonClient.Core;

internal static class JavaRuntimePaths
{
    public static string PreferredExecutablePath(string runtimeRoot, string component)
    {
        var javaDir = Path.Combine(runtimeRoot, "java", component);

        if (OperatingSystem.IsWindows())
            return Path.Combine(javaDir, "bin", "java.exe");

        if (OperatingSystem.IsMacOS())
            return Path.Combine(javaDir, "jre.bundle", "Contents", "Home", "bin", "java");

        return Path.Combine(javaDir, "bin", "java");
    }

    public static string? FindExecutable(string runtimeRoot, string component)
    {
        foreach (var path in CandidateExecutablePaths(runtimeRoot, component))
        {
            if (IsUsableExecutable(path))
                return path;
        }

        return null;
    }

    public static bool IsUsableExecutable(string path)
    {
        if (!File.Exists(path))
            return false;

        if (OperatingSystem.IsWindows())
            return true;

        try
        {
            var mode = File.GetUnixFileMode(path);
            return (mode & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0;
        }
        catch
        {
            return true;
        }
    }

    public static string? FindAnyExecutable(string runtimeRoot)
    {
        var javaRoot = Path.Combine(runtimeRoot, "java");
        if (!Directory.Exists(javaRoot))
            return null;

        foreach (var dir in Directory.GetDirectories(javaRoot))
        {
            var component = Path.GetFileName(dir);
            if (string.IsNullOrWhiteSpace(component))
                continue;

            var java = FindExecutable(runtimeRoot, component);
            if (java is not null)
                return java;
        }

        return null;
    }

    private static IEnumerable<string> CandidateExecutablePaths(string runtimeRoot, string component)
    {
        var javaDir = Path.Combine(runtimeRoot, "java", component);

        yield return PreferredExecutablePath(runtimeRoot, component);
        yield return Path.Combine(javaDir, "bin", "java.exe");
        yield return Path.Combine(javaDir, "bin", "java");
        yield return Path.Combine(javaDir, "jre.bundle", "Contents", "Home", "bin", "java");
    }
}
