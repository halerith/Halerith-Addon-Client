namespace HalerithAddonClient.Core;

public sealed record EulaDocument(
    string Product,
    string Version,
    string EffectiveDate,
    string Title,
    string Html,
    string ContentHash,
    bool IsRemote)
{
    public bool MateriallyAdverse { get; init; }
    public string? ChangeSummary { get; init; }
    public static EulaDocument PackagedClient() => new(
        "client",
        EulaContent.Version,
        EulaContent.EffectiveDate,
        "End User License Agreement",
        EulaContent.NormalizedHtml,
        Sha256Hex(EulaContent.NormalizedHtml),
        false);

    public static string Sha256Hex(string input)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(input ?? "");
        var hash = System.Security.Cryptography.SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
