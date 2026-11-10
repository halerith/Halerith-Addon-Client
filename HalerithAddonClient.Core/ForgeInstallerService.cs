using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace HalerithAddonClient.Core;

// ══════════════════════════════════════════════════════════════════════════════
//  Forge / NeoForge Installer Service
// ══════════════════════════════════════════════════════════════════════════════
//
//  Unlike Fabric/Quilt (download JARs → build classpath), Forge and NeoForge
//  use an installer-based model:
//
//    1. Download the installer JAR from Maven.
//    2. Extract install_profile.json (processor pipeline) and version.json
//       (launch profile with libraries, mainClass, arguments).
//    3. Download all libraries referenced by install_profile and version.json.
//    4. Run each processor step by shelling out to the local Java runtime.
//       Processors remap and patch the vanilla JAR to produce the final
//       client artifact.
//    5. Cache results so subsequent launches skip the install step.
//
//  Directory layout under Runtime/:
//    forge/{mcVersion}-{loaderVersion}/
//      installer.jar                  — downloaded installer
//      install_profile.json           — extracted from installer
//      version.json                   — extracted from installer
//      .installed                     — marker file (processors completed)
//    libraries/{maven-path}/…         — shared library pool
// ══════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Handles the full Forge/NeoForge installation pipeline: download installer,
/// extract metadata, download libraries, run processors, and cache the result.
/// </summary>
public sealed class ForgeInstallerService : IDisposable
{
    private readonly HttpClient _http;
    private readonly bool _ownsHttpClient;
    private readonly string _runtimeRoot;

    public ForgeInstallerService(string? runtimeRoot = null, HttpClient? httpClient = null)
    {
        _ownsHttpClient = httpClient is null;
        _http = MinecraftMetaHttp.Configure(httpClient);
        _runtimeRoot = runtimeRoot ?? Path.Combine(LauncherPaths.GetAppDataDirectory(), "Runtime");
    }

    // ──────────────────────────────────────────────────────────────────────
    //  Public API
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the directory where installer artifacts are cached for a given
    /// loader version. E.g. Runtime/forge/1.21.1-52.0.16/ or
    /// Runtime/neoforge/21.1.72/.
    /// </summary>
    public string GetInstallerCacheDir(string loader, string loaderVersion)
        => Path.Combine(_runtimeRoot, loader.ToLowerInvariant(), loaderVersion);

    /// <summary>
    /// Returns true if the installer pipeline has already completed for this
    /// loader version (the .installed marker exists).
    /// </summary>
    public bool IsInstalled(string loader, string loaderVersion)
        => File.Exists(Path.Combine(GetInstallerCacheDir(loader, loaderVersion), ".installed"));

    /// <summary>
    /// Returns true if the cache directory exists but the installer hasn't
    /// completed (no .installed marker) — i.e. a previous build failed partway.
    /// </summary>
    public bool IsPartialInstall(string loader, string loaderVersion)
    {
        var dir = GetInstallerCacheDir(loader, loaderVersion);
        return Directory.Exists(dir) && !File.Exists(Path.Combine(dir, ".installed"));
    }

    /// <summary>
    /// Deletes the entire cache directory for a loader version so the next
    /// <see cref="InstallAsync"/> starts from scratch.
    /// </summary>
    public void CleanInstall(string loader, string loaderVersion)
    {
        var dir = GetInstallerCacheDir(loader, loaderVersion);
        if (Directory.Exists(dir))
            Directory.Delete(dir, recursive: true);
    }

    /// <summary>
    /// Runs the full Forge/NeoForge installation pipeline:
    /// download installer → extract → download libraries → run processors.
    /// Skips if already installed (marker file present).
    /// </summary>
    public async Task InstallAsync(
        string loader,
        string loaderVersion,
        string minecraftVersion,
        string installerUrl,
        IProgress<(int completed, int total, string label)>? progress = null,
        CancellationToken ct = default)
    {
        var cacheDir = GetInstallerCacheDir(loader, loaderVersion);
        Directory.CreateDirectory(cacheDir);

        // Skip if already installed
        if (File.Exists(Path.Combine(cacheDir, ".installed")))
            return;

        var installerPath = Path.Combine(cacheDir, "installer.jar");

        // ── Step 1: Download installer JAR ──────────────────────────────
        progress?.Report((0, 5, $"Downloading {loader} installer…"));
        if (!File.Exists(installerPath))
        {
            await DownloadFileAsync(installerUrl, installerPath, ct: ct).ConfigureAwait(false);
        }

        // ── Step 2: Extract install_profile.json and version.json ───────
        progress?.Report((1, 5, "Extracting installer metadata…"));
        var (installProfile, versionJson) = await ExtractInstallerMetadataAsync(installerPath, cacheDir, ct).ConfigureAwait(false);

        // ── Step 3: Extract data files from installer JAR ───────────────
        progress?.Report((2, 5, "Extracting installer data files…"));
        await ExtractInstallerDataFilesAsync(installerPath, installProfile, cacheDir, ct).ConfigureAwait(false);

        // ── Step 4: Download libraries ──────────────────────────────────
        progress?.Report((3, 5, "Downloading libraries…"));
        await DownloadInstallerLibrariesAsync(installProfile, versionJson, progress, ct).ConfigureAwait(false);

        // ── Step 5: Run processors ──────────────────────────────────────
        progress?.Report((4, 5, "Running installer processors…"));
        await RunProcessorsAsync(loader, loaderVersion, minecraftVersion, installProfile, cacheDir, ct).ConfigureAwait(false);

        // ── Mark as installed ───────────────────────────────────────────
        await File.WriteAllTextAsync(Path.Combine(cacheDir, ".installed"),
            $"Installed {loader} {loaderVersion} at {DateTime.UtcNow:O}", ct).ConfigureAwait(false);
        progress?.Report((5, 5, $"{loader} installation complete"));
    }

    /// <summary>
    /// Loads the cached ForgeVersionJson for a completed installation.
    /// Returns null if the installation hasn't been run yet.
    /// </summary>
    public async Task<ForgeVersionJson?> LoadVersionJsonAsync(string loader, string loaderVersion, CancellationToken ct = default)
    {
        var path = Path.Combine(GetInstallerCacheDir(loader, loaderVersion), "version.json");
        if (!File.Exists(path)) return null;
        var json = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
        return JsonSerializer.Deserialize<ForgeVersionJson>(json, SerializerOptions.Default);
    }

    /// <summary>
    /// Loads the cached ForgeInstallProfile for a completed installation.
    /// Returns null if the installation hasn't been run yet.
    /// </summary>
    public async Task<ForgeInstallProfile?> LoadInstallProfileAsync(string loader, string loaderVersion, CancellationToken ct = default)
    {
        var path = Path.Combine(GetInstallerCacheDir(loader, loaderVersion), "install_profile.json");
        if (!File.Exists(path)) return null;
        var json = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
        return JsonSerializer.Deserialize<ForgeInstallProfile>(json, SerializerOptions.Default);
    }

    // ──────────────────────────────────────────────────────────────────────
    //  Step 2: Extract metadata from installer JAR
    // ──────────────────────────────────────────────────────────────────────

    private async Task<(ForgeInstallProfile profile, ForgeVersionJson versionJson)> ExtractInstallerMetadataAsync(
        string installerJarPath, string cacheDir, CancellationToken ct)
    {
        using var zip = ZipFile.OpenRead(installerJarPath);

        // Extract install_profile.json
        var profileEntry = zip.GetEntry("install_profile.json")
            ?? throw new InvalidOperationException("Installer JAR missing install_profile.json");
        var profilePath = Path.Combine(cacheDir, "install_profile.json");
        await ExtractEntryAsync(profileEntry, profilePath, ct).ConfigureAwait(false);

        var profileJson = await File.ReadAllTextAsync(profilePath, ct).ConfigureAwait(false);
        var profile = JsonSerializer.Deserialize<ForgeInstallProfile>(profileJson, SerializerOptions.Default)
            ?? throw new InvalidOperationException("Failed to parse install_profile.json");

        // Extract version.json — path is specified in install_profile.json → json field
        // Typically "/version.json" (with leading slash)
        var versionEntryName = profile.Json.TrimStart('/');
        var versionEntry = zip.GetEntry(versionEntryName)
            ?? zip.GetEntry("version.json")
            ?? throw new InvalidOperationException($"Installer JAR missing version JSON at '{versionEntryName}'");
        var versionPath = Path.Combine(cacheDir, "version.json");
        await ExtractEntryAsync(versionEntry, versionPath, ct).ConfigureAwait(false);

        var versionJsonStr = await File.ReadAllTextAsync(versionPath, ct).ConfigureAwait(false);
        var versionJson = JsonSerializer.Deserialize<ForgeVersionJson>(versionJsonStr, SerializerOptions.Default)
            ?? throw new InvalidOperationException("Failed to parse version.json");

        return (profile, versionJson);
    }

    // ──────────────────────────────────────────────────────────────────────
    //  Step 3: Extract data files referenced by install_profile
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Extracts data files from the installer JAR that are referenced in the
    /// install_profile.json data section. Values starting with '/' are paths
    /// inside the JAR; values starting with '[' are Maven coordinates pointing
    /// to JARs already inside the installer's maven/ directory.
    /// </summary>
    private async Task ExtractInstallerDataFilesAsync(
        string installerJarPath, ForgeInstallProfile profile, string cacheDir, CancellationToken ct)
    {
        using var zip = ZipFile.OpenRead(installerJarPath);

        foreach (var (key, dataEntry) in profile.Data)
        {
            var clientValue = dataEntry.Client;

            // Values starting with '/' are paths inside the installer JAR
            if (clientValue.StartsWith('/'))
            {
                var entryName = clientValue.TrimStart('/');
                ValidateZipEntryName(entryName, "installer data entry");
                var entry = zip.GetEntry(entryName);
                if (entry is not null)
                {
                    var dataRoot = Path.Combine(cacheDir, "data");
                    var destPath = CombineUnderDirectory(dataRoot, entryName.Replace('/', Path.DirectorySeparatorChar));
                    await ExtractEntryAsync(entry, destPath, ct).ConfigureAwait(false);
                }
            }
            // Values like '[group:artifact:version]' → extract from maven/ inside the JAR
            else if (clientValue.StartsWith('[') && clientValue.EndsWith(']'))
            {
                var mavenCoord = clientValue[1..^1];
                var mavenPath = MavenCoordToPath(mavenCoord);
                ValidateRelativePath(mavenPath, "installer Maven path");
                var jarEntry = zip.GetEntry($"maven/{mavenPath}");
                if (jarEntry is not null)
                {
                    var destPath = CombineUnderDirectory(Path.Combine(_runtimeRoot, "libraries"), mavenPath);
                    if (!File.Exists(destPath))
                        await ExtractEntryAsync(jarEntry, destPath, ct).ConfigureAwait(false);
                }
            }
        }

        // Also extract the universal/patched JAR from maven/ if install_profile.path is set
        if (!string.IsNullOrWhiteSpace(profile.Path))
        {
            var universalPath = MavenCoordToPath(profile.Path);
            ValidateRelativePath(universalPath, "installer universal path");
            var universalEntry = zip.GetEntry($"maven/{universalPath}");
            if (universalEntry is not null)
            {
                var destPath = CombineUnderDirectory(Path.Combine(_runtimeRoot, "libraries"), universalPath);
                if (!File.Exists(destPath))
                    await ExtractEntryAsync(universalEntry, destPath, ct).ConfigureAwait(false);
            }
        }
    }

    // ──────────────────────────────────────────────────────────────────────
    //  Step 4: Download libraries
    // ──────────────────────────────────────────────────────────────────────

    private async Task DownloadInstallerLibrariesAsync(
        ForgeInstallProfile profile, ForgeVersionJson versionJson,
        IProgress<(int completed, int total, string label)>? progress,
        CancellationToken ct)
    {
        // Collect all unique libraries from both install_profile and version.json
        var allLibraries = new List<MojangLibrary>();
        allLibraries.AddRange(profile.Libraries);
        allLibraries.AddRange(versionJson.Libraries);

        var toDownload = new List<(string path, List<string> urls, string? sha1)>();

        foreach (var lib in allLibraries)
        {
            if (!lib.AppliesToCurrentOs()) continue;

            var libPath = Path.Combine(_runtimeRoot, "libraries", lib.ToRelativePath());
            if (File.Exists(libPath)) continue;

            // Determine if this library has a genuine download URL.
            // Libraries with Mojang-style Downloads.Artifact.Url are always downloadable.
            // Libraries with an explicit Url property (Fabric/Quilt style) are downloadable.
            // Libraries with NEITHER are processor outputs or were extracted from the
            // installer JAR — they'll be created by Step 5 (RunProcessors) and should
            // NOT be downloaded.
            var hasMojangUrl = !string.IsNullOrWhiteSpace(lib.Downloads?.Artifact?.Url);
            var hasExplicitUrl = !string.IsNullOrWhiteSpace(lib.Url);

            if (!hasMojangUrl && !hasExplicitUrl)
            {
                FileLogger.Info($"[ForgeInstaller] Skipping processor output: {lib.Name}");
                continue;
            }

            // Build a list of candidate URLs: primary download URL first, then Maven repo fallbacks.
            var urls = new List<string>();
            var primaryUrl = lib.GetDownloadUrl();
            if (!string.IsNullOrWhiteSpace(primaryUrl))
                urls.Add(primaryUrl);

            // Always add Maven repo fallbacks for resilience.
            var mavenPath = lib.ToRelativePath();
            foreach (var candidate in GetMavenCandidateUrls(mavenPath))
            {
                if (!urls.Contains(candidate, StringComparer.OrdinalIgnoreCase))
                    urls.Add(candidate);
            }

            if (urls.Count > 0)
            {
                var sha1 = lib.Downloads?.Artifact?.Sha1;
                toDownload.Add((libPath, urls, sha1));
            }
        }

        for (int i = 0; i < toDownload.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var (path, urls, sha1) = toDownload[i];
            progress?.Report((i, toDownload.Count, $"Library {i + 1}/{toDownload.Count}"));
            await DownloadFileWithFallbackAsync(urls, path, sha1, ct).ConfigureAwait(false);
        }
    }

    // ──────────────────────────────────────────────────────────────────────
    //  Step 5: Run processors
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Runs the Forge/NeoForge processor pipeline. Each processor is a Java
    /// program that transforms/patches files (e.g. remap vanilla JAR, apply
    /// binary patches). Processors are run sequentially.
    /// </summary>
    private async Task RunProcessorsAsync(
        string loader, string loaderVersion, string minecraftVersion,
        ForgeInstallProfile profile, string cacheDir, CancellationToken ct)
    {
        var javaExe = FindJavaExecutable(minecraftVersion);
        if (javaExe is null)
            throw new InvalidOperationException(
                "Java runtime not found. The game environment must be built (Java downloaded) before running the Forge installer.");

        var clientJarPath = Path.Combine(_runtimeRoot, "versions", minecraftVersion, $"{minecraftVersion}.jar");

        // Build the data substitution map
        var dataMap = BuildDataMap(profile, cacheDir, minecraftVersion, clientJarPath);

        foreach (var processor in profile.Processors)
        {
            ct.ThrowIfCancellationRequested();

            // Skip server-only processors
            if (processor.Sides is not null && !processor.Sides.Contains("client", StringComparer.OrdinalIgnoreCase))
                continue;

            // Check if outputs already exist and match expected hashes
            if (processor.Outputs is not null && AreOutputsValid(processor.Outputs, dataMap))
                continue;

            await RunSingleProcessorAsync(javaExe, processor, dataMap, ct).ConfigureAwait(false);
        }
    }

    private async Task RunSingleProcessorAsync(
        string javaExe, ForgeProcessor processor,
        Dictionary<string, string> dataMap, CancellationToken ct)
    {
        // Resolve the processor JAR's main class from its manifest
        var processorJarPath = Path.Combine(_runtimeRoot, "libraries", MavenCoordToPath(processor.Jar));
        if (!File.Exists(processorJarPath))
            throw new FileNotFoundException($"Processor JAR not found: {processorJarPath}");

        // Build classpath: processor JAR + all classpath entries
        var classpathParts = new List<string> { processorJarPath };
        foreach (var cp in processor.Classpath)
        {
            var cpPath = Path.Combine(_runtimeRoot, "libraries", MavenCoordToPath(cp));
            if (File.Exists(cpPath))
                classpathParts.Add(cpPath);
        }
        var classpath = string.Join(Path.PathSeparator.ToString(), classpathParts);

        // Read the processor JAR's main class from META-INF/MANIFEST.MF
        var mainClass = ReadJarMainClass(processorJarPath)
            ?? throw new InvalidOperationException($"Cannot determine main class for processor JAR: {processor.Jar}");

        // Resolve processor arguments using the data map
        var args = new List<string>();
        foreach (var arg in processor.Args)
        {
            args.Add(SubstituteDataTokens(arg, dataMap));
        }

        // Build the Java command
        var psi = new ProcessStartInfo
        {
            FileName = javaExe,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        psi.ArgumentList.Add("-cp");
        psi.ArgumentList.Add(classpath);
        psi.ArgumentList.Add(mainClass);
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start processor: {processor.Jar}");

        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);

        await process.WaitForExitAsync(ct).ConfigureAwait(false);
        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Forge processor '{processor.Jar}' failed with exit code {process.ExitCode}.\n" +
                $"Args: {string.Join(" ", args)}\n" +
                $"Stdout: {stdout}\n" +
                $"Stderr: {stderr}");
        }
    }

    // ──────────────────────────────────────────────────────────────────────
    //  Data map & token substitution
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds the substitution map used to resolve {TOKENS} in processor args.
    /// Keys are token names (e.g. "MINECRAFT_JAR"), values are file paths or literal values.
    /// </summary>
    private Dictionary<string, string> BuildDataMap(
        ForgeInstallProfile profile, string cacheDir, string minecraftVersion, string clientJarPath)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["SIDE"] = "client",
            ["MINECRAFT_JAR"] = clientJarPath,
            ["MINECRAFT_VERSION"] = minecraftVersion,
            ["ROOT"] = _runtimeRoot,
            ["INSTALLER"] = Path.Combine(cacheDir, "installer.jar"),
            ["LIBRARY_DIR"] = Path.Combine(_runtimeRoot, "libraries"),
        };

        // Add entries from install_profile.json → data
        foreach (var (key, entry) in profile.Data)
        {
            var value = entry.Client;

            if (value.StartsWith('[') && value.EndsWith(']'))
            {
                // Maven coordinate → resolve to library path
                var mavenCoord = value[1..^1];
                map[key] = Path.Combine(_runtimeRoot, "libraries", MavenCoordToPath(mavenCoord));
            }
            else if (value.StartsWith('/'))
            {
                // Path inside installer → resolve to extracted data file
                map[key] = Path.Combine(cacheDir, "data", value.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));
            }
            else
            {
                // Literal value
                map[key] = value;
            }
        }

        return map;
    }

    /// <summary>
    /// Replaces {TOKEN} placeholders and [maven:coord] references in a processor argument.
    /// </summary>
    private string SubstituteDataTokens(string arg, Dictionary<string, string> dataMap)
    {
        // [maven:coord] → library path
        if (arg.StartsWith('[') && arg.EndsWith(']'))
        {
            var mavenCoord = arg[1..^1];
            return Path.Combine(_runtimeRoot, "libraries", MavenCoordToPath(mavenCoord));
        }

        // {TOKEN} → data map value
        if (arg.StartsWith('{') && arg.EndsWith('}'))
        {
            var token = arg[1..^1];
            return dataMap.TryGetValue(token, out var value) ? value : arg;
        }

        return arg;
    }

    /// <summary>
    /// Checks whether all processor output files exist and match their expected SHA1 hashes.
    /// </summary>
    private bool AreOutputsValid(Dictionary<string, string> outputs, Dictionary<string, string> dataMap)
    {
        foreach (var (keyToken, expectedHashToken) in outputs)
        {
            var filePath = SubstituteDataTokens(keyToken, dataMap);
            var expectedHash = SubstituteDataTokens(expectedHashToken, dataMap);

            if (!File.Exists(filePath)) return false;

            // If expected hash looks like a SHA1 (40 hex chars), verify it
            if (expectedHash.Length == 40 && expectedHash.All(c => "0123456789abcdef".Contains(char.ToLower(c))))
            {
                var actualHash = ComputeSha1(filePath);
                if (!string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase))
                    return false;
            }
        }
        return true;
    }

    // ──────────────────────────────────────────────────────────────────────
    //  Utility helpers
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Finds the Java executable in the Runtime directory. Tries the standard
    /// game environment Java path based on the Mojang version manifest.
    /// </summary>
    private string? FindJavaExecutable(string minecraftVersion)
    {
        // Try to read the version JSON to find the Java component
        var versionJsonPath = Path.Combine(_runtimeRoot, "versions", minecraftVersion, $"{minecraftVersion}.json");
        if (File.Exists(versionJsonPath))
        {
            try
            {
                var json = File.ReadAllText(versionJsonPath);
                var detail = JsonSerializer.Deserialize<MojangVersionDetail>(json, SerializerOptions.Default);
                var component = detail?.JavaVersion?.Component ?? "java-runtime-gamma";
                var javaExe = JavaRuntimePaths.FindExecutable(_runtimeRoot, component);
                if (javaExe is not null) return javaExe;
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Failed to resolve Java from version JSON: {ex.Message}"); }
        }

        return JavaRuntimePaths.FindAnyExecutable(_runtimeRoot);
    }

    /// <summary>
    /// Reads the Main-Class from a JAR's META-INF/MANIFEST.MF.
    /// </summary>
    private static string? ReadJarMainClass(string jarPath)
    {
        try
        {
            using var zip = ZipFile.OpenRead(jarPath);
            var manifestEntry = zip.GetEntry("META-INF/MANIFEST.MF");
            if (manifestEntry is null) return null;

            using var reader = new StreamReader(manifestEntry.Open());
            var manifest = reader.ReadToEnd();
            foreach (var line in manifest.Split('\n'))
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("Main-Class:", StringComparison.OrdinalIgnoreCase))
                {
                    return trimmed["Main-Class:".Length..].Trim();
                }
            }
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Failed to read JAR manifest: {ex.Message}"); }
        return null;
    }

    /// <summary>
    /// Converts a Maven coordinate to a relative filesystem path.
    /// Supports format: group:artifact:version[:classifier][@extension]
    /// </summary>
    internal static string MavenCoordToPath(string mavenCoord)
    {
        // Split off @extension if present
        var extension = "jar";
        var coordPart = mavenCoord;
        var atIdx = mavenCoord.IndexOf('@');
        if (atIdx >= 0)
        {
            extension = mavenCoord[(atIdx + 1)..];
            coordPart = mavenCoord[..atIdx];
        }

        var parts = coordPart.Split(':');
        if (parts.Length < 3) return coordPart.Replace(':', '/') + "." + extension;

        var group = parts[0].Replace('.', '/');
        var artifact = parts[1];
        var version = parts[2];
        var classifier = parts.Length > 3 ? "-" + parts[3] : "";

        return $"{group}/{artifact}/{version}/{artifact}-{version}{classifier}.{extension}";
    }

    /// <summary>
    /// Known Maven repositories for Forge/NeoForge dependencies.
    /// </summary>
    private static readonly string[] MavenRepos =
    [
        "https://maven.minecraftforge.net/",
        "https://maven.neoforged.net/releases/",
        "https://libraries.minecraft.net/",
        "https://repo.maven.apache.org/maven2/",
    ];

    /// <summary>
    /// Returns candidate download URLs for a Maven path across all known repos.
    /// </summary>
    private static List<string> GetMavenCandidateUrls(string mavenPath)
        => MavenRepos.Select(repo => repo + mavenPath).ToList();

    /// <summary>
    /// Tries each URL in order until one succeeds. Throws only if all URLs fail.
    /// </summary>
    private async Task DownloadFileWithFallbackAsync(List<string> urls, string destPath, string? expectedSha1 = null, CancellationToken ct = default)
    {
        Exception? lastException = null;
        foreach (var url in urls)
        {
            try
            {
                await DownloadFileAsync(url, destPath, expectedSha1, ct).ConfigureAwait(false);
                return; // success
            }
            catch (HttpRequestException ex)
            {
                FileLogger.Warn($"[ForgeInstaller] Download failed ({ex.StatusCode}): {url}");
                lastException = ex;
                // Try next URL
            }
        }

        var fileName = Path.GetFileName(destPath);
        FileLogger.Error($"[ForgeInstaller] All download URLs failed for {fileName}: {string.Join(", ", urls)}");
        throw lastException ?? new InvalidOperationException($"No download URLs available for {fileName}");
    }

    private async Task DownloadFileAsync(string url, string destPath, string? expectedSha1 = null, CancellationToken ct = default)
    {
        var uri = HttpsUrlPolicy.RequireHttpsAbsoluteUri(url, "Forge download URL");
        var targetDir = Path.GetDirectoryName(destPath)
            ?? throw new InvalidOperationException($"Destination path has no parent directory: {destPath}");
        Directory.CreateDirectory(targetDir);

        var tempPath = SafeFileHelpers.CreateDownloadTempPath(targetDir, Path.GetFileName(destPath));
        try
        {
            using var response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            await using (var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            await using (var target = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
            {
                await source.CopyToAsync(target, ct).ConfigureAwait(false);
            }

            await SafeFileHelpers.VerifyExpectedHashAsync(tempPath, expectedSha1, ct).ConfigureAwait(false);
            File.Move(tempPath, destPath, overwrite: true);
        }
        finally
        {
            TryDeleteFile(tempPath);
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // Non-fatal cleanup failure.
        }
    }

    private static string ComputeSha1(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        var hash = SHA1.HashData(stream);
        return Convert.ToHexString(hash);
    }

    private static async Task ExtractEntryAsync(ZipArchiveEntry entry, string destPath, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
        using var source = entry.Open();
        using var dest = File.Create(destPath);
        await source.CopyToAsync(dest, ct).ConfigureAwait(false);
    }

    private static string CombineUnderDirectory(string rootDirectory, string relativePath)
    {
        ValidateRelativePath(relativePath, "relative path");

        var root = Path.GetFullPath(rootDirectory);
        var fullPath = Path.GetFullPath(Path.Combine(root, relativePath));
        var rootWithSeparator = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;

        if (!fullPath.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Resolved installer path escapes target folder: {relativePath}");

        return fullPath;
    }

    private static void ValidateZipEntryName(string entryName, string label)
    {
        if (string.IsNullOrWhiteSpace(entryName))
            throw new InvalidOperationException($"{label} is empty.");

        if (entryName.StartsWith('/') ||
            entryName.StartsWith('\\') ||
            entryName.Contains('\\') ||
            entryName.Split('/').Any(part => part is "" or "." or ".."))
        {
            throw new InvalidOperationException($"{label} is not safe: {entryName}");
        }
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

    public void Dispose()
    {
        if (_ownsHttpClient)
            _http.Dispose();
    }
}
