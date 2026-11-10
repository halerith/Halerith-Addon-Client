namespace HalerithAddonClient.Core;

// ──────────────────────────────────────────────────────────────────────────────
//  Local instance model
//
//  A LocalInstance is a standalone Minecraft environment created by the user
//  (not tied to a Pterodactyl host).  It stores the MC version, loader, and
//  loader version chosen at creation time and uses the shared build
//  infrastructure (GameEnvironmentBuilder / RuntimeDatabase) for the actual
//  game files.  Each instance gets its own GUID and instance folder under
//  Runtime/instances/{guid}.
//
//  Persisted in the SQLite app storage file.
// ──────────────────────────────────────────────────────────────────────────────

/// <summary>
/// A user-created local Minecraft instance (no remote Pterodactyl server).
/// </summary>
public sealed class LocalInstance
{
    /// <summary>Stable GUID assigned at creation time.</summary>
    public string Id { get; set; } = "";

    /// <summary>User-chosen display name (e.g. "Survival 1.21").</summary>
    public string Name { get; set; } = "";

    /// <summary>Minecraft release version (e.g. "1.21.4").</summary>
    public string MinecraftVersion { get; set; } = "";

    /// <summary>
    /// Mod loader identifier: "fabric", "forge", "neoforge", "quilt", or "" for vanilla.
    /// Matches the values used by <see cref="GameEnvironmentBuilder"/>.
    /// </summary>
    public string Loader { get; set; } = "";

    /// <summary>Loader version string (e.g. "0.16.9" for Fabric). Empty for vanilla.</summary>
    public string LoaderVersion { get; set; } = "";

    /// <summary>
    /// Optional server address the user wants to auto-connect to (e.g. "play.example.com:25565").
    /// Empty for single-player instances.
    /// </summary>
    public string ServerAddress { get; set; } = "";

    /// <summary>
    /// Minecraft UUID of the account to use when launching this instance.
    /// Empty = use the global default from <see cref="AccountStore"/>.
    /// </summary>
    public string SelectedAccountId { get; set; } = "";

    /// <summary>UTC timestamp when this instance was created.</summary>
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    /// <summary>UTC timestamp of the last successful launch. Null if never launched.</summary>
    public DateTime? LastLaunchedAtUtc { get; set; }

    // ── Java Launch Settings ─────────────────────────────────────────────

    /// <summary>JVM launch mode: "Auto", "Manual", or "Custom".</summary>
    public string JavaMode { get; set; } = "Auto";

    /// <summary>Legacy minimum heap size in MB. Manual mode now uses fixed GC-specific defaults.</summary>
    public int JavaMinMemoryMb { get; set; }

    /// <summary>Maximum heap size in MB (-Xmx). 0 = use the default.</summary>
    public int JavaMaxMemoryMb { get; set; }

    /// <summary>Garbage collector selected for Manual mode.</summary>
    public string JavaGarbageCollector { get; set; } = JavaLaunchSettings.GarbageCollectorZgc;

    /// <summary>Legacy extra JVM flags. Manual mode no longer reads this; use Custom mode for raw JVM flags.</summary>
    public string JavaExtraArgs { get; set; } = "";

    /// <summary>Full JVM argument string (Custom mode).</summary>
    public string JavaCustomArgs { get; set; } = "";

    // ── Computed ──────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the instance folder path under Runtime/instances/{guid}.
    /// </summary>
    public string GetInstanceDirectory()
        => Path.Combine(LauncherPaths.GetAppDataDirectory(), "Runtime", "instances", Id);

    /// <summary>
    /// Returns a display-friendly loader label.  "Vanilla" when no loader is set,
    /// otherwise the loader name with a capital first letter (e.g. "Fabric 0.16.9").
    /// </summary>
    public string LoaderDisplayName
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Loader))
                return "Vanilla";

            var name = char.ToUpperInvariant(Loader[0]) + Loader[1..];
            return string.IsNullOrWhiteSpace(LoaderVersion) ? name : $"{name} {LoaderVersion}";
        }
    }

    /// <summary>Short summary line: "1.21.4 — Fabric 0.16.9".</summary>
    public string VersionSummary =>
        string.IsNullOrWhiteSpace(Loader)
            ? $"{MinecraftVersion} — Vanilla"
            : $"{MinecraftVersion} — {LoaderDisplayName}";
}
