using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace HalerithAddonClient.Core;

/// <summary>
/// Signs Client EULA, privacy and diagnostic requests with a per-user ECDSA key.
/// The private key is stored in SQLite using the platform secret protector; the website
/// stores the public key for the hashed device ID and rejects mismatched or
/// replayed requests.
/// </summary>
public static class ClientRequestSigner
{
    private const string PrivateKeySecureItem = "client_identity.ecdsa_p256_private_key";
    // Preserve signing identity for existing installations during the key-name migration.
    private const string LegacyPrivateKeySecureItem = "ad_identity.ecdsa_p256_private_key";

    private static readonly Lazy<Identity> Current = new(LoadOrCreateIdentity);
    private static readonly object SigningLock = new();

    public static void Sign(HttpRequestMessage request, byte[] bodyBytes, string hwid)
        => SignCanonical(request, bodyBytes, hwid);

    public static void SignCanonical(HttpRequestMessage request, string canonicalPayload, string hwid)
    {
        ArgumentNullException.ThrowIfNull(canonicalPayload);
        SignCanonical(request, Encoding.UTF8.GetBytes(canonicalPayload), hwid);
    }

    private static void SignCanonical(HttpRequestMessage request, byte[] payloadBytes, string hwid)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(payloadBytes);

        var identity = Current.Value;
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        var nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(18));

        var signingBytes = BuildSigningBytes(timestamp, nonce, payloadBytes);
        byte[] signature;
        lock (SigningLock)
        {
            signature = identity.Key.SignData(
                signingBytes,
                HashAlgorithmName.SHA256,
                DSASignatureFormat.Rfc3279DerSequence);
        }

        SetHeader(request, "X-Client-Hwid", hwid);
        SetHeader(request, "X-Client-Timestamp", timestamp);
        SetHeader(request, "X-Client-Nonce", nonce);
        SetHeader(request, "X-Client-Public-Key", identity.PublicKeyBase64);
        SetHeader(request, "X-Client-Signature", Convert.ToBase64String(signature));
    }

    private static void SetHeader(HttpRequestMessage request, string name, string value)
    {
        request.Headers.Remove(name);
        request.Headers.TryAddWithoutValidation(name, value);
    }

    private static byte[] BuildSigningBytes(string timestamp, string nonce, byte[] bodyBytes)
    {
        var prefix = Encoding.UTF8.GetBytes($"{timestamp}\n{nonce}\n");
        var data = new byte[prefix.Length + bodyBytes.Length];
        Buffer.BlockCopy(prefix, 0, data, 0, prefix.Length);
        Buffer.BlockCopy(bodyBytes, 0, data, prefix.Length, bodyBytes.Length);
        return data;
    }

    private static Identity LoadOrCreateIdentity()
    {
        var storedPrivateKey = AppStorageDatabase.LoadSecureItem(PrivateKeySecureItem);
        var fromLegacy = storedPrivateKey is not { Length: > 0 };
        if (fromLegacy)
            storedPrivateKey = AppStorageDatabase.LoadSecureItem(LegacyPrivateKeySecureItem);
        if (storedPrivateKey is { Length: > 0 })
        {
            try
            {
                var ecdsa = ECDsa.Create();
                ecdsa.ImportPkcs8PrivateKey(storedPrivateKey, out _);
                if (fromLegacy)
                {
                    AppStorageDatabase.SaveSecureItem(PrivateKeySecureItem, storedPrivateKey);
                    AppStorageDatabase.ClearSecureItem(LegacyPrivateKeySecureItem);
                }
                return new Identity(ecdsa);
            }
            catch (CryptographicException)
            {
                AppStorageDatabase.ClearSecureItem(fromLegacy ? LegacyPrivateKeySecureItem : PrivateKeySecureItem);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(storedPrivateKey);
            }
        }

        var created = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var privateBytes = created.ExportPkcs8PrivateKey();
        AppStorageDatabase.SaveSecureItem(PrivateKeySecureItem, privateBytes);
        CryptographicOperations.ZeroMemory(privateBytes);
        return new Identity(created);
    }

    private sealed class Identity
    {
        public Identity(ECDsa key)
        {
            Key = key;
            PublicKeyBase64 = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
        }

        public ECDsa Key { get; }
        public string PublicKeyBase64 { get; }
    }
}
