using System.Security.Cryptography;

namespace HalerithAddonClient.Core;

internal static class SafeFileHelpers
{
    public static string RequireSafeFileName(string fileName, string label)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            throw new InvalidOperationException($"{label} is empty.");

        fileName = fileName.Trim();
        var stem = Path.GetFileNameWithoutExtension(fileName);

        if (!string.Equals(Path.GetFileName(fileName), fileName, StringComparison.Ordinal) ||
            fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            fileName.Contains(Path.DirectorySeparatorChar) ||
            fileName.Contains(Path.AltDirectorySeparatorChar) ||
            string.Equals(fileName, ".", StringComparison.Ordinal) ||
            string.Equals(fileName, "..", StringComparison.Ordinal) ||
            fileName.EndsWith('.') ||
            IsReservedWindowsDeviceName(stem))
        {
            throw new InvalidOperationException($"{label} is not a safe file name: {fileName}");
        }

        return fileName;
    }

    public static string CombineUnderDirectory(string directory, string safeFileName)
    {
        var root = Path.GetFullPath(directory);
        var fullPath = Path.GetFullPath(Path.Combine(root, safeFileName));
        var rootWithSeparator = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;

        if (!fullPath.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Resolved file path escapes target folder: {safeFileName}");

        return fullPath;
    }

    public static string CreateDownloadTempPath(string tempFolder, string safeFileName)
    {
        Directory.CreateDirectory(tempFolder);
        return Path.Combine(tempFolder, $"{Guid.NewGuid():N}-{safeFileName}.download");
    }

    public static async Task VerifyExpectedHashAsync(
        string filePath,
        string? expectedHash,
        CancellationToken ct)
    {
        await VerifyExpectedHashAsync(filePath, ManifestHashInfo.FromHash(expectedHash), ct).ConfigureAwait(false);
    }

    public static async Task VerifyExpectedHashAsync(
        string filePath,
        ManifestHashInfo? expectedHash,
        CancellationToken ct)
    {
        var normalized = NormalizeExpectedHash(expectedHash, Path.GetFileName(filePath), allowMissing: true);
        if (normalized is null)
            return;

        using HashAlgorithm algorithm = normalized.Algorithm switch
        {
            "sha1" => SHA1.Create(),
            "sha256" => SHA256.Create(),
            "sha512" => SHA512.Create(),
            _ => throw new InvalidOperationException(
                $"Unsupported hash algorithm for {Path.GetFileName(filePath)}: {normalized.Algorithm}.")
        };

        await using var stream = File.OpenRead(filePath);
        var actualHash = Convert.ToHexString(await algorithm.ComputeHashAsync(stream, ct).ConfigureAwait(false));
        if (!string.Equals(actualHash, normalized.Hash, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Hash mismatch for {Path.GetFileName(filePath)}: expected {normalized.Hash}, got {actualHash}.");
        }
    }

    public static ManifestHashInfo RequireExpectedHash(string? expectedHash, string label) =>
        RequireExpectedHash(ManifestHashInfo.FromHash(expectedHash), label);

    public static ManifestHashInfo RequireExpectedHash(ManifestHashInfo? expectedHash, string label) =>
        NormalizeExpectedHash(expectedHash, label, allowMissing: false)!;

    private static ManifestHashInfo? NormalizeExpectedHash(
        ManifestHashInfo? expectedHash,
        string label,
        bool allowMissing)
    {
        if (expectedHash is null)
        {
            if (allowMissing)
                return null;

            throw new InvalidOperationException($"{label} is missing a provider hash.");
        }

        var normalized = expectedHash.Normalized();
        if (string.IsNullOrWhiteSpace(normalized.Algorithm) && string.IsNullOrWhiteSpace(normalized.Hash))
        {
            if (allowMissing)
                return null;

            throw new InvalidOperationException($"{label} is missing a provider hash.");
        }

        if (!normalized.HasValue)
            throw new InvalidOperationException($"{label} has an incomplete expected hash.");

        if (!normalized.Hash.All(IsHex))
            throw new InvalidOperationException($"{label} has an expected hash that is not valid hex.");

        var expectedLength = normalized.Algorithm switch
        {
            "sha1" => 40,
            "sha256" => 64,
            "sha512" => 128,
            _ => throw new InvalidOperationException(
                $"{label} has an unsupported hash algorithm: {normalized.Algorithm}.")
        };

        if (normalized.Hash.Length != expectedLength)
        {
            throw new InvalidOperationException(
                $"{label} has an invalid {normalized.Algorithm} hash length: {normalized.Hash.Length}.");
        }

        return normalized;
    }

    private static bool IsHex(char value) =>
        (value >= '0' && value <= '9') ||
        (value >= 'a' && value <= 'f') ||
        (value >= 'A' && value <= 'F');

    private static bool IsReservedWindowsDeviceName(string stem)
    {
        var name = stem.TrimEnd(' ', '.');
        return name.Equals("CON", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("AUX", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
               IsReservedDeviceRange(name, "COM") ||
               IsReservedDeviceRange(name, "LPT");
    }

    private static bool IsReservedDeviceRange(string name, string prefix) =>
        name.Length == 4 &&
        name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
        name[3] >= '1' &&
        name[3] <= '9';
}
