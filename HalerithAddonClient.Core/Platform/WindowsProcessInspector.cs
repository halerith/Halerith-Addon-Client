#if WINDOWS
using System.Management;

namespace HalerithAddonClient.Core.Platform;

internal static class WindowsProcessInspector
{
    public static string? TryGetCommandLine(int pid)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                $"SELECT CommandLine FROM Win32_Process WHERE ProcessId = {pid}");
            foreach (ManagementObject obj in searcher.Get())
                return obj["CommandLine"] as string;
        }
        catch
        {
        }

        return null;
    }
}
#endif
