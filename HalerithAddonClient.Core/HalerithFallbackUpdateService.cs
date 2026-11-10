using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Text.Json.Serialization;
using HalerithAddonClient.Core.Platform;

namespace HalerithAddonClient.Core;

public sealed class HalerithFallbackUpdateService : IDisposable
{
    public const string DefaultUpdateCheckUrl = "https://update.halerith.com/api/client-update-check.php";

    private readonly HttpClient _httpClient;
    private readonly string _updateCheckUrl;
    private string? _expectedSha256;

    public HalerithFallbackUpdateService(HttpClient? httpClient = null, string? updateCheckUrl = null)
    {
        _httpClient = httpClient ?? new HttpClient();
        _updateCheckUrl = string.IsNullOrWhiteSpace(updateCheckUrl)
            ? DefaultUpdateCheckUrl
            : updateCheckUrl.Trim();
    }

    public async Task<HealthLauncherInfo?> CheckAsync(string clientVersion, CancellationToken ct)
    {
        var endpoint = HttpsUrlPolicy.RequireHttpsAbsoluteUri(_updateCheckUrl, "Halerith update check URL");
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = JsonContent.Create(new UpdateCheckRequest
            {
                ClientVersion = clientVersion,
                Platform = PlatformServices.Current.Updates.Platform,
                Arch = PlatformServices.Current.Updates.Architecture,
            }, options: SerializerOptions.Default)
        };

        request.Headers.UserAgent.ParseAdd($"HalerithAddonClient/{clientVersion}");

        using var response = await _httpClient.SendAsync(request, ct).ConfigureAwait(false);
        var result = await response.Content.ReadFromJsonAsync<UpdateCheckResponse>(SerializerOptions.Default, ct)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode || result is null || !result.Success)
            return null;

        _expectedSha256 = string.IsNullOrWhiteSpace(result.Sha256)
            ? null
            : result.Sha256.Trim();

        return new HealthLauncherInfo
        {
            Version = result.Version,
            DownloadUrl = result.DownloadUrl,
            Filename = result.Filename,
            PackageType = result.PackageType,
            Sha256 = result.Sha256,
            LinuxManifest = result.LinuxManifest,
            LinuxSignature = result.LinuxSignature,
            AppcastUrl = result.AppcastUrl,
            FileAvailable = result.FileAvailable,
            UpdateAvailable = result.UpdateAvailable,
        };
    }

    public async Task<bool> RunUpdateIfAvailableAsync(RuntimeConfig config, HealthLauncherInfo? launcher, CancellationToken ct, string? relaunchArgs = null)
    {
        if (launcher is null || !launcher.UpdateAvailable || !launcher.FileAvailable || string.IsNullOrWhiteSpace(launcher.DownloadUrl))
            return false;

        return await UpdateCoordinator.RunHelperIfNeededAsync(
            DownloadPackageAsync,
            config,
            launcher,
            launcher.DownloadUrl,
            ct,
            relaunchArgs).ConfigureAwait(false);
    }

    private async Task DownloadPackageAsync(string url, string destinationPath, CancellationToken ct, long? maxBytes)
    {
        var uri = HttpsUrlPolicy.RequireHttpsAbsoluteUri(url, "Halerith update download URL");
        if (!IsTrustedHalerithHost(uri))
            throw new InvalidOperationException("Update download URL is not hosted by Halerith.");

        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        if (maxBytes is > 0 && response.Content.Headers.ContentLength is > 0 &&
            response.Content.Headers.ContentLength.Value > maxBytes.Value)
        {
            throw new InvalidOperationException("Update package is larger than the allowed limit.");
        }

        var targetDir = Path.GetDirectoryName(destinationPath)
            ?? throw new InvalidOperationException($"Destination path has no parent directory: {destinationPath}");
        Directory.CreateDirectory(targetDir);

        await using (var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
        await using (var destination = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            await CopyToAsync(source, destination, maxBytes, ct).ConfigureAwait(false);
        }

        if (!string.IsNullOrWhiteSpace(_expectedSha256))
            await SafeFileHelpers.VerifyExpectedHashAsync(destinationPath, _expectedSha256, ct).ConfigureAwait(false);

        // Windows additionally authenticates every EXE through WindowsUpdateTrust.
        // Linux verifies the signed manifest in staging and again inside its privileged helper.
    }

    private static async Task CopyToAsync(Stream source, Stream destination, long? maxBytes, CancellationToken ct)
    {
        var buffer = new byte[81920];
        long totalBytes = 0;

        while (true)
        {
            var read = await source.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (read == 0)
                return;

            totalBytes += read;
            if (maxBytes is > 0 && totalBytes > maxBytes.Value)
                throw new InvalidOperationException("Update package is larger than the allowed limit.");

            await destination.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
        }
    }

    private static bool IsTrustedHalerithHost(Uri uri)
        => string.Equals(uri.Host, "halerith.com", StringComparison.OrdinalIgnoreCase)
        || string.Equals(uri.Host, "activate.halerith.com", StringComparison.OrdinalIgnoreCase)
        || uri.Host.EndsWith(".halerith.com", StringComparison.OrdinalIgnoreCase);

    public void Dispose() => _httpClient.Dispose();

    private sealed class UpdateCheckRequest
    {
        [JsonPropertyName("client_version")] public string ClientVersion { get; init; } = "";
        [JsonPropertyName("platform")] public string Platform { get; init; } = "";
        [JsonPropertyName("arch")] public string Arch { get; init; } = "";
    }

    private sealed class UpdateCheckResponse
    {
        [JsonPropertyName("ok")] public bool Ok { get; init; }
        [JsonPropertyName("success")] public bool SuccessValue { get; init; }
        [JsonPropertyName("update_available")] public bool UpdateAvailable { get; init; }
        [JsonPropertyName("file_available")] public bool FileAvailable { get; init; }
        [JsonPropertyName("version")] public string? Version { get; init; }
        [JsonPropertyName("download_url")] public string? DownloadUrl { get; init; }
        [JsonPropertyName("appcast_url")] public string? AppcastUrl { get; init; }
        [JsonPropertyName("filename")] public string? Filename { get; init; }
        [JsonPropertyName("package_type")] public string? PackageType { get; init; }
        [JsonPropertyName("linux_manifest")] public string? LinuxManifest { get; init; }
        [JsonPropertyName("linux_signature")] public string? LinuxSignature { get; init; }

        [JsonPropertyName("sha256")] public string? Sha256 { get; init; }

        public bool Success => Ok || SuccessValue;
    }
}
