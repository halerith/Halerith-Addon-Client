namespace HalerithAddonClient.Core;

/// <summary>
/// Persists <see cref="MinecraftAccount"/> entries to SQLite using the platform secret protector
/// encryption for token fields.
/// </summary>
public static class AccountStore
{
    // ══════════════════════════════════════════════════════════════════════
    //  Read
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>Loads all saved accounts from app storage.</summary>
    public static List<MinecraftAccount> LoadAll()
    {
        return AppStorageDatabase.LoadMinecraftAccounts();
    }

    /// <summary>Returns the UUID of the global default launcher account, or empty.</summary>
    public static string GetDefaultAccountId()
    {
        return AppStorageDatabase.GetDefaultMinecraftAccountId();
    }

    // ══════════════════════════════════════════════════════════════════════
    //  Write
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>Saves or updates an account in app storage.</summary>
    public static void Save(MinecraftAccount account)
    {
        AppStorageDatabase.SaveMinecraftAccount(account);
    }

    /// <summary>Sets the global default launcher account UUID.</summary>
    public static void SetDefaultAccountId(string uuid)
    {
        AppStorageDatabase.SetDefaultMinecraftAccountId(uuid);
    }

    /// <summary>Removes an account from app storage.</summary>
    public static void Remove(string uuid)
    {
        AppStorageDatabase.RemoveMinecraftAccount(uuid);
        try { ManagedAccessDisplayStore.Remove(uuid); }
        catch { /* Account removal must not fail because a display cache cannot be cleared. */ }
    }
}
