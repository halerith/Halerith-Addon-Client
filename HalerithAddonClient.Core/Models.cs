using System.Text.Json.Serialization;

namespace HalerithAddonClient.Core;

/// <summary>Content type values used in IgnoreRule and LocalPinnedItem.</summary>
public static class ContentTypes
{
    public const string Mod = "mod";
    public const string ResourcePack = "resource_pack";
    public const string Shader = "shader";
    public const string Modpack = "modpack";

    public static string ToDisplay(string contentType) => contentType switch
    {
        Mod          => "Mod",
        ResourcePack => "Resource Pack",
        Shader       => "Shader",
        Modpack      => "Modpack",
        _            => contentType
    };

    public static IReadOnlyList<string> All => [Mod, ResourcePack, Shader];
}

/// <summary>A file-pattern rule that tells the client to leave a local file alone rather than removing it.</summary>
public sealed class IgnoreRule
{
    [JsonPropertyName("content_type")] public string ContentType { get; set; } = ContentTypes.Mod;
    /// <summary>Filename (with extension) or stem (without extension) to match.</summary>
    [JsonPropertyName("pattern")] public string Pattern { get; set; } = "";
}

/// <summary>Maps a local file to its server-side slug so the client treats it as already up-to-date.</summary>
public sealed class LocalPinnedItem
{
    [JsonPropertyName("content_type")] public string ContentType { get; set; } = ContentTypes.Mod;
    [JsonPropertyName("file_name")] public string FileName { get; set; } = "";
    [JsonPropertyName("slug")] public string Slug { get; set; } = "";
}


public sealed class HealthRequest
{
    [JsonPropertyName("username")]       public string Username      { get; set; } = "";
    [JsonPropertyName("minecraft_uuid")] public string MinecraftUuid { get; set; } = "";
    [JsonPropertyName("client_version")] public string ClientVersion { get; set; } = "";
    [JsonPropertyName("platform")]       public string Platform      { get; set; } = "";
    [JsonPropertyName("arch")]           public string Architecture  { get; set; } = "";
}

public sealed class ManifestRequest
{
    [JsonPropertyName("client_version")] public string ClientVersion { get; set; } = "";
}

public class ApiResponseBase
{
    [JsonPropertyName("ok")] public bool Ok { get; set; }

    /// <summary>
    /// Numeric error code returned by the server for any failed response.
    /// The client maps this to a user-facing message via <see cref="ApiErrorTranslator"/>.
    /// Supersedes the string <see cref="Error"/> / <see cref="Message"/> fields.
    /// </summary>
    [JsonPropertyName("code")] public int? Code { get; set; }

    /// <summary>
    /// Legacy string error slug. Set internally for non-server errors
    /// (e.g. network failures, JSON parse errors). Never populated by the server.
    /// </summary>
    [JsonPropertyName("error")] public string? Error { get; set; }

    /// <summary>
    /// Resolved user-facing message. Populated by <see cref="ApiClient"/> after
    /// translation — not sent by the server.
    /// </summary>
    [JsonPropertyName("message")] public string? Message { get; set; }

    [JsonPropertyName("auth")] public ClientAccessTokenInfo? Auth { get; set; }
}

public sealed class HealthResponse : ApiResponseBase
{
    [JsonPropertyName("client_status")] public string? ClientStatus { get; set; }
    [JsonPropertyName("status")] public string? Status { get; set; }
    [JsonPropertyName("username")] public string? Username { get; set; }
    [JsonPropertyName("minecraft_uuid")] public string? MinecraftUuid { get; set; }
    [JsonPropertyName("minecraft")] public HealthMinecraftInfo? Minecraft { get; set; }
    [JsonPropertyName("generated_at_utc")] public string? GeneratedAtUtc { get; set; }
    [JsonPropertyName("free_access")] public ManagedAccessStatusResponse? FreeAccess { get; set; }
    [JsonPropertyName("roster_complete")] public bool RosterComplete { get; set; }

    /// <summary>
    /// Servers available to the authenticated client.
    /// Unauthenticated: empty, because the endpoint only confirms manager reachability.
    /// Authenticated:   contains server IDs, names, and extended detail such as
    /// <c>minecraft_version</c> and <c>loader</c>.
    /// </summary>
    [JsonPropertyName("servers")] public List<HealthServerEntry> Servers { get; set; } = [];
    [JsonPropertyName("environments")] public List<HealthEnvironmentEntry> Environments { get; set; } = [];

    /// <summary>
    /// Mod loaders supported by the panel (e.g. fabric, forge, neoforge, quilt).
    /// Only present in authenticated responses.  The client cross-references this
    /// against its own capabilities to determine what's available in Default mode.
    /// </summary>
    [JsonPropertyName("supported_mod_loaders")] public List<string>? SupportedModLoaders { get; set; }

    /// <summary>
    /// Plugin loaders supported by the panel (e.g. spigot, paper, purpur).
    /// Plugin loaders are server-side only — from the client's perspective these
    /// are vanilla Minecraft.  Only present in authenticated responses.
    /// </summary>
    [JsonPropertyName("supported_plugin_loaders")] public List<string>? SupportedPluginLoaders { get; set; }
}

public sealed class ManagedAccessStatusResponse : ApiResponseBase
{
    [JsonPropertyName("mode")] public string Mode { get; set; } = "";
    [JsonPropertyName("trial_ends_at")] public string? TrialEndsAt { get; set; }
    [JsonPropertyName("server_public_ids")] public List<string> ServerPublicIds { get; set; } = [];
    [JsonPropertyName("needs_selection")] public bool NeedsSelection { get; set; }
    // Optional fields from the new UUID-scoped access response. Older servers omit them.
    [JsonPropertyName("next_change_at")] public string? NextChangeAt { get; set; }
    [JsonPropertyName("can_change_now")] public bool CanChangeNow { get; set; }
    [JsonPropertyName("linked_halerith_account")] public bool? LinkedHalerithAccount { get; set; }
    [JsonPropertyName("linked_halerith_email")] public string? LinkedHalerithEmail { get; set; }
    [JsonPropertyName("grant_source")] public string? GrantSource { get; set; }
    [JsonPropertyName("paid_access_ends_at")] public string? PaidAccessEndsAt { get; set; }
    [JsonPropertyName("initial_correction_available")] public bool InitialCorrectionAvailable { get; set; }
    [JsonPropertyName("change_interval_days")] public int? ChangeIntervalDays { get; set; }
}

public sealed class HealthMinecraftInfo
{
    [JsonPropertyName("uuid")] public string? Uuid { get; set; }
    [JsonPropertyName("username")] public string? Username { get; set; }
}

public sealed class ClientAccessTokenInfo
{
    [JsonPropertyName("access_token")] public string AccessToken { get; set; } = "";
    [JsonPropertyName("token_type")] public string TokenType { get; set; } = "Bearer";
    [JsonPropertyName("expires_at")] public string ExpiresAt { get; set; } = "";
    [JsonPropertyName("expires_in")] public int ExpiresIn { get; set; }
}

public sealed class HealthEnvironmentEntry
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("status")] public string? Status { get; set; }
    [JsonPropertyName("access_active")] public bool AccessActive { get; set; }
    [JsonPropertyName("servers")] public List<HealthServerEntry> Servers { get; set; } = [];
}

/// <summary>
/// A server entry returned by the manager health endpoint.
/// When authenticated, includes Minecraft version, loader, connection info,
/// and related launcher-facing metadata.
/// </summary>
public sealed class HealthServerEntry
{
    [JsonPropertyName("server_public_id")] public string ServerPublicId { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("access_active")] public bool AccessActive { get; set; }
    [JsonPropertyName("node_fqdn")]         public string? NodeFqdn         { get; set; }
    [JsonPropertyName("port")]              public int?    Port             { get; set; }
    [JsonPropertyName("game_endpoint")]     public HealthGameEndpoint? GameEndpoint { get; set; }
    [JsonPropertyName("minecraft_version")] public string? MinecraftVersion { get; set; }
    [JsonPropertyName("loader")]            public string? Loader           { get; set; }
    [JsonPropertyName("loader_version")]    public string? LoaderVersion    { get; set; }
    [JsonPropertyName("motd")]              public string? Motd             { get; set; }
    [JsonPropertyName("power_status")]      public string? PowerStatus      { get; set; }
    [JsonPropertyName("icon")]              public HealthServerIconInfo? Icon { get; set; }
}

public sealed class HealthGameEndpoint
{
    [JsonPropertyName("host")] public string? Host { get; set; }
    [JsonPropertyName("port")] public int? Port { get; set; }
    [JsonPropertyName("host_source")] public string? HostSource { get; set; }
    [JsonPropertyName("port_source")] public string? PortSource { get; set; }
    [JsonPropertyName("complete")] public bool Complete { get; set; }
}

public sealed class HealthServerIconInfo
{
    [JsonPropertyName("download_url")] public string? DownloadUrl { get; set; }
    [JsonPropertyName("path")] public string? Path { get; set; }
    [JsonPropertyName("filename")] public string? Filename { get; set; }
    [JsonPropertyName("sha256")] public string? Sha256 { get; set; }
    [JsonPropertyName("size_bytes")] public long? SizeBytes { get; set; }
    [JsonPropertyName("content_type")] public string? ContentType { get; set; }
}

/// <summary>
/// Public client installer update information returned in the health response.
/// </summary>
public sealed class HealthLauncherInfo
{
    /// <summary>The version string of the installer currently hosted by Halerith.</summary>
    [JsonPropertyName("version")] public string? Version { get; set; }

    /// <summary>The public installer download URL returned by health.</summary>
    [JsonPropertyName("download_url")] public string? DownloadUrl { get; set; }

    /// <summary>The original hosted package filename, used to preserve Linux .deb update names.</summary>
    [JsonPropertyName("filename")] public string? Filename { get; set; }

    /// <summary>Package type such as zip, dmg, tgz, or deb.</summary>
    [JsonPropertyName("package_type")] public string? PackageType { get; set; }

    /// <summary>Exact signed Linux release manifest and base64-encoded detached signature.</summary>
    [JsonPropertyName("linux_manifest")] public string? LinuxManifest { get; set; }
    [JsonPropertyName("linux_signature")] public string? LinuxSignature { get; set; }

    /// <summary>SHA-256 checksum of the hosted update package.</summary>
    [JsonPropertyName("sha256")] public string? Sha256 { get; set; }

    /// <summary>
    /// Optional public Sparkle appcast URL for macOS clients.
    /// Windows ignores this and continues to use <see cref="DownloadUrl"/>.
    /// </summary>
    [JsonPropertyName("appcast_url")] public string? AppcastUrl { get; set; }

    /// <summary>True when a client.zip file is available for download on the panel.</summary>
    [JsonPropertyName("file_available")] public bool FileAvailable { get; set; }

    /// <summary>
    /// True when the panel's stored version is newer than the client's reported version.
    /// </summary>
    [JsonPropertyName("update_available")] public bool UpdateAvailable { get; set; }
}

public sealed class ManifestResponse : ApiResponseBase
{
    [JsonPropertyName("authorized")] public bool Authorized { get; set; }
    [JsonPropertyName("minecraft_uuid")] public string? MinecraftUuid { get; set; }
    [JsonPropertyName("free_access")] public ManagedAccessStatusResponse? FreeAccess { get; set; }
    [JsonPropertyName("server")]     public ManifestServerInfo? Server { get; set; }
    [JsonPropertyName("mod_count")]  public int ModCount { get; set; }
    [JsonPropertyName("mods")]       public List<ManifestMod> Mods { get; set; } = [];
    [JsonPropertyName("resource_pack_count")] public int ResourcePackCount { get; set; }
    [JsonPropertyName("resource_packs")]      public List<ManifestResourcePack> ResourcePacks { get; set; } = [];
    [JsonPropertyName("shader_count")] public int ShaderCount { get; set; }
    [JsonPropertyName("shaders")]      public List<ManifestShader> Shaders { get; set; } = [];
    [JsonPropertyName("modpack")] public ManifestModpack? Modpack { get; set; }
}

public sealed class ManifestModpack
{
    // Local Client inspection only. Never accepted from a Manager JSON manifest.
    [JsonIgnore] internal string? VerifiedLocalArchivePath { get; set; }
    [JsonIgnore] internal List<string> LocalRetiredPaths { get; set; } = [];
    [JsonPropertyName("runtime_requirements")] public ModpackRuntimeRequirements? RuntimeRequirements { get; set; }
    [JsonIgnore] internal HashSet<string> SeparatelyManagedPaths { get; set; } = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    internal ManifestModpack WithSeparatePaths(HashSet<string> paths)
    {
        var copy = (ManifestModpack)MemberwiseClone();
        copy.SeparatelyManagedPaths = paths;
        return copy;
    }
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("application_revision")] public string ApplicationRevision { get; set; } = "";
    [JsonPropertyName("applied_at")] public string? AppliedAt { get; set; }
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("provider")] public string Provider { get; set; } = "";
    [JsonPropertyName("project_id")] public string ProjectId { get; set; } = "";
    [JsonPropertyName("revision_id")] public string RevisionId { get; set; } = "";
    [JsonPropertyName("version_id")] public string VersionId { get; set; } = "";
    [JsonPropertyName("version_name")] public string VersionName { get; set; } = "";
    [JsonPropertyName("force_config")] public bool ForceConfig { get; set; }
    [JsonPropertyName("archive")] public ManifestModpackArchive Archive { get; set; } = new();
    [JsonPropertyName("files")] public List<ManifestModpackFile> Files { get; set; } = [];
}

public sealed class ManifestModpackArchive
{
    [JsonPropertyName("url")] public string Url { get; set; } = "";
    [JsonPropertyName("filename")] public string Filename { get; set; } = "";
    [JsonPropertyName("size")] public long Size { get; set; }
    [JsonPropertyName("sha256")] public string Sha256 { get; set; } = "";
}

public sealed class ManifestModpackFile
{
    [JsonPropertyName("path")] public string Path { get; set; } = "";
    [JsonPropertyName("archive_path")] public string ArchivePath { get; set; } = "";
    [JsonPropertyName("size")] public long Size { get; set; }
    [JsonPropertyName("sha256")] public string Sha256 { get; set; } = "";
    [JsonPropertyName("category")] public string Category { get; set; } = "";
}

/// <summary>
/// Server connection details returned inside the manifest response.
/// Used to keep the Minecraft <c>servers.dat</c> entry up to date before each launch.
/// </summary>
public sealed class ManifestServerInfo
{
    [JsonPropertyName("server_public_id")]   public string ServerPublicId   { get; set; } = "";
    [JsonPropertyName("name")]               public string Name             { get; set; } = "";
    [JsonPropertyName("node_fqdn")]          public string? NodeFqdn        { get; set; }
    [JsonPropertyName("port")]               public int?   Port             { get; set; }
    [JsonPropertyName("game_endpoint")]      public HealthGameEndpoint? GameEndpoint { get; set; }
    [JsonPropertyName("minecraft_version")]  public string MinecraftVersion { get; set; } = "";
    [JsonPropertyName("loader")]             public string Loader           { get; set; } = "";
    [JsonPropertyName("loader_version")]     public string LoaderVersion    { get; set; } = "";
}


/// <summary>Rich metadata for a non-active version of a manifest item.</summary>
public sealed class AlternateVersionInfo
{
    [JsonPropertyName("filename")] public string Filename { get; set; } = "";
    [JsonPropertyName("file_hash")] public ManifestHashInfo? FileHash { get; set; }
    [JsonPropertyName("version_name")] public string VersionName { get; set; } = "";
    [JsonPropertyName("version_number")] public string VersionNumber { get; set; } = "";
    [JsonPropertyName("game_versions")] public List<string> GameVersions { get; set; } = [];
}

public sealed class ManifestMod
{
    [JsonPropertyName("pack_managed")] public bool PackManaged { get; set; }
    [JsonPropertyName("unavailable")] public bool Unavailable { get; set; }
    [JsonPropertyName("manual_download_allowed")] public bool ManualDownloadAllowed { get; set; }
    [JsonPropertyName("source_version_id")] public string SourceVersionId { get; set; } = "";
    [JsonPropertyName("mod_id")] public int ModId { get; set; }
    [JsonPropertyName("source_id")] public string SourceId { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("slug")] public string Slug { get; set; } = "";
    [JsonPropertyName("icon_url")] public string? IconUrl { get; set; }
    /// <summary>Rich metadata for all non-active versions of this item.</summary>
    [JsonPropertyName("alternate_versions")] public List<AlternateVersionInfo> AlternateVersions { get; set; } = [];
    [JsonPropertyName("game_versions")] public List<string> GameVersions { get; set; } = [];
    [JsonPropertyName("mod_side")] public string ModSide { get; set; } = "";
    /// <summary>
    /// Source of this version: 'modrinth', 'curseforge', or 'custom'.
    /// Used to determine whether the item routes to CustomFolder.
    /// </summary>
    [JsonPropertyName("version_source")] public string VersionSource { get; set; } = "";
    [JsonPropertyName("version_name")] public string VersionName { get; set; } = "";
    [JsonPropertyName("version_number")] public string VersionNumber { get; set; } = "";
    [JsonPropertyName("download_url")] public string DownloadUrl { get; set; } = "";
    [JsonPropertyName("download_filename")] public string DownloadFilename { get; set; } = "";
    [JsonPropertyName("download_hash")] public ManifestHashInfo? DownloadHash { get; set; }
    [JsonPropertyName("download_size_bytes")] public long? DownloadSizeBytes { get; set; }
    [JsonPropertyName("aliases")] public List<ModAlias> Aliases { get; set; } = [];

    /// <summary>True when the item was uploaded via custom upload rather than fetched from Modrinth/CurseForge.</summary>
    [JsonIgnore]
    public bool IsCustom => string.Equals(VersionSource, "custom", StringComparison.OrdinalIgnoreCase);
}

public sealed class ModAlias
{
    [JsonPropertyName("type")] public string Type { get; set; } = "";
    [JsonPropertyName("value")] public string Value { get; set; } = "";
}

public sealed class ManifestResourcePack
{
    [JsonPropertyName("pack_managed")] public bool PackManaged { get; set; }
    [JsonPropertyName("unavailable")] public bool Unavailable { get; set; }
    [JsonPropertyName("manual_download_allowed")] public bool ManualDownloadAllowed { get; set; }
    [JsonPropertyName("source_version_id")] public string SourceVersionId { get; set; } = "";
    [JsonPropertyName("resource_pack_id")] public int ResourcePackId { get; set; }
    [JsonPropertyName("source_id")] public string SourceId { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("slug")] public string Slug { get; set; } = "";
    [JsonPropertyName("icon_url")] public string? IconUrl { get; set; }
    /// <summary>Rich metadata for all non-active versions of this item.</summary>
    [JsonPropertyName("alternate_versions")] public List<AlternateVersionInfo> AlternateVersions { get; set; } = [];
    [JsonPropertyName("game_versions")] public List<string> GameVersions { get; set; } = [];
    /// <summary>Source of this version: 'modrinth', 'curseforge', or 'custom'.</summary>
    [JsonPropertyName("version_source")] public string VersionSource { get; set; } = "";
    [JsonPropertyName("version_name")] public string VersionName { get; set; } = "";
    [JsonPropertyName("version_number")] public string VersionNumber { get; set; } = "";
    [JsonPropertyName("download_url")] public string DownloadUrl { get; set; } = "";
    [JsonPropertyName("download_filename")] public string DownloadFilename { get; set; } = "";
    [JsonPropertyName("download_hash")] public ManifestHashInfo? DownloadHash { get; set; }
    [JsonPropertyName("download_size_bytes")] public long? DownloadSizeBytes { get; set; }
    [JsonPropertyName("aliases")] public List<ModAlias> Aliases { get; set; } = [];

    /// <summary>True when the item was uploaded via custom upload rather than fetched from Modrinth/CurseForge.</summary>
    [JsonIgnore]
    public bool IsCustom => string.Equals(VersionSource, "custom", StringComparison.OrdinalIgnoreCase);
}

public sealed class ManifestShader
{
    [JsonPropertyName("pack_managed")] public bool PackManaged { get; set; }
    [JsonPropertyName("unavailable")] public bool Unavailable { get; set; }
    [JsonPropertyName("manual_download_allowed")] public bool ManualDownloadAllowed { get; set; }
    [JsonPropertyName("source_version_id")] public string SourceVersionId { get; set; } = "";
    [JsonPropertyName("shader_id")] public int ShaderId { get; set; }
    [JsonPropertyName("source_id")] public string SourceId { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("slug")] public string Slug { get; set; } = "";
    [JsonPropertyName("icon_url")] public string? IconUrl { get; set; }
    /// <summary>Rich metadata for all non-active versions of this item.</summary>
    [JsonPropertyName("alternate_versions")] public List<AlternateVersionInfo> AlternateVersions { get; set; } = [];
    [JsonPropertyName("game_versions")] public List<string> GameVersions { get; set; } = [];
    /// <summary>Source of this version: 'modrinth', 'curseforge', or 'custom'.</summary>
    [JsonPropertyName("version_source")] public string VersionSource { get; set; } = "";
    [JsonPropertyName("version_name")] public string VersionName { get; set; } = "";
    [JsonPropertyName("version_number")] public string VersionNumber { get; set; } = "";
    [JsonPropertyName("download_url")] public string DownloadUrl { get; set; } = "";
    [JsonPropertyName("download_filename")] public string DownloadFilename { get; set; } = "";
    [JsonPropertyName("download_hash")] public ManifestHashInfo? DownloadHash { get; set; }
    [JsonPropertyName("download_size_bytes")] public long? DownloadSizeBytes { get; set; }
    [JsonPropertyName("aliases")] public List<ModAlias> Aliases { get; set; } = [];

    /// <summary>True when the item was uploaded via custom upload rather than fetched from Modrinth/CurseForge.</summary>
    [JsonIgnore]
    public bool IsCustom => string.Equals(VersionSource, "custom", StringComparison.OrdinalIgnoreCase);
}

public sealed class LocalModFile
{
    public string FileName { get; set; } = "";
    public string FullPath { get; set; } = "";
    public string Stem { get; set; } = "";
    public string Source { get; set; } = "";
    public string SourceId { get; set; } = "";
    public string Slug { get; set; } = "";
    public string InventoryName { get; set; } = "";
    public string InventoryVersionName { get; set; } = "";
    public string InventoryVersionNumber { get; set; } = "";
    public string InventoryFileHash { get; set; } = "";
}

/// <summary>Common adapter used by SyncPlanner for resource packs and shaders.</summary>
public sealed class PackManifestItem
{
    public bool PackManaged { get; init; }
    public bool Unavailable { get; init; }
    public bool ManualDownloadAllowed { get; init; }
    public string SourceVersionId { get; init; } = "";
    public string ContentItemId { get; init; } = "";
    public string SourceId { get; init; } = "";
    public string Slug { get; init; } = "";
    public string Name { get; init; } = "";
    public string? IconUrl { get; init; }
    public string VersionSource { get; init; } = "";
    public string VersionName { get; init; } = "";
    public string VersionNumber { get; init; } = "";
    public string DownloadFilename { get; init; } = "";
    public string DownloadUrl { get; init; } = "";
    /// <summary>Typed digest of the download file. Null or empty if not provided by server.</summary>
    public ManifestHashInfo? DownloadHash { get; init; }
    public long? DownloadSizeBytes { get; init; }
    public IReadOnlyList<ModAlias> Aliases { get; init; } = [];
    /// <summary>
    /// Rich metadata for all non-active versions of this item, as supplied by the
    /// server manifest. The client matches filenames from these before falling back
    /// to alias matching, treating any hit as a Replace action.
    /// </summary>
    public IReadOnlyList<AlternateVersionInfo> AlternateVersions { get; init; } = [];
    /// <summary>Minecraft versions supported by the active version.</summary>
    public IReadOnlyList<string> GameVersions { get; init; } = [];
    /// <summary>True when the item was uploaded via custom upload (version_source = 'custom').</summary>
    public bool IsCustom { get; init; }

    public static PackManifestItem From(ManifestResourcePack rp) => new()
    {
        PackManaged = rp.PackManaged, Unavailable = rp.Unavailable, ManualDownloadAllowed = rp.ManualDownloadAllowed, SourceVersionId = rp.SourceVersionId,
        ContentItemId = rp.ResourcePackId.ToString(),
        SourceId = rp.SourceId,
        Slug = rp.Slug,
        Name = rp.Name,
        IconUrl = rp.IconUrl,
        VersionSource = rp.VersionSource,
        VersionName = rp.VersionName,
        VersionNumber = rp.VersionNumber,
        DownloadFilename = rp.DownloadFilename,
        DownloadUrl = rp.DownloadUrl,
        DownloadHash = rp.DownloadHash,
        DownloadSizeBytes = rp.DownloadSizeBytes,
        Aliases = rp.Aliases,
        AlternateVersions = rp.AlternateVersions,
        GameVersions = rp.GameVersions,
        IsCustom = rp.IsCustom
    };

    public static PackManifestItem From(ManifestShader sh) => new()
    {
        PackManaged = sh.PackManaged, Unavailable = sh.Unavailable, ManualDownloadAllowed = sh.ManualDownloadAllowed, SourceVersionId = sh.SourceVersionId,
        ContentItemId = sh.ShaderId.ToString(),
        SourceId = sh.SourceId,
        Slug = sh.Slug,
        Name = sh.Name,
        IconUrl = sh.IconUrl,
        VersionSource = sh.VersionSource,
        VersionName = sh.VersionName,
        VersionNumber = sh.VersionNumber,
        DownloadFilename = sh.DownloadFilename,
        DownloadUrl = sh.DownloadUrl,
        DownloadHash = sh.DownloadHash,
        DownloadSizeBytes = sh.DownloadSizeBytes,
        Aliases = sh.Aliases,
        AlternateVersions = sh.AlternateVersions,
        GameVersions = sh.GameVersions,
        IsCustom = sh.IsCustom
    };
}

public enum SyncActionKind
{
    Download,
    Replace,
    Remove,
    Ignore,
    UpToDate,
    Missing
}

public enum SyncMatchConfidence
{
    None,
    Low,
    Medium,
    High
}

public sealed class SyncAction
{
    public BundledContentFile? BundledFile { get; set; }
    public SyncActionKind Kind { get; set; }
    /// <summary>ContentTypes constant indicating mod, resource_pack, or shader.</summary>
    public string ContentType { get; set; } = ContentTypes.Mod;
    public ManifestMod? ManifestMod { get; set; }
    /// <summary>Set for resource pack and shader actions.</summary>
    public PackManifestItem? PackManifestItem { get; set; }
    public LocalModFile? LocalFile { get; set; }
    public string InstalledVersion { get; set; } = "";
    public string TargetVersion { get; set; } = "";
    public string? Reason { get; set; }
    public SyncMatchConfidence MatchConfidence { get; set; } = SyncMatchConfidence.High;
    /// <summary>
    /// True when this action targets a custom content item (version_source = 'custom').
    /// The executor uses this to route downloads to CustomFolder/&lt;type&gt;/ instead
    /// of the standard content folder.
    /// </summary>
    public bool IsCustom { get; set; }
    public ManifestModpack? Modpack { get; set; }
    public string? ModpackStatus { get; set; }
    public bool CanReviewModpack { get; set; }
    public bool RequiresModpackReview { get; set; }
    public ModpackFileReview? ApprovedModpackReview { get; set; }
}

public sealed class SyncPlan
{
    public List<SyncAction> Actions { get; set; } = [];
}

public sealed class SyncActionAuditItem
{
    [JsonPropertyName("content_item_id")] public string ContentItemId { get; set; } = "";
    [JsonPropertyName("slug")] public string Slug { get; set; } = "";
    [JsonPropertyName("failure_code")] public string? FailureCode { get; set; }
}

public sealed class SyncExecutionResult
{
    public int Downloaded { get; set; }
    public int Replaced { get; set; }
    public int Removed { get; set; }
    public int Ignored { get; set; }
    public int UpToDate { get; set; }
    public int Failed { get; set; }
    public List<SyncActionAuditItem> DownloadedPackages { get; } = [];
    public List<SyncActionAuditItem> ReplacedPackages { get; } = [];
    public List<SyncActionAuditItem> RemovedPackages { get; } = [];
    public List<SyncActionAuditItem> IgnoredPackages { get; } = [];
    public List<SyncActionAuditItem> UpToDatePackages { get; } = [];
    public List<SyncActionAuditItem> FailedPackages { get; } = [];

    /// <summary>
    /// Derived report status.
    /// "success" — all actions completed, no failures.
    /// "partial" — some succeeded, at least one failed.
    /// "failed"  — everything failed or the executor threw before any action completed.
    /// </summary>
    public string Status =>
        Failed == 0                            ? "success" :
        (Downloaded + Replaced + Removed) > 0 ? "partial"  :
                                                 "failed";
}

public sealed class ReportRequest
{
    [JsonPropertyName("client_version")] public string ClientVersion { get; set; } = "";
    [JsonPropertyName("mode")]           public string Mode          { get; set; } = "sync";
    [JsonPropertyName("status")]         public string Status        { get; set; } = "";
    [JsonPropertyName("started_at")]     public string StartedAt     { get; set; } = "";
    [JsonPropertyName("completed_at")]   public string CompletedAt   { get; set; } = "";
    [JsonPropertyName("details")]        public SyncReportDetails? Details { get; set; }
}

public sealed class SyncReportDetails
{
    [JsonPropertyName("downloaded")] public int Downloaded { get; set; }
    [JsonPropertyName("replaced")] public int Replaced { get; set; }
    [JsonPropertyName("removed")] public int Removed { get; set; }
    [JsonPropertyName("ignored")] public int Ignored { get; set; }
    [JsonPropertyName("up_to_date")] public int UpToDate { get; set; }
    [JsonPropertyName("failed")] public int Failed { get; set; }
    [JsonPropertyName("error_code")] public string? ErrorCode { get; set; }
    [JsonPropertyName("packages")] public SyncReportPackages Packages { get; set; } = new();

    public static SyncReportDetails From(SyncExecutionResult result, string? errorCode = null) => new()
    {
        Downloaded = result.Downloaded,
        Replaced = result.Replaced,
        Removed = result.Removed,
        Ignored = result.Ignored,
        UpToDate = result.UpToDate,
        Failed = result.Failed,
        ErrorCode = errorCode,
        Packages = new SyncReportPackages
        {
            Downloaded = result.DownloadedPackages,
            Replaced = result.ReplacedPackages,
            Removed = result.RemovedPackages,
            Ignored = result.IgnoredPackages,
            UpToDate = result.UpToDatePackages,
            Failed = result.FailedPackages,
        },
    };
}

public sealed class SyncReportPackages
{
    [JsonPropertyName("downloaded")] public List<SyncActionAuditItem> Downloaded { get; set; } = [];
    [JsonPropertyName("replaced")] public List<SyncActionAuditItem> Replaced { get; set; } = [];
    [JsonPropertyName("removed")] public List<SyncActionAuditItem> Removed { get; set; } = [];
    [JsonPropertyName("ignored")] public List<SyncActionAuditItem> Ignored { get; set; } = [];
    [JsonPropertyName("up_to_date")] public List<SyncActionAuditItem> UpToDate { get; set; } = [];
    [JsonPropertyName("failed")] public List<SyncActionAuditItem> Failed { get; set; } = [];
}

public sealed class ReportResponse : ApiResponseBase
{
    [JsonPropertyName("stored")] public bool Stored { get; set; }
}
