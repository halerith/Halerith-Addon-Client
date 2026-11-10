using System.Diagnostics;
using HalerithAddonClient.Core.Platform;

namespace HalerithAddonClient.Core;

public enum BuiltInGameSessionState
{
    Launching,
    Running,
}

public sealed record BuiltInGameSessionTarget(
    string Kind,
    string HostId,
    string Id)
{
    public static BuiltInGameSessionTarget Server(string hostId, string serverPublicId) =>
        new("Server", hostId.Trim(), serverPublicId.Trim());

    public static BuiltInGameSessionTarget LocalInstance(string instanceId) =>
        new("LocalInstance", "", instanceId.Trim());
}

public sealed record BuiltInGameSessionRecord(
    BuiltInGameSessionTarget Target,
    int Pid,
    string GameDirectory,
    string MinecraftVersion,
    string Loader,
    string LoaderVersion,
    BuiltInGameSessionState State,
    DateTime StartedAtUtc,
    DateTime? ProcessStartedAtUtc = null);

public static class BuiltInGameSessionStore
{
    public static void Save(BuiltInGameSessionRecord record) =>
        AppStorageDatabase.SaveBuiltInGameSession(record);

    public static void MarkRunning(BuiltInGameSessionTarget target)
    {
        var record = Read(target);
        if (record is null) return;
        Save(record with { State = BuiltInGameSessionState.Running });
    }

    public static BuiltInGameSessionRecord? Read(BuiltInGameSessionTarget target) =>
        AppStorageDatabase.LoadBuiltInGameSession(target);

    public static BuiltInGameSessionRecord? ReadVerified(BuiltInGameSessionTarget target)
    {
        var record = Read(target);
        if (record is null)
            return null;

        if (!IsProcessValidForRecord(record))
        {
            Clear(target);
            return null;
        }

        if (record.State == BuiltInGameSessionState.Launching
            && DateTime.UtcNow - record.StartedAtUtc > TimeSpan.FromSeconds(60))
        {
            record = record with { State = BuiltInGameSessionState.Running };
            Save(record);
        }

        return record;
    }

    public static BuiltInGameSessionRecord? ReadVerifiedFresh(BuiltInGameSessionTarget target)
    {
        var record = Read(target);
        if (record is null)
            return null;

        if (!IsProcessValidForRecord(record) && !IsFreshJavaSession(record))
        {
            Clear(target);
            return null;
        }

        return record;
    }

    public static void Clear(BuiltInGameSessionTarget target) =>
        AppStorageDatabase.ClearBuiltInGameSession(target);

    public static bool TryKill(BuiltInGameSessionTarget target, out string message)
    {
        var record = ReadVerified(target);
        if (record is null)
        {
            Clear(target);
            message = "No running Built-In game session was found.";
            return false;
        }

        try
        {
            var process = Process.GetProcessById(record.Pid);
            process.Kill(entireProcessTree: true);
            Clear(target);
            message = "Game process killed.";
            return true;
        }
        catch (Exception ex)
        {
            Clear(target);
            message = $"Unable to kill game process: {ex.Message}";
            return false;
        }
    }

    public static bool TryKillActiveOrStarting(BuiltInGameSessionTarget target, out string message)
    {
        var record = Read(target);
        if (record is null)
        {
            Clear(target);
            message = "No running Built-In game session was found.";
            return false;
        }

        var verified = IsProcessValidForRecord(record);
        var freshLaunch = IsFreshJavaSession(record);

        if (!verified && !freshLaunch)
        {
            Clear(target);
            message = "No running Built-In game session was found.";
            return false;
        }

        try
        {
            var process = Process.GetProcessById(record.Pid);
            process.Kill(entireProcessTree: true);
            Clear(target);
            message = "Game process killed.";
            return true;
        }
        catch (Exception ex)
        {
            Clear(target);
            message = $"Unable to kill game process: {ex.Message}";
            return false;
        }
    }

    private static bool IsFreshJavaSession(BuiltInGameSessionRecord record)
    {
        return record.State == BuiltInGameSessionState.Launching
            && DateTime.UtcNow - record.StartedAtUtc < TimeSpan.FromMinutes(2)
            && IsJavaProcess(record.Pid);
    }

    public static bool IsProcessValidForRecord(BuiltInGameSessionRecord record)
    {
        try
        {
            var process = Process.GetProcessById(record.Pid);
            if (process.HasExited)
                return false;

            if (!IsJavaProcess(process))
                return false;

            if (ProcessStartTimeMatchesRecord(process, record))
                return true;

            var commandLine = TryGetCommandLine(record.Pid);
            return CommandLineMatchesGameDirectory(commandLine, record.GameDirectory);
        }
        catch
        {
            return false;
        }
    }

    public static bool IsJavaPid(int pid)
        => IsJavaProcess(pid);

    public static DateTime? TryGetProcessStartedAtUtc(Process process)
        => PlatformServices.Current.Processes.TryGetProcessStartedAtUtc(process);

    private static bool IsJavaProcess(int pid)
    {
        try
        {
            return IsJavaProcess(Process.GetProcessById(pid));
        }
        catch
        {
            return false;
        }
    }

    private static bool IsJavaProcess(Process process)
    {
        if (process.HasExited)
            return false;

        var processName = process.ProcessName;
        return processName.Equals("java", StringComparison.OrdinalIgnoreCase)
            || processName.Equals("javaw", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ProcessStartTimeMatchesRecord(Process process, BuiltInGameSessionRecord record)
    {
        if (record.ProcessStartedAtUtc is not { } expected)
            return false;

        var actual = TryGetProcessStartedAtUtc(process);
        if (actual is null)
            return false;

        return DurationBetween(actual.Value, expected) <= TimeSpan.FromSeconds(2);
    }

    private static TimeSpan DurationBetween(DateTime a, DateTime b)
        => a > b ? a - b : b - a;

    private static bool CommandLineMatchesGameDirectory(string? commandLine, string gameDirectory)
    {
        if (string.IsNullOrWhiteSpace(commandLine) || string.IsNullOrWhiteSpace(gameDirectory))
            return false;

        return PlatformServices.Current.Processes.CommandLineContainsPath(commandLine, gameDirectory);
    }

    private static string? TryGetCommandLine(int pid)
        => PlatformServices.Current.Processes.TryGetCommandLine(pid);

}
