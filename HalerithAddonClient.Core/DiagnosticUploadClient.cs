using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace HalerithAddonClient.Core;

public sealed record UploadResult(bool Success, string? ErrorMessage = null)
{
    public static UploadResult Ok() => new(true);
    public static UploadResult Fail(string? message) => new(false, message);
}

/// <summary>Uploads an explicitly requested diagnostics package independently of billing.</summary>
public static class DiagnosticUploadClient
{
    private const long MaxZipBytes = 25L * 1024 * 1024;

    public static async Task<UploadResult> UploadAsync(
        string zipPath, string filename, string? notes, CancellationToken cancellationToken = default)
    {
        try
        {
            var file = new FileInfo(zipPath);
            if (!file.Exists || file.Length > MaxZipBytes)
                return UploadResult.Fail("Diagnostic ZIP is missing or exceeds the 25 MB upload limit.");

            await using var hashStream = File.OpenRead(zipPath);
            var sha256 = Convert.ToHexString(await SHA256.HashDataAsync(hashStream, cancellationToken))
                .ToLowerInvariant();
            var deviceId = DeviceIdProvider.GetDeviceId();
            using var form = new MultipartFormDataContent();
            form.Add(new StringContent(deviceId), "device_id");
            form.Add(new StringContent(sha256), "file_sha256");
            if (!string.IsNullOrWhiteSpace(notes)) form.Add(new StringContent(notes), "notes");
            var stream = File.OpenRead(zipPath);
            var content = new StreamContent(stream);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
            form.Add(content, "file", filename);

            using var request = new HttpRequestMessage(HttpMethod.Post,
                HalerithServiceEndpoints.ClientApiBaseUrl + HalerithServiceEndpoints.Client.ReportsPath)
            { Content = form };
            var canonical = string.Join('\n', "report", filename, sha256, deviceId, "", "");
            ClientRequestSigner.SignCanonical(request, canonical, DeviceIdProvider.GetDeviceIdHash());
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
            using var response = await client.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            using var result = JsonDocument.Parse(body);
            if (response.IsSuccessStatusCode && result.RootElement.TryGetProperty("success", out var success)
                && success.ValueKind == JsonValueKind.True)
                return UploadResult.Ok();
            return UploadResult.Fail(result.RootElement.TryGetProperty("error", out var error)
                ? error.GetString() : $"Server returned HTTP {(int)response.StatusCode}.");
        }
        catch (OperationCanceledException)
        {
            return UploadResult.Fail("Upload timed out or was cancelled.");
        }
        catch (Exception ex)
        {
            return UploadResult.Fail(ex.Message);
        }
    }
}
