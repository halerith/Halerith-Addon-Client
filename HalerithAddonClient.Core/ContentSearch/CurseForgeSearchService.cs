namespace HalerithAddonClient.Core.ContentSearch;

using System.Text.Json;
using HalerithAddonClient.Core;

/// <summary>
/// Integration with the CurseForge v1 API for searching and fetching mods, resource packs, and shaders.
/// Uses an embedded app key when present, otherwise the user's protected local key.
/// Uses dynamic JSON parsing via JsonDocument.
/// </summary>
public sealed class CurseForgeSearchService : IContentSearchService
{
    private const string BaseUrl = "https://api.curseforge.com/v1/";
    private static string UserAgent => ProviderContacts.CurseForgeUserAgent;
    private const int RequestTimeoutSeconds = 15;
    private const int GameId = 432;  // Minecraft game ID
    private const int SearchResultLimit = 20;

    private readonly string _apiKey = CurseForgeApiKeyStore.GetEffectiveKey();

    private static readonly HttpClient Http = new(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
    {
        Timeout = TimeSpan.FromSeconds(RequestTimeoutSeconds),
        DefaultRequestHeaders =
        {
            { "User-Agent", UserAgent }
        }
    };

    public string SourceName => "CurseForge";

    /// <summary>
    /// Returns true only if the API key is configured.
    /// </summary>
    public bool IsAvailable => !string.IsNullOrWhiteSpace(_apiKey);

    /// <summary>
    /// Searches for content on CurseForge using the v1 API.
    /// </summary>
    public async Task<List<ContentSearchResult>> SearchAsync(
        string query,
        string contentType,
        string? mcVersion = null,
        string? loader = null,
        CancellationToken ct = default)
    {
        if (!IsAvailable)
            return [];

        // Map content type to CurseForge class ID
        var classId = contentType switch
        {
            ContentTypes.Mod => 6,
            ContentTypes.ResourcePack => 12,
            ContentTypes.Shader => 6552,
            ContentTypes.Modpack => 4471,
            _ => 6
        };

        // Map loader to CurseForge mod loader type ID
        var loaderType = MapLoaderType(loader);

        var url = $"{BaseUrl}mods/search?gameId={GameId}&searchFilter={Uri.EscapeDataString(query)}&classId={classId}&sortField=2&sortOrder=desc&pageSize={SearchResultLimit}";

        if (!string.IsNullOrWhiteSpace(mcVersion))
            url += $"&gameVersion={Uri.EscapeDataString(mcVersion)}";

        if (loaderType >= 0)
            url += $"&modLoaderType={loaderType}";

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("X-API-Key", _apiKey);

            using var response = await Http.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);

            var results = new List<ContentSearchResult>();
            if (doc.RootElement.TryGetProperty("data", out var dataArray))
            {
                foreach (var mod in dataArray.EnumerateArray())
                {
                    var result = ParseSearchResult(mod, contentType);
                    if (result != null)
                        results.Add(result);
                }
            }

            return results;
        }
        catch (HttpRequestException ex)
        {
            System.Diagnostics.Debug.WriteLine($"CurseForge search error: {ex.Message}");
            return [];
        }
    }

    /// <summary>
    /// Fetches available versions for a project on CurseForge.
    /// </summary>
    public async Task<List<ContentVersionResult>> GetVersionsAsync(
        string projectId,
        string? mcVersion = null,
        string? loader = null,
        CancellationToken ct = default)
    {
        if (!IsAvailable)
            return [];

        var loaderType = MapLoaderType(loader);

        var url = $"{BaseUrl}mods/{Uri.EscapeDataString(projectId)}/files?gameId={GameId}&pageSize=50&sortOrder=desc";

        if (!string.IsNullOrWhiteSpace(mcVersion))
            url += $"&gameVersion={Uri.EscapeDataString(mcVersion)}";

        if (loaderType >= 0)
            url += $"&modLoaderType={loaderType}";

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("X-API-Key", _apiKey);

            using var response = await Http.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);

            var results = new List<ContentVersionResult>();
            if (doc.RootElement.TryGetProperty("data", out var filesArray))
            {
                foreach (var file in filesArray.EnumerateArray())
                {
                    var result = ParseVersionElement(file);
                    if (result != null)
                        results.Add(result);
                }
            }

            return results;
        }
        catch (HttpRequestException ex)
        {
            System.Diagnostics.Debug.WriteLine($"CurseForge versions error: {ex.Message}");
            return [];
        }
    }

    /// <summary>
    /// Parses a search result from the CurseForge API response.
    /// </summary>
    private ContentSearchResult? ParseSearchResult(JsonElement mod, string contentType)
    {
        try
        {
            var id = GetInt32Property(mod, "id")?.ToString() ?? "";
            var name = GetStringProperty(mod, "name") ?? "";
            var slug = GetStringProperty(mod, "slug") ?? "";
            var summary = GetStringProperty(mod, "summary") ?? "";
            string? sourceUrl = null;

            // Get thumbnail URL from logo object
            string? iconUrl = null;
            if (mod.TryGetProperty("logo", out var logo))
                iconUrl = GetStringProperty(logo, "thumbnailUrl");

            var downloads = GetInt64Property(mod, "downloadCount") ?? 0;

            // Extract website URL from links
            if (mod.TryGetProperty("links", out var links))
                sourceUrl = GetStringProperty(links, "websiteUrl");

            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name))
                return null;

            return new ContentSearchResult
            {
                Id = id,
                Source = "curseforge",
                Name = name,
                Author = mod.TryGetProperty("authors", out var authors) && authors.ValueKind == JsonValueKind.Array
                    ? string.Join(", ", authors.EnumerateArray().Select(a => GetStringProperty(a, "name")?.Trim()).Where(n => !string.IsNullOrWhiteSpace(n)).Distinct())
                    : "",
                Slug = slug,
                Description = summary,
                IconUrl = iconUrl,
                ContentType = contentType,
                Downloads = downloads,
                SourceUrl = sourceUrl ?? $"https://www.curseforge.com/minecraft/mc-mods/{slug}"
            };
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Maps loader name to CurseForge modLoaderType integer. Returns -1 for unrecognized.
    /// </summary>
    private static int MapLoaderType(string? loader) => loader?.ToLowerInvariant() switch
    {
        "forge" => 1,
        "fabric" => 4,
        "quilt" => 5,
        "neoforge" => 6,
        _ => -1
    };

    /// <summary>
    /// Parses a file/version element from the CurseForge API response.
    /// Splits gameVersions into MC versions (start with digit) and loaders.
    /// </summary>
    private ContentVersionResult? ParseVersionElement(JsonElement file)
    {
        try
        {
            var versionId = GetInt64Property(file, "id")?.ToString() ?? "";
            var displayName = GetStringProperty(file, "displayName") ?? "";
            var downloadUrl = GetStringProperty(file, "downloadUrl");
            var fileName = GetStringProperty(file, "fileName") ?? "";
            var fileLength = GetInt64Property(file, "fileLength");
            var fileDate = GetStringProperty(file, "fileDate");

            if (string.IsNullOrWhiteSpace(versionId))
                return null;

            // Extract hash (algorithm 1 = SHA-1)
            string? fileHash = null;
            if (file.TryGetProperty("hashes", out var hashesArray))
            {
                foreach (var hashObj in hashesArray.EnumerateArray())
                {
                    if (GetInt32Property(hashObj, "algo") == 1)
                    {
                        fileHash = GetStringProperty(hashObj, "value");
                        break;
                    }
                }
            }

            // Parse game versions and loaders from gameVersions array
            var gameVersions = new List<string>();
            var loaders = new List<string>();

            if (file.TryGetProperty("gameVersions", out var gameVersionsArray))
            {
                foreach (var v in gameVersionsArray.EnumerateArray())
                {
                    if (v.GetString() is string version && !string.IsNullOrWhiteSpace(version))
                    {
                        // Version starts with digit = Minecraft version; otherwise = loader
                        if (char.IsDigit(version[0]))
                            gameVersions.Add(version);
                        else if (!version.StartsWith("Java", StringComparison.OrdinalIgnoreCase))
                            loaders.Add(version);
                    }
                }
            }

            var mcVersion = gameVersions.FirstOrDefault();
            var loader = loaders.FirstOrDefault();

            return new ContentVersionResult
            {
                SourceVersionId = versionId,
                VersionName = displayName,
                IsServerPack = file.TryGetProperty("isServerPack", out var serverPack) && serverPack.ValueKind == JsonValueKind.True,
                VersionNumber = null,  // CurseForge doesn't provide explicit version number
                McVersion = mcVersion,
                GameVersions = gameVersions,
                Loader = loader,
                Loaders = loaders,
                DownloadUrl = downloadUrl,
                Filename = fileName,
                FileHash = fileHash,
                FileSizeBytes = fileLength,
                ReleasedAt = fileDate
            };
        }
        catch
        {
            return null;
        }
    }

    public async Task<(ContentSearchResult Project, ContentVersionResult Version)> GetExactFileAsync(string projectId, string fileId, CancellationToken ct)
    {
        if (!IsAvailable) throw new InvalidOperationException("CurseForge is not configured.");
        async Task<JsonDocument> Read(string path)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, BaseUrl + path);
            request.Headers.Add("X-API-Key", _apiKey);
            using var response = await Http.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();
            return JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        }
        using var project = await Read($"mods/{Uri.EscapeDataString(projectId)}");
        using var file = await Read($"mods/{Uri.EscapeDataString(projectId)}/files/{Uri.EscapeDataString(fileId)}");
        var data = project.RootElement.GetProperty("data");
        var type = GetInt32Property(data, "classId") switch { 12 => ContentTypes.ResourcePack, 6552 => ContentTypes.Shader, 4471 => ContentTypes.Modpack, _ => ContentTypes.Mod };
        return (ParseSearchResult(data, type) ?? throw new InvalidDataException("Invalid CurseForge project."),
            ParseVersionElement(file.RootElement.GetProperty("data")) ?? throw new InvalidDataException("Invalid CurseForge file."));
    }

    public async Task<Dictionary<string, (ContentSearchResult Project, ContentVersionResult Version)>> GetPackFilesAsync(
        IReadOnlyList<(long ProjectId, long FileId)> requested, CancellationToken ct)
    {
        if (!IsAvailable) throw new InvalidOperationException("CurseForge is not configured.");
        async Task<List<JsonElement>> Fetch(string endpoint, string key, IEnumerable<long> ids)
        {
            var result = new List<JsonElement>();
            foreach (var chunk in ids.Distinct().Chunk(50))
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, BaseUrl + endpoint);
                request.Headers.Add("X-API-Key", _apiKey);
                request.Content = new StringContent(JsonSerializer.Serialize(new Dictionary<string, object> { [key] = chunk }, SerializerOptions.Default), System.Text.Encoding.UTF8, "application/json");
                using var response = await Http.SendAsync(request, ct);
                response.EnsureSuccessStatusCode();
                using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
                result.AddRange(json.RootElement.GetProperty("data").EnumerateArray().Select(e => e.Clone()));
            }
            return result;
        }
        var projects = (await Fetch("mods", "modIds", requested.Select(r => r.ProjectId))).ToDictionary(e => e.GetProperty("id").GetInt64());
        var files = (await Fetch("mods/files", "fileIds", requested.Select(r => r.FileId))).ToDictionary(e => e.GetProperty("id").GetInt64());
        var resolved = new Dictionary<string, (ContentSearchResult, ContentVersionResult)>();
        foreach (var item in requested)
        {
            if (!projects.TryGetValue(item.ProjectId, out var project) || !files.TryGetValue(item.FileId, out var file)
                || file.GetProperty("modId").GetInt64() != item.ProjectId) continue;
            var type = GetInt32Property(project, "classId") switch { 12 => ContentTypes.ResourcePack, 6552 => ContentTypes.Shader, _ => ContentTypes.Mod };
            if (ParseSearchResult(project, type) is { } p && ParseVersionElement(file) is { } v) resolved[item.FileId.ToString()] = (p, v);
        }
        return resolved;
    }

    private static string? GetStringProperty(JsonElement element, string propertyName)
    {
        if (element.TryGetProperty(propertyName, out var prop) && prop.ValueKind != JsonValueKind.Null)
            return prop.GetString();
        return null;
    }

    private static int? GetInt32Property(JsonElement element, string propertyName)
    {
        if (element.TryGetProperty(propertyName, out var prop) && prop.ValueKind == JsonValueKind.Number)
            return prop.GetInt32();
        return null;
    }

    private static long? GetInt64Property(JsonElement element, string propertyName)
    {
        if (element.TryGetProperty(propertyName, out var prop) && prop.ValueKind == JsonValueKind.Number)
            return prop.GetInt64();
        return null;
    }
}
