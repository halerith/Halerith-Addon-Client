using System.Text.RegularExpressions;

namespace HalerithAddonClient.Core.Platform;

/// <summary>
/// Removes common credentials and personal identifiers from Client-owned
/// diagnostic events before they leave the device.
/// </summary>
public static partial class DiagnosticDataRedactor
{
    public static string Redact(string value)
    {
        if (string.IsNullOrEmpty(value))
            return value;

        var redacted = BearerTokenRegex().Replace(value, "$1[redacted]");
        redacted = SensitiveAssignmentRegex().Replace(redacted, "$1$2[redacted]");
        redacted = EmailRegex().Replace(redacted, "[email]");
        redacted = WindowsUserPathRegex().Replace(redacted, "$1[profile]\\");
        redacted = UnixUserPathRegex().Replace(redacted, "$1[profile]/");
        redacted = UrlHostRegex().Replace(redacted, "$1[host]");
        redacted = IpV4Regex().Replace(redacted, "[ip-address]");
        redacted = IpV6Regex().Replace(redacted, "[ip-address]");

        return redacted;
    }

    [GeneratedRegex(@"(?i)\b(authorization\s*[:=]\s*bearer\s+)[^\s,;]+")]
    private static partial Regex BearerTokenRegex();

    [GeneratedRegex("""(?i)\b(password|passwd|secret|token|cookie|signature|credential|api[-_ ]?key|authorization(?:[-_ ]?code)?|user(?:name|_name)?|player(?:name|_name)?)\b(\s*[:=]\s*|\s+)(?:"[^"]*"|'[^']*'|[^\s,;]+)""")]
    private static partial Regex SensitiveAssignmentRegex();

    [GeneratedRegex(@"(?i)(?<![\w.+-])[\w.+-]+@[\w.-]+\.[a-z]{2,}(?![\w.-])")]
    private static partial Regex EmailRegex();

    [GeneratedRegex(@"(?i)(\b[a-z]:\\users\\)[^\\\r\n]+\\")]
    private static partial Regex WindowsUserPathRegex();

    [GeneratedRegex(@"(?i)(/(?:home|users)/)[^/\r\n]+/")]
    private static partial Regex UnixUserPathRegex();

    [GeneratedRegex(@"(?i)\b((?:https?|sftp|ftps?|scp)://)(?:\[[0-9a-f:]+\]|[^/\s:]+)(?::\d+)?")]
    private static partial Regex UrlHostRegex();

    [GeneratedRegex(@"(?<![\d.])(?:25[0-5]|2[0-4]\d|1?\d?\d)(?:\.(?:25[0-5]|2[0-4]\d|1?\d?\d)){3}(?![\d.])")]
    private static partial Regex IpV4Regex();

    [GeneratedRegex(@"(?i)(?<![0-9a-f:])(?:(?:[0-9a-f]{1,4}:){1,7}:[0-9a-f]{0,4}|(?:[0-9a-f]{1,4}:){2,7}[0-9a-f]{1,4})(?![0-9a-f:])")]
    private static partial Regex IpV6Regex();
}
