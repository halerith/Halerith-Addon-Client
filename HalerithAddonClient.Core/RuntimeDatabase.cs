using Microsoft.Data.Sqlite;

namespace HalerithAddonClient.Core;

// ══════════════════════════════════════════════════════════════════════════════
//  Runtime Database — tracks which files belong to which game environment
// ══════════════════════════════════════════════════════════════════════════════
//
//  SQLite database at %LOCALAPPDATA%\Halerith Addon Client\storage.
//
//  Tables:
//    instances      — one row per built game environment (MC version + loader)
//    instance_files — one row per file tracked by an instance (libraries,
//                     assets, Java runtime files)
//
//  Files that are already cleanly isolated by directory (versions/{mcVersion},
//  forge/{version}, neoforge/{version}, instances/{serverPublicId}) are NOT tracked
//  here — they are deleted directly by path. Only shared-pool files that can
//  be referenced by multiple instances are tracked:
//    - libraries/**
//    - assets/indexes/**
//    - assets/objects/**
//    - java/{component}/**
// ══════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Represents a tracked game environment instance in the Runtime database.
/// </summary>
public sealed class RuntimeInstance
{
    public long   Id              { get; set; }
    public string MinecraftVersion { get; set; } = "";
    public string Loader          { get; set; } = "";
    public string LoaderVersion   { get; set; } = "";
    public string CreatedAt       { get; set; } = "";
}

/// <summary>
/// Represents a tracked file belonging to an instance.
/// </summary>
public sealed class RuntimeInstanceFile
{
    public long   InstanceId { get; set; }
    public string FilePath   { get; set; } = "";
    public long   SizeBytes  { get; set; }
    public string Sha1       { get; set; } = "";
    public string Category   { get; set; } = "";   // "library", "asset_index", "asset_object", "java"
}

/// <summary>
/// Summary of a tracked instance for display in the Instances tab.
/// </summary>
public sealed class RuntimeInstanceSummary
{
    public long   Id              { get; set; }
    public string MinecraftVersion { get; set; } = "";
    public string Loader          { get; set; } = "";
    public string LoaderVersion   { get; set; } = "";
    public string CreatedAt       { get; set; } = "";
    public long   TotalSizeBytes  { get; set; }
    public int    FileCount       { get; set; }
}

public sealed class RuntimeDatabase : IDisposable
{
    private readonly SqliteConnection _db;

    public RuntimeDatabase(string runtimeRoot)
    {
        Directory.CreateDirectory(runtimeRoot);

        _db = AppStorageDatabase.OpenConnection();
        InitializeSchema();
    }

    // ──────────────────────────────────────────────────────────────────────
    //  Schema
    // ──────────────────────────────────────────────────────────────────────

    private void InitializeSchema()
    {
        Execute("""
            CREATE TABLE IF NOT EXISTS instances (
                id              INTEGER PRIMARY KEY AUTOINCREMENT,
                mc_version      TEXT    NOT NULL,
                loader          TEXT    NOT NULL DEFAULT '',
                loader_version  TEXT    NOT NULL DEFAULT '',
                created_at      TEXT    NOT NULL DEFAULT (datetime('now'))
            )
            """);

        Execute("""
            CREATE TABLE IF NOT EXISTS instance_files (
                instance_id     INTEGER NOT NULL REFERENCES instances(id) ON DELETE CASCADE,
                file_path       TEXT    NOT NULL,
                size_bytes      INTEGER NOT NULL DEFAULT 0,
                sha1            TEXT    NOT NULL DEFAULT '',
                category        TEXT    NOT NULL DEFAULT '',
                PRIMARY KEY (instance_id, file_path)
            )
            """);

        // Index for orphan queries: find files with zero remaining references
        Execute("""
            CREATE INDEX IF NOT EXISTS idx_instance_files_path
            ON instance_files(file_path)
            """);

        // Ensure cascade deletes work
        Execute("PRAGMA foreign_keys = ON");
    }

    // ──────────────────────────────────────────────────────────────────────
    //  Instance lifecycle
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Creates a new instance record and returns its ID.
    /// If an instance with the same MC version + loader + loader version already exists,
    /// deletes it first (rebuild scenario).
    /// </summary>
    public long CreateInstance(string mcVersion, string loader, string loaderVersion)
    {
        var normalizedMcVersion = NormalizeMinecraftVersion(mcVersion);
        var normalizedLoader = NormalizeLoader(loader);
        var normalizedLoaderVersion = NormalizeLoaderVersion(loaderVersion);

        // Delete existing instance for the same environment (rebuild)
        var existingId = GetInstanceId(normalizedMcVersion, normalizedLoader, normalizedLoaderVersion);
        if (existingId is not null)
            DeleteInstance(existingId.Value);

        using var cmd = _db.CreateCommand();
        cmd.CommandText = """
            INSERT INTO instances (mc_version, loader, loader_version)
            VALUES ($mc, $loader, $lv);
            SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("$mc", normalizedMcVersion);
        cmd.Parameters.AddWithValue("$loader", normalizedLoader);
        cmd.Parameters.AddWithValue("$lv", normalizedLoaderVersion);

        return (long)cmd.ExecuteScalar()!;
    }

    /// <summary>
    /// Finds the instance ID for a given MC version + loader combo, or null if not found.
    /// </summary>
    public long? GetInstanceId(string mcVersion, string loader, string loaderVersion)
    {
        var normalizedMcVersion = NormalizeMinecraftVersion(mcVersion);
        var normalizedLoader = NormalizeLoader(loader);
        var normalizedLoaderVersion = NormalizeLoaderVersion(loaderVersion);

        using var cmd = _db.CreateCommand();
        cmd.CommandText = """
            SELECT id FROM instances
            WHERE mc_version = $mc AND loader = $loader AND loader_version = $lv
            LIMIT 1
            """;
        cmd.Parameters.AddWithValue("$mc", normalizedMcVersion);
        cmd.Parameters.AddWithValue("$loader", normalizedLoader);
        cmd.Parameters.AddWithValue("$lv", normalizedLoaderVersion);

        var result = cmd.ExecuteScalar();
        return result is long id ? id : null;
    }

    /// <summary>
    /// Deletes an instance and returns the list of file paths that are now orphaned
    /// (no longer referenced by any remaining instance).
    /// </summary>
    public List<string> DeleteInstance(long instanceId)
    {
        // Collect files that will become orphaned after this delete
        var orphans = GetOrphanedFilesAfterDelete(instanceId);

        using var cmd = _db.CreateCommand();
        cmd.CommandText = "DELETE FROM instances WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", instanceId);
        cmd.ExecuteNonQuery();

        return orphans;
    }

    /// <summary>
    /// Returns file paths that would become orphaned if the given instance were deleted.
    /// These are files referenced ONLY by this instance and no others.
    /// </summary>
    public List<string> GetOrphanedFilesAfterDelete(long instanceId)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = """
            SELECT f.file_path
            FROM instance_files f
            WHERE f.instance_id = $id
              AND NOT EXISTS (
                  SELECT 1 FROM instance_files other
                  WHERE other.file_path = f.file_path
                    AND other.instance_id != $id
              )
            """;
        cmd.Parameters.AddWithValue("$id", instanceId);

        var orphans = new List<string>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            orphans.Add(reader.GetString(0));
        return orphans;
    }

    // ──────────────────────────────────────────────────────────────────────
    //  File tracking
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Records a single file as belonging to an instance.
    /// If the file is already tracked for this instance, the record is updated.
    /// </summary>
    public void AddFile(long instanceId, string filePath, long sizeBytes, string sha1, string category)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = """
            INSERT INTO instance_files (instance_id, file_path, size_bytes, sha1, category)
            VALUES ($iid, $path, $size, $sha1, $cat)
            ON CONFLICT(instance_id, file_path) DO UPDATE
            SET size_bytes = excluded.size_bytes,
                sha1       = excluded.sha1,
                category   = excluded.category
            """;
        cmd.Parameters.AddWithValue("$iid", instanceId);
        cmd.Parameters.AddWithValue("$path", filePath);
        cmd.Parameters.AddWithValue("$size", sizeBytes);
        cmd.Parameters.AddWithValue("$sha1", sha1);
        cmd.Parameters.AddWithValue("$cat", category);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Records multiple files as belonging to an instance in a single transaction.
    /// </summary>
    public void AddFiles(long instanceId, IEnumerable<RuntimeInstanceFile> files)
    {
        using var transaction = _db.BeginTransaction();
        try
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = """
                INSERT INTO instance_files (instance_id, file_path, size_bytes, sha1, category)
                VALUES ($iid, $path, $size, $sha1, $cat)
                ON CONFLICT(instance_id, file_path) DO UPDATE
                SET size_bytes = excluded.size_bytes,
                    sha1       = excluded.sha1,
                    category   = excluded.category
                """;

            var pIid  = cmd.Parameters.Add("$iid", SqliteType.Integer);
            var pPath = cmd.Parameters.Add("$path", SqliteType.Text);
            var pSize = cmd.Parameters.Add("$size", SqliteType.Integer);
            var pSha1 = cmd.Parameters.Add("$sha1", SqliteType.Text);
            var pCat  = cmd.Parameters.Add("$cat", SqliteType.Text);

            foreach (var file in files)
            {
                pIid.Value  = instanceId;
                pPath.Value = file.FilePath;
                pSize.Value = file.SizeBytes;
                pSha1.Value = file.Sha1;
                pCat.Value  = file.Category;
                cmd.ExecuteNonQuery();
            }

            transaction.Commit();
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    // ──────────────────────────────────────────────────────────────────────
    //  Queries
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns all tracked instances with their total file size and count.
    /// </summary>
    public List<RuntimeInstanceSummary> GetAllInstances()
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = """
            SELECT
                i.id,
                i.mc_version,
                i.loader,
                i.loader_version,
                i.created_at,
                COALESCE(SUM(f.size_bytes), 0) AS total_size,
                COUNT(f.file_path) AS file_count
            FROM instances i
            LEFT JOIN instance_files f ON f.instance_id = i.id
            GROUP BY i.id
            ORDER BY i.mc_version, i.loader
            """;

        var results = new List<RuntimeInstanceSummary>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            results.Add(new RuntimeInstanceSummary
            {
                Id = reader.GetInt64(0),
                MinecraftVersion = reader.GetString(1),
                Loader = reader.GetString(2),
                LoaderVersion = reader.GetString(3),
                CreatedAt = reader.GetString(4),
                TotalSizeBytes = reader.GetInt64(5),
                FileCount = reader.GetInt32(6),
            });
        }
        return results;
    }

    /// <summary>
    /// Returns true if an instance exists for the given environment.
    /// </summary>
    public bool InstanceExists(string mcVersion, string loader, string loaderVersion)
        => GetInstanceId(mcVersion, loader, loaderVersion) is not null;

    /// <summary>
    /// Returns all file paths tracked globally (for diagnostics / orphan detection).
    /// </summary>
    public HashSet<string> GetAllTrackedFilePaths()
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "SELECT DISTINCT file_path FROM instance_files";

        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            paths.Add(reader.GetString(0));
        return paths;
    }

    // ──────────────────────────────────────────────────────────────────────
    //  Helpers
    // ──────────────────────────────────────────────────────────────────────

    private void Execute(string sql)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static string NormalizeMinecraftVersion(string value) => (value ?? "").Trim();

    private static string NormalizeLoader(string value) => (value ?? "").Trim().ToLowerInvariant();

    private static string NormalizeLoaderVersion(string value) => (value ?? "").Trim();

    public void Dispose()
    {
        _db.Close();
        _db.Dispose();
    }
}
