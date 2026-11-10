namespace HalerithAddonClient.Core;

public static class HttpsUrlPolicy
{
    public static Uri RequireHttpsAbsoluteUri(string? url, string label)
    {
        if (!TryNormalizeHttpsUrl(url, out var normalized, out var error, label))
            throw new InvalidOperationException(error);

        return new Uri(normalized, UriKind.Absolute);
    }

    public static Uri RequireHttpOrHttpsAbsoluteUri(string? url, string label)
    {
        if (!TryNormalizeHttpOrHttpsUrl(url, out var normalized, out var error, label))
            throw new InvalidOperationException(error);

        return new Uri(normalized, UriKind.Absolute);
    }

    public static bool TryNormalizeHttpsUrl(string? url, out string normalized, out string error, string label = "URL")
    {
        normalized = "";
        error = "";

        var trimmed = (url ?? string.Empty).Trim().TrimEnd('/');
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            error = $"{label} is required.";
            return false;
        }

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
        {
            error = $"{label} must be an absolute HTTPS URL.";
            return false;
        }

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            error = $"{label} must use HTTPS.";
            return false;
        }

        normalized = uri.ToString().TrimEnd('/');
        return true;
    }

    public static bool TryNormalizeHttpOrHttpsUrl(string? url, out string normalized, out string error, string label = "URL")
    {
        normalized = "";
        error = "";

        var trimmed = (url ?? string.Empty).Trim().TrimEnd('/');
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            error = $"{label} is required.";
            return false;
        }

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
        {
            error = $"{label} must be an absolute HTTP or HTTPS URL.";
            return false;
        }

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
        {
            error = $"{label} must use HTTP or HTTPS.";
            return false;
        }

        normalized = uri.ToString().TrimEnd('/');
        return true;
    }
}
