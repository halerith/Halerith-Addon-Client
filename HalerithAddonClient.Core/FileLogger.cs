using HalerithAddonClient.Core.Platform;

namespace HalerithAddonClient.Core;

/// <summary>
/// Simple file-based debug logger that writes timestamped lines to
/// the platform diagnostics log folder.
///
/// Thread-safety: all public members are safe to call from any thread.
/// </summary>
public static class FileLogger
{
    private static volatile bool _enabled;

    /// <summary>
    /// Initialises the file logger.  Must be called once during startup
    /// after <see cref="AppConfigStore.LoadAppConfig"/> returns.
    /// Creates the Logs directory if it doesn't exist and truncates an
    /// oversized log file.
    /// </summary>
    /// <param name="enabled">Initial enabled state from the diagnostics setting.</param>
    public static void Initialise(bool enabled)
    {
        _enabled = enabled;
        PlatformServices.Current.Diagnostics.SetEnabled(enabled);
        Info("FileLogger initialised");
    }

    /// <summary>
    /// Updates the enabled state at runtime (e.g. when the user toggles
    /// diagnostic logging in the UI).
    /// </summary>
    public static void SetEnabled(bool enabled)
    {
        _enabled = enabled;
        PlatformServices.Current.Diagnostics.SetEnabled(enabled);
    }

    /// <summary>Current enabled state.</summary>
    public static bool IsEnabled => _enabled;

    // ── Public API ───────────────────────────────────────────────────────

    /// <summary>Writes an informational line.</summary>
    public static void Info(string message) => Write("INFO", message);

    /// <summary>Writes a warning line.</summary>
    public static void Warn(string message) => Write("WARN", message);

    /// <summary>Writes an error line.</summary>
    public static void Error(string message) => Write("ERR ", message);

    /// <summary>Writes an error line with exception details.</summary>
    public static void Error(string message, Exception ex)
        => Write("ERR ", $"{message}: {ex}");

    // ── Internal ─────────────────────────────────────────────────────────

    private static void Write(string level, string message)
    {
        if (!_enabled) return;

        if (level == "WARN")
            PlatformServices.Current.Diagnostics.Warn("debug", message);
        else if (level == "ERR ")
            PlatformServices.Current.Diagnostics.Error("debug", message);
        else
            PlatformServices.Current.Diagnostics.Info("debug", message);
    }
}
