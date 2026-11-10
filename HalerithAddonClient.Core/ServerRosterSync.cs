namespace HalerithAddonClient.Core;

/// <summary>
/// Applies a manager health server list to stored server config.
/// Existing MC/loader values are left as the installed/source-of-truth values,
/// and absent servers remain saved. A complete health response replaces only
/// the requesting Minecraft UUID's local display roster.
/// </summary>
public static class ServerRosterSync
{
    public static bool ApplyHealth(HostConfig host, HealthResponse health)
    {
        if (!health.Ok)
            return false;
        var authenticated = health.RosterComplete || health.Environments.Count > 0
                            || health.Servers.Count > 0;
        if (!authenticated)
            return false;

        var changed = false;
        var healthUuid = MinecraftProfileClient.NormalizeUuid(health.Minecraft?.Uuid ?? health.MinecraftUuid);
        if (healthUuid is null)
            return false;
        if (!string.Equals(host.MinecraftUuid, healthUuid, StringComparison.OrdinalIgnoreCase))
        {
            host.HalerithAccessToken = "";
            host.HalerithAccessTokenExpiresAtUtc = "";
            changed = true;
        }
        if (health.Auth is { AccessToken.Length: > 0 })
        {
            if (!string.Equals(host.HalerithAccessToken, health.Auth.AccessToken, StringComparison.Ordinal)
                || !string.Equals(host.HalerithAccessTokenExpiresAtUtc, health.Auth.ExpiresAt, StringComparison.Ordinal))
            {
                host.HalerithAccessToken = health.Auth.AccessToken;
                host.HalerithAccessTokenExpiresAtUtc = health.Auth.ExpiresAt;
                changed = true;
            }
        }

        if (!string.IsNullOrWhiteSpace(healthUuid)
            && !string.Equals(host.MinecraftUuid, healthUuid, StringComparison.OrdinalIgnoreCase))
        {
            host.MinecraftUuid = healthUuid;
            changed = true;
        }

        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (srv, environmentId, environmentName) in FlattenServers(health))
        {
            if (string.IsNullOrWhiteSpace(srv.ServerPublicId) || !seenIds.Add(srv.ServerPublicId))
                continue;

            var existing = host.Servers.FirstOrDefault(s =>
                string.Equals(s.ServerPublicId, srv.ServerPublicId, StringComparison.OrdinalIgnoreCase));

            if (existing is null)
            {
                var gameHost = srv.GameEndpoint?.Host ?? srv.NodeFqdn ?? "";
                var gamePort = srv.GameEndpoint?.Port ?? srv.Port;
                host.Servers.Add(new ServerConfig
                {
                    ServerPublicId = srv.ServerPublicId,
                    EnvironmentId = environmentId,
                    EnvironmentName = environmentName,
                    ServerName = srv.Name,
                    IsAvailable = true,
                    MinecraftVersion = srv.MinecraftVersion ?? "",
                    Loader = srv.Loader ?? "",
                    LoaderVersion = srv.LoaderVersion ?? "",
                    Motd = srv.Motd ?? "",
                    GameHost = gameHost,
                    Port = gamePort,
                    PowerStatus = ServerPowerStates.Normalize(srv.PowerStatus),
                    ServerIconUrl = srv.Icon?.DownloadUrl ?? "",
                    ServerIconSha256 = srv.Icon?.Sha256 ?? "",
                    ServerIconContentType = srv.Icon?.ContentType ?? "",
                });
                changed = true;
                continue;
            }

            if (!string.Equals(existing.EnvironmentId, environmentId, StringComparison.Ordinal))
            {
                existing.EnvironmentId = environmentId;
                changed = true;
            }

            if (!string.Equals(existing.EnvironmentName, environmentName, StringComparison.Ordinal))
            {
                existing.EnvironmentName = environmentName;
                changed = true;
            }

            if (!existing.IsAvailable)
            {
                existing.IsAvailable = true;
                changed = true;
            }

            if (!string.Equals(existing.ServerName, srv.Name, StringComparison.Ordinal))
            {
                existing.ServerName = srv.Name;
                changed = true;
            }

            // Connection info is refreshed from health, but MC/loader fields are
            // intentionally not overwritten; they represent the installed target.
            var refreshedHost = srv.GameEndpoint?.Host ?? srv.NodeFqdn;
            var refreshedPort = srv.GameEndpoint?.Port ?? srv.Port;
            if (!string.IsNullOrWhiteSpace(refreshedHost)
                && !string.Equals(existing.GameHost, refreshedHost, StringComparison.OrdinalIgnoreCase))
            {
                existing.GameHost = refreshedHost;
                changed = true;
            }
            if (refreshedPort.HasValue && existing.Port != refreshedPort)
            {
                existing.Port = refreshedPort;
                changed = true;
            }

            var motd = srv.Motd ?? "";
            if (!string.Equals(existing.Motd, motd, StringComparison.Ordinal))
            {
                existing.Motd = motd;
                changed = true;
            }

            var powerStatus = ServerPowerStates.Normalize(srv.PowerStatus);
            if (!string.Equals(existing.PowerStatus, powerStatus, StringComparison.Ordinal))
            {
                existing.PowerStatus = powerStatus;
                changed = true;
            }

            var iconUrl = srv.Icon?.DownloadUrl ?? "";
            var iconSha = srv.Icon?.Sha256 ?? "";
            var iconContentType = srv.Icon?.ContentType ?? "";
            if (!string.Equals(existing.ServerIconUrl, iconUrl, StringComparison.Ordinal) ||
                !string.Equals(existing.ServerIconSha256, iconSha, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(existing.ServerIconContentType, iconContentType, StringComparison.OrdinalIgnoreCase))
            {
                existing.ServerIconUrl = iconUrl;
                existing.ServerIconSha256 = iconSha;
                existing.ServerIconContentType = iconContentType;
                if (string.IsNullOrWhiteSpace(iconSha))
                    existing.ServerIconLocalPath = "";
                changed = true;
            }
        }

        if (health.RosterComplete)
        {
            var nextIds = seenIds.OrderBy(id => id, StringComparer.OrdinalIgnoreCase).ToList();
            if (!host.VisibleServerIdsByMinecraftUuid.TryGetValue(healthUuid, out var oldIds)
                || !oldIds.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(nextIds))
            {
                host.VisibleServerIdsByMinecraftUuid[healthUuid] = nextIds;
                changed = true;
            }
        }

        return host.AlignSecondaryRuntimeVersions() || changed;
    }

    private static IEnumerable<(HealthServerEntry Server, string EnvironmentId, string EnvironmentName)> FlattenServers(HealthResponse health)
    {
        if (health.Servers.Count > 0)
        {
            foreach (var server in health.Servers)
                yield return (server, "", "");
            yield break;
        }

        foreach (var environment in health.Environments)
        {
            foreach (var server in environment.Servers)
                yield return (server, environment.Id, environment.Name);
        }
    }
}
