namespace HalerithAddonClient.Core;

/// <summary>
/// Flat configuration model resolved for the active Halerith manager server,
/// backed by the SQLite app configuration store.
/// </summary>
public sealed class RuntimeConfig
{
    // ── Connection / Authentication ──────────────────────────────────
    public string ApiBaseUrl { get; set; } = "";
    public string HostId { get; set; } = "";
    public string ServerPublicId { get; set; } = "";
    public string Username { get; set; } = "";
    public string MinecraftUuid { get; set; } = "";
    public string MinecraftAccessToken { get; set; } = "";
    public string HalerithAccessToken { get; set; } = "";
    public string HalerithAccessTokenExpiresAtUtc { get; set; } = "";
    public bool IgnoreCertificateErrors { get; set; }

    // ── Game data path ───────────────────────────────────────────────
    /// <summary>Resolved game data folder for the active runtime instance.</summary>
    public string GameDataPath { get; set; } = "";

    // ── Content Folders ──────────────────────────────────────────────
    public string ModsFolder { get; set; } = "";
    public string ResourcePacksFolder { get; set; } = "";
    public string ShadersFolder { get; set; } = "";

    // ── Working Folders ──────────────────────────────────────────────
    public string TempFolder { get; set; } = "";

    // ── Client Rules ─────────────────────────────────────────────────
    public List<IgnoreRule> IgnoreRules { get; set; } = [];
    public List<LocalPinnedItem> LocalPinnedItems { get; set; } = [];
    public string ModpackFilePolicy { get; set; } = ModpackFilePolicies.Preserve;

    // ── Behavior ─────────────────────────────────────────────────────
    public int StartupTimeoutSeconds    { get; set; } = 60;
    /// <summary>Seconds the launcher waits after fetching the manifest before applying changes.</summary>
    public int LauncherCountdownSeconds { get; set; } = 3;
    public bool EnableEventLogging { get; set; }

    // ── Backup ──────────────────────────────────────────────────────
    /// <summary>When true, replaced/removed files are kept in _backup / _removed folders.</summary>
    public bool KeepBackups             { get; set; } = true;
    /// <summary>Maximum files to retain per backup folder. 0 = unlimited.</summary>
    public int  BackupMaxFilesPerFolder { get; set; } = 20;

    // ── Computed ─────────────────────────────────────────────────────

    /// <summary>Returns true when the client can authenticate to the Halerith manager API.</summary>
    public bool IsEnrolled =>
        !string.IsNullOrWhiteSpace(MinecraftUuid) &&
        (!string.IsNullOrWhiteSpace(HalerithAccessToken) || !string.IsNullOrWhiteSpace(MinecraftAccessToken));
}
