namespace HalerithAddonClient.Core;

/// <summary>
/// Represents a signed-in Microsoft/Minecraft account.
/// Stored globally (not per-host) — any server can use any account.
/// </summary>
public sealed class MinecraftAccount
{
    /// <summary>Minecraft profile UUID (e.g. "a1b2c3d4-...").</summary>
    public string Uuid { get; set; } = "";

    /// <summary>Minecraft gamertag / in-game name.</summary>
    public string Gamertag { get; set; } = "";

    /// <summary>Xbox user id returned by XSTS, when available.</summary>
    public string Xuid { get; set; } = "";

    /// <summary>Microsoft OAuth access token (for token refresh).</summary>
    public string MsAccessToken { get; set; } = "";

    /// <summary>Microsoft OAuth refresh token (long-lived).</summary>
    public string MsRefreshToken { get; set; } = "";

    /// <summary>Minecraft access token used for game launch authentication.</summary>
    public string McAccessToken { get; set; } = "";

    /// <summary>UTC time when <see cref="McAccessToken"/> expires.</summary>
    public DateTime ExpiresAtUtc { get; set; }

    /// <summary>Whether the Minecraft access token has expired.</summary>
    public bool IsExpired => DateTime.UtcNow >= ExpiresAtUtc;

    /// <summary>Display string for UI dropdowns.</summary>
    public override string ToString() => Gamertag;
}
