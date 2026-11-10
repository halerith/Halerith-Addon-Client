using HalerithAddonClient.Core.LocalModpacks;

namespace HalerithAddonClient.Core.ContentSearch;

public sealed partial class LocalContentPlanner
{
    private async Task AddPackRows(LocalContentPlan plan, LocalModpack pack, string root, HashSet<string> tracked, CancellationToken ct)
    {
        bool missing = false;
        foreach (var member in pack.Members)
        {
            if (!member.IsAddon)
            {
                missing |= member.Enabled && !member.Missing && pack.Policy != ModpackFilePolicies.Ignore && !pack.KeptFiles.Contains(member.Path)
                    && !File.Exists(LocalModpackStore.SafePath(root, member.Path));
                continue;
            }
            if (member.Path.Length > 0) tracked.Add(Path.GetFileName(member.Path));
            if (member.Overridden && member.Enabled && member.Project.Id.Length > 0 && File.Exists(LocalModpackStore.SafePath(root, member.OverridePath ?? member.Path))) continue;
            var providerUnavailable = member.Project.Source.Equals("curseforge", StringComparison.OrdinalIgnoreCase) && !_curseForge.IsAvailable;
            var exists = member.Path.Length > 0 && File.Exists(LocalModpackStore.SafePath(root, member.Path));
            if (exists && !member.Overridden && !string.IsNullOrWhiteSpace(member.Version.FileHash))
                try { await SafeFileHelpers.VerifyExpectedHashAsync(LocalModpackStore.SafePath(root, member.Path), member.Version.FileHash, ct); }
                catch (InvalidOperationException) { exists = false; }
            missing |= member.Enabled && !member.Overridden && !exists && !member.Missing;
            var unavailable = providerUnavailable || member.Missing || string.IsNullOrWhiteSpace(member.Version.DownloadUrl);
            plan.Items.Add(new LocalContentPlanItem
            {
                PackMember = member, Name = member.Project.Name, ContentType = member.Project.ContentType,
                Kind = !member.Enabled ? SyncActionKind.Ignore : exists ? SyncActionKind.UpToDate : unavailable ? SyncActionKind.Missing : SyncActionKind.Download,
                Status = providerUnavailable ? "Provider unavailable" : null,
                InstalledVersion = exists ? member.Version.VersionName : "", LatestVersion = member.Version.VersionName,
                ManifestItem = member.Project.Id.Length == 0 ? null : new ContentManifestItem
                {
                    Source = member.Project.Source, SourceId = member.Project.Id, Slug = member.Project.Slug,
                    Name = member.Project.Name, ContentType = member.Project.ContentType, IconUrl = member.Project.IconUrl,
                    InstalledVersion = new ContentManifestVersion { Filename = member.Version.Filename, SourceVersionId = member.Version.SourceVersionId,
                        VersionName = member.Version.VersionName, FileHash = member.Version.FileHash, DownloadUrl = member.Version.DownloadUrl }
                }
            });
        }
        missing |= pack.Policy != ModpackFilePolicies.Ignore && pack.BundledFiles.Any(f => !pack.KeptFiles.Contains(f.Path) && !File.Exists(LocalModpackStore.SafePath(root, f.Path)));
        foreach (var file in pack.BundledFiles.Where(f => f.Path.StartsWith("mods/") || f.Path.StartsWith("resourcepacks/") || f.Path.StartsWith("shaderpacks/")))
        {
            tracked.Add(Path.GetFileName(file.Path));
            var exists = File.Exists(LocalModpackStore.SafePath(root, file.Path));
            missing |= !exists && pack.Policy != ModpackFilePolicies.Ignore && !pack.KeptFiles.Contains(file.Path);
            plan.Items.Add(new LocalContentPlanItem { IsBundledFile = true, Name = Path.GetFileName(file.Path), ContentType = LocalPackInspector.TypeFor(file.Path),
                InstalledVersion = exists ? pack.Version : "", Kind = exists ? SyncActionKind.UpToDate : SyncActionKind.Ignore,
                Status = exists ? "Current" : pack.Policy == ModpackFilePolicies.Ignore || pack.KeptFiles.Contains(file.Path) ? "Ignored" : "Pending" });
        }
        ContentVersionResult? newer = null;
        if (pack.ProjectId.Length > 0 && GetService(pack.Provider) is { } service)
        {
            var versions = await service.GetVersionsAsync(pack.ProjectId, ct: ct);
            var eligible = versions.Where(v => !v.IsServerPack && LocalPackDownloads.IsAllowedUrl(v.DownloadUrl)).ToList();
            if (eligible.FirstOrDefault() is { } latest && latest.SourceVersionId != pack.VersionId && eligible.Any(v => v.SourceVersionId == pack.VersionId)) newer = latest;
        }
        plan.Items.Insert(0, new LocalContentPlanItem { Pack = pack, ContentType = ContentTypes.Modpack, Name = pack.Name,
            InstalledVersion = pack.AppliedAtUtc is null ? "Not applied" : pack.Version, LatestVersion = newer?.VersionName ?? pack.Version,
            LatestVersionResult = newer, Kind = newer is not null ? SyncActionKind.Replace : pack.Failed || missing || pack.AppliedAtUtc is null ? SyncActionKind.Download : SyncActionKind.UpToDate,
            Status = pack.Failed ? "Failed" : newer is not null ? "Update" : pack.AppliedAtUtc is null ? "Download" : missing ? "Reapply"
                : pack.Provider.Equals("curseforge", StringComparison.OrdinalIgnoreCase) && !_curseForge.IsAvailable ? "Provider unavailable" : "Current" });
    }
}
