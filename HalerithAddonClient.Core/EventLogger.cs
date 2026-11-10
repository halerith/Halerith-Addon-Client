using HalerithAddonClient.Core.Platform;

namespace HalerithAddonClient.Core;

/// <summary>
/// Centralized component logger. The historical name is kept for compatibility,
/// but entries now go to platform-standard diagnostic log files.
/// </summary>
public sealed class EventLogger
{
    private readonly string _componentName;
    private volatile bool _enabled;

    /// <summary>
    /// Creates a new <see cref="EventLogger"/> for the given component.
    /// </summary>
    /// <param name="componentName">
    /// Tag prepended to every message, e.g. "Client", "Launcher", "Install", "Migrate".
    /// </param>
    /// <param name="enabled">Initial enabled state controlled by the diagnostic logging setting.</param>
    public EventLogger(string componentName, bool enabled = true, string? eventSource = null)
    {
        _componentName = componentName;
        _enabled = enabled;
    }

    /// <summary>
    /// Updates the enabled state at runtime (e.g. when the user toggles the setting).
    /// </summary>
    public void SetEnabled(bool enabled) => _enabled = enabled;

    /// <summary>Current enabled state.</summary>
    public bool IsEnabled => _enabled;

    // ── Public API ───────────────────────────────────────────────────────

    public void Info(string message, int eventId)
        => Write(message, "INFO", eventId);

    public void Warn(string message, int eventId)
        => Write(message, "WARN", eventId);

    public void Error(string message, int eventId)
        => Write(message, "ERR ", eventId);

    // ── Internal write ───────────────────────────────────────────────────

    private void Write(string message, string level, int eventId)
    {
        if (!_enabled) return;

        if (level == "WARN")
            PlatformServices.Current.Diagnostics.Warn(_componentName, message, eventId);
        else if (level == "ERR ")
            PlatformServices.Current.Diagnostics.Error(_componentName, message, eventId);
        else
            PlatformServices.Current.Diagnostics.Info(_componentName, message, eventId);
    }

    // ── Convenience: Action<string> adapter for ApiClient traceLog ────────

    /// <summary>
    /// Returns an <see cref="Action{String}"/> suitable for passing as the
    /// <c>traceLog</c> parameter to <see cref="ApiClient"/>.
    /// </summary>
    public Action<string> TraceAction(int eventId)
        => msg => Info(msg, eventId);

    // ── Static helper: export recent entries for diagnostics ──────────────

    /// <summary>
    /// Reads diagnostic log files written within the last <paramref name="hours"/>
    /// hours. Returns them as formatted text.
    /// </summary>
    public static string ExportRecentEntries(int hours = 24)
        => PlatformServices.Current.Diagnostics.ExportRecentEntries(TimeSpan.FromHours(hours));
}
