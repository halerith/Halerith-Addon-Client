using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace HalerithAddonClient.Services;

internal static class FolderOpenService
{
    public static bool TryOpenFolder(string path, out string message)
    {
        message = "";

        if (string.IsNullOrWhiteSpace(path))
        {
            message = "Folder path is empty.";
            return false;
        }

        try
        {
            var fullPath = Path.GetFullPath(path);
            Directory.CreateDirectory(fullPath);

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                Start("explorer.exe", fullPath);
                return true;
            }

            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                Start("open", fullPath);
                return true;
            }

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                if (TryStart("xdg-open", fullPath, out var linuxError))
                    return true;

                if (TryStart("gio", "open", fullPath, out linuxError))
                    return true;

                message = linuxError ?? "No Linux file manager opener was found.";
                return false;
            }

            message = "Opening folders is not supported on this platform.";
            return false;
        }
        catch (Exception ex)
        {
            message = $"Could not open folder: {ex.Message}";
            return false;
        }
    }

    private static void Start(string fileName, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
        };

        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        Process.Start(startInfo);
    }

    private static bool TryStart(string fileName, string argument, out string? error)
        => TryStart(fileName, [argument], out error);

    private static bool TryStart(string fileName, string firstArgument, string secondArgument, out string? error)
        => TryStart(fileName, [firstArgument, secondArgument], out error);

    private static bool TryStart(string fileName, string[] arguments, out string? error)
    {
        try
        {
            Start(fileName, arguments);
            error = null;
            return true;
        }
        catch (Win32Exception ex)
        {
            error = ex.Message;
            return false;
        }
        catch (FileNotFoundException ex)
        {
            error = ex.Message;
            return false;
        }
    }
}
