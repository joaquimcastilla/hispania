using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Hispania.Core.Configuration;

namespace Hispania.Core.Persistence;

internal sealed class SqliteCorePersistence : ICorePersistence
{
    private const int CurrentSchemaVersion = 2;
    private const int BusyTimeoutMilliseconds = 5000;
    private const string MigrationTable = "core_schema_migrations";
    private const string InstanceTable = "core_instance";
    private static readonly Action<ILogger, int, Exception?> Initialized =
        LoggerMessage.Define<int>(LogLevel.Information, new EventId(10, "CorePersistenceInitialized"), "Core SQLite persistence initialized at schema version {SchemaVersion}.");
    private static readonly Action<ILogger, string, Exception?> InitializationFailed =
        LoggerMessage.Define<string>(LogLevel.Error, new EventId(11, "CorePersistenceInitializationFailed"), "Core SQLite initialization failed with {FailureType}; database contents were not deleted.");
    private static readonly Action<ILogger, string, Exception?> StateReadFailed =
        LoggerMessage.Define<string>(LogLevel.Error, new EventId(12, "CoreStateReadFailed"), "Core state read failed with {FailureType}.");
    private static readonly Action<ILogger, string, Exception?> DirectoryPreparationFailed =
        LoggerMessage.Define<string>(LogLevel.Error, new EventId(13, "CoreDirectoryPreparationFailed"), "Core data directory preparation failed with {FailureType}.");
    private static readonly Action<ILogger, Exception?> CheckpointDeferred =
        LoggerMessage.Define(LogLevel.Warning, new EventId(14, "CoreCheckpointDeferred"), "SQLite passive checkpoint was deferred because active readers or writers remain.");
    private static readonly Action<ILogger, Exception?> CheckpointCompleted =
        LoggerMessage.Define(LogLevel.Information, new EventId(15, "CoreCheckpointCompleted"), "Core SQLite passive checkpoint completed.");
    private static readonly Action<ILogger, string, Exception?> CheckpointFailed =
        LoggerMessage.Define<string>(LogLevel.Warning, new EventId(16, "CoreCheckpointFailed"), "Core SQLite passive checkpoint ended with {FailureType}; SQLite recovery remains available on next open.");
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> InitializationLocks = new(PathComparer);

    private readonly CoreStorageOptions _options;
    private readonly ILogger<SqliteCorePersistence> _logger;
    private readonly string _connectionString;
    private readonly SemaphoreSlim _instanceLock;
    private volatile bool _initialized;

    public SqliteCorePersistence(CoreStorageOptions options, ILogger<SqliteCorePersistence> logger)
    {
        _options = options;
        _logger = logger;
        _instanceLock = InitializationLocks.GetOrAdd(options.DatabasePath, static _ => new SemaphoreSlim(1, 1));
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = options.DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            DefaultTimeout = BusyTimeoutMilliseconds / 1000,
            Pooling = false
        }.ToString();
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        if (_initialized) return;
        await _instanceLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_initialized) return;
            PrepareDataDirectory();
            await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

            var integrity = await ExecuteScalarStringAsync(connection, "PRAGMA quick_check;", cancellationToken).ConfigureAwait(false);
            if (!string.Equals(integrity, "ok", StringComparison.OrdinalIgnoreCase))
                throw new CorePersistenceException("Core storage failed SQLite integrity verification; data was left unchanged.");

            await using (var journal = connection.CreateCommand())
            {
                journal.CommandText = "PRAGMA journal_mode=WAL;";
                var mode = Convert.ToString(await journal.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
                if (!string.Equals(mode, "wal", StringComparison.OrdinalIgnoreCase))
                    throw new CorePersistenceException("Core storage could not enable the configured SQLite journal mode.");
            }

            // Microsoft.Data.Sqlite's native transaction acquisition is synchronous even
            // for the ADO.NET async surface. Keep its bounded busy wait off the host-start
            // thread so cancellation and lifecycle coordination remain responsive.
            await using (var transaction = await Task.Run(
                () => connection.BeginTransaction(deferred: false), cancellationToken).ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();
                await ApplyMigrationsAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }

            ApplyPrivateFilePermissions();
            _initialized = true;
            Initialized(_logger, CurrentSchemaVersion, null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (CorePersistenceException)
        {
            throw;
        }
        catch (Exception exception) when (exception is SqliteException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            InitializationFailed(_logger, exception.GetType().Name, null);
            throw new CorePersistenceException("Core SQLite initialization failed; the existing database was preserved.");
        }
        finally
        {
            _instanceLock.Release();
        }
    }

    public async Task<CoreInstanceState> GetInstanceStateAsync(CancellationToken cancellationToken)
    {
        EnsureInitialized();
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = $"SELECT installation_id, created_utc FROM {InstanceTable} WHERE singleton_id = 1;";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ||
                !Guid.TryParseExact(reader.GetString(0), "N", out var installationId) ||
                !DateTimeOffset.TryParseExact(reader.GetString(1), "O", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var createdUtc))
                throw new CorePersistenceException("Core storage contains invalid instance metadata; the database was preserved.");
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                throw new CorePersistenceException("Core storage contains duplicate instance metadata; the database was preserved.");
            return new CoreInstanceState(installationId, createdUtc);
        }
        catch (OperationCanceledException) { throw; }
        catch (CorePersistenceException) { throw; }
        catch (Exception exception) when (exception is SqliteException or IOException or UnauthorizedAccessException)
        {
            StateReadFailed(_logger, exception.GetType().Name, null);
            throw new CorePersistenceException("Core instance state could not be read; the database was preserved.");
        }
    }

    public async Task CheckpointAsync(CancellationToken cancellationToken)
    {
        if (!_initialized) return;
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA wal_checkpoint(PASSIVE);";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false) && reader.GetInt32(0) != 0)
                CheckpointDeferred(_logger, null);
            else
                CheckpointCompleted(_logger, null);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) when (exception is SqliteException or IOException or UnauthorizedAccessException)
        {
            CheckpointFailed(_logger, exception.GetType().Name, null);
        }
    }

    private static async Task ApplyMigrationsAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken cancellationToken)
    {
        var hasMigrationTable = await TableExistsAsync(connection, transaction, MigrationTable, cancellationToken).ConfigureAwait(false);
        if (!hasMigrationTable)
        {
            var existingTables = await GetUserTableNamesAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
            if (existingTables.Count != 0)
                throw new CorePersistenceException("Core database has no migration history and contains existing tables; it was preserved.");

            await ExecuteAsync(connection, transaction,
                $"CREATE TABLE {MigrationTable} (version INTEGER NOT NULL PRIMARY KEY CHECK (version > 0), applied_utc TEXT NOT NULL);",
                cancellationToken).ConfigureAwait(false);
        }

        var versions = await ReadAppliedVersionsAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        if (versions.Any(version => version > CurrentSchemaVersion))
            throw new CorePersistenceException("Core database schema is newer than this Core version; it was preserved.");
        if (!versions.SequenceEqual(Enumerable.Range(1, versions.Count)))
            throw new CorePersistenceException("Core database migration history is incomplete; it was preserved.");

        var appliedVersions = versions.ToList();
        if (appliedVersions.Count == 0)
        {
            await ApplyMigration1Async(connection, transaction, cancellationToken).ConfigureAwait(false);
            await RecordMigrationAsync(connection, transaction, 1, cancellationToken).ConfigureAwait(false);
            appliedVersions.Add(1);
        }

        for (var nextVersion = appliedVersions.Count + 1; nextVersion <= CurrentSchemaVersion; nextVersion++)
        {
            switch (nextVersion)
            {
                case 2:
                    await ApplyMigration2Async(connection, transaction, cancellationToken).ConfigureAwait(false);
                    break;
                default:
                    throw new CorePersistenceException("Core database is missing a supported migration; it was preserved.");
            }
            await RecordMigrationAsync(connection, transaction, nextVersion, cancellationToken).ConfigureAwait(false);
        }

        await VerifyCurrentSchemaAsync(connection, transaction, cancellationToken).ConfigureAwait(false);

        await using var foreignKeys = connection.CreateCommand();
        foreignKeys.Transaction = transaction;
        foreignKeys.CommandText = "PRAGMA foreign_key_check;";
        await using var violations = await foreignKeys.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (await violations.ReadAsync(cancellationToken).ConfigureAwait(false))
            throw new CorePersistenceException("Core database has referential integrity violations; it was preserved.");
    }

    private static async Task ApplyMigration1Async(SqliteConnection connection, SqliteTransaction transaction, CancellationToken cancellationToken)
    {
        await ExecuteAsync(connection, transaction,
            $"CREATE TABLE {InstanceTable} (singleton_id INTEGER NOT NULL PRIMARY KEY CHECK (singleton_id = 1), installation_id TEXT NOT NULL UNIQUE, created_utc TEXT NOT NULL);",
            cancellationToken).ConfigureAwait(false);
        await using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = $"INSERT INTO {InstanceTable} (singleton_id, installation_id, created_utc) VALUES (1, $installationId, $createdUtc);";
        insert.Parameters.AddWithValue("$installationId", Guid.NewGuid().ToString("N"));
        insert.Parameters.AddWithValue("$createdUtc", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task RecordMigrationAsync(SqliteConnection connection, SqliteTransaction transaction, int version, CancellationToken cancellationToken)
    {
        await using var applied = connection.CreateCommand();
        applied.Transaction = transaction;
        applied.CommandText = $"INSERT INTO {MigrationTable} (version, applied_utc) VALUES ($version, $appliedUtc);";
        applied.Parameters.AddWithValue("$version", version);
        applied.Parameters.AddWithValue("$appliedUtc", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        await applied.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task ApplyMigration2Async(SqliteConnection connection, SqliteTransaction transaction, CancellationToken cancellationToken)
    {
        var statements = new[]
        {
            "ALTER TABLE core_instance ADD COLUMN bootstrap_completed_utc TEXT NULL;",
            "CREATE TABLE core_identities (subject_id TEXT NOT NULL PRIMARY KEY CHECK(length(subject_id) = 32), subject_kind TEXT NOT NULL CHECK(length(subject_kind) BETWEEN 1 AND 32), username TEXT NOT NULL CHECK(length(username) BETWEEN 3 AND 64), username_normalized TEXT NOT NULL UNIQUE CHECK(length(username_normalized) BETWEEN 3 AND 64), status TEXT NOT NULL CHECK(status IN ('active', 'disabled')), created_utc TEXT NOT NULL, updated_utc TEXT NOT NULL);",
            "CREATE TABLE core_credentials (subject_id TEXT NOT NULL PRIMARY KEY, hash_format TEXT NOT NULL CHECK(length(hash_format) BETWEEN 1 AND 64), algorithm TEXT NOT NULL CHECK(length(algorithm) BETWEEN 1 AND 64), iterations INTEGER NOT NULL CHECK(iterations BETWEEN 100000 AND 220000), password_hash TEXT NOT NULL CHECK(length(password_hash) BETWEEN 1 AND 512), updated_utc TEXT NOT NULL, FOREIGN KEY(subject_id) REFERENCES core_identities(subject_id) ON DELETE CASCADE);",
            "CREATE TABLE core_roles (role_id TEXT NOT NULL PRIMARY KEY CHECK(length(role_id) BETWEEN 2 AND 128), is_system INTEGER NOT NULL CHECK(is_system IN (0, 1)), created_utc TEXT NOT NULL);",
            "CREATE TABLE core_permissions (permission_id TEXT NOT NULL PRIMARY KEY CHECK(length(permission_id) BETWEEN 2 AND 128), created_utc TEXT NOT NULL);",
            "CREATE TABLE core_role_permissions (role_id TEXT NOT NULL, permission_id TEXT NOT NULL, granted_utc TEXT NOT NULL, PRIMARY KEY(role_id, permission_id), FOREIGN KEY(role_id) REFERENCES core_roles(role_id) ON DELETE CASCADE, FOREIGN KEY(permission_id) REFERENCES core_permissions(permission_id) ON DELETE CASCADE);",
            "CREATE TABLE core_subject_roles (subject_id TEXT NOT NULL, role_id TEXT NOT NULL, assigned_utc TEXT NOT NULL, PRIMARY KEY(subject_id, role_id), FOREIGN KEY(subject_id) REFERENCES core_identities(subject_id) ON DELETE CASCADE, FOREIGN KEY(role_id) REFERENCES core_roles(role_id) ON DELETE CASCADE);",
            "CREATE TABLE core_sessions (session_id TEXT NOT NULL PRIMARY KEY CHECK(length(session_id) = 32), subject_id TEXT NOT NULL, token_hash BLOB NOT NULL UNIQUE CHECK(length(token_hash) = 32), created_utc TEXT NOT NULL, expires_utc TEXT NOT NULL, revoked_utc TEXT NULL, FOREIGN KEY(subject_id) REFERENCES core_identities(subject_id) ON DELETE CASCADE);",
            "CREATE INDEX ix_core_sessions_subject_active ON core_sessions(subject_id, revoked_utc, expires_utc);",
            "CREATE TABLE core_bootstrap_state (singleton_id INTEGER NOT NULL PRIMARY KEY CHECK(singleton_id = 1), completed_utc TEXT NULL, completed_by_subject_id TEXT NULL, FOREIGN KEY(completed_by_subject_id) REFERENCES core_identities(subject_id) ON DELETE SET NULL);",
            "CREATE TABLE core_security_audit (event_id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT, event_code TEXT NOT NULL CHECK(length(event_code) BETWEEN 1 AND 64), occurred_utc TEXT NOT NULL, actor_subject_id TEXT NULL, subject_id TEXT NULL, FOREIGN KEY(actor_subject_id) REFERENCES core_identities(subject_id) ON DELETE SET NULL, FOREIGN KEY(subject_id) REFERENCES core_identities(subject_id) ON DELETE SET NULL);",
            "INSERT INTO core_bootstrap_state(singleton_id, completed_utc, completed_by_subject_id) VALUES (1, NULL, NULL);",
            "INSERT INTO core_roles(role_id, is_system, created_utc) VALUES ('core.admin', 1, strftime('%Y-%m-%dT%H:%M:%fZ', 'now'));",
            "INSERT INTO core_permissions(permission_id, created_utc) VALUES ('core.identity.read', strftime('%Y-%m-%dT%H:%M:%fZ', 'now')), ('core.identity.manage', strftime('%Y-%m-%dT%H:%M:%fZ', 'now')), ('core.authorization.manage', strftime('%Y-%m-%dT%H:%M:%fZ', 'now')), ('core.session.revoke', strftime('%Y-%m-%dT%H:%M:%fZ', 'now'));",
            "INSERT INTO core_role_permissions(role_id, permission_id, granted_utc) SELECT 'core.admin', permission_id, strftime('%Y-%m-%dT%H:%M:%fZ', 'now') FROM core_permissions;"
        };

        foreach (var statement in statements)
            await ExecuteAsync(connection, transaction, statement, cancellationToken).ConfigureAwait(false);
    }

    private static async Task VerifyCurrentSchemaAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken cancellationToken)
    {
        await using (var migrationShape = connection.CreateCommand())
        {
            migrationShape.Transaction = transaction;
            migrationShape.CommandText = $"SELECT version, applied_utc FROM {MigrationTable} LIMIT 0;";
            await migrationShape.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT singleton_id, installation_id, created_utc, bootstrap_completed_utc FROM {InstanceTable} ORDER BY singleton_id;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false) || reader.GetInt32(0) != 1 ||
            !Guid.TryParseExact(reader.GetString(1), "N", out _) ||
            !DateTimeOffset.TryParseExact(reader.GetString(2), "O", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out _))
            throw new CorePersistenceException("Core database instance metadata is invalid; it was preserved.");
        if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            throw new CorePersistenceException("Core database contains unexpected instance records; it was preserved.");
        await reader.DisposeAsync().ConfigureAwait(false);

        foreach (var table in new[] { "core_identities", "core_credentials", "core_roles", "core_permissions", "core_role_permissions", "core_subject_roles", "core_sessions", "core_bootstrap_state", "core_security_audit" })
        {
            if (!await TableExistsAsync(connection, transaction, table, cancellationToken).ConfigureAwait(false))
                throw new CorePersistenceException("Core database schema is incomplete; it was preserved.");
        }
        await using var bootstrap = connection.CreateCommand();
        bootstrap.Transaction = transaction;
        bootstrap.CommandText = "SELECT COUNT(*) FROM core_bootstrap_state WHERE singleton_id = 1;";
        if (Convert.ToInt32(await bootstrap.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture) != 1)
            throw new CorePersistenceException("Core database bootstrap state is missing or inconsistent; it was preserved.");
    }

    private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA busy_timeout=5000; PRAGMA foreign_keys=ON; PRAGMA synchronous=FULL;";
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private void PrepareDataDirectory()
    {
        try
        {
            Directory.CreateDirectory(_options.DataDirectory);
            var directoryAttributes = File.GetAttributes(_options.DataDirectory);
            if ((directoryAttributes & FileAttributes.ReparsePoint) != 0)
                throw new CorePersistenceException("Core data directory cannot be a symbolic link or reparse point.");
            if (File.Exists(_options.DatabasePath) && (File.GetAttributes(_options.DatabasePath) & FileAttributes.ReparsePoint) != 0)
                throw new CorePersistenceException("Core database file cannot be a symbolic link or reparse point.");
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(_options.DataDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        catch (CorePersistenceException) { throw; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            DirectoryPreparationFailed(_logger, exception.GetType().Name, null);
            throw new CorePersistenceException("Core data directory is unavailable; existing data was preserved.");
        }
    }

    private void ApplyPrivateFilePermissions()
    {
        if (OperatingSystem.IsWindows() || !File.Exists(_options.DatabasePath)) return;
        File.SetUnixFileMode(_options.DatabasePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    private static async Task<IReadOnlyList<int>> ReadAppliedVersionsAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT version FROM {MigrationTable} ORDER BY version;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var versions = new List<int>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var version = reader.GetInt32(0);
            if (version < 1) throw new CorePersistenceException("Core database migration history contains an invalid version; it was preserved.");
            versions.Add(version);
        }
        return versions;
    }

    private static async Task<bool> TableExistsAsync(SqliteConnection connection, SqliteTransaction transaction, string name, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT EXISTS (SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $name);";
        command.Parameters.AddWithValue("$name", name);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture) == 1;
    }

    private static async Task<IReadOnlyList<string>> GetUserTableNamesAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' ORDER BY name;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var names = new List<string>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) names.Add(reader.GetString(0));
        return names;
    }

    private static async Task ExecuteAsync(SqliteConnection connection, SqliteTransaction transaction, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<string?> ExecuteScalarStringAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
    }

    private void EnsureInitialized()
    {
        if (!_initialized) throw new InvalidOperationException("Core persistence has not completed initialization.");
    }

    private static StringComparer PathComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
}
