using System.Net;

namespace HalerithAddonClient.Core;

public static class HostSslValidator
{
    private static readonly HttpClient Http = CreateHttpClient();

    public static async Task ValidateAsync(
        string apiBaseUrl,
        bool ignoreCertificateErrors = false,
        CancellationToken ct = default)
    {
        var uri = HttpsUrlPolicy.RequireHttpsAbsoluteUri(apiBaseUrl, "Manager API URL");
        if (ignoreCertificateErrors)
            return;

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            using var response = await Http.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new InvalidOperationException("Manager API SSL certificate validation timed out.");
        }
        catch (HttpRequestException ex)
        {
            throw new InvalidOperationException(
                $"Manager API SSL certificate validation failed for {uri.Host}: {ex.Message}",
                ex);
        }
    }

    private static HttpClient CreateHttpClient()
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
        };

        return new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(15)
        };
    }
}
