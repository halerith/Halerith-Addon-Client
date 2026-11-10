using System.Text.Json.Serialization;

namespace HalerithAddonClient.Core;

public sealed class ModpackRuntimeRequirements
{
    [JsonPropertyName("minecraft_version")] public string MinecraftVersion { get; set; } = "";
    [JsonPropertyName("loader")] public string Loader { get; set; } = "";
    [JsonPropertyName("loader_version")] public string LoaderVersion { get; set; } = "";
    [JsonPropertyName("complete")] public bool Complete { get; set; }
}

public static class ModpackInstallationRequirements
{
    // The server's effective runtime is authoritative in both Client modes.
    // Pack requirements describe compatibility; they never select the runtime.
    public static ManifestServerInfo? RuntimeTarget(ManifestResponse manifest)
    {
        return manifest.Server;
    }

    public static async Task AssertReadyAsync(ManifestResponse manifest, SyncPlan plan, RuntimeConfig config, CancellationToken ct)
    {
        if (manifest.Modpack is null) return;
        if ((manifest.Mods.Any(m => m.PackManaged && !m.Unavailable) && string.IsNullOrWhiteSpace(config.ModsFolder))
            || (manifest.ResourcePacks.Any(m => m.PackManaged && !m.Unavailable) && string.IsNullOrWhiteSpace(config.ResourcePacksFolder))
            || (manifest.Shaders.Any(m => m.PackManaged && !m.Unavailable) && string.IsNullOrWhiteSpace(config.ShadersFolder)))
            throw new InvalidOperationException("A required pack content folder is not configured. Review the instance settings before launching.");

        foreach (var action in plan.Actions)
        {
            ct.ThrowIfCancellationRequested();
            // Explicit Client pins/ignore choices retain their existing meaning.
            if (action.Kind == SyncActionKind.Ignore) continue;
            if (action.Kind == SyncActionKind.Missing &&
                !ServerPackManualImport.IsEligible(action) &&
                (action.ManifestMod is { PackManaged: true, Unavailable: true } || action.PackManifestItem is { PackManaged: true, Unavailable: true })) continue;
            if (action.ManifestMod?.PackManaged != true && action.PackManifestItem?.PackManaged != true) continue;
            if (action.Kind != SyncActionKind.UpToDate || action.LocalFile is null || !File.Exists(action.LocalFile.FullPath))
                throw new InvalidOperationException("Required modpack content is missing or needs repair. Update content before launching.");
            var hash = action.ManifestMod?.DownloadHash ?? action.PackManifestItem?.DownloadHash;
            _ = SafeFileHelpers.RequireExpectedHash(hash, action.LocalFile.FileName);
            await SafeFileHelpers.VerifyExpectedHashAsync(action.LocalFile.FullPath, hash, ct).ConfigureAwait(false);
        }

        if (config.ModpackFilePolicy != ModpackFilePolicies.Ignore || manifest.Modpack.ForceConfig)
        {
            if (plan.Actions.Any(a => a.BundledFile is { Exists: false }))
                throw new InvalidOperationException("Bundled modpack content is missing. Apply the modpack before launching.");
        }
        // Provider project IDs cannot reliably identify Fabric dependency IDs for an
        // unavailable jar. Do not turn the expressly non-blocking Missing state back
        // into a launch veto through this heuristic. The checks above still enforce
        // every available download, pending repair, runtime and bundled file.
        if (!manifest.Mods.Any(m => m.PackManaged && m.Unavailable)
            && RuntimeTarget(manifest)?.Loader.Equals("fabric", StringComparison.OrdinalIgnoreCase) == true)
        {
            var missing = FabricDependencyPreflight.Missing(config.ModsFolder, config.GameDataPath);
            if (missing.Count > 0)
                throw new InvalidOperationException("Required mod dependencies are missing. Review the content in Manager: " + string.Join("; ", missing));
        }
    }
}
