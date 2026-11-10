using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace HalerithAddonClient.Core;

public sealed partial class ModpackOverlayService
{
    public static ModpackFileReview Review(ManifestModpack pack, string root, string preference, bool reopen = false)
    {
        Validate(pack);
        root = Path.GetFullPath(root);
        var statePath = StatePath(root, pack.Id);
        AssertNoReparsePoint(root, statePath);
        var prior = ReadState(statePath);
        var files = pack.Files.Where(f => !pack.SeparatelyManagedPaths.Contains(CombineUnderRoot(root, NormalizeRelative(f.Path)))).ToList();
        var policy = pack.ForceConfig ? ModpackFilePolicies.Replace : ModpackFilePolicies.Normalize(preference);
        var publication = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            pack.RevisionId, pack.ApplicationRevision, pack.ForceConfig, Policy = policy,
            Files = files.OrderBy(f => f.Path, StringComparer.Ordinal).Select(f => new { f.Path, f.ArchivePath, f.Size, f.Sha256 })
        }, PackJsonOptions))));
        var review = new ModpackFileReview
        {
            Revision = publication, InstalledVersion = prior?.VersionName ?? "", Policy = policy, Forced = pack.ForceConfig,
            RevisionChanged = prior?.Publication != publication
        };
        if (policy == ModpackFilePolicies.Ignore) return review;
        var approved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            var path = NormalizeRelative(file.Path);
            if (!approved.Add(path)) throw new InvalidOperationException("The modpack contains duplicate destination paths.");
            Add(path, file);
        }
        if (prior is not null)
            foreach (var path in prior.Files.Keys.Where(p => !approved.Contains(p))) Add(path, null);
        if (pack.VerifiedLocalArchivePath is not null)
            foreach (var path in pack.LocalRetiredPaths.Where(p => !approved.Contains(p) && !review.Files.Any(f => f.Path == p))) Add(path, null);
        return review;

        void Add(string path, ManifestModpackFile? file)
        {
            if (pack.SeparatelyManagedPaths.Contains(CombineUnderRoot(root, NormalizeRelative(path)))) return;
            if (NormalizeRelative(path).Split('/').Any(ProtectedNames.Contains))
                throw new InvalidOperationException("A tracked modpack path is protected.");
            var target = CombineUnderRoot(root, NormalizeRelative(path));
            AssertNoReparsePoint(root, target);
            var current = File.Exists(target) ? HashFileAsync(target, default).GetAwaiter().GetResult() : null;
            var same = file is not null && string.Equals(current, file.Sha256, StringComparison.OrdinalIgnoreCase);
            var action = file is null ? (current is null ? "No change" : "Remove") : same ? "No change" : current is null ? "Install" : "Replace";
            var chosen = action;
            var remembered = !reopen && prior?.Publication == publication && prior.Choices?.ContainsKey(path) == true;
            if (policy == ModpackFilePolicies.Review && remembered && action != "No change")
                chosen = prior!.Choices![path] == "Keep" ? "Keep" : action;
            else if (policy == ModpackFilePolicies.Preserve && current is not null && action != "No change"
                && !(prior?.Files.TryGetValue(path, out var oldHash) == true && string.Equals(current, oldHash, StringComparison.OrdinalIgnoreCase)))
                chosen = "Keep";
            review.Files.Add(new ModpackFileChoice
            {
                Path = path, File = file, ObservedHash = current, ProposedAction = action, SelectedAction = chosen,
                Change = file is null ? "Removed from pack" : same ? "Unchanged" : current is null ? "Missing" : "Changed",
                RequiresReview = policy == ModpackFilePolicies.Review && !remembered && action != "No change"
            });
        }
    }

    public async Task ApplyReviewedAsync(ManifestModpack pack, ModpackFileReview review, CancellationToken ct)
    {
        var root = Path.GetFullPath(_config.GameDataPath);
        var fresh = Review(pack, root, _config.ModpackFilePolicy);
        if (fresh.Policy == ModpackFilePolicies.Ignore) return;
        if (fresh.Revision != review.Revision || fresh.Files.Count != review.Files.Count)
            throw new InvalidOperationException("The modpack review changed. Review the files again.");
        foreach (var choice in review.Files)
        {
            var expected = fresh.Files.SingleOrDefault(f => f.Path == choice.Path);
            if (expected is null || expected.ObservedHash != choice.ObservedHash || expected.ProposedAction != choice.ProposedAction
                || !expected.Actions.Contains(choice.SelectedAction)
                || (pack.ForceConfig && choice.SelectedAction != expected.ProposedAction))
                throw new InvalidOperationException("A modpack file changed during review. Review the files again.");
        }
        // Content can be synced before the separate Minecraft runtime build.
        // Create the instance only after validating the approved review, never during preview.
        ct.ThrowIfCancellationRequested();
        AssertNoReparsePoint(root, StatePath(root, pack.Id));
        Directory.CreateDirectory(root);
        var prior = ReadState(StatePath(root, pack.Id));
        var nextFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var nextRevisions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var writes = review.Files.Where(f => f.SelectedAction is "Install" or "Replace").ToList();
        string? archivePath = writes.Count == 0 ? null : await VerifiedArchiveAsync(pack, root, ct).ConfigureAwait(false);
        using var archive = archivePath is null ? null : ZipFile.OpenRead(archivePath);
        var entries = archive?.Entries.GroupBy(e => NormalizeRelative(e.FullName), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
        foreach (var choice in review.Files)
        {
            ct.ThrowIfCancellationRequested();
            var target = CombineUnderRoot(root, choice.Path);
            AssertNoReparsePoint(root, target);
            if (choice.SelectedAction == "Keep")
            {
                var retainedHash = prior?.Files.GetValueOrDefault(choice.Path) ?? choice.ObservedHash;
                if (retainedHash is not null)
                {
                    nextFiles[choice.Path] = retainedHash;
                    nextRevisions[choice.Path] = prior?.FileRevisions?.GetValueOrDefault(choice.Path) ?? prior?.RevisionId ?? "";
                }
            }
            else if (choice.File is not null)
            {
                nextFiles[choice.Path] = choice.File.Sha256.ToLowerInvariant();
                nextRevisions[choice.Path] = pack.RevisionId;
            }
            if (choice.SelectedAction is "No change" or "Keep") continue;
            var current = File.Exists(target) ? await HashFileAsync(target, ct).ConfigureAwait(false) : null;
            if (current != choice.ObservedHash) throw new InvalidOperationException("A modpack file changed while applying. Sync again.");
            if (choice.SelectedAction == "Remove") { Backup(target, root, choice.Path); File.Delete(target); continue; }
            var file = choice.File!;
            if (entries is null || !entries.TryGetValue(NormalizeRelative(file.ArchivePath), out var matches)
                || matches.Count != 1 || matches[0].Length != file.Size || IsLink(matches[0]))
                throw new InvalidOperationException("An approved modpack entry is unavailable or changed.");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            var staged = target + $".halerith-{Guid.NewGuid():N}.tmp";
            try
            {
                await ExtractVerifiedAsync(matches[0], staged, file.Size, file.Sha256, ct).ConfigureAwait(false);
                if (current is not null) Backup(target, root, choice.Path);
                VerifiedContentCommit.Commit(staged, target, target, null, log: _log);
            }
            finally { TryDelete(staged); }
            _log($"Applied modpack file: {choice.Path}");
        }
        AssertNoReparsePoint(root, StatePath(root, pack.Id));
        WriteState(StatePath(root, pack.Id), new ModpackState(pack.RevisionId, pack.Archive.Sha256.ToLowerInvariant(), nextFiles,
            review.Revision, pack.VersionName, review.Files.ToDictionary(f => f.Path, f => f.SelectedAction, StringComparer.OrdinalIgnoreCase), nextRevisions));
        TryDelete(StatePath(root, pack.Id) + ".failed");
        // Only obsolete archives in this owned cache; recovery backups are independent.
        var cache = CacheDirectory(root, pack.Id);
        PruneCache(root, [Path.Combine(cache, pack.Archive.Sha256.ToLowerInvariant() + ".zip")]);
    }

    private static string CacheDirectory(string root, string packId) => Path.Combine(root, ".halerith", "modpack-cache", packId);

    private async Task<string> VerifiedArchiveAsync(ManifestModpack pack, string root, CancellationToken ct)
    {
        if (pack.VerifiedLocalArchivePath is { } local)
        {
            // Explicit internal capability; no file:// or remote-manifest bypass.
            AssertNoReparsePoint(root, local);
            if (!File.Exists(local) || new FileInfo(local).Length != pack.Archive.Size
                || !string.Equals(await HashFileAsync(local, ct).ConfigureAwait(false), pack.Archive.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The inspected local pack archive changed.");
            return local;
        }
        var cache = CacheDirectory(root, pack.Id);
        var destination = Path.Combine(cache, pack.Archive.Sha256.ToLowerInvariant() + ".zip");
        AssertNoReparsePoint(root, destination);
        Directory.CreateDirectory(cache);
        var prior = ReadState(StatePath(root, pack.Id));
        var keep = new HashSet<string>(StringComparer.Ordinal) { destination };
        if (prior is not null && prior.ArchiveSha256.Length == 64 && prior.ArchiveSha256.All(Uri.IsHexDigit))
            keep.Add(Path.Combine(cache, prior.ArchiveSha256.ToLowerInvariant() + ".zip"));
        PruneCache(root, keep);
        if (File.Exists(destination) && new FileInfo(destination).Length == pack.Archive.Size
            && string.Equals(await HashFileAsync(destination, ct).ConfigureAwait(false), pack.Archive.Sha256, StringComparison.OrdinalIgnoreCase))
            return destination;
        var staged = SafeFileHelpers.CreateDownloadTempPath(cache, "pack.zip");
        try
        {
            _log($"Downloading modpack files: {pack.VersionName}");
            await _api!.DownloadFileAsync(pack.Archive.Url, staged, ct, pack.Archive.Size).ConfigureAwait(false);
            if (new FileInfo(staged).Length != pack.Archive.Size) throw new InvalidOperationException("The modpack archive size did not match the approved release.");
            await SafeFileHelpers.VerifyExpectedHashAsync(staged, new ManifestHashInfo { Algorithm = "sha256", Hash = pack.Archive.Sha256 }, ct).ConfigureAwait(false);
            VerifiedContentCommit.Commit(staged, destination, destination, null);
            return destination;
        }
        finally { TryDelete(staged); }
    }

    private static void PruneCache(string root, ICollection<string> keep)
    {
        var cacheRoot = Path.Combine(root, ".halerith", "modpack-cache");
        if (!Directory.Exists(cacheRoot)) return;
        foreach (var directory in Directory.EnumerateDirectories(cacheRoot))
        {
            if (!Guid.TryParse(Path.GetFileName(directory), out _)) continue;
            AssertNoReparsePoint(root, Path.Combine(directory, "boundary"));
            foreach (var file in Directory.EnumerateFiles(directory, "*.zip"))
                if (Path.GetFileNameWithoutExtension(file) is { Length: 64 } hash && hash.All(Uri.IsHexDigit) && !keep.Contains(file)) TryDelete(file);
        }
    }

    public static bool LastApplyFailed(ManifestModpack pack, string root)
        => Guid.TryParse(pack.Id, out _) && File.Exists(StatePath(root, pack.Id) + ".failed");

    internal static void RecordFailure(ManifestModpack pack, string root)
    {
        try
        {
            Validate(pack);
            var path = StatePath(Path.GetFullPath(root), pack.Id) + ".failed";
            AssertNoReparsePoint(root, path); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "Apply failed; retry required.");
        }
        catch { /* Error reporting must not mask the sync failure. */ }
    }
}
