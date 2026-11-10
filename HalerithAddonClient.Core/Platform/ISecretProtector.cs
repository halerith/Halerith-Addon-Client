using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace HalerithAddonClient.Core.Platform;

public interface ISecretProtector
{
    byte[] Protect(byte[] plainBytes, string purpose);
    byte[]? Unprotect(byte[] protectedBytes, string purpose);
}

#if WINDOWS
internal sealed class WindowsSecretProtector : ISecretProtector
{
    public byte[] Protect(byte[] plainBytes, string purpose) =>
        ProtectedData.Protect(plainBytes, GetEntropy(purpose), DataProtectionScope.CurrentUser);

    public byte[]? Unprotect(byte[] protectedBytes, string purpose)
    {
        try
        {
            return ProtectedData.Unprotect(protectedBytes, GetEntropy(purpose), DataProtectionScope.CurrentUser);
        }
        catch (CryptographicException)
        {
            return null;
        }
        catch (PlatformNotSupportedException)
        {
            return null;
        }
    }

    private static byte[] GetEntropy(string purpose) =>
        SHA256.HashData(Encoding.UTF8.GetBytes("HalerithAddonClient:" + purpose));
}
#endif

internal sealed class UnsupportedSecretProtector : ISecretProtector
{
    public byte[] Protect(byte[] plainBytes, string purpose) =>
        throw new PlatformNotSupportedException("Secret protection is not implemented for this platform.");

    public byte[]? Unprotect(byte[] protectedBytes, string purpose) => null;
}

internal sealed class MacKeychainSecretProtector : ISecretProtector
{
    private const string VersionPrefix = "v1";
    private const string ServiceName = "com.halerith.addon-client.master-key";
    private readonly Lazy<byte[]> _masterKey = new(LoadOrCreateMasterKey);

    public byte[] Protect(byte[] plainBytes, string purpose)
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var tag = new byte[16];
        var cipher = new byte[plainBytes.Length];

        using var aes = new AesGcm(_masterKey.Value, tag.Length);
        aes.Encrypt(nonce, plainBytes, cipher, tag, GetAssociatedData(purpose));

        using var payload = new MemoryStream();
        payload.Write(Encoding.ASCII.GetBytes(VersionPrefix));
        payload.WriteByte(0);
        payload.Write(nonce);
        payload.Write(tag);
        payload.Write(cipher);
        return payload.ToArray();
    }

    public byte[]? Unprotect(byte[] protectedBytes, string purpose)
    {
        try
        {
            var prefixBytes = Encoding.ASCII.GetBytes(VersionPrefix);
            if (protectedBytes.Length < prefixBytes.Length + 1 + 12 + 16)
                return null;

            if (!protectedBytes.AsSpan(0, prefixBytes.Length).SequenceEqual(prefixBytes) ||
                protectedBytes[prefixBytes.Length] != 0)
                return null;

            var offset = prefixBytes.Length + 1;
            var nonce = protectedBytes.AsSpan(offset, 12);
            offset += 12;
            var tag = protectedBytes.AsSpan(offset, 16);
            offset += 16;
            var cipher = protectedBytes.AsSpan(offset);
            var plain = new byte[cipher.Length];

            using var aes = new AesGcm(_masterKey.Value, tag.Length);
            aes.Decrypt(nonce, cipher, tag, plain, GetAssociatedData(purpose));
            return plain;
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    private static byte[] LoadOrCreateMasterKey()
    {
        var account = Environment.UserName;
        var existing = TryReadKeychainPassword(ServiceName, account);
        if (!string.IsNullOrWhiteSpace(existing) &&
            TryDecodeKey(existing, out var key))
        {
            return key;
        }

        var generated = RandomNumberGenerator.GetBytes(32);
        var encoded = Convert.ToBase64String(generated);
        if (!TryWriteKeychainPassword(ServiceName, account, encoded))
            throw new InvalidOperationException("Unable to store Halerith encryption key in macOS Keychain.");

        return generated;
    }

    private static bool TryDecodeKey(string encoded, out byte[] key)
    {
        try
        {
            key = Convert.FromBase64String(encoded.Trim());
            return key.Length == 32;
        }
        catch
        {
            key = [];
            return false;
        }
    }

    private static byte[] GetAssociatedData(string purpose) =>
        Encoding.UTF8.GetBytes("HalerithAddonClient:" + purpose);

    private static string? TryReadKeychainPassword(string service, string account)
    {
        try
        {
            using var process = StartSecurityProcess([
                "find-generic-password",
                "-s", service,
                "-a", account,
                "-w",
            ]);
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            return process.ExitCode == 0 ? output.Trim() : null;
        }
        catch
        {
            return null;
        }
    }

    private static bool TryWriteKeychainPassword(string service, string account, string password)
    {
        try
        {
            using var process = StartSecurityProcess([
                "add-generic-password",
                "-s", service,
                "-a", account,
                "-w", password,
                "-U",
            ]);
            process.WaitForExit();
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static Process StartSecurityProcess(string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "/usr/bin/security",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        return Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start macOS security tool.");
    }
}
