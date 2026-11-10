using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HalerithAddonClient.Core;

/// <summary>
/// Implements the full Microsoft → Xbox Live → XSTS → Minecraft authentication
/// chain using the OAuth 2.0 Device Code flow.
///
/// Flow:
///   1. Request device code from Microsoft OAuth
///   2. User navigates to https://microsoft.com/link and enters code
///   3. Poll for token grant
///   4. Exchange MS token for Xbox Live token (user authenticate)
///   5. Exchange Xbox Live token for XSTS token (authorise)
///   6. Exchange XSTS token for Minecraft access token
///   7. Fetch Minecraft profile (UUID + gamertag)
///
/// Token refresh:
///   MS refresh tokens are long-lived.  Call <see cref="RefreshAsync"/> to
///   re-run steps 3–7 using the stored refresh token.
/// </summary>
public sealed class MicrosoftAuthService : IDisposable
{
    // ── Azure AD app registration (public client, device code enabled) ──
    // App must be approved via https://aka.ms/mce-reviewappid before
    // api.minecraftservices.com will accept tokens from it.
    public const string ClientId = "928cf5c6-c582-4acc-9baa-db2f4e425c1f";

    // ── Endpoints ────────────────────────────────────────────────────────
    private const string MsDeviceCodeUrl   = "https://login.microsoftonline.com/consumers/oauth2/v2.0/devicecode";
    private const string MsTokenUrl        = "https://login.microsoftonline.com/consumers/oauth2/v2.0/token";
    private const string XblAuthUrl        = "https://user.auth.xboxlive.com/user/authenticate";
    private const string XstsAuthUrl       = "https://xsts.auth.xboxlive.com/xsts/authorize";
    private const string McAuthUrl         = "https://api.minecraftservices.com/authentication/login_with_xbox";
    private const string McProfileUrl      = "https://api.minecraftservices.com/minecraft/profile";

    private const string Scope = "XboxLive.signin offline_access";
    private const string UserAgent = "HalerithAddonClient/1.0";
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(60);

    private readonly HttpClient _http;
    private readonly bool _ownsHttpClient;

    public MicrosoftAuthService(HttpClient? httpClient = null)
    {
        _ownsHttpClient = httpClient is null;
        _http = ConfigureHttpClient(httpClient);
    }

    private static HttpClient ConfigureHttpClient(HttpClient? httpClient)
    {
        var http = httpClient ?? new HttpClient { Timeout = DefaultTimeout };

        if (!http.DefaultRequestHeaders.UserAgent.Any(x =>
                string.Equals(x.Product?.Name, "HalerithAddonClient", StringComparison.OrdinalIgnoreCase)))
        {
            http.DefaultRequestHeaders.UserAgent.TryParseAdd(UserAgent);
        }

        return http;
    }

    // ══════════════════════════════════════════════════════════════════════
    //  Step 1 — Request device code
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Initiates the device code flow.  Returns the device code response
    /// containing the user code and verification URI.
    /// </summary>
    public async Task<DeviceCodeResponse> RequestDeviceCodeAsync(CancellationToken ct = default)
    {
        var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = ClientId,
            ["scope"]     = Scope,
        });

        var resp = await _http.PostAsync(MsDeviceCodeUrl, content, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();

        return await resp.Content.ReadFromJsonAsync<DeviceCodeResponse>(SerializerOptions.Default, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Empty device code response.");
    }

    // ══════════════════════════════════════════════════════════════════════
    //  Step 2 — Poll for token grant
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Polls Microsoft until the user completes the device code login or
    /// the code expires.  Returns the MS token response.
    /// </summary>
    public async Task<MsTokenResponse> PollForTokenAsync(
        DeviceCodeResponse deviceCode,
        CancellationToken ct = default)
    {
        var interval = Math.Max(deviceCode.Interval, 5);

        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(interval), ct).ConfigureAwait(false);

            var content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"]  = "urn:ietf:params:oauth:grant-type:device_code",
                ["client_id"]   = ClientId,
                ["device_code"] = deviceCode.DeviceCode,
            });

            var resp = await _http.PostAsync(MsTokenUrl, content, ct).ConfigureAwait(false);

            if (resp.IsSuccessStatusCode)
            {
                return await resp.Content.ReadFromJsonAsync<MsTokenResponse>(SerializerOptions.Default, ct).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("Empty token response.");
            }

            var error = await resp.Content.ReadFromJsonAsync<MsErrorResponse>(SerializerOptions.Default, ct).ConfigureAwait(false);

            switch (error?.Error)
            {
                case "authorization_pending":
                    continue; // User hasn't entered the code yet

                case "slow_down":
                    interval += 5;
                    continue;

                case "expired_token":
                    throw new TimeoutException("The device code expired. Please try again.");

                default:
                    throw new InvalidOperationException(
                        $"Authentication failed: {error?.Error} — {error?.ErrorDescription}");
            }
        }

        throw new OperationCanceledException(ct);
    }

    // ══════════════════════════════════════════════════════════════════════
    //  Steps 3–7 — Exchange chain (MS → XBL → XSTS → MC → Profile)
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Runs the full exchange chain from an MS token to a Minecraft account.
    /// </summary>
    public async Task<MinecraftAccount> ExchangeAsync(MsTokenResponse msToken, CancellationToken ct = default)
    {
        // Step 3: Xbox Live
        var xblToken = await AuthenticateXboxLiveAsync(msToken.AccessToken, ct).ConfigureAwait(false);

        // Step 4: XSTS
        var xstsToken = await AuthorizeXstsAsync(xblToken, ct).ConfigureAwait(false);

        // Step 5: Minecraft
        var (mcToken, expiresIn) = await LoginMinecraftAsync(xstsToken, ct).ConfigureAwait(false);

        // Step 6: Profile
        var (uuid, gamertag) = await GetMinecraftProfileAsync(mcToken, ct).ConfigureAwait(false);

        return new MinecraftAccount
        {
            Uuid           = uuid,
            Gamertag       = gamertag,
            Xuid           = xstsToken.Xuid,
            MsAccessToken  = msToken.AccessToken,
            MsRefreshToken = msToken.RefreshToken ?? "",
            McAccessToken  = mcToken,
            ExpiresAtUtc   = DateTime.UtcNow.AddSeconds(expiresIn),
        };
    }

    // ══════════════════════════════════════════════════════════════════════
    //  Token refresh
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Refreshes an account's tokens using its stored MS refresh token.
    /// Returns the updated account (callers should persist via AccountStore).
    /// </summary>
    public async Task<MinecraftAccount> RefreshAsync(MinecraftAccount account, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(account.MsRefreshToken))
            throw new InvalidOperationException("No refresh token available — re-login required.");

        var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"]    = "refresh_token",
            ["client_id"]     = ClientId,
            ["scope"]         = Scope,
            ["refresh_token"] = account.MsRefreshToken,
        });

        var resp = await _http.PostAsync(MsTokenUrl, content, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();

        var msToken = await resp.Content.ReadFromJsonAsync<MsTokenResponse>(SerializerOptions.Default, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Empty refresh response.");

        var refreshed = await ExchangeAsync(msToken, ct).ConfigureAwait(false);

        // Preserve the original UUID (shouldn't change, but be safe)
        refreshed.Uuid = account.Uuid;
        // Microsoft normally rotates refresh tokens, but a valid refresh response
        // is also allowed to omit a replacement. Keep the previous long-lived token
        // in that case so the next application launch can refresh again.
        if (string.IsNullOrWhiteSpace(refreshed.MsRefreshToken))
            refreshed.MsRefreshToken = account.MsRefreshToken;
        return refreshed;
    }

    // ══════════════════════════════════════════════════════════════════════
    //  Xbox Live authentication
    // ══════════════════════════════════════════════════════════════════════

    private async Task<string> AuthenticateXboxLiveAsync(string msAccessToken, CancellationToken ct)
    {
        var body = new
        {
            Properties = new
            {
                AuthMethod = "RPS",
                SiteName   = "user.auth.xboxlive.com",
                RpsTicket  = $"d={msAccessToken}",
            },
            RelyingParty = "http://auth.xboxlive.com",
            TokenType    = "JWT",
        };

        var req = new HttpRequestMessage(HttpMethod.Post, XblAuthUrl)
        {
            Content = JsonContent.Create(body, options: SerializerOptions.Default),
        };
        req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            var body2 = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            throw new InvalidOperationException(
                $"Xbox Live authentication failed ({resp.StatusCode}): {body2}");
        }

        var json = await resp.Content.ReadFromJsonAsync<JsonElement>(SerializerOptions.Default, ct).ConfigureAwait(false);
        return json.GetProperty("Token").GetString()
            ?? throw new InvalidOperationException("XBL response missing Token.");
    }

    private async Task<XstsTokenResult> AuthorizeXstsAsync(string xblToken, CancellationToken ct)
    {
        var body = new
        {
            Properties = new
            {
                SandboxId  = "RETAIL",
                UserTokens = new[] { xblToken },
            },
            RelyingParty = "rp://api.minecraftservices.com/",
            TokenType    = "JWT",
        };

        var req = new HttpRequestMessage(HttpMethod.Post, XstsAuthUrl)
        {
            Content = JsonContent.Create(body, options: SerializerOptions.Default),
        };
        req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);

        if (!resp.IsSuccessStatusCode)
        {
            var errJson = await resp.Content.ReadFromJsonAsync<JsonElement>(SerializerOptions.Default, ct).ConfigureAwait(false);
            var xErr = errJson.TryGetProperty("XErr", out var xe) ? xe.GetUInt64() : 0UL;

            var message = xErr switch
            {
                2148916233 => "This Microsoft account has no Xbox account. Please create one at xbox.com first.",
                2148916235 => "Xbox Live is not available in your region.",
                2148916236 or 2148916237 => "This account requires adult verification. Please sign in at xbox.com first.",
                2148916238 => "This is a child account. A parent must add it to a Family first.",
                _ => $"XSTS authorization failed (XErr={xErr}).",
            };

            throw new InvalidOperationException(message);
        }

        var json = await resp.Content.ReadFromJsonAsync<JsonElement>(SerializerOptions.Default, ct).ConfigureAwait(false);
        var token = json.GetProperty("Token").GetString()
            ?? throw new InvalidOperationException("XSTS response missing Token.");

        var xui = json.GetProperty("DisplayClaims").GetProperty("xui")[0];
        var uhs = xui.GetProperty("uhs").GetString()
            ?? throw new InvalidOperationException("XSTS response missing user hash.");
        var xuid = xui.TryGetProperty("xid", out var xidProp)
            ? xidProp.GetString() ?? ""
            : "";

        return new XstsTokenResult(token, uhs, xuid);
    }

    // ══════════════════════════════════════════════════════════════════════
    //  Minecraft authentication
    // ══════════════════════════════════════════════════════════════════════

    private async Task<(string Token, int ExpiresIn)> LoginMinecraftAsync(XstsTokenResult xsts, CancellationToken ct)
    {
        var body = new { identityToken = $"XBL3.0 x={xsts.UserHash};{xsts.Token}" };

        var req = new HttpRequestMessage(HttpMethod.Post, McAuthUrl)
        {
            Content = JsonContent.Create(body, options: SerializerOptions.Default),
        };

        var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            var body2 = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            throw new InvalidOperationException(
                $"Minecraft login failed ({resp.StatusCode}): {body2}");
        }

        var json = await resp.Content.ReadFromJsonAsync<JsonElement>(SerializerOptions.Default, ct).ConfigureAwait(false);
        var token = json.GetProperty("access_token").GetString()
            ?? throw new InvalidOperationException("MC auth response missing access_token.");
        var expiresIn = json.GetProperty("expires_in").GetInt32();

        return (token, expiresIn);
    }

    private async Task<(string Uuid, string Gamertag)> GetMinecraftProfileAsync(string mcToken, CancellationToken ct)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, McProfileUrl);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", mcToken);

        var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);

        if (!resp.IsSuccessStatusCode)
        {
            var body2 = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            throw new InvalidOperationException(
                $"Minecraft profile fetch failed ({resp.StatusCode}): {body2}");
        }

        var json = await resp.Content.ReadFromJsonAsync<JsonElement>(SerializerOptions.Default, ct).ConfigureAwait(false);
        var uuid = json.GetProperty("id").GetString()
            ?? throw new InvalidOperationException("Profile response missing id.");
        var name = json.GetProperty("name").GetString()
            ?? throw new InvalidOperationException("Profile response missing name.");

        // Insert hyphens into the UUID if it's the compact 32-char form
        if (uuid.Length == 32 && !uuid.Contains('-'))
        {
            uuid = $"{uuid[..8]}-{uuid[8..12]}-{uuid[12..16]}-{uuid[16..20]}-{uuid[20..]}";
        }

        return (uuid, name);
    }

    public void Dispose()
    {
        if (_ownsHttpClient)
            _http.Dispose();
    }

    // ══════════════════════════════════════════════════════════════════════
    //  DTO types
    // ══════════════════════════════════════════════════════════════════════

    private sealed record XstsTokenResult(string Token, string UserHash, string Xuid);
}

// ══════════════════════════════════════════════════════════════════════════
//  Public DTO types (used by login UI)
// ══════════════════════════════════════════════════════════════════════════

public sealed class DeviceCodeResponse
{
    [JsonPropertyName("device_code")]
    public string DeviceCode { get; set; } = "";

    [JsonPropertyName("user_code")]
    public string UserCode { get; set; } = "";

    [JsonPropertyName("verification_uri")]
    public string VerificationUri { get; set; } = "";

    [JsonPropertyName("expires_in")]
    public int ExpiresIn { get; set; }

    [JsonPropertyName("interval")]
    public int Interval { get; set; } = 5;

    [JsonPropertyName("message")]
    public string Message { get; set; } = "";
}

public sealed class MsTokenResponse
{
    [JsonPropertyName("access_token")]
    public string AccessToken { get; set; } = "";

    [JsonPropertyName("refresh_token")]
    public string? RefreshToken { get; set; }

    [JsonPropertyName("expires_in")]
    public int ExpiresIn { get; set; }

    [JsonPropertyName("token_type")]
    public string TokenType { get; set; } = "";
}

public sealed class MsErrorResponse
{
    [JsonPropertyName("error")]
    public string? Error { get; set; }

    [JsonPropertyName("error_description")]
    public string? ErrorDescription { get; set; }
}
