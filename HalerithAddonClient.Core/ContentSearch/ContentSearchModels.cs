namespace HalerithAddonClient.Core.ContentSearch;

/// <summary>
/// Normalized search result representing one project from Modrinth or CurseForge.
/// </summary>
public sealed class ContentSearchResult
{
    /// <summary>Modrinth project ID or CurseForge mod ID.</summary>
    public string Id { get; set; } = "";

    /// <summary>"modrinth" or "curseforge".</summary>
    public string Source { get; set; } = "";

    /// <summary>Public author name(s) supplied by the provider, not inferred ownership.</summary>
    public string Author { get; set; } = "";

    [System.Text.Json.Serialization.JsonIgnore]
    public string ProviderByline
    {
        get
        {
            var provider = Source.ToLowerInvariant() switch { "curseforge" => "CurseForge", "modrinth" => "Modrinth", _ => Source };
            return string.IsNullOrWhiteSpace(Author) ? provider : $"{provider} · by {Author.Trim()}";
        }
    }

    /// <summary>Display name of the project.</summary>
    public string Name { get; set; } = "";

    /// <summary>URL-friendly slug (e.g., "my-mod").</summary>
    public string Slug { get; set; } = "";

    /// <summary>Short description of the project.</summary>
    public string Description { get; set; } = "";

    /// <summary>Optional icon/thumbnail URL.</summary>
    public string? IconUrl { get; set; }

    /// <summary>Content type constant from ContentTypes (mod, resource_pack, or shader).</summary>
    public string ContentType { get; set; } = "";

    /// <summary>Total download count.</summary>
    public long Downloads { get; set; }

    /// <summary>Link to the project page on the source platform.</summary>
    public string SourceUrl { get; set; } = "";
}

/// <summary>
/// Normalized version result representing one file/version available for download.
/// </summary>
public sealed class ContentVersionResult
{
    public bool IsServerPack { get; set; }
    /// <summary>Version ID from the source (Modrinth or CurseForge).</summary>
    public string SourceVersionId { get; set; } = "";

    /// <summary>Display name of the version (e.g., "1.0.0", "Release 1").</summary>
    public string VersionName { get; set; } = "";

    /// <summary>Optional version number (e.g., "1.0.0").</summary>
    public string? VersionNumber { get; set; }

    /// <summary>Single Minecraft version if available (e.g., "1.21.4"). May be null when multiple versions are supported.</summary>
    public string? McVersion { get; set; }

    /// <summary>List of supported Minecraft versions.</summary>
    public List<string> GameVersions { get; set; } = [];

    /// <summary>Single loader if applicable (e.g., "fabric"). May be null for multi-loader support.</summary>
    public string? Loader { get; set; }

    /// <summary>List of supported loaders.</summary>
    public List<string> Loaders { get; set; } = [];

    /// <summary>Direct download URL for this file.</summary>
    public string? DownloadUrl { get; set; }

    /// <summary>Filename (e.g., "my-mod-1.0.0+1.21.4.jar").</summary>
    public string Filename { get; set; } = "";

    /// <summary>SHA-1 or SHA-512 hash of the file (hex string), if available.</summary>
    public string? FileHash { get; set; }

    /// <summary>File size in bytes, if available.</summary>
    public long? FileSizeBytes { get; set; }

    /// <summary>ISO 8601 timestamp when this version was released.</summary>
    public string? ReleasedAt { get; set; }

    /// <summary>
    /// Display label for version picker: "{VersionName} for {Loader} {McVersion}".
    /// Gracefully handles missing values.
    /// </summary>
    public string DisplayLabel
    {
        get
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(VersionName))
                parts.Add(VersionName);

            var suffix = new List<string>();
            if (!string.IsNullOrWhiteSpace(Loader))
                suffix.Add(Loader);
            if (!string.IsNullOrWhiteSpace(McVersion))
                suffix.Add(McVersion);

            if (suffix.Count > 0)
            {
                return parts.Count > 0
                    ? $"{string.Join(" ", parts)} for {string.Join(" ", suffix)}"
                    : $"for {string.Join(" ", suffix)}";
            }

            return parts.Count > 0 ? string.Join(" ", parts) : "(unknown)";
        }
    }
}

/// <summary>
/// Service interface for searching and fetching content from a platform (Modrinth or CurseForge).
/// </summary>
public interface IContentSearchService
{
    /// <summary>Display name of the service (e.g., "Modrinth", "CurseForge").</summary>
    string SourceName { get; }

    /// <summary>True if the service is available (e.g., API key is configured for CurseForge).</summary>
    bool IsAvailable { get; }

    /// <summary>
    /// Searches for content matching the given criteria.
    /// </summary>
    /// <param name="query">Search query string (e.g., "fabric-api").</param>
    /// <param name="contentType">Content type filter (ContentTypes constant).</param>
    /// <param name="mcVersion">Optional Minecraft version filter (e.g., "1.21.4").</param>
    /// <param name="loader">Optional loader filter (e.g., "fabric").</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>List of matching projects, up to platform limit (usually 20).</returns>
    Task<List<ContentSearchResult>> SearchAsync(
        string query,
        string contentType,
        string? mcVersion = null,
        string? loader = null,
        CancellationToken ct = default);

    /// <summary>
    /// Fetches available versions for a project.
    /// </summary>
    /// <param name="projectId">Project ID from the source.</param>
    /// <param name="mcVersion">Optional Minecraft version filter.</param>
    /// <param name="loader">Optional loader filter.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>List of versions, newest first.</returns>
    Task<List<ContentVersionResult>> GetVersionsAsync(
        string projectId,
        string? mcVersion = null,
        string? loader = null,
        CancellationToken ct = default);
}
