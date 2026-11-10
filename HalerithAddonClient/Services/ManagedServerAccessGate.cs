using HalerithAddonClient.Core;

namespace HalerithAddonClient.Services;

/// <summary>Checks the selected Minecraft account against a fresh, server-filtered Manager roster.</summary>
internal static class ManagedServerAccessGate
{
    private static readonly Dictionary<string, ManagedAccessStatusResponse> CachedByAccount = new(StringComparer.OrdinalIgnoreCase);

    public static ManagedAccessStatusResponse? CachedFor(string minecraftUuid)
    {
        var uuid = MinecraftProfileClient.NormalizeUuid(minecraftUuid);
        if (uuid is null) return null;
        lock (CachedByAccount)
        {
            if (CachedByAccount.TryGetValue(uuid, out var value)) return value;
            var saved = ManagedAccessDisplayStore.Load(uuid);
            if (saved is null) return null;
            value = new ManagedAccessStatusResponse
            {
                Ok = true, Mode = saved.Mode, TrialEndsAt = saved.TrialEndsAt,
                PaidAccessEndsAt = saved.PaidAccessEndsAt, GrantSource = saved.GrantSource,
                NextChangeAt = saved.NextChangeAt, CanChangeNow = saved.CanChangeNow,
                LinkedHalerithEmail = saved.LinkedHalerithEmail,
                ServerPublicIds = saved.FreeServerPublicId is { Length: > 0 } ? [saved.FreeServerPublicId] : []
            };
            CachedByAccount[uuid] = value;
            return value;
        }
    }

    public static void Forget(string minecraftUuid)
    {
        var uuid = MinecraftProfileClient.NormalizeUuid(minecraftUuid);
        if (uuid is null) return;
        lock (CachedByAccount)
        {
            CachedByAccount.Remove(uuid);
        }
    }

    public static void Record(HealthResponse health)
    {
        var uuid = MinecraftProfileClient.NormalizeUuid(health.Minecraft?.Uuid);
        if (!health.Ok || uuid is null) return;
        var access = health.FreeAccess;
        var hasKnownStatus = access?.Ok == true &&
            access.Mode is "new" or "trial" or "free" or "paid";
        if (hasKnownStatus && access!.LinkedHalerithAccount == true &&
            string.IsNullOrWhiteSpace(access.LinkedHalerithEmail))
            access.LinkedHalerithEmail = CachedFor(uuid)?.LinkedHalerithEmail;
        lock (CachedByAccount)
        {
            if (hasKnownStatus) CachedByAccount[uuid] = access!;
            else CachedByAccount.Remove(uuid);
        }
        try
        {
            if (hasKnownStatus) ManagedAccessDisplayStore.Save(uuid, access!, FreeServerName(health, access!));
            else ManagedAccessDisplayStore.Remove(uuid);
        }
        catch { /* A display cache failure must not change server authorization. */ }
    }

    public static void RecordManifest(ManifestResponse manifest, string expectedMinecraftUuid)
    {
        var expected = MinecraftProfileClient.NormalizeUuid(expectedMinecraftUuid);
        if (!manifest.Ok || !manifest.Authorized || manifest.FreeAccess?.Ok != true || expected is null ||
            !string.Equals(MinecraftProfileClient.NormalizeUuid(manifest.MinecraftUuid), expected,
                StringComparison.OrdinalIgnoreCase))
            return;

        var server = manifest.Server;
        Record(new HealthResponse
        {
            Ok = true,
            Minecraft = new HealthMinecraftInfo { Uuid = expected },
            FreeAccess = manifest.FreeAccess,
            Environments = server is null ? [] :
            [new HealthEnvironmentEntry
            {
                AccessActive = true,
                Servers = [new HealthServerEntry
                {
                    ServerPublicId = server.ServerPublicId,
                    Name = server.Name,
                    AccessActive = true
                }]
            }]
        });
    }

    private static string? FreeServerName(HealthResponse health, ManagedAccessStatusResponse access)
    {
        if (access.Mode != "free") return null;
        return health.Environments.Where(environment => environment.AccessActive)
            .SelectMany(environment => environment.Servers)
            .FirstOrDefault(server => server.AccessActive && access.ServerPublicIds.Contains(
                server.ServerPublicId, StringComparer.OrdinalIgnoreCase))?.Name;
    }

    internal static bool HasAuthorizedServer(HealthResponse health, string minecraftUuid, string serverPublicId)
    {
        var uuid = MinecraftProfileClient.NormalizeUuid(minecraftUuid);
        return health.Ok && uuid is not null &&
            string.Equals(MinecraftProfileClient.NormalizeUuid(health.Minecraft?.Uuid), uuid, StringComparison.OrdinalIgnoreCase) &&
            health.Environments.Where(environment => environment.AccessActive)
                .SelectMany(environment => environment.Servers)
                .Any(server => server.AccessActive &&
                    string.Equals(server.ServerPublicId, serverPublicId, StringComparison.OrdinalIgnoreCase));
    }

    public static async Task<ManagedAccessCheckResult> EnsureAsync(
        RuntimeConfig config, string serverPublicId, string minecraftUuid, CancellationToken ct = default)
    {
        var expectedUuid = MinecraftProfileClient.NormalizeUuid(minecraftUuid);
        if (expectedUuid is null)
            return Block("Select a Minecraft account before using a managed server.");

        var account = AccountStore.LoadAll().FirstOrDefault(a =>
            string.Equals(MinecraftProfileClient.NormalizeUuid(a.Uuid), expectedUuid, StringComparison.OrdinalIgnoreCase));
        if (account is null)
            return Block("Sign in to the selected Minecraft account to verify server access.");
        if (account.IsExpired || string.IsNullOrWhiteSpace(account.McAccessToken))
        {
            try
            {
                using var auth = new MicrosoftAuthService();
                account = await auth.RefreshAsync(account);
                AccountStore.Save(account);
            }
            catch { return Block("Sign in to the selected Minecraft account to verify server access."); }
        }
        config.MinecraftUuid = expectedUuid;
        config.MinecraftAccessToken = account.McAccessToken;
        // A Manager bearer token can belong to a previously selected account.
        config.HalerithAccessToken = "";
        config.HalerithAccessTokenExpiresAtUtc = "";

        using var api = new ApiClient(config, serverPublicId);
        HealthResponse health;
        try { health = await api.GetHostHealthAsync(ct); }
        catch { return Block("Connection to Halerith was interrupted. Managed access could not be verified."); }

        if (!HasAuthorizedServer(health, expectedUuid, serverPublicId))
            return Block("This server is not available to the selected Minecraft account. Refresh the server list.");
        Record(health); // Subscription state is display-only.
        return ManagedAccessCheckResult.Allow();
    }

    private static ManagedAccessCheckResult Block(string message) =>
        ManagedAccessCheckResult.Block(message);
}
