using System.Text.Json;

namespace HalerithAddonClient.Core;

public sealed class MinecraftProfileClient : IDisposable
{
    private readonly HttpClient _httpClient;

    public MinecraftProfileClient()
    {
        _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(10),
        };
        _httpClient.DefaultRequestHeaders.UserAgent.TryParseAdd("HalerithAddonClient/1.0");
    }

    public async Task<MinecraftProfile?> ResolveUsernameAsync(string username, CancellationToken ct)
    {
        username = (username ?? "").Trim();
        if (!IsValidUsername(username))
            return null;

        using var response = await _httpClient.GetAsync(
            $"https://api.mojang.com/users/profiles/minecraft/{Uri.EscapeDataString(username)}",
            ct).ConfigureAwait(false);

        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            return null;

        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        return await ParseProfileAsync(stream, ct).ConfigureAwait(false);
    }

    public async Task<MinecraftProfile?> ResolveUuidAsync(string uuid, CancellationToken ct)
    {
        uuid = NormalizeUuid(uuid) ?? "";
        if (uuid.Length == 0)
            return null;

        using var response = await _httpClient.GetAsync(
            $"https://api.mojang.com/user/profile/{uuid}",
            ct).ConfigureAwait(false);

        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            return null;

        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        return await ParseProfileAsync(stream, ct).ConfigureAwait(false);
    }

    public static string? NormalizeUuid(string? uuid)
    {
        var value = (uuid ?? "").Trim().Replace("-", "", StringComparison.Ordinal).ToLowerInvariant();
        if (value.Length != 32)
            return null;

        return value.All(Uri.IsHexDigit) ? value : null;
    }

    private static bool IsValidUsername(string username)
    {
        if (username.Length is < 3 or > 16)
            return false;

        return username.All(ch => char.IsAsciiLetterOrDigit(ch) || ch == '_');
    }

    private static async Task<MinecraftProfile?> ParseProfileAsync(Stream stream, CancellationToken ct)
    {
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
        var root = doc.RootElement;

        if (!root.TryGetProperty("id", out var idEl) || !root.TryGetProperty("name", out var nameEl))
            return null;

        var uuid = NormalizeUuid(idEl.GetString());
        var name = (nameEl.GetString() ?? "").Trim();

        return uuid is null || name.Length == 0 ? null : new MinecraftProfile(uuid, name);
    }

    public void Dispose() => _httpClient.Dispose();
}

public sealed record MinecraftProfile(string Uuid, string Name);
