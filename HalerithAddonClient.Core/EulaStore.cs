using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HalerithAddonClient.Core;

/// <summary>
/// Persists and checks EULA acceptance state in local SQLite storage.
///
/// Storage key: <c>local_acceptances.eula_stamp</c>
/// Format: <c>YYYYMMDD-X.Y.Z</c> (e.g. "20260417-1.9.2")
///
/// On startup the app compares the stored EULA document version and content
/// hash with the required document. If they don't match, the EULA must be
/// re-accepted.
/// </summary>
public static class EulaStore
{
    private const string AcceptanceKey = "eula_stamp";
    private const string AcceptanceJsonKey = "eula_acceptance_client";
    private const string HistoryKey = "eula_acceptance_client_history";

    /// <summary>
    /// Returns the current EULA acceptance stamp, or <see langword="null"/>
    /// if the EULA has never been accepted or the version doesn't match.
    /// </summary>
    public static string? GetAcceptedStamp()
    {
        var stamp = AppStorageDatabase.GetLocalAcceptance(AcceptanceKey);
        return string.IsNullOrWhiteSpace(stamp) ? null : stamp;
    }

    /// <summary>
    /// Checks whether the packaged EULA has been accepted.
    /// </summary>
    public static bool IsAcceptedForCurrentVersion()
    {
        return IsAcceptedForDocument(EulaDocument.PackagedClient());
    }

    public static bool IsAcceptedForDocument(EulaDocument document)
    {
        var acceptance = GetAcceptedDocument();
        if (acceptance is not null)
            return IsStructuredAcceptanceForDocument(acceptance, document);

        var stamp = GetAcceptedStamp();
        if (string.IsNullOrEmpty(stamp)) return false;

        var dashIdx = stamp.IndexOf('-');
        if (dashIdx < 0 || dashIdx >= stamp.Length - 1) return false;

        var storedVersion = stamp[(dashIdx + 1)..];
        return string.Equals(storedVersion, document.Version, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Records EULA acceptance for the supplied EULA document.
    /// Writes <c>YYYYMMDD-X.Y.Z</c> to local SQLite storage.
    /// </summary>
    /// <returns>The acceptance stamp that was written (for pushing to the server).</returns>
    public static string RecordAcceptance()
    {
        return RecordAcceptance(EulaDocument.PackagedClient());
    }

    public static string RecordAcceptance(EulaDocument document)
    {
        CacheDocument(document);
        var history = GetAcceptanceHistory();
        var acceptance = AcceptanceForDocument(history, document, DateTimeOffset.UtcNow);

        if (!history.Contains(acceptance)) history.Add(acceptance);
        using (var db = AppStorageDatabase.OpenConnection())
        {
            foreach (var item in history)
            {
                using var insert = db.CreateCommand();
                insert.CommandText = "INSERT INTO local_acceptances (key, value, updated_at) VALUES ($key, $value, datetime('now')) ON CONFLICT(key) DO NOTHING";
                insert.Parameters.AddWithValue("$key", $"{HistoryKey}:{item.Product}:{item.EulaVersion}:{item.ContentHash}");
                insert.Parameters.AddWithValue("$value", JsonSerializer.Serialize(item, SerializerOptions.Default));
                insert.ExecuteNonQuery();
            }
        }
        // Read the winning original record if another process accepted concurrently.
        acceptance = JsonSerializer.Deserialize<EulaAcceptanceRecord>(AppStorageDatabase.GetLocalAcceptance($"{HistoryKey}:{acceptance.Product}:{acceptance.EulaVersion}:{acceptance.ContentHash}"), SerializerOptions.Default)!;
        var stamp = $"{acceptance.AcceptedAtUtc:yyyyMMdd}-{document.Version}";
        AppStorageDatabase.SaveLocalAcceptance(AcceptanceKey, stamp);
        AppStorageDatabase.SaveLocalAcceptance(AcceptanceJsonKey, JsonSerializer.Serialize(acceptance, SerializerOptions.Default));
        return stamp;
    }

    public static EulaAcceptanceRecord AcceptanceForDocument(IReadOnlyList<EulaAcceptanceRecord> history, EulaDocument document, DateTimeOffset acceptedAt)
        => history.FirstOrDefault(item => item.Product == document.Product && item.EulaVersion == document.Version && item.ContentHash == document.ContentHash)
            ?? new EulaAcceptanceRecord { Product = document.Product, EulaVersion = document.Version, EffectiveDate = document.EffectiveDate,
                ContentHash = document.ContentHash, AcceptedAtUtc = acceptedAt, Source = document.IsRemote ? "remote" : "packaged" };

    public static EulaAcceptanceRecord? GetAcceptedDocumentForUpload(EulaDocument requiredDocument)
    {
        var acceptance = GetAcceptedDocument();
        if (acceptance is not null && IsStructuredAcceptanceForDocument(acceptance, requiredDocument))
            return acceptance;

        // A legacy stamp does not prove that the current text/hash was accepted.
        return null;
    }

    public static List<EulaAcceptanceRecord> GetAcceptanceHistory()
    {
        var json = AppStorageDatabase.GetLocalAcceptance(HistoryKey);
        List<EulaAcceptanceRecord> history;
        try { history = string.IsNullOrWhiteSpace(json) ? [] : JsonSerializer.Deserialize<List<EulaAcceptanceRecord>>(json, SerializerOptions.Default) ?? []; }
        catch (JsonException) { history = []; }
        using (var db = AppStorageDatabase.OpenConnection())
        using (var command = db.CreateCommand())
        {
            command.CommandText = "SELECT value FROM local_acceptances WHERE substr(key, 1, length($prefix)) = $prefix";
            command.Parameters.AddWithValue("$prefix", HistoryKey + ":");
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                try
                {
                    var item = JsonSerializer.Deserialize<EulaAcceptanceRecord>(reader.GetString(0), SerializerOptions.Default);
                    if (item is not null && !history.Any(existing => existing.Product == item.Product && existing.EulaVersion == item.EulaVersion && existing.ContentHash == item.ContentHash)) history.Add(item);
                }
                catch (JsonException) { /* A damaged entry must not hide the remaining history. */ }
            }
        }
        var current = GetAcceptedDocument();
        if (current is not null && !history.Any(item => item.Product == current.Product
            && item.EulaVersion == current.EulaVersion && item.ContentHash == current.ContentHash)) history.Add(current);
        return history;
    }

    public static bool WasUploaded(EulaAcceptanceRecord acceptance) =>
        AppStorageDatabase.GetLocalAcceptance($"eula_uploaded_{acceptance.EulaVersion}_{acceptance.ContentHash}") == "yes";

    public static void MarkUploaded(EulaAcceptanceRecord acceptance) =>
        AppStorageDatabase.SaveLocalAcceptance($"eula_uploaded_{acceptance.EulaVersion}_{acceptance.ContentHash}", "yes");

    public static void InvalidateDeletedServerAcceptance(EulaAcceptanceRecord acceptance)
    {
        var current = GetAcceptedDocument();
        if (current is null || current.EulaVersion != acceptance.EulaVersion || current.ContentHash != acceptance.ContentHash) return;
        var remaining = GetAcceptanceHistory().Where(item => item.Product != acceptance.Product
            || item.EulaVersion != acceptance.EulaVersion || item.ContentHash != acceptance.ContentHash).ToList();
        using var db = AppStorageDatabase.OpenConnection();
        using var transaction = db.BeginTransaction();
        foreach (var key in new[] { AcceptanceKey, AcceptanceJsonKey,
                     $"{HistoryKey}:{acceptance.Product}:{acceptance.EulaVersion}:{acceptance.ContentHash}",
                     $"eula_uploaded_{acceptance.EulaVersion}_{acceptance.ContentHash}" })
        {
            using var delete = db.CreateCommand();
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM local_acceptances WHERE key = $key";
            delete.Parameters.AddWithValue("$key", key);
            delete.ExecuteNonQuery();
        }
        using (var update = db.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = "UPDATE local_acceptances SET value = $value, updated_at = datetime('now') WHERE key = $key";
            update.Parameters.AddWithValue("$key", HistoryKey);
            update.Parameters.AddWithValue("$value", JsonSerializer.Serialize(remaining, SerializerOptions.Default));
            update.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    /// <summary>
    /// Builds the EULA stamp string without writing it to local storage.
    /// Useful for recording acceptance on the server.
    /// </summary>
    public static string BuildCurrentStamp()
    {
        var existing = GetAcceptedStamp();
        if (!string.IsNullOrEmpty(existing))
            return existing;

        return $"{DateTime.UtcNow:yyyyMMdd}-{EulaContent.Version}";
    }

    public static EulaAcceptanceRecord? GetAcceptedDocument()
    {
        var json = AppStorageDatabase.GetLocalAcceptance(AcceptanceJsonKey);
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<EulaAcceptanceRecord>(json, SerializerOptions.Default); }
        catch { return null; }
    }

    public static bool MatchesAcceptance(EulaDocument document, EulaAcceptanceRecord acceptance) =>
        document.Product == "client" && acceptance.Product == "client"
        && document.Version == acceptance.EulaVersion
        && !string.IsNullOrWhiteSpace(document.Html)
        && string.Equals(document.ContentHash, acceptance.ContentHash, StringComparison.OrdinalIgnoreCase)
        && (string.Equals(EulaDocument.Sha256Hex(document.Html), acceptance.ContentHash, StringComparison.OrdinalIgnoreCase)
            || string.Equals(EulaDocument.Sha256Hex(document.Html.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Trim()), acceptance.ContentHash, StringComparison.OrdinalIgnoreCase));

    public static void CacheDocument(EulaDocument document)
    {
        var identity = new EulaAcceptanceRecord { Product = document.Product, EulaVersion = document.Version, ContentHash = document.ContentHash };
        if (!MatchesAcceptance(document, identity)) return;
        AppStorageDatabase.SaveLocalAcceptance($"eula_document_client:{document.Version}:{document.ContentHash.ToLowerInvariant()}", JsonSerializer.Serialize(document, SerializerOptions.Default));
    }

    public static EulaDocument? CachedDocument(EulaAcceptanceRecord acceptance)
    {
        try
        {
            var json = AppStorageDatabase.GetLocalAcceptance($"eula_document_client:{acceptance.EulaVersion}:{acceptance.ContentHash.ToLowerInvariant()}");
            var document = string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<EulaDocument>(json, SerializerOptions.Default);
            return document is not null && MatchesAcceptance(document, acceptance) ? document : null;
        }
        catch (JsonException) { return null; }
    }

    public static int CompareVersions(string? left, string? right)
    {
        if (Version.TryParse(left, out var l) && Version.TryParse(right, out var r))
            return l.CompareTo(r);
        return string.Compare(left ?? "", right ?? "", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsStructuredAcceptanceForDocument(EulaAcceptanceRecord acceptance, EulaDocument document)
    {
        if (!string.Equals(acceptance.Product, document.Product, StringComparison.OrdinalIgnoreCase))
            return false;

        var versionCompare = CompareVersions(acceptance.EulaVersion, document.Version);
        if (versionCompare < 0)
            return false;

        if (versionCompare == 0
            && document.IsRemote
            && !string.IsNullOrWhiteSpace(document.ContentHash)
            && !string.IsNullOrWhiteSpace(acceptance.ContentHash)
            && !string.Equals(acceptance.ContentHash, document.ContentHash, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// Returns the app version (X.Y.Z) from the entry assembly's
    /// <see cref="AssemblyInformationalVersionAttribute"/> or
    /// <see cref="Assembly.GetName"/> version.
    /// </summary>
    public static string GetAppVersion()
    {
        var asm = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();

        // Prefer InformationalVersion (set by <Version> in csproj).
        var infoAttr = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>();
        if (infoAttr is not null)
        {
            // Strip any +metadata suffix (e.g. "1.9.2+abcdef").
            var ver = infoAttr.InformationalVersion;
            var plusIdx = ver.IndexOf('+');
            return plusIdx > 0 ? ver[..plusIdx] : ver;
        }

        var asmVer = asm.GetName().Version;
        return asmVer is not null
            ? $"{asmVer.Major}.{asmVer.Minor}.{asmVer.Build}"
            : "0.0.0";
    }
}

public sealed class EulaAcceptanceRecord
{
    [JsonPropertyName("product")] public string Product { get; set; } = "client";
    [JsonPropertyName("eula_version")] public string EulaVersion { get; set; } = "";
    [JsonPropertyName("effective_date")] public string EffectiveDate { get; set; } = "";
    [JsonPropertyName("content_hash")] public string ContentHash { get; set; } = "";
    [JsonPropertyName("accepted_at_utc")] public DateTimeOffset AcceptedAtUtc { get; set; }
    [JsonPropertyName("source")] public string Source { get; set; } = "packaged";
}
