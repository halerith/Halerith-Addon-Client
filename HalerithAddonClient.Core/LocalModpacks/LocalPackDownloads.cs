using HalerithAddonClient.Core.ContentSearch;

namespace HalerithAddonClient.Core.LocalModpacks;

/// <summary>No credentials, cookies, redirects to arbitrary hosts, or private-network URLs.</summary>
public static class LocalPackDownloads
{
    private static readonly HttpClient Http = new(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }) { Timeout = TimeSpan.FromMinutes(10) };
    public static bool IsAllowedUrl(string? value) => Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && uri.Scheme == "https" && uri.IsDefaultPort && uri.UserInfo.Length == 0
        && uri.Host is "cdn.modrinth.com" or "edge.forgecdn.net" or "mediafilez.forgecdn.net"
            or "github.com" or "raw.githubusercontent.com" or "objects.githubusercontent.com" or "release-assets.githubusercontent.com" or "gitlab.com";
    public static async Task DownloadAsync(string? url, string path, string? hash, long? size, CancellationToken ct)
    {
        if (!IsAllowedUrl(url)) throw new InvalidOperationException("This pack file has no supported provider download. Install it manually if required.");
        var expected = SafeFileHelpers.RequireExpectedHash(hash, "Pack download");
        if (size is <= 0 or > LocalPackInspector.MaxArchiveBytes) throw new InvalidDataException("Invalid pack download size.");
        for (var redirect = 0; redirect < 6; redirect++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.UserAgent.ParseAdd(ProviderContacts.ModrinthUserAgent);
            using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if ((int)response.StatusCode is >= 300 and < 400)
            {
                url = response.Headers.Location is { } location ? new Uri(new Uri(url!), location).ToString() : null;
                if (!IsAllowedUrl(url)) throw new InvalidDataException("Unsafe pack download redirect.");
                continue;
            }
            response.EnsureSuccessStatusCode();
            var max = size ?? LocalPackInspector.MaxArchiveBytes;
            if (response.Content.Headers.ContentLength > max) throw new InvalidDataException("Pack download is too large.");
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            try
            {
                await using (var input = await response.Content.ReadAsStreamAsync(ct))
                await using (var output = new FileStream(temporary, FileMode.CreateNew))
                {
                    var buffer = new byte[81920]; long count = 0; int read;
                    while ((read = await input.ReadAsync(buffer, ct)) > 0)
                    {
                        count += read; if (count > max) throw new InvalidDataException("Pack download exceeded its size limit.");
                        await output.WriteAsync(buffer.AsMemory(0, read), ct);
                    }
                    if (size is not null && count != size) throw new InvalidDataException("Pack download size did not match.");
                }
                await SafeFileHelpers.VerifyExpectedHashAsync(temporary, expected, ct);
                File.Move(temporary, path, true); return;
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        throw new InvalidDataException("Too many pack download redirects.");
    }
}
