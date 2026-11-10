namespace HalerithAddonClient.Core;

/// <summary>
/// Thrown when a sync/update is requested but the active server's configuration
/// is missing one or more required fields (e.g. the Game Data folder has not
/// been set yet on a newly-added host).
///
/// The message on this exception is intentionally user-facing: callers can
/// surface it directly in the UI footer without wrapping it in a "failed:"
/// prefix. This replaces the previous behaviour of leaking the low-level
/// <see cref="ArgumentException"/> from <c>Path.Combine</c> /
/// <c>Directory.CreateDirectory</c> with its "Value cannot be an empty string.
/// (Parameter 'path')" text, which is meaningless to end users.
/// </summary>
public sealed class ConfigurationIncompleteException : InvalidOperationException
{
    public ConfigurationIncompleteException(string message) : base(message) { }
}
