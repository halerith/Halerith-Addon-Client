using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace HalerithAddonClient.Core;

public sealed class HalerithEulaClient : IDisposable
{
    private readonly HttpClient _httpClient;

    public HalerithEulaClient(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
    }

    public async Task<EulaDocument> ResolveRequiredClientDocumentAsync(CancellationToken cancellationToken = default)
    {
        var packaged = EulaDocument.PackagedClient();

        try
        {
            using var response = await _httpClient.PostAsJsonAsync(
                HalerithServiceEndpoints.Client.EulaUrl,
                new EulaCurrentRequest { Action = "current", Product = "client" },
                SerializerOptions.Default,
                cancellationToken).ConfigureAwait(false);

            var result = await response.Content.ReadFromJsonAsync<EulaCurrentResponse>(
                SerializerOptions.Default,
                cancellationToken).ConfigureAwait(false);

            var remote = result?.Document;
            if (!response.IsSuccessStatusCode || result?.Success != true || remote is null)
                return packaged;

            var remoteDoc = new EulaDocument(
                "client",
                remote.Version ?? "",
                remote.EffectiveDate ?? "",
                remote.Title ?? "End User License Agreement",
                remote.Html ?? "",
                remote.ContentHash ?? "",
                true) { MateriallyAdverse = remote.MateriallyAdverse, ChangeSummary = remote.ChangeSummary };

            if (string.IsNullOrWhiteSpace(remoteDoc.Version) || string.IsNullOrWhiteSpace(remoteDoc.Html))
                return packaged;

            return EulaStore.CompareVersions(remoteDoc.Version, packaged.Version) >= 0 ? remoteDoc : packaged;
        }
        catch
        {
            return packaged;
        }
    }

    public async Task PushClientAcceptanceAsync(EulaDocument document, CancellationToken cancellationToken = default)
    {
        var acceptance = EulaStore.GetAcceptedDocumentForUpload(document);
        if (acceptance is not null) await PushClientAcceptanceAsync(acceptance, cancellationToken).ConfigureAwait(false);
    }

    public async Task ReconcileDeletedAcceptanceAsync(EulaDocument document, CancellationToken cancellationToken = default)
    {
        var acceptance = EulaStore.GetAcceptedDocumentForUpload(document);
        if (acceptance is null || !EulaStore.WasUploaded(acceptance)) return;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            using var request = new HttpRequestMessage(HttpMethod.Post, HalerithServiceEndpoints.Client.EulaUrl)
            {
                Content = JsonContent.Create(new { action = "acceptance_status", product = "client", eula_version = acceptance.EulaVersion, content_hash = acceptance.ContentHash },
                    options: SerializerOptions.Default)
            };
            var body = await request.Content.ReadAsByteArrayAsync(timeout.Token).ConfigureAwait(false);
            ClientRequestSigner.Sign(request, body, DeviceIdProvider.GetDeviceIdHash());
            using var response = await _httpClient.SendAsync(request, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return;
            using var state = await System.Text.Json.JsonDocument.ParseAsync(
                await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false), cancellationToken: timeout.Token).ConfigureAwait(false);
            if (state.RootElement.TryGetProperty("success", out var success) && success.ValueKind == System.Text.Json.JsonValueKind.True
                && state.RootElement.TryGetProperty("accepted", out var accepted) && accepted.ValueKind == System.Text.Json.JsonValueKind.False)
                EulaStore.InvalidateDeletedServerAcceptance(acceptance);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch { /* Keep the local acceptance when the server cannot be checked. */ }
    }

    public async Task<EulaDocument?> ResolveAcceptedClientDocumentAsync(EulaAcceptanceRecord acceptance, CancellationToken cancellationToken = default)
    {
        var cached = EulaStore.CachedDocument(acceptance);
        if (cached is not null) return cached;
        var packaged = EulaDocument.PackagedClient();
        if (EulaStore.MatchesAcceptance(packaged, acceptance)) return packaged;
        var remote = await GetAcceptedVersionAsync(acceptance, cancellationToken).ConfigureAwait(false);
        if (remote is not null) EulaStore.CacheDocument(remote);
        return remote;
    }

    public async Task<EulaDocument?> GetAcceptedVersionAsync(EulaAcceptanceRecord acceptance, CancellationToken cancellationToken = default)
    {
        if (acceptance.Product != "client" || string.IsNullOrWhiteSpace(acceptance.ContentHash)) return null;
        try
        {
            using var response = await _httpClient.PostAsJsonAsync(HalerithServiceEndpoints.Client.EulaUrl,
                new { action = "version", product = "client", version = acceptance.EulaVersion }, SerializerOptions.Default, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;
            var result = await response.Content.ReadFromJsonAsync<EulaCurrentResponse>(SerializerOptions.Default, cancellationToken).ConfigureAwait(false);
            var remote = result?.Document;
            if (result?.Success != true || remote is null) return null;
            var document = new EulaDocument("client", remote.Version ?? "", remote.EffectiveDate ?? "", remote.Title ?? "End User License Agreement", remote.Html ?? "", remote.ContentHash ?? "", true);
            return EulaStore.MatchesAcceptance(document, acceptance) ? document : null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch { return null; }
    }

    public async Task PushClientAcceptanceAsync(EulaAcceptanceRecord acceptance, CancellationToken cancellationToken = default)
    {
        if (!string.Equals(acceptance.Product, "client", StringComparison.OrdinalIgnoreCase))
            return;

        // Retry older offline acceptances as well as the current version. A failed
        // delivery keeps its original local timestamp and is retried on launch.
        var history = EulaStore.GetAcceptanceHistory();
        foreach (var item in history.Where(item => item.Product == "client" && !EulaStore.WasUploaded(item)).OrderByDescending(item => item.AcceptedAtUtc).Take(10))
        {
            if (await SendAcceptanceAsync(item, cancellationToken).ConfigureAwait(false)) EulaStore.MarkUploaded(item);
            else break;
        }
    }

    private async Task<bool> SendAcceptanceAsync(
        EulaAcceptanceRecord acceptance,
        CancellationToken cancellationToken)
    {
        try
        {
            var payload = new EulaAcceptRequest
            {
                Action = "accept",
                Product = "client",
                EulaVersion = acceptance.EulaVersion,
                EffectiveDate = acceptance.EffectiveDate,
                ContentHash = acceptance.ContentHash,
                ClientReportedAt = acceptance.AcceptedAtUtc,
            };

            using var request = new HttpRequestMessage(HttpMethod.Post, HalerithServiceEndpoints.Client.EulaUrl)
            {
                Content = JsonContent.Create(payload, options: SerializerOptions.Default)
            };

            var bodyBytes = await request.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            ClientRequestSigner.Sign(request, bodyBytes, DeviceIdProvider.GetDeviceIdHash());

            using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            // Best-effort only; local acceptance remains authoritative for startup.
            return false;
        }
    }

    public void Dispose() => _httpClient.Dispose();

    private sealed class EulaCurrentRequest
    {
        [JsonPropertyName("action")] public string Action { get; set; } = "current";
        [JsonPropertyName("product")] public string Product { get; set; } = "client";
    }


    private sealed class EulaCurrentResponse
    {
        [JsonPropertyName("success")] public bool Success { get; set; }
        [JsonPropertyName("document")] public EulaDocumentDto? Document { get; set; }
    }

    private sealed class EulaDocumentDto
    {
        [JsonPropertyName("materially_adverse")] public bool MateriallyAdverse { get; set; }
        [JsonPropertyName("change_summary")] public string? ChangeSummary { get; set; }
        [JsonPropertyName("version")] public string? Version { get; set; }
        [JsonPropertyName("effective_date")] public string? EffectiveDate { get; set; }
        [JsonPropertyName("title")] public string? Title { get; set; }
        [JsonPropertyName("html")] public string? Html { get; set; }
        [JsonPropertyName("content_hash")] public string? ContentHash { get; set; }
    }

    private sealed class EulaAcceptRequest
    {
        [JsonPropertyName("action")] public string Action { get; set; } = "accept";
        [JsonPropertyName("product")] public string Product { get; set; } = "client";
        [JsonPropertyName("eula_version")] public string EulaVersion { get; set; } = "";
        [JsonPropertyName("effective_date")] public string EffectiveDate { get; set; } = "";
        [JsonPropertyName("content_hash")] public string ContentHash { get; set; } = "";
        [JsonPropertyName("client_reported_at")] public DateTimeOffset ClientReportedAt { get; set; }
    }
}
