using System.Text.Json;

namespace HalerithAddonClient.Core;

/// <summary>Last known, display-only managed access state for a verified Minecraft UUID.</summary>
public static class ManagedAccessDisplayStore
{
    private const string KeyPrefix = "managed_access_display_v2:";
    private const string LegacyKeyPrefix = "managed_access_display_v1:";

    public sealed class Snapshot
    {
        public string Mode { get; set; } = "";
        public string? TrialEndsAt { get; set; }
        public string? PaidAccessEndsAt { get; set; }
        public string? GrantSource { get; set; }
        public string? NextChangeAt { get; set; }
        public bool CanChangeNow { get; set; }
        public string? FreeServerPublicId { get; set; }
        public string? FreeServerName { get; set; }
        public string? LinkedHalerithEmail { get; set; }
        public DateTimeOffset ObservedAtUtc { get; set; }
    }

    public static Snapshot? Load(string minecraftUuid)
    {
        var uuid = MinecraftProfileClient.NormalizeUuid(minecraftUuid);
        if (uuid is null) return null;
        try
        {
            var json = AppStorageDatabase.LoadSecureString(KeyPrefix + uuid);
            if (string.IsNullOrWhiteSpace(json))
                json = AppStorageDatabase.GetAppSetting(LegacyKeyPrefix + uuid);
            var snapshot = string.IsNullOrWhiteSpace(json)
                ? null : JsonSerializer.Deserialize<Snapshot>(json, SerializerOptions.Default);
            return snapshot is { Mode: "new" or "trial" or "free" or "paid" } ? snapshot : null;
        }
        catch { return null; }
    }

    public static void Save(string minecraftUuid, ManagedAccessStatusResponse status, string? freeServerName = null)
    {
        var uuid = MinecraftProfileClient.NormalizeUuid(minecraftUuid);
        if (uuid is null || !status.Ok || status.Mode is not ("new" or "trial" or "free" or "paid")) return;
        var snapshot = new Snapshot
        {
            Mode = status.Mode,
            TrialEndsAt = status.TrialEndsAt,
            PaidAccessEndsAt = status.PaidAccessEndsAt,
            GrantSource = status.GrantSource,
            NextChangeAt = status.NextChangeAt,
            CanChangeNow = status.CanChangeNow,
            FreeServerPublicId = status.Mode == "free" ? status.ServerPublicIds.FirstOrDefault() : null,
            FreeServerName = status.Mode == "free" ? freeServerName : null,
            LinkedHalerithEmail = status.LinkedHalerithEmail,
            ObservedAtUtc = DateTimeOffset.UtcNow
        };
        AppStorageDatabase.SaveSecureString(KeyPrefix + uuid,
            JsonSerializer.Serialize(snapshot, SerializerOptions.Default));
        AppStorageDatabase.DeleteAppSetting(LegacyKeyPrefix + uuid);
    }

    public static void Remove(string minecraftUuid)
    {
        var uuid = MinecraftProfileClient.NormalizeUuid(minecraftUuid);
        if (uuid is null) return;
        AppStorageDatabase.ClearSecureItem(KeyPrefix + uuid);
        AppStorageDatabase.DeleteAppSetting(LegacyKeyPrefix + uuid);
    }
}
