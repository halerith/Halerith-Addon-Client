using System.Text.Json.Serialization;
using HalerithAddonClient.Core.Platform;

namespace HalerithAddonClient.Core;

// ──────────────────────────────────────────────────────────────────────────────
//  Manager configuration model
//
//  AppConfig       — app-wide settings + Halerith-managed environment/server list
//  HostConfig      — legacy storage scope for the fixed manager API endpoint
//  ServerConfig    — one managed server: ID, name, pinned items, environment grouping
//
//  AppConfigStore reads/writes AppConfig to the SQLite storage file.
// ──────────────────────────────────────────────────────────────────────────────

/// <summary>Top-level application configuration (multi-host aware).</summary>
public sealed class AppConfig
{
    public const string DefaultInstanceKindServer = "server";
    public const string DefaultInstanceKindLocal = "local";

    // ── Fixed manager identity ───────────────────────────────────────────────
    /// <summary>Legacy storage scope. Always points to the Halerith manager endpoint.</summary>
    public string DefaultHostId { get; set; } = ManagerApiDefaults.ManagerScopeId;

    // ── App-wide behaviour (not per-host) ────────────────────────────────────
    public int    StartupTimeoutSeconds    { get; set; } = 60;
    /// <summary>Seconds the launcher waits after fetching the manifest before applying changes.</summary>
    public int    LauncherCountdownSeconds { get; set; } = 3;
    public bool   EnableEventLogging       { get; set; }
    /// <summary>Shows contextual first-use guidance until each tip is acknowledged.</summary>
    public bool   GuidedTipsEnabled        { get; set; } = true;

    // ── Launcher auto-update ────────────────────────────────────────────────
    /// <summary>
    /// When true, the launcher (-launch) mode is allowed to automatically
    /// sync/update content before launching the game.
    /// </summary>
    public bool AllowAutoUpdate { get; set; } = true;

    // ── Backup ──────────────────────────────────────────────────────────────
    /// <summary>When true, replaced/removed files are kept in _backup / _removed folders.</summary>
    public bool KeepBackups             { get; set; } = true;
    /// <summary>Maximum files to retain per backup folder. 0 = unlimited.</summary>
    public int  BackupMaxFilesPerFolder { get; set; } = 20;

    // ── Halerith manager scope ───────────────────────────────────────────────
    public List<HostConfig> Hosts { get; set; } = [];

    // ── Local instances (standalone, no Pterodactyl host) ────────────────
    public List<LocalInstance> LocalInstances { get; set; } = [];

    // ── Default launcher target ─────────────────────────────────────────
    /// <summary>Default instance kind: "server" for manager servers, "local" for local instances.</summary>
    public string DefaultInstanceKind { get; set; } = DefaultInstanceKindServer;

    /// <summary>Default server ID or local instance ID, depending on <see cref="DefaultInstanceKind"/>.</summary>
    public string DefaultInstanceId { get; set; } = "";

    // ── Computed ─────────────────────────────────────────────────────────────
    public HostConfig ActiveHost => EnsureManagerScope();

    public bool HasAnyEnrolledHost => AccountStore.LoadAll().Count > 0;

    public HostConfig EnsureManagerScope()
    {
        var scope = Hosts.FirstOrDefault(h =>
            string.Equals(h.Id, ManagerApiDefaults.ManagerScopeId, StringComparison.OrdinalIgnoreCase))
            ?? Hosts.FirstOrDefault();

        if (scope is null)
        {
            scope = new HostConfig
            {
                Id = ManagerApiDefaults.ManagerScopeId,
                ApiBaseUrl = ManagerApiDefaults.ApiBaseUrl,
            };
            Hosts.Add(scope);
        }

        scope.Id = ManagerApiDefaults.ManagerScopeId;
        scope.ApiBaseUrl = ManagerApiDefaults.ApiBaseUrl;
        DefaultHostId = ManagerApiDefaults.ManagerScopeId;
        return scope;
    }

    /// <summary>
    /// Assembles a flat <see cref="RuntimeConfig"/> for the given host and
    /// server, merging global behaviour settings. Working folders are resolved
    /// through the active platform service and are not user-configurable.
    /// </summary>
    public RuntimeConfig ToRuntimeConfig(HostConfig? host, string? serverPublicIdOverride = null)
    {
        host ??= EnsureManagerScope();
        host.Id = ManagerApiDefaults.ManagerScopeId;
        host.ApiBaseUrl = ManagerApiDefaults.ApiBaseUrl;

        var serverPublicId = serverPublicIdOverride ?? host.DefaultServerPublicId;
        var server   = host.Servers.FirstOrDefault(s => s.ServerPublicId == serverPublicId);

        // When the server is a secondary, use the primary's paths/launcher/rules.
        var effectiveServer = host.ResolveEffectiveServer(serverPublicId);

        var platformPaths = PlatformServices.Current.Paths;
        string gameDataPath, modsFolder, resourcePacksFolder, shadersFolder;

        if (!string.IsNullOrWhiteSpace(effectiveServer?.ServerPublicId))
        {
            var runtimeRoot = platformPaths.RuntimeDirectory;
            var instanceDir = System.IO.Path.Combine(runtimeRoot, "instances", effectiveServer.ServerPublicId);
            gameDataPath        = instanceDir;
            modsFolder          = System.IO.Path.Combine(instanceDir, "mods");
            resourcePacksFolder = System.IO.Path.Combine(instanceDir, "resourcepacks");
            shadersFolder       = System.IO.Path.Combine(instanceDir, "shaderpacks");
        }
        else
        {
            gameDataPath = modsFolder = resourcePacksFolder = shadersFolder = "";
        }

        return new RuntimeConfig
        {
            ApiBaseUrl          = ManagerApiDefaults.ApiBaseUrl,
            HostId              = ManagerApiDefaults.ManagerScopeId,
            ServerPublicId            = serverPublicId,
            Username            = "",
            MinecraftUuid       = ResolveMinecraftUuid(),
            MinecraftAccessToken = ResolveMinecraftAccessToken(),
            HalerithAccessToken = string.Equals(ResolveMinecraftUuid(),
                MinecraftProfileClient.NormalizeUuid(host.MinecraftUuid), StringComparison.OrdinalIgnoreCase)
                ? host.HalerithAccessToken : "",
            HalerithAccessTokenExpiresAtUtc = string.Equals(ResolveMinecraftUuid(),
                MinecraftProfileClient.NormalizeUuid(host.MinecraftUuid), StringComparison.OrdinalIgnoreCase)
                ? host.HalerithAccessTokenExpiresAtUtc : "",
            IgnoreCertificateErrors = host.IgnoreCertificateErrors,
            // Content paths resolved to the per-server runtime instance.
            GameDataPath        = gameDataPath,
            ModsFolder          = modsFolder,
            ResourcePacksFolder = resourcePacksFolder,
            ShadersFolder       = shadersFolder,

            // Working folders are platform-standard and not user-configurable.
            TempFolder          = platformPaths.TempDirectory,

            IgnoreRules         = effectiveServer?.IgnoreRules   ?? [],
            LocalPinnedItems    = effectiveServer?.PinnedItems    ?? [],
            ModpackFilePolicy = ModpackFilePolicies.Normalize(effectiveServer?.ModpackFilePolicy),

            StartupTimeoutSeconds    = StartupTimeoutSeconds,
            LauncherCountdownSeconds = LauncherCountdownSeconds,
            EnableEventLogging       = EnableEventLogging,
            KeepBackups             = KeepBackups,
            BackupMaxFilesPerFolder = BackupMaxFilesPerFolder,
        };
    }

    /// <summary>Convenience overload: uses the active host.</summary>
    public RuntimeConfig ToRuntimeConfig(string? serverPublicIdOverride = null)
        => ToRuntimeConfig(EnsureManagerScope(), serverPublicIdOverride);

    private static string ResolveMinecraftUuid()
        => MinecraftProfileClient.NormalizeUuid(AccountStore.GetDefaultAccountId()) ?? "";

    private static string ResolveMinecraftAccessToken()
    {
        var accountId = ResolveMinecraftUuid();
        if (string.IsNullOrWhiteSpace(accountId))
            return "";

        var account = AccountStore.LoadAll().FirstOrDefault(a =>
            string.Equals(MinecraftProfileClient.NormalizeUuid(a.Uuid), accountId, StringComparison.OrdinalIgnoreCase));

        return account is { IsExpired: false } ? account.McAccessToken : "";
    }
}

public static class ServerPowerStates
{
    public const string Online = "online";
    public const string Restarting = "restarting";
    public const string Offline = "offline";
    public const string Unknown = "unknown";

    public static string Normalize(string? value)
    {
        var clean = value?.Trim().ToLowerInvariant() ?? "";
        return clean switch
        {
            Online => Online,
            Restarting => Restarting,
            "starting" => Restarting,
            "stopping" => Restarting,
            Offline => Offline,
            _ => Unknown,
        };
    }

    public static bool IsLaunchable(string? value)
        => string.Equals(Normalize(value), Online, StringComparison.OrdinalIgnoreCase);
}

/// <summary>Legacy storage scope for the fixed manager API endpoint.</summary>
public sealed class HostConfig
{
    /// <summary>Stable ID for the fixed manager scope.</summary>
    public string Id { get; set; } = "";

    // ── Fixed manager connection ─────────────────────────────────────────────
    public string ApiBaseUrl { get; set; } = ManagerApiDefaults.ApiBaseUrl;
    public string Username   { get; set; } = "";
    public string MinecraftUuid { get; set; } = "";
    public string HalerithAccessToken { get; set; } = "";
    public string HalerithAccessTokenExpiresAtUtc { get; set; } = "";
    public bool IgnoreCertificateErrors { get; set; }

    // ── Servers available through the manager API ────────────────────────────
    public List<ServerConfig> Servers { get; set; } = [];

    /// <summary>Last complete server roster for each Minecraft UUID. This only controls local display.</summary>
    public Dictionary<string, List<string>> VisibleServerIdsByMinecraftUuid { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    public List<ServerConfig> VisibleServersFor(string? minecraftUuid)
    {
        var uuid = MinecraftProfileClient.NormalizeUuid(minecraftUuid ?? "");
        if (uuid is null || !VisibleServerIdsByMinecraftUuid.TryGetValue(uuid, out var ids))
            return [];
        var visibleIds = ids.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return Servers.Where(server => server.IsAvailable && visibleIds.Contains(server.ServerPublicId)).ToList();
    }

    /// <summary>ServerPublicId of the last selected server for this manager scope.</summary>
    public string DefaultServerPublicId { get; set; } = "";

    // ── Computed ──────────────────────────────────────────────────────────────
    public bool IsEnrolled =>
        AccountStore.LoadAll().Count > 0;

    /// <summary>
    /// Resolves the effective server for a given server ID.  If the server is
    /// a secondary, returns its primary; otherwise returns the server itself.
    /// Returns null if the server (or its primary) is not found.
    /// </summary>
    public ServerConfig? ResolveEffectiveServer(string? serverPublicId)
    {
        var server = Servers.FirstOrDefault(s => string.Equals(s.ServerPublicId, serverPublicId, StringComparison.OrdinalIgnoreCase));
        if (server is null) return null;
        if (!server.IsSecondary || string.IsNullOrWhiteSpace(server.PrimaryServerPublicId)) return server;
        return Servers.FirstOrDefault(s => string.Equals(s.ServerPublicId, server.PrimaryServerPublicId, StringComparison.OrdinalIgnoreCase)) ?? server;
    }

    /// <summary>
    /// Copies each primary server's runtime version into its secondaries.
    /// Secondary servers mirror the primary runtime and should not retain stale
    /// MC/loader values after the primary changes version.
    /// </summary>
    public bool AlignSecondaryRuntimeVersions()
    {
        var changed = false;

        foreach (var secondary in Servers.Where(s => s.IsSecondary))
        {
            if (string.IsNullOrWhiteSpace(secondary.PrimaryServerPublicId))
                continue;

            var primary = Servers.FirstOrDefault(s =>
                !s.IsSecondary &&
                string.Equals(s.ServerPublicId, secondary.PrimaryServerPublicId, StringComparison.OrdinalIgnoreCase));
            if (primary is null)
                continue;

            if (!string.Equals(secondary.MinecraftVersion, primary.MinecraftVersion, StringComparison.Ordinal))
            {
                secondary.MinecraftVersion = primary.MinecraftVersion;
                changed = true;
            }

            if (!string.Equals(secondary.Loader, primary.Loader, StringComparison.Ordinal))
            {
                secondary.Loader = primary.Loader;
                changed = true;
            }

            if (!string.Equals(secondary.LoaderVersion, primary.LoaderVersion, StringComparison.Ordinal))
            {
                secondary.LoaderVersion = primary.LoaderVersion;
                changed = true;
            }
        }

        return changed;
    }

    /// <summary>
    /// Returns all servers that share the same primary - i.e. the primary itself
    /// plus all its secondaries.  If <paramref name="serverPublicId"/> is a secondary,
    /// the group is based on its primary.
    /// </summary>
    public List<ServerConfig> GetServerGroup(string? serverPublicId)
    {
        var effective = ResolveEffectiveServer(serverPublicId);
        if (effective is null) return [];
        var primaryId = effective.ServerPublicId;
        return Servers.Where(s =>
            string.Equals(s.ServerPublicId, primaryId, StringComparison.OrdinalIgnoreCase) ||
            (s.IsSecondary && string.Equals(s.PrimaryServerPublicId, primaryId, StringComparison.OrdinalIgnoreCase))
        ).ToList();
    }

    /// <summary>Returns a display-friendly name for the fixed manager scope.</summary>
    public string GetDisplayName()
    {
        return "Halerith";
    }

    /// <summary>Returns a short status string suitable for manager scope status displays.</summary>
    public string StatusDisplay => IsEnrolled ? "Ok" : "Pending";

    /// <summary>
    /// Returns the stable ID used by the fixed manager scope.
    /// </summary>
    public static string DeriveId(string apiBaseUrl) => ManagerApiDefaults.ManagerScopeId;
}

/// <summary>Configuration for one server returned by the manager API.</summary>
public sealed class ServerConfig
{
    public string ModpackFilePolicy { get; set; } = ModpackFilePolicies.Preserve;
    public string ServerPublicId   { get; set; } = "";
    public string ServerName { get; set; } = "";
    public string EnvironmentId { get; set; } = "";
    public string EnvironmentName { get; set; } = "";
    /// <summary>
    /// True when the server currently appears in the manager health response.
    /// Servers removed from a successful health response are deleted from local storage.
    /// </summary>
    public bool IsAvailable { get; set; } = true;

    // ── Server game info (populated from health/manifest responses) ─────────
    /// <summary>Minecraft version reported by the server (e.g. "1.20.1").</summary>
    public string MinecraftVersion { get; set; } = "";
    /// <summary>Mod loader reported by the server (e.g. "fabric", "forge", or "" for vanilla).</summary>
    public string Loader           { get; set; } = "";
    /// <summary>Loader version reported by the server (e.g. "0.16.9" for Fabric).</summary>
    public string LoaderVersion    { get; set; } = "";
    /// <summary>Raw MOTD reported from server.properties during manager health.</summary>
    public string Motd             { get; set; } = "";

    // ── Connection info (refreshed from each health response) ──────
    /// <summary>Effective public hostname or IP players use to connect.</summary>
    public string GameHost { get; set; } = "";
    /// <summary>Compatibility alias for older launcher call sites.</summary>
    public string NodeFqdn
    {
        get => GameHost;
        set => GameHost = value;
    }
    /// <summary>Game port allocation (e.g. 25565). Null when not yet known.</summary>
    public int?   Port     { get; set; }
    /// <summary>Last address this Client injected into servers.dat for this server.</summary>
    public string LastInjectedServerAddress { get; set; } = "";

    /// <summary>Last power state reported by manager health: online, restarting, offline, or unknown.</summary>
    public string PowerStatus { get; set; } = ServerPowerStates.Unknown;

    /// <summary>Authenticated verified PNG/JPEG server icon URL, when supplied by health.</summary>
    public string ServerIconUrl { get; set; } = "";

    /// <summary>SHA-256 checksum for the current server icon bytes.</summary>
    public string ServerIconSha256 { get; set; } = "";

    /// <summary>Verified content type for the current server icon: image/png or image/jpeg.</summary>
    public string ServerIconContentType { get; set; } = "";

    /// <summary>Local cached verified server icon path for the current icon hash. Runtime only.</summary>
    [JsonIgnore]
    public string ServerIconLocalPath { get; set; } = "";

    // ── Java / JVM settings (per-server) ────────────────────────────────────────
    /// <summary>"Auto" (default MC launch), "Manual" (max memory + GC), or "Custom" (raw JVM args).</summary>
    public string JavaMode { get; set; } = "Auto";
    /// <summary>Legacy minimum heap size in MB. Manual mode now uses fixed GC-specific defaults.</summary>
    public int JavaMinMemoryMb { get; set; }
    /// <summary>Maximum heap size in MB (Manual mode). 0 = use the default.</summary>
    public int JavaMaxMemoryMb { get; set; }
    /// <summary>Garbage collector selected for Manual mode.</summary>
    public string JavaGarbageCollector { get; set; } = JavaLaunchSettings.GarbageCollectorZgc;
    /// <summary>Legacy additional JVM flags. Manual mode no longer reads this; use Custom mode for raw JVM flags.</summary>
    public string JavaExtraArgs { get; set; } = "";
    /// <summary>Raw JVM arguments for Custom mode.</summary>
    public string JavaCustomArgs { get; set; } = "";

    // ── Client rules (per-server) ─────────────────────────────────────────────
    /// <summary>Ignore rules for this server's content paths.</summary>
    public List<IgnoreRule>      IgnoreRules { get; set; } = [];
    /// <summary>Pinned items are per-server (different servers have different mod lists).</summary>
    public List<LocalPinnedItem> PinnedItems { get; set; } = [];

    // ── Launcher account (per-server override of global default) ────────────
    /// <summary>
    /// Minecraft UUID of the account to use when launching this server.
    /// Empty = use the global default from <see cref="AccountStore"/>.
    /// </summary>
    public string SelectedAccountId { get; set; } = "";

    // ── Secondary server linkage ──────────────────────────────────────────────
    /// <summary>
    /// When true, this server is a secondary that mirrors its primary server's
    /// paths, launcher settings, and rules.  The main screen shows the primary's
    /// data and syncing a secondary delegates to the primary.
    /// </summary>
    public bool IsSecondary { get; set; }

    /// <summary>
    /// UUID of the primary server that this secondary mirrors.
    /// Only meaningful when <see cref="IsSecondary"/> is true.
    /// </summary>
    public string PrimaryServerPublicId { get; set; } = "";
}
