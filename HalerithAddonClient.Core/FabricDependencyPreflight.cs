using System.IO.Compression;
using System.Text.Json;

namespace HalerithAddonClient.Core;

/// <summary>Read-only, bounded missing-ID check, not a replacement for Fabric's version resolver.</summary>
public static class FabricDependencyPreflight
{
    public static IReadOnlyList<string> Missing(string modsFolder, string gameFolder)
    {
        if (!Directory.Exists(modsFolder)) return [];
        var metadata = new List<JsonElement>();
        var roots = new List<JsonElement>();
        long nestedBytes = 0;
        var complete = true;
        void ReadArchive(ZipArchive archive, int depth)
        {
            if (depth > 8 || metadata.Count > 5000) { complete = false; return; }
            var entry = archive.GetEntry("fabric.mod.json");
            if (entry is null) return;
            if (entry.Length > 1024 * 1024) { complete = false; return; }
            using var stream = entry.Open();
            using var json = JsonDocument.Parse(stream);
            var mod = json.RootElement;
            if (mod.ValueKind != JsonValueKind.Object || !mod.TryGetProperty("id", out _)) { complete = false; return; }
            if (mod.TryGetProperty("environment", out var env) && env.ValueKind == JsonValueKind.String && env.GetString() == "server") return;
            metadata.Add(mod.Clone());
            if (depth == 0) roots.Add(mod.Clone());
            if (!mod.TryGetProperty("jars", out var jars) || jars.ValueKind != JsonValueKind.Array) return;
            foreach (var child in jars.EnumerateArray())
            {
                if (!child.TryGetProperty("file", out var filename) || filename.ValueKind != JsonValueKind.String) { complete = false; continue; }
                var nested = archive.GetEntry(filename.GetString()!);
                if (nested is null || nested.Length > 32 * 1024 * 1024 || (nestedBytes += nested.Length) > 128 * 1024 * 1024) { complete = false; continue; }
                using var buffer = new MemoryStream();
                using (var input = nested.Open()) input.CopyTo(buffer);
                buffer.Position = 0;
                using var nestedArchive = new ZipArchive(buffer, ZipArchiveMode.Read);
                ReadArchive(nestedArchive, depth + 1);
            }
        }
        try
        {
            var paths = Directory.EnumerateFiles(modsFolder, "*.jar").Take(5001).ToArray();
            if (paths.Length > 5000) return [];
            foreach (var path in paths)
            {
                using var archive = ZipFile.OpenRead(path);
                ReadArchive(archive, 0);
            }
            // An incomplete inventory cannot prove that a dependency is absent.
            if (!complete) return [];
            // Different candidates for one ID require Fabric's full resolver;
            // do not union their dependencies and invent a missing requirement.
            if (metadata.GroupBy(m => m.GetProperty("id").GetString()).Any(group => group.Count() > 1)) return [];
            var available = new HashSet<string>(StringComparer.Ordinal) { "minecraft", "java", "fabricloader" };
            foreach (var mod in metadata)
            {
                available.Add(mod.GetProperty("id").GetString()!);
                if (mod.TryGetProperty("provides", out var aliases) && aliases.ValueKind == JsonValueKind.Array)
                    foreach (var alias in aliases.EnumerateArray()) available.Add(alias.GetString()!);
            }
            var overrides = default(JsonElement);
            var overridePath = Path.Combine(gameFolder, "config", "fabric_loader_dependencies.json");
            if (File.Exists(overridePath))
            {
                if (new FileInfo(overridePath).Length > 1024 * 1024) return [];
                using var json = JsonDocument.Parse(File.ReadAllText(overridePath));
                if (json.RootElement.TryGetProperty("overrides", out var value)) overrides = value.Clone();
            }
            var missing = new HashSet<string>(StringComparer.Ordinal);
            foreach (var mod in roots)
            {
                var id = mod.GetProperty("id").GetString()!;
                var required = new HashSet<string>(StringComparer.Ordinal);
                void Add(JsonElement source) { if (source.ValueKind == JsonValueKind.Object) foreach (var dependency in source.EnumerateObject()) required.Add(dependency.Name); }
                if (mod.TryGetProperty("depends", out var depends)) Add(depends);
                if (overrides.ValueKind == JsonValueKind.Object && overrides.TryGetProperty(id, out var changes))
                {
                    if (changes.TryGetProperty("depends", out var replacement)) { required.Clear(); Add(replacement); }
                    if (changes.TryGetProperty("+depends", out var added)) Add(added);
                    if (changes.TryGetProperty("-depends", out var removed) && removed.ValueKind == JsonValueKind.Object)
                        foreach (var dependency in removed.EnumerateObject()) required.Remove(dependency.Name);
                }
                foreach (var dependency in required.Where(dep => !available.Contains(dep)))
                    missing.Add($"{id} requires {dependency}");
            }
            return missing.Order(StringComparer.Ordinal).Take(20).ToArray();
        }
        catch (Exception exception) when (exception is IOException or JsonException or InvalidOperationException or InvalidDataException)
        {
            // Unknown metadata is left to the loader, not interpreted as an
            // empty dependency inventory or permission to download extra code.
            return [];
        }
    }
}
