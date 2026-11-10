namespace HalerithAddonClient.Core.ContentSearch;

using System.Text.Json.Serialization;

/// <summary>
/// In-memory projection of content items installed into a local instance.
/// Persisted through SQLite by <see cref="LocalInstanceContentStore"/>.
/// </summary>
public sealed class ContentManifest
{
    /// <summary>Format version for compatibility with future schema changes.</summary>
    [JsonPropertyName("format_version")]
    public int FormatVersion { get; set; } = 1;

    /// <summary>List of installed content items.</summary>
    [JsonPropertyName("items")]
    public List<ContentManifestItem> Items { get; set; } = [];
}

/// <summary>
/// A single content item tracked in the manifest.
/// </summary>
public sealed class ContentManifestItem
{
    /// <summary>"modrinth" or "curseforge".</summary>
    [JsonPropertyName("source")]
    public string Source { get; set; } = "";

    /// <summary>Project/mod ID from the source.</summary>
    [JsonPropertyName("source_id")]
    public string SourceId { get; set; } = "";

    /// <summary>URL-friendly slug.</summary>
    [JsonPropertyName("slug")]
    public string Slug { get; set; } = "";

    /// <summary>Display name.</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    /// <summary>Content type constant (mod, resource_pack, or shader).</summary>
    [JsonPropertyName("content_type")]
    public string ContentType { get; set; } = "";

    /// <summary>Optional icon/thumbnail URL.</summary>
    [JsonPropertyName("icon_url")]
    public string? IconUrl { get; set; }

    /// <summary>Information about the installed version.</summary>
    [JsonPropertyName("installed_version")]
    public ContentManifestVersion InstalledVersion { get; set; } = new();
}

/// <summary>
/// Version information for an installed content item.
/// </summary>
public sealed class ContentManifestVersion
{
    /// <summary>Version ID from the source.</summary>
    [JsonPropertyName("source_version_id")]
    public string SourceVersionId { get; set; } = "";

    /// <summary>Display name of the version.</summary>
    [JsonPropertyName("version_name")]
    public string VersionName { get; set; } = "";

    /// <summary>Filename of the downloaded file.</summary>
    [JsonPropertyName("filename")]
    public string Filename { get; set; } = "";

    /// <summary>Optional hash of the file for integrity verification.</summary>
    [JsonPropertyName("file_hash")]
    public string? FileHash { get; set; }

    /// <summary>Minecraft version this was built for, if applicable.</summary>
    [JsonPropertyName("mc_version")]
    public string? McVersion { get; set; }

    /// <summary>Mod loader (e.g., "Fabric", "Forge"), if applicable.</summary>
    [JsonPropertyName("loader")]
    public string? Loader { get; set; }

    /// <summary>Download URL for the file (for re-download/verification).</summary>
    [JsonPropertyName("download_url")]
    public string? DownloadUrl { get; set; }

    /// <summary>ISO 8601 timestamp when this item was installed.</summary>
    [JsonPropertyName("installed_at")]
    public string InstalledAt { get; set; } = "";
}
