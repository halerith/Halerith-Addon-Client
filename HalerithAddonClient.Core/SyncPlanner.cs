using System.Text.RegularExpressions;

namespace HalerithAddonClient.Core;

public static class SyncPlanner
{
    public static SyncPlan BuildPlan(
        ManifestResponse manifest,
        IReadOnlyList<LocalModFile> localFiles,
        IReadOnlyCollection<IgnoreRule> ignoreRules,
        IReadOnlyCollection<LocalPinnedItem> localPinnedItems,
        string? gameDataPath = null,
        string modpackFilePolicy = ModpackFilePolicies.Preserve)
    {
        var plan = new SyncPlan();
        var matchedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Filter to mod-only rules for mods sync; resource pack / shader sync is handled separately.
        var modIgnorePatterns = ignoreRules
            .Where(r => string.Equals(r.ContentType, ContentTypes.Mod, StringComparison.OrdinalIgnoreCase))
            .Select(r => r.Pattern)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var modPinnedItems = localPinnedItems
            .Where(x => string.Equals(x.ContentType, ContentTypes.Mod, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var pinnedByFileName = modPinnedItems
            .Where(x => !string.IsNullOrWhiteSpace(x.FileName))
            .GroupBy(x => x.FileName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var pinnedBySlug = modPinnedItems
            .Where(x => !string.IsNullOrWhiteSpace(x.Slug))
            .GroupBy(x => x.Slug, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        foreach (var mod in manifest.Mods)
        {
            if (mod.Unavailable)
            {
                var retained = localFiles.Where(f => MatchesUnavailable(f, mod.VersionSource, mod.SourceId, mod.Slug, mod.DownloadFilename)).ToList();
                foreach (var file in retained) matchedPaths.Add(file.FullPath);
                var exact = retained.FirstOrDefault(f => HashMatchesFile(f.FullPath, mod.DownloadHash, out _));
                plan.Actions.Add(new SyncAction { Kind = exact is null ? SyncActionKind.Missing : SyncActionKind.UpToDate, ManifestMod = mod,
                    LocalFile = exact ?? retained.FirstOrDefault(),
                    InstalledVersion = exact is not null ? GetDisplayVersion(mod)
                        : retained.FirstOrDefault() is { } oldFile ? GetInstalledVersion(mod, oldFile, fileHash: null) : "",
                    TargetVersion = GetDisplayVersion(mod),
                    Reason = mod.ManualDownloadAllowed
                        ? "The exact CurseForge file requires manual installation. Any existing local copy is kept."
                        : "The pack's requested file is unavailable. Any existing local copy is kept; build and launch are allowed." });
                continue;
            }
            if (!string.IsNullOrWhiteSpace(mod.Slug) &&
                pinnedBySlug.TryGetValue(mod.Slug, out var pinnedMod))
            {
                var pinnedLocal = localFiles.FirstOrDefault(x =>
                    string.Equals(x.FileName, pinnedMod.FileName, StringComparison.OrdinalIgnoreCase));

                if (pinnedLocal is not null)
                {
                    matchedPaths.Add(pinnedLocal.FullPath);
                plan.Actions.Add(new SyncAction
                {
                    Kind = SyncActionKind.Ignore,
                    ManifestMod = mod,
                    LocalFile = pinnedLocal,
                    IsCustom = mod.IsCustom,
                    MatchConfidence = SyncMatchConfidence.High,
                    Reason = $"Pinned local mod override for slug '{mod.Slug}'."
                });
                    continue;
                }

                plan.Actions.Add(new SyncAction
                {
                    Kind = SyncActionKind.Ignore,
                    ManifestMod = mod,
                    IsCustom = mod.IsCustom,
                    MatchConfidence = SyncMatchConfidence.High,
                    Reason = $"Pinned local mod override for slug '{mod.Slug}' is configured but local file '{pinnedMod.FileName}' was not found."
                });
                continue;
            }

            var best = localFiles
                .Where(x => !matchedPaths.Contains(x.FullPath))
                .Select(x => new { File = x, Score = Matching.Score(mod, x) })
                .Where(x => x.Score > 0)
                .OrderByDescending(x => x.Score)
                .ThenBy(x => x.File.FileName, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();

            if (best is null)
            {
                plan.Actions.Add(new SyncAction
                {
                    Kind = SyncActionKind.Download,
                    ManifestMod = mod,
                    IsCustom = mod.IsCustom,
                    TargetVersion = GetDisplayVersion(mod),
                    MatchConfidence = SyncMatchConfidence.None,
                    Reason = "Mod not found locally."
                });
                continue;
            }

            matchedPaths.Add(best.File.FullPath);

            var exactFile = string.Equals(best.File.FileName, mod.DownloadFilename, StringComparison.OrdinalIgnoreCase);
            if (exactFile && mod.PackManaged)
                exactFile = HashMatchesFile(best.File.FullPath, mod.DownloadHash, out _);

            plan.Actions.Add(new SyncAction
            {
                Kind = exactFile ? SyncActionKind.UpToDate : SyncActionKind.Replace,
                ManifestMod = mod,
                LocalFile = best.File,
                IsCustom = mod.IsCustom,
                MatchConfidence = best.Score >= 900 ? SyncMatchConfidence.High : SyncMatchConfidence.Medium,
                InstalledVersion = exactFile
                    ? GetDisplayVersion(mod)
                    : GetInstalledVersion(mod, best.File, fileHash: null),
                TargetVersion = exactFile ? "" : GetDisplayVersion(mod),
                Reason = exactFile
                    ? "Expected file is installed."
                    : $"Replace {best.File.FileName} with {mod.DownloadFilename}."
            });
        }

        foreach (var local in localFiles.Where(x => !matchedPaths.Contains(x.FullPath)))
        {
            var ignoredByFile = modIgnorePatterns.Contains(local.FileName) ||
                                modIgnorePatterns.Contains(local.Stem);

            if (pinnedByFileName.TryGetValue(local.FileName, out var pinned))
            {
                plan.Actions.Add(new SyncAction
                {
                    Kind = SyncActionKind.Ignore,
                    LocalFile = local,
                    MatchConfidence = SyncMatchConfidence.High,
                    Reason = $"Pinned local mod override for slug '{pinned.Slug}'."
                });
                continue;
            }

            plan.Actions.Add(new SyncAction
            {
                Kind = ignoredByFile ? SyncActionKind.Ignore : SyncActionKind.Remove,
                LocalFile = local,
                MatchConfidence = ignoredByFile ? SyncMatchConfidence.High : SyncMatchConfidence.None,
                Reason = ignoredByFile ? "On ignore list." : "Not present in manifest."
            });
        }

        if (manifest.Modpack is { } modpack)
        {
            var review = string.IsNullOrWhiteSpace(gameDataPath) ? new ModpackFileReview
                { RevisionChanged = true, Policy = modpack.ForceConfig ? ModpackFilePolicies.Replace : ModpackFilePolicies.Normalize(modpackFilePolicy) }
                : ModpackOverlayService.Review(modpack, gameDataPath, modpackFilePolicy);
            var ignored = review.Policy == ModpackFilePolicies.Ignore;
            var failed = !ignored && !string.IsNullOrWhiteSpace(gameDataPath) && review.HasWork
                && ModpackOverlayService.LastApplyFailed(modpack, gameDataPath);
            var status = ignored ? "Ignored" : failed ? "Failed" : review.RequiresReview ? "Pending"
                : !review.HasWork ? "Current" : string.IsNullOrWhiteSpace(review.InstalledVersion) ? "Download"
                : review.InstalledVersion == modpack.VersionName ? "Reapply" : "Update";
            var detail = status switch
            {
                "Ignored" => "Pack files are disabled by your server settings.",
                "Failed" => "The last pack application failed. Sync again to retry; file review may be required.",
                "Pending" => "Review and approve the pack file changes before applying them.",
                "Download" => "Apply the pack files for the first time. A verified cached archive will be reused when available.",
                "Reapply" => "Reapply this pack version or repair missing pack files.",
                "Update" => "Apply the server's selected pack version.",
                _ => review.HasLocalChoices ? "Pack files are current with your local file choices preserved." : "Pack files are current."
            };
            plan.Actions.Insert(0, new SyncAction
            {
                Kind = ignored ? SyncActionKind.Ignore : review.HasWork ? SyncActionKind.Download : SyncActionKind.UpToDate,
                ContentType = "modpack",
                Modpack = modpack,
                CanReviewModpack = review.Policy == ModpackFilePolicies.Review,
                RequiresModpackReview = review.RequiresReview,
                InstalledVersion = review.InstalledVersion,
                TargetVersion = modpack.VersionName,
                ModpackStatus = status,
                Reason = detail + (modpack.ForceConfig ? " Pack files are required by the server." : "")
            });
        }

        return plan;
    }

    public static SyncPlan BuildResourcePackPlan(
        ManifestResponse manifest,
        string localFolder,
        IReadOnlyCollection<IgnoreRule> ignoreRules,
        IReadOnlyCollection<LocalPinnedItem> localPinnedItems)
    {
        return BuildPackPlanCore(
            manifest.ResourcePacks.Select(PackManifestItem.From).ToList(),
            localFolder,
            "*.zip",
            ContentTypes.ResourcePack,
            ignoreRules,
            localPinnedItems);
    }

    public static SyncPlan BuildResourcePackPlan(
        ManifestResponse manifest,
        IReadOnlyList<LocalModFile> localFiles,
        IReadOnlyCollection<IgnoreRule> ignoreRules,
        IReadOnlyCollection<LocalPinnedItem> localPinnedItems)
    {
        return BuildPackPlanCore(
            manifest.ResourcePacks.Select(PackManifestItem.From).ToList(),
            localFiles,
            ContentTypes.ResourcePack,
            ignoreRules,
            localPinnedItems);
    }

    public static SyncPlan BuildShaderPlan(
        ManifestResponse manifest,
        string localFolder,
        IReadOnlyCollection<IgnoreRule> ignoreRules,
        IReadOnlyCollection<LocalPinnedItem> localPinnedItems)
    {
        return BuildPackPlanCore(
            manifest.Shaders.Select(PackManifestItem.From).ToList(),
            localFolder,
            "*.zip",
            ContentTypes.Shader,
            ignoreRules,
            localPinnedItems);
    }

    public static SyncPlan BuildShaderPlan(
        ManifestResponse manifest,
        IReadOnlyList<LocalModFile> localFiles,
        IReadOnlyCollection<IgnoreRule> ignoreRules,
        IReadOnlyCollection<LocalPinnedItem> localPinnedItems)
    {
        return BuildPackPlanCore(
            manifest.Shaders.Select(PackManifestItem.From).ToList(),
            localFiles,
            ContentTypes.Shader,
            ignoreRules,
            localPinnedItems);
    }

    private static SyncPlan BuildPackPlanCore(
        IReadOnlyList<PackManifestItem> serverItems,
        string localFolder,
        string searchPattern,
        string contentType,
        IReadOnlyCollection<IgnoreRule> ignoreRules,
        IReadOnlyCollection<LocalPinnedItem> localPinnedItems)
    {
        var plan = new SyncPlan();

        if (!Directory.Exists(localFolder))
        {
            // Folder not yet created — list all server items as missing/no-version
            foreach (var item in serverItems)
            {
                plan.Actions.Add(new SyncAction
                {
                    Kind = string.IsNullOrWhiteSpace(item.DownloadUrl) ? SyncActionKind.Ignore : SyncActionKind.Download,
                    ContentType = contentType,
                    PackManifestItem = item,
                    IsCustom = item.IsCustom,
                    TargetVersion = string.IsNullOrWhiteSpace(item.DownloadUrl) ? "" : GetDisplayVersion(item),
                    MatchConfidence = SyncMatchConfidence.None,
                    Reason = string.IsNullOrWhiteSpace(item.DownloadUrl)
                        ? "No compatible version found in manifest."
                        : "Local folder does not exist yet."
                });
            }
            return plan;
        }

        var localFiles = Directory.GetFiles(localFolder, searchPattern, SearchOption.TopDirectoryOnly)
            .Select(f => new LocalModFile
            {
                FileName = Path.GetFileName(f),
                FullPath = f,
                Stem = Path.GetFileNameWithoutExtension(f)
            })
            .ToList();

        return BuildPackPlanCore(serverItems, localFiles, contentType, ignoreRules, localPinnedItems);
    }

    private static SyncPlan BuildPackPlanCore(
        IReadOnlyList<PackManifestItem> serverItems,
        IReadOnlyList<LocalModFile> localFiles,
        string contentType,
        IReadOnlyCollection<IgnoreRule> ignoreRules,
        IReadOnlyCollection<LocalPinnedItem> localPinnedItems)
    {
        if (serverItems.Any(i => i.Unavailable))
        {
            var missing = serverItems.Where(i => i.Unavailable).ToList();
            bool Retain(LocalModFile f) => missing.Any(i => MatchesUnavailable(f, i.VersionSource, i.SourceId, i.Slug, i.DownloadFilename));
            var availablePlan = BuildPackPlanCore(serverItems.Where(i => !i.Unavailable).ToList(),
                localFiles.Where(f => !Retain(f)).ToList(), contentType, ignoreRules, localPinnedItems);
            foreach (var item in missing)
            {
                var retained = localFiles.Where(f => MatchesUnavailable(f, item.VersionSource, item.SourceId, item.Slug, item.DownloadFilename)).ToList();
                var exact = retained.FirstOrDefault(f => HashMatchesFile(f.FullPath, item.DownloadHash, out _));
                availablePlan.Actions.Add(new SyncAction { Kind = exact is null ? SyncActionKind.Missing : SyncActionKind.UpToDate, ContentType = contentType,
                    PackManifestItem = item, LocalFile = exact ?? retained.FirstOrDefault(),
                    InstalledVersion = exact is not null ? GetDisplayVersion(item)
                        : retained.FirstOrDefault() is { } oldFile ? GetInstalledVersion(item, oldFile, fileHash: null) : "",
                    TargetVersion = GetDisplayVersion(item),
                    Reason = item.ManualDownloadAllowed
                        ? "The exact CurseForge file requires manual installation. Any existing local copy is kept."
                        : "The pack's requested file is unavailable. Any existing local copy is kept; build and launch are allowed." });
            }
            return availablePlan;
        }
        var plan = new SyncPlan();

        // Ignore patterns for this content type
        var ignorePatterns = ignoreRules
            .Where(r => string.Equals(r.ContentType, contentType, StringComparison.OrdinalIgnoreCase))
            .Select(r => r.Pattern)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Pinned items by slug
        var pinnedBySlug = localPinnedItems
            .Where(x => string.Equals(x.ContentType, contentType, StringComparison.OrdinalIgnoreCase)
                     && !string.IsNullOrWhiteSpace(x.Slug))
            .GroupBy(x => x.Slug, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        // ── Classify items: ambiguous-filename vs distinguishable ────────────
        // An item has "ambiguous filenames" when every known version (active +
        // alternates) shares the same filename — e.g. "Fast Better Grass.zip".
        // For these we must use hash comparison.  For items with distinct
        // filenames across versions we can rely on filename matching first.
        var ambiguousItems = new HashSet<PackManifestItem>(ReferenceEqualityComparer.Instance);
        foreach (var item in serverItems)
        {
            if (item.AlternateVersions.Count == 0)
                continue; // no alternates — filename matching is fine

            var allSame = item.AlternateVersions.All(alt =>
                string.Equals(alt.Filename, item.DownloadFilename, StringComparison.OrdinalIgnoreCase));
            if (allSame)
                ambiguousItems.Add(item);
        }

        // Build reverse lookup: filename → list of server items that claim that exact filename.
        // Only DownloadFilename and 'filename' type aliases are keyed here — exact matches only.
        // Prefix and regex aliases are evaluated separately by FindPatternMatch() below.
        // Alternate-version filenames are only registered for non-ambiguous items
        // (ambiguous items all share one filename — adding alternates would be redundant).
        var filenameToPacks = new Dictionary<string, List<PackManifestItem>>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in serverItems)
        {
            // The active filename, alternates and aliases can repeat a name for
            // this same item. Only different items constitute an ambiguous match.
            var filenames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrWhiteSpace(item.DownloadFilename))
                filenames.Add(item.DownloadFilename);

            if (!ambiguousItems.Contains(item))
            {
                foreach (var alt in item.AlternateVersions)
                    if (!string.IsNullOrWhiteSpace(alt.Filename))
                        filenames.Add(alt.Filename);
            }

            foreach (var alias in item.Aliases)
                if (alias.Type.Equals("filename", StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrWhiteSpace(alias.Value))
                    filenames.Add(alias.Value);

            foreach (var filename in filenames)
                GetOrAdd(filenameToPacks, filename).Add(item);
        }

        // Build hash → server item lookup for ALL items.
        // Includes the active version hash and all alternate version hashes.
        // Used for ambiguous-filename items (hash-first) and as a last-resort
        // fallback when filename matching fails for distinguishable items.
        var hashToPacks = new Dictionary<string, Dictionary<string, PackManifestItem>>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in serverItems)
        {
            AddHashLookup(hashToPacks, item.DownloadHash, item);

            foreach (var alt in item.AlternateVersions)
                AddHashLookup(hashToPacks, alt.FileHash, item);
        }

        var matchedLocalPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Track by reference, not by slug — slugs can be empty for custom packs, causing
        // multiple distinct items to collide in a string-keyed set and be silently dropped.
        var matchedServerItems = new HashSet<PackManifestItem>(ReferenceEqualityComparer.Instance);

        // Step 1: Handle pinned items first (by slug)
        foreach (var serverItem in serverItems)
        {
            if (!pinnedBySlug.TryGetValue(serverItem.Slug, out var pinned))
                continue;

            matchedServerItems.Add(serverItem);
            var pinnedLocal = localFiles.FirstOrDefault(x =>
                string.Equals(x.FileName, pinned.FileName, StringComparison.OrdinalIgnoreCase));

            if (pinnedLocal != null)
            {
                matchedLocalPaths.Add(pinnedLocal.FullPath);
                plan.Actions.Add(new SyncAction
                {
                    Kind = SyncActionKind.Ignore,
                    ContentType = contentType,
                    PackManifestItem = serverItem,
                    LocalFile = pinnedLocal,
                    IsCustom = serverItem.IsCustom,
                    MatchConfidence = SyncMatchConfidence.High,
                    Reason = $"Pinned local override for '{serverItem.Slug}'."
                });
            }
            else
            {
                plan.Actions.Add(new SyncAction
                {
                    Kind = SyncActionKind.Ignore,
                    ContentType = contentType,
                    PackManifestItem = serverItem,
                    IsCustom = serverItem.IsCustom,
                    MatchConfidence = SyncMatchConfidence.High,
                    Reason = $"Pinned override for '{serverItem.Slug}' configured but file '{pinned.FileName}' not found."
                });
            }
        }

        // Step 2: SQLite identity pass for rows that were previously tracked.
        foreach (var local in localFiles)
        {
            if (matchedLocalPaths.Contains(local.FullPath))
                continue;

            var identityMatches = serverItems
                .Where(item => PackIdentityMatches(item, local))
                .ToList();

            if (identityMatches.Count == 0)
                continue;

            if (identityMatches.Count > 1)
            {
                plan.Actions.Add(new SyncAction
                {
                    Kind = SyncActionKind.Ignore,
                    ContentType = contentType,
                    LocalFile = local,
                    MatchConfidence = SyncMatchConfidence.Low,
                    Reason = "Ambiguous: multiple server items match stored SQLite identity. Skipping."
                });
                matchedLocalPaths.Add(local.FullPath);
                continue;
            }

            var serverPack = identityMatches[0];
            matchedLocalPaths.Add(local.FullPath);
            matchedServerItems.Add(serverPack);
            plan.Actions.Add(CreatePackIdentityAction(contentType, serverPack, local));
        }

        // Step 3: Hash-first pass for ambiguous items.
        // For items whose versions all share the same filename, scan local
        // files by hash to determine if any known version is on disk.
        if (ambiguousItems.Count > 0 && hashToPacks.Count > 0)
        {
            foreach (var local in localFiles)
            {
                if (matchedLocalPaths.Contains(local.FullPath))
                    continue;

                // Only consider local files whose filename matches at least one
                // ambiguous item's download filename (avoids hashing unrelated files).
                if (!filenameToPacks.TryGetValue(local.FileName, out var possiblePacks))
                    continue;
                if (!possiblePacks.Any(p => ambiguousItems.Contains(p)))
                    continue;

                if (!TryFindHashMatch(hashToPacks, local.FullPath, out var ownerItem, out var localHash))
                    continue; // hash doesn't match any known version — handled later

                matchedLocalPaths.Add(local.FullPath);
                matchedServerItems.Add(ownerItem);

                var isActiveHash = HashMatchesFile(local.FullPath, ownerItem.DownloadHash, out _);

                plan.Actions.Add(new SyncAction
                {
                    Kind = isActiveHash ? SyncActionKind.UpToDate : SyncActionKind.Replace,
                    ContentType = contentType,
                    PackManifestItem = ownerItem,
                    LocalFile = local,
                    IsCustom = ownerItem.IsCustom,
                    MatchConfidence = SyncMatchConfidence.High,
                    InstalledVersion = isActiveHash
                        ? GetDisplayVersion(ownerItem)
                        : GetInstalledVersion(ownerItem, local, localHash),
                    TargetVersion = isActiveHash ? "" : GetDisplayVersion(ownerItem),
                    Reason = isActiveHash
                        ? "Hash matches active version."
                        : $"Hash matches an older version — replace with current version."
                });
            }
        }

        // Step 4: Filename-based matching for remaining local files
        foreach (var local in localFiles)
        {
            if (matchedLocalPaths.Contains(local.FullPath))
                continue;

            var isIgnored = ignorePatterns.Contains(local.FileName) || ignorePatterns.Contains(local.Stem);

            if (!filenameToPacks.TryGetValue(local.FileName, out var matchingPacks) || matchingPacks.Count == 0)
            {
                // Exact filename not found — try filename_prefix and regex aliases.
                var (patternMatch, patternAmbiguous) = FindPatternMatch(serverItems, local.FileName);

                if (patternAmbiguous)
                {
                    plan.Actions.Add(new SyncAction
                    {
                        Kind = SyncActionKind.Ignore,
                        ContentType = contentType,
                        LocalFile = local,
                        MatchConfidence = SyncMatchConfidence.High,
                        Reason = "Ambiguous: multiple server items' pattern aliases match this file. Skipping."
                    });
                    matchedLocalPaths.Add(local.FullPath);
                    continue;
                }

                if (patternMatch == null)
                {
                    if (isIgnored)
                    {
                        plan.Actions.Add(new SyncAction
                        {
                        Kind = SyncActionKind.Ignore,
                        ContentType = contentType,
                        LocalFile = local,
                        MatchConfidence = SyncMatchConfidence.High,
                        Reason = "On ignore list."
                    });
                        matchedLocalPaths.Add(local.FullPath);
                        continue;
                    }

                    // Last resort: hash-based fallback.  The file didn't match
                    // any filename or alias, but its content might still match a
                    // known version (e.g. the upstream filename changed between
                    // releases while the file on disk is from an older release).
                    if (TryFindHashMatch(hashToPacks, local.FullPath, out var hashOwner, out var fallbackHash))
                    {
                        matchedLocalPaths.Add(local.FullPath);
                        matchedServerItems.Add(hashOwner);
                        var isActive = HashMatchesFile(local.FullPath, hashOwner.DownloadHash, out _);
                        plan.Actions.Add(new SyncAction
                        {
                            Kind = isActive ? SyncActionKind.UpToDate : SyncActionKind.Replace,
                            ContentType = contentType,
                            PackManifestItem = hashOwner,
                            LocalFile = local,
                            IsCustom = hashOwner.IsCustom,
                            MatchConfidence = SyncMatchConfidence.High,
                            InstalledVersion = isActive
                                ? GetDisplayVersion(hashOwner)
                                : GetInstalledVersion(hashOwner, local, fallbackHash),
                            TargetVersion = isActive ? "" : GetDisplayVersion(hashOwner),
                            Reason = isActive
                                ? $"Hash fallback: {local.FileName} matches active version of '{hashOwner.Name}'."
                                : $"Hash fallback: {local.FileName} matches older version of '{hashOwner.Name}' — replace with {hashOwner.DownloadFilename}."
                        });
                        continue;
                    }

                    var (identityMatch, identityAmbiguous) = FindMediumIdentityMatch(serverItems, local.FileName);
                    if (identityAmbiguous)
                    {
                        plan.Actions.Add(new SyncAction
                        {
                            Kind = SyncActionKind.Ignore,
                            ContentType = contentType,
                            LocalFile = local,
                            MatchConfidence = SyncMatchConfidence.Low,
                            Reason = "Ambiguous: multiple server items appear to match this filename. Skipping."
                        });
                        matchedLocalPaths.Add(local.FullPath);
                        continue;
                    }

                    if (identityMatch is not null)
                    {
                        matchedLocalPaths.Add(local.FullPath);
                        matchedServerItems.Add(identityMatch);
                        plan.Actions.Add(new SyncAction
                        {
                            Kind = SyncActionKind.Replace,
                            ContentType = contentType,
                            PackManifestItem = identityMatch,
                            LocalFile = local,
                            IsCustom = identityMatch.IsCustom,
                    MatchConfidence = SyncMatchConfidence.Medium,
                            InstalledVersion = InferVersionNameFromFilename(local),
                            TargetVersion = GetDisplayVersion(identityMatch),
                            Reason = $"Likely same item by name/slug: replace {local.FileName} with {identityMatch.DownloadFilename}."
                        });
                        continue;
                    }

                    // Truly unrecognised — treat as extra.
                    plan.Actions.Add(new SyncAction
                    {
                        Kind = SyncActionKind.Remove,
                        ContentType = contentType,
                        LocalFile = local,
                        MatchConfidence = SyncMatchConfidence.None,
                        Reason = "Not present in manifest."
                    });
                    matchedLocalPaths.Add(local.FullPath);
                    continue;
                }

                // Pattern alias matched — file belongs to this item but is not the current version.
                matchedLocalPaths.Add(local.FullPath);
                matchedServerItems.Add(patternMatch);
                plan.Actions.Add(new SyncAction
                {
                    Kind = SyncActionKind.Replace,
                    ContentType = contentType,
                    PackManifestItem = patternMatch,
                    LocalFile = local,
                    IsCustom = patternMatch.IsCustom,
                    MatchConfidence = SyncMatchConfidence.High,
                    TargetVersion = GetDisplayVersion(patternMatch),
                    Reason = $"Alias pattern match: replace {local.FileName} with {patternMatch.DownloadFilename}."
                });
                continue;
            }

            if (matchingPacks.Count > 1)
            {
                // Multiple server items share this filename — ambiguous, skip safely
                plan.Actions.Add(new SyncAction
                {
                    Kind = SyncActionKind.Ignore,
                    ContentType = contentType,
                    LocalFile = local,
                    MatchConfidence = SyncMatchConfidence.High,
                    Reason = $"Ambiguous: {matchingPacks.Count} server items share this filename. Skipping."
                });
                matchedLocalPaths.Add(local.FullPath);
                continue;
            }

            // Exactly one server item owns this filename
            var serverPack = matchingPacks[0];
            matchedLocalPaths.Add(local.FullPath);
            matchedServerItems.Add(serverPack);

            var filenameMatches = string.Equals(local.FileName, serverPack.DownloadFilename, StringComparison.OrdinalIgnoreCase);

            if (filenameMatches && HasUsableHash(serverPack.DownloadHash))
            {
                // Filename matches — verify with hash to catch static-filename packs
                var hashMatches = HashMatchesFile(local.FullPath, serverPack.DownloadHash, out var localHash);
                plan.Actions.Add(new SyncAction
                {
                    Kind = hashMatches ? SyncActionKind.UpToDate : SyncActionKind.Replace,
                    ContentType = contentType,
                    PackManifestItem = serverPack,
                    LocalFile = local,
                    IsCustom = serverPack.IsCustom,
                    MatchConfidence = SyncMatchConfidence.High,
                    InstalledVersion = hashMatches
                        ? GetDisplayVersion(serverPack)
                        : GetInstalledVersion(serverPack, local, localHash),
                    TargetVersion = hashMatches ? "" : GetDisplayVersion(serverPack),
                    Reason = hashMatches
                        ? "Filename and hash match."
                        : $"Filename matches but hash differs — {local.FileName} has a newer version available."
                });
            }
            else
            {
                // No hash available or filename is an alias — fall back to filename comparison
                plan.Actions.Add(new SyncAction
                {
                    Kind = filenameMatches ? SyncActionKind.UpToDate : SyncActionKind.Replace,
                    ContentType = contentType,
                    PackManifestItem = serverPack,
                    LocalFile = local,
                    IsCustom = serverPack.IsCustom,
                    MatchConfidence = SyncMatchConfidence.High,
                    InstalledVersion = filenameMatches
                        ? GetDisplayVersion(serverPack)
                        : GetInstalledVersion(serverPack, local, fileHash: null),
                    TargetVersion = filenameMatches ? "" : GetDisplayVersion(serverPack),
                    Reason = filenameMatches
                        ? "Exact filename match."
                        : $"Replace {local.FileName} with {serverPack.DownloadFilename}."
                });
            }
        }

        // Step 5: Server items with no local match at all
        foreach (var serverItem in serverItems.Where(x => !matchedServerItems.Contains(x)))
        {
            plan.Actions.Add(new SyncAction
            {
                Kind = string.IsNullOrWhiteSpace(serverItem.DownloadUrl) ? SyncActionKind.Ignore : SyncActionKind.Download,
                ContentType = contentType,
                PackManifestItem = serverItem,
                IsCustom = serverItem.IsCustom,
                TargetVersion = string.IsNullOrWhiteSpace(serverItem.DownloadUrl) ? "" : GetDisplayVersion(serverItem),
                MatchConfidence = SyncMatchConfidence.None,
                Reason = string.IsNullOrWhiteSpace(serverItem.DownloadUrl)
                    ? "No compatible version found in manifest."
                    : "Not present locally."
            });
        }

        return plan;
    }

    private static List<T> GetOrAdd<T>(Dictionary<string, List<T>> dict, string key)
    {
        if (!dict.TryGetValue(key, out var list))
            dict[key] = list = [];
        return list;
    }

    private static string GetDisplayVersion(ManifestMod mod)
        => !string.IsNullOrWhiteSpace(mod.VersionName) ? mod.VersionName.Trim()
         : !string.IsNullOrWhiteSpace(mod.VersionNumber) ? mod.VersionNumber.Trim()
         : "";

    private static string GetDisplayVersion(PackManifestItem item)
        => !string.IsNullOrWhiteSpace(item.VersionName) ? item.VersionName.Trim()
         : !string.IsNullOrWhiteSpace(item.VersionNumber) ? item.VersionNumber.Trim()
         : "";

    private static string GetMatchedAlternateVersion(IReadOnlyList<AlternateVersionInfo> alternates, string fileName, string? fileHash)
    {
        var alt = alternates.FirstOrDefault(x =>
            !string.IsNullOrWhiteSpace(x.Filename) &&
            string.Equals(x.Filename, fileName, StringComparison.OrdinalIgnoreCase));

        if (alt is null && !string.IsNullOrWhiteSpace(fileHash))
        {
            alt = alternates.FirstOrDefault(x =>
                !string.IsNullOrWhiteSpace(x.FileHash?.Hash) &&
                string.Equals(x.FileHash.Hash, fileHash, StringComparison.OrdinalIgnoreCase));
        }

        return alt is null
            ? ""
            : !string.IsNullOrWhiteSpace(alt.VersionName) ? alt.VersionName.Trim()
            : !string.IsNullOrWhiteSpace(alt.VersionNumber) ? alt.VersionNumber.Trim()
            : "";
    }

    private static string GetInstalledVersion(ManifestMod mod, LocalModFile file, string? fileHash)
    {
        var fromAlternate = GetMatchedAlternateVersion(mod.AlternateVersions, file.FileName, fileHash);
        return FirstNonEmpty(fromAlternate, file.InventoryVersionName, file.InventoryVersionNumber, InferVersionNameFromFilename(file));
    }

    private static string GetInstalledVersion(PackManifestItem item, LocalModFile file, string? fileHash)
    {
        var fromAlternate = GetMatchedAlternateVersion(item.AlternateVersions, file.FileName, fileHash);
        return FirstNonEmpty(fromAlternate, file.InventoryVersionName, file.InventoryVersionNumber, InferVersionNameFromFilename(file));
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
                return value.Trim();
        }

        return "";
    }

    /// <summary>
    /// Finds the server item whose <c>filename_prefix</c> or <c>regex</c> alias matches
    /// <paramref name="fileName"/>.
    /// <list type="bullet">
    ///   <item>(item, false) — exactly one item matched.</item>
    ///   <item>(null, false) — no item matched.</item>
    ///   <item>(null, true)  — more than one item matched (ambiguous; skip).</item>
    /// </list>
    /// </summary>
    private static (PackManifestItem? Match, bool Ambiguous) FindPatternMatch(
        IReadOnlyList<PackManifestItem> serverItems, string fileName)
    {
        PackManifestItem? found = null;

        foreach (var item in serverItems)
        {
            var itemMatches = false;

            foreach (var alias in item.Aliases)
            {
                var aliasType  = alias.Type?.Trim()  ?? "";
                var aliasValue = alias.Value?.Trim() ?? "";
                if (aliasValue.Length == 0)
                    continue;

                if (aliasType.Equals("filename_prefix", StringComparison.OrdinalIgnoreCase) &&
                    fileName.StartsWith(aliasValue, StringComparison.OrdinalIgnoreCase))
                {
                    itemMatches = true;
                    break;
                }

                if (aliasType.Equals("regex", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        if (Regex.IsMatch(fileName, aliasValue, RegexOptions.IgnoreCase))
                        {
                            itemMatches = true;
                            break;
                        }
                    }
                    catch (RegexParseException)
                    {
                        // Invalid pattern stored on server — skip rather than crash.
                    }
                }
            }

            if (!itemMatches)
                continue;

            if (found == null)
                found = item;
            else if (!ReferenceEquals(found, item))
                return (null, true); // two different items matched — ambiguous
        }

        return (found, false);
    }

    /// <summary>
    /// Conservative identity match for files no longer listed as compatible
    /// alternates. A match must start with a normalized project slug/name or a
    /// project/display/manual alias. This catches filenames like
    /// "beautiful-enchanted-books-mc-1-21-10-8-0-0" without accepting loose
    /// contains matches in unrelated packs.
    /// </summary>
    private static (PackManifestItem? Match, bool Ambiguous) FindMediumIdentityMatch(
        IReadOnlyList<PackManifestItem> serverItems, string fileName)
    {
        var fileTokens = Matching.Tokenize(Path.GetFileNameWithoutExtension(fileName));
        PackManifestItem? found = null;

        foreach (var item in serverItems)
        {
            if (MediumIdentityMatches(item, fileTokens))
            {
                if (found is null)
                    found = item;
                else if (!ReferenceEquals(found, item))
                    return (null, true);
            }
        }

        return (found, false);
    }

    private static bool MediumIdentityMatches(PackManifestItem item, string[] fileTokens)
    {
        if (PrefixTokensMatch(fileTokens, item.Slug))
            return true;

        if (PrefixTokensMatch(fileTokens, item.Name))
            return true;

        foreach (var alias in item.Aliases)
        {
            var aliasType = alias.Type?.Trim() ?? "";
            if (!aliasType.Equals("project_slug", StringComparison.OrdinalIgnoreCase) &&
                !aliasType.Equals("display", StringComparison.OrdinalIgnoreCase) &&
                !aliasType.Equals("manual", StringComparison.OrdinalIgnoreCase))
                continue;

            if (PrefixTokensMatch(fileTokens, alias.Value))
                return true;
        }

        return false;
    }

    private static bool PrefixTokensMatch(string[] fileTokens, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var tokens = Matching.Tokenize(value);
        return tokens.Length >= 2 && Matching.TokensMatchAtStart(fileTokens, tokens);
    }

    private static bool PackIdentityMatches(PackManifestItem item, LocalModFile file)
    {
        if (!string.IsNullOrWhiteSpace(file.SourceId) &&
            !string.IsNullOrWhiteSpace(item.SourceId) &&
            string.Equals(file.SourceId, item.SourceId, StringComparison.OrdinalIgnoreCase))
            return true;

        return !string.IsNullOrWhiteSpace(file.Slug) &&
               !string.IsNullOrWhiteSpace(item.Slug) &&
               string.Equals(file.Slug, item.Slug, StringComparison.OrdinalIgnoreCase);
    }

    private static SyncAction CreatePackIdentityAction(string contentType, PackManifestItem serverPack, LocalModFile local)
    {
        var filenameMatches = string.Equals(local.FileName, serverPack.DownloadFilename, StringComparison.OrdinalIgnoreCase);
        string? localHash = null;
        if (HasUsableHash(serverPack.DownloadHash) && File.Exists(local.FullPath))
            _ = HashMatchesFile(local.FullPath, serverPack.DownloadHash, out localHash);

        var hashMatches = !HasUsableHash(serverPack.DownloadHash) ||
                          (File.Exists(local.FullPath) && HashMatchesFile(local.FullPath, serverPack.DownloadHash, out localHash));
        var isCurrent = filenameMatches && hashMatches;

        return new SyncAction
        {
            Kind = isCurrent ? SyncActionKind.UpToDate : SyncActionKind.Replace,
            ContentType = contentType,
            PackManifestItem = serverPack,
            LocalFile = local,
            IsCustom = serverPack.IsCustom,
            MatchConfidence = SyncMatchConfidence.High,
            InstalledVersion = isCurrent
                ? GetDisplayVersion(serverPack)
                : GetInstalledVersion(serverPack, local, localHash),
            TargetVersion = isCurrent ? "" : GetDisplayVersion(serverPack),
            Reason = isCurrent
                ? "SQLite identity and active file match."
                : $"SQLite identity match: replace {local.FileName} with {serverPack.DownloadFilename}."
        };
    }

    private static string InferVersionNameFromFilename(LocalModFile file)
        => InferVersionNameFromFilename(file.FileName);

    private static string InferVersionNameFromFilename(string fileName)
    {
        return Path.GetFileNameWithoutExtension(fileName).Trim();
    }

    private static void AddHashLookup(
        Dictionary<string, Dictionary<string, PackManifestItem>> hashToPacks,
        ManifestHashInfo? hash,
        PackManifestItem item)
    {
        if (!TryNormalizeHash(hash, out var normalized))
            return;

        if (!hashToPacks.TryGetValue(normalized.Algorithm, out var algorithmHashes))
            hashToPacks[normalized.Algorithm] = algorithmHashes = new Dictionary<string, PackManifestItem>(StringComparer.OrdinalIgnoreCase);

        algorithmHashes.TryAdd(normalized.Hash, item);
    }

    private static bool TryFindHashMatch(
        Dictionary<string, Dictionary<string, PackManifestItem>> hashToPacks,
        string path,
        out PackManifestItem item,
        out string localHash)
    {
        foreach (var (algorithm, hashes) in hashToPacks)
        {
            localHash = ComputeFileHash(path, algorithm);
            if (hashes.TryGetValue(localHash, out item!))
                return true;
        }

        item = null!;
        localHash = "";
        return false;
    }

    private static bool MatchesUnavailable(LocalModFile file, string provider, string sourceId, string slug, string filename)
        => (!string.IsNullOrWhiteSpace(filename) && string.Equals(file.FileName, filename, StringComparison.OrdinalIgnoreCase))
            || (!string.IsNullOrWhiteSpace(provider) && string.Equals(file.Source, provider, StringComparison.OrdinalIgnoreCase)
                && ((!string.IsNullOrWhiteSpace(sourceId) && string.Equals(file.SourceId, sourceId, StringComparison.Ordinal))
                    || (string.IsNullOrWhiteSpace(sourceId) && !string.IsNullOrWhiteSpace(slug) && string.Equals(file.Slug, slug, StringComparison.OrdinalIgnoreCase))));

    private static bool HashMatchesFile(string path, ManifestHashInfo? expectedHash, out string localHash)
    {
        localHash = "";
        if (!TryNormalizeHash(expectedHash, out var normalized))
            return false;

        localHash = ComputeFileHash(path, normalized.Algorithm);
        return string.Equals(localHash, normalized.Hash, StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasUsableHash(ManifestHashInfo? hash) => TryNormalizeHash(hash, out _);

    private static bool TryNormalizeHash(ManifestHashInfo? hash, out ManifestHashInfo normalized)
    {
        normalized = ManifestHashInfo.Empty;
        if (hash is null || !hash.HasValue)
            return false;

        normalized = hash.Normalized();
        var expectedLength = normalized.Algorithm switch
        {
            "sha1" => 40,
            "sha256" => 64,
            "sha512" => 128,
            _ => 0
        };

        return expectedLength > 0 &&
               normalized.Hash.Length == expectedLength &&
               normalized.Hash.All(IsHex);
    }

    private static string ComputeFileHash(string path, string algorithm)
    {
        using System.Security.Cryptography.HashAlgorithm hashAlgorithm = algorithm switch
        {
            "sha1" => System.Security.Cryptography.SHA1.Create(),
            "sha256" => System.Security.Cryptography.SHA256.Create(),
            "sha512" => System.Security.Cryptography.SHA512.Create(),
            _ => throw new InvalidOperationException($"Unsupported hash algorithm: {algorithm}.")
        };

        using var stream = File.OpenRead(path);
        var hashBytes = hashAlgorithm.ComputeHash(stream);
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }

    private static bool IsHex(char value) =>
        (value >= '0' && value <= '9') ||
        (value >= 'a' && value <= 'f') ||
        (value >= 'A' && value <= 'F');
}
