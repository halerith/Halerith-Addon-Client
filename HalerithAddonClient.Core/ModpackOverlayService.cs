using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace HalerithAddonClient.Core;

/// <summary>Applies only the exact client overlay inventory approved by the Manager.</summary>
public sealed partial class ModpackOverlayService
{
    // Explicit metadata is required when the published Client disables JSON reflection defaults.
    // Keep compact JSON: changing whitespace would change existing publication fingerprints.
    private static readonly JsonSerializerOptions PackJsonOptions = new(SerializerOptions.Default)
    {
        WriteIndented = false
    };

    private static readonly HashSet<string> AllowedArchiveHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "cdn.modrinth.com", "edge.forgecdn.net", "mediafilez.forgecdn.net"
    };
    private static readonly HashSet<string> ProtectedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ".halerith", ".env", "credentials", "secrets", "secret", "private.key", "id_rsa", "accounts.json", "launcher_accounts.json"
    };

    private readonly ApiClient? _api;
    private readonly RuntimeConfig _config;
    private readonly Action<string> _log;

    public ModpackOverlayService(ApiClient api, RuntimeConfig config, Action<string> log)
    {
        _api = api;
        _config = config;
        _log = log;
    }

    public Func<ModpackFileReview, CancellationToken, Task<bool>>? ReviewFilesAsync { get; init; }

    internal ModpackOverlayService(RuntimeConfig config, Action<string> log)
    {
        _config = config;
        _log = log;
    }

    public static bool NeedsApply(ManifestModpack pack, string? gameDataPath)
        => string.IsNullOrWhiteSpace(gameDataPath) || Review(pack, gameDataPath, ModpackFilePolicies.Preserve).HasWork;

    public async Task ApplyAsync(ManifestModpack pack, CancellationToken ct)
    {
        var review = Review(pack, _config.GameDataPath, _config.ModpackFilePolicy);
        if (!review.HasWork) return;
        if (review.RequiresReview)
        {
            if (ReviewFilesAsync is null || !await ReviewFilesAsync(review, ct).ConfigureAwait(false))
                throw new OperationCanceledException("Modpack file review was cancelled.", ct);
        }
        await ApplyReviewedAsync(pack, review, ct).ConfigureAwait(false);
    }

    private static void Validate(ManifestModpack pack)
    {
        if (!Guid.TryParse(pack.Id, out _) || !Guid.TryParse(pack.RevisionId, out _)
            || (pack.VerifiedLocalArchivePath is null && (!Uri.TryCreate(pack.Archive.Url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps
                || !AllowedArchiveHosts.Contains(uri.Host))) || pack.Archive.Size < 1 || pack.Archive.Size > 2L * 1024 * 1024 * 1024
            || pack.Archive.Sha256.Length != 64 || !pack.Archive.Sha256.All(Uri.IsHexDigit)
            || pack.Files.Count > 20_000 || pack.Files.Sum(file => Math.Max(0, file.Size)) > 2L * 1024 * 1024 * 1024)
            throw new InvalidOperationException("The modpack manifest is invalid.");
        foreach (var file in pack.Files)
        {
            var path = NormalizeRelative(file.Path);
            NormalizeRelative(file.ArchivePath);
            if (file.Size < 0 || file.Sha256.Length != 64 || !file.Sha256.All(Uri.IsHexDigit)
                || path.Split('/').Any(part => ProtectedNames.Contains(part)))
                throw new InvalidOperationException("The modpack file inventory is invalid.");
        }
    }

    private static string NormalizeRelative(string value)
    {
        var path = (value ?? "").Replace('\\', '/').Trim('/');
        var parts = path.Split('/');
        if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(value) || parts.Any(part => string.IsNullOrWhiteSpace(part) || part is "." or "..")
            || path.Any(character => character < 32 || character == 127))
            throw new InvalidOperationException("A modpack path is unsafe.");
        return string.Join('/', parts);
    }

    private static string CombineUnderRoot(string root, string relative)
    {
        var full = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        var prefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) throw new InvalidOperationException("A modpack path escapes the instance folder.");
        return full;
    }

    private static void AssertNoReparsePoint(string root, string target)
    {
        for (var current = Path.GetDirectoryName(target); !string.IsNullOrEmpty(current) && current.StartsWith(root, StringComparison.OrdinalIgnoreCase); current = Path.GetDirectoryName(current))
            if (Directory.Exists(current) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("A modpack destination crosses a linked folder.");
        if (File.Exists(target) && (File.GetAttributes(target) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("A modpack destination is a linked file.");
    }

    private static bool IsLink(ZipArchiveEntry entry) => ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000;

    private static async Task ExtractVerifiedAsync(ZipArchiveEntry entry, string destination, long size, string sha256, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        await using var input = entry.Open();
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920];
        long written = 0;
        while (true)
        {
            var read = await input.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (read == 0) break;
            written += read;
            if (written > size) throw new InvalidOperationException("A modpack entry exceeded its approved size.");
            hash.AppendData(buffer, 0, read);
            await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
        }
        var actual = Convert.ToHexString(hash.GetHashAndReset());
        if (written != size || !string.Equals(actual, sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("A modpack entry failed integrity verification.");
    }

    private void Backup(string target, string root, string relative)
    {
        // Pack configuration can intentionally override a user's local edits.
        // That destructive boundary always gets a recovery copy, independent
        // of the ordinary content-file backup preference.
        var backupRoot = Path.Combine(root, ".halerith", "modpack-backups", DateTime.UtcNow.ToString("yyyyMMddHHmmssfff") + "-" + Guid.NewGuid().ToString("N"));
        var destination = CombineUnderRoot(backupRoot, relative);
        AssertNoReparsePoint(root, destination);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.Copy(target, destination, true);
    }

    private static async Task<string> HashFileAsync(string path, CancellationToken ct)
    {
        await using var input = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(input, ct).ConfigureAwait(false)).ToLowerInvariant();
    }

    private static string StatePath(string root, string packId) => Path.Combine(root, ".halerith", "modpacks", $"{packId}.json");
    private static ModpackState? ReadState(string path)
    {
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length > 16 * 1024 * 1024) return null;
            var state = JsonSerializer.Deserialize<ModpackState>(File.ReadAllText(path), PackJsonOptions);
            return state?.Files is { Count: <= 20000 } ? state : null;
        }
        catch { return null; }
    }
    private static void WriteState(string path, ModpackState state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(state, PackJsonOptions));
            VerifiedContentCommit.Commit(temporary, path, path, null);
        }
        finally { TryDelete(temporary); }
    }
    private static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }

    private sealed record ModpackState(string RevisionId, string ArchiveSha256, Dictionary<string, string> Files,
        string Publication = "", string VersionName = "", Dictionary<string, string>? Choices = null,
        Dictionary<string, string>? FileRevisions = null);
}
