using System.Security.Cryptography;
using System.Text;

namespace HalerithAddonClient.Core.LocalModpacks;

public sealed partial class LocalPackInstaller
{
    public static Uri DownloadPage(LocalPackMember member)
    {
        var project = member.Project; var version = Uri.EscapeDataString(member.Version.SourceVersionId);
        if (version.Length == 0) throw new InvalidOperationException("The pack file identity is unavailable. Reapply the pack to retry its metadata.");
        if (project.Source == "curseforge" && long.TryParse(project.Id, out var id) && long.TryParse(member.Version.SourceVersionId, out _))
        {
            if (Uri.TryCreate(project.SourceUrl, UriKind.Absolute, out var page) && page.Scheme == "https" && page.Host is "www.curseforge.com" or "curseforge.com")
                return new Uri(page.GetLeftPart(UriPartial.Path).TrimEnd('/') + "/files/" + version);
            return new Uri($"https://www.curseforge.com/projects/{id}/files/{version}");
        }
        if (project.Source == "modrinth" && project.Id.Length > 0)
            return new Uri($"https://modrinth.com/{(project.ContentType == ContentTypes.ResourcePack ? "resourcepack" : project.ContentType == ContentTypes.Shader ? "shader" : "mod")}/{Uri.EscapeDataString(project.Id)}/version/{version}");
        throw new InvalidOperationException("This item has no provider file page.");
    }

    public async Task ImportMemberAsync(string instanceId, string root, LocalPackMember expected, string selectedFile, CancellationToken ct = default)
    {
        using var lease = Acquire(root);
        var pack = LocalModpackStore.Load(root) ?? throw new InvalidOperationException("The pack is no longer installed.");
        var member = pack.Members.SingleOrDefault(m => m.Path == expected.Path && m.Project.Id == expected.Project.Id && m.Project.Source == expected.Project.Source);
        if (member is null || member.Path.Length == 0 || member.Version.SourceVersionId != expected.Version.SourceVersionId
            || member.Version.FileHash != expected.Version.FileHash || member.Overridden || !member.Enabled)
            throw new InvalidOperationException("The pack item changed or has an override. Refresh it before importing the pack's file.");
        var hash = SafeFileHelpers.RequireExpectedHash(member.Version.FileHash, "Pack file");
        var target = LocalModpackStore.SafePath(root, member.Path);
        var stage = LocalModpackStore.SafePath(root, member.Path + "." + Guid.NewGuid().ToString("N") + ".tmp");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        try
        {
            await using (var input = File.OpenRead(selectedFile))
            {
                if (input.Length > LocalPackInspector.MaxExpandedBytes || (member.Version.FileSizeBytes is > 0 && input.Length != member.Version.FileSizeBytes))
                    throw new InvalidOperationException("This file's size does not match the pack's required download.");
                await using var output = File.Create(stage);
                await LocalPackInspector.CopyBoundedAsync(input, output, input.Length, ct);
            }
            await SafeFileHelpers.VerifyExpectedHashAsync(stage, hash, ct);
            ct.ThrowIfCancellationRequested();
            var cacheKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(member.Version.FileHash!))).ToLowerInvariant();
            var cached = LocalModpackStore.SafePath(root, $".halerith/local-pack-cache/files/{cacheKey}", true);
            Directory.CreateDirectory(Path.GetDirectoryName(cached)!);
            File.Copy(stage, cached, overwrite: true);
            var backup = LocalModpackStore.SafePath(root, ".halerith/modpack-backups/" + Guid.NewGuid().ToString("N") + "/" + member.Path, true);
            Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
            VerifiedContentCommit.Commit(stage, target, target, backup, log: _log);
            if (member.Project.Id.Length > 0)
                ContentSearch.LocalInstanceContentStore.AddOrUpdateItem(instanceId, root, member.Project, member.Version);
            member.Missing = false;
            LocalModpackStore.Save(root, pack);
        }
        finally { if (File.Exists(stage)) File.Delete(stage); }
    }
}
