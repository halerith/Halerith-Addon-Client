using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace HalerithAddonClient.Core.Platform;

public sealed class SecretStorageUnavailableException : Exception
{
    public SecretStorageUnavailableException(string message)
        : base(message)
    {
    }

    public SecretStorageUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

internal enum LinuxSecretLookupStatus
{
    Found,
    NotFound,
    Unavailable,
}

internal readonly record struct LinuxSecretLookupResult(
    LinuxSecretLookupStatus Status,
    string? Value = null,
    string? SafeError = null);

internal interface ILinuxSecretStore
{
    LinuxSecretLookupResult Lookup(int version);
    bool Store(int version, string value);
    bool Delete(int version);
}

internal sealed class LinuxSecretProtector : ISecretProtector
{
    internal const string LegacyDatabaseKey = "linux_secret_master_key";
    private const string Purpose = "app-storage";
    private const string VersionOnePrefix = "v1";
    private const string VersionTwoPrefix = "v2";
    private readonly ILinuxSecretStore _store;
    private readonly object _sync = new();
    private byte[]? _versionTwoKey;
    private bool _ready;

    public LinuxSecretProtector()
        : this(new SecretToolLinuxSecretStore())
    {
    }

    internal LinuxSecretProtector(ILinuxSecretStore store)
    {
        _store = store;
    }

    public byte[] Protect(byte[] plainBytes, string purpose)
    {
        ArgumentNullException.ThrowIfNull(plainBytes);
        return ProtectWithKey(plainBytes, purpose, ReadyKey(), VersionTwoPrefix);
    }

    public byte[]? Unprotect(byte[] protectedBytes, string purpose)
    {
        ArgumentNullException.ThrowIfNull(protectedBytes);
        var key = ReadyKey();
        return EnvelopeVersion(protectedBytes) == 2
            ? UnprotectWithKey(protectedBytes, purpose, key, VersionTwoPrefix)
            : null;
    }

    internal void PrepareStorage(SqliteConnection database)
    {
        ArgumentNullException.ThrowIfNull(database);

        lock (_sync)
        {
            if (_ready)
                return;

            byte[]? versionTwoKey = null;
            byte[]? versionOneKey = null;
            byte[]? legacyDatabaseKey = null;
            var cells = new List<ProtectedCell>();

            try
            {
                var versionTwoLookup = RequireAvailable(_store.Lookup(2));
                var versionOneLookup = RequireAvailable(_store.Lookup(1));
                versionTwoKey = DecodeStoredKey(versionTwoLookup, "The Linux keyring contains an invalid Halerith key.");
                versionOneKey = DecodeStoredKey(versionOneLookup, "The Linux keyring contains an invalid legacy Halerith key.");
                legacyDatabaseKey = ReadLegacyDatabaseKey(database);
                cells = ReadProtectedCells(database);

                var versionOneCells = cells.Where(cell => EnvelopeVersion(cell.Value) == 1).ToList();
                var versionTwoCells = cells.Where(cell => EnvelopeVersion(cell.Value) == 2).ToList();
                if (versionOneCells.Count + versionTwoCells.Count != cells.Count)
                    throw new SecretStorageUnavailableException("Stored Linux credentials use an unsupported encryption format.");

                if (versionTwoCells.Count > 0)
                {
                    if (versionTwoKey is null || !CanDecryptAll(versionTwoCells, versionTwoKey, VersionTwoPrefix))
                        throw new SecretStorageUnavailableException("The Linux keyring cannot unlock the stored Halerith credentials.");
                }

                byte[]? versionOneSource = null;
                if (versionOneCells.Count > 0)
                {
                    versionOneSource = SelectVersionOneSource(versionOneCells, versionOneKey, legacyDatabaseKey)
                        ?? throw new SecretStorageUnavailableException("The Linux keyring cannot migrate the stored Halerith credentials.");
                }

                if (versionTwoKey is null)
                    versionTwoKey = CreateAndVerifyVersionTwoKey();

                if (versionOneCells.Count > 0 || legacyDatabaseKey is not null)
                    MigrateStorage(database, versionOneCells, versionOneSource, versionTwoKey);

                if (versionOneKey is not null)
                    _store.Delete(1);

                _versionTwoKey = versionTwoKey;
                versionTwoKey = null;
                _ready = true;
            }
            finally
            {
                Zero(versionTwoKey);
                Zero(versionOneKey);
                Zero(legacyDatabaseKey);
                foreach (var cell in cells)
                    CryptographicOperations.ZeroMemory(cell.Value);
            }
        }
    }

    private byte[] ReadyKey()
    {
        lock (_sync)
        {
            if (!_ready || _versionTwoKey is null)
                throw new SecretStorageUnavailableException("Linux secure storage has not been initialized.");

            return _versionTwoKey;
        }
    }

    private static LinuxSecretLookupResult RequireAvailable(LinuxSecretLookupResult lookup)
    {
        if (lookup.Status != LinuxSecretLookupStatus.Unavailable)
            return lookup;

        throw new SecretStorageUnavailableException(
            string.IsNullOrWhiteSpace(lookup.SafeError)
                ? "The Linux desktop keyring is unavailable or locked."
                : lookup.SafeError);
    }

    private static byte[]? DecodeStoredKey(LinuxSecretLookupResult lookup, string invalidMessage)
    {
        if (lookup.Status == LinuxSecretLookupStatus.NotFound)
            return null;
        if (lookup.Status != LinuxSecretLookupStatus.Found || !TryDecodeKey(lookup.Value, out var key))
            throw new SecretStorageUnavailableException(invalidMessage);
        return key;
    }

    private byte[] CreateAndVerifyVersionTwoKey()
    {
        var generated = RandomNumberGenerator.GetBytes(32);
        byte[]? stored = null;
        try
        {
            var encoded = Convert.ToBase64String(generated);
            if (!_store.Store(2, encoded))
                throw new SecretStorageUnavailableException("The Halerith encryption key could not be stored in the Linux desktop keyring.");

            var verification = RequireAvailable(_store.Lookup(2));
            stored = DecodeStoredKey(verification, "The Linux desktop keyring did not return the stored Halerith key.");
            if (stored is null || !CryptographicOperations.FixedTimeEquals(generated, stored))
                throw new SecretStorageUnavailableException("The Halerith encryption key could not be verified in the Linux desktop keyring.");

            return generated;
        }
        catch
        {
            CryptographicOperations.ZeroMemory(generated);
            throw;
        }
        finally
        {
            Zero(stored);
        }
    }

    private static byte[]? SelectVersionOneSource(
        IReadOnlyCollection<ProtectedCell> cells,
        byte[]? keyringKey,
        byte[]? databaseKey)
    {
        if (keyringKey is not null && CanDecryptAll(cells, keyringKey, VersionOnePrefix))
            return keyringKey;
        if (databaseKey is not null && CanDecryptAll(cells, databaseKey, VersionOnePrefix))
            return databaseKey;
        return null;
    }

    private static bool CanDecryptAll(
        IEnumerable<ProtectedCell> cells,
        byte[] key,
        string versionPrefix)
    {
        foreach (var cell in cells)
        {
            var plain = UnprotectWithKey(cell.Value, Purpose, key, versionPrefix);
            if (plain is null)
                return false;
            CryptographicOperations.ZeroMemory(plain);
        }
        return true;
    }

    private static void MigrateStorage(
        SqliteConnection database,
        IReadOnlyCollection<ProtectedCell> versionOneCells,
        byte[]? versionOneKey,
        byte[] versionTwoKey)
    {
        if (versionOneCells.Count > 0 && versionOneKey is null)
            throw new SecretStorageUnavailableException("The legacy Linux encryption key is unavailable.");

        Execute(database, "PRAGMA secure_delete=ON");
        using (var transaction = database.BeginTransaction())
        {
            foreach (var cell in versionOneCells)
            {
                var plain = UnprotectWithKey(cell.Value, Purpose, versionOneKey!, VersionOnePrefix)
                    ?? throw new SecretStorageUnavailableException("A stored Linux credential could not be migrated.");
                try
                {
                    var migrated = ProtectWithKey(plain, Purpose, versionTwoKey, VersionTwoPrefix);
                    using var update = database.CreateCommand();
                    update.Transaction = transaction;
                    update.CommandText = $"UPDATE {Quote(cell.Table)} SET {Quote(cell.Column)} = $value WHERE rowid = $rowid";
                    update.Parameters.Add("$value", SqliteType.Blob).Value = migrated;
                    update.Parameters.AddWithValue("$rowid", cell.RowId);
                    if (update.ExecuteNonQuery() != 1)
                        throw new SecretStorageUnavailableException("A stored Linux credential changed during migration.");
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(plain);
                }
            }

            using var deleteLegacyKey = database.CreateCommand();
            deleteLegacyKey.Transaction = transaction;
            deleteLegacyKey.CommandText = "DELETE FROM app_settings WHERE key = $key";
            deleteLegacyKey.Parameters.AddWithValue("$key", LegacyDatabaseKey);
            deleteLegacyKey.ExecuteNonQuery();
            transaction.Commit();
        }

        Execute(database, "PRAGMA wal_checkpoint(TRUNCATE)");
        Execute(database, "VACUUM");
    }

    private static List<ProtectedCell> ReadProtectedCells(SqliteConnection database)
    {
        var cells = new List<ProtectedCell>();
        ReadProtectedCells(database, cells, "hosts", ["halerith_access_token_protected"]);
        ReadProtectedCells(database, cells, "minecraft_accounts", [
            "ms_access_token_protected",
            "ms_refresh_token_protected",
            "mc_access_token_protected",
        ]);
        ReadProtectedCells(database, cells, "secure_items", ["protected_value"]);
        return cells;
    }

    private static void ReadProtectedCells(
        SqliteConnection database,
        ICollection<ProtectedCell> cells,
        string table,
        IEnumerable<string> columns)
    {
        foreach (var column in columns)
        {
            using var command = database.CreateCommand();
            command.CommandText = $"SELECT rowid, {Quote(column)} FROM {Quote(table)} WHERE {Quote(column)} IS NOT NULL AND length({Quote(column)}) > 0";
            using var reader = command.ExecuteReader();
            while (reader.Read())
                cells.Add(new ProtectedCell(table, column, reader.GetInt64(0), (byte[])reader.GetValue(1)));
        }
    }

    private static byte[]? ReadLegacyDatabaseKey(SqliteConnection database)
    {
        using var command = database.CreateCommand();
        command.CommandText = "SELECT value FROM app_settings WHERE key = $key";
        command.Parameters.AddWithValue("$key", LegacyDatabaseKey);
        var encoded = command.ExecuteScalar() as string;
        if (string.IsNullOrWhiteSpace(encoded))
            return null;
        if (!TryDecodeKey(encoded, out var key))
            throw new SecretStorageUnavailableException("The legacy Linux encryption key is invalid.");
        return key;
    }

    private static bool TryDecodeKey(string? encoded, out byte[] key)
    {
        try
        {
            key = Convert.FromBase64String(encoded?.Trim() ?? string.Empty);
            if (key.Length == 32)
                return true;
            CryptographicOperations.ZeroMemory(key);
        }
        catch (FormatException)
        {
            // Handled by the caller as an invalid key.
        }

        key = [];
        return false;
    }

    internal static byte[] ProtectVersionOneForMigrationTest(byte[] plainBytes, byte[] key) =>
        ProtectWithKey(plainBytes, Purpose, key, VersionOnePrefix);

    internal static int EnvelopeVersion(byte[] protectedBytes)
    {
        if (HasPrefix(protectedBytes, VersionOnePrefix))
            return 1;
        return HasPrefix(protectedBytes, VersionTwoPrefix) ? 2 : 0;
    }

    private static bool HasPrefix(byte[] protectedBytes, string versionPrefix)
    {
        var prefixBytes = Encoding.ASCII.GetBytes(versionPrefix);
        return protectedBytes.Length >= prefixBytes.Length + 1 + 12 + 16
            && protectedBytes.AsSpan(0, prefixBytes.Length).SequenceEqual(prefixBytes)
            && protectedBytes[prefixBytes.Length] == 0;
    }

    private static byte[] ProtectWithKey(
        byte[] plainBytes,
        string purpose,
        byte[] key,
        string versionPrefix)
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var tag = new byte[16];
        var cipher = new byte[plainBytes.Length];
        using var aes = new AesGcm(key, tag.Length);
        aes.Encrypt(nonce, plainBytes, cipher, tag, GetAssociatedData(purpose));

        using var payload = new MemoryStream();
        payload.Write(Encoding.ASCII.GetBytes(versionPrefix));
        payload.WriteByte(0);
        payload.Write(nonce);
        payload.Write(tag);
        payload.Write(cipher);
        return payload.ToArray();
    }

    private static byte[]? UnprotectWithKey(
        byte[] protectedBytes,
        string purpose,
        byte[] key,
        string versionPrefix)
    {
        try
        {
            if (!HasPrefix(protectedBytes, versionPrefix))
                return null;
            var offset = versionPrefix.Length + 1;
            var nonce = protectedBytes.AsSpan(offset, 12);
            offset += 12;
            var tag = protectedBytes.AsSpan(offset, 16);
            offset += 16;
            var cipher = protectedBytes.AsSpan(offset);
            var plain = new byte[cipher.Length];
            using var aes = new AesGcm(key, tag.Length);
            aes.Decrypt(nonce, cipher, tag, plain, GetAssociatedData(purpose));
            return plain;
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    private static byte[] GetAssociatedData(string purpose) =>
        Encoding.UTF8.GetBytes("HalerithAddonClient:" + purpose);

    private static void Execute(SqliteConnection database, string sql)
    {
        using var command = database.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static string Quote(string identifier) =>
        $"\"{identifier.Replace("\"", "\"\"")}\"";

    private static void Zero(byte[]? value)
    {
        if (value is not null)
            CryptographicOperations.ZeroMemory(value);
    }

    private sealed record ProtectedCell(string Table, string Column, long RowId, byte[] Value);
}

internal sealed class SecretToolLinuxSecretStore : ILinuxSecretStore
{
    private const string ServiceName = "com.halerith.addon-client.master-key";
    private const int TimeoutMilliseconds = 15_000;

    public LinuxSecretLookupResult Lookup(int version)
    {
        var result = Run(BuildAttributes("lookup", version), null);
        if (!result.Started)
            return new(LinuxSecretLookupStatus.Unavailable, SafeError: "Linux secure storage requires the desktop Secret Service and secret-tool.");
        if (result.TimedOut)
            return new(LinuxSecretLookupStatus.Unavailable, SafeError: "The Linux desktop keyring did not respond.");
        if (result.ExitCode == 0 && !string.IsNullOrWhiteSpace(result.StandardOutput))
            return new(LinuxSecretLookupStatus.Found, result.StandardOutput.Trim());
        if (result.ExitCode == 1 && string.IsNullOrWhiteSpace(result.StandardError))
            return new(LinuxSecretLookupStatus.NotFound);
        return new(LinuxSecretLookupStatus.Unavailable, SafeError: "The Linux desktop keyring is unavailable or locked.");
    }

    public bool Store(int version, string value)
    {
        var arguments = BuildAttributes("store", version);
        arguments.Insert(1, "--label=Halerith Addon Client encryption key");
        var result = Run(arguments, value);
        return result.Started && !result.TimedOut && result.ExitCode == 0;
    }

    public bool Delete(int version)
    {
        var result = Run(BuildAttributes("clear", version), null);
        return result.Started && !result.TimedOut && result.ExitCode is 0 or 1;
    }

    private static List<string> BuildAttributes(string operation, int version)
    {
        var arguments = new List<string>
        {
            operation,
            "application", ServiceName,
            "purpose", version >= 2 ? "master-key-v2" : "master-key",
        };
        if (version >= 2)
        {
            arguments.Add("version");
            arguments.Add(version.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        return arguments;
    }

    private static SecretToolResult Run(IReadOnlyCollection<string> arguments, string? input)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "secret-tool",
                RedirectStandardInput = input is not null,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var argument in arguments)
                startInfo.ArgumentList.Add(argument);

            using var process = Process.Start(startInfo);
            if (process is null)
                return new(false, false, -1, string.Empty, string.Empty);

            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            if (input is not null)
            {
                process.StandardInput.Write(input);
                process.StandardInput.Close();
            }

            if (!process.WaitForExit(TimeoutMilliseconds))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                    // The process may have exited between the timeout and kill.
                }
                process.WaitForExit();
                return new(true, true, -1, output.GetAwaiter().GetResult(), error.GetAwaiter().GetResult());
            }

            return new(true, false, process.ExitCode, output.GetAwaiter().GetResult(), error.GetAwaiter().GetResult());
        }
        catch
        {
            return new(false, false, -1, string.Empty, string.Empty);
        }
    }

    private readonly record struct SecretToolResult(
        bool Started,
        bool TimedOut,
        int ExitCode,
        string StandardOutput,
        string StandardError);
}
