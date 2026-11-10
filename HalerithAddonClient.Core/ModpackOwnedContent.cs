namespace HalerithAddonClient.Core;

public sealed record BundledContentFile(string PackId, string PackName, string Revision,
    string InstalledHash, string FullPath, string ContentType, bool Exists);

public static class ServerContentPlanner
{
    public static SyncPlan Build(ManifestResponse manifest, VerifiedServerContentInventory inventory, RuntimeConfig config)
    {
        var pack = manifest.Modpack;
        var separate = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        void Add(string folder, string filename)
        {
            if (pack is not null && !string.IsNullOrWhiteSpace(folder) && !string.IsNullOrWhiteSpace(filename))
                separate.Add(SafeFileHelpers.CombineUnderDirectory(folder, SafeFileHelpers.RequireSafeFileName(filename, "Content filename")));
        }
        void AddItem(string folder, string filename, IEnumerable<AlternateVersionInfo> alternates, IEnumerable<ModAlias> aliases)
        {
            Add(folder, filename);
            foreach (var alt in alternates) Add(folder, alt.Filename);
            foreach (var alias in aliases.Where(a => a.Type.Equals("filename", StringComparison.OrdinalIgnoreCase))) Add(folder, alias.Value);
        }
        foreach (var item in manifest.Mods) AddItem(config.ModsFolder, item.DownloadFilename, item.AlternateVersions, item.Aliases);
        foreach (var item in manifest.ResourcePacks) AddItem(config.ResourcePacksFolder, item.DownloadFilename, item.AlternateVersions, item.Aliases);
        foreach (var item in manifest.Shaders) AddItem(config.ShadersFolder, item.DownloadFilename, item.AlternateVersions, item.Aliases);
        foreach (var pin in config.LocalPinnedItems)
            Add(pin.ContentType == ContentTypes.ResourcePack ? config.ResourcePacksFolder : pin.ContentType == ContentTypes.Shader ? config.ShadersFolder : config.ModsFolder, pin.FileName);
        pack = pack?.WithSeparatePaths(separate);
        var owned = pack is null ? [] : ModpackOverlayService.BundledContent(pack, config.GameDataPath);
        var ownedPaths = owned.Select(f => f.FullPath).ToHashSet(separate.Comparer);
        List<LocalModFile> Ordinary(List<LocalModFile> files) => files.Where(f => !ownedPaths.Contains(Path.GetFullPath(f.FullPath))).ToList();

        // Build the ordinary actions without the pack, then add the correctly scoped pack action.
        var ordinary = new ManifestResponse { Mods = manifest.Mods, ResourcePacks = manifest.ResourcePacks, Shaders = manifest.Shaders };
        var plan = SyncPlanner.BuildPlan(ordinary, Ordinary(inventory.Mods), config.IgnoreRules, config.LocalPinnedItems);
        if (!string.IsNullOrWhiteSpace(config.ResourcePacksFolder))
            plan.Actions.AddRange(SyncPlanner.BuildResourcePackPlan(ordinary, Ordinary(inventory.ResourcePacks), config.IgnoreRules, config.LocalPinnedItems).Actions);
        if (!string.IsNullOrWhiteSpace(config.ShadersFolder))
            plan.Actions.AddRange(SyncPlanner.BuildShaderPlan(ordinary, Ordinary(inventory.Shaders), config.IgnoreRules, config.LocalPinnedItems).Actions);
        if (pack is not null)
            plan.Actions.InsertRange(0, SyncPlanner.BuildPlan(new() { Modpack = pack }, [], [], [], config.GameDataPath, config.ModpackFilePolicy).Actions);
        foreach (var file in owned)
            plan.Actions.Add(new SyncAction
            {
                Kind = SyncActionKind.UpToDate, BundledFile = file, ContentType = file.ContentType,
                LocalFile = new() { FullPath = file.FullPath, FileName = Path.GetFileName(file.FullPath), Stem = Path.GetFileNameWithoutExtension(file.FullPath) },
                Reason = $"Included in {file.PackName}; updated through the modpack." + (file.Exists ? "" : " Missing; use the modpack action to apply it.")
            });
        return plan;
    }
}

public sealed partial class ModpackOverlayService
{
    // Read-only projection: also recognises current files after an interrupted apply.
    public static List<BundledContentFile> BundledContent(ManifestModpack pack, string root)
    {
        Validate(pack);
        root = Path.GetFullPath(root);
        var statePath = StatePath(root, pack.Id);
        AssertNoReparsePoint(root, statePath);
        var state = ReadState(statePath);
        var paths = pack.Files.Select(f => f.Path).Concat(state?.Files.Keys ?? Enumerable.Empty<string>()).Distinct(StringComparer.OrdinalIgnoreCase);
        var result = new List<BundledContentFile>();
        foreach (var path in paths)
        {
            var relative = NormalizeRelative(path);
            if (relative.Split('/').Any(ProtectedNames.Contains)) throw new InvalidOperationException("A tracked modpack path is protected.");
            var full = CombineUnderRoot(root, relative);
            AssertNoReparsePoint(root, full);
            if (pack.SeparatelyManagedPaths.Contains(full)) continue;
            var parts = relative.Split('/');
            if (parts.Length != 2) continue;
            var type = parts[0].ToLowerInvariant() switch
            {
                "mods" when relative.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) => ContentTypes.Mod,
                "resourcepacks" when relative.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) => ContentTypes.ResourcePack,
                "shaderpacks" when relative.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) => ContentTypes.Shader,
                _ => null
            };
            if (type is null) continue;
            var hash = state?.Files.GetValueOrDefault(relative) ?? "";
            var revision = state?.FileRevisions?.GetValueOrDefault(relative) ?? (hash.Length > 0 ? state?.RevisionId : "") ?? "";
            result.Add(new(pack.Id, pack.Name, revision, hash, full, type, File.Exists(full)));
        }
        return result;
    }
}
