using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using HalerithAddonClient.Core.ContentSearch;

namespace HalerithAddonClient.Core.LocalModpacks;

/// <summary>Inspects a client pack without extracting or running any of its content.</summary>
public sealed class LocalPackInspector
{
    public const long MaxArchiveBytes = 2L * 1024 * 1024 * 1024;
    public const long MaxExpandedBytes = 2L * 1024 * 1024 * 1024;
    public async Task<LocalModpack> InspectAsync(string archivePath, ContentSearchResult? project = null,
        ContentVersionResult? release = null, CancellationToken ct = default)
    {
        var info = new FileInfo(archivePath);
        if (!info.Exists || info.Length is < 1 or > MaxArchiveBytes) throw new InvalidDataException("Invalid pack archive size.");
        using var zip = ZipFile.OpenRead(archivePath);
        if (zip.Entries.Count > 20000 || zip.Entries.Sum(e => e.Length) > MaxExpandedBytes) throw new InvalidDataException("Pack archive exceeds extraction limits.");
        var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in zip.Entries)
        {
            ct.ThrowIfCancellationRequested();
            if (((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000) throw new InvalidDataException("Links are not allowed in packs.");
            var path = LocalModpackStore.Relative(entry.FullName.TrimEnd('/'));
            if (entry.FullName.EndsWith('/')) continue;
            if (!entries.TryAdd(path, entry)) throw new InvalidDataException("Duplicate pack archive entry.");
        }
        var mr = entries.GetValueOrDefault("modrinth.index.json");
        var cf = entries.GetValueOrDefault("manifest.json");
        if ((mr is null) == (cf is null)) throw new InvalidDataException("Select a Modrinth .mrpack or CurseForge client export, not a server/archive ZIP.");
        var manifest = mr ?? cf!;
        if (manifest.Length > 8 * 1024 * 1024) throw new InvalidDataException("Pack manifest is too large.");
        using var stream = manifest.Open();
        using var manifestBytes = new MemoryStream();
        await CopyBoundedAsync(stream, manifestBytes, manifest.Length, ct);
        using var json = JsonDocument.Parse(manifestBytes.ToArray());
        var data = json.RootElement;
        await using var input = File.OpenRead(archivePath);
        var pack = new LocalModpack
        {
            ArchivePath = Path.GetFullPath(archivePath), ArchiveHash = Convert.ToHexString(await SHA256.HashDataAsync(input, ct)).ToLowerInvariant(),
            Provider = project?.Source ?? (mr is not null ? "modrinth" : "curseforge"), ProjectId = project?.Id ?? "",
            IconUrl = project?.IconUrl,
            Name = project?.Name ?? Text(data, "name"), Version = release?.VersionName ?? Text(data, mr is not null ? "versionId" : "version"),
            VersionId = release?.SourceVersionId ?? ""
        };
        if (string.IsNullOrWhiteSpace(pack.Name)) throw new InvalidDataException("Pack name is missing.");
        string overrides;
        if (mr is not null)
        {
            if (data.GetProperty("formatVersion").GetInt32() != 1 || Text(data, "game") != "minecraft") throw new InvalidDataException("Unsupported Modrinth pack format.");
            var deps = data.GetProperty("dependencies");
            pack.MinecraftVersion = Text(deps, "minecraft");
            var loaders = new[] { "fabric-loader", "quilt-loader", "forge", "neoforge" }.Where(k => deps.TryGetProperty(k, out _)).ToList();
            if (loaders.Count > 1 || deps.EnumerateObject().Any(d => d.Name != "minecraft" && !loaders.Contains(d.Name))) throw new InvalidDataException("Pack declares unsupported or conflicting runtime dependencies.");
            if (loaders.Count == 1) { pack.Loader = loaders[0].Replace("-loader", ""); pack.LoaderVersion = Text(deps, loaders[0]); }
            foreach (var file in data.GetProperty("files").EnumerateArray())
            {
                var client = file.TryGetProperty("env", out var env) ? Text(env, "client") : "required";
                if (client == "unsupported") continue;
                var path = LocalModpackStore.Relative(Text(file, "path"));
                var hashes = file.GetProperty("hashes");
                var hash = Text(hashes, "sha512"); if (hash.Length == 0) hash = Text(hashes, "sha1");
                SafeFileHelpers.RequireExpectedHash(hash, "Modrinth pack member");
                var url = file.GetProperty("downloads").EnumerateArray().Select(v => v.GetString()).FirstOrDefault(LocalPackDownloads.IsAllowedUrl);
                var identity = url is null ? [] : new Uri(url).AbsolutePath.Split('/');
                var modrinth = url is not null && new Uri(url).Host == "cdn.modrinth.com" && identity.Length == 6 && identity[1] == "data" && identity[3] == "versions";
                pack.Members.Add(new LocalPackMember
                {
                    Path = path, Optional = client == "optional", Enabled = client != "optional",
                    Project = new ContentSearchResult { Source = "modrinth", Id = modrinth ? identity[2] : "", Slug = modrinth ? identity[2] : "", Name = Path.GetFileName(path), ContentType = TypeFor(path) },
                    Version = new ContentVersionResult { SourceVersionId = modrinth ? identity[4] : "", Filename = Path.GetFileName(path), VersionName = Path.GetFileName(path), DownloadUrl = url,
                        FileHash = hash, FileSizeBytes = file.GetProperty("fileSize").GetInt64() }, Missing = url is null
                });
            }
            try
            {
                var metadata = await new ModrinthSearchService().GetProjectsAsync(pack.Members.Select(m => m.Project.Id), ct);
                foreach (var member in pack.Members)
                    if (metadata.TryGetValue(member.Project.Id, out var item)) member.Project = item;
            }
            catch (HttpRequestException) { pack.Warnings.Add("Some project names and icons are unavailable; exact pack files are still retained."); }
            overrides = "overrides";
        }
        else
        {
            if (Text(data, "manifestType") != "minecraftModpack" || data.GetProperty("manifestVersion").GetInt32() != 1) throw new InvalidDataException("Not a supported CurseForge client export.");
            var minecraft = data.GetProperty("minecraft"); pack.MinecraftVersion = Text(minecraft, "version");
            var loaders = minecraft.GetProperty("modLoaders").EnumerateArray().Where(l => l.TryGetProperty("primary", out var primary) && primary.GetBoolean()).ToList();
            if (loaders.Count != 1) throw new InvalidDataException("Pack must specify one primary loader.");
            var loader = Text(loaders[0], "id").Split('-', 2);
            if (loader.Length != 2) throw new InvalidDataException("Pack loader version is missing.");
            pack.Loader = loader[0].ToLowerInvariant(); pack.LoaderVersion = loader[1];
            var provider = new CurseForgeSearchService();
            var requested = data.GetProperty("files").EnumerateArray().Select(f => (f.GetProperty("projectID").GetInt64(), f.GetProperty("fileID").GetInt64())).ToList();
            if (requested.Count > 20000) throw new InvalidDataException("Too many pack members.");
            var resolved = new Dictionary<string, (ContentSearchResult Project, ContentVersionResult Version)>();
            try { resolved = await provider.GetPackFilesAsync(requested, ct); }
            catch (Exception e) when (e is HttpRequestException or InvalidOperationException)
            { pack.Warnings.Add("CurseForge file metadata is unavailable. Unresolved items remain Missing and can be retried."); }
            foreach (var file in data.GetProperty("files").EnumerateArray())
            {
                ct.ThrowIfCancellationRequested();
                var projectId = file.GetProperty("projectID").GetInt64().ToString();
                var fileId = file.GetProperty("fileID").GetInt64().ToString();
                var required = !file.TryGetProperty("required", out var requirement) || requirement.GetBoolean();
                var member = new LocalPackMember { Optional = !required, Enabled = required,
                    Project = new ContentSearchResult { Id = projectId, Source = "curseforge", Name = "CurseForge " + projectId, ContentType = ContentTypes.Mod },
                    Version = new ContentVersionResult { SourceVersionId = fileId }, Missing = true };
                try
                {
                    if (!resolved.TryGetValue(fileId, out var exact)) throw new InvalidOperationException("File metadata unavailable.");
                    member.Project = exact.Project; member.Version = exact.Version;
                    member.Path = LocalModpackStore.Relative(ContentDownloader.GetSubfolder(member.Project.ContentType) + "/" + SafeFileHelpers.RequireSafeFileName(member.Version.Filename, "Pack filename"));
                    member.Missing = !LocalPackDownloads.IsAllowedUrl(member.Version.DownloadUrl);
                }
                catch (Exception e) when (e is HttpRequestException or InvalidOperationException)
                { pack.Warnings.Add($"Could not resolve CurseForge item {projectId}/{fileId}; it will be shown as Missing."); }
                pack.Members.Add(member);
            }
            overrides = LocalModpackStore.Relative(Text(data, "overrides"));
        }
        if (pack.Loader is not ("" or "fabric" or "quilt" or "forge" or "neoforge") || (pack.Loader.Length > 0 && !Exact(pack.LoaderVersion)) || !Exact(pack.MinecraftVersion))
            throw new InvalidDataException("The pack must declare exact Minecraft and loader versions (not latest/recommended/ranges).");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var member in pack.Members.Where(m => m.Path.Length > 0))
            if (!seen.Add(member.Path)) throw new InvalidDataException("Multiple pack members target the same file.");
        if (pack.Members.Count > 20000 || pack.Members.Sum(m => Math.Max(0, m.Version.FileSizeBytes ?? 0)) > MaxExpandedBytes)
            throw new InvalidDataException("Pack content exceeds supported size limits.");
        var bundled = new Dictionary<string, ManifestModpackFile>(StringComparer.OrdinalIgnoreCase);
        foreach (var prefix in mr is not null ? new[] { overrides + "/", "client-overrides/" } : new[] { overrides + "/" })
            foreach (var entry in entries.Values.Where(e => e.FullName.StartsWith(prefix, StringComparison.Ordinal)))
            {
                var path = LocalModpackStore.Relative(entry.FullName[prefix.Length..]);
                if (seen.Contains(path)) throw new InvalidDataException("A bundled file conflicts with a referenced pack member: " + path);
                using var content = entry.Open();
                // Hash through a bounded stream: ZIP length fields are not trusted as a byte limit.
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[81920]; long readTotal = 0; int count;
                while ((count = await content.ReadAsync(buffer, ct)) > 0)
                {
                    readTotal += count; if (readTotal > entry.Length) throw new InvalidDataException("Pack entry exceeds declared size.");
                    hash.AppendData(buffer, 0, count);
                }
                if (readTotal != entry.Length) throw new InvalidDataException("Pack entry is truncated.");
                bundled[path] = new ManifestModpackFile { Path = path, ArchivePath = entry.FullName, Size = entry.Length,
                    Sha256 = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant(), Category = TypeFor(path) };
            }
        pack.BundledFiles = bundled.Values.ToList();
        return pack;
    }
    private static bool Exact(string value) => value.Length > 0 && char.IsDigit(value[0]) && value.All(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_' or '+');
    internal static async Task CopyBoundedAsync(Stream input, Stream output, long expected, CancellationToken ct)
    {
        var buffer = new byte[81920]; long total = 0; int count;
        while ((count = await input.ReadAsync(buffer, ct)) > 0)
        {
            total += count; if (total > expected) throw new InvalidDataException("Pack entry exceeds declared size.");
            await output.WriteAsync(buffer.AsMemory(0, count), ct);
        }
        if (total != expected) throw new InvalidDataException("Pack entry is truncated.");
    }
    private static string Text(JsonElement value, string key) => value.TryGetProperty(key, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() ?? "" : "";
    public static string TypeFor(string path) => path.Split('/')[0] switch { "resourcepacks" => ContentTypes.ResourcePack, "shaderpacks" => ContentTypes.Shader, _ => ContentTypes.Mod };
    public static bool IsAddonPath(string path)
    {
        var parts = path.Split('/');
        return parts.Length == 2 && (parts[0] == "mods" && parts[1].EndsWith(".jar", StringComparison.OrdinalIgnoreCase)
            || parts[0] is "resourcepacks" or "shaderpacks" && parts[1].EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
    }
}
