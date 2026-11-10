using System.IO.Compression;
using System.Text;
using HalerithAddonClient.Core.Platform;

namespace HalerithAddonClient.Windows;

internal sealed class DiagnosticPackageWorkspace : IDisposable
{
    private const long MaxZipBytes = 25L * 1024L * 1024L;
    private const long MaxAttachmentBytes = 1L * 1024L * 1024L;
    private const long MaxGameLogBytes = 4L * 1024L * 1024L;

    public string RootDirectory { get; }
    public string SourceDirectory { get; }
    public string ZipName { get; } = $"{Guid.NewGuid():N}.zip";
    public string ZipPath => Path.Combine(RootDirectory, ZipName);

    public DiagnosticPackageWorkspace(string? tempDirectory = null)
    {
        RootDirectory = Path.Combine(tempDirectory ?? Path.GetTempPath(), $"halerith-diag-{Guid.NewGuid():N}");
        SourceDirectory = Path.Combine(RootDirectory, "source");
        Directory.CreateDirectory(SourceDirectory);
    }

    public void WriteGeneratedText(string fileName, string text)
    {
        if (fileName is not ("diagnostics.txt" or "halerith-services.txt" or "sysinfo.txt"
            or "minecraft-latest.log" or "minecraft-console.log" or "minecraft-debug.log"
            or "minecraft-crash-1.txt" or "minecraft-crash-2.txt"))
            throw new ArgumentException("Unknown diagnostic file.", nameof(fileName));

        File.WriteAllText(Path.Combine(SourceDirectory, fileName),
            DiagnosticDataRedactor.Redact(text), Encoding.UTF8);
    }

    public void AddGameLogs(string gameDirectory)
    {
        var logsDirectory = Path.Combine(gameDirectory, "logs");
        var crashDirectory = Path.Combine(gameDirectory, "crash-reports");
        if (!Directory.Exists(gameDirectory) || new DirectoryInfo(gameDirectory).LinkTarget is not null)
            throw new InvalidOperationException("The selected instance logs are no longer available.");

        var included = 0;
        if (Directory.Exists(logsDirectory) && new DirectoryInfo(logsDirectory).LinkTarget is null)
        {
            foreach (var (sourceName, packageName) in new[]
            {
                ("latest.log", "minecraft-latest.log"),
                ("launcher-output.log", "minecraft-console.log"),
                ("debug.log", "minecraft-debug.log")
            })
            {
                if (AddGameLogFile(Path.Combine(logsDirectory, sourceName), packageName))
                    included++;
            }
        }

        if (Directory.Exists(crashDirectory) && new DirectoryInfo(crashDirectory).LinkTarget is null)
        {
            var recentCrashes = Directory.EnumerateFiles(crashDirectory, "crash-*.txt", SearchOption.TopDirectoryOnly)
                .Where(path => new FileInfo(path).LinkTarget is null)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .Take(2);
            var index = 0;
            foreach (var crash in recentCrashes)
            {
                index++;
                if (AddGameLogFile(crash, $"minecraft-crash-{index}.txt"))
                    included++;
            }
        }

        if (included == 0)
            throw new InvalidOperationException("No Minecraft logs or crash reports were found for the selected instance.");
    }

    private bool AddGameLogFile(string source, string packageName)
    {
        if (!File.Exists(source) || new FileInfo(source).LinkTarget is not null)
            return false;

        using var stream = new FileStream(source, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length > MaxGameLogBytes)
            stream.Seek(-MaxGameLogBytes, SeekOrigin.End);
        var buffer = new byte[(int)MaxGameLogBytes];
        var count = 0;
        while (count < buffer.Length)
        {
            var read = stream.Read(buffer, count, buffer.Length - count);
            if (read == 0) break;
            count += read;
        }
        WriteGeneratedText(packageName, Encoding.UTF8.GetString(buffer, 0, count));
        return true;
    }

    public void CreateZip(string? attachmentPath)
    {
        using (var zip = ZipFile.Open(ZipPath, ZipArchiveMode.Create))
        {
            foreach (var file in Directory.EnumerateFiles(SourceDirectory, "*", SearchOption.TopDirectoryOnly))
            {
                zip.CreateEntryFromFile(file, Path.GetFileName(file), CompressionLevel.Optimal);
                ThrowIfTooLarge();
            }

            if (attachmentPath is not null)
            {
                var attachment = new FileInfo(attachmentPath);
                if (!attachment.Exists)
                    throw new FileNotFoundException("The selected attachment is no longer available.");
                if (attachment.Length > MaxAttachmentBytes)
                    throw new InvalidOperationException("The selected attachment exceeds the 1 MB limit.");

                zip.CreateEntryFromFile(attachment.FullName,
                    "attachment_" + attachment.Name, CompressionLevel.Optimal);
                ThrowIfTooLarge();
            }
        }

        ThrowIfTooLarge();
    }

    private void ThrowIfTooLarge()
    {
        if (new FileInfo(ZipPath).Length > MaxZipBytes)
            throw new InvalidOperationException("Diagnostic package is larger than the 25 MB upload limit.");
    }

    public void Dispose()
    {
        try { Directory.Delete(RootDirectory, recursive: true); }
        catch (DirectoryNotFoundException) { }
        catch (Exception)
        {
            LauncherLog.Warn("Could not remove temporary diagnostic files.");
        }
    }
}
