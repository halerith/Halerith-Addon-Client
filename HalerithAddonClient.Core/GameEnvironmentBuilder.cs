using System.Security.Cryptography;
using System.Text.Json;

namespace HalerithAddonClient.Core;

// ══════════════════════════════════════════════════════════════════════════════
//  Readiness Report
// ══════════════════════════════════════════════════════════════════════════════

/// <summary>Describes the readiness state of one component of the game environment.</summary>
public sealed class ReadinessItem
{
    public string Category { get; init; } = "";
    public string Label    { get; init; } = "";
    public bool   Present  { get; init; }
    /// <summary>Local file path (when present) or expected path (when missing).</summary>
    public string Path     { get; init; } = "";
    /// <summary>Download URL when missing.</summary>
    public string Url      { get; init; } = "";
    /// <summary>Expected SHA1 for verification (empty if not available).</summary>
    public string Sha1     { get; init; } = "";
    /// <summary>Expected file size in bytes (0 if unknown).</summary>
    public long   Size     { get; init; }
}

/// <summary>Full readiness report for a game environment.</summary>
public sealed class ReadinessReport
{
    public string MinecraftVersion { get; init; } = "";
    public string Loader           { get; init; } = "";
    public string LoaderVersion    { get; init; } = "";
    public string JavaComponent    { get; init; } = "";
    public int    JavaMajorVersion { get; init; }

    public List<ReadinessItem> Items { get; init; } = [];

    public bool IsReady => Items.All(i => i.Present);
    public int  MissingCount => Items.Count(i => !i.Present);
    public long MissingBytes => Items.Where(i => !i.Present).Sum(i => i.Size);
}

// ══════════════════════════════════════════════════════════════════════════════
//  Resolved Game Environment
// ══════════════════════════════════════════════════════════════════════════════

/// <summary>
/// A fully resolved game environment — all paths verified to exist.
/// Passed to <see cref="MinecraftLauncher"/> to build the JVM command line.
/// </summary>
public sealed class GameEnvironment
{
    /// <summary>Root directory for the runtime files (versions, libraries, assets, java).</summary>
    public string RuntimeRoot    { get; init; } = "";
    public string MinecraftVersion { get; init; } = "";
    public string Loader         { get; init; } = "";
    public string MainClass      { get; init; } = "";
    public string AssetIndexId   { get; init; } = "";
    public string Log4jConfigurationPath { get; init; } = "";

    /// <summary>Path to the client JAR.</summary>
    public string ClientJarPath  { get; init; } = "";

    /// <summary>Ordered list of library JAR paths that form the classpath.</summary>
    public List<string> LibraryPaths { get; init; } = [];

    /// <summary>Path to the java executable.</summary>
    public string JavaExecutable { get; init; } = "";

    /// <summary>Path to the assets directory.</summary>
    public string AssetsDirectory { get; init; } = "";

    /// <summary>Path to the natives directory (extracted native libs).</summary>
    public string NativesDirectory { get; init; } = "";

    /// <summary>The parsed version detail JSON (for argument template evaluation).</summary>
    public MojangVersionDetail VersionDetail { get; init; } = new();

    /// <summary>
    /// Additional JVM arguments from the mod loader (e.g. Forge/NeoForge version.json).
    /// Applied after the vanilla JVM arguments but before the main class.
    /// </summary>
    public List<string> ExtraJvmArgs { get; init; } = [];

    /// <summary>
    /// Additional game arguments from the mod loader (e.g. Forge/NeoForge version.json).
    /// Applied after the vanilla game arguments.
    /// </summary>
    public List<string> ExtraGameArgs { get; init; } = [];
}

// ══════════════════════════════════════════════════════════════════════════════
//  Game Environment Builder
// ══════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Resolves, checks, and builds a complete Minecraft game environment under
/// <c>%LOCALAPPDATA%\Halerith Addon Client\Runtime\</c>.
///
/// Directory layout:
///   Runtime/
///     versions/{mcVersion}/{mcVersion}.json, {mcVersion}.jar
///     libraries/{maven-path}/artifact.jar
///     assets/indexes/{assetId}.json
///     assets/objects/{hash[0..2]}/{hash}
///     java/{component}/bin/java.exe
///     java/{component}/jre.bundle/Contents/Home/bin/java (macOS)
///     fabric/{mcVersion}-{loaderVersion}.json  (merged profile)
/// </summary>
public sealed class GameEnvironmentBuilder : IDisposable
{
    private readonly MojangMetaClient _mojang;
    private readonly FabricMetaClient _fabric;
    private readonly QuiltMetaClient _quilt;
    private readonly ForgeMetaClient _forgeMeta;
    private readonly NeoForgeMetaClient _neoForgeMeta;
    private readonly ForgeInstallerService _forgeInstaller;
    private readonly RuntimeDatabase? _db;
    private readonly string _runtimeRoot;

    public GameEnvironmentBuilder(
        MojangMetaClient? mojangClient = null,
        FabricMetaClient? fabricClient = null,
        QuiltMetaClient? quiltClient = null,
        ForgeMetaClient? forgeMetaClient = null,
        NeoForgeMetaClient? neoForgeMetaClient = null,
        ForgeInstallerService? forgeInstallerService = null,
        RuntimeDatabase? database = null)
    {
        _mojang = mojangClient ?? new MojangMetaClient();
        _fabric = fabricClient ?? new FabricMetaClient();
        _quilt = quiltClient ?? new QuiltMetaClient();
        _forgeMeta = forgeMetaClient ?? new ForgeMetaClient();
        _neoForgeMeta = neoForgeMetaClient ?? new NeoForgeMetaClient();
        _runtimeRoot = Path.Combine(LauncherPaths.GetAppDataDirectory(), "Runtime");
        _forgeInstaller = forgeInstallerService ?? new ForgeInstallerService(_runtimeRoot);
        _db = database;
    }

    /// <summary>The root directory where all runtime files are stored.</summary>
    public string RuntimeRoot => _runtimeRoot;

    // ══════════════════════════════════════════════════════════════════════
    //  Check readiness
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Checks what's present and what's missing for the given Minecraft version + loader.
    /// Fetches metadata from Mojang and Fabric APIs to determine the full dependency tree.
    /// </summary>
    public async Task<ReadinessReport> CheckReadinessAsync(string minecraftVersion, string loader, string loaderVersion = "", CancellationToken ct = default)
    {
        var items = new List<ReadinessItem>();

        // 1. Fetch Mojang version manifest and find our version
        var manifest = await _mojang.GetVersionManifestAsync(ct).ConfigureAwait(false);
        var versionEntry = manifest.Versions.FirstOrDefault(v =>
            string.Equals(v.Id, minecraftVersion, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Minecraft version {minecraftVersion} not found in Mojang manifest.");

        // 2. Fetch version detail
        var versionJsonPath = Path.Combine(_runtimeRoot, "versions", minecraftVersion, $"{minecraftVersion}.json");
        MojangVersionDetail versionDetail;

        if (File.Exists(versionJsonPath))
        {
            var cached = await File.ReadAllTextAsync(versionJsonPath, ct).ConfigureAwait(false);
            versionDetail = JsonSerializer.Deserialize<MojangVersionDetail>(cached, SerializerOptions.Default)!;
        }
        else
        {
            versionDetail = await _mojang.GetVersionDetailAsync(versionEntry.Url, ct).ConfigureAwait(false);
        }

        items.Add(new ReadinessItem
        {
            Category = "Version",
            Label = $"Version JSON ({minecraftVersion})",
            Present = File.Exists(versionJsonPath),
            Path = versionJsonPath,
            Url = versionEntry.Url,
            Sha1 = versionEntry.Sha1,
        });

        // 3. Client JAR
        var clientJarPath = Path.Combine(_runtimeRoot, "versions", minecraftVersion, $"{minecraftVersion}.jar");
        items.Add(new ReadinessItem
        {
            Category = "Version",
            Label = $"Client JAR ({minecraftVersion})",
            Present = File.Exists(clientJarPath),
            Path = clientJarPath,
            Url = versionDetail.Downloads?.Client?.Url ?? "",
            Sha1 = versionDetail.Downloads?.Client?.Sha1 ?? "",
            Size = versionDetail.Downloads?.Client?.Size ?? 0,
        });

        // 4. Libraries (vanilla — filtered by OS rules)
        foreach (var lib in versionDetail.Libraries)
        {
            if (!lib.AppliesToCurrentOs()) continue;

            var libPath = Path.Combine(_runtimeRoot, "libraries", lib.ToRelativePath());
            var downloadUrl = lib.GetDownloadUrl() ?? "";
            var sha1 = lib.Downloads?.Artifact?.Sha1 ?? "";
            var size = lib.Downloads?.Artifact?.Size ?? 0;

            items.Add(new ReadinessItem
            {
                Category = "Library",
                Label = lib.Name,
                Present = File.Exists(libPath),
                Path = libPath,
                Url = downloadUrl,
                Sha1 = sha1,
                Size = size,
            });
        }

        // 5. Asset index
        var assetIndexId = versionDetail.AssetIndex?.Id ?? versionDetail.Assets;
        var assetIndexPath = Path.Combine(_runtimeRoot, "assets", "indexes", $"{assetIndexId}.json");
        items.Add(new ReadinessItem
        {
            Category = "Assets",
            Label = $"Asset Index ({assetIndexId})",
            Present = File.Exists(assetIndexPath),
            Path = assetIndexPath,
            Url = versionDetail.AssetIndex?.Url ?? "",
            Sha1 = versionDetail.AssetIndex?.Sha1 ?? "",
            Size = versionDetail.AssetIndex?.Size ?? 0,
        });

        // 6. Log4j configuration
        var logConfig = GetClientLoggingConfig(versionDetail);
        if (logConfig is not null)
        {
            items.Add(new ReadinessItem
            {
                Category = "Logging",
                Label = $"Log4j Config ({logConfig.Value.Id})",
                Present = File.Exists(logConfig.Value.Path),
                Path = logConfig.Value.Path,
                Url = logConfig.Value.Url,
                Sha1 = logConfig.Value.Sha1,
                Size = logConfig.Value.Size,
            });
        }

        // 7. Asset objects (only check if we have the index)
        if (File.Exists(assetIndexPath))
        {
            var assetIndex = JsonSerializer.Deserialize<MojangAssetIndexDetail>(
                await File.ReadAllTextAsync(assetIndexPath, ct).ConfigureAwait(false), SerializerOptions.Default);
            if (assetIndex is not null)
            {
                int present = 0, missing = 0;
                long missingSize = 0;
                foreach (var obj in assetIndex.Objects.Values)
                {
                    var objPath = Path.Combine(_runtimeRoot, "assets", "objects",
                        obj.Hash[..2], obj.Hash);
                    if (File.Exists(objPath)) present++;
                    else { missing++; missingSize += obj.Size; }
                }

                // Report assets as a summary rather than individual items
                items.Add(new ReadinessItem
                {
                    Category = "Assets",
                    Label = missing > 0
                        ? $"Asset Objects ({present} present, {missing} missing)"
                        : $"Asset Objects ({present} files)",
                    Present = missing == 0,
                    Path = Path.Combine(_runtimeRoot, "assets", "objects"),
                    Url = "", // Downloaded individually
                    Size = missingSize,
                });
            }
        }
        else
        {
            // Can't check assets without the index — report as missing
            items.Add(new ReadinessItem
            {
                Category = "Assets",
                Label = "Asset Objects (index not downloaded yet)",
                Present = false,
                Path = Path.Combine(_runtimeRoot, "assets", "objects"),
            });
        }

        // 8. Java runtime
        var javaComponent = versionDetail.JavaVersion?.Component ?? "java-runtime-gamma";
        var javaMajor = versionDetail.JavaVersion?.MajorVersion ?? 17;
        var javaExe = JavaRuntimePaths.FindExecutable(_runtimeRoot, javaComponent)
            ?? JavaRuntimePaths.PreferredExecutablePath(_runtimeRoot, javaComponent);
        items.Add(new ReadinessItem
        {
            Category = "Java",
            Label = $"Java {javaMajor} ({javaComponent})",
            Present = JavaRuntimePaths.IsUsableExecutable(javaExe),
            Path = javaExe,
        });

        // 9. Mod loader (if applicable)
        //    Plugin loaders (spigot, paper, etc.) are server-side only and need
        //    no client-side libraries — they are treated as vanilla.
        string resolvedLoaderVersion = "";

        if (LoaderClassification.IsVanillaLike(loader))
        {
            // Nothing extra to check — vanilla Minecraft or server-side plugin loader.
        }
        else if (string.Equals(loader, "fabric", StringComparison.OrdinalIgnoreCase))
        {
            var fabricLoader = await _fabric.GetLoaderAsync(minecraftVersion, loaderVersion, ct).ConfigureAwait(false);
            if (fabricLoader is null)
                throw new InvalidOperationException($"No Fabric loader found for Minecraft {minecraftVersion}.");

            resolvedLoaderVersion = fabricLoader.Loader?.Version ?? "";

            // Fabric intermediary
            if (fabricLoader.Intermediary is not null)
            {
                var intPath = Path.Combine(_runtimeRoot, "libraries",
                    ToMavenPath(fabricLoader.Intermediary.Maven));
                items.Add(new ReadinessItem
                {
                    Category = "Fabric",
                    Label = $"Intermediary ({fabricLoader.Intermediary.Version})",
                    Present = File.Exists(intPath),
                    Path = intPath,
                    Url = $"https://maven.fabricmc.net/{ToMavenPath(fabricLoader.Intermediary.Maven)}",
                });
            }

            // Fabric loader JAR
            if (fabricLoader.Loader is not null)
            {
                var loaderPath = Path.Combine(_runtimeRoot, "libraries",
                    ToMavenPath(fabricLoader.Loader.Maven));
                items.Add(new ReadinessItem
                {
                    Category = "Fabric",
                    Label = $"Fabric Loader ({fabricLoader.Loader.Version})",
                    Present = File.Exists(loaderPath),
                    Path = loaderPath,
                    Url = $"https://maven.fabricmc.net/{ToMavenPath(fabricLoader.Loader.Maven)}",
                });
            }

            // Fabric launcher libraries (common + client)
            if (fabricLoader.LauncherMeta?.Libraries is not null)
            {
                var allFabricLibs = new List<FabricLibraryEntry>();
                allFabricLibs.AddRange(fabricLoader.LauncherMeta.Libraries.Common);
                allFabricLibs.AddRange(fabricLoader.LauncherMeta.Libraries.Client);

                foreach (var fLib in allFabricLibs)
                {
                    var libPath = Path.Combine(_runtimeRoot, "libraries", fLib.ToRelativePath());
                    items.Add(new ReadinessItem
                    {
                        Category = "Fabric",
                        Label = fLib.Name,
                        Present = File.Exists(libPath),
                        Path = libPath,
                        Url = fLib.GetDownloadUrl(),
                    });
                }
            }
        }
        else if (string.Equals(loader, "quilt", StringComparison.OrdinalIgnoreCase))
        {
            var quiltLoader = await _quilt.GetLoaderAsync(minecraftVersion, loaderVersion, ct).ConfigureAwait(false);
            if (quiltLoader is null)
                throw new InvalidOperationException($"No Quilt loader found for Minecraft {minecraftVersion}.");

            resolvedLoaderVersion = quiltLoader.Loader?.Version ?? "";

            // Quilt hashed mappings (preferred) or intermediary fallback
            var mappingsMaven = quiltLoader.Hashed?.Maven ?? quiltLoader.Intermediary?.Maven;
            var mappingsVersion = quiltLoader.Hashed?.Version ?? quiltLoader.Intermediary?.Version;
            if (!string.IsNullOrWhiteSpace(mappingsMaven))
            {
                var intPath = Path.Combine(_runtimeRoot, "libraries", ToMavenPath(mappingsMaven));
                items.Add(new ReadinessItem
                {
                    Category = "Quilt",
                    Label = $"Mappings ({mappingsVersion})",
                    Present = File.Exists(intPath),
                    Path = intPath,
                    Url = $"https://maven.quiltmc.org/repository/release/{ToMavenPath(mappingsMaven)}",
                });
            }

            // Quilt loader JAR
            if (quiltLoader.Loader is not null)
            {
                var loaderPath = Path.Combine(_runtimeRoot, "libraries",
                    ToMavenPath(quiltLoader.Loader.Maven));
                items.Add(new ReadinessItem
                {
                    Category = "Quilt",
                    Label = $"Quilt Loader ({quiltLoader.Loader.Version})",
                    Present = File.Exists(loaderPath),
                    Path = loaderPath,
                    Url = $"https://maven.quiltmc.org/repository/release/{ToMavenPath(quiltLoader.Loader.Maven)}",
                });
            }

            // Quilt launcher libraries (common + client)
            if (quiltLoader.LauncherMeta?.Libraries is not null)
            {
                var allQuiltLibs = new List<QuiltLibraryEntry>();
                allQuiltLibs.AddRange(quiltLoader.LauncherMeta.Libraries.Common);
                allQuiltLibs.AddRange(quiltLoader.LauncherMeta.Libraries.Client);

                foreach (var qLib in allQuiltLibs)
                {
                    var libPath = Path.Combine(_runtimeRoot, "libraries", qLib.ToRelativePath());
                    items.Add(new ReadinessItem
                    {
                        Category = "Quilt",
                        Label = qLib.Name,
                        Present = File.Exists(libPath),
                        Path = libPath,
                        Url = qLib.GetDownloadUrl(),
                    });
                }
            }
        }
        else if (string.Equals(loader, "forge", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(loader, "neoforge", StringComparison.OrdinalIgnoreCase))
        {
            var isForge = string.Equals(loader, "forge", StringComparison.OrdinalIgnoreCase);
            var category = isForge ? "Forge" : "NeoForge";

            // Resolve loader version if not specified
            string resolvedForgeVersion;
            if (!string.IsNullOrWhiteSpace(loaderVersion))
            {
                resolvedForgeVersion = loaderVersion;
            }
            else
            {
                string? latest;
                if (isForge)
                    latest = await _forgeMeta.GetLatestVersionAsync(minecraftVersion, ct).ConfigureAwait(false);
                else
                    latest = await _neoForgeMeta.GetLatestVersionAsync(minecraftVersion, ct).ConfigureAwait(false);

                resolvedForgeVersion = latest
                    ?? throw new InvalidOperationException($"No {category} version found for Minecraft {minecraftVersion}.");
            }

            resolvedLoaderVersion = resolvedForgeVersion;

            // Check if installer has already run
            var installed = _forgeInstaller.IsInstalled(loader.ToLowerInvariant(), resolvedForgeVersion);

            items.Add(new ReadinessItem
            {
                Category = category,
                Label = $"{category} Installer ({resolvedForgeVersion})",
                Present = installed,
                Path = _forgeInstaller.GetInstallerCacheDir(loader.ToLowerInvariant(), resolvedForgeVersion),
            });

            // If installer has completed, check the version.json libraries
            if (installed)
            {
                var forgeVersionJson = await _forgeInstaller.LoadVersionJsonAsync(loader.ToLowerInvariant(), resolvedForgeVersion, ct).ConfigureAwait(false);
                if (forgeVersionJson is not null)
                {
                    foreach (var lib in forgeVersionJson.Libraries)
                    {
                        if (!lib.AppliesToCurrentOs()) continue;
                        var libPath = Path.Combine(_runtimeRoot, "libraries", lib.ToRelativePath());

                        // Only set a download URL for libraries that have genuine download
                        // metadata. Libraries without Downloads.Artifact.Url or an explicit
                        // Url are processor outputs (e.g. the patched client JAR) — they
                        // were created during installation, not downloaded.
                        var hasMojangUrl = !string.IsNullOrWhiteSpace(lib.Downloads?.Artifact?.Url);
                        var hasExplicitUrl = !string.IsNullOrWhiteSpace(lib.Url);
                        var downloadUrl = (hasMojangUrl || hasExplicitUrl)
                            ? lib.GetDownloadUrl() ?? ""
                            : "";

                        items.Add(new ReadinessItem
                        {
                            Category = category,
                            Label = lib.Name,
                            Present = File.Exists(libPath),
                            Path = libPath,
                            Url = downloadUrl,
                            Sha1 = lib.Downloads?.Artifact?.Sha1 ?? "",
                            Size = lib.Downloads?.Artifact?.Size ?? 0,
                        });
                    }
                }
            }
        }
        else
        {
            throw new InvalidOperationException(
                $"Unknown mod loader '{loader}'. Supported loaders: fabric, quilt, forge, neoforge.");
        }

        return new ReadinessReport
        {
            MinecraftVersion = minecraftVersion,
            Loader = loader,
            LoaderVersion = resolvedLoaderVersion,
            JavaComponent = versionDetail.JavaVersion?.Component ?? "java-runtime-gamma",
            JavaMajorVersion = versionDetail.JavaVersion?.MajorVersion ?? 17,
            Items = items,
        };
    }

    // ══════════════════════════════════════════════════════════════════════
    //  Build (download missing items)
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Downloads all missing items from the readiness report.
    /// Asset objects are handled specially — they require the asset index to enumerate.
    /// </summary>
    public async Task BuildAsync(ReadinessReport report,
        IProgress<(int completed, int total, string label)>? progress = null,
        CancellationToken ct = default)
    {
        var missing = report.Items.Where(i => !i.Present).ToList();
        int completed = 0;
        int total = missing.Count;

        foreach (var item in missing)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report((completed, total, item.Label));

            if (item.Category == "Assets" && item.Label.Contains("Asset Objects"))
            {
                // Download asset objects individually from the asset index.
                // Use the specific asset index file for this MC version, not a wildcard scan.
                var assetIndexId = report.Items
                    .Where(i => i.Category == "Assets" && i.Label.Contains("Asset Index"))
                    .Select(i => i.Path)
                    .FirstOrDefault();
                await DownloadAssetObjectsAsync(assetIndexId, progress, ct).ConfigureAwait(false);
            }
            else if (item.Category == "Java")
            {
                await DownloadJavaRuntimeAsync(report.JavaComponent, progress, ct).ConfigureAwait(false);
            }
            else if ((item.Category == "Forge" || item.Category == "NeoForge") &&
                     item.Label.Contains("Installer"))
            {
                // Run the Forge/NeoForge installer pipeline (download, extract, process)
                var loaderKey = item.Category.ToLowerInvariant();
                var isForge = item.Category == "Forge";
                var installerUrl = isForge
                    ? _forgeMeta.GetInstallerUrl(report.LoaderVersion)
                    : _neoForgeMeta.GetInstallerUrl(report.LoaderVersion);

                await _forgeInstaller.InstallAsync(
                    loaderKey, report.LoaderVersion, report.MinecraftVersion,
                    installerUrl, progress, ct).ConfigureAwait(false);
            }
            else if (!string.IsNullOrWhiteSpace(item.Url))
            {
                var sha1 = !string.IsNullOrWhiteSpace(item.Sha1) ? item.Sha1 : null;
                await DownloadWithMavenFallbackAsync(item.Url, item.Path, sha1, ct).ConfigureAwait(false);
            }

            completed++;
            progress?.Report((completed, total, item.Label));
        }
    }

    /// <summary>
    /// Known Maven repositories for Forge/NeoForge library fallback.
    /// </summary>
    private static readonly string[] MavenRepos =
    [
        "https://maven.minecraftforge.net/",
        "https://maven.neoforged.net/releases/",
        "https://libraries.minecraft.net/",
        "https://repo.maven.apache.org/maven2/",
    ];

    /// <summary>
    /// Downloads a library file, trying the primary URL first then falling back
    /// to known Maven repos if the primary returns a non-success status.
    /// </summary>
    private async Task DownloadWithMavenFallbackAsync(
        string primaryUrl, string destPath, string? sha1, CancellationToken ct)
    {
        // Try the primary URL first.
        try
        {
            await _mojang.DownloadFileAsync(primaryUrl, destPath, sha1, ct: ct).ConfigureAwait(false);
            return;
        }
        catch (HttpRequestException ex)
        {
            FileLogger.Warn($"[Build] Primary download failed ({ex.StatusCode}): {primaryUrl}");
        }

        // Derive the Maven-relative path from the dest path (everything after "libraries/").
        var libMarker = Path.Combine("libraries", "");
        var libIdx = destPath.IndexOf(libMarker, StringComparison.OrdinalIgnoreCase);
        if (libIdx < 0)
        {
            FileLogger.Error($"[Build] No Maven fallback path for {Path.GetFileName(destPath)}");
            throw new HttpRequestException($"Download failed for {Path.GetFileName(destPath)} and no Maven fallback path available.");
        }

        var mavenRelative = destPath[(libIdx + libMarker.Length)..].Replace(Path.DirectorySeparatorChar, '/');

        foreach (var repo in MavenRepos)
        {
            var fallbackUrl = repo + mavenRelative;
            if (string.Equals(fallbackUrl, primaryUrl, StringComparison.OrdinalIgnoreCase))
                continue; // already tried

            try
            {
                await _mojang.DownloadFileAsync(fallbackUrl, destPath, sha1, ct: ct).ConfigureAwait(false);
                FileLogger.Info($"[Build] Fallback succeeded: {fallbackUrl}");
                return;
            }
            catch (HttpRequestException ex)
            {
                FileLogger.Warn($"[Build] Fallback failed ({ex.StatusCode}): {fallbackUrl}");
            }
        }

        var fileName = Path.GetFileName(destPath);
        FileLogger.Error($"[Build] All download URLs failed for {fileName}");
        throw new HttpRequestException($"All download URLs failed for {fileName}");
    }

    /// <summary>
    /// Records the built instance and all its shared-pool files in the RuntimeDatabase.
    /// Call after <see cref="BuildAsync"/> completes successfully.
    /// Does nothing if no database was provided to the constructor.
    /// </summary>
    public async Task RecordInstanceAsync(ReadinessReport report, CancellationToken ct = default)
    {
        if (_db is null) return;

        var instanceId = _db.CreateInstance(report.MinecraftVersion, report.Loader, report.LoaderVersion);
        var files = new List<RuntimeInstanceFile>();

        // ── Track libraries (vanilla + loader) ──────────────────────────
        foreach (var item in report.Items.Where(i => i.Category is "Library" or "Fabric" or "Quilt" or "Forge" or "NeoForge"))
        {
            if (string.IsNullOrWhiteSpace(item.Path)) continue;
            // Only track files inside the shared libraries/ pool
            if (!item.Path.Contains(Path.Combine("libraries", ""), StringComparison.OrdinalIgnoreCase)) continue;

            var size = File.Exists(item.Path) ? new FileInfo(item.Path).Length : item.Size;
            files.Add(new RuntimeInstanceFile
            {
                InstanceId = instanceId,
                FilePath = item.Path,
                SizeBytes = size,
                Sha1 = item.Sha1,
                Category = "library",
            });
        }

        // ── Track asset index ───────────────────────────────────────────
        var assetIndexItem = report.Items.FirstOrDefault(i =>
            i.Category == "Assets" && i.Label.Contains("Asset Index"));
        if (assetIndexItem is not null && File.Exists(assetIndexItem.Path))
        {
            files.Add(new RuntimeInstanceFile
            {
                InstanceId = instanceId,
                FilePath = assetIndexItem.Path,
                SizeBytes = new FileInfo(assetIndexItem.Path).Length,
                Sha1 = assetIndexItem.Sha1,
                Category = "asset_index",
            });

            // ── Track asset objects ─────────────────────────────────────
            var assetIndex = System.Text.Json.JsonSerializer.Deserialize<MojangAssetIndexDetail>(
                await File.ReadAllTextAsync(assetIndexItem.Path, ct).ConfigureAwait(false), SerializerOptions.Default);

            if (assetIndex is not null)
            {
                foreach (var obj in assetIndex.Objects.Values)
                {
                    var objPath = Path.Combine(_runtimeRoot, "assets", "objects", obj.Hash[..2], obj.Hash);
                    files.Add(new RuntimeInstanceFile
                    {
                        InstanceId = instanceId,
                        FilePath = objPath,
                        SizeBytes = obj.Size,
                        Sha1 = obj.Hash,
                        Category = "asset_object",
                    });
                }
            }
        }

        // ── Track Log4j configuration ──────────────────────────────────
        var loggingItem = report.Items.FirstOrDefault(i => i.Category == "Logging");
        if (loggingItem is not null && File.Exists(loggingItem.Path))
        {
            files.Add(new RuntimeInstanceFile
            {
                InstanceId = instanceId,
                FilePath = loggingItem.Path,
                SizeBytes = new FileInfo(loggingItem.Path).Length,
                Sha1 = loggingItem.Sha1,
                Category = "log_config",
            });
        }

        // ── Track Java runtime files ────────────────────────────────────
        var javaItem = report.Items.FirstOrDefault(i => i.Category == "Java");
        if (javaItem is not null)
        {
            // The Java item path points to java.exe; we need to track all files
            // under the java/{component}/ directory
            var javaComponent = report.JavaComponent;
            var javaDir = Path.Combine(_runtimeRoot, "java", javaComponent);
            if (Directory.Exists(javaDir))
            {
                foreach (var file in new DirectoryInfo(javaDir).EnumerateFiles("*", SearchOption.AllDirectories))
                {
                    files.Add(new RuntimeInstanceFile
                    {
                        InstanceId = instanceId,
                        FilePath = file.FullName,
                        SizeBytes = file.Length,
                        Sha1 = "",
                        Category = "java",
                    });
                }
            }
        }

        // ── Track Forge/NeoForge version.json libraries ─────────────────
        if (string.Equals(report.Loader, "forge", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(report.Loader, "neoforge", StringComparison.OrdinalIgnoreCase))
        {
            var versionJson = await _forgeInstaller.LoadVersionJsonAsync(
                report.Loader.ToLowerInvariant(), report.LoaderVersion, ct).ConfigureAwait(false);

            if (versionJson is not null)
            {
                foreach (var lib in versionJson.Libraries)
                {
                    if (!lib.AppliesToCurrentOs()) continue;
                    var libPath = Path.Combine(_runtimeRoot, "libraries", lib.ToRelativePath());
                    if (File.Exists(libPath))
                    {
                        files.Add(new RuntimeInstanceFile
                        {
                            InstanceId = instanceId,
                            FilePath = libPath,
                            SizeBytes = new FileInfo(libPath).Length,
                            Sha1 = lib.Downloads?.Artifact?.Sha1 ?? "",
                            Category = "library",
                        });
                    }
                }
            }
        }

        _db.AddFiles(instanceId, files);
    }

    /// <summary>
    /// Resolves and returns a fully built <see cref="GameEnvironment"/> for launching.
    /// Assumes <see cref="BuildAsync"/> has been called or readiness is confirmed.
    /// </summary>
    public async Task<GameEnvironment> ResolveEnvironmentAsync(string minecraftVersion, string loader, string loaderVersion = "", CancellationToken ct = default)
    {
        var versionJsonPath = Path.Combine(_runtimeRoot, "versions", minecraftVersion, $"{minecraftVersion}.json");
        var versionDetail = JsonSerializer.Deserialize<MojangVersionDetail>(
            await File.ReadAllTextAsync(versionJsonPath, ct).ConfigureAwait(false), SerializerOptions.Default)!;

        var clientJarPath = Path.Combine(_runtimeRoot, "versions", minecraftVersion, $"{minecraftVersion}.jar");
        var assetIndexId = versionDetail.AssetIndex?.Id ?? versionDetail.Assets;
        var assetsDir = Path.Combine(_runtimeRoot, "assets");
        var javaComponent = versionDetail.JavaVersion?.Component ?? "java-runtime-gamma";
        var javaExe = JavaRuntimePaths.FindExecutable(_runtimeRoot, javaComponent)
            ?? JavaRuntimePaths.PreferredExecutablePath(_runtimeRoot, javaComponent);
        var nativesDir = Path.Combine(_runtimeRoot, "versions", minecraftVersion, "natives");
        var log4jConfigPath = GetClientLoggingConfig(versionDetail)?.Path ?? "";
        Directory.CreateDirectory(nativesDir);

        // Build library path list (vanilla) and extra args (for Forge/NeoForge)
        var libraryPaths = new List<string>();
        var seenLibraryPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var extraJvmArgs = new List<string>();
        var extraGameArgs = new List<string>();

        void AddLibraryPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return;

            var fullPath = Path.GetFullPath(path);
            if (File.Exists(fullPath) && seenLibraryPaths.Add(fullPath))
                libraryPaths.Add(fullPath);
        }

        foreach (var lib in versionDetail.Libraries)
        {
            if (!lib.AppliesToCurrentOs()) continue;
            var libPath = Path.Combine(_runtimeRoot, "libraries", lib.ToRelativePath());
            AddLibraryPath(libPath);
        }

        // Determine main class
        var mainClass = versionDetail.MainClass;

        // Add mod loader libraries and override main class.
        // Plugin loaders (spigot, paper, etc.) are server-side only — no extra
        // libraries needed, vanilla main class is used.
        if (LoaderClassification.IsVanillaLike(loader))
        {
            // Nothing extra — vanilla Minecraft or server-side plugin loader.
        }
        else if (string.Equals(loader, "fabric", StringComparison.OrdinalIgnoreCase))
        {
            var fabricLoader = await _fabric.GetLoaderAsync(minecraftVersion, loaderVersion, ct).ConfigureAwait(false);
            if (fabricLoader is not null)
            {
                // Intermediary
                if (fabricLoader.Intermediary is not null)
                {
                    var intPath = Path.Combine(_runtimeRoot, "libraries",
                        ToMavenPath(fabricLoader.Intermediary.Maven));
                    AddLibraryPath(intPath);
                }

                // Loader JAR
                if (fabricLoader.Loader is not null)
                {
                    var loaderPath = Path.Combine(_runtimeRoot, "libraries",
                        ToMavenPath(fabricLoader.Loader.Maven));
                    AddLibraryPath(loaderPath);
                }

                // Fabric libraries (common + client)
                if (fabricLoader.LauncherMeta?.Libraries is not null)
                {
                    foreach (var fLib in fabricLoader.LauncherMeta.Libraries.Common
                        .Concat(fabricLoader.LauncherMeta.Libraries.Client))
                    {
                        var libPath = Path.Combine(_runtimeRoot, "libraries", fLib.ToRelativePath());
                        AddLibraryPath(libPath);
                    }
                }

                // Override main class with Fabric's
                if (fabricLoader.LauncherMeta?.MainClass.ValueKind == System.Text.Json.JsonValueKind.String)
                    mainClass = fabricLoader.LauncherMeta.MainClass.GetString() ?? mainClass;
                else if (fabricLoader.LauncherMeta?.MainClass.ValueKind == System.Text.Json.JsonValueKind.Object &&
                         fabricLoader.LauncherMeta.MainClass.TryGetProperty("client", out var clientMain))
                    mainClass = clientMain.GetString() ?? mainClass;
            }
        }
        else if (string.Equals(loader, "quilt", StringComparison.OrdinalIgnoreCase))
        {
            var quiltLoader = await _quilt.GetLoaderAsync(minecraftVersion, loaderVersion, ct).ConfigureAwait(false);
            if (quiltLoader is not null)
            {
                // Hashed mappings (preferred) or intermediary fallback
                var mappingsMaven = quiltLoader.Hashed?.Maven ?? quiltLoader.Intermediary?.Maven;
                if (!string.IsNullOrWhiteSpace(mappingsMaven))
                {
                    var intPath = Path.Combine(_runtimeRoot, "libraries", ToMavenPath(mappingsMaven));
                    AddLibraryPath(intPath);
                }

                // Loader JAR
                if (quiltLoader.Loader is not null)
                {
                    var loaderPath = Path.Combine(_runtimeRoot, "libraries",
                        ToMavenPath(quiltLoader.Loader.Maven));
                    AddLibraryPath(loaderPath);
                }

                // Quilt libraries (common + client)
                if (quiltLoader.LauncherMeta?.Libraries is not null)
                {
                    foreach (var qLib in quiltLoader.LauncherMeta.Libraries.Common
                        .Concat(quiltLoader.LauncherMeta.Libraries.Client))
                    {
                        var libPath = Path.Combine(_runtimeRoot, "libraries", qLib.ToRelativePath());
                        AddLibraryPath(libPath);
                    }
                }

                // Override main class with Quilt's
                if (quiltLoader.LauncherMeta?.MainClass.ValueKind == System.Text.Json.JsonValueKind.String)
                    mainClass = quiltLoader.LauncherMeta.MainClass.GetString() ?? mainClass;
                else if (quiltLoader.LauncherMeta?.MainClass.ValueKind == System.Text.Json.JsonValueKind.Object &&
                         quiltLoader.LauncherMeta.MainClass.TryGetProperty("client", out var clientMain))
                    mainClass = clientMain.GetString() ?? mainClass;
            }
        }
        else if (string.Equals(loader, "forge", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(loader, "neoforge", StringComparison.OrdinalIgnoreCase))
        {
            var isForge = string.Equals(loader, "forge", StringComparison.OrdinalIgnoreCase);

            // Resolve loader version
            string resolvedForgeVersion;
            if (!string.IsNullOrWhiteSpace(loaderVersion))
            {
                resolvedForgeVersion = loaderVersion;
            }
            else
            {
                string? latest;
                if (isForge)
                    latest = await _forgeMeta.GetLatestVersionAsync(minecraftVersion, ct).ConfigureAwait(false);
                else
                    latest = await _neoForgeMeta.GetLatestVersionAsync(minecraftVersion, ct).ConfigureAwait(false);
                resolvedForgeVersion = latest
                    ?? throw new InvalidOperationException($"No {loader} version found for Minecraft {minecraftVersion}.");
            }

            // Run installer if not already done
            if (!_forgeInstaller.IsInstalled(loader.ToLowerInvariant(), resolvedForgeVersion))
            {
                var installerUrl = isForge
                    ? _forgeMeta.GetInstallerUrl(resolvedForgeVersion)
                    : _neoForgeMeta.GetInstallerUrl(resolvedForgeVersion);

                await _forgeInstaller.InstallAsync(
                    loader.ToLowerInvariant(), resolvedForgeVersion, minecraftVersion, installerUrl, ct: ct).ConfigureAwait(false);
            }

            // Load the version.json from the completed installation
            var forgeVersionJson = await _forgeInstaller.LoadVersionJsonAsync(loader.ToLowerInvariant(), resolvedForgeVersion, ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"Failed to load {loader} version.json after installation.");

            // Add Forge/NeoForge libraries to classpath
            foreach (var lib in forgeVersionJson.Libraries)
            {
                if (!lib.AppliesToCurrentOs()) continue;
                var libPath = Path.Combine(_runtimeRoot, "libraries", lib.ToRelativePath());
                AddLibraryPath(libPath);
            }

            // Override main class
            mainClass = forgeVersionJson.MainClass;

            // Extract extra JVM and game arguments from the Forge/NeoForge version.json.
            // These include module path settings, --add-opens, --add-exports, etc.
            if (forgeVersionJson.Arguments is not null)
            {
                foreach (var el in forgeVersionJson.Arguments.Jvm)
                {
                    if (el.ValueKind == JsonValueKind.String)
                        extraJvmArgs.Add(el.GetString() ?? "");
                }
                foreach (var el in forgeVersionJson.Arguments.Game)
                {
                    if (el.ValueKind == JsonValueKind.String)
                        extraGameArgs.Add(el.GetString() ?? "");
                }
            }
            else if (!string.IsNullOrWhiteSpace(forgeVersionJson.MinecraftArguments))
            {
                // Legacy Forge (pre-1.13) uses a single argument string
                extraGameArgs.AddRange(forgeVersionJson.MinecraftArguments
                    .Split(' ', StringSplitOptions.RemoveEmptyEntries));
            }
        }

        return new GameEnvironment
        {
            RuntimeRoot = _runtimeRoot,
            MinecraftVersion = minecraftVersion,
            Loader = loader,
            MainClass = mainClass,
            AssetIndexId = assetIndexId,
            Log4jConfigurationPath = log4jConfigPath,
            ClientJarPath = clientJarPath,
            LibraryPaths = libraryPaths,
            JavaExecutable = javaExe,
            AssetsDirectory = assetsDir,
            NativesDirectory = nativesDir,
            VersionDetail = versionDetail,
            ExtraJvmArgs = extraJvmArgs,
            ExtraGameArgs = extraGameArgs,
        };
    }

    // ══════════════════════════════════════════════════════════════════════
    //  Private helpers
    // ══════════════════════════════════════════════════════════════════════

    private async Task DownloadAssetObjectsAsync(string? assetIndexPath,
        IProgress<(int completed, int total, string label)>? progress,
        CancellationToken ct)
    {
        // If no specific index path was provided, fall back to scanning the indexes directory.
        if (string.IsNullOrWhiteSpace(assetIndexPath) || !File.Exists(assetIndexPath))
        {
            var indexesDir = Path.Combine(_runtimeRoot, "assets", "indexes");
            if (Directory.Exists(indexesDir))
                assetIndexPath = Directory.GetFiles(indexesDir, "*.json").FirstOrDefault();
        }

        if (assetIndexPath is null || !File.Exists(assetIndexPath))
            return;

        var assetIndex = JsonSerializer.Deserialize<MojangAssetIndexDetail>(
            await File.ReadAllTextAsync(assetIndexPath, ct).ConfigureAwait(false), SerializerOptions.Default);
        if (assetIndex is null) return;

        var missing = new List<(string hash, long size)>();
        foreach (var obj in assetIndex.Objects.Values)
        {
            var objPath = Path.Combine(_runtimeRoot, "assets", "objects", obj.Hash[..2], obj.Hash);
            if (!File.Exists(objPath))
                missing.Add((obj.Hash, obj.Size));
        }

        for (int i = 0; i < missing.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var (hash, _) = missing[i];
            var url = $"https://resources.download.minecraft.net/{hash[..2]}/{hash}";
            var destPath = Path.Combine(_runtimeRoot, "assets", "objects", hash[..2], hash);
            progress?.Report((i, missing.Count, $"Asset {i + 1}/{missing.Count}"));
            await _mojang.DownloadFileAsync(url, destPath, hash, ct: ct).ConfigureAwait(false);
        }
    }

    private (string Id, string Path, string Url, string Sha1, long Size)? GetClientLoggingConfig(MojangVersionDetail versionDetail)
    {
        if (versionDetail.Logging is not { ValueKind: JsonValueKind.Object } logging ||
            !logging.TryGetProperty("client", out var client) ||
            client.ValueKind != JsonValueKind.Object ||
            !client.TryGetProperty("file", out var file) ||
            file.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var id = file.TryGetProperty("id", out var idProp) ? idProp.GetString() ?? "" : "";
        var url = file.TryGetProperty("url", out var urlProp) ? urlProp.GetString() ?? "" : "";
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(url))
            return null;

        var sha1 = file.TryGetProperty("sha1", out var sha1Prop) ? sha1Prop.GetString() ?? "" : "";
        var size = file.TryGetProperty("size", out var sizeProp) && sizeProp.TryGetInt64(out var parsedSize)
            ? parsedSize
            : 0L;
        var path = Path.Combine(_runtimeRoot, "assets", "log_configs", id);

        return (id, path, url, sha1, size);
    }

    private async Task DownloadJavaRuntimeAsync(string component,
        IProgress<(int completed, int total, string label)>? progress,
        CancellationToken ct)
    {
        progress?.Report((0, 1, $"Downloading Java ({component})…"));

        var runtimes = await _mojang.GetJavaRuntimesAsync(component, ct).ConfigureAwait(false);
        var runtime = runtimes.FirstOrDefault();
        if (runtime?.Manifest?.Url is null)
            throw new InvalidOperationException($"No Java runtime found for component '{component}'.");

        // Fetch the runtime manifest — this lists all files in the JRE.
        var manifestJson = await _mojang.GetJsonAsync(runtime.Manifest.Url, ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(manifestJson);

        if (!doc.RootElement.TryGetProperty("files", out var filesEl))
            throw new InvalidOperationException("Java runtime manifest missing 'files' property.");

        var javaDir = Path.Combine(_runtimeRoot, "java", component);
        int fileCount = 0;
        var filesToDownload = new List<(string path, string url, string sha1, bool executable)>();

        foreach (var fileProp in filesEl.EnumerateObject())
        {
            var relPath = fileProp.Name;
            var fileInfo = fileProp.Value;

            if (!fileInfo.TryGetProperty("type", out var typeProp)) continue;
            var type = typeProp.GetString();

            var destPath = CombineUnderDirectory(javaDir, relPath.Replace('/', Path.DirectorySeparatorChar));

            if (type == "directory")
            {
                Directory.CreateDirectory(destPath);
            }
            else if (type == "file")
            {
                var executable = fileInfo.TryGetProperty("executable", out var executableProp)
                    && executableProp.ValueKind == JsonValueKind.True;

                if (File.Exists(destPath))
                {
                    if (executable)
                        EnsureExecutablePermission(destPath);
                    continue;
                }

                if (fileInfo.TryGetProperty("downloads", out var downloads) &&
                    downloads.TryGetProperty("raw", out var raw))
                {
                    var url = raw.GetProperty("url").GetString() ?? "";
                    var sha1 = raw.TryGetProperty("sha1", out var sha1Prop) ? sha1Prop.GetString() ?? "" : "";
                    filesToDownload.Add((destPath, url, sha1, executable));
                }
            }
        }

        for (int i = 0; i < filesToDownload.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var (path, url, sha1, executable) = filesToDownload[i];
            progress?.Report((i, filesToDownload.Count, $"Java file {i + 1}/{filesToDownload.Count}"));
            var expectedSha1 = !string.IsNullOrWhiteSpace(sha1) ? sha1 : null;
            await _mojang.DownloadFileAsync(url, path, expectedSha1, ct: ct).ConfigureAwait(false);
            if (executable)
                EnsureExecutablePermission(path);
            fileCount++;
        }
    }

    private static void EnsureExecutablePermission(string path)
    {
        if (OperatingSystem.IsWindows() || !File.Exists(path))
            return;

        try
        {
            var mode = File.GetUnixFileMode(path);
            mode |= UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;
            mode |= UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead;
            File.SetUnixFileMode(path, mode);
        }
        catch
        {
            // Best effort: a missing execute bit will be surfaced by readiness/launch checks.
        }
    }

    private static string CombineUnderDirectory(string rootDirectory, string relativePath)
    {
        ValidateRelativePath(relativePath, "Java runtime manifest path");

        var root = Path.GetFullPath(rootDirectory);
        var fullPath = Path.GetFullPath(Path.Combine(root, relativePath));
        var rootWithSeparator = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;

        if (!fullPath.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Resolved Java runtime path escapes target folder: {relativePath}");

        return fullPath;
    }

    private static void ValidateRelativePath(string relativePath, string label)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
            throw new InvalidOperationException($"{label} is empty.");

        var separators = new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar };
        if (Path.IsPathRooted(relativePath) ||
            relativePath.Split(separators, StringSplitOptions.None).Any(part => part is "" or "." or ".."))
        {
            throw new InvalidOperationException($"{label} is not safe: {relativePath}");
        }
    }

    /// <summary>Converts a Maven coordinate (group:artifact:version) to a relative path.</summary>
    private static string ToMavenPath(string mavenCoord)
    {
        var parts = mavenCoord.Split(':');
        if (parts.Length < 3) return mavenCoord.Replace(':', '/') + ".jar";
        var group = parts[0].Replace('.', '/');
        var artifact = parts[1];
        var version = parts[2];
        var classifier = parts.Length > 3 ? "-" + parts[3] : "";
        return $"{group}/{artifact}/{version}/{artifact}-{version}{classifier}.jar";
    }

    public void Dispose()
    {
        _mojang.Dispose();
        _fabric.Dispose();
        _quilt.Dispose();
        _forgeMeta.Dispose();
        _neoForgeMeta.Dispose();
        _forgeInstaller.Dispose();
    }
}
