using HalerithAddonClient.Core;

namespace HalerithAddonClient.GuidedTips;

internal static class GuidedTipProgressStore
{
    private const string CompletedSettingKey = "guided_tips_completed";
    private static readonly object Sync = new();
    private static bool _postponedForSession;

    internal static event EventHandler? ResetRequested;
    internal static event EventHandler<bool>? EnabledChanged;

    internal static bool IsComplete(string id)
    {
        lock (Sync)
            return GuidedTipProgressCodec.Decode(AppStorageDatabase.GetAppSetting(CompletedSettingKey)).Contains(id);
    }

    internal static bool IsPostponedForSession()
    {
        lock (Sync)
            return _postponedForSession;
    }

    internal static void Complete(string id)
    {
        lock (Sync)
        {
            var completed = GuidedTipProgressCodec.Decode(AppStorageDatabase.GetAppSetting(CompletedSettingKey));
            if (completed.Add(id))
                AppStorageDatabase.SaveAppSetting(CompletedSettingKey, GuidedTipProgressCodec.Encode(completed));
        }
    }

    internal static void PostponeForSession()
    {
        lock (Sync)
            _postponedForSession = true;
    }

    internal static void Restart()
    {
        lock (Sync)
        {
            AppStorageDatabase.SaveAppSetting(CompletedSettingKey, "");
            _postponedForSession = false;
        }
        ResetRequested?.Invoke(null, EventArgs.Empty);
    }

    internal static void NotifyEnabledChanged(bool enabled)
        => EnabledChanged?.Invoke(null, enabled);
}

internal static class GuidedTipProgressCodec
{
    internal static HashSet<string> Decode(string? value)
        => (value ?? "")
            .Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(IsValidId)
            .ToHashSet(StringComparer.Ordinal);

    internal static string Encode(IEnumerable<string> values)
        => string.Join('|', values.Where(IsValidId).Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal));

    private static bool IsValidId(string value)
        => value.Length is > 0 and <= 80 && value.All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '.' or '-');
}
