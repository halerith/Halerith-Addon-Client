using HalerithAddonClient.Core;

namespace HalerithAddonClient;

/// <summary>
/// Static convenience wrapper around <see cref="EventLogger"/> for the main
/// client application ("Client" component). Entries are written to platform
/// diagnostic log files.
/// </summary>
internal static class LauncherLog
{
    private static readonly EventLogger Logger = new("Client", enabled: true);

    public static void Configure(bool enabled)
        => Logger.SetEnabled(enabled);

    /// <summary>
    /// Writes an informational diagnostic event.
    /// Default event ID is <see cref="EventIds.Client.Started"/>.
    /// </summary>
    public static void Write(string message, int eventId = EventIds.Client.Started)
        => Logger.Info(message, eventId);

    /// <summary>
    /// Writes a warning diagnostic event.
    /// </summary>
    public static void Warn(string message, int eventId = EventIds.Client.Warning)
        => Logger.Warn(message, eventId);

    /// <summary>
    /// Writes an error diagnostic event.
    /// </summary>
    public static void Error(string message, int eventId = EventIds.Client.Error)
        => Logger.Error(message, eventId);

    /// <summary>
    /// Returns an <see cref="Action{String}"/> for use as the ApiClient traceLog
    /// parameter.
    /// </summary>
    public static Action<string> TraceAction()
        => Logger.TraceAction(EventIds.Client.ApiTrace);
}
