using System.Net;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using HalerithAddonClient.Core.Platform;

namespace HalerithAddonClient.Core;

public sealed class ApiClient : IDisposable
{
    private const string ManifestDebugDumpPrefix = "manifest";

    /// <summary>
    /// Runtime switch set by the main app's DebugBuild project-file setting.
    /// Kept runtime-gated so the Core project does not depend on transitive
    /// MSBuild constants from the UI project.
    /// </summary>
    public static bool ManifestDebugDumpEnabled { get; set; }

    private readonly HttpClient _httpClient;
    private readonly HttpClient _downloadHttpClient;
    private readonly string _baseUrl;
    private readonly Uri _baseUri;
    private readonly bool _ignoreCertificateErrors;
    private readonly RuntimeConfig _config;
    private readonly string? _username;
    private string _minecraftUuid;
    private readonly string _serverPublicId;
    private readonly Action<string>? _traceLog;

    /// <summary>
    /// Creates an ApiClient for the Halerith manager API.
    /// </summary>
    /// <param name="config">Registry-based configuration.</param>
    /// <param name="serverPublicIdOverride">
    /// Optional Manager server public UUID to use instead of <see cref="RuntimeConfig.ServerPublicId"/>.
    /// Used when the user selects a different server from the dropdown without
    /// persisting the change to app storage.
    /// </param>
    /// <param name="traceLog">
    /// Optional callback for verbose trace output (e.g. raw HTTP response bodies).
    /// Receives detail that is intentionally omitted from user-facing error messages
    /// but useful for file-based diagnostic logging.
    /// </param>
    public ApiClient(RuntimeConfig config, string? serverPublicIdOverride = null,
                     Action<string>? traceLog = null)
        : this(config, serverPublicIdOverride, traceLog, null, null)
    {
    }

    internal ApiClient(RuntimeConfig config, string? serverPublicIdOverride,
        Action<string>? traceLog, HttpMessageHandler? apiHandler, HttpMessageHandler? downloadHandler)
    {
        var baseUri = HttpsUrlPolicy.RequireHttpsAbsoluteUri(config.ApiBaseUrl, "Manager API URL");
        _baseUrl = baseUri.ToString().TrimEnd('/');
        _baseUri = new Uri(_baseUrl + "/");
        _ignoreCertificateErrors = config.IgnoreCertificateErrors;
        _config = config;
        _username = config.Username;
        _minecraftUuid = MinecraftProfileClient.NormalizeUuid(config.MinecraftUuid) ?? "";
        _serverPublicId = serverPublicIdOverride ?? config.ServerPublicId;
        _traceLog = traceLog;
        // Enable transparent gzip/deflate decompression so the panel can serve
        // compressed manifests.  The handler automatically sends Accept-Encoding
        // and decompresses the response body.
        var handler = apiHandler ?? CreateHttpClientHandler();

        _httpClient = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(Math.Clamp(config.StartupTimeoutSeconds, 10, 300))
        };

        // Separate client with no auth headers for third-party download URLs
        _downloadHttpClient = new HttpClient(downloadHandler ?? CreateHttpClientHandler())
        {
            Timeout = TimeSpan.FromMinutes(10)
        };
    }

    private string ServerBase => $"{_baseUrl}{ManagerApiDefaults.AgentApiPath}/servers/{Uri.EscapeDataString(_serverPublicId)}";
    private string ApiBase    => $"{_baseUrl}{ManagerApiDefaults.AgentApiPath}";

    internal HttpClientHandler CreateHttpClientHandler()
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            AllowAutoRedirect = false,
            UseCookies = false
        };

        if (_ignoreCertificateErrors)
            handler.ServerCertificateCustomValidationCallback = AcceptHostCertificateErrors;

        return handler;
    }

    private bool AcceptHostCertificateErrors(
        HttpRequestMessage request,
        X509Certificate2? certificate,
        X509Chain? chain,
        SslPolicyErrors sslPolicyErrors)
    {
        if (sslPolicyErrors == SslPolicyErrors.None)
            return true;

        return request.RequestUri is not null && IsSameApiOrigin(request.RequestUri);
    }

    /// <summary>
    /// Calls the manager health endpoint. Authenticated requests return the
    /// selected Minecraft account's environments and servers; unauthenticated
    /// requests only verify public API reachability.
    /// </summary>
    /// <param name="clientVersion">The caller's assembly version (e.g. "1.2.0").</param>
    public async Task<HealthResponse> GetHostHealthAsync(CancellationToken ct, string? clientVersion = null)
    {
        var body = new HealthRequest
        {
            Username      = _username ?? "",
            MinecraftUuid = _minecraftUuid,
            ClientVersion = clientVersion ?? "",
            Platform      = PlatformServices.Current.Updates.Platform,
            Architecture  = PlatformServices.Current.Updates.Architecture,
        };
        // Authenticate when we have a valid Halerith token or Minecraft token;
        // unauthenticated otherwise so public health can still verify reachability.
        bool auth = CanAuthenticate();
        return await PostAsync<HealthResponse>($"{ApiBase}/health", body, ct, authenticated: auth)
            .ConfigureAwait(false);
    }

    public async Task<HealthResponse> GetHostHealthWithMinecraftAsync(
        string minecraftAccessToken,
        string minecraftUuid,
        string username,
        CancellationToken ct,
        string? clientVersion = null)
    {
        _config.MinecraftAccessToken = minecraftAccessToken;
        _config.MinecraftUuid = MinecraftProfileClient.NormalizeUuid(minecraftUuid) ?? minecraftUuid;
        _minecraftUuid = _config.MinecraftUuid;

        var body = new HealthRequest
        {
            Username = username,
            MinecraftUuid = _minecraftUuid,
            ClientVersion = clientVersion ?? "",
            Platform = PlatformServices.Current.Updates.Platform,
            Architecture = PlatformServices.Current.Updates.Architecture,
        };

        return await PostAsync<HealthResponse>($"{ApiBase}/health", body, ct, authenticated: true)
            .ConfigureAwait(false);
    }

    public async Task<ManifestResponse> GetManifestAsync(ManifestRequest request, CancellationToken ct)
    {
        var manifest = await PostAsync<ManifestResponse>($"{ServerBase}/manifest", request, ct, authenticated: true)
            .ConfigureAwait(false);
        if (manifest.Ok)
            ManifestValidator.RequireDownloadHashes(manifest);
        return manifest;
    }

    public Task<ReportResponse> SendReportAsync(ReportRequest request, CancellationToken ct)
        => PostAsync<ReportResponse>($"{ServerBase}/report", request, ct, authenticated: true);

    private static List<HealthServerEntry> FlattenHealthServers(HealthResponse health)
    {
        if (health.Servers.Count > 0)
            return health.Servers;

        return health.Environments
            .SelectMany(e => e.Servers)
            .Where(s => !string.IsNullOrWhiteSpace(s.ServerPublicId))
            .ToList();
    }

    private async Task<T> GetAsync<T>(string url, CancellationToken ct, bool authenticated = true) where T : ApiResponseBase, new()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (authenticated)
            ApplyClientAuth(request);

        using var response = await _httpClient.SendAsync(request, ct).ConfigureAwait(false);
        return await ReadResponseAsync<T>(url, response, ct).ConfigureAwait(false);
    }

    private async Task<T> PostAsync<T>(string url, object body, CancellationToken ct, bool authenticated = true) where T : ApiResponseBase, new()
    {
        var bodyBytes = JsonSerializer.SerializeToUtf8Bytes(body, SerializerOptions.Default);
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new ByteArrayContent(bodyBytes)
        };
        request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json")
        {
            CharSet = "utf-8"
        };

        if (authenticated)
            ApplyClientAuth(request);

        if (authenticated && string.Equals(url, $"{ApiBase}/health", StringComparison.OrdinalIgnoreCase))
            ClientRequestSigner.Sign(request, bodyBytes, DeviceIdProvider.GetDeviceIdHash());

        using var response = await _httpClient.SendAsync(request, ct).ConfigureAwait(false);
        return await ReadResponseAsync<T>(url, response, ct).ConfigureAwait(false);
    }

    private bool CanAuthenticate()
    {
        return HasUsableHalerithToken() || HasMinecraftToken();
    }

    private void ApplyClientAuth(HttpRequestMessage request)
    {
        if (!string.Equals(_config.MinecraftUuid, _minecraftUuid, StringComparison.OrdinalIgnoreCase))
            _config.MinecraftUuid = _minecraftUuid;

        if (HasUsableHalerithToken())
        {
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _config.HalerithAccessToken.Trim());
        }
        else if (HasMinecraftToken())
        {
            request.Headers.TryAddWithoutValidation("X-Halerith-Minecraft-Access-Token", _config.MinecraftAccessToken.Trim());
        }

        if (!string.IsNullOrWhiteSpace(_config.MinecraftUuid))
            request.Headers.TryAddWithoutValidation("X-Halerith-Minecraft-UUID", _config.MinecraftUuid.Trim());
    }

    private bool HasMinecraftToken()
        => !string.IsNullOrWhiteSpace(_config.MinecraftAccessToken)
           && !string.IsNullOrWhiteSpace(_config.MinecraftUuid);

    private bool HasUsableHalerithToken()
    {
        if (string.IsNullOrWhiteSpace(_config.HalerithAccessToken))
            return false;

        if (!DateTimeOffset.TryParse(_config.HalerithAccessTokenExpiresAtUtc, out var expiresAt))
            return false;

        return expiresAt.UtcDateTime > DateTime.UtcNow.AddSeconds(60);
    }

    /// <summary>
    /// Returns the HTTP status code of the last response, or null if
    /// the status was not captured. Used for 401 detection in callers.
    /// </summary>
    public HttpStatusCode? LastStatusCode { get; private set; }

    private async Task<T> ReadResponseAsync<T>(string url, HttpResponseMessage response, CancellationToken ct)
        where T : ApiResponseBase, new()
    {
        LastStatusCode = response.StatusCode;

        var body = response.Content == null
            ? string.Empty
            : await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (ManifestDebugDumpEnabled && typeof(T) == typeof(ManifestResponse))
        {
            var message = $"Manifest debug dump triggered for {url}";
            FileLogger.Info(message);
            _traceLog?.Invoke(message);
            DumpManifestResponse(url, response.StatusCode, body);
        }

        if (string.IsNullOrWhiteSpace(body))
        {
            return new T
            {
                Ok = response.IsSuccessStatusCode,
                Error = response.IsSuccessStatusCode ? "empty_response" : $"http_{(int)response.StatusCode}",
                Message = $"API returned an empty response body from {url} (HTTP {(int)response.StatusCode})."
            };
        }

        var mediaType = response.Content?.Headers.ContentType?.MediaType ?? "";
        if (LooksLikeHtmlResponse(body, mediaType))
        {
            return new T
            {
                Ok = false,
                Error = $"http_{(int)response.StatusCode}_html",
                Message = $"API endpoint returned an HTML page instead of JSON from {url} (HTTP {(int)response.StatusCode}). Check the subdomain document root/rewrite configuration."
            };
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<T>(body, SerializerOptions.Default);
            if (parsed == null)
            {
                _traceLog?.Invoke($"API returned JSON that deserialized to null from {url}.");
                return new T
                {
                    Ok = false,
                    Error = "invalid_json",
                    Message = $"API returned an unexpected empty response from {url}."
                };
            }

            if (!response.IsSuccessStatusCode && parsed.Ok)
            {
                parsed.Ok = false;
                parsed.Error = $"http_{(int)response.StatusCode}";
                parsed.Message ??= $"Request failed for {url}.";
            }

            // Only translate the message when the response signals failure.
            // Successful responses typically have no 'error' or 'message' field;
            // running ToUserMessage on a null error/message would replace null
            // with the generic "The API request failed." fallback, misleadingly
            // showing an error string even when ok=true.
            if (!parsed.Ok)
                parsed.Message = ApiErrorTranslator.ToUserMessage(parsed.Code, parsed.Error, parsed.Message);
            else if (IsTrustedAuthenticatedResponse(response, new Uri(url)))
                PersistResponseAuth(parsed.Auth);

            return parsed;
        }
        catch (Exception ex)
        {
            // Write the raw body to the trace/file log only — it can be a full HTML page
            // (e.g. a 404 page) that would flood the GUI log.
            _traceLog?.Invoke(
                $"Failed to parse API response from {url} (HTTP {(int)response.StatusCode}): {ex.Message}");
            return new T
            {
                Ok = false,
                Error = "json_parse_failed",
                Message = $"Failed to parse API response from {url} (HTTP {(int)response.StatusCode}): {ex.Message}"
            };
        }
    }

    private static bool LooksLikeHtmlResponse(string body, string mediaType)
        => mediaType.Contains("html", StringComparison.OrdinalIgnoreCase)
           || body.TrimStart().StartsWith("<!doctype", StringComparison.OrdinalIgnoreCase)
           || body.TrimStart().StartsWith("<html", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Downloads a file from the given URL to <paramref name="destinationPath"/>.
    ///
    /// For known own-API download endpoints, sends a POST with device signature auth. For
    /// third-party CDN URLs (Modrinth, CurseForge) and any other URL, sends a
    /// plain GET with no auth headers.
    /// </summary>
    /// <param name="url">Absolute download URL.</param>
    /// <param name="destinationPath">Local file path to write to.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task DownloadFileAsync(
        string url,
        string destinationPath,
        CancellationToken ct,
        long? maxBytes = null)
    {
        var uri = HttpsUrlPolicy.RequireHttpsAbsoluteUri(url, "Download URL");
        bool isAuthenticatedDownload = IsAuthenticatedDownloadEndpoint(uri);

        using var request = isAuthenticatedDownload
            ? new HttpRequestMessage(HttpMethod.Post, uri)
            : new HttpRequestMessage(HttpMethod.Get, uri);

        if (isAuthenticatedDownload)
            ApplyClientAuth(request);

        var targetDir = Path.GetDirectoryName(destinationPath)
            ?? throw new InvalidOperationException($"Destination path has no parent directory: {destinationPath}");
        Directory.CreateDirectory(targetDir);

        var tempPath = SafeFileHelpers.CreateDownloadTempPath(targetDir, Path.GetFileName(destinationPath));
        try
        {
            using var response = await SendDownloadAsync(request, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if (isAuthenticatedDownload && IsTrustedAuthenticatedResponse(response, uri))
                PersistHeaderAuth(response);

            if (maxBytes is > 0 && response.Content.Headers.ContentLength is > 0 &&
                response.Content.Headers.ContentLength.Value > maxBytes.Value)
            {
                throw new InvalidOperationException(
                    $"Download is larger than the allowed limit of {FormatBytes(maxBytes.Value)}.");
            }

            await using (var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            await using (var destination = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                await CopyToAsync(source, destination, maxBytes, ct).ConfigureAwait(false);

            File.Move(tempPath, destinationPath, overwrite: true);
        }
        finally
        {
            TryDeleteFile(tempPath);
        }
    }

    // API calls never follow redirects. File redirects are fresh, unauthenticated
    // HTTPS GETs, including redirects back to our own origin. Never replay a POST
    // or custom Minecraft authentication headers to a download/CDN host.
    private async Task<HttpResponseMessage> SendDownloadAsync(HttpRequestMessage initial, CancellationToken ct)
    {
        var request = initial;
        try
        {
            for (var redirects = 0; ; redirects++)
            {
                var response = await _downloadHttpClient.SendAsync(request,
                    HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                if (response.StatusCode is not (HttpStatusCode.MovedPermanently or HttpStatusCode.Redirect
                    or HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect))
                    return response;

                Uri next;
                try
                {
                    if (redirects >= 10 || response.Headers.Location is null)
                        throw new HttpRequestException("The file download redirected too many times or did not provide a destination.");
                    next = HttpsUrlPolicy.RequireHttpsAbsoluteUri(
                        new Uri(request.RequestUri!, response.Headers.Location).AbsoluteUri, "Download redirect");
                }
                finally { response.Dispose(); }

                if (!ReferenceEquals(request, initial)) request.Dispose();
                request = new HttpRequestMessage(HttpMethod.Get, next);
            }
        }
        finally
        {
            if (!ReferenceEquals(request, initial)) request.Dispose();
        }
    }

    private bool IsTrustedAuthenticatedResponse(HttpResponseMessage response, Uri expected)
    {
        var request = response.RequestMessage;
        return response.IsSuccessStatusCode && IsSameApiOrigin(expected)
            && request?.RequestUri == expected
            && (request.Headers.Authorization is not null
                || request.Headers.Contains("X-Halerith-Minecraft-Access-Token"));
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // Download temp cleanup should not mask the original failure.
        }
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
            {
                throw new InvalidOperationException(
                    $"Download is larger than the allowed limit of {FormatBytes(maxBytes.Value)}.");
            }

            await destination.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
        }
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes % (1024L * 1024L * 1024L) == 0)
            return $"{bytes / (1024L * 1024L * 1024L)} GB";

        if (bytes % (1024L * 1024L) == 0)
            return $"{bytes / (1024L * 1024L)} MB";

        return $"{bytes:N0} bytes";
    }

    private bool IsAuthenticatedDownloadEndpoint(Uri uri)
    {
        if (!IsSameApiOrigin(uri))
            return false;

        var path = NormalizePath(uri.AbsolutePath);
        var serverDownloadPrefix = NormalizePath(new Uri($"{ApiBase}/servers/").AbsolutePath) + "/";
        return path.StartsWith(serverDownloadPrefix, StringComparison.OrdinalIgnoreCase)
            && path.EndsWith("/files/download", StringComparison.OrdinalIgnoreCase);
    }

    private bool IsSameApiOrigin(Uri uri)
        => string.Equals(uri.Scheme, _baseUri.Scheme, StringComparison.OrdinalIgnoreCase)
        && string.Equals(uri.Host, _baseUri.Host, StringComparison.OrdinalIgnoreCase)
        && uri.Port == _baseUri.Port;

    private static string NormalizePath(string path) => path.TrimEnd('/');

    private void DumpManifestResponse(string url, HttpStatusCode statusCode, string body)
    {
        try
        {
            var logsDir = Path.Combine(LauncherPaths.GetAppDataDirectory(), "Logs");
            Directory.CreateDirectory(logsDir);

            var timestamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmssfff");
            var safeServerPublicId = MakeSafeFileName(_serverPublicId);
            var fileName = $"{ManifestDebugDumpPrefix}-{timestamp}-{safeServerPublicId}.json";
            var path = Path.Combine(logsDir, fileName);

            var json = string.IsNullOrWhiteSpace(body)
                ? JsonSerializer.Serialize(new
                {
                    ok = false,
                    status_code = (int)statusCode,
                    url,
                    message = "Manifest response body was empty."
                }, SerializerOptions.Default)
                : body;

            File.WriteAllText(path, json);
            var message = $"Manifest debug dump written: {path}";
            FileLogger.Info(message);
            _traceLog?.Invoke(message);
        }
        catch (Exception ex)
        {
            var message = $"Manifest debug dump failed: {ex.Message}";
            FileLogger.Warn(message);
            _traceLog?.Invoke(message);
            // Debug-only diagnostic output must never affect the manifest request.
        }
    }

    private static string MakeSafeFileName(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "unknown-server";

        var invalid = Path.GetInvalidFileNameChars();
        var chars = value.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray();
        var safe = new string(chars).Trim();
        return safe.Length == 0 ? "unknown-server" : safe;
    }

    public void Dispose()
    {
        _httpClient.Dispose();
        _downloadHttpClient.Dispose();
    }

    private void PersistResponseAuth(ClientAccessTokenInfo? auth)
    {
        if (auth is not { AccessToken.Length: > 0 })
            return;

        _config.HalerithAccessToken = auth.AccessToken;
        _config.HalerithAccessTokenExpiresAtUtc = auth.ExpiresAt;

        if (!string.IsNullOrWhiteSpace(_config.HostId))
        {
            AppStorageDatabase.SaveClientAccessTokenForHost(
                _config.HostId,
                auth.AccessToken,
                auth.ExpiresAt);
        }
    }

    private void PersistHeaderAuth(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("X-Halerith-Access-Token", out var tokens))
            return;

        var token = tokens.FirstOrDefault()?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(token))
            return;

        response.Headers.TryGetValues("X-Halerith-Access-Token-Expires-At", out var expiresValues);
        response.Headers.TryGetValues("X-Halerith-Access-Token-Expires-In", out var expiresInValues);
        var expiresAt = expiresValues?.FirstOrDefault()?.Trim() ?? "";
        var expiresIn = int.TryParse(expiresInValues?.FirstOrDefault(), out var parsedExpiresIn) ? parsedExpiresIn : 0;

        if (string.IsNullOrWhiteSpace(expiresAt)
            && expiresIn > 0)
        {
            expiresAt = DateTimeOffset.UtcNow.AddSeconds(expiresIn).ToString("O");
        }

        PersistResponseAuth(new ClientAccessTokenInfo
        {
            AccessToken = token,
            ExpiresAt = expiresAt,
            ExpiresIn = expiresIn > 0 ? expiresIn : 0,
        });
    }
}
