using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace HalerithAddonClient.Core.Platform;

public interface IDiagnosticsService
{
    void SetEnabled(bool enabled);
    bool IsEnabled { get; }
    void Info(string component, string message, int eventId = 0);
    void Warn(string component, string message, int eventId = 0);
    void Error(string component, string message, int eventId = 0);
    string ExportRecentEntries(TimeSpan maxAge);
    Task<string> GetSystemSummaryAsync(CancellationToken ct = default);
}

internal sealed class FileDiagnosticsService : IDiagnosticsService
{
    private const long MaxLogFileSizeBytes = 2 * 1024 * 1024;
    private readonly IPlatformPaths _paths;
    private readonly object _lock = new();
    private volatile bool _enabled;

    public FileDiagnosticsService(IPlatformPaths paths)
    {
        _paths = paths;
    }

    public void SetEnabled(bool enabled) => _enabled = enabled;
    public bool IsEnabled => _enabled;

    public void Info(string component, string message, int eventId = 0) =>
        Write("INFO", component, message, eventId);

    public void Warn(string component, string message, int eventId = 0) =>
        Write("WARN", component, message, eventId);

    public void Error(string component, string message, int eventId = 0) =>
        Write("ERR ", component, message, eventId);

    public string ExportRecentEntries(TimeSpan maxAge)
    {
        try
        {
            var cutoff = DateTimeOffset.UtcNow - maxAge;
            var builder = new StringBuilder();
            var hasRecentEntries = false;
            foreach (var file in Directory.EnumerateFiles(_paths.LogsDirectory, "*.log", SearchOption.TopDirectoryOnly)
                         .OrderBy(Path.GetFileName))
            {
                var includedEntry = false;
                var wroteHeader = false;
                foreach (var line in File.ReadLines(file))
                {
                    if (TryReadEntryTimestamp(line, out var timestamp))
                        includedEntry = timestamp >= cutoff;

                    if (!includedEntry)
                        continue;

                    if (!wroteHeader)
                    {
                        builder.AppendLine($"===== {DiagnosticDataRedactor.Redact(Path.GetFileName(file))} =====");
                        wroteHeader = true;
                    }
                    builder.AppendLine(DiagnosticDataRedactor.Redact(line));
                    hasRecentEntries = true;
                }
                if (wroteHeader)
                    builder.AppendLine();
            }

            return hasRecentEntries
                ? builder.ToString()
                : $"No diagnostic log files found in the last {maxAge.TotalHours:0.#} hour(s).";
        }
        catch
        {
            return "Failed to read diagnostic logs.";
        }
    }

    private static bool TryReadEntryTimestamp(string line, out DateTimeOffset timestamp)
    {
        timestamp = default;
        if (line.Length == 0 || line[0] != '[')
            return false;

        var end = line.IndexOf(']');
        return end > 1 && end + 2 < line.Length && line[end + 1] == ' '
            && line[end + 2] == '['
            && DateTimeOffset.TryParse(line.AsSpan(1, end - 1), CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out timestamp);
    }

    public async Task<string> GetSystemSummaryAsync(CancellationToken ct = default)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Halerith Addon Client system diagnostics");
        builder.AppendLine($"GeneratedAtUtc: {DateTimeOffset.UtcNow:O}");
        builder.AppendLine($"ClientVersion: {ClientVersion()}");
        builder.AppendLine($"DotNetRuntime: {RuntimeInformation.FrameworkDescription}");
        builder.AppendLine($"OperatingSystem: {RuntimeInformation.OSDescription}");
        builder.AppendLine($"OsArchitecture: {RuntimeInformation.OSArchitecture}");
        builder.AppendLine($"ProcessArchitecture: {RuntimeInformation.ProcessArchitecture}");
        builder.AppendLine($"LogicalProcessorCount: {Environment.ProcessorCount}");

        AppendClientProcessMetrics(builder);
        AppendRuntimeInstanceCount(builder);
        AppendDiskMetrics(builder);

        var memory = await ReadMemoryMetricsAsync(ct).ConfigureAwait(false);
        AppendBytes(builder, "PhysicalMemoryTotalBytes", memory?.TotalBytes);
        AppendBytes(builder, "PhysicalMemoryUsedBytes", memory?.UsedBytes);
        AppendBytes(builder, "PhysicalMemoryAvailableBytes", memory?.AvailableBytes);

        AppendJavaMetrics(builder);
        return builder.ToString();
    }

    private void Write(string level, string component, string message, int eventId)
    {
        if (!_enabled)
            return;

        try
        {
            Directory.CreateDirectory(_paths.LogsDirectory);
            var safeComponent = SanitizeComponent(component);
            var path = Path.Combine(_paths.LogsDirectory, $"{safeComponent}.log");
            var suffix = eventId > 0 ? $" (ID:{eventId})" : "";
            var line = $"[{DateTime.UtcNow:O}] [{level}] [{component}]{suffix} {message}{Environment.NewLine}";

            lock (_lock)
            {
                RotateIfNeeded(path);
                File.AppendAllText(path, line, Encoding.UTF8);
            }
        }
        catch
        {
            // Logging is best effort and must never block the app.
        }
    }

    private static string SanitizeComponent(string component)
    {
        var value = string.IsNullOrWhiteSpace(component) ? "client" : component.Trim().ToLowerInvariant();
        foreach (var c in Path.GetInvalidFileNameChars())
            value = value.Replace(c, '-');
        return value.Replace(' ', '-');
    }

    private static void RotateIfNeeded(string path)
    {
        if (!File.Exists(path) || new FileInfo(path).Length <= MaxLogFileSizeBytes)
            return;

        var oldPath = path + ".old";
        try { if (File.Exists(oldPath)) File.Delete(oldPath); } catch { }
        try { File.Move(path, oldPath, overwrite: true); } catch { }
    }

    private static string ClientVersion()
    {
        var assembly = Assembly.GetEntryAssembly() ?? typeof(FileDiagnosticsService).Assembly;
        return assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString()
            ?? "Unknown";
    }

    private static void AppendClientProcessMetrics(StringBuilder builder)
    {
        try
        {
            using var process = Process.GetCurrentProcess();
            builder.AppendLine($"ClientProcessUptimeSeconds: {Math.Max(0, (long)(DateTime.UtcNow - process.StartTime.ToUniversalTime()).TotalSeconds)}");
            builder.AppendLine($"ClientWorkingSetBytes: {process.WorkingSet64}");
            builder.AppendLine($"ClientPrivateMemoryBytes: {process.PrivateMemorySize64}");
        }
        catch
        {
            builder.AppendLine("ClientProcessUptimeSeconds: Unknown");
            builder.AppendLine("ClientWorkingSetBytes: Unknown");
            builder.AppendLine("ClientPrivateMemoryBytes: Unknown");
        }
    }

    private void AppendRuntimeInstanceCount(StringBuilder builder)
    {
        try
        {
            var root = Path.Combine(_paths.RuntimeDirectory, "instances");
            builder.AppendLine($"ConfiguredRuntimeInstanceCount: {(Directory.Exists(root) ? Directory.EnumerateDirectories(root).Count() : 0)}");
        }
        catch
        {
            builder.AppendLine("ConfiguredRuntimeInstanceCount: Unknown");
        }
    }

    private void AppendDiskMetrics(StringBuilder builder)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(_paths.AppDataDirectory));
            if (string.IsNullOrWhiteSpace(root))
                throw new InvalidOperationException();

            var drive = new DriveInfo(root);
            if (!drive.IsReady)
                throw new InvalidOperationException();

            builder.AppendLine($"ClientDataVolumeTotalBytes: {drive.TotalSize}");
            builder.AppendLine($"ClientDataVolumeUsedBytes: {Math.Max(0, drive.TotalSize - drive.AvailableFreeSpace)}");
            builder.AppendLine($"ClientDataVolumeAvailableBytes: {drive.AvailableFreeSpace}");
        }
        catch
        {
            builder.AppendLine("ClientDataVolumeTotalBytes: Unknown");
            builder.AppendLine("ClientDataVolumeUsedBytes: Unknown");
            builder.AppendLine("ClientDataVolumeAvailableBytes: Unknown");
        }
    }

    private static void AppendJavaMetrics(StringBuilder builder)
    {
        var count = 0;
        long workingSet = 0;
        long privateMemory = 0;
        long largestWorkingSet = 0;
        Process[] processes = [];
        try
        {
            processes = Process.GetProcesses();
            foreach (var process in processes)
            {
                try
                {
                    if (!string.Equals(process.ProcessName, "java", StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(process.ProcessName, "javaw", StringComparison.OrdinalIgnoreCase))
                        continue;

                    count++;
                    workingSet += Math.Max(0, process.WorkingSet64);
                    privateMemory += Math.Max(0, process.PrivateMemorySize64);
                    largestWorkingSet = Math.Max(largestWorkingSet, process.WorkingSet64);
                }
                catch
                {
                    // A process may exit or become inaccessible while metrics are read.
                }
            }
        }
        catch
        {
            // Report a safe zero/false result when process enumeration is unavailable.
        }
        finally
        {
            foreach (var process in processes)
                process.Dispose();
        }

        builder.AppendLine($"JavaRunning: {count > 0}");
        builder.AppendLine($"JavaProcessCount: {count}");
        builder.AppendLine($"JavaWorkingSetBytes: {workingSet}");
        builder.AppendLine($"JavaPrivateMemoryBytes: {privateMemory}");
        builder.AppendLine($"JavaLargestWorkingSetBytes: {largestWorkingSet}");
    }

    private static void AppendBytes(StringBuilder builder, string name, long? value) =>
        builder.AppendLine($"{name}: {(value.HasValue ? value.Value.ToString(CultureInfo.InvariantCulture) : "Unknown")}");

    private static async Task<MemoryMetrics?> ReadMemoryMetricsAsync(CancellationToken ct)
    {
        if (OperatingSystem.IsWindows())
        {
            var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
            return GlobalMemoryStatusEx(ref status)
                ? CreateMemoryMetrics(status.TotalPhysical, status.AvailablePhysical)
                : null;
        }

        if (OperatingSystem.IsLinux())
        {
            try
            {
                var values = File.ReadLines("/proc/meminfo")
                    .Select(line => line.Split(':', 2, StringSplitOptions.TrimEntries))
                    .Where(parts => parts.Length == 2)
                    .ToDictionary(parts => parts[0], parts => parts[1], StringComparer.Ordinal);
                if (TryParseKilobytes(values.GetValueOrDefault("MemTotal"), out var total)
                    && TryParseKilobytes(values.GetValueOrDefault("MemAvailable"), out var available))
                    return CreateMemoryMetrics((ulong)total, (ulong)available);
            }
            catch
            {
                return null;
            }
        }

        if (OperatingSystem.IsMacOS())
        {
            var totalText = await RunProcessOutputAsync("/usr/sbin/sysctl", "-n hw.memsize", ct).ConfigureAwait(false);
            var vmText = await RunProcessOutputAsync("/usr/bin/vm_stat", "", ct).ConfigureAwait(false);
            if (ulong.TryParse(totalText?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var total)
                && TryParseMacAvailableMemory(vmText, out var available))
                return CreateMemoryMetrics(total, available);
        }

        return null;
    }

    private static MemoryMetrics CreateMemoryMetrics(ulong total, ulong available)
    {
        var safeTotal = (long)Math.Min(total, long.MaxValue);
        var safeAvailable = (long)Math.Min(Math.Min(available, total), long.MaxValue);
        return new MemoryMetrics(safeTotal, safeTotal - safeAvailable, safeAvailable);
    }

    private static bool TryParseKilobytes(string? value, out long bytes)
    {
        var number = value?.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (long.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out var kilobytes))
        {
            bytes = checked(kilobytes * 1024);
            return true;
        }

        bytes = 0;
        return false;
    }

    private static bool TryParseMacAvailableMemory(string? output, out ulong bytes)
    {
        bytes = 0;
        if (string.IsNullOrWhiteSpace(output))
            return false;

        var pageSize = 4096UL;
        var pageMatch = System.Text.RegularExpressions.Regex.Match(output, @"page size of (\d+) bytes", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (pageMatch.Success
            && ulong.TryParse(pageMatch.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsedPageSize))
            pageSize = parsedPageSize;

        ulong availablePages = 0;
        foreach (var line in output.Split('\n'))
        {
            if (!line.StartsWith("Pages free", StringComparison.Ordinal)
                && !line.StartsWith("Pages inactive", StringComparison.Ordinal)
                && !line.StartsWith("Pages speculative", StringComparison.Ordinal))
                continue;

            var value = line.Split(':', 2).ElementAtOrDefault(1)?.Trim().TrimEnd('.');
            if (ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var pages))
                availablePages += pages;
        }

        bytes = availablePages * pageSize;
        return availablePages > 0;
    }

    private static async Task<string?> RunProcessOutputAsync(string fileName, string arguments, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using var proc = Process.Start(psi);
            if (proc is null)
                return "";

            var output = await proc.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
            var error = await proc.StandardError.ReadToEndAsync(ct).ConfigureAwait(false);
            await proc.WaitForExitAsync(ct).ConfigureAwait(false);
            return proc.ExitCode == 0 && string.IsNullOrWhiteSpace(error) ? output : null;
        }
        catch
        {
            return null;
        }
    }

    private readonly record struct MemoryMetrics(long TotalBytes, long UsedBytes, long AvailableBytes);

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhysical;
        public ulong AvailablePhysical;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);
}
