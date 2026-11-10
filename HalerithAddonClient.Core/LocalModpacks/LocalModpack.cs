using System.Text.Json;
using System.Text.Json.Serialization;
using HalerithAddonClient.Core.ContentSearch;

namespace HalerithAddonClient.Core.LocalModpacks;

public sealed class LocalModpack
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Provider { get; set; } = "";
    public string ProjectId { get; set; } = "";
    public string? IconUrl { get; set; }
    public string VersionId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Version { get; set; } = "";
    public string MinecraftVersion { get; set; } = "";
    public string Loader { get; set; } = "";
    public string LoaderVersion { get; set; } = "";
    public string ArchivePath { get; set; } = "";
    public string ArchiveHash { get; set; } = "";
    public string Policy { get; set; } = ModpackFilePolicies.Preserve;
    public DateTime? AppliedAtUtc { get; set; }
    public List<LocalPackMember> Members { get; set; } = [];
    public List<ManifestModpackFile> BundledFiles { get; set; } = [];
    public List<string> Warnings { get; set; } = [];
    public bool Failed { get; set; }
    public List<string> RetiredPaths { get; set; } = [];
    public Dictionary<string, string> RetiredReplacementPaths { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> KeptFiles { get; set; } = [];
}

public sealed class LocalPackMember
{
    [JsonIgnore] public bool IsAddon => Path.Length == 0 || LocalPackInspector.IsAddonPath(Path);
    public string Path { get; set; } = "";
    public ContentSearchResult Project { get; set; } = new();
    public ContentVersionResult Version { get; set; } = new();
    public bool Optional { get; set; }
    public bool Enabled { get; set; } = true;
    public bool Overridden { get; set; }
    public string? OverridePath { get; set; }
    public bool Missing { get; set; }
}

public static class LocalModpackStore
{
    public static string StatePath(string root) => SafePath(root, ".halerith/local-pack.json", allowMetadata: true);
    public static LocalModpack? Load(string root)
    {
        var path = StatePath(root);
        if (!File.Exists(path)) return null;
        if (new FileInfo(path).Length > 16 * 1024 * 1024) throw new InvalidDataException("Local pack record is too large.");
        return JsonSerializer.Deserialize<LocalModpack>(File.ReadAllText(path), SerializerOptions.Default)
            ?? throw new InvalidDataException("Local pack record is invalid.");
    }
    public static void Save(string root, LocalModpack pack)
    {
        var path = StatePath(root);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temporary, JsonSerializer.Serialize(pack, SerializerOptions.Default)); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public static string Relative(string path, bool allowMetadata = false)
    {
        var value = path.Replace('\\', '/');
        var parts = value.Split('/');
        if (Path.IsPathRooted(value) || value.Contains(':') || parts.Any(p => p is "" or "." or ".." || p.EndsWith('.') || p.EndsWith(' '))
            || value.Any(c => c < 32 || c == 127) || parts.Any(p => p.IndexOfAny(['*', '?', '"', '<', '>', '|']) >= 0)
            || (!allowMetadata && parts.Any(p => p.Equals(".halerith", StringComparison.OrdinalIgnoreCase)))
            || parts.Any(p => new[] { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" }.Contains(p.Split('.')[0], StringComparer.OrdinalIgnoreCase))
            || (!allowMetadata && parts.Any(p => new[] { ".env", "credentials", "secrets", "private.key", "id_rsa", "accounts.json", "launcher_accounts.json" }.Contains(p, StringComparer.OrdinalIgnoreCase)))
            || (!allowMetadata && parts[0].Equals("saves", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("Unsafe or protected pack path: " + path);
        return value;
    }
    public static string SafePath(string root, string path, bool allowMetadata = false)
    {
        root = Path.GetFullPath(root);
        var target = Path.GetFullPath(Path.Combine(root, Relative(path, allowMetadata).Replace('/', Path.DirectorySeparatorChar)));
        if (!target.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) throw new InvalidDataException("Path escapes the instance.");
        for (string? current = target; current is not null; current = Path.GetDirectoryName(current))
        {
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Pack path crosses a linked file or directory.");
            if (current == root) break;
        }
        return target;
    }
}
