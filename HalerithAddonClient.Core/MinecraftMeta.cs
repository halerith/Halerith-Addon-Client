using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HalerithAddonClient.Core;

// ══════════════════════════════════════════════════════════════════════════════
//  Mojang Version Manifest models
// ══════════════════════════════════════════════════════════════════════════════

/// <summary>Top-level version manifest from piston-meta.mojang.com.</summary>
public sealed class MojangVersionManifest
{
    [JsonPropertyName("latest")] public MojangLatestVersions? Latest { get; set; }
    [JsonPropertyName("versions")] public List<MojangVersionEntry> Versions { get; set; } = [];
}

public sealed class MojangLatestVersions
{
    [JsonPropertyName("release")] public string Release { get; set; } = "";
    [JsonPropertyName("snapshot")] public string Snapshot { get; set; } = "";
}

public sealed class MojangVersionEntry
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("type")] public string Type { get; set; } = "";
    [JsonPropertyName("url")] public string Url { get; set; } = "";
    [JsonPropertyName("sha1")] public string Sha1 { get; set; } = "";
}

// ══════════════════════════════════════════════════════════════════════════════
//  Mojang Version Detail (per-version JSON)
// ══════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Full version detail JSON for a single Minecraft release.
/// Specifies libraries, arguments, main class, assets, and downloads.
/// </summary>
public sealed class MojangVersionDetail
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("type")] public string Type { get; set; } = "";
    [JsonPropertyName("mainClass")] public string MainClass { get; set; } = "";
    [JsonPropertyName("assets")] public string Assets { get; set; } = "";
    [JsonPropertyName("assetIndex")] public MojangAssetIndex? AssetIndex { get; set; }
    [JsonPropertyName("downloads")] public MojangDownloads? Downloads { get; set; }
    [JsonPropertyName("libraries")] public List<MojangLibrary> Libraries { get; set; } = [];
    [JsonPropertyName("arguments")] public MojangArguments? Arguments { get; set; }

    /// <summary>Legacy pre-1.13 argument string (flattened).</summary>
    [JsonPropertyName("minecraftArguments")] public string? MinecraftArguments { get; set; }

    [JsonPropertyName("javaVersion")] public MojangJavaVersion? JavaVersion { get; set; }

    /// <summary>Logging configuration (not needed for launching but present in the JSON).</summary>
    [JsonPropertyName("logging")] public JsonElement? Logging { get; set; }
}

public sealed class MojangAssetIndex
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("sha1")] public string Sha1 { get; set; } = "";
    [JsonPropertyName("size")] public long Size { get; set; }
    [JsonPropertyName("totalSize")] public long TotalSize { get; set; }
    [JsonPropertyName("url")] public string Url { get; set; } = "";
}

public sealed class MojangDownloads
{
    [JsonPropertyName("client")] public MojangDownloadArtifact? Client { get; set; }
    [JsonPropertyName("server")] public MojangDownloadArtifact? Server { get; set; }
    [JsonPropertyName("client_mappings")] public MojangDownloadArtifact? ClientMappings { get; set; }
}

public sealed class MojangDownloadArtifact
{
    [JsonPropertyName("sha1")] public string Sha1 { get; set; } = "";
    [JsonPropertyName("size")] public long Size { get; set; }
    [JsonPropertyName("url")] public string Url { get; set; } = "";
}

public sealed class MojangLibrary
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("downloads")] public MojangLibraryDownloads? Downloads { get; set; }
    [JsonPropertyName("rules")] public List<MojangRule>? Rules { get; set; }
    [JsonPropertyName("natives")] public Dictionary<string, string>? Natives { get; set; }

    /// <summary>Fabric/loader libraries use a plain URL instead of the Mojang artifact structure.</summary>
    [JsonPropertyName("url")] public string? Url { get; set; }

    /// <summary>
    /// Evaluates the OS rules for this library against the current platform.
    /// Returns true if the library should be included on this OS.
    /// </summary>
    public bool AppliesToCurrentOs()
    {
        if (Rules is null || Rules.Count == 0) return true;

        var osName = GetCurrentOsName();
        bool allowed = false;

        foreach (var rule in Rules)
        {
            if (rule.Os is null)
            {
                allowed = string.Equals(rule.Action, "allow", StringComparison.OrdinalIgnoreCase);
            }
            else if (string.Equals(rule.Os.Name, osName, StringComparison.OrdinalIgnoreCase))
            {
                allowed = string.Equals(rule.Action, "allow", StringComparison.OrdinalIgnoreCase);
            }
        }
        return allowed;
    }

    /// <summary>
    /// Converts the Maven coordinate (group:artifact:version[:classifier]) into
    /// a relative filesystem path (e.g. "org/lwjgl/lwjgl/3.3.1/lwjgl-3.3.1.jar").
    /// </summary>
    public string ToRelativePath()
    {
        // If Mojang-style artifact path is available, prefer it.
        if (Downloads?.Artifact?.Path is { Length: > 0 } artifactPath)
            return artifactPath;

        // Otherwise derive from Maven coordinate: group:artifact:version[:classifier]
        var parts = Name.Split(':');
        if (parts.Length < 3) return Name.Replace(':', '/') + ".jar";

        var group = parts[0].Replace('.', '/');
        var artifact = parts[1];
        var version = parts[2];
        var classifier = parts.Length > 3 ? "-" + parts[3] : "";
        return $"{group}/{artifact}/{version}/{artifact}-{version}{classifier}.jar";
    }

    /// <summary>Resolves the download URL for this library.</summary>
    public string? GetDownloadUrl()
    {
        // Mojang-style explicit download URL
        if (Downloads?.Artifact?.Url is { Length: > 0 } url)
            return url;

        // Fabric/loader-style: base URL + Maven path
        var baseUrl = Url ?? "https://maven.fabricmc.net/";
        return baseUrl.TrimEnd('/') + "/" + ToRelativePath();
    }

    private static string GetCurrentOsName()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return "windows";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) return "linux";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) return "osx";
        return "unknown";
    }
}

public sealed class MojangLibraryDownloads
{
    [JsonPropertyName("artifact")] public MojangLibraryArtifact? Artifact { get; set; }
    [JsonPropertyName("classifiers")] public Dictionary<string, MojangLibraryArtifact>? Classifiers { get; set; }
}

public sealed class MojangLibraryArtifact
{
    [JsonPropertyName("path")] public string Path { get; set; } = "";
    [JsonPropertyName("sha1")] public string Sha1 { get; set; } = "";
    [JsonPropertyName("size")] public long Size { get; set; }
    [JsonPropertyName("url")] public string Url { get; set; } = "";
}

public sealed class MojangRule
{
    [JsonPropertyName("action")] public string Action { get; set; } = "";
    [JsonPropertyName("os")] public MojangOsCondition? Os { get; set; }
}

public sealed class MojangOsCondition
{
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("version")] public string? Version { get; set; }
    [JsonPropertyName("arch")] public string? Arch { get; set; }
}

public sealed class MojangArguments
{
    [JsonPropertyName("game")] public List<JsonElement> Game { get; set; } = [];
    [JsonPropertyName("jvm")] public List<JsonElement> Jvm { get; set; } = [];
}

public sealed class MojangJavaVersion
{
    [JsonPropertyName("component")] public string Component { get; set; } = "";
    [JsonPropertyName("majorVersion")] public int MajorVersion { get; set; }
}

// ══════════════════════════════════════════════════════════════════════════════
//  Mojang Asset Index
// ══════════════════════════════════════════════════════════════════════════════

/// <summary>Parsed asset index JSON — maps logical names to hash/size objects.</summary>
public sealed class MojangAssetIndexDetail
{
    [JsonPropertyName("objects")] public Dictionary<string, MojangAssetObject> Objects { get; set; } = new();
}

public sealed class MojangAssetObject
{
    [JsonPropertyName("hash")] public string Hash { get; set; } = "";
    [JsonPropertyName("size")] public long Size { get; set; }
}

// ══════════════════════════════════════════════════════════════════════════════
//  Mojang Java Runtime manifest
// ══════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Entry in the Java runtime manifest returned by Mojang.
/// Each entry points to a platform-specific runtime with download info.
/// </summary>
public sealed class MojangJavaRuntimeEntry
{
    [JsonPropertyName("availability")] public MojangJavaAvailability? Availability { get; set; }
    [JsonPropertyName("manifest")] public MojangDownloadArtifact? Manifest { get; set; }
    [JsonPropertyName("version")] public MojangJavaRuntimeVersion? Version { get; set; }
}

public sealed class MojangJavaAvailability
{
    [JsonPropertyName("group")] public int Group { get; set; }
    [JsonPropertyName("progress")] public int Progress { get; set; }
}

public sealed class MojangJavaRuntimeVersion
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("released")] public string Released { get; set; } = "";
}

// ══════════════════════════════════════════════════════════════════════════════
//  Fabric Meta models
// ══════════════════════════════════════════════════════════════════════════════

/// <summary>
/// A single Fabric loader version entry from <c>meta.fabricmc.net/v2/versions/loader/{gameVersion}</c>.
/// </summary>
public sealed class FabricLoaderVersionEntry
{
    [JsonPropertyName("loader")] public FabricLoaderInfo? Loader { get; set; }
    [JsonPropertyName("intermediary")] public FabricIntermediaryInfo? Intermediary { get; set; }
    [JsonPropertyName("launcherMeta")] public FabricLauncherMeta? LauncherMeta { get; set; }
}

public sealed class FabricLoaderInfo
{
    [JsonPropertyName("separator")] public string Separator { get; set; } = "";
    [JsonPropertyName("build")] public int Build { get; set; }
    [JsonPropertyName("maven")] public string Maven { get; set; } = "";
    [JsonPropertyName("version")] public string Version { get; set; } = "";
    [JsonPropertyName("stable")] public bool Stable { get; set; }
}

public sealed class FabricIntermediaryInfo
{
    [JsonPropertyName("maven")] public string Maven { get; set; } = "";
    [JsonPropertyName("version")] public string Version { get; set; } = "";
    [JsonPropertyName("stable")] public bool Stable { get; set; }
}

public sealed class FabricLauncherMeta
{
    [JsonPropertyName("version")] public int Version { get; set; }
    [JsonPropertyName("libraries")] public FabricLibraries? Libraries { get; set; }
    [JsonPropertyName("mainClass")] public JsonElement MainClass { get; set; }
}

public sealed class FabricLibraries
{
    [JsonPropertyName("common")] public List<FabricLibraryEntry> Common { get; set; } = [];
    [JsonPropertyName("client")] public List<FabricLibraryEntry> Client { get; set; } = [];
    [JsonPropertyName("server")] public List<FabricLibraryEntry> Server { get; set; } = [];
}

public sealed class FabricLibraryEntry
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("url")] public string Url { get; set; } = "";

    /// <summary>
    /// Converts the Maven coordinate to a relative filesystem path,
    /// consistent with <see cref="MojangLibrary.ToRelativePath"/>.
    /// </summary>
    public string ToRelativePath()
    {
        var parts = Name.Split(':');
        if (parts.Length < 3) return Name.Replace(':', '/') + ".jar";
        var group = parts[0].Replace('.', '/');
        var artifact = parts[1];
        var version = parts[2];
        var classifier = parts.Length > 3 ? "-" + parts[3] : "";
        return $"{group}/{artifact}/{version}/{artifact}-{version}{classifier}.jar";
    }

    public string GetDownloadUrl()
    {
        var baseUrl = string.IsNullOrWhiteSpace(Url) ? "https://maven.fabricmc.net/" : Url;
        return baseUrl.TrimEnd('/') + "/" + ToRelativePath();
    }
}

// ══════════════════════════════════════════════════════════════════════════════
//  Quilt Meta models (reuses Fabric model shape — Quilt's API is compatible)
// ══════════════════════════════════════════════════════════════════════════════

/// <summary>
/// A single Quilt loader version entry from <c>meta.quiltmc.org/v3/versions/loader/{gameVersion}</c>.
/// The JSON structure mirrors Fabric's <see cref="FabricLoaderVersionEntry"/>.
/// </summary>
public sealed class QuiltLoaderVersionEntry
{
    [JsonPropertyName("loader")] public QuiltLoaderInfo? Loader { get; set; }
    [JsonPropertyName("hashed")] public QuiltHashedInfo? Hashed { get; set; }
    [JsonPropertyName("intermediary")] public QuiltIntermediaryInfo? Intermediary { get; set; }
    [JsonPropertyName("launcherMeta")] public QuiltLauncherMeta? LauncherMeta { get; set; }
}

public sealed class QuiltLoaderInfo
{
    [JsonPropertyName("separator")] public string Separator { get; set; } = "";
    [JsonPropertyName("build")] public int Build { get; set; }
    [JsonPropertyName("maven")] public string Maven { get; set; } = "";
    [JsonPropertyName("version")] public string Version { get; set; } = "";
}

/// <summary>Quilt's own "hashed" mappings (equivalent to Fabric's intermediary).</summary>
public sealed class QuiltHashedInfo
{
    [JsonPropertyName("maven")] public string Maven { get; set; } = "";
    [JsonPropertyName("version")] public string Version { get; set; } = "";
}

/// <summary>Fallback intermediary info — present when Quilt supports Fabric intermediary.</summary>
public sealed class QuiltIntermediaryInfo
{
    [JsonPropertyName("maven")] public string Maven { get; set; } = "";
    [JsonPropertyName("version")] public string Version { get; set; } = "";
}

public sealed class QuiltLauncherMeta
{
    [JsonPropertyName("version")] public int Version { get; set; }
    [JsonPropertyName("libraries")] public QuiltLibraries? Libraries { get; set; }
    [JsonPropertyName("mainClass")] public JsonElement MainClass { get; set; }
}

public sealed class QuiltLibraries
{
    [JsonPropertyName("common")] public List<QuiltLibraryEntry> Common { get; set; } = [];
    [JsonPropertyName("client")] public List<QuiltLibraryEntry> Client { get; set; } = [];
    [JsonPropertyName("server")] public List<QuiltLibraryEntry> Server { get; set; } = [];
}

public sealed class QuiltLibraryEntry
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("url")] public string Url { get; set; } = "";

    /// <summary>
    /// Converts the Maven coordinate to a relative filesystem path,
    /// consistent with <see cref="MojangLibrary.ToRelativePath"/>.
    /// </summary>
    public string ToRelativePath()
    {
        var parts = Name.Split(':');
        if (parts.Length < 3) return Name.Replace(':', '/') + ".jar";
        var group = parts[0].Replace('.', '/');
        var artifact = parts[1];
        var version = parts[2];
        var classifier = parts.Length > 3 ? "-" + parts[3] : "";
        return $"{group}/{artifact}/{version}/{artifact}-{version}{classifier}.jar";
    }

    public string GetDownloadUrl()
    {
        var baseUrl = string.IsNullOrWhiteSpace(Url) ? "https://maven.quiltmc.org/repository/release/" : Url;
        return baseUrl.TrimEnd('/') + "/" + ToRelativePath();
    }
}

// ══════════════════════════════════════════════════════════════════════════════
//  Loader classification helper
// ══════════════════════════════════════════════════════════════════════════════

/// <summary>Classifies a loader string into its category for launch behaviour.</summary>
public static class LoaderClassification
{
    /// <summary>Mod loaders that require additional libraries, classpath entries, and main class override.</summary>
    private static readonly HashSet<string> ModLoaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "fabric", "quilt", "forge", "neoforge"
    };

    /// <summary>
    /// Plugin loaders are server-side only. From the client perspective these are
    /// vanilla Minecraft — no extra libraries, no main class override.
    /// </summary>
    private static readonly HashSet<string> PluginLoaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "spigot", "bukkit", "paper", "purpur", "folia"
    };

    /// <summary>Mod loaders that the Default launcher currently supports building and launching.</summary>
    private static readonly HashSet<string> SupportedModLoaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "fabric", "quilt", "forge", "neoforge"
    };

    public static bool IsModLoader(string loader) =>
        !string.IsNullOrWhiteSpace(loader) && ModLoaders.Contains(loader);

    public static bool IsPluginLoader(string loader) =>
        !string.IsNullOrWhiteSpace(loader) && PluginLoaders.Contains(loader);

    /// <summary>
    /// True when the loader requires no client-side mod loader resolution
    /// (vanilla, empty, or any server-side plugin loader).
    /// </summary>
    public static bool IsVanillaLike(string loader) =>
        string.IsNullOrWhiteSpace(loader) || IsPluginLoader(loader);

    /// <summary>
    /// True when the Default launcher can fully build and launch this loader
    /// (vanilla-like, or a mod loader with complete implementation).
    /// </summary>
    public static bool IsDefaultLauncherSupported(string loader) =>
        IsVanillaLike(loader) || SupportedModLoaders.Contains(loader);
}

// ══════════════════════════════════════════════════════════════════════════════
//  HTTP clients for Mojang + Fabric + Quilt meta APIs
// ══════════════════════════════════════════════════════════════════════════════

internal static class MinecraftMetaHttp
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);
    private const string UserAgent = "HalerithAddonClient/1.0";

    public static HttpClient Configure(HttpClient? httpClient)
    {
        var http = httpClient ?? new HttpClient { Timeout = Timeout };

        if (!http.DefaultRequestHeaders.UserAgent.Any(x =>
                string.Equals(x.Product?.Name, "HalerithAddonClient", StringComparison.OrdinalIgnoreCase)))
        {
            http.DefaultRequestHeaders.UserAgent.TryParseAdd(UserAgent);
        }

        return http;
    }
}

/// <summary>
/// Fetches Minecraft version manifests and detail JSON from Mojang's CDN.
/// </summary>
public sealed class MojangMetaClient : IDisposable
{
    private const string ManifestUrl = "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json";
    private const string JavaRuntimeUrl = "https://launchermeta.mojang.com/v1/products/java-runtime/2ec0cc96c44e5a76b9c8b7c39df7210883d12871/all.json";

    private readonly HttpClient _http;
    private readonly bool _ownsHttpClient;

    public MojangMetaClient(HttpClient? httpClient = null)
    {
        _ownsHttpClient = httpClient is null;
        _http = MinecraftMetaHttp.Configure(httpClient);
    }

    /// <summary>Fetches the full version manifest (list of all Minecraft versions).</summary>
    public async Task<MojangVersionManifest> GetVersionManifestAsync(CancellationToken ct = default)
    {
        var json = await _http.GetStringAsync(ManifestUrl, ct).ConfigureAwait(false);
        return JsonSerializer.Deserialize<MojangVersionManifest>(json, SerializerOptions.Default)
               ?? throw new InvalidOperationException("Failed to parse Mojang version manifest.");
    }

    /// <summary>Fetches the detailed version JSON for a specific Minecraft version.</summary>
    public async Task<MojangVersionDetail> GetVersionDetailAsync(string versionUrl, CancellationToken ct = default)
    {
        var json = await _http.GetStringAsync(versionUrl, ct).ConfigureAwait(false);
        return JsonSerializer.Deserialize<MojangVersionDetail>(json, SerializerOptions.Default)
               ?? throw new InvalidOperationException("Failed to parse Mojang version detail.");
    }

    /// <summary>Fetches the asset index JSON for a given asset index URL.</summary>
    public async Task<MojangAssetIndexDetail> GetAssetIndexAsync(string assetIndexUrl, CancellationToken ct = default)
    {
        var json = await _http.GetStringAsync(assetIndexUrl, ct).ConfigureAwait(false);
        return JsonSerializer.Deserialize<MojangAssetIndexDetail>(json, SerializerOptions.Default)
               ?? throw new InvalidOperationException("Failed to parse asset index.");
    }

    /// <summary>
    /// Fetches the Java runtime manifest and returns entries for the given component
    /// (e.g. "java-runtime-gamma") on the current platform.
    /// </summary>
    public async Task<List<MojangJavaRuntimeEntry>> GetJavaRuntimesAsync(string component, CancellationToken ct = default)
    {
        var json = await _http.GetStringAsync(JavaRuntimeUrl, ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);

        var platformKey = GetPlatformKey();
        if (!doc.RootElement.TryGetProperty(platformKey, out var platformEl))
            return [];

        if (!platformEl.TryGetProperty(component, out var componentEl))
            return [];

        return JsonSerializer.Deserialize<List<MojangJavaRuntimeEntry>>(componentEl.GetRawText(), SerializerOptions.Default) ?? [];
    }

    /// <summary>Fetches an arbitrary Mojang metadata JSON document using the shared client lifetime.</summary>
    public Task<string> GetJsonAsync(string url, CancellationToken ct = default) =>
        _http.GetStringAsync(url, ct);

    /// <summary>
    /// Downloads a file to the given path, verifying SHA1 if provided.
    /// Creates parent directories as needed.
    /// </summary>
    public async Task DownloadFileAsync(string url, string destPath, string? expectedSha1 = null,
        IProgress<long>? progress = null, CancellationToken ct = default)
    {
        var dir = Path.GetDirectoryName(destPath);
        if (!string.IsNullOrWhiteSpace(dir))
            Directory.CreateDirectory(dir);

        var tempPath = destPath + ".tmp";
        try
        {
            using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            await using var contentStream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var fileStream = new FileStream(tempPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 8192, true);

            var buffer = new byte[8192];
            long totalRead = 0;
            int bytesRead;
            while ((bytesRead = await contentStream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), ct).ConfigureAwait(false);
                totalRead += bytesRead;
                progress?.Report(totalRead);
            }

            if (!string.IsNullOrWhiteSpace(expectedSha1))
            {
                fileStream.Position = 0;
                var hash = Convert.ToHexString(await SHA1.HashDataAsync(fileStream, ct).ConfigureAwait(false)).ToLowerInvariant();
                if (!string.Equals(hash, expectedSha1, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        $"SHA1 mismatch for {Path.GetFileName(destPath)}: expected {expectedSha1}, got {hash}");
                }
            }

            fileStream.Close();
            File.Move(tempPath, destPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
                try { File.Delete(tempPath); } catch { }
        }
    }

    private static string GetPlatformKey()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return RuntimeInformation.OSArchitecture == Architecture.X64 ? "windows-x64" : "windows-x86";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return "linux";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "mac-os-arm64" : "mac-os";
        return "linux";
    }

    public void Dispose()
    {
        if (_ownsHttpClient)
            _http.Dispose();
    }
}

/// <summary>
/// Fetches Fabric loader metadata from <c>meta.fabricmc.net</c>.
/// </summary>
public sealed class FabricMetaClient : IDisposable
{
    private const string BaseUrl = "https://meta.fabricmc.net/v2/versions/loader/";

    private readonly HttpClient _http;
    private readonly bool _ownsHttpClient;

    public FabricMetaClient(HttpClient? httpClient = null)
    {
        _ownsHttpClient = httpClient is null;
        _http = MinecraftMetaHttp.Configure(httpClient);
    }

    /// <summary>
    /// Returns all available Fabric loader versions for the given Minecraft version,
    /// sorted by build number descending (latest first).
    /// </summary>
    public async Task<List<FabricLoaderVersionEntry>> GetLoaderVersionsAsync(string minecraftVersion, CancellationToken ct = default)
    {
        var url = BaseUrl + Uri.EscapeDataString(minecraftVersion);
        var json = await _http.GetStringAsync(url, ct).ConfigureAwait(false);
        return JsonSerializer.Deserialize<List<FabricLoaderVersionEntry>>(json, SerializerOptions.Default) ?? [];
    }

    /// <summary>
    /// Returns the latest stable Fabric loader version for the given Minecraft version,
    /// or null if no stable version is available.
    /// </summary>
    public async Task<FabricLoaderVersionEntry?> GetLatestStableLoaderAsync(string minecraftVersion, CancellationToken ct = default)
    {
        var versions = await GetLoaderVersionsAsync(minecraftVersion, ct).ConfigureAwait(false);
        return versions.FirstOrDefault(v => v.Loader?.Stable == true) ?? versions.FirstOrDefault();
    }

    /// <summary>
    /// Returns the Fabric loader entry for a specific loader version on the given
    /// Minecraft version.  Falls back to <see cref="GetLatestStableLoaderAsync"/>
    /// if <paramref name="loaderVersion"/> is empty or not found.
    /// </summary>
    public async Task<FabricLoaderVersionEntry?> GetLoaderAsync(string minecraftVersion, string loaderVersion, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(loaderVersion))
            return await GetLatestStableLoaderAsync(minecraftVersion, ct).ConfigureAwait(false);

        var versions = await GetLoaderVersionsAsync(minecraftVersion, ct).ConfigureAwait(false);
        var exact = versions.FirstOrDefault(v =>
            string.Equals(v.Loader?.Version, loaderVersion, StringComparison.OrdinalIgnoreCase));

        return exact ?? versions.FirstOrDefault(v => v.Loader?.Stable == true) ?? versions.FirstOrDefault();
    }

    public void Dispose()
    {
        if (_ownsHttpClient)
            _http.Dispose();
    }
}

/// <summary>
/// Fetches Quilt loader metadata from <c>meta.quiltmc.org</c>.
/// </summary>
public sealed class QuiltMetaClient : IDisposable
{
    private const string BaseUrl = "https://meta.quiltmc.org/v3/versions/loader/";

    private readonly HttpClient _http;
    private readonly bool _ownsHttpClient;

    public QuiltMetaClient(HttpClient? httpClient = null)
    {
        _ownsHttpClient = httpClient is null;
        _http = MinecraftMetaHttp.Configure(httpClient);
    }

    /// <summary>
    /// Returns all available Quilt loader versions for the given Minecraft version,
    /// sorted by build number descending (latest first).
    /// </summary>
    public async Task<List<QuiltLoaderVersionEntry>> GetLoaderVersionsAsync(string minecraftVersion, CancellationToken ct = default)
    {
        var url = BaseUrl + Uri.EscapeDataString(minecraftVersion);
        var json = await _http.GetStringAsync(url, ct).ConfigureAwait(false);
        return JsonSerializer.Deserialize<List<QuiltLoaderVersionEntry>>(json, SerializerOptions.Default) ?? [];
    }

    /// <summary>
    /// Returns the latest Quilt loader version for the given Minecraft version,
    /// or null if none are available.
    /// </summary>
    public async Task<QuiltLoaderVersionEntry?> GetLatestLoaderAsync(string minecraftVersion, CancellationToken ct = default)
    {
        var versions = await GetLoaderVersionsAsync(minecraftVersion, ct).ConfigureAwait(false);
        return versions.FirstOrDefault();
    }

    /// <summary>
    /// Returns the Quilt loader entry for a specific loader version on the given
    /// Minecraft version.  Falls back to <see cref="GetLatestLoaderAsync"/>
    /// if <paramref name="loaderVersion"/> is empty or not found.
    /// </summary>
    public async Task<QuiltLoaderVersionEntry?> GetLoaderAsync(string minecraftVersion, string loaderVersion, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(loaderVersion))
            return await GetLatestLoaderAsync(minecraftVersion, ct).ConfigureAwait(false);

        var versions = await GetLoaderVersionsAsync(minecraftVersion, ct).ConfigureAwait(false);
        var exact = versions.FirstOrDefault(v =>
            string.Equals(v.Loader?.Version, loaderVersion, StringComparison.OrdinalIgnoreCase));

        return exact ?? versions.FirstOrDefault();
    }

    public void Dispose()
    {
        if (_ownsHttpClient)
            _http.Dispose();
    }
}

// ══════════════════════════════════════════════════════════════════════════════
//  Forge / NeoForge — installer profile & version JSON models
// ══════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Represents a single data entry from install_profile.json → data.
/// Keys prefixed with '[' are artifact paths; otherwise literal values.
/// </summary>
public sealed class ForgeDataEntry
{
    [JsonPropertyName("client")] public string Client { get; set; } = "";
    [JsonPropertyName("server")] public string Server { get; set; } = "";
}

/// <summary>
/// Represents one processor step from install_profile.json → processors.
/// Each processor is a runnable JAR with arguments and optional outputs to verify.
/// </summary>
public sealed class ForgeProcessor
{
    [JsonPropertyName("jar")]     public string       Jar     { get; set; } = "";
    [JsonPropertyName("classpath")] public List<string> Classpath { get; set; } = [];
    [JsonPropertyName("args")]    public List<string>  Args    { get; set; } = [];
    [JsonPropertyName("outputs")] public Dictionary<string, string>? Outputs { get; set; }
    /// <summary>When present, only run this processor for the listed sides ("client" / "server").</summary>
    [JsonPropertyName("sides")]   public List<string>? Sides   { get; set; }
}

/// <summary>
/// Parsed install_profile.json from a Forge or NeoForge installer JAR.
/// </summary>
public sealed class ForgeInstallProfile
{
    [JsonPropertyName("spec")]        public int?    Spec       { get; set; }
    [JsonPropertyName("profile")]     public string  Profile    { get; set; } = "";
    [JsonPropertyName("version")]     public string  Version    { get; set; } = "";
    [JsonPropertyName("json")]        public string  Json       { get; set; } = "";   // path inside JAR to version.json
    [JsonPropertyName("path")]        public string? Path       { get; set; }          // Maven coordinate of the universal JAR
    [JsonPropertyName("minecraft")]   public string  Minecraft  { get; set; } = "";
    [JsonPropertyName("data")]        public Dictionary<string, ForgeDataEntry> Data       { get; set; } = [];
    [JsonPropertyName("processors")]  public List<ForgeProcessor>              Processors { get; set; } = [];
    [JsonPropertyName("libraries")]   public List<MojangLibrary>               Libraries  { get; set; } = [];
}

/// <summary>Arguments block in the Forge/NeoForge version.json.</summary>
public sealed class ForgeArguments
{
    [JsonPropertyName("game")] public List<JsonElement> Game { get; set; } = [];
    [JsonPropertyName("jvm")]  public List<JsonElement> Jvm  { get; set; } = [];
}

/// <summary>
/// Parsed version.json from a Forge or NeoForge installer JAR.
/// Used at launch time for mainClass, libraries, and arguments.
/// </summary>
public sealed class ForgeVersionJson
{
    [JsonPropertyName("id")]                  public string          Id              { get; set; } = "";
    [JsonPropertyName("inheritsFrom")]        public string          InheritsFrom    { get; set; } = "";
    [JsonPropertyName("type")]                public string          Type            { get; set; } = "";
    [JsonPropertyName("mainClass")]           public string          MainClass       { get; set; } = "";
    [JsonPropertyName("arguments")]           public ForgeArguments? Arguments       { get; set; }
    [JsonPropertyName("minecraftArguments")]  public string?         MinecraftArguments { get; set; }   // legacy Forge (< 1.13)
    [JsonPropertyName("libraries")]           public List<MojangLibrary> Libraries   { get; set; } = [];
}

// ══════════════════════════════════════════════════════════════════════════════
//  Forge Meta Client — resolves Forge installer versions via Maven XML
// ══════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Fetches available Forge versions from the Forge Maven repository.
/// Forge does not have a simple JSON meta API like Fabric/Quilt — instead
/// we parse the Maven metadata XML from maven-metadata.xml.
/// </summary>
public sealed class ForgeMetaClient : IDisposable
{
    private const string MavenBaseUrl = "https://files.minecraftforge.net/maven/net/minecraftforge/forge/";
    private const string InstallerBaseUrl = "https://maven.minecraftforge.net/net/minecraftforge/forge/";
    private readonly HttpClient _http;
    private readonly bool _ownsHttpClient;

    public ForgeMetaClient(HttpClient? httpClient = null)
    {
        _ownsHttpClient = httpClient is null;
        _http = MinecraftMetaHttp.Configure(httpClient);
    }

    /// <summary>
    /// Returns the latest Forge version string for a given Minecraft version
    /// (e.g. "1.21.1-52.0.16"), or null if no Forge build exists.
    /// Parses Maven metadata XML to find matching versions.
    /// </summary>
    public async Task<string?> GetLatestVersionAsync(string minecraftVersion, CancellationToken ct = default)
    {
        var versions = await GetAvailableVersionsAsync(minecraftVersion, ct).ConfigureAwait(false);
        // Versions are listed latest-first in Maven metadata
        return versions.FirstOrDefault();
    }

    /// <summary>
    /// Returns all available Forge version strings for a given Minecraft version,
    /// sorted latest first. Each string is like "1.21.1-52.0.16".
    /// </summary>
    public async Task<List<string>> GetAvailableVersionsAsync(string minecraftVersion, CancellationToken ct = default)
    {
        var url = MavenBaseUrl + "maven-metadata.xml";
        var xml = await _http.GetStringAsync(url, ct).ConfigureAwait(false);

        // Parse <version> elements from the XML — simple string scanning (no XDocument dependency needed)
        var prefix = minecraftVersion + "-";
        var versions = new List<string>();
        var searchTag = "<version>";
        var endTag = "</version>";
        int pos = 0;
        while ((pos = xml.IndexOf(searchTag, pos, StringComparison.Ordinal)) >= 0)
        {
            pos += searchTag.Length;
            var endPos = xml.IndexOf(endTag, pos, StringComparison.Ordinal);
            if (endPos < 0) break;
            var version = xml[pos..endPos].Trim();
            if (version.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                versions.Add(version);
            pos = endPos;
        }

        // Maven lists oldest first; reverse so latest is first
        versions.Reverse();
        return versions;
    }

    /// <summary>
    /// Returns the URL to download the Forge installer JAR for a given full version string.
    /// </summary>
    public string GetInstallerUrl(string forgeVersion)
        => $"{InstallerBaseUrl}{forgeVersion}/forge-{forgeVersion}-installer.jar";

    public void Dispose()
    {
        if (_ownsHttpClient)
            _http.Dispose();
    }
}

// ══════════════════════════════════════════════════════════════════════════════
//  NeoForge Meta Client — resolves NeoForge versions via Maven XML
// ══════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Fetches available NeoForge versions from the NeoForge Maven repository.
/// NeoForge version scheme: MC 1.20.1 → "47.x.y", MC 1.21.1 → "21.1.x" (YY.minor.build).
/// </summary>
public sealed class NeoForgeMetaClient : IDisposable
{
    private const string MavenBaseUrl = "https://maven.neoforged.net/releases/net/neoforged/neoforge/";
    private readonly HttpClient _http;
    private readonly bool _ownsHttpClient;

    public NeoForgeMetaClient(HttpClient? httpClient = null)
    {
        _ownsHttpClient = httpClient is null;
        _http = MinecraftMetaHttp.Configure(httpClient);
    }

    /// <summary>
    /// Returns the latest NeoForge version string for a given Minecraft version,
    /// or null if no NeoForge build exists.
    /// </summary>
    public async Task<string?> GetLatestVersionAsync(string minecraftVersion, CancellationToken ct = default)
    {
        var versions = await GetAvailableVersionsAsync(minecraftVersion, ct).ConfigureAwait(false);
        return versions.FirstOrDefault();
    }

    /// <summary>
    /// Returns all available NeoForge version strings for a given Minecraft version,
    /// sorted latest first. Each string is like "21.1.72".
    /// </summary>
    public async Task<List<string>> GetAvailableVersionsAsync(string minecraftVersion, CancellationToken ct = default)
    {
        var url = MavenBaseUrl + "maven-metadata.xml";
        var xml = await _http.GetStringAsync(url, ct).ConfigureAwait(false);

        // Map Minecraft version to NeoForge prefix.
        // NeoForge 1.20.1 used the old Forge-era numbering (47.x).
        // From 1.20.2+ the scheme is YY.minor.build where YY.minor matches MC's minor.patch.
        var neoPrefix = MapMinecraftToNeoForgePrefix(minecraftVersion);
        if (neoPrefix is null) return [];

        var versions = new List<string>();
        var searchTag = "<version>";
        var endTag = "</version>";
        int pos = 0;
        while ((pos = xml.IndexOf(searchTag, pos, StringComparison.Ordinal)) >= 0)
        {
            pos += searchTag.Length;
            var endPos = xml.IndexOf(endTag, pos, StringComparison.Ordinal);
            if (endPos < 0) break;
            var version = xml[pos..endPos].Trim();
            if (version.StartsWith(neoPrefix, StringComparison.OrdinalIgnoreCase))
                versions.Add(version);
            pos = endPos;
        }

        versions.Reverse();
        return versions;
    }

    /// <summary>
    /// Returns the URL to download the NeoForge installer JAR for a given version string.
    /// </summary>
    public string GetInstallerUrl(string neoForgeVersion)
        => $"{MavenBaseUrl}{neoForgeVersion}/neoforge-{neoForgeVersion}-installer.jar";

    /// <summary>
    /// Maps a Minecraft version string to the NeoForge version prefix used in Maven.
    /// Returns null if the MC version can't be mapped.
    /// </summary>
    internal static string? MapMinecraftToNeoForgePrefix(string minecraftVersion)
    {
        // Special case: 1.20.1 used Forge-era numbering under the "neoforge" artifact
        if (minecraftVersion == "1.20.1") return "47.";

        // 1.20.2+ → YY.minor. (e.g. 1.20.4 → "20.4.", 1.21.1 → "21.1.")
        var parts = minecraftVersion.Split('.');
        if (parts.Length < 2) return null;

        var major = parts[0]; // always "1" for now
        var minor = parts[1]; // e.g. "20", "21"
        var patch = parts.Length > 2 ? parts[2] : "0";

        // Skip versions before NeoForge existed
        if (!int.TryParse(minor, out var minorInt) || minorInt < 20) return null;

        return $"{minor}.{patch}.";
    }

    public void Dispose()
    {
        if (_ownsHttpClient)
            _http.Dispose();
    }
}
