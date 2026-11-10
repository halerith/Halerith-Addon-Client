namespace HalerithAddonClient.Core.ContentSearch;

using System.Text.Json;
using HalerithAddonClient.Core;

/// <summary>
/// Integration with the Modrinth v2 API for searching and fetching mods, resource packs, and shaders.
/// Uses dynamic JSON parsing via JsonDocument.
/// </summary>
public sealed class ModrinthSearchService : IContentSearchService
{
    private const string BaseUrl = "https://api.modrinth.com/v2";
    private static string UserAgent => ProviderContacts.ModrinthUserAgent;
    private const int RequestTimeoutSeconds = 15;
    private const int SearchResultLimit = 20;

    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(RequestTimeoutSeconds),
        DefaultRequestHeaders =
        {
            { "User-Agent", UserAgent }
        }
    };

    public string SourceName => "Modrinth";
    public bool IsAvailable => true;  // Modrinth API requires no authentication

    public async Task<Dictionary<string, ContentSearchResult>> GetProjectsAsync(IEnumerable<string> ids, CancellationToken ct)
    {
        var result = new Dictionary<string, ContentSearchResult>();
        foreach (var chunk in ids.Where(id => id.Length > 0).Distinct().Chunk(100))
        {
            var encoded = Uri.EscapeDataString(JsonSerializer.Serialize(chunk, SerializerOptions.Default));
            using var response = await Http.GetAsync(BaseUrl + "/projects?ids=" + encoded, ct);
            response.EnsureSuccessStatusCode();
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            foreach (var project in json.RootElement.EnumerateArray())
                if (ParseSearchHit(project, GetStringProperty(project, "project_type") ?? "mod") is { } parsed) result[parsed.Id] = parsed;
        }
        return result;
    }

    /// <summary>
    /// Searches for content on Modrinth using the v2 API.
    /// </summary>
    public async Task<List<ContentSearchResult>> SearchAsync(
        string query,
        string contentType,
        string? mcVersion = null,
        string? loader = null,
        CancellationToken ct = default)
    {
        // Map content type to Modrinth project type (note: resource_pack → resourcepack without underscore)
        var projectType = contentType switch
        {
            ContentTypes.Mod => "mod",
            ContentTypes.ResourcePack => "resourcepack",
            ContentTypes.Shader => "shader",
            ContentTypes.Modpack => "modpack",
            _ => "mod"
        };

        // Build facets array: [["project_type:mod"], ["versions:1.21.4"], ["categories:fabric"]]
        var facets = new List<List<string>>
        {
            new() { $"project_type:{projectType}" }
        };

        if (!string.IsNullOrWhiteSpace(mcVersion))
            facets.Add(new List<string> { $"versions:{mcVersion}" });

        // Only apply categories facet for mods (loaders like fabric, forge are categories in Modrinth)
        if (!string.IsNullOrWhiteSpace(loader) && contentType == ContentTypes.Mod)
            facets.Add(new List<string> { $"categories:{loader}" });

        // Serialize facets as JSON-encoded array of arrays
        var facetsJson = JsonSerializer.Serialize(facets, SerializerOptions.Default);
        var url = $"{BaseUrl}/search?query={Uri.EscapeDataString(query)}&facets={Uri.EscapeDataString(facetsJson)}&limit={SearchResultLimit}";

        try
        {
            using var response = await Http.GetAsync(url, ct);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);

            var results = new List<ContentSearchResult>();
            if (doc.RootElement.TryGetProperty("hits", out var hitsArray))
            {
                foreach (var hit in hitsArray.EnumerateArray())
                {
                    var result = ParseSearchHit(hit, projectType);
                    if (result != null)
                        results.Add(result);
                }
            }

            return results;
        }
        catch (HttpRequestException ex)
        {
            System.Diagnostics.Debug.WriteLine($"Modrinth search error: {ex.Message}");
            return [];
        }
    }

    /// <summary>
    /// Fetches available versions for a project on Modrinth.
    /// </summary>
    public async Task<List<ContentVersionResult>> GetVersionsAsync(
        string projectId,
        string? mcVersion = null,
        string? loader = null,
        CancellationToken ct = default)
    {
        // Build query parameters
        var url = $"{BaseUrl}/project/{Uri.EscapeDataString(projectId)}/version";
        var queryParts = new List<string>();

        if (!string.IsNullOrWhiteSpace(mcVersion))
        {
            var versionsJson = JsonSerializer.Serialize(new[] { mcVersion }, SerializerOptions.Default);
            queryParts.Add($"game_versions={Uri.EscapeDataString(versionsJson)}");
        }

        if (!string.IsNullOrWhiteSpace(loader))
        {
            var loadersJson = JsonSerializer.Serialize(new[] { loader }, SerializerOptions.Default);
            queryParts.Add($"loaders={Uri.EscapeDataString(loadersJson)}");
        }

        if (queryParts.Count > 0)
            url += "?" + string.Join("&", queryParts);

        try
        {
            using var response = await Http.GetAsync(url, ct);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);

            var results = new List<ContentVersionResult>();
            foreach (var versionElement in doc.RootElement.EnumerateArray())
            {
                var result = ParseVersionElement(versionElement);
                if (result != null)
                    results.Add(result);
            }

            return results;
        }
        catch (HttpRequestException ex)
        {
            System.Diagnostics.Debug.WriteLine($"Modrinth versions error: {ex.Message}");
            return [];
        }
    }

    /// <summary>
    /// Parses a search hit from the Modrinth API response.
    /// </summary>
    private ContentSearchResult? ParseSearchHit(JsonElement hit, string projectType)
    {
        try
        {
            var id = GetStringProperty(hit, "project_id") ?? GetStringProperty(hit, "id") ?? "";
            var name = GetStringProperty(hit, "title") ?? "";
            var slug = GetStringProperty(hit, "slug") ?? "";
            var description = GetStringProperty(hit, "description") ?? "";
            var iconUrl = GetStringProperty(hit, "icon_url");
            var downloads = GetLongProperty(hit, "downloads") ?? 0;

            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name))
                return null;

            // Build source URL: https://modrinth.com/{project_type}/{slug}
            var sourceUrl = $"https://modrinth.com/{projectType}/{slug}";

            return new ContentSearchResult
            {
                Id = id,
                Source = "modrinth",
                Name = name,
                Author = GetStringProperty(hit, "author") ?? "",
                Slug = slug,
                Description = description,
                IconUrl = iconUrl,
                ContentType = projectType == "resourcepack" ? ContentTypes.ResourcePack :
                              projectType == "shader" ? ContentTypes.Shader : projectType == "modpack" ? ContentTypes.Modpack : ContentTypes.Mod,
                Downloads = downloads,
                SourceUrl = sourceUrl
            };
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Parses a version element from the Modrinth API response.
    /// Picks the primary file from files[] array.
    /// </summary>
    private ContentVersionResult? ParseVersionElement(JsonElement version)
    {
        try
        {
            var versionId = GetStringProperty(version, "id") ?? "";
            var versionName = GetStringProperty(version, "name") ?? "";
            var versionNumber = GetStringProperty(version, "version_number");

            if (string.IsNullOrWhiteSpace(versionId))
                return null;

            // Parse game versions
            var gameVersions = new List<string>();
            if (version.TryGetProperty("game_versions", out var versionsArray))
            {
                foreach (var v in versionsArray.EnumerateArray())
                {
                    if (v.GetString() is string ver)
                        gameVersions.Add(ver);
                }
            }
            var mcVersion = gameVersions.FirstOrDefault();

            // Parse loaders (capitalize first letter)
            var loaders = new List<string>();
            if (version.TryGetProperty("loaders", out var loadersArray))
            {
                foreach (var l in loadersArray.EnumerateArray())
                {
                    if (l.GetString() is string loaderName && !string.IsNullOrWhiteSpace(loaderName))
                    {
                        var capitalized = char.ToUpperInvariant(loaderName[0]) + loaderName[1..];
                        loaders.Add(capitalized);
                    }
                }
            }
            var primaryLoader = loaders.FirstOrDefault();

            // Get primary file (prefer primary=true, fallback to first)
            JsonElement? primaryFile = null;
            if (version.TryGetProperty("files", out var filesArray))
            {
                foreach (var f in filesArray.EnumerateArray())
                {
                    if (f.TryGetProperty("primary", out var primaryProp) && primaryProp.GetBoolean())
                    {
                        primaryFile = f;
                        break;
                    }
                }
                primaryFile ??= filesArray.EnumerateArray().FirstOrDefault();
            }

            if (primaryFile == null)
                return null;

            var pf = primaryFile.Value;
            var downloadUrl = GetStringProperty(pf, "url");
            var filename = GetStringProperty(pf, "filename") ?? "";
            var fileHash = GetFileHash(pf);
            var releasedAt = GetStringProperty(version, "date_published");

            return new ContentVersionResult
            {
                SourceVersionId = versionId,
                VersionName = versionName,
                VersionNumber = versionNumber,
                McVersion = mcVersion,
                GameVersions = gameVersions,
                Loader = primaryLoader,
                Loaders = loaders,
                DownloadUrl = downloadUrl,
                Filename = filename,
                FileHash = fileHash,
                FileSizeBytes = GetLongProperty(pf, "size"),
                ReleasedAt = releasedAt
            };
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Extracts hash from hashes object (prefer SHA-512, fallback to SHA-1).
    /// </summary>
    private static string? GetFileHash(JsonElement file)
    {
        if (!file.TryGetProperty("hashes", out var hashes))
            return null;

        if (hashes.TryGetProperty("sha512", out var sha512) && sha512.GetString() is string hash)
            return hash;

        if (hashes.TryGetProperty("sha1", out var sha1) && sha1.GetString() is string hash1)
            return hash1;

        return null;
    }

    private static string? GetStringProperty(JsonElement element, string propertyName)
    {
        if (element.TryGetProperty(propertyName, out var prop) && prop.ValueKind != JsonValueKind.Null)
            return prop.GetString();
        return null;
    }

    private static long? GetLongProperty(JsonElement element, string propertyName)
    {
        if (element.TryGetProperty(propertyName, out var prop) && prop.ValueKind == JsonValueKind.Number)
            return prop.GetInt64();
        return null;
    }
}
