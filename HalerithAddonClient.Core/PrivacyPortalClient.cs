using System.Net.Http.Json;
using System.Net;
using System.Text.Json.Serialization;

namespace HalerithAddonClient.Core;

public sealed class PrivacyPortalClient : IDisposable
{
    private readonly HttpClient _httpClient;

    public PrivacyPortalClient(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? new HttpClient();
    }

    public async Task<string?> CreatePortalUrlAsync(CancellationToken cancellationToken = default)
    {
        var payload = new CreateClaimRequest
        {
            Action = "create_claim",
            ClientVersion = EulaStore.GetAppVersion(),
        };
        var accounts = AccountStore.LoadAll();
        if (accounts.Count > 32)
            throw new InvalidOperationException("Too many saved Minecraft accounts for one data request.");
        using (var auth = new MicrosoftAuthService())
        {
            foreach (var saved in accounts)
            {
                var uuid = MinecraftProfileClient.NormalizeUuid(saved.Uuid);
                if (uuid is null || payload.MinecraftAccounts.Any(candidate => candidate.Uuid == uuid))
                    continue;
                var account = saved;
                if (account.IsExpired || string.IsNullOrWhiteSpace(account.McAccessToken))
                {
                    try
                    {
                        account = await auth.RefreshAsync(account, cancellationToken).ConfigureAwait(false);
                        AccountStore.Save(account);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                    catch (HttpRequestException ex) when (ex.StatusCode.HasValue && (int) ex.StatusCode.Value >= 400 && (int) ex.StatusCode.Value < 500) { continue; }
                    catch (HttpRequestException) { throw new InvalidOperationException("Minecraft account verification is temporarily unavailable. Please try again."); }
                    catch { continue; }
                }
                if (!account.IsExpired && !string.IsNullOrWhiteSpace(account.McAccessToken))
                    payload.MinecraftAccounts.Add(new MinecraftCredential(uuid, account.McAccessToken));
            }
        }
        payload.Scope = payload.MinecraftAccounts.Count == 0 ? "device_only" : "client_accounts";

        using var request = new HttpRequestMessage(HttpMethod.Post, HalerithServiceEndpoints.Client.PrivacyUrl)
        {
            Content = JsonContent.Create(payload, options: SerializerOptions.Default)
        };

        var bodyBytes = await request.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        ClientRequestSigner.Sign(request, bodyBytes, DeviceIdProvider.GetDeviceIdHash());

        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var result = await response.Content.ReadFromJsonAsync<CreateClaimResponse>(
            SerializerOptions.Default,
            cancellationToken).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.Unauthorized)
            throw new InvalidOperationException("This Client installation could not be verified. Contact privacy@halerith.com for help.");
        if (response.StatusCode == HttpStatusCode.Forbidden && payload.MinecraftAccounts.Count > 0)
            throw new InvalidOperationException("Minecraft account verification failed. Please try the request again.");
        if (!response.IsSuccessStatusCode || result?.Success != true || string.IsNullOrWhiteSpace(result.Url))
            return null;

        return result.Url;
    }

    public void Dispose() => _httpClient.Dispose();

    private sealed class CreateClaimRequest
    {
        [JsonPropertyName("action")] public string Action { get; set; } = "create_claim";
        [JsonPropertyName("client_version")] public string ClientVersion { get; set; } = "";
        [JsonPropertyName("minecraft_accounts")] public List<MinecraftCredential> MinecraftAccounts { get; set; } = [];
        [JsonPropertyName("scope")] public string Scope { get; set; } = "device_only";
    }

    private sealed record MinecraftCredential(
        [property: JsonPropertyName("minecraft_uuid")] string Uuid,
        [property: JsonPropertyName("minecraft_access_token")] string AccessToken);

    private sealed class CreateClaimResponse
    {
        [JsonPropertyName("success")] public bool Success { get; set; }
        [JsonPropertyName("url")] public string? Url { get; set; }
    }
}
