using HalerithAddonClient.Core;

namespace HalerithAddonClient;

/// <summary>Presentation only. Never substitute these labels in authentication or persisted models.</summary>
public static class DemoMode
{
#if HALERITH_DEMO_MODE
    public static bool Enabled => true;
#else
    public static bool Enabled => false;
#endif
    public static string PlayerName(string? realName) => Enabled ? "Halerith" : realName ?? "";
    public static string Email(string realEmail) => Enabled ? "demo@halerith.com" : realEmail;
    public static bool Visible(ServerConfig server) => !Enabled || (server.ServerName?.Contains("Halerith", StringComparison.OrdinalIgnoreCase) ?? false);
    public static List<ServerConfig> VisibleServers(IEnumerable<ServerConfig> servers) => servers
        .Where(s => Enabled ? Visible(s) : s.IsAvailable).OrderBy(s => s.ServerName, StringComparer.OrdinalIgnoreCase)
        .ThenBy(s => s.ServerPublicId, StringComparer.OrdinalIgnoreCase).ToList();
    public static string ServerName(ServerConfig server, int index) => Enabled
        ? $"Demo Server {index + 1}"
        : string.IsNullOrWhiteSpace(server.ServerName) ? server.ServerPublicId : server.ServerName;
}
