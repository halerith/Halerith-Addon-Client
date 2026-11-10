namespace HalerithAddonClient.Core;

public sealed record ClientDatabaseVersionCheck(
    bool IsCompatible,
    string StoredVersion,
    string RunningVersion);

public static class ClientDatabaseVersionGuard
{
    private const string SettingKey = "client_db_version";

    public static ClientDatabaseVersionCheck CheckAndUpdate(string runningVersion)
    {
        runningVersion = NormalizeVersionText(runningVersion);
        if (string.IsNullOrWhiteSpace(runningVersion))
            runningVersion = "0.0.0.0";

        var storedVersion = NormalizeVersionText(AppStorageDatabase.GetAppSetting(SettingKey));

        if (string.IsNullOrWhiteSpace(storedVersion) || IsUnset(storedVersion))
        {
            AppStorageDatabase.SaveAppSetting(SettingKey, runningVersion);
            return new ClientDatabaseVersionCheck(true, storedVersion, runningVersion);
        }

        var comparison = CompareVersions(storedVersion, runningVersion);
        if (comparison > 0)
            return new ClientDatabaseVersionCheck(false, storedVersion, runningVersion);

        if (!string.Equals(storedVersion, runningVersion, StringComparison.OrdinalIgnoreCase))
            AppStorageDatabase.SaveAppSetting(SettingKey, runningVersion);

        return new ClientDatabaseVersionCheck(true, storedVersion, runningVersion);
    }

    private static bool IsUnset(string version)
    {
        var trimmed = version.Trim();
        return trimmed == "0" || trimmed == "0.0" || trimmed == "0.0.0" || trimmed == "0.0.0.0";
    }

    private static int CompareVersions(string left, string right)
    {
        var leftParts = ParseParts(left);
        var rightParts = ParseParts(right);

        for (var i = 0; i < 4; i++)
        {
            var comparison = leftParts[i].CompareTo(rightParts[i]);
            if (comparison != 0)
                return comparison;
        }

        return 0;
    }

    private static int[] ParseParts(string version)
    {
        var parts = new int[4];
        var numeric = version.Split('+', 2)[0].Split('-', 2)[0];
        var rawParts = numeric.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        for (var i = 0; i < Math.Min(parts.Length, rawParts.Length); i++)
        {
            if (int.TryParse(rawParts[i], out var value) && value >= 0)
                parts[i] = value;
        }

        return parts;
    }

    private static string NormalizeVersionText(string? version)
        => (version ?? "").Trim();
}
