using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using HalerithAddonClient.Core.ContentSearch;

namespace HalerithAddonClient.Core.LocalModpacks;

public sealed class PreparedLocalPack : IDisposable
{
    public required LocalModpack Pack { get; init; }
    public required ManifestModpack Manifest { get; init; }
    public required ModpackFileReview Review { get; init; }
    public required string Root { get; init; }
    public string? PreviousState { get; init; }
    public void Dispose()
    {
        var path = Manifest.VerifiedLocalArchivePath;
        if (path is null) return;
        var name = Path.GetFileName(path);
        if (name.StartsWith("prepared-", StringComparison.Ordinal) && name.EndsWith(".zip", StringComparison.Ordinal)
            && Guid.TryParseExact(name[9..^4], "N", out _)
            && path == LocalModpackStore.SafePath(Root, ".halerith/local-pack-cache/" + name, true))
            try { File.Delete(path); } catch (IOException) { }
    }
}

/// <summary>Local pack files have explicit ownership; unrelated files are never inferred as obsolete.</summary>
public sealed partial class LocalPackInstaller
{
    private readonly Action<string> _log;
    private readonly Func<string?, string, string?, long?, CancellationToken, Task> _download;
    public LocalPackInstaller(Action<string>? log = null) : this(log, null) { }
    internal LocalPackInstaller(Action<string>? log,
        Func<string?, string, string?, long?, CancellationToken, Task>? download)
    { _log = log ?? (_ => { }); _download = download ?? LocalPackDownloads.DownloadAsync; }

    public async Task<PreparedLocalPack> PrepareAsync(string root, LocalModpack pack, bool resetOverrides = false, CancellationToken ct = default, string? instanceId = null)
    {
        var previous = LocalModpackStore.Load(root);
        var statePath = LocalModpackStore.StatePath(root);
        var previousState = File.Exists(statePath) ? File.ReadAllText(statePath) : null;
        if (previous is not null)
        {
            if (previous.Provider != pack.Provider || previous.ProjectId != pack.ProjectId || (pack.ProjectId.Length == 0 && previous.Name != pack.Name))
                throw new InvalidOperationException("Remove the existing pack before installing another pack.");
            pack.Id = previous.Id;
            if (string.IsNullOrWhiteSpace(pack.IconUrl)) pack.IconUrl = previous.IconUrl;
            if (!resetOverrides)
                foreach (var member in pack.Members)
                {
                    var old = previous.Members.FirstOrDefault(m => m.Path == member.Path);
                    if (old is not null) { member.Overridden = old.Overridden; member.Enabled = old.Enabled; member.OverridePath = old.OverridePath; }
                }
            else foreach (var member in pack.Members) { member.Overridden = false; member.OverridePath = null; if (!member.Optional) member.Enabled = true; }
            pack.RetiredPaths.AddRange(previous.RetiredPaths);
            foreach (var pair in previous.RetiredReplacementPaths) pack.RetiredReplacementPaths[pair.Key] = pair.Value;
            foreach (var old in previous.Members.Where(m => m.OverridePath is not null))
                if (!pack.Members.Any(m => m.Overridden && m.OverridePath == old.OverridePath))
                {
                    pack.RetiredPaths.Add(old.OverridePath!);
                    if (pack.Members.FirstOrDefault(m => m.Project.Id == old.Project.Id && m.Project.Source == old.Project.Source) is { } replacement)
                        pack.RetiredReplacementPaths[old.OverridePath!] = replacement.Path;
                }
        }
        if (instanceId is not null)
            foreach (var installed in LocalInstanceContentStore.Load(instanceId).Items)
                if (pack.Members.FirstOrDefault(m => !m.Overridden && m.Project.Id.Length > 0 && m.Project.Id == installed.SourceId && m.Project.Source == installed.Source) is { } replacement)
                {
                    var path = ContentDownloader.GetSubfolder(installed.ContentType) + "/" + installed.InstalledVersion.Filename;
                    pack.RetiredPaths.Add(path); pack.RetiredReplacementPaths[path] = replacement.Path;
                }
        pack.RetiredPaths = pack.RetiredPaths.Select(p => LocalModpackStore.Relative(p)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var archive = LocalModpackStore.SafePath(root, $".halerith/local-pack-cache/{pack.ArchiveHash}.zip", true);
        await SafeFileHelpers.VerifyExpectedHashAsync(pack.ArchivePath, new ManifestHashInfo { Algorithm = "sha256", Hash = pack.ArchiveHash }, ct);
        Directory.CreateDirectory(Path.GetDirectoryName(archive)!);
        if (!string.Equals(Path.GetFullPath(pack.ArchivePath), archive, StringComparison.OrdinalIgnoreCase)) File.Copy(pack.ArchivePath, archive, true);
        pack.ArchivePath = archive;
        var preparedPath = LocalModpackStore.SafePath(root, $".halerith/local-pack-cache/prepared-{Guid.NewGuid():N}.zip", true);
        var files = new List<ManifestModpackFile>();
        try
        {
        using (var output = ZipFile.Open(preparedPath, ZipArchiveMode.Create))
        using (var original = ZipFile.OpenRead(archive))
        {
            long expanded = 0;
            foreach (var file in pack.BundledFiles)
            {
                ct.ThrowIfCancellationRequested();
                expanded += file.Size; if (expanded > LocalPackInspector.MaxExpandedBytes) throw new InvalidDataException("Prepared pack is too large.");
                var source = original.GetEntry(file.ArchivePath) ?? throw new InvalidDataException("Bundled pack entry is missing.");
                var path = LocalModpackStore.Relative(file.Path);
                using var input = source.Open(); using var destination = output.CreateEntry(path).Open();
                await LocalPackInspector.CopyBoundedAsync(input, destination, file.Size, ct);
                files.Add(new ManifestModpackFile { Path = path, ArchivePath = path, Sha256 = file.Sha256, Size = file.Size, Category = file.Category });
            }
            foreach (var member in pack.Members.Where(m => m.Enabled && !m.Overridden))
            {
                ct.ThrowIfCancellationRequested();
                if (member.Path.Length == 0) { member.Missing = true; continue; }
                var hash = SafeFileHelpers.RequireExpectedHash(member.Version.FileHash, "Pack member");
                var cacheKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(member.Version.FileHash!))).ToLowerInvariant();
                var cached = LocalModpackStore.SafePath(root, $".halerith/local-pack-cache/files/{cacheKey}", true);
                try
                {
                    var valid = false;
                    if (File.Exists(cached))
                        try { await SafeFileHelpers.VerifyExpectedHashAsync(cached, hash, ct); valid = true; } catch (InvalidOperationException) { }
                    if (!valid)
                    {
                        if (!LocalPackDownloads.IsAllowedUrl(member.Version.DownloadUrl)) { member.Missing = true; continue; }
                        _log("Downloading " + member.Project.Name);
                        await _download(member.Version.DownloadUrl, cached, member.Version.FileHash, member.Version.FileSizeBytes, ct);
                    }
                    var size = new FileInfo(cached).Length;
                    expanded += size; if (expanded > LocalPackInspector.MaxExpandedBytes) throw new InvalidDataException("Prepared pack is too large.");
                    var path = LocalModpackStore.Relative(member.Path);
                    await using var input = File.OpenRead(cached);
                    var sha = Convert.ToHexString(await SHA256.HashDataAsync(input, ct)).ToLowerInvariant(); input.Position = 0;
                    using var destination = output.CreateEntry(path).Open(); await input.CopyToAsync(destination, ct);
                    files.Add(new ManifestModpackFile { Path = path, ArchivePath = path, Size = size, Sha256 = sha, Category = member.Project.ContentType });
                    member.Missing = false;
                }
                catch (Exception e) when (e is HttpRequestException or InvalidOperationException)
                { member.Missing = true; _log(member.Project.Name + " is Missing: " + e.Message); }
            }
        }
        await using var prepared = File.OpenRead(preparedPath);
        var preparedHash = Convert.ToHexString(await SHA256.HashDataAsync(prepared, ct)).ToLowerInvariant();
        var manifest = new ManifestModpack
        {
            Id = pack.Id, RevisionId = new Guid(SHA256.HashData(Encoding.UTF8.GetBytes(pack.ArchiveHash + pack.VersionId))[..16]).ToString(),
            Name = pack.Name, VersionName = pack.Version, VersionId = pack.VersionId, Provider = pack.Provider, ProjectId = pack.ProjectId,
            VerifiedLocalArchivePath = preparedPath, Files = files,
            LocalRetiredPaths = pack.RetiredPaths,
            Archive = new ManifestModpackArchive { Sha256 = preparedHash, Size = prepared.Length, Filename = "local-pack.zip" }
        };
        var review = ModpackOverlayService.Review(manifest, root, ModpackFilePolicies.Preserve, reopen: true);
        var memberPaths = pack.Members.Where(m => m.Path.Length > 0).ToDictionary(m => m.Path, StringComparer.OrdinalIgnoreCase);
        var oldMemberPaths = previous?.Members.Where(m => m.IsAddon && m.Path.Length > 0).Select(m => m.Path).ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];
        foreach (var choice in review.Files)
        {
            if (memberPaths.TryGetValue(choice.Path, out var member) && (member.IsAddon || member.Missing))
            {
                choice.SelectedAction = member.Overridden || member.Missing ? (choice.ProposedAction == "No change" ? "No change" : "Keep") : choice.ProposedAction;
                choice.FixedAction = choice.SelectedAction;
            }
            else if (oldMemberPaths.Contains(choice.Path) || pack.RetiredPaths.Contains(choice.Path))
            {
                var unavailableReplacement = pack.RetiredReplacementPaths.TryGetValue(choice.Path, out var replacement)
                    && pack.Members.Any(m => m.Path == replacement && m.Missing);
                choice.SelectedAction = unavailableReplacement && choice.ProposedAction != "No change" ? "Keep" : choice.ProposedAction;
                choice.FixedAction = choice.SelectedAction;
            }
            else if (pack.Policy == ModpackFilePolicies.Ignore && choice.ProposedAction != "No change") choice.SelectedAction = "Keep";
            else if (pack.Policy == ModpackFilePolicies.Replace) choice.SelectedAction = choice.ProposedAction;
        }
        return new PreparedLocalPack { Root = root, Pack = pack, Manifest = manifest, Review = review, PreviousState = previousState };
        }
        catch { try { File.Delete(preparedPath); } catch (IOException) { } throw; }
    }

    public async Task ApplyAsync(string instanceId, PreparedLocalPack prepared, CancellationToken ct = default)
    {
        using var lease = Acquire(prepared.Root);
        var pack = prepared.Pack;
        var statePath = LocalModpackStore.StatePath(prepared.Root);
        if ((File.Exists(statePath) ? File.ReadAllText(statePath) : null) != prepared.PreviousState)
            throw new InvalidOperationException("The pack changed while reviewing. Review it again.");
        var prior = LocalModpackStore.Load(prepared.Root);
        if (prepared.Review.Files.Any(f => f.FixedAction is not null && f.SelectedAction != f.FixedAction))
            throw new InvalidOperationException("Pack addon changes cannot be altered in the configuration review.");
        // Record intent before touching live content; an interruption remains retryable.
        pack.Failed = true; LocalModpackStore.Save(prepared.Root, pack);
        var config = new RuntimeConfig { GameDataPath = prepared.Root, ModpackFilePolicy = ModpackFilePolicies.Preserve };
        await new ModpackOverlayService(config, _log).ApplyReviewedAsync(prepared.Manifest, prepared.Review, ct);
        foreach (var member in pack.Members.Where(m => m.IsAddon && m.Enabled && !m.Overridden && !m.Missing))
        {
            var choice = prepared.Review.Files.FirstOrDefault(f => f.Path == member.Path);
            if (choice?.SelectedAction == "Keep") { member.Overridden = true; continue; }
            if (!string.IsNullOrEmpty(member.Project.Id) && File.Exists(LocalModpackStore.SafePath(prepared.Root, member.Path)))
                LocalInstanceContentStore.AddOrUpdateItem(instanceId, prepared.Root, member.Project, member.Version);
        }
        pack.AppliedAtUtc = DateTime.UtcNow; pack.Failed = false;
        pack.KeptFiles = prepared.Review.Files.Where(f => f.SelectedAction == "Keep" && f.FixedAction is null).Select(f => f.Path).ToList();
        pack.RetiredPaths = pack.RetiredPaths.Where(p => prepared.Review.Files.Any(f => f.Path == p && f.SelectedAction == "Keep")).ToList();
        pack.RetiredReplacementPaths = pack.RetiredReplacementPaths.Where(p => pack.RetiredPaths.Contains(p.Key)).ToDictionary(p => p.Key, p => p.Value);
        LocalModpackStore.Save(prepared.Root, pack);
        PruneCache(prepared.Root, pack, prior);
    }

    private static void PruneCache(string root, LocalModpack pack, LocalModpack? previous)
    {
        var cache = LocalModpackStore.SafePath(root, ".halerith/local-pack-cache", true);
        foreach (var path in Directory.EnumerateFiles(cache, "*.zip"))
        {
            var hash = Path.GetFileNameWithoutExtension(path);
            if (hash.Length == 64 && hash.All(Uri.IsHexDigit) && hash != pack.ArchiveHash && hash != previous?.ArchiveHash)
                File.Delete(LocalModpackStore.SafePath(root, ".halerith/local-pack-cache/" + Path.GetFileName(path), true));
        }
        var files = Path.Combine(cache, "files");
        if (!Directory.Exists(files)) return;
        var keep = pack.Members.Concat(previous?.Members ?? []).Where(m => !string.IsNullOrWhiteSpace(m.Version.FileHash))
            .Select(m => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(m.Version.FileHash!))).ToLowerInvariant()).ToHashSet();
        foreach (var path in Directory.EnumerateFiles(files))
        {
            var hash = Path.GetFileName(path);
            if (hash.Length == 64 && hash.All(Uri.IsHexDigit) && !keep.Contains(hash))
                File.Delete(LocalModpackStore.SafePath(root, ".halerith/local-pack-cache/files/" + hash, true));
        }
    }

    public void Remove(string instanceId, string root)
    {
        using var lease = Acquire(root);
        var pack = LocalModpackStore.Load(root); if (pack is null) return;
        foreach (var member in pack.Members.Where(m => m.IsAddon))
        {
            if (member.Path.Length > 0) RemoveOwnedFile(root, member.Path);
            if (member.OverridePath is { Length: > 0 } path && path != member.Path) RemoveOwnedFile(root, path);
            if (member.Project.Id.Length > 0) LocalInstanceContentStore.RemoveItem(instanceId, member.Project.Source, member.Project.Id);
        }
        if (Guid.TryParse(pack.Id, out _))
        {
            var state = LocalModpackStore.SafePath(root, $".halerith/modpacks/{pack.Id}.json", true);
            if (File.Exists(state)) File.Delete(state);
        }
        File.Delete(LocalModpackStore.StatePath(root));
    }
    internal static void RemoveOwnedFile(string root, string path)
    {
        var target = LocalModpackStore.SafePath(root, path);
        if (!File.Exists(target)) return;
        var backup = LocalModpackStore.SafePath(root, ".halerith/modpack-backups/" + Guid.NewGuid().ToString("N") + "/" + path, true);
        Directory.CreateDirectory(Path.GetDirectoryName(backup)!); File.Copy(target, backup); File.Delete(target);
    }

    public async Task ChangeMemberAsync(string instanceId, string root, string memberPath, ContentVersionResult? version, bool enabled, CancellationToken ct = default)
    {
        using var lease = Acquire(root);
        var pack = LocalModpackStore.Load(root) ?? throw new InvalidOperationException("The pack is no longer installed.");
        var member = pack.Members.Single(m => m.Path == memberPath);
        var oldPath = member.OverridePath ?? member.Path;
        if (!enabled)
        {
            RemoveOwnedFile(root, oldPath);
            if (member.Project.Id.Length > 0) LocalInstanceContentStore.RemoveItem(instanceId, member.Project.Source, member.Project.Id);
            member.Overridden = true; member.Enabled = false; member.OverridePath = null;
        }
        else
        {
            var selected = version ?? member.Version;
            var relative = ContentDownloader.GetSubfolder(member.Project.ContentType) + "/" + SafeFileHelpers.RequireSafeFileName(selected.Filename, "Addon filename");
            var target = LocalModpackStore.SafePath(root, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            var staged = LocalModpackStore.SafePath(root, relative + "." + Guid.NewGuid().ToString("N") + ".tmp");
            var backup = LocalModpackStore.SafePath(root, ".halerith/modpack-backups/" + Guid.NewGuid().ToString("N") + "/" + relative, true);
            try
            {
                await _download(selected.DownloadUrl, staged, selected.FileHash, selected.FileSizeBytes, ct);
                ct.ThrowIfCancellationRequested();
                Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
                VerifiedContentCommit.Commit(staged, target, LocalModpackStore.SafePath(root, oldPath), backup, log: _log);
            }
            finally { if (File.Exists(staged)) File.Delete(staged); }
            member.Overridden = version is not null && version.SourceVersionId != member.Version.SourceVersionId;
            member.OverridePath = member.Overridden ? relative : null; member.Enabled = true; member.Missing = false;
            if (member.Project.Id.Length > 0) LocalInstanceContentStore.AddOrUpdateItem(instanceId, root, member.Project, selected);
        }
        LocalModpackStore.Save(root, pack);
    }

    private static FileStream Acquire(string root)
    {
        var path = LocalModpackStore.SafePath(root, ".halerith/local-pack.lock", true);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException error) { throw new InvalidOperationException("Another pack operation is already running for this instance. Wait for it to finish.", error); }
    }

    public static void DiscardUnappliedCache(string root)
    {
        if (LocalModpackStore.Load(root) is not null) return;
        var cache = LocalModpackStore.SafePath(root, ".halerith/local-pack-cache", true);
        if (!Directory.Exists(cache)) return;
        foreach (var directory in new[] { cache, LocalModpackStore.SafePath(root, ".halerith/local-pack-cache/files", true) })
        {
            if (!Directory.Exists(directory)) continue;
            foreach (var path in Directory.EnumerateFiles(directory))
            {
                var name = Path.GetFileNameWithoutExtension(path);
                if (name.Length != 64 || !name.All(Uri.IsHexDigit)) continue;
                var safe = LocalModpackStore.SafePath(root, Path.GetRelativePath(root, path), true); File.Delete(safe);
            }
        }
        var members = Path.Combine(cache, "files");
        if (Directory.Exists(members) && !Directory.EnumerateFileSystemEntries(members).Any()) Directory.Delete(members);
        if (!Directory.EnumerateFileSystemEntries(cache).Any()) Directory.Delete(cache);
    }
}
