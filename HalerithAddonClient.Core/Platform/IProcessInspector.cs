using System.Diagnostics;

namespace HalerithAddonClient.Core.Platform;

public interface IProcessInspector
{
    DateTime? TryGetProcessStartedAtUtc(Process process);
    string? TryGetCommandLine(int pid);
    bool CommandLineContainsPath(string? commandLine, string path);
}

internal sealed class DefaultProcessInspector : IProcessInspector
{
    public DateTime? TryGetProcessStartedAtUtc(Process process)
    {
        try
        {
            return process.StartTime.ToUniversalTime();
        }
        catch
        {
            return null;
        }
    }

    public string? TryGetCommandLine(int pid)
    {
        if (OperatingSystem.IsWindows())
#if WINDOWS
            return WindowsProcessInspector.TryGetCommandLine(pid);
#else
            return null;
#endif

        if (OperatingSystem.IsMacOS() || OperatingSystem.IsLinux())
            return TryGetCommandLineWithPs(pid);

        return null;
    }

    public bool CommandLineContainsPath(string? commandLine, string path)
    {
        if (string.IsNullOrWhiteSpace(commandLine) || string.IsNullOrWhiteSpace(path))
            return false;

        var comparison = OperatingSystem.IsLinux()
            ? StringComparison.Ordinal
            : StringComparison.OrdinalIgnoreCase;

        if (OperatingSystem.IsWindows())
        {
            var normalizedCommand = commandLine.Replace('/', '\\');
            var normalizedPath = Path.GetFullPath(path).TrimEnd('\\', '/').Replace('/', '\\');
            return normalizedCommand.Contains(normalizedPath, comparison);
        }

        var fullPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return commandLine.Contains(fullPath, comparison);
    }

    private static string? TryGetCommandLineWithPs(int pid)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "/bin/ps",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("-p");
            psi.ArgumentList.Add(pid.ToString());
            psi.ArgumentList.Add("-o");
            psi.ArgumentList.Add("command=");

            using var process = Process.Start(psi);
            if (process is null)
                return null;

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            return process.ExitCode == 0 ? output.Trim() : null;
        }
        catch
        {
            return null;
        }
    }
}
