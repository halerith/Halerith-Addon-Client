using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;

namespace HalerithAddonClient.Core;

public static class ImageCacheService
{
    private const string ContentIconKind = "content-icon";
    private const string MinecraftHeadKind = "minecraft-head";
    private const string ServerIconKind = "server-icon";
    private const long MaxImageBytes = 100L * 1024L;
    internal const long MaxContentIconBytes = 512L * 1024L;
    private const long MaxServerIconBytes = 265L * 1024L;
    private static readonly TimeSpan MinecraftHeadRefreshInterval = TimeSpan.FromDays(7);

    private static readonly HttpClient HttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(15),
    };

    private static readonly SemaphoreSlim DownloadSlots = new(4, 4);
    private static readonly ConcurrentDictionary<string, Task> ActiveDownloads = new(StringComparer.OrdinalIgnoreCase);

    static ImageCacheService()
    {
        HttpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("HalerithAddonClient", "1.0"));
    }

    public static async Task<string?> ResolveContentIconPathAsync(
        string? source,
        string? sourceId,
        string? slug,
        string? contentType,
        string? iconUrl)
    {
        if (string.IsNullOrWhiteSpace(iconUrl))
            return null;

        if (!HttpsUrlPolicy.TryNormalizeHttpsUrl(iconUrl, out var normalizedIconUrl, out _, "Image URL"))
            return null;
        iconUrl = normalizedIconUrl;

        var cacheKey = BuildContentIconCacheKey(source, sourceId, slug, contentType, iconUrl);
        if (cacheKey is null)
            return null;

        var localPath = BuildContentIconPath(cacheKey, iconUrl);
        var now = DateTime.UtcNow.ToString("O");
        var record = AppStorageDatabase.LoadImageCacheRecord(cacheKey);
        var urlChanged = record is not null &&
                         !string.Equals(record.Url, iconUrl, StringComparison.OrdinalIgnoreCase);

        record ??= new ImageCacheRecord { CacheKey = cacheKey };
        record.ImageKind = ContentIconKind;
        record.Source = source?.Trim() ?? "";
        record.SourceId = sourceId?.Trim() ?? "";
        record.Slug = slug?.Trim() ?? "";
        record.ContentType = contentType?.Trim() ?? "";
        record.Url = iconUrl.Trim();
        record.LocalPath = localPath;
        record.LastSeenUtc = now;
        if (urlChanged)
        {
            record.DownloadedAtUtc = "";
            record.FailedAtUtc = "";
            record.FailureCount = 0;
        }
        AppStorageDatabase.UpsertImageCacheRecord(record);

        if (!urlChanged && File.Exists(localPath))
        {
            AppStorageDatabase.MarkImageCacheAccessed(cacheKey);
            return localPath;
        }

        if (IsInBackoff(record)) return null;
        await DownloadSlots.WaitAsync().ConfigureAwait(false);
        try
        {
            return await DownloadAsync(cacheKey, iconUrl.Trim(), localPath, MaxContentIconBytes).ConfigureAwait(false) ? localPath : null;
        }
        finally { DownloadSlots.Release(); }
    }

    public static string? ResolveMinecraftHeadPath(
        MinecraftAccount? account,
        Action<string>? downloaded = null,
        bool refreshIfStale = false)
    {
        if (account is null || string.IsNullOrWhiteSpace(account.Uuid))
            return null;

        var uuid = NormalizeUuid(account.Uuid);
        if (string.IsNullOrWhiteSpace(uuid))
            return null;

        var cacheKey = BuildMinecraftHeadCacheKey(uuid);
        var url = BuildMinecraftHeadUrl(uuid);
        var localPath = Path.Combine(LauncherPaths.GetImageCacheDirectory(), MinecraftHeadKind, $"{uuid}.png");
        var now = DateTime.UtcNow.ToString("O");
        var record = AppStorageDatabase.LoadImageCacheRecord(cacheKey);
        var urlChanged = record is not null &&
                         !string.Equals(record.Url, url, StringComparison.OrdinalIgnoreCase);
        var fileExists = File.Exists(localPath);
        var shouldRefresh = !fileExists || (refreshIfStale && (urlChanged || IsMinecraftHeadStale(record)));

        record ??= new ImageCacheRecord { CacheKey = cacheKey };
        record.ImageKind = MinecraftHeadKind;
        record.Source = "minecraft";
        record.SourceId = uuid;
        record.Url = url;
        record.LocalPath = localPath;
        record.LastSeenUtc = now;
        if (urlChanged)
        {
            record.DownloadedAtUtc = "";
            record.FailedAtUtc = "";
            record.FailureCount = 0;
        }
        AppStorageDatabase.UpsertImageCacheRecord(record);

        if (!shouldRefresh && fileExists)
        {
            AppStorageDatabase.MarkImageCacheAccessed(cacheKey);
            return localPath;
        }

        QueueDownload(cacheKey, url, localPath, downloaded);
        return fileExists ? localPath : null;
    }

    public static async Task<string?> ResolveServerIconPathAsync(
        ApiClient apiClient,
        string? hostId,
        string? serverPublicId,
        string? serverName,
        HealthServerIconInfo? icon,
        Action<string>? downloaded = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(hostId) || string.IsNullOrWhiteSpace(serverPublicId))
            return null;

        var iconUrl = icon?.DownloadUrl?.Trim() ?? "";
        var expectedSha256 = NormalizeSha256(icon?.Sha256);
        if (string.IsNullOrWhiteSpace(iconUrl) || string.IsNullOrWhiteSpace(expectedSha256))
            return null;

        var expectedContentType = NormalizeServerIconContentType(icon?.ContentType);
        if (expectedContentType is null)
            return null;
        var safeFilename = icon?.Filename?.Trim().ToLowerInvariant() ?? "";
        if (safeFilename.Length > 0 &&
            (Path.GetFileName(safeFilename) != safeFilename ||
             (expectedContentType == "image/png" && !safeFilename.EndsWith(".png", StringComparison.Ordinal)) ||
             (expectedContentType == "image/jpeg" &&
              !safeFilename.EndsWith(".jpg", StringComparison.Ordinal) &&
              !safeFilename.EndsWith(".jpeg", StringComparison.Ordinal))))
            return null;

        if (!HttpsUrlPolicy.TryNormalizeHttpsUrl(iconUrl, out var normalizedIconUrl, out _, "Server icon URL"))
            return null;
        iconUrl = normalizedIconUrl;

        var cacheKey = BuildServerIconCacheKey(hostId, serverPublicId);
        var localPath = BuildServerIconPath(cacheKey, expectedContentType);
        var now = DateTime.UtcNow.ToString("O");
        var record = AppStorageDatabase.LoadImageCacheRecord(cacheKey);
        var cacheMatches = record is not null
            && string.Equals(record.Url, iconUrl, StringComparison.OrdinalIgnoreCase)
            && string.Equals(record.ImageHash, expectedSha256, StringComparison.OrdinalIgnoreCase)
            && string.Equals(record.ContentType, expectedContentType, StringComparison.OrdinalIgnoreCase)
            && File.Exists(record.LocalPath)
            && IsVerifiedServerIconFile(record.LocalPath, expectedContentType);

        record ??= new ImageCacheRecord { CacheKey = cacheKey };
        record.ImageKind = ServerIconKind;
        record.Source = "server";
        record.SourceId = serverPublicId.Trim();
        record.Slug = serverName?.Trim() ?? "";
        record.ContentType = expectedContentType;
        record.Url = iconUrl;
        record.LocalPath = localPath;
        record.ImageHash = expectedSha256;
        record.LastSeenUtc = now;
        if (!cacheMatches)
        {
            record.DownloadedAtUtc = "";
            record.FailedAtUtc = "";
            record.FailureCount = 0;
        }
        AppStorageDatabase.UpsertImageCacheRecord(record);

        if (cacheMatches)
        {
            AppStorageDatabase.MarkImageCacheAccessed(cacheKey);
            return record.LocalPath;
        }

        try
        {
            await apiClient.DownloadFileAsync(iconUrl, localPath, ct, maxBytes: MaxServerIconBytes).ConfigureAwait(false);
            if (!IsVerifiedServerIconFile(localPath, expectedContentType))
            {
                TryDeleteFile(localPath);
                AppStorageDatabase.MarkImageCacheFailed(cacheKey);
                return null;
            }
            var actualSha256 = ComputeFileSha256(localPath);
            if (!string.Equals(actualSha256, expectedSha256, StringComparison.OrdinalIgnoreCase))
            {
                TryDeleteFile(localPath);
                AppStorageDatabase.MarkImageCacheFailed(cacheKey);
                return null;
            }

            AppStorageDatabase.MarkImageCacheDownloaded(cacheKey, localPath, expectedSha256);
            downloaded?.Invoke(localPath);
            return localPath;
        }
        catch
        {
            AppStorageDatabase.MarkImageCacheFailed(cacheKey);
            return null;
        }
    }

    public static string? ResolveCachedServerIconPath(string? hostId, string? serverPublicId, string? expectedSha256)
    {
        if (string.IsNullOrWhiteSpace(hostId) || string.IsNullOrWhiteSpace(serverPublicId))
            return null;

        var cleanSha = NormalizeSha256(expectedSha256);
        if (string.IsNullOrWhiteSpace(cleanSha))
            return null;

        var record = AppStorageDatabase.LoadImageCacheRecord(BuildServerIconCacheKey(hostId, serverPublicId));
        if (record is null ||
            !string.Equals(record.ImageHash, cleanSha, StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(record.LocalPath) ||
            !File.Exists(record.LocalPath) ||
            NormalizeServerIconContentType(record.ContentType) is not string contentType ||
            !IsVerifiedServerIconFile(record.LocalPath, contentType))
            return null;

        AppStorageDatabase.MarkImageCacheAccessed(record.CacheKey);
        return record.LocalPath;
    }

    public static void CleanupUnreferencedImages(Action<string>? log = null)
    {
        try
        {
            var referenced = LoadReferencedCacheKeys();
            var cutoff = DateTime.UtcNow - TimeSpan.FromDays(30);
            var deleteKeys = new List<(string CacheKey, string LocalPath)>();

            using (var db = AppStorageDatabase.OpenConnection())
            using (var cmd = db.CreateCommand())
            {
                cmd.CommandText = "SELECT cache_key, local_path, last_seen_utc FROM image_cache";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    var cacheKey = reader.GetString(0);
                    if (referenced.Contains(cacheKey))
                        continue;

                    var localPath = reader.GetString(1);
                    var lastSeen = DateTime.TryParse(reader.GetString(2), out var parsed)
                        ? parsed.ToUniversalTime()
                        : DateTime.MinValue;
                    if (lastSeen <= cutoff)
                        deleteKeys.Add((cacheKey, localPath));
                }
            }

            if (deleteKeys.Count == 0)
                return;

            using (var db = AppStorageDatabase.OpenConnection())
            using (var tx = db.BeginTransaction())
            using (var delete = db.CreateCommand())
            {
                delete.Transaction = tx;
                delete.CommandText = "DELETE FROM image_cache WHERE cache_key = $key";
                delete.Parameters.Add("$key", Microsoft.Data.Sqlite.SqliteType.Text);

                foreach (var (cacheKey, _) in deleteKeys)
                {
                    delete.Parameters["$key"].Value = cacheKey;
                    delete.ExecuteNonQuery();
                }

                tx.Commit();
            }

            foreach (var (_, localPath) in deleteKeys)
                TryDeleteFile(localPath);

            log?.Invoke($"Image cache cleanup removed {deleteKeys.Count} stale entr{(deleteKeys.Count == 1 ? "y" : "ies")}.");
        }
        catch (Exception ex)
        {
            log?.Invoke($"Image cache cleanup skipped: {ex.Message}");
        }
    }

    public static string? BuildContentIconCacheKey(
        string? source,
        string? sourceId,
        string? slug,
        string? contentType,
        string iconUrl)
    {
        var cleanSource = source?.Trim().ToLowerInvariant() ?? "";
        var cleanSourceId = sourceId?.Trim().ToLowerInvariant() ?? "";
        if (cleanSource.Length > 0 && cleanSourceId.Length > 0)
            return $"content-icon:{cleanSource}:{cleanSourceId}";

        var cleanSlug = slug?.Trim().ToLowerInvariant() ?? "";
        var cleanType = contentType?.Trim().ToLowerInvariant() ?? "";
        if (cleanSource.Length > 0 && cleanSlug.Length > 0 && cleanType.Length > 0)
            return $"content-icon:{cleanSource}:slug:{cleanType}:{cleanSlug}";

        return $"content-icon:url:{Sha256Hex(iconUrl)}";
    }

    public static string BuildMinecraftHeadCacheKey(string uuid)
        => $"minecraft-head:{NormalizeUuid(uuid)}";

    public static string BuildServerIconCacheKey(string hostId, string serverPublicId)
        => $"server-icon:{hostId.Trim().ToLowerInvariant()}:{serverPublicId.Trim().ToLowerInvariant()}";

    private static string BuildMinecraftHeadUrl(string uuid)
        => $"https://mc-heads.net/avatar/{NormalizeUuid(uuid)}/32";

    private static HashSet<string> LoadReferencedCacheKeys()
    {
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        using var db = AppStorageDatabase.OpenConnection();
        AddReferencedContentKeys(db, keys, "server_content_items");
        AddReferencedContentKeys(db, keys, "local_instance_content_items");
        AddReferencedServerIconKeys(db, keys);

        using var accounts = db.CreateCommand();
        accounts.CommandText = "SELECT uuid FROM minecraft_accounts";
        using var accountReader = accounts.ExecuteReader();
        while (accountReader.Read())
        {
            var uuid = NormalizeUuid(accountReader.GetString(0));
            if (!string.IsNullOrWhiteSpace(uuid))
                keys.Add(BuildMinecraftHeadCacheKey(uuid));
        }

        return keys;
    }

    private static void AddReferencedContentKeys(Microsoft.Data.Sqlite.SqliteConnection db, HashSet<string> keys, string table)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = $"SELECT source, source_id, slug, content_type, icon_url FROM {table} WHERE icon_url IS NOT NULL AND icon_url <> ''";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var cacheKey = BuildContentIconCacheKey(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4));
            if (!string.IsNullOrWhiteSpace(cacheKey))
                keys.Add(cacheKey);
        }
    }

    private static void AddReferencedServerIconKeys(Microsoft.Data.Sqlite.SqliteConnection db, HashSet<string> keys)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT host_id, server_public_id FROM servers WHERE server_icon_sha256 <> ''";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            keys.Add(BuildServerIconCacheKey(reader.GetString(0), reader.GetString(1)));
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // Cache cleanup should never interrupt client startup/shutdown.
        }
    }

    private static string BuildContentIconPath(string cacheKey, string url)
    {
        var extension = GetSafeExtension(url);
        var fileName = $"{Sha256Hex(cacheKey + "|" + url)}{extension}";
        return Path.Combine(LauncherPaths.GetImageCacheDirectory(), ContentIconKind, fileName);
    }

    private static string BuildServerIconPath(string cacheKey, string contentType)
        => Path.Combine(
            LauncherPaths.GetImageCacheDirectory(),
            ServerIconKind,
            $"{Sha256Hex(cacheKey)}{(contentType == "image/jpeg" ? ".jpg" : ".png")}");

    private static string? NormalizeServerIconContentType(string? value)
    {
        var clean = value?.Trim().ToLowerInvariant() ?? "";
        return clean is "image/png" or "image/jpeg" ? clean : null;
    }

    private static bool IsVerifiedServerIconFile(string path, string expectedContentType)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length < 4 || info.Length > MaxServerIconBytes)
                return false;

            using var stream = File.OpenRead(path);
            Span<byte> header = stackalloc byte[8];
            var read = stream.Read(header);
            if (expectedContentType == "image/png")
            {
                ReadOnlySpan<byte> signature = stackalloc byte[] { 137, 80, 78, 71, 13, 10, 26, 10 };
                return read == signature.Length && header.SequenceEqual(signature);
            }

            if (read < 3 || header[0] != 0xFF || header[1] != 0xD8 || header[2] != 0xFF)
                return false;
            stream.Seek(-2, SeekOrigin.End);
            return stream.ReadByte() == 0xFF && stream.ReadByte() == 0xD9;
        }
        catch
        {
            return false;
        }
    }

    private static void QueueDownload(string cacheKey, string url, string localPath, Action<string>? downloaded)
    {
        if (!HttpsUrlPolicy.TryNormalizeHttpsUrl(url, out var normalizedUrl, out _, "Image URL"))
            return;

        var record = AppStorageDatabase.LoadImageCacheRecord(cacheKey);
        if (IsInBackoff(record))
            return;

        ActiveDownloads.GetOrAdd(cacheKey, key => Task.Run(async () =>
        {
            try
            {
                await DownloadSlots.WaitAsync().ConfigureAwait(false);
                try
                {
                    if (await DownloadAsync(cacheKey, normalizedUrl, localPath).ConfigureAwait(false))
                        downloaded?.Invoke(localPath);
                }
                finally
                {
                    DownloadSlots.Release();
                }
            }
            finally
            {
                ActiveDownloads.TryRemove(key, out var _);
            }
        }));
    }

    private static async Task<bool> DownloadAsync(string cacheKey, string url, string localPath, long maxBytes = MaxImageBytes)
    {
        var tempPath = localPath + ".tmp";
        try
        {
            var uri = HttpsUrlPolicy.RequireHttpsAbsoluteUri(url, "Image URL");
            Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);
            using var response = await HttpClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            await using (var target = File.Create(tempPath))
                await CopyBoundedImageAsync(response.Content, target, maxBytes).ConfigureAwait(false);

            File.Move(tempPath, localPath, overwrite: true);
            AppStorageDatabase.MarkImageCacheDownloaded(cacheKey, localPath);
            return true;
        }
        catch
        {
            AppStorageDatabase.MarkImageCacheFailed(cacheKey);
            return false;
        }
        finally
        {
            TryDeleteFile(tempPath);
        }
    }

    internal static async Task CopyBoundedImageAsync(HttpContent content, Stream target, long maxBytes)
    {
        if (content.Headers.ContentLength > maxBytes)
            throw new InvalidOperationException("Image is larger than the cache limit.");
        await using var source = await content.ReadAsStreamAsync().ConfigureAwait(false);
        var buffer = new byte[81920]; long total = 0; int read;
        while ((read = await source.ReadAsync(buffer).ConfigureAwait(false)) > 0)
        {
            total += read;
            if (total > maxBytes) throw new InvalidOperationException("Image is larger than the cache limit.");
            await target.WriteAsync(buffer.AsMemory(0, read)).ConfigureAwait(false);
        }
    }

    private static bool IsInBackoff(ImageCacheRecord? record)
    {
        if (record is null || record.FailureCount < 3 || string.IsNullOrWhiteSpace(record.FailedAtUtc))
            return false;

        return DateTime.TryParse(record.FailedAtUtc, out var failedAt) &&
               DateTime.UtcNow - failedAt.ToUniversalTime() < TimeSpan.FromHours(1);
    }

    private static bool IsMinecraftHeadStale(ImageCacheRecord? record)
    {
        if (record is null || string.IsNullOrWhiteSpace(record.DownloadedAtUtc))
            return true;

        return DateTime.TryParse(record.DownloadedAtUtc, out var downloadedAt) &&
               DateTime.UtcNow - downloadedAt.ToUniversalTime() >= MinecraftHeadRefreshInterval;
    }

    private static string? NormalizeSha256(string? value)
    {
        var clean = value?.Trim().ToLowerInvariant() ?? "";
        return clean.Length == 64 && clean.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f')
            ? clean
            : null;
    }

    private static string ComputeFileSha256(string path)
    {
        using var sha = SHA256.Create();
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant();
    }

    private static string GetSafeExtension(string url)
    {
        try
        {
            var path = Uri.TryCreate(url, UriKind.Absolute, out var uri)
                ? uri.AbsolutePath
                : url;
            var ext = Path.GetExtension(path).ToLowerInvariant();
            return ext is ".png" or ".jpg" or ".jpeg" or ".webp"
                ? ext
                : ".img";
        }
        catch
        {
            return ".img";
        }
    }

    private static string NormalizeUuid(string uuid)
        => uuid.Trim().Replace("-", "", StringComparison.Ordinal).ToLowerInvariant();

    private static string Sha256Hex(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
