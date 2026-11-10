using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HalerithAddonClient.Core.Platform;
using Microsoft.Data.Sqlite;

namespace HalerithAddonClient.Core;

/// <summary>
/// Owns the single client SQLite storage file at
/// the platform-specific Halerith Addon Client application data folder.
/// </summary>
public static class AppStorageDatabase
{
    private static readonly object SchemaGate = new();
    private static readonly Dictionary<string, long> InitializedSchemas = new(StringComparer.Ordinal);
    private const long VacuumMinimumBytes = 50L * 1024L * 1024L;
    private const int VacuumIntervalDays = 14;
    private const string LastVacuumSettingKey = "storage_last_vacuum_utc";

    public static string StoragePath =>
        Path.Combine(LauncherPaths.GetAppDataDirectory(), "storage");

    public static SqliteConnection OpenConnection()
    {
        var appDataDirectory = LauncherPaths.GetAppDataDirectory();
        if (OperatingSystem.IsLinux())
        {
            Directory.CreateDirectory(
                appDataDirectory,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        else
        {
            Directory.CreateDirectory(appDataDirectory);
        }
        RestrictLinuxStoragePermissions(appDataDirectory, directory: true);

        var connection = new SqliteConnection($"Data Source={StoragePath}");
        try
        {
            connection.Open();
            RestrictLinuxStorageFiles();

            Execute(connection, "PRAGMA journal_mode=WAL");
            Execute(connection, "PRAGMA foreign_keys=ON");
            EnsureSchemaInitialized(connection);
            RestrictLinuxStorageFiles();

            if (PlatformServices.Current.Secrets is LinuxSecretProtector linuxSecrets)
                linuxSecrets.PrepareStorage(connection);

            RestrictLinuxStorageFiles();
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    internal static void EnsureSchemaInitialized(SqliteConnection connection)
    {
        lock (SchemaGate)
        {
            using var version = connection.CreateCommand();
            version.CommandText = "PRAGMA schema_version";
            var current = Convert.ToInt64(version.ExecuteScalar());
            // Recheck external schema changes. Never cache a failed migration or an anonymous in-memory database.
            var cacheable = !string.IsNullOrWhiteSpace(connection.DataSource) && connection.DataSource != ":memory:";
            if (cacheable && InitializedSchemas.TryGetValue(connection.DataSource, out var initialized) && initialized == current)
                return;
            InitializeSchema(connection);
            if (cacheable) InitializedSchemas[connection.DataSource] = Convert.ToInt64(version.ExecuteScalar());
        }
    }

    private static void RestrictLinuxStorageFiles()
    {
        if (!OperatingSystem.IsLinux())
            return;

        foreach (var path in new[] { StoragePath, StoragePath + "-wal", StoragePath + "-shm" })
        {
            if (File.Exists(path))
                RestrictLinuxStoragePermissions(path, directory: false);
        }
    }

    private static void RestrictLinuxStoragePermissions(string path, bool directory)
    {
        if (!OperatingSystem.IsLinux())
            return;

        try
        {
            var mode = directory
                ? UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                : UnixFileMode.UserRead | UnixFileMode.UserWrite;
            File.SetUnixFileMode(path, mode);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            throw new SecretStorageUnavailableException(
                "Halerith could not secure its Linux storage permissions.",
                ex);
        }
    }

    public static void EnsureCreated()
    {
        using var _ = OpenConnection();
    }

    public static void MaintainStorage(Action<string>? log = null)
    {
        try
        {
            using var db = OpenConnection();

            Execute(db, "PRAGMA wal_checkpoint(TRUNCATE)");
            Execute(db, "PRAGMA optimize");

            if (!ShouldVacuum(db))
                return;

            log?.Invoke($"Vacuuming SQLite storage ({GetStorageSizeBytes() / 1024 / 1024} MB).");
            Execute(db, "VACUUM");

            using var tx = db.BeginTransaction();
            UpsertSetting(db, tx, LastVacuumSettingKey, DateTime.UtcNow.ToString("O"));
            tx.Commit();
        }
        catch (Exception ex)
        {
            log?.Invoke($"SQLite storage maintenance skipped: {ex.Message}");
        }
    }

    public static AppConfig LoadAppConfig()
    {
        using var db = OpenConnection();
        var settings = LoadSettings(db);

        var app = new AppConfig
        {
            DefaultHostId = ManagerApiDefaults.ManagerScopeId,
            StartupTimeoutSeconds = GetIntSetting(settings, "startup_timeout_seconds", 60),
            LauncherCountdownSeconds = GetIntSetting(settings, "launcher_countdown_seconds", 3),
            EnableEventLogging = GetBoolSetting(settings, "enable_event_logging", false),
            GuidedTipsEnabled = GetBoolSetting(settings, "guided_tips_enabled", true),
            AllowAutoUpdate = GetBoolSetting(settings, "allow_auto_update", true),
            KeepBackups = GetBoolSetting(settings, "keep_backups", true),
            BackupMaxFilesPerFolder = GetIntSetting(settings, "backup_max_files_per_folder", 20),
            DefaultInstanceKind = NormalizeDefaultInstanceKind(GetSetting(settings, "default_instance_kind")),
            DefaultInstanceId = GetSetting(settings, "default_instance_id"),
        };

        app.Hosts.AddRange(LoadHosts(db));
        app.EnsureManagerScope();
        app.LocalInstances.AddRange(LoadLocalInstances(db));
        NormalizeDefaultInstance(app);
        return app;
    }

    public static void SaveAppConfig(AppConfig config)
    {
        using var db = OpenConnection();
        using var tx = db.BeginTransaction();

        config.EnsureManagerScope();
        NormalizeDefaultInstance(config);
        SaveSettings(db, tx, config);
        SaveHosts(db, tx, config.Hosts);
        SaveLocalInstances(db, tx, config.LocalInstances);

        tx.Commit();
    }

    public static string GetAppSetting(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
            return "";

        using var db = OpenConnection();
        var settings = LoadSettings(db);
        return GetSetting(settings, key.Trim());
    }

    public static void SaveAppSetting(string key, string? value)
    {
        if (string.IsNullOrWhiteSpace(key))
            return;

        using var db = OpenConnection();
        using var tx = db.BeginTransaction();
        UpsertSetting(db, tx, key.Trim(), value ?? "");
        tx.Commit();
    }

    public static void DeleteAppSetting(string key)
    {
        if (string.IsNullOrWhiteSpace(key)) return;
        using var db = OpenConnection();
        using var command = db.CreateCommand();
        command.CommandText = "DELETE FROM app_settings WHERE key = $key";
        command.Parameters.AddWithValue("$key", key.Trim());
        command.ExecuteNonQuery();
    }

    public static void SaveLastInjectedServerAddress(string hostId, string serverPublicId, string address)
    {
        if (string.IsNullOrWhiteSpace(hostId)
            || string.IsNullOrWhiteSpace(serverPublicId)
            || string.IsNullOrWhiteSpace(address))
            return;

        using var db = OpenConnection();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            UPDATE servers
            SET last_injected_server_address = $address, updated_at = datetime('now')
            WHERE host_id = $host AND server_public_id = $server
            """;
        cmd.Parameters.AddWithValue("$address", address.Trim());
        cmd.Parameters.AddWithValue("$host", hostId);
        cmd.Parameters.AddWithValue("$server", serverPublicId);
        cmd.ExecuteNonQuery();
    }

    public static void SaveClientAccessTokenForHost(
        string hostId,
        string accessToken,
        string expiresAtUtc)
    {
        if (string.IsNullOrWhiteSpace(hostId)) return;

        using var db = OpenConnection();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            UPDATE hosts SET
                halerith_access_token_protected = $token,
                halerith_access_token_expires_at_utc = $expires,
                updated_at = datetime('now')
            WHERE id = $id
            """;
        cmd.Parameters.AddWithValue("$id", hostId);
        cmd.Parameters.Add("$token", SqliteType.Blob).Value =
            string.IsNullOrWhiteSpace(accessToken) ? DBNull.Value : ProtectString(accessToken);
        cmd.Parameters.AddWithValue("$expires", expiresAtUtc?.Trim() ?? "");
        cmd.ExecuteNonQuery();
    }

    public static List<MinecraftAccount> LoadMinecraftAccounts()
    {
        using var db = OpenConnection();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT uuid, gamertag, xuid, ms_access_token_protected, ms_refresh_token_protected,
                   mc_access_token_protected, expires_at_utc
            FROM minecraft_accounts
            ORDER BY gamertag COLLATE NOCASE, uuid COLLATE NOCASE
            """;

        var accounts = new List<MinecraftAccount>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            accounts.Add(new MinecraftAccount
            {
                Uuid = reader.GetString(0),
                Gamertag = reader.GetString(1),
                Xuid = reader.GetString(2),
                MsAccessToken = reader.IsDBNull(3) ? "" : UnprotectString((byte[])reader.GetValue(3)),
                MsRefreshToken = reader.IsDBNull(4) ? "" : UnprotectString((byte[])reader.GetValue(4)),
                McAccessToken = reader.IsDBNull(5) ? "" : UnprotectString((byte[])reader.GetValue(5)),
                ExpiresAtUtc = ParseUtc(reader.GetString(6)),
            });
        }

        return accounts;
    }

    public static string GetDefaultMinecraftAccountId()
    {
        using var db = OpenConnection();
        var settings = LoadSettings(db);
        return GetSetting(settings, "default_account_id");
    }

    public static void SaveMinecraftAccount(MinecraftAccount account)
    {
        if (string.IsNullOrWhiteSpace(account.Uuid))
            return;

        using var db = OpenConnection();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            INSERT INTO minecraft_accounts (
                uuid, gamertag, xuid, ms_access_token_protected, ms_refresh_token_protected,
                mc_access_token_protected, expires_at_utc, updated_at
            )
            VALUES (
                $uuid, $gamertag, $xuid, $msAccess, $msRefresh, $mcAccess, $expires, datetime('now')
            )
            ON CONFLICT(uuid) DO UPDATE SET
                gamertag = excluded.gamertag,
                xuid = excluded.xuid,
                ms_access_token_protected = excluded.ms_access_token_protected,
                ms_refresh_token_protected = excluded.ms_refresh_token_protected,
                mc_access_token_protected = excluded.mc_access_token_protected,
                expires_at_utc = excluded.expires_at_utc,
                updated_at = datetime('now')
            """;
        cmd.Parameters.AddWithValue("$uuid", account.Uuid);
        cmd.Parameters.AddWithValue("$gamertag", account.Gamertag ?? "");
        cmd.Parameters.AddWithValue("$xuid", account.Xuid ?? "");
        AddProtectedParameter(cmd, "$msAccess", account.MsAccessToken);
        AddProtectedParameter(cmd, "$msRefresh", account.MsRefreshToken);
        AddProtectedParameter(cmd, "$mcAccess", account.McAccessToken);
        cmd.Parameters.AddWithValue("$expires", account.ExpiresAtUtc.ToUniversalTime().ToString("O"));
        cmd.ExecuteNonQuery();
    }

    public static void SetDefaultMinecraftAccountId(string uuid)
    {
        using var db = OpenConnection();
        using var tx = db.BeginTransaction();
        UpsertSetting(db, tx, "default_account_id", uuid ?? "");
        tx.Commit();
    }

    public static void RemoveMinecraftAccount(string uuid)
    {
        if (string.IsNullOrWhiteSpace(uuid))
            return;

        using var db = OpenConnection();
        using var tx = db.BeginTransaction();

        using (var cmd = db.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "DELETE FROM minecraft_accounts WHERE uuid = $uuid";
            cmd.Parameters.AddWithValue("$uuid", uuid);
            cmd.ExecuteNonQuery();
        }

        var settings = LoadSettings(db);
        if (string.Equals(GetSetting(settings, "default_account_id"), uuid, StringComparison.OrdinalIgnoreCase))
            UpsertSetting(db, tx, "default_account_id", "");

        tx.Commit();
    }

    public static ImageCacheRecord? LoadImageCacheRecord(string cacheKey)
    {
        if (string.IsNullOrWhiteSpace(cacheKey))
            return null;

        using var db = OpenConnection();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT cache_key, image_kind, source, source_id, slug, content_type, url,
                   local_path, width, height, last_seen_utc, last_accessed_utc,
                   downloaded_at_utc, image_hash, failed_at_utc, failure_count
            FROM image_cache
            WHERE cache_key = $key
            """;
        cmd.Parameters.AddWithValue("$key", cacheKey);

        using var reader = cmd.ExecuteReader();
        return reader.Read()
            ? ReadImageCacheRecord(reader)
            : null;
    }

    public static void UpsertImageCacheRecord(ImageCacheRecord record)
    {
        if (string.IsNullOrWhiteSpace(record.CacheKey))
            return;

        using var db = OpenConnection();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            INSERT INTO image_cache (
                cache_key, image_kind, source, source_id, slug, content_type, url,
                local_path, width, height, last_seen_utc, last_accessed_utc,
                downloaded_at_utc, image_hash, failed_at_utc, failure_count, updated_at
            )
            VALUES (
                $key, $kind, $source, $sourceId, $slug, $contentType, $url,
                $path, $width, $height, $seen, $accessed,
                $downloaded, $imageHash, $failed, $failureCount, datetime('now')
            )
            ON CONFLICT(cache_key) DO UPDATE SET
                image_kind = excluded.image_kind,
                source = excluded.source,
                source_id = excluded.source_id,
                slug = excluded.slug,
                content_type = excluded.content_type,
                url = excluded.url,
                local_path = excluded.local_path,
                width = excluded.width,
                height = excluded.height,
                last_seen_utc = excluded.last_seen_utc,
                last_accessed_utc = excluded.last_accessed_utc,
                downloaded_at_utc = excluded.downloaded_at_utc,
                image_hash = excluded.image_hash,
                failed_at_utc = excluded.failed_at_utc,
                failure_count = excluded.failure_count,
                updated_at = datetime('now')
            """;
        BindImageCacheRecord(cmd, record);
        cmd.ExecuteNonQuery();
    }

    public static void MarkImageCacheAccessed(string cacheKey)
    {
        if (string.IsNullOrWhiteSpace(cacheKey))
            return;

        using var db = OpenConnection();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            UPDATE image_cache
            SET last_accessed_utc = $now,
                updated_at = datetime('now')
            WHERE cache_key = $key
            """;
        cmd.Parameters.AddWithValue("$key", cacheKey);
        cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
        cmd.ExecuteNonQuery();
    }

    public static void MarkImageCacheDownloaded(string cacheKey, string localPath, string? imageHash = null)
    {
        if (string.IsNullOrWhiteSpace(cacheKey))
            return;

        using var db = OpenConnection();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            UPDATE image_cache
            SET local_path = $path,
                downloaded_at_utc = $now,
                image_hash = COALESCE(NULLIF($imageHash, ''), image_hash),
                last_accessed_utc = $now,
                failed_at_utc = '',
                failure_count = 0,
                updated_at = datetime('now')
            WHERE cache_key = $key
            """;
        cmd.Parameters.AddWithValue("$key", cacheKey);
        cmd.Parameters.AddWithValue("$path", localPath ?? "");
        cmd.Parameters.AddWithValue("$imageHash", imageHash ?? "");
        cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
        cmd.ExecuteNonQuery();
    }

    public static void MarkImageCacheFailed(string cacheKey)
    {
        if (string.IsNullOrWhiteSpace(cacheKey))
            return;

        using var db = OpenConnection();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            UPDATE image_cache
            SET failed_at_utc = $now,
                failure_count = failure_count + 1,
                updated_at = datetime('now')
            WHERE cache_key = $key
            """;
        cmd.Parameters.AddWithValue("$key", cacheKey);
        cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
        cmd.ExecuteNonQuery();
    }

    public static void SaveServerContentInventory(
        string hostId,
        string serverPublicId,
        IEnumerable<ContentInventoryItem> items)
    {
        if (string.IsNullOrWhiteSpace(hostId) || string.IsNullOrWhiteSpace(serverPublicId))
            return;

        using var db = OpenConnection();
        using var tx = db.BeginTransaction();

        using (var delete = db.CreateCommand())
        {
            delete.Transaction = tx;
            delete.CommandText = "DELETE FROM server_content_items WHERE host_id = $host AND server_public_id = $server";
            delete.Parameters.AddWithValue("$host", hostId);
            delete.Parameters.AddWithValue("$server", serverPublicId);
            delete.ExecuteNonQuery();
        }

        using var insert = db.CreateCommand();
        insert.Transaction = tx;
        insert.CommandText = """
            INSERT INTO server_content_items (
                host_id, server_public_id, content_type, source, source_id, slug, name, icon_url,
                source_version_id, version_name, version_number, download_filename, file_hash,
                mc_version, loader, download_url, local_path, installed_at_utc, pack_id, pack_revision, pack_file_hash, updated_at
            )
            VALUES (
                $host, $server, $type, $source, $sourceId, $slug, $name, $icon,
                $sourceVersionId, $versionName, $versionNumber, $filename, $hash,
                $mcVersion, $loader, $downloadUrl, $path, $installedAt, $packId, $packRevision, $packHash, datetime('now')
            )
            """;
        AddContentParameters(insert, includeOwner: true);

        foreach (var item in items)
        {
            SetContentParameterValues(insert, item, hostId, serverPublicId);
            insert.ExecuteNonQuery();
        }

        tx.Commit();
    }

    public static List<ContentInventoryItem> LoadServerContentInventory(string hostId, string serverPublicId)
    {
        if (string.IsNullOrWhiteSpace(hostId) || string.IsNullOrWhiteSpace(serverPublicId))
            return [];

        using var db = OpenConnection();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT
                content_type, source, source_id, slug, name, icon_url,
                source_version_id, version_name, version_number, download_filename, file_hash,
                mc_version, loader, download_url, local_path, installed_at_utc, pack_id, pack_revision, pack_file_hash
            FROM server_content_items
            WHERE host_id = $host AND server_public_id = $server
            ORDER BY content_type, name COLLATE NOCASE, download_filename COLLATE NOCASE, local_path COLLATE NOCASE
            """;
        cmd.Parameters.AddWithValue("$host", hostId);
        cmd.Parameters.AddWithValue("$server", serverPublicId);

        return ReadContentInventory(cmd);
    }

    public static void SaveLocalInstanceContentInventory(
        string localInstanceId,
        IEnumerable<ContentInventoryItem> items)
    {
        if (string.IsNullOrWhiteSpace(localInstanceId))
            return;

        using var db = OpenConnection();
        using var tx = db.BeginTransaction();

        using (var delete = db.CreateCommand())
        {
            delete.Transaction = tx;
            delete.CommandText = "DELETE FROM local_instance_content_items WHERE local_instance_id = $id";
            delete.Parameters.AddWithValue("$id", localInstanceId);
            delete.ExecuteNonQuery();
        }

        using var insert = db.CreateCommand();
        insert.Transaction = tx;
        insert.CommandText = """
            INSERT INTO local_instance_content_items (
                local_instance_id, content_type, source, source_id, slug, name, icon_url,
                source_version_id, version_name, version_number, download_filename, file_hash,
                mc_version, loader, download_url, local_path, installed_at_utc, pack_id, pack_revision, pack_file_hash, updated_at
            )
            VALUES (
                $localInstance, $type, $source, $sourceId, $slug, $name, $icon,
                $sourceVersionId, $versionName, $versionNumber, $filename, $hash,
                $mcVersion, $loader, $downloadUrl, $path, $installedAt, $packId, $packRevision, $packHash, datetime('now')
            )
            """;
        AddContentParameters(insert, includeOwner: false);

        foreach (var item in items)
        {
            SetContentParameterValues(insert, item, localInstanceId, "");
            insert.ExecuteNonQuery();
        }

        tx.Commit();
    }

    public static List<ContentInventoryItem> LoadLocalInstanceContentInventory(string localInstanceId)
    {
        if (string.IsNullOrWhiteSpace(localInstanceId))
            return [];

        using var db = OpenConnection();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT
                content_type, source, source_id, slug, name, icon_url,
                source_version_id, version_name, version_number, download_filename, file_hash,
                mc_version, loader, download_url, local_path, installed_at_utc, pack_id, pack_revision, pack_file_hash
            FROM local_instance_content_items
            WHERE local_instance_id = $id
            ORDER BY content_type, name COLLATE NOCASE, download_filename COLLATE NOCASE, local_path COLLATE NOCASE
            """;
        cmd.Parameters.AddWithValue("$id", localInstanceId);

        return ReadContentInventory(cmd);
    }

    public static void UpsertLocalInstanceContentItem(
        string localInstanceId,
        ContentInventoryItem item)
    {
        if (string.IsNullOrWhiteSpace(localInstanceId))
            return;

        using var db = OpenConnection();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            INSERT INTO local_instance_content_items (
                local_instance_id, content_type, source, source_id, slug, name, icon_url,
                source_version_id, version_name, version_number, download_filename, file_hash,
                mc_version, loader, download_url, local_path, installed_at_utc, updated_at
            )
            VALUES (
                $localInstance, $type, $source, $sourceId, $slug, $name, $icon,
                $sourceVersionId, $versionName, $versionNumber, $filename, $hash,
                $mcVersion, $loader, $downloadUrl, $path, $installedAt, datetime('now')
            )
            ON CONFLICT(local_instance_id, content_type, local_path) DO UPDATE SET
                pack_id = '', pack_revision = '', pack_file_hash = '',
                source = excluded.source,
                source_id = excluded.source_id,
                slug = excluded.slug,
                name = excluded.name,
                icon_url = excluded.icon_url,
                source_version_id = excluded.source_version_id,
                version_name = excluded.version_name,
                version_number = excluded.version_number,
                download_filename = excluded.download_filename,
                file_hash = excluded.file_hash,
                mc_version = excluded.mc_version,
                loader = excluded.loader,
                download_url = excluded.download_url,
                installed_at_utc = excluded.installed_at_utc,
                updated_at = datetime('now')
            """;
        AddContentParameters(cmd, includeOwner: false);
        SetContentParameterValues(cmd, item, localInstanceId, "");
        cmd.ExecuteNonQuery();
    }

    public static bool RemoveLocalInstanceContentItem(string localInstanceId, string source, string sourceId)
    {
        if (string.IsNullOrWhiteSpace(localInstanceId) ||
            string.IsNullOrWhiteSpace(source) ||
            string.IsNullOrWhiteSpace(sourceId))
            return false;

        using var db = OpenConnection();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            DELETE FROM local_instance_content_items
            WHERE local_instance_id = $id
              AND source = $source
              AND source_id = $sourceId
            """;
        cmd.Parameters.AddWithValue("$id", localInstanceId);
        cmd.Parameters.AddWithValue("$source", source);
        cmd.Parameters.AddWithValue("$sourceId", sourceId);
        return cmd.ExecuteNonQuery() > 0;
    }

    public static HashSet<string> GetLocalInstanceInstalledContentKeys(string localInstanceId)
    {
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(localInstanceId))
            return keys;

        using var db = OpenConnection();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT source, source_id
            FROM local_instance_content_items
            WHERE local_instance_id = $id
              AND source <> ''
              AND source_id <> ''
            """;
        cmd.Parameters.AddWithValue("$id", localInstanceId);

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            keys.Add($"{reader.GetString(0)}:{reader.GetString(1)}");

        return keys;
    }

    public static void SaveBuiltInGameSession(BuiltInGameSessionRecord record)
    {
        if (string.IsNullOrWhiteSpace(record.Target.Kind) || string.IsNullOrWhiteSpace(record.Target.Id))
            return;

        using var db = OpenConnection();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            INSERT INTO builtin_game_sessions (
                target_kind, host_id, target_id, pid, state, game_directory,
                minecraft_version, loader, loader_version, started_at_utc,
                process_started_at_utc, updated_at
            )
            VALUES (
                $kind, $host, $target, $pid, $state, $gameDir, $mc, $loader,
                $loaderVersion, $startedAt, $processStartedAt, datetime('now')
            )
            ON CONFLICT(target_kind, host_id, target_id) DO UPDATE SET
                pid = excluded.pid,
                state = excluded.state,
                game_directory = excluded.game_directory,
                minecraft_version = excluded.minecraft_version,
                loader = excluded.loader,
                loader_version = excluded.loader_version,
                started_at_utc = excluded.started_at_utc,
                process_started_at_utc = excluded.process_started_at_utc,
                updated_at = datetime('now')
            """;
        cmd.Parameters.AddWithValue("$kind", record.Target.Kind.Trim());
        cmd.Parameters.AddWithValue("$host", record.Target.HostId?.Trim() ?? "");
        cmd.Parameters.AddWithValue("$target", record.Target.Id.Trim());
        cmd.Parameters.AddWithValue("$pid", record.Pid);
        cmd.Parameters.AddWithValue("$state", record.State.ToString());
        cmd.Parameters.AddWithValue("$gameDir", record.GameDirectory ?? "");
        cmd.Parameters.AddWithValue("$mc", record.MinecraftVersion ?? "");
        cmd.Parameters.AddWithValue("$loader", record.Loader ?? "");
        cmd.Parameters.AddWithValue("$loaderVersion", record.LoaderVersion ?? "");
        cmd.Parameters.AddWithValue("$startedAt", record.StartedAtUtc.ToUniversalTime().ToString("O"));
        cmd.Parameters.AddWithValue("$processStartedAt", record.ProcessStartedAtUtc?.ToUniversalTime().ToString("O") ?? "");
        cmd.ExecuteNonQuery();
    }

    public static BuiltInGameSessionRecord? LoadBuiltInGameSession(BuiltInGameSessionTarget target)
    {
        if (string.IsNullOrWhiteSpace(target.Kind) || string.IsNullOrWhiteSpace(target.Id))
            return null;

        using var db = OpenConnection();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT pid, state, game_directory, minecraft_version, loader,
                   loader_version, started_at_utc, process_started_at_utc
            FROM builtin_game_sessions
            WHERE target_kind = $kind
              AND host_id = $host
              AND target_id = $target
            """;
        cmd.Parameters.AddWithValue("$kind", target.Kind.Trim());
        cmd.Parameters.AddWithValue("$host", target.HostId?.Trim() ?? "");
        cmd.Parameters.AddWithValue("$target", target.Id.Trim());

        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
            return null;

        var pid = reader.GetInt32(0);
        var gameDir = reader.GetString(2);
        if (pid <= 0 || string.IsNullOrWhiteSpace(gameDir))
            return null;

        var stateText = reader.GetString(1);
        var state = Enum.TryParse<BuiltInGameSessionState>(stateText, ignoreCase: true, out var parsed)
            ? parsed
            : BuiltInGameSessionState.Launching;

        var startedAt = ParseUtc(reader.GetString(6));
        if (startedAt == DateTime.MinValue)
            startedAt = DateTime.UtcNow;

        var processStartedAt = ParseUtc(reader.GetString(7));

        return new BuiltInGameSessionRecord(
            target,
            pid,
            gameDir,
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            state,
            startedAt,
            processStartedAt == DateTime.MinValue ? null : processStartedAt);
    }

    public static void ClearBuiltInGameSession(BuiltInGameSessionTarget target)
    {
        if (string.IsNullOrWhiteSpace(target.Kind) || string.IsNullOrWhiteSpace(target.Id))
            return;

        using var db = OpenConnection();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            DELETE FROM builtin_game_sessions
            WHERE target_kind = $kind
              AND host_id = $host
              AND target_id = $target
            """;
        cmd.Parameters.AddWithValue("$kind", target.Kind.Trim());
        cmd.Parameters.AddWithValue("$host", target.HostId?.Trim() ?? "");
        cmd.Parameters.AddWithValue("$target", target.Id.Trim());
        cmd.ExecuteNonQuery();
    }

    public static string GetLocalAcceptance(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
            return "";

        using var db = OpenConnection();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT value FROM local_acceptances WHERE key = $key";
        cmd.Parameters.AddWithValue("$key", key.Trim());
        return cmd.ExecuteScalar() as string ?? "";
    }

    public static void SaveLocalAcceptance(string key, string value)
    {
        if (string.IsNullOrWhiteSpace(key))
            return;

        using var db = OpenConnection();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            INSERT INTO local_acceptances (key, value, updated_at)
            VALUES ($key, $value, datetime('now'))
            ON CONFLICT(key) DO UPDATE SET
                value = excluded.value,
                updated_at = datetime('now')
            """;
        cmd.Parameters.AddWithValue("$key", key.Trim());
        cmd.Parameters.AddWithValue("$value", value ?? "");
        cmd.ExecuteNonQuery();
    }

    public static byte[]? LoadSecureItem(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
            return null;

        using var db = OpenConnection();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT protected_value FROM secure_items WHERE key = $key";
        cmd.Parameters.AddWithValue("$key", key.Trim());

        var value = cmd.ExecuteScalar();
        return value is byte[] encrypted && encrypted.Length > 0
            ? UnprotectBytes(encrypted)
            : null;
    }

    public static void SaveSecureItem(string key, byte[] plainBytes, string metadataJson = "{}")
    {
        if (string.IsNullOrWhiteSpace(key) || plainBytes.Length == 0)
            return;

        using var db = OpenConnection();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            INSERT INTO secure_items (key, protected_value, metadata_json, updated_at)
            VALUES ($key, $value, $metadata, datetime('now'))
            ON CONFLICT(key) DO UPDATE SET
                protected_value = excluded.protected_value,
                metadata_json = excluded.metadata_json,
                updated_at = datetime('now')
            """;
        cmd.Parameters.AddWithValue("$key", key.Trim());
        cmd.Parameters.Add("$value", SqliteType.Blob).Value = ProtectBytes(plainBytes);
        cmd.Parameters.AddWithValue("$metadata", string.IsNullOrWhiteSpace(metadataJson) ? "{}" : metadataJson);
        cmd.ExecuteNonQuery();
    }

    public static string? LoadSecureString(string key)
    {
        var plainBytes = LoadSecureItem(key);
        if (plainBytes is null)
            return null;

        try
        {
            return Encoding.UTF8.GetString(plainBytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plainBytes);
        }
    }

    public static void SaveSecureString(string key, string? value, string metadataJson = "{}")
    {
        if (string.IsNullOrEmpty(value))
        {
            ClearSecureItem(key);
            return;
        }

        var plainBytes = Encoding.UTF8.GetBytes(value);
        try
        {
            SaveSecureItem(key, plainBytes, metadataJson);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plainBytes);
        }
    }

    public static void ClearSecureItem(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
            return;

        using var db = OpenConnection();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "DELETE FROM secure_items WHERE key = $key";
        cmd.Parameters.AddWithValue("$key", key.Trim());
        cmd.ExecuteNonQuery();
    }

    private static void InitializeSchema(SqliteConnection db)
    {
        Execute(db, """
            CREATE TABLE IF NOT EXISTS app_settings (
                key        TEXT PRIMARY KEY,
                value      TEXT NOT NULL DEFAULT '',
                updated_at TEXT NOT NULL DEFAULT (datetime('now'))
            )
            """);

        Execute(db, """
            CREATE TABLE IF NOT EXISTS hosts (
                id                    TEXT PRIMARY KEY,
                api_base_url          TEXT NOT NULL DEFAULT '',
                username              TEXT NOT NULL DEFAULT '',
                minecraft_uuid        TEXT NOT NULL DEFAULT '',
                ignore_certificate_errors INTEGER NOT NULL DEFAULT 0,
                halerith_access_token_protected BLOB,
                halerith_access_token_expires_at_utc TEXT NOT NULL DEFAULT '',
                default_server_public_id     TEXT NOT NULL DEFAULT '',
                sort_order            INTEGER NOT NULL DEFAULT 0,
                created_at            TEXT NOT NULL DEFAULT (datetime('now')),
                updated_at            TEXT NOT NULL DEFAULT (datetime('now'))
            )
            """);

        Execute(db, """
            CREATE TABLE IF NOT EXISTS servers (
                host_id                    TEXT NOT NULL REFERENCES hosts(id) ON DELETE CASCADE,
                server_public_id                  TEXT NOT NULL,
                environment_id             TEXT NOT NULL DEFAULT '',
                environment_name           TEXT NOT NULL DEFAULT '',
                server_name                TEXT NOT NULL DEFAULT '',
                is_available               INTEGER NOT NULL DEFAULT 1,
                minecraft_version          TEXT NOT NULL DEFAULT '',
                loader                     TEXT NOT NULL DEFAULT '',
                loader_version             TEXT NOT NULL DEFAULT '',
                motd                       TEXT NOT NULL DEFAULT '',
                java_mode                  TEXT NOT NULL DEFAULT 'Auto',
                java_min_memory_mb         INTEGER NOT NULL DEFAULT 0,
                java_max_memory_mb         INTEGER NOT NULL DEFAULT 0,
                java_garbage_collector     TEXT NOT NULL DEFAULT 'ZGC',
                java_extra_args            TEXT NOT NULL DEFAULT '',
                java_custom_args           TEXT NOT NULL DEFAULT '',
                ignore_rules_json          TEXT NOT NULL DEFAULT '[]',
                pinned_items_json          TEXT NOT NULL DEFAULT '[]',
                selected_account_id        TEXT NOT NULL DEFAULT '',
                is_secondary               INTEGER NOT NULL DEFAULT 0,
                primary_server_public_id          TEXT NOT NULL DEFAULT '',
                power_status               TEXT NOT NULL DEFAULT 'unknown',
                server_icon_url            TEXT NOT NULL DEFAULT '',
                server_icon_sha256         TEXT NOT NULL DEFAULT '',
                server_icon_content_type   TEXT NOT NULL DEFAULT '',
                game_host                  TEXT NOT NULL DEFAULT '',
                game_port                  INTEGER,
                last_injected_server_address TEXT NOT NULL DEFAULT '',
                sort_order                 INTEGER NOT NULL DEFAULT 0,
                created_at                 TEXT NOT NULL DEFAULT (datetime('now')),
                updated_at                 TEXT NOT NULL DEFAULT (datetime('now')),
                PRIMARY KEY (host_id, server_public_id)
            )
            """);

        Execute(db, """
            CREATE TABLE IF NOT EXISTS minecraft_server_rosters (
                host_id TEXT NOT NULL REFERENCES hosts(id) ON DELETE CASCADE,
                minecraft_uuid TEXT NOT NULL,
                server_ids_json TEXT NOT NULL DEFAULT '[]',
                PRIMARY KEY (host_id, minecraft_uuid)
            )
            """);

        Execute(db, """
            CREATE TABLE IF NOT EXISTS local_instances (
                id                       TEXT PRIMARY KEY,
                name                     TEXT NOT NULL DEFAULT '',
                minecraft_version        TEXT NOT NULL DEFAULT '',
                loader                   TEXT NOT NULL DEFAULT '',
                loader_version           TEXT NOT NULL DEFAULT '',
                server_address           TEXT NOT NULL DEFAULT '',
                selected_account_id      TEXT NOT NULL DEFAULT '',
                java_mode                TEXT NOT NULL DEFAULT 'Auto',
                java_min_memory_mb       INTEGER NOT NULL DEFAULT 0,
                java_max_memory_mb       INTEGER NOT NULL DEFAULT 0,
                java_garbage_collector   TEXT NOT NULL DEFAULT 'ZGC',
                java_extra_args          TEXT NOT NULL DEFAULT '',
                java_custom_args         TEXT NOT NULL DEFAULT '',
                created_at_utc           TEXT NOT NULL DEFAULT '',
                last_launched_at_utc     TEXT NOT NULL DEFAULT '',
                sort_order               INTEGER NOT NULL DEFAULT 0,
                updated_at               TEXT NOT NULL DEFAULT (datetime('now'))
            )
            """);

        Execute(db, """
            CREATE TABLE IF NOT EXISTS minecraft_accounts (
                uuid                       TEXT PRIMARY KEY,
                gamertag                   TEXT NOT NULL DEFAULT '',
                xuid                       TEXT NOT NULL DEFAULT '',
                ms_access_token_protected  BLOB,
                ms_refresh_token_protected BLOB,
                mc_access_token_protected  BLOB,
                expires_at_utc             TEXT NOT NULL DEFAULT '',
                created_at                 TEXT NOT NULL DEFAULT (datetime('now')),
                updated_at                 TEXT NOT NULL DEFAULT (datetime('now'))
            )
            """);

        Execute(db, """
            CREATE TABLE IF NOT EXISTS builtin_game_sessions (
                target_kind            TEXT NOT NULL,
                host_id                TEXT NOT NULL DEFAULT '',
                target_id              TEXT NOT NULL,
                pid                    INTEGER NOT NULL DEFAULT 0,
                state                  TEXT NOT NULL DEFAULT '',
                game_directory         TEXT NOT NULL DEFAULT '',
                minecraft_version      TEXT NOT NULL DEFAULT '',
                loader                 TEXT NOT NULL DEFAULT '',
                loader_version         TEXT NOT NULL DEFAULT '',
                started_at_utc         TEXT NOT NULL DEFAULT '',
                process_started_at_utc TEXT NOT NULL DEFAULT '',
                updated_at             TEXT NOT NULL DEFAULT (datetime('now')),
                PRIMARY KEY (target_kind, host_id, target_id)
            )
            """);

        Execute(db, """
            CREATE TABLE IF NOT EXISTS local_acceptances (
                key        TEXT PRIMARY KEY,
                value      TEXT NOT NULL DEFAULT '',
                updated_at TEXT NOT NULL DEFAULT (datetime('now'))
            )
            """);

        Execute(db, """
            CREATE TABLE IF NOT EXISTS secure_items (
                key             TEXT PRIMARY KEY,
                protected_value BLOB,
                metadata_json   TEXT NOT NULL DEFAULT '{}',
                updated_at      TEXT NOT NULL DEFAULT (datetime('now'))
            )
            """);

        Execute(db, """
            CREATE TABLE IF NOT EXISTS image_cache (
                cache_key           TEXT PRIMARY KEY,
                image_kind          TEXT NOT NULL DEFAULT '',
                source              TEXT NOT NULL DEFAULT '',
                source_id           TEXT NOT NULL DEFAULT '',
                slug                TEXT NOT NULL DEFAULT '',
                content_type        TEXT NOT NULL DEFAULT '',
                url                 TEXT NOT NULL DEFAULT '',
                local_path          TEXT NOT NULL DEFAULT '',
                width               INTEGER NOT NULL DEFAULT 0,
                height              INTEGER NOT NULL DEFAULT 0,
                last_seen_utc       TEXT NOT NULL DEFAULT '',
                last_accessed_utc   TEXT NOT NULL DEFAULT '',
                downloaded_at_utc   TEXT NOT NULL DEFAULT '',
                image_hash          TEXT NOT NULL DEFAULT '',
                failed_at_utc       TEXT NOT NULL DEFAULT '',
                failure_count       INTEGER NOT NULL DEFAULT 0,
                updated_at          TEXT NOT NULL DEFAULT (datetime('now'))
            )
            """);

        Execute(db, """
            CREATE TABLE IF NOT EXISTS instances (
                id              INTEGER PRIMARY KEY AUTOINCREMENT,
                mc_version      TEXT    NOT NULL,
                loader          TEXT    NOT NULL DEFAULT '',
                loader_version  TEXT    NOT NULL DEFAULT '',
                created_at      TEXT    NOT NULL DEFAULT (datetime('now'))
            )
            """);

        Execute(db, """
            CREATE TABLE IF NOT EXISTS instance_files (
                instance_id     INTEGER NOT NULL REFERENCES instances(id) ON DELETE CASCADE,
                file_path       TEXT    NOT NULL,
                size_bytes      INTEGER NOT NULL DEFAULT 0,
                sha1            TEXT    NOT NULL DEFAULT '',
                category        TEXT    NOT NULL DEFAULT '',
                PRIMARY KEY (instance_id, file_path)
            )
            """);

        Execute(db, """
            CREATE INDEX IF NOT EXISTS idx_instance_files_path
            ON instance_files(file_path)
            """);

        Execute(db, """
            CREATE TABLE IF NOT EXISTS server_content_items (
                host_id              TEXT NOT NULL,
                server_public_id            TEXT NOT NULL,
                content_type         TEXT NOT NULL,
                source               TEXT NOT NULL DEFAULT '',
                source_id            TEXT NOT NULL DEFAULT '',
                slug                 TEXT NOT NULL DEFAULT '',
                name                 TEXT NOT NULL DEFAULT '',
                icon_url             TEXT,
                source_version_id    TEXT NOT NULL DEFAULT '',
                version_name         TEXT NOT NULL DEFAULT '',
                version_number       TEXT NOT NULL DEFAULT '',
                download_filename    TEXT NOT NULL DEFAULT '',
                file_hash            TEXT NOT NULL DEFAULT '',
                mc_version           TEXT NOT NULL DEFAULT '',
                loader               TEXT NOT NULL DEFAULT '',
                download_url         TEXT NOT NULL DEFAULT '',
                local_path           TEXT NOT NULL DEFAULT '',
                installed_at_utc     TEXT NOT NULL DEFAULT '',
                updated_at           TEXT NOT NULL DEFAULT (datetime('now')),
                PRIMARY KEY (host_id, server_public_id, content_type, local_path),
                FOREIGN KEY (host_id, server_public_id) REFERENCES servers(host_id, server_public_id) ON DELETE CASCADE
            )
            """);

        Execute(db, """
            CREATE TABLE IF NOT EXISTS local_instance_content_items (
                local_instance_id    TEXT NOT NULL REFERENCES local_instances(id) ON DELETE CASCADE,
                content_type         TEXT NOT NULL,
                source               TEXT NOT NULL DEFAULT '',
                source_id            TEXT NOT NULL DEFAULT '',
                slug                 TEXT NOT NULL DEFAULT '',
                name                 TEXT NOT NULL DEFAULT '',
                icon_url             TEXT,
                source_version_id    TEXT NOT NULL DEFAULT '',
                version_name         TEXT NOT NULL DEFAULT '',
                version_number       TEXT NOT NULL DEFAULT '',
                download_filename    TEXT NOT NULL DEFAULT '',
                file_hash            TEXT NOT NULL DEFAULT '',
                mc_version           TEXT NOT NULL DEFAULT '',
                loader               TEXT NOT NULL DEFAULT '',
                download_url         TEXT NOT NULL DEFAULT '',
                local_path           TEXT NOT NULL DEFAULT '',
                installed_at_utc     TEXT NOT NULL DEFAULT '',
                updated_at           TEXT NOT NULL DEFAULT (datetime('now')),
                PRIMARY KEY (local_instance_id, content_type, local_path)
            )
            """);

        MigrateServerIdentifierSchema(db);
        EnsureHostColumns(db);
        EnsureMinecraftAccountColumns(db);
        EnsureServerColumns(db);
        EnsureLocalInstanceColumns(db);
        EnsureImageCacheColumns(db);
        EnsureContentColumns(db, "server_content_items");
        EnsureContentColumns(db, "local_instance_content_items");
    }

    private static Dictionary<string, string> LoadSettings(SqliteConnection db)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT key, value FROM app_settings";

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            values[reader.GetString(0)] = reader.GetString(1);
        return values;
    }

    private static List<HostConfig> LoadHosts(SqliteConnection db)
    {
        var hosts = new List<HostConfig>();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT
                id, api_base_url, username, minecraft_uuid, ignore_certificate_errors,
                halerith_access_token_protected, halerith_access_token_expires_at_utc,
                default_server_public_id
            FROM hosts
            ORDER BY sort_order, api_base_url, id
            """;

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var host = new HostConfig
            {
                Id = ManagerApiDefaults.ManagerScopeId,
                ApiBaseUrl = ManagerApiDefaults.ApiBaseUrl,
                Username = reader.GetString(2),
                MinecraftUuid = reader.GetString(3),
                IgnoreCertificateErrors = reader.GetInt32(4) != 0,
                HalerithAccessToken = reader.IsDBNull(5) ? "" : UnprotectString((byte[])reader.GetValue(5)),
                HalerithAccessTokenExpiresAtUtc = reader.GetString(6),
                DefaultServerPublicId = reader.GetString(7),
            };
            hosts.Add(host);
        }

        foreach (var host in hosts)
        {
            host.Servers.AddRange(LoadServers(db, host.Id));
            host.VisibleServerIdsByMinecraftUuid = LoadServerRosters(db, host.Id);
        }

        return hosts;
    }

    private static Dictionary<string, List<string>> LoadServerRosters(SqliteConnection db, string hostId)
    {
        var rosters = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT minecraft_uuid, server_ids_json FROM minecraft_server_rosters WHERE host_id = $host";
        cmd.Parameters.AddWithValue("$host", hostId);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var uuid = MinecraftProfileClient.NormalizeUuid(reader.GetString(0));
            if (uuid is not null)
                rosters[uuid] = DeserializeList<string>(reader.GetString(1));
        }
        return rosters;
    }

    private static List<ServerConfig> LoadServers(SqliteConnection db, string hostId)
    {
        var servers = new List<ServerConfig>();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT
                server_public_id, environment_id, environment_name, server_name, is_available, minecraft_version, loader,
                loader_version, motd, java_mode, java_min_memory_mb, java_max_memory_mb,
                java_garbage_collector, java_extra_args, java_custom_args, ignore_rules_json, pinned_items_json,
                selected_account_id, is_secondary, primary_server_public_id,
                power_status, server_icon_url, server_icon_sha256, server_icon_content_type,
                game_host, game_port, last_injected_server_address, modpack_file_policy
            FROM servers
            WHERE host_id = $host
            ORDER BY sort_order, server_name, server_public_id
            """;
        cmd.Parameters.AddWithValue("$host", hostId);

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            servers.Add(new ServerConfig
            {
                ServerPublicId = reader.GetString(0),
                EnvironmentId = reader.GetString(1),
                EnvironmentName = reader.GetString(2),
                ServerName = reader.GetString(3),
                IsAvailable = reader.GetInt32(4) != 0,
                MinecraftVersion = reader.GetString(5),
                Loader = reader.GetString(6),
                LoaderVersion = reader.GetString(7),
                Motd = reader.GetString(8),
                JavaMode = reader.GetString(9),
                JavaMinMemoryMb = reader.GetInt32(10),
                JavaMaxMemoryMb = reader.GetInt32(11),
                JavaGarbageCollector = JavaLaunchSettings.NormalizeGarbageCollector(reader.GetString(12)),
                JavaExtraArgs = reader.GetString(13),
                JavaCustomArgs = reader.GetString(14),
                IgnoreRules = DeserializeList<IgnoreRule>(reader.GetString(15)),
                PinnedItems = DeserializeList<LocalPinnedItem>(reader.GetString(16)),
                SelectedAccountId = reader.GetString(17),
                IsSecondary = reader.GetInt32(18) != 0,
                PrimaryServerPublicId = reader.GetString(19),
                PowerStatus = reader.GetString(20),
                ServerIconUrl = reader.GetString(21),
                ServerIconSha256 = reader.GetString(22),
                ServerIconContentType = reader.GetString(23),
                GameHost = reader.GetString(24),
                Port = reader.IsDBNull(25) ? null : reader.GetInt32(25),
                LastInjectedServerAddress = reader.GetString(26),
                ModpackFilePolicy = ModpackFilePolicies.Normalize(reader.GetString(27)),
            });
        }

        return servers;
    }

    private static List<LocalInstance> LoadLocalInstances(SqliteConnection db)
    {
        var instances = new List<LocalInstance>();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT
                id, name, minecraft_version, loader, loader_version, server_address,
                selected_account_id, java_mode, java_min_memory_mb, java_max_memory_mb,
                java_garbage_collector, java_extra_args, java_custom_args, created_at_utc, last_launched_at_utc
            FROM local_instances
            ORDER BY sort_order, name, id
            """;

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var inst = new LocalInstance
            {
                Id = reader.GetString(0),
                Name = reader.GetString(1),
                MinecraftVersion = reader.GetString(2),
                Loader = reader.GetString(3),
                LoaderVersion = reader.GetString(4),
                ServerAddress = reader.GetString(5),
                SelectedAccountId = reader.GetString(6),
                JavaMode = reader.GetString(7),
                JavaMinMemoryMb = reader.GetInt32(8),
                JavaMaxMemoryMb = reader.GetInt32(9),
                JavaGarbageCollector = JavaLaunchSettings.NormalizeGarbageCollector(reader.GetString(10)),
                JavaExtraArgs = reader.GetString(11),
                JavaCustomArgs = reader.GetString(12),
            };

            if (DateTime.TryParse(reader.GetString(13), null, System.Globalization.DateTimeStyles.RoundtripKind, out var created))
                inst.CreatedAtUtc = created;
            if (DateTime.TryParse(reader.GetString(14), null, System.Globalization.DateTimeStyles.RoundtripKind, out var launched))
                inst.LastLaunchedAtUtc = launched;

            instances.Add(inst);
        }

        return instances;
    }

    private static void SaveSettings(SqliteConnection db, SqliteTransaction tx, AppConfig config)
    {
        UpsertSetting(db, tx, "default_host_id", ManagerApiDefaults.ManagerScopeId);
        UpsertSetting(db, tx, "startup_timeout_seconds", config.StartupTimeoutSeconds.ToString());
        UpsertSetting(db, tx, "launcher_countdown_seconds", config.LauncherCountdownSeconds.ToString());
        UpsertSetting(db, tx, "enable_event_logging", config.EnableEventLogging ? "1" : "0");
        UpsertSetting(db, tx, "guided_tips_enabled", config.GuidedTipsEnabled ? "1" : "0");
        UpsertSetting(db, tx, "allow_auto_update", config.AllowAutoUpdate ? "1" : "0");
        UpsertSetting(db, tx, "keep_backups", config.KeepBackups ? "1" : "0");
        UpsertSetting(db, tx, "backup_max_files_per_folder", config.BackupMaxFilesPerFolder.ToString());
        UpsertSetting(db, tx, "default_instance_kind", NormalizeDefaultInstanceKind(config.DefaultInstanceKind));
        UpsertSetting(db, tx, "default_instance_id", config.DefaultInstanceId ?? "");
    }

    private static void SaveHosts(SqliteConnection db, SqliteTransaction tx, List<HostConfig> hosts)
    {
        var hostIds = hosts
            .Where(h => !string.IsNullOrWhiteSpace(h.Id))
            .Select(h => h.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        DeleteMissing(db, tx, "hosts", "id", hostIds);

        for (var i = 0; i < hosts.Count; i++)
        {
            var host = hosts[i];
            if (string.IsNullOrWhiteSpace(host.Id)) continue;

            using var cmd = db.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO hosts (
                    id, api_base_url, username, minecraft_uuid, ignore_certificate_errors,
                    halerith_access_token_protected, halerith_access_token_expires_at_utc,
                    default_server_public_id, sort_order, updated_at
                )
                VALUES (
                    $id, $url, $user, $minecraftUuid, $ignoreCertErrors,
                    $halerithToken, $halerithTokenExpires,
                    $defaultServer, $sort, datetime('now')
                )
                ON CONFLICT(id) DO UPDATE SET
                    api_base_url = excluded.api_base_url,
                    username = excluded.username,
                    minecraft_uuid = excluded.minecraft_uuid,
                    ignore_certificate_errors = excluded.ignore_certificate_errors,
                    halerith_access_token_protected = excluded.halerith_access_token_protected,
                    halerith_access_token_expires_at_utc = excluded.halerith_access_token_expires_at_utc,
                    default_server_public_id = excluded.default_server_public_id,
                    sort_order = excluded.sort_order,
                    updated_at = datetime('now')
                """;
            cmd.Parameters.AddWithValue("$id", ManagerApiDefaults.ManagerScopeId);
            cmd.Parameters.AddWithValue("$url", ManagerApiDefaults.ApiBaseUrl);
            cmd.Parameters.AddWithValue("$user", "");
            cmd.Parameters.AddWithValue("$minecraftUuid", "");
            cmd.Parameters.AddWithValue("$ignoreCertErrors", host.IgnoreCertificateErrors ? 1 : 0);
            cmd.Parameters.Add("$halerithToken", SqliteType.Blob).Value =
                string.IsNullOrWhiteSpace(host.HalerithAccessToken) ? DBNull.Value : ProtectString(host.HalerithAccessToken);
            cmd.Parameters.AddWithValue("$halerithTokenExpires", host.HalerithAccessTokenExpiresAtUtc?.Trim() ?? "");
            cmd.Parameters.AddWithValue("$defaultServer", host.DefaultServerPublicId ?? "");
            cmd.Parameters.AddWithValue("$sort", i);
            cmd.ExecuteNonQuery();

            SaveServers(db, tx, ManagerApiDefaults.ManagerScopeId, host.Servers);
            SaveServerRosters(db, tx, ManagerApiDefaults.ManagerScopeId, host.VisibleServerIdsByMinecraftUuid);
        }
    }

    private static void SaveServerRosters(SqliteConnection db, SqliteTransaction tx, string hostId,
        Dictionary<string, List<string>> rosters)
    {
        foreach (var (accountId, serverIds) in rosters)
        {
            var uuid = MinecraftProfileClient.NormalizeUuid(accountId);
            if (uuid is null) continue;
            using var cmd = db.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO minecraft_server_rosters (host_id, minecraft_uuid, server_ids_json)
                VALUES ($host, $uuid, $servers)
                ON CONFLICT(host_id, minecraft_uuid) DO UPDATE SET server_ids_json = excluded.server_ids_json
                """;
            cmd.Parameters.AddWithValue("$host", hostId);
            cmd.Parameters.AddWithValue("$uuid", uuid);
            cmd.Parameters.AddWithValue("$servers", JsonSerializer.Serialize(serverIds, SerializerOptions.Default));
            cmd.ExecuteNonQuery();
        }
    }

    private static void SaveServers(SqliteConnection db, SqliteTransaction tx, string hostId, List<ServerConfig> servers)
    {
        var serverPublicIds = servers
            .Where(s => !string.IsNullOrWhiteSpace(s.ServerPublicId))
            .Select(s => s.ServerPublicId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        DeleteMissingServers(db, tx, hostId, serverPublicIds);

        for (var i = 0; i < servers.Count; i++)
        {
            var server = servers[i];
            if (string.IsNullOrWhiteSpace(server.ServerPublicId)) continue;

            using var cmd = db.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO servers (
                    host_id, server_public_id, environment_id, environment_name, server_name, is_available, minecraft_version,
                    loader, loader_version, motd, java_mode, java_min_memory_mb, java_max_memory_mb,
                    java_garbage_collector, java_extra_args, java_custom_args, ignore_rules_json, pinned_items_json, selected_account_id,
                    is_secondary, primary_server_public_id, power_status, server_icon_url,
                    server_icon_sha256, server_icon_content_type, game_host, game_port,
                    last_injected_server_address, modpack_file_policy, sort_order, updated_at
                )
                VALUES (
                    $host, $server, $environmentId, $environmentName, $name, $available, $mc, $loader, $loaderVersion,
                    $motd, $javaMode, $javaMin, $javaMax, $javaGc, $javaExtra,
                    $javaCustom, $ignoreRules, $pinnedItems, $account, $secondary,
                    $primary, $powerStatus, $iconUrl, $iconSha, $iconContentType,
                    $gameHost, $gamePort, $lastInjectedAddress, $modpackPolicy, $sort, datetime('now')
                )
                ON CONFLICT(host_id, server_public_id) DO UPDATE SET
                    environment_id = excluded.environment_id,
                    environment_name = excluded.environment_name,
                    server_name = excluded.server_name,
                    is_available = excluded.is_available,
                    minecraft_version = excluded.minecraft_version,
                    loader = excluded.loader,
                    loader_version = excluded.loader_version,
                    motd = excluded.motd,
                    java_mode = excluded.java_mode,
                    java_min_memory_mb = excluded.java_min_memory_mb,
                    java_max_memory_mb = excluded.java_max_memory_mb,
                    java_garbage_collector = excluded.java_garbage_collector,
                    java_extra_args = excluded.java_extra_args,
                    java_custom_args = excluded.java_custom_args,
                    ignore_rules_json = excluded.ignore_rules_json,
                    pinned_items_json = excluded.pinned_items_json,
                    selected_account_id = excluded.selected_account_id,
                    is_secondary = excluded.is_secondary,
                    primary_server_public_id = excluded.primary_server_public_id,
                    power_status = excluded.power_status,
                    server_icon_url = excluded.server_icon_url,
                    server_icon_sha256 = excluded.server_icon_sha256,
                    server_icon_content_type = excluded.server_icon_content_type,
                    game_host = excluded.game_host,
                    game_port = excluded.game_port,
                    last_injected_server_address = excluded.last_injected_server_address,
                    modpack_file_policy = excluded.modpack_file_policy,
                    sort_order = excluded.sort_order,
                    updated_at = datetime('now')
                """;
            cmd.Parameters.AddWithValue("$host", hostId);
            cmd.Parameters.AddWithValue("$server", server.ServerPublicId);
            cmd.Parameters.AddWithValue("$environmentId", server.EnvironmentId ?? "");
            cmd.Parameters.AddWithValue("$environmentName", server.EnvironmentName ?? "");
            cmd.Parameters.AddWithValue("$name", server.ServerName ?? "");
            cmd.Parameters.AddWithValue("$available", server.IsAvailable ? 1 : 0);
            cmd.Parameters.AddWithValue("$mc", server.MinecraftVersion ?? "");
            cmd.Parameters.AddWithValue("$loader", server.Loader ?? "");
            cmd.Parameters.AddWithValue("$loaderVersion", server.LoaderVersion ?? "");
            cmd.Parameters.AddWithValue("$motd", server.Motd ?? "");
            cmd.Parameters.AddWithValue("$javaMode", string.IsNullOrWhiteSpace(server.JavaMode) ? "Auto" : server.JavaMode);
            cmd.Parameters.AddWithValue("$javaMin", server.JavaMinMemoryMb);
            cmd.Parameters.AddWithValue("$javaMax", server.JavaMaxMemoryMb);
            cmd.Parameters.AddWithValue("$javaGc", JavaLaunchSettings.NormalizeGarbageCollector(server.JavaGarbageCollector));
            cmd.Parameters.AddWithValue("$javaExtra", server.JavaExtraArgs ?? "");
            cmd.Parameters.AddWithValue("$javaCustom", server.JavaCustomArgs ?? "");
            cmd.Parameters.AddWithValue("$ignoreRules", JsonSerializer.Serialize(server.IgnoreRules ?? [], SerializerOptions.Default));
            cmd.Parameters.AddWithValue("$pinnedItems", JsonSerializer.Serialize(server.PinnedItems ?? [], SerializerOptions.Default));
            cmd.Parameters.AddWithValue("$account", server.SelectedAccountId ?? "");
            cmd.Parameters.AddWithValue("$secondary", server.IsSecondary ? 1 : 0);
            cmd.Parameters.AddWithValue("$primary", server.PrimaryServerPublicId ?? "");
            cmd.Parameters.AddWithValue("$powerStatus", ServerPowerStates.Normalize(server.PowerStatus));
            cmd.Parameters.AddWithValue("$iconUrl", server.ServerIconUrl ?? "");
            cmd.Parameters.AddWithValue("$iconSha", server.ServerIconSha256 ?? "");
            cmd.Parameters.AddWithValue("$iconContentType", server.ServerIconContentType ?? "");
            cmd.Parameters.AddWithValue("$gameHost", server.GameHost ?? "");
            cmd.Parameters.AddWithValue("$gamePort", (object?)server.Port ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$lastInjectedAddress", server.LastInjectedServerAddress ?? "");
            cmd.Parameters.AddWithValue("$modpackPolicy", ModpackFilePolicies.Normalize(server.ModpackFilePolicy));
            cmd.Parameters.AddWithValue("$sort", i);
            cmd.ExecuteNonQuery();
        }
    }

    public static void SaveLocalInstance(LocalInstance instance)
    {
        using var db = OpenConnection();
        SaveLocalInstance(db, instance);
    }

    internal static void SaveLocalInstance(SqliteConnection db, LocalInstance instance)
    {
        using var tx = db.BeginTransaction();
        SaveLocalInstances(db, tx, [instance], pruneMissing: false);
        tx.Commit();
    }

    private static void SaveLocalInstances(SqliteConnection db, SqliteTransaction tx, List<LocalInstance> instances, bool pruneMissing = true)
    {
        var ids = instances
            .Where(i => !string.IsNullOrWhiteSpace(i.Id))
            .Select(i => i.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (pruneMissing) DeleteMissing(db, tx, "local_instances", "id", ids);

        for (var i = 0; i < instances.Count; i++)
        {
            var inst = instances[i];
            if (string.IsNullOrWhiteSpace(inst.Id)) continue;

            using var cmd = db.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO local_instances (
                    id, name, minecraft_version, loader, loader_version, server_address,
                    selected_account_id, java_mode, java_min_memory_mb, java_max_memory_mb,
                    java_garbage_collector, java_extra_args, java_custom_args, created_at_utc, last_launched_at_utc,
                    sort_order, updated_at
                )
                VALUES (
                    $id, $name, $mc, $loader, $loaderVersion, $address, $account,
                    $javaMode, $javaMin, $javaMax, $javaGc, $javaExtra, $javaCustom, $created,
                    $launched, CASE WHEN $preserveSort THEN (SELECT COALESCE(MAX(sort_order), -1) + 1 FROM local_instances) ELSE $sort END, datetime('now')
                )
                ON CONFLICT(id) DO UPDATE SET
                    name = excluded.name,
                    minecraft_version = excluded.minecraft_version,
                    loader = excluded.loader,
                    loader_version = excluded.loader_version,
                    server_address = excluded.server_address,
                    selected_account_id = excluded.selected_account_id,
                    java_mode = excluded.java_mode,
                    java_min_memory_mb = excluded.java_min_memory_mb,
                    java_max_memory_mb = excluded.java_max_memory_mb,
                    java_garbage_collector = excluded.java_garbage_collector,
                    java_extra_args = excluded.java_extra_args,
                    java_custom_args = excluded.java_custom_args,
                    created_at_utc = excluded.created_at_utc,
                    last_launched_at_utc = excluded.last_launched_at_utc,
                    sort_order = CASE WHEN $preserveSort THEN local_instances.sort_order ELSE excluded.sort_order END,
                    updated_at = datetime('now')
                """;
            cmd.Parameters.AddWithValue("$id", inst.Id);
            cmd.Parameters.AddWithValue("$name", inst.Name ?? "");
            cmd.Parameters.AddWithValue("$mc", inst.MinecraftVersion ?? "");
            cmd.Parameters.AddWithValue("$loader", inst.Loader ?? "");
            cmd.Parameters.AddWithValue("$loaderVersion", inst.LoaderVersion ?? "");
            cmd.Parameters.AddWithValue("$address", inst.ServerAddress ?? "");
            cmd.Parameters.AddWithValue("$account", inst.SelectedAccountId ?? "");
            cmd.Parameters.AddWithValue("$javaMode", string.IsNullOrWhiteSpace(inst.JavaMode) ? "Auto" : inst.JavaMode);
            cmd.Parameters.AddWithValue("$javaMin", inst.JavaMinMemoryMb);
            cmd.Parameters.AddWithValue("$javaMax", inst.JavaMaxMemoryMb);
            cmd.Parameters.AddWithValue("$javaGc", JavaLaunchSettings.NormalizeGarbageCollector(inst.JavaGarbageCollector));
            cmd.Parameters.AddWithValue("$javaExtra", inst.JavaExtraArgs ?? "");
            cmd.Parameters.AddWithValue("$javaCustom", inst.JavaCustomArgs ?? "");
            cmd.Parameters.AddWithValue("$created", inst.CreatedAtUtc.ToString("O"));
            cmd.Parameters.AddWithValue("$preserveSort", !pruneMissing);
            cmd.Parameters.AddWithValue("$launched", inst.LastLaunchedAtUtc?.ToString("O") ?? "");
            cmd.Parameters.AddWithValue("$sort", i);
            cmd.ExecuteNonQuery();
        }
    }

    private static void UpsertSetting(SqliteConnection db, SqliteTransaction tx, string key, string? value)
    {
        using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO app_settings (key, value, updated_at)
            VALUES ($key, $value, datetime('now'))
            ON CONFLICT(key) DO UPDATE SET
                value = excluded.value,
                updated_at = datetime('now')
            """;
        cmd.Parameters.AddWithValue("$key", key);
        cmd.Parameters.AddWithValue("$value", value ?? "");
        cmd.ExecuteNonQuery();
    }

    private static bool ShouldVacuum(SqliteConnection db)
    {
        if (GetStorageSizeBytes() < VacuumMinimumBytes)
            return false;

        var settings = LoadSettings(db);
        var lastVacuum = ParseUtc(GetSetting(settings, LastVacuumSettingKey));
        return lastVacuum == DateTime.MinValue
            || DateTime.UtcNow - lastVacuum >= TimeSpan.FromDays(VacuumIntervalDays);
    }

    private static long GetStorageSizeBytes()
    {
        try
        {
            return File.Exists(StoragePath) ? new FileInfo(StoragePath).Length : 0;
        }
        catch
        {
            return 0;
        }
    }

    private static void AddContentParameters(SqliteCommand cmd, bool includeOwner)
    {
        if (includeOwner)
        {
            cmd.Parameters.Add("$host", SqliteType.Text);
            cmd.Parameters.Add("$server", SqliteType.Text);
        }
        else
        {
            cmd.Parameters.Add("$localInstance", SqliteType.Text);
        }

        cmd.Parameters.Add("$type", SqliteType.Text);
        cmd.Parameters.Add("$source", SqliteType.Text);
        cmd.Parameters.Add("$sourceId", SqliteType.Text);
        cmd.Parameters.Add("$slug", SqliteType.Text);
        cmd.Parameters.Add("$name", SqliteType.Text);
        cmd.Parameters.Add("$icon", SqliteType.Text);
        cmd.Parameters.Add("$sourceVersionId", SqliteType.Text);
        cmd.Parameters.Add("$versionName", SqliteType.Text);
        cmd.Parameters.Add("$versionNumber", SqliteType.Text);
        cmd.Parameters.Add("$filename", SqliteType.Text);
        cmd.Parameters.Add("$hash", SqliteType.Text);
        cmd.Parameters.Add("$mcVersion", SqliteType.Text);
        cmd.Parameters.Add("$loader", SqliteType.Text);
        cmd.Parameters.Add("$downloadUrl", SqliteType.Text);
        cmd.Parameters.Add("$path", SqliteType.Text);
        cmd.Parameters.Add("$installedAt", SqliteType.Text);
        cmd.Parameters.Add("$packId", SqliteType.Text);
        cmd.Parameters.Add("$packRevision", SqliteType.Text);
        cmd.Parameters.Add("$packHash", SqliteType.Text);
    }

    private static void SetContentParameterValues(
        SqliteCommand cmd,
        ContentInventoryItem item,
        string ownerId,
        string serverPublicId)
    {
        if (cmd.Parameters.Contains("$host"))
        {
            cmd.Parameters["$host"].Value = ownerId;
            cmd.Parameters["$server"].Value = serverPublicId;
        }
        else
        {
            cmd.Parameters["$localInstance"].Value = ownerId;
        }

        cmd.Parameters["$type"].Value = item.ContentType ?? "";
        cmd.Parameters["$source"].Value = item.Source ?? "";
        cmd.Parameters["$sourceId"].Value = item.SourceId ?? "";
        cmd.Parameters["$slug"].Value = item.Slug ?? "";
        cmd.Parameters["$name"].Value = item.Name ?? "";
        cmd.Parameters["$icon"].Value = (object?)item.IconUrl ?? DBNull.Value;
        cmd.Parameters["$sourceVersionId"].Value = item.SourceVersionId ?? "";
        cmd.Parameters["$versionName"].Value = item.VersionName ?? "";
        cmd.Parameters["$versionNumber"].Value = item.VersionNumber ?? "";
        cmd.Parameters["$filename"].Value = item.DownloadFilename ?? "";
        cmd.Parameters["$hash"].Value = item.FileHash ?? "";
        cmd.Parameters["$mcVersion"].Value = item.McVersion ?? "";
        cmd.Parameters["$loader"].Value = item.Loader ?? "";
        cmd.Parameters["$downloadUrl"].Value = item.DownloadUrl ?? "";
        cmd.Parameters["$path"].Value = item.LocalPath ?? "";
        cmd.Parameters["$installedAt"].Value = item.InstalledAtUtc ?? "";
        cmd.Parameters["$packId"].Value = item.PackId ?? "";
        cmd.Parameters["$packRevision"].Value = item.PackRevision ?? "";
        cmd.Parameters["$packHash"].Value = item.PackFileHash ?? "";
    }

    private static ImageCacheRecord ReadImageCacheRecord(SqliteDataReader reader)
        => new()
        {
            CacheKey = reader.GetString(0),
            ImageKind = reader.GetString(1),
            Source = reader.GetString(2),
            SourceId = reader.GetString(3),
            Slug = reader.GetString(4),
            ContentType = reader.GetString(5),
            Url = reader.GetString(6),
            LocalPath = reader.GetString(7),
            Width = reader.GetInt32(8),
            Height = reader.GetInt32(9),
            LastSeenUtc = reader.GetString(10),
            LastAccessedUtc = reader.GetString(11),
            DownloadedAtUtc = reader.GetString(12),
            ImageHash = reader.GetString(13),
            FailedAtUtc = reader.GetString(14),
            FailureCount = reader.GetInt32(15),
        };

    private static void BindImageCacheRecord(SqliteCommand cmd, ImageCacheRecord record)
    {
        cmd.Parameters.AddWithValue("$key", record.CacheKey ?? "");
        cmd.Parameters.AddWithValue("$kind", record.ImageKind ?? "");
        cmd.Parameters.AddWithValue("$source", record.Source ?? "");
        cmd.Parameters.AddWithValue("$sourceId", record.SourceId ?? "");
        cmd.Parameters.AddWithValue("$slug", record.Slug ?? "");
        cmd.Parameters.AddWithValue("$contentType", record.ContentType ?? "");
        cmd.Parameters.AddWithValue("$url", record.Url ?? "");
        cmd.Parameters.AddWithValue("$path", record.LocalPath ?? "");
        cmd.Parameters.AddWithValue("$width", record.Width);
        cmd.Parameters.AddWithValue("$height", record.Height);
        cmd.Parameters.AddWithValue("$seen", record.LastSeenUtc ?? "");
        cmd.Parameters.AddWithValue("$accessed", record.LastAccessedUtc ?? "");
        cmd.Parameters.AddWithValue("$downloaded", record.DownloadedAtUtc ?? "");
        cmd.Parameters.AddWithValue("$imageHash", record.ImageHash ?? "");
        cmd.Parameters.AddWithValue("$failed", record.FailedAtUtc ?? "");
        cmd.Parameters.AddWithValue("$failureCount", record.FailureCount);
    }

    private static List<ContentInventoryItem> ReadContentInventory(SqliteCommand cmd)
    {
        var items = new List<ContentInventoryItem>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            items.Add(new ContentInventoryItem
            {
                ContentType = reader.GetString(0),
                Source = reader.GetString(1),
                SourceId = reader.GetString(2),
                Slug = reader.GetString(3),
                Name = reader.GetString(4),
                IconUrl = reader.IsDBNull(5) ? null : reader.GetString(5),
                SourceVersionId = reader.GetString(6),
                VersionName = reader.GetString(7),
                VersionNumber = reader.GetString(8),
                DownloadFilename = reader.GetString(9),
                FileHash = reader.GetString(10),
                McVersion = reader.GetString(11),
                Loader = reader.GetString(12),
                DownloadUrl = reader.GetString(13),
                LocalPath = reader.GetString(14),
                InstalledAtUtc = reader.GetString(15),
                PackId = reader.GetString(16),
                PackRevision = reader.GetString(17),
                PackFileHash = reader.GetString(18),
            });
        }

        return items;
    }

    private static void EnsureContentColumns(SqliteConnection db, string table)
    {
        EnsureColumn(db, table, "pack_id", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(db, table, "pack_revision", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(db, table, "pack_file_hash", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(db, table, "source_version_id", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(db, table, "mc_version", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(db, table, "loader", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(db, table, "download_url", "TEXT NOT NULL DEFAULT ''");
    }

    internal static void MigrateServerIdentifierSchema(SqliteConnection db)
    {
        var renames = new (string Table, string OldColumn, string NewColumn)[]
        {
            ("hosts", "default_server_id", "default_server_public_id"),
            ("servers", "primary_server_id", "primary_server_public_id"),
            ("servers", "server_id", "server_public_id"),
            ("server_content_items", "server_id", "server_public_id"),
        };

        using var transaction = db.BeginTransaction();
        foreach (var (table, oldColumn, newColumn) in renames)
        {
            if (!ColumnExists(db, transaction, table, oldColumn)
                || ColumnExists(db, transaction, table, newColumn))
                continue;

            using var rename = db.CreateCommand();
            rename.Transaction = transaction;
            rename.CommandText = $"ALTER TABLE {table} RENAME COLUMN {oldColumn} TO {newColumn}";
            rename.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    private static bool ColumnExists(
        SqliteConnection db,
        SqliteTransaction transaction,
        string table,
        string column)
    {
        using var check = db.CreateCommand();
        check.Transaction = transaction;
        check.CommandText = $"PRAGMA table_info({table})";
        using var reader = check.ExecuteReader();
        while (reader.Read())
        {
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static void EnsureHostColumns(SqliteConnection db)
    {
        EnsureColumn(db, "hosts", "ignore_certificate_errors", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn(db, "hosts", "minecraft_uuid", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(db, "hosts", "halerith_access_token_protected", "BLOB");
        EnsureColumn(db, "hosts", "halerith_access_token_expires_at_utc", "TEXT NOT NULL DEFAULT ''");
    }

    private static void EnsureServerColumns(SqliteConnection db)
    {
        EnsureColumn(db, "servers", "environment_id", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(db, "servers", "environment_name", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(db, "servers", "motd", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(db, "servers", "java_garbage_collector", "TEXT NOT NULL DEFAULT 'ZGC'");
        EnsureColumn(db, "servers", "power_status", "TEXT NOT NULL DEFAULT 'unknown'");
        EnsureColumn(db, "servers", "server_icon_url", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(db, "servers", "server_icon_sha256", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(db, "servers", "server_icon_content_type", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(db, "servers", "game_host", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(db, "servers", "game_port", "INTEGER");
        EnsureColumn(db, "servers", "last_injected_server_address", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(db, "servers", "modpack_file_policy", "TEXT NOT NULL DEFAULT 'preserve'");
    }

    private static void EnsureLocalInstanceColumns(SqliteConnection db)
    {
        EnsureColumn(db, "local_instances", "java_garbage_collector", "TEXT NOT NULL DEFAULT 'ZGC'");
    }

    private static void EnsureImageCacheColumns(SqliteConnection db)
    {
        EnsureColumn(db, "image_cache", "image_hash", "TEXT NOT NULL DEFAULT ''");
    }

    private static void EnsureMinecraftAccountColumns(SqliteConnection db)
    {
        EnsureColumn(db, "minecraft_accounts", "xuid", "TEXT NOT NULL DEFAULT ''");
    }

    private static void EnsureColumn(SqliteConnection db, string table, string column, string definition)
    {
        using (var check = db.CreateCommand())
        {
            check.CommandText = $"PRAGMA table_info({table})";
            using var reader = check.ExecuteReader();
            while (reader.Read())
            {
                if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                    return;
            }
        }

        Execute(db, $"ALTER TABLE {table} ADD COLUMN {column} {definition}");
    }

    private static void DeleteMissing(SqliteConnection db, SqliteTransaction tx, string table, string idColumn, HashSet<string> ids)
    {
        using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        if (ids.Count == 0)
        {
            cmd.CommandText = $"DELETE FROM {table}";
        }
        else
        {
            var parameters = ids.Select((_, i) => $"$id{i}").ToArray();
            cmd.CommandText = $"DELETE FROM {table} WHERE {idColumn} NOT IN ({string.Join(",", parameters)})";
            var index = 0;
            foreach (var id in ids)
                cmd.Parameters.AddWithValue($"$id{index++}", id);
        }
        cmd.ExecuteNonQuery();
    }

    private static void DeleteMissingServers(SqliteConnection db, SqliteTransaction tx, string hostId, HashSet<string> serverPublicIds)
    {
        using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        if (serverPublicIds.Count == 0)
        {
            cmd.CommandText = "DELETE FROM servers WHERE host_id = $host";
            cmd.Parameters.AddWithValue("$host", hostId);
        }
        else
        {
            var parameters = serverPublicIds.Select((_, i) => $"$server{i}").ToArray();
            cmd.CommandText = $"DELETE FROM servers WHERE host_id = $host AND server_public_id NOT IN ({string.Join(",", parameters)})";
            cmd.Parameters.AddWithValue("$host", hostId);
            var index = 0;
            foreach (var id in serverPublicIds)
                cmd.Parameters.AddWithValue($"$server{index++}", id);
        }
        cmd.ExecuteNonQuery();
    }

    private static string GetSetting(Dictionary<string, string> settings, string key) =>
        settings.TryGetValue(key, out var value) ? value : "";

    private static int GetIntSetting(Dictionary<string, string> settings, string key, int fallback) =>
        int.TryParse(GetSetting(settings, key), out var value) ? value : fallback;

    private static bool GetBoolSetting(Dictionary<string, string> settings, string key, bool fallback)
    {
        var raw = GetSetting(settings, key);
        if (string.IsNullOrWhiteSpace(raw)) return fallback;
        return raw == "1" || string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeDefaultInstanceKind(string? value)
    {
        var clean = value?.Trim().ToLowerInvariant() ?? "";
        return clean == AppConfig.DefaultInstanceKindLocal
            ? AppConfig.DefaultInstanceKindLocal
            : AppConfig.DefaultInstanceKindServer;
    }

    private static void NormalizeDefaultInstance(AppConfig config)
    {
        var host = config.EnsureManagerScope();
        config.DefaultInstanceKind = NormalizeDefaultInstanceKind(config.DefaultInstanceKind);
        config.DefaultInstanceId = (config.DefaultInstanceId ?? "").Trim();

        if (string.IsNullOrWhiteSpace(config.DefaultInstanceId))
        {
            config.DefaultInstanceKind = AppConfig.DefaultInstanceKindServer;
            config.DefaultInstanceId = host.DefaultServerPublicId ?? "";
            return;
        }

        if (config.DefaultInstanceKind == AppConfig.DefaultInstanceKindLocal)
        {
            if (config.LocalInstances.Any(i =>
                    string.Equals(i.Id, config.DefaultInstanceId, StringComparison.OrdinalIgnoreCase)))
                return;

            config.DefaultInstanceKind = AppConfig.DefaultInstanceKindServer;
            config.DefaultInstanceId = host.DefaultServerPublicId ?? "";
            return;
        }

        if (!host.Servers.Any(s =>
                string.Equals(s.ServerPublicId, config.DefaultInstanceId, StringComparison.OrdinalIgnoreCase)))
            config.DefaultInstanceId = host.DefaultServerPublicId ?? "";
    }

    private static List<T> DeserializeList<T>(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try { return JsonSerializer.Deserialize<List<T>>(json, SerializerOptions.Default) ?? []; }
        catch { return []; }
    }

    private static byte[] ProtectString(string value)
    {
        var plain = Encoding.UTF8.GetBytes(value);
        return ProtectBytes(plain);
    }

    private static byte[] ProtectBytes(byte[] plainBytes) =>
        PlatformServices.Current.Secrets.Protect(plainBytes, "app-storage");

    private static void AddProtectedParameter(SqliteCommand cmd, string name, string? value)
    {
        var parameter = cmd.Parameters.Add(name, SqliteType.Blob);
        parameter.Value = string.IsNullOrWhiteSpace(value)
            ? DBNull.Value
            : ProtectString(value);
    }

    private static string UnprotectString(byte[] encrypted)
    {
        if (encrypted.Length == 0) return "";
        var plain = UnprotectBytes(encrypted);
        return plain is null ? "" : Encoding.UTF8.GetString(plain);
    }

    private static byte[]? UnprotectBytes(byte[] encrypted)
    {
        if (encrypted.Length == 0) return null;
        try
        {
            return PlatformServices.Current.Secrets.Unprotect(encrypted, "app-storage");
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    private static DateTime ParseUtc(string value)
        => DateTime.TryParse(
            value,
            null,
            System.Globalization.DateTimeStyles.RoundtripKind,
            out var parsed)
            ? parsed.ToUniversalTime()
            : DateTime.MinValue;

    private static void Execute(SqliteConnection db, string sql)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static void Execute(SqliteConnection db, SqliteTransaction tx, string sql)
    {
        using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
