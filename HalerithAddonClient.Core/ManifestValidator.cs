namespace HalerithAddonClient.Core;

public static class ManifestValidator
{
    public static void RequireDownloadHashes(ManifestResponse manifest)
    {
        foreach (var item in manifest.Mods)
            if (RequiresHash(item.Unavailable, item.PackManaged, item.ManualDownloadAllowed, item.VersionSource, item.DownloadUrl))
                RequireHash(item.DownloadHash, "mod", item.Name, item.DownloadFilename);

        foreach (var item in manifest.ResourcePacks)
            if (RequiresHash(item.Unavailable, item.PackManaged, item.ManualDownloadAllowed, item.VersionSource, item.DownloadUrl))
                RequireHash(item.DownloadHash, "resource pack", item.Name, item.DownloadFilename);

        foreach (var item in manifest.Shaders)
            if (RequiresHash(item.Unavailable, item.PackManaged, item.ManualDownloadAllowed, item.VersionSource, item.DownloadUrl))
                RequireHash(item.DownloadHash, "shader", item.Name, item.DownloadFilename);

        if (manifest.Modpack is { } pack)
        {
            RequireHash(new ManifestHashInfo { Algorithm = "sha256", Hash = pack.Archive.Sha256 }, "modpack", pack.VersionName, pack.Archive.Filename);
            foreach (var file in pack.Files)
                RequireHash(new ManifestHashInfo { Algorithm = "sha256", Hash = file.Sha256 }, "modpack file", file.Path, file.Path);
        }
    }

    private static bool RequiresHash(bool unavailable, bool managed, bool manualDownloadAllowed, string source, string url)
    {
        if (!unavailable) return true;
        if (!string.IsNullOrWhiteSpace(url))
            throw new InvalidOperationException("An unavailable item must not include a download URL.");
        if (managed) return false;
        if (!manualDownloadAllowed || !string.Equals(source, "curseforge", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("An unavailable non-pack item must require a manual CurseForge download.");
        return true;
    }

    private static void RequireHash(ManifestHashInfo? hash, string contentType, string name, string fileName)
    {
        var label = string.IsNullOrWhiteSpace(name)
            ? string.IsNullOrWhiteSpace(fileName) ? contentType : $"{contentType} '{fileName}'"
            : $"{contentType} '{name}'";

        if (hash is null)
            throw new InvalidOperationException($"Manifest item {label} is missing download_hash.");

        var normalized = hash.Normalized();
        if (!normalized.HasValue)
            throw new InvalidOperationException($"Manifest item {label} has incomplete download_hash.");

        if (!normalized.Hash.All(IsHex))
            throw new InvalidOperationException($"Manifest item {label} has a non-hex download_hash.");

        var expectedLength = normalized.Algorithm switch
        {
            "sha1" => 40,
            "sha256" => 64,
            "sha512" => 128,
            _ => throw new InvalidOperationException(
                $"Manifest item {label} has unsupported download_hash algorithm '{normalized.Algorithm}'.")
        };

        if (normalized.Hash.Length != expectedLength)
        {
            throw new InvalidOperationException(
                $"Manifest item {label} has invalid {normalized.Algorithm} hash length {normalized.Hash.Length}.");
        }
    }

    private static bool IsHex(char value) =>
        (value >= '0' && value <= '9') ||
        (value >= 'a' && value <= 'f') ||
        (value >= 'A' && value <= 'F');
}
