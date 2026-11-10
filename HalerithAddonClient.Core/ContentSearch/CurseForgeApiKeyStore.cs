namespace HalerithAddonClient.Core.ContentSearch;

using System.Reflection;

/// <summary>Resolves the app's CurseForge key before falling back to the user's protected local key.</summary>
public static class CurseForgeApiKeyStore
{
    private const string StorageKey = "curseforge_api_key";

    public static string EmbeddedKey => typeof(CurseForgeApiKeyStore).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>()
        .FirstOrDefault(attribute => string.Equals(attribute.Key, "CurseForgeApiKey", StringComparison.Ordinal))
        ?.Value?.Trim() ?? "";

    public static bool HasEmbeddedKey => !string.IsNullOrWhiteSpace(EmbeddedKey);
    public static bool HasEffectiveKey => GetEffectiveKey().Length > 0;

    public static string? LoadUserKey() => AppStorageDatabase.LoadSecureString(StorageKey)?.Trim();

    public static string GetEffectiveKey()
    {
        var embedded = EmbeddedKey;
        return embedded.Length > 0 ? embedded : SelectEffectiveKey(embedded, LoadUserKey());
    }

    public static string SelectEffectiveKey(string? embeddedKey, string? userKey) =>
        !string.IsNullOrWhiteSpace(embeddedKey) ? embeddedKey.Trim() : userKey?.Trim() ?? "";

    public static void SaveUserKey(string key)
    {
        var value = key.Trim();
        if (value.Length == 0) throw new ArgumentException("Enter a CurseForge API key.", nameof(key));
        AppStorageDatabase.SaveSecureString(StorageKey, value);
    }

    public static void ClearUserKey() => AppStorageDatabase.ClearSecureItem(StorageKey);
}
