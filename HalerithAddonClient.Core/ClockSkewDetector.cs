namespace HalerithAddonClient.Core;

/// <summary>
/// Checks the panel's unauthenticated health timestamp to detect large client
/// clock skew that can break timestamp-bound request authentication.
/// </summary>
public static class ClockSkewDetector
{
    public const int WarningThresholdSeconds = 60;

    public static async Task<ClockSkewCheckResult> CheckAsync(
        string apiBaseUrl,
        int startupTimeoutSeconds,
        string? clientVersion,
        bool ignoreCertificateErrors = false,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(apiBaseUrl))
            return ClockSkewCheckResult.Inconclusive();

        var tempConfig = new RuntimeConfig
        {
            ApiBaseUrl = apiBaseUrl.Trim().TrimEnd('/'),
            IgnoreCertificateErrors = ignoreCertificateErrors,
            StartupTimeoutSeconds = startupTimeoutSeconds,
        };

        using var apiClient = new ApiClient(tempConfig);
        var health = await apiClient.GetHostHealthAsync(ct, clientVersion).ConfigureAwait(false);
        if (!health.Ok || string.IsNullOrWhiteSpace(health.GeneratedAtUtc))
            return ClockSkewCheckResult.Inconclusive();

        if (!DateTimeOffset.TryParse(health.GeneratedAtUtc, out var serverTime))
            return ClockSkewCheckResult.Inconclusive();

        var skew = serverTime - DateTimeOffset.UtcNow;
        var absoluteSkew = skew.Duration();
        if (absoluteSkew < TimeSpan.FromSeconds(WarningThresholdSeconds))
            return ClockSkewCheckResult.NoSkewDetected();

        return ClockSkewCheckResult.Warning(
            $"Your computer clock differs from the server by about {FormatDuration(absoluteSkew)}. Correct your system time and try again.");
    }

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration.TotalHours >= 1.5)
            return $"{Math.Round(duration.TotalHours):0} hours";

        if (duration.TotalHours >= 1)
            return "1 hour";

        if (duration.TotalMinutes >= 1.5)
            return $"{Math.Round(duration.TotalMinutes):0} minutes";

        if (duration.TotalMinutes >= 1)
            return "1 minute";

        return $"{Math.Max(1, (int)Math.Round(duration.TotalSeconds)):0} seconds";
    }
}

public readonly record struct ClockSkewCheckResult(bool ProbeSucceeded, string? WarningMessage)
{
    public bool HasClockSkewWarning => !string.IsNullOrWhiteSpace(WarningMessage);

    public static ClockSkewCheckResult Inconclusive() => new(false, null);

    public static ClockSkewCheckResult NoSkewDetected() => new(true, null);

    public static ClockSkewCheckResult Warning(string message) => new(true, message);
}
