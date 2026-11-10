using System.Security.Cryptography;
using System.Text.Json;

namespace HalerithAddonClient.Core;

// Shared verbatim with the privileged helper. Trust never comes from the download server.
public static class LinuxUpdateTrust
{
    public const long MaximumPackageBytes = 100L * 1024 * 1024;
    public const int MaximumManifestBytes = 4096;
    public static void Verify(string package, byte[] manifest, byte[] signature, string expectedVersion, string? installedVersion = null)
    {
        using var keyStream = typeof(LinuxUpdateTrust).Assembly.GetManifestResourceStream("LinuxReleasePublicKey")!;
        using var reader = new StreamReader(keyStream);
        VerifyWithKey(package, manifest, signature, expectedVersion, installedVersion, reader.ReadToEnd());
    }

    internal static void VerifyWithKey(string package, byte[] manifest, byte[] signature, string expectedVersion, string? installedVersion, string publicKey)
    {
        if (manifest.Length is < 1 or > MaximumManifestBytes || signature.Length != 384)
            throw new InvalidDataException("Linux release manifest or signature has an invalid size.");
        using var rsa = RSA.Create();
        rsa.ImportFromPem(publicKey);
        if (rsa.KeySize != 3072 || !rsa.VerifyData(manifest, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
            throw new InvalidDataException("Linux release signature is not trusted.");
        using var document = JsonDocument.Parse(manifest, new JsonDocumentOptions { MaxDepth = 4 });
        var data = document.RootElement;
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in data.EnumerateObject())
            if (!names.Add(field.Name)) throw new InvalidDataException("Duplicate manifest field.");
        if (names.Count != 9 || data.GetProperty("schema").GetInt32() != 1
            || data.GetProperty("product").GetString() != "halerith-addon-client"
            || data.GetProperty("platform").GetString() != "linux"
            || data.GetProperty("architecture").GetString() != "x64"
            || data.GetProperty("package_type").GetString() != "deb"
            || data.GetProperty("version").GetString() != expectedVersion
            || data.GetProperty("filename").GetString() != Path.GetFileName(package))
            throw new InvalidDataException("Linux release manifest does not match this update.");
        _ = ParseVersion(expectedVersion);
        if (installedVersion is not null) RequireNewer(expectedVersion, installedVersion);
        using var file = File.OpenRead(package);
        if (file.Length is < 1 or > MaximumPackageBytes || data.GetProperty("size").GetInt64() != file.Length)
            throw new InvalidDataException("Linux package size does not match its signed manifest.");
        var hash = data.GetProperty("sha256").GetString();
        if (hash != Convert.ToHexString(SHA256.HashData(file)).ToLowerInvariant())
            throw new InvalidDataException("Linux package hash does not match its signed manifest.");
    }

    public static void RequireNewer(string candidate, string installed)
    {
        if (ParseVersion(candidate) <= ParseVersion(installed))
            throw new InvalidDataException("Linux updates must be newer than the installed version.");
    }

    private static Version ParseVersion(string value)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(value, @"^[0-9]+\.[0-9]+\.[0-9]+(\.[0-9]+)?$") || !Version.TryParse(value, out var v))
            throw new InvalidDataException("Invalid Linux release version.");
        return new Version(v.Major, v.Minor, Math.Max(v.Build, 0), Math.Max(v.Revision, 0));
    }

    public static byte[] ReadBounded(string path, int maximum)
    {
        using var file = File.OpenRead(path);
        if (file.Length is < 1 || file.Length > maximum) throw new InvalidDataException("Release metadata exceeds its size limit.");
        var bytes = new byte[(int)file.Length];
        file.ReadExactly(bytes);
        if (file.ReadByte() != -1) throw new InvalidDataException("Release metadata changed while reading.");
        return bytes;
    }
}
