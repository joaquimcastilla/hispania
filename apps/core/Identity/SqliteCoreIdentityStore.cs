using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Hispania.Core.Configuration;
using Hispania.Core.Persistence;
using Microsoft.Data.Sqlite;

namespace Hispania.Core.Identity;

internal sealed class SqliteCoreIdentityStore : ICoreIdentityStore
{
    private const int BusyTimeoutSeconds = 5;
    private readonly string _connectionString;

    public SqliteCoreIdentityStore(CoreStorageOptions options)
    {
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = options.DatabasePath,
            Mode = SqliteOpenMode.ReadWrite,
            Cache = SqliteCacheMode.Private,
            DefaultTimeout = BusyTimeoutSeconds,
            Pooling = false
        }.ToString();
    }

    public async Task<bool> IsBootstrapPendingAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT CASE WHEN
                i.bootstrap_completed_utc IS NULL
                AND b.completed_utc IS NULL
                AND NOT EXISTS (SELECT 1 FROM core_subject_roles sr WHERE sr.role_id = $adminRole)
                AND NOT EXISTS (SELECT 1 FROM core_security_audit a WHERE a.event_code = 'bootstrap.admin.completed')
            THEN 1 ELSE 0 END
            FROM core_instance i CROSS JOIN core_bootstrap_state b
            WHERE i.singleton_id = 1 AND b.singleton_id = 1;
            """;
        command.Parameters.AddWithValue("$adminRole", CoreIdentityRules.AdministratorRole);
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (result is null or DBNull) throw new CorePersistenceException("Core bootstrap state is unavailable or inconsistent.");
        return Convert.ToInt32(result, CultureInfo.InvariantCulture) == 1;
    }

    public async Task<CoreBootstrapResult> BootstrapAdministratorAsync(
        string username,
        string normalizedUsername,
        string passwordHash,
        int iterations,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await BeginImmediateAsync(connection, cancellationToken).ConfigureAwait(false);
        if (!await IsBootstrapPendingAsync(connection, transaction, cancellationToken).ConfigureAwait(false))
            return CoreBootstrapResult.AlreadyCompleted;

        var subjectId = Guid.NewGuid();
        var subjectIdText = subjectId.ToString("N");
        var now = FormatUtc(nowUtc);
        try
        {
            await using (var identity = connection.CreateCommand())
            {
                identity.Transaction = transaction;
                identity.CommandText = "INSERT INTO core_identities(subject_id, subject_kind, username, username_normalized, status, created_utc, updated_utc) VALUES ($id, $kind, $username, $normalized, 'active', $now, $now);";
                identity.Parameters.AddWithValue("$id", subjectIdText);
                identity.Parameters.AddWithValue("$kind", CoreIdentityRules.LocalUserKind);
                identity.Parameters.AddWithValue("$username", username);
                identity.Parameters.AddWithValue("$normalized", normalizedUsername);
                identity.Parameters.AddWithValue("$now", now);
                await identity.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await InsertCredentialAsync(connection, transaction, subjectIdText, passwordHash, iterations, now, cancellationToken).ConfigureAwait(false);
            await AssignRoleWithinTransactionAsync(connection, transaction, subjectIdText, CoreIdentityRules.AdministratorRole, now, cancellationToken).ConfigureAwait(false);
            await using (var state = connection.CreateCommand())
            {
                state.Transaction = transaction;
                state.CommandText = "UPDATE core_bootstrap_state SET completed_utc = $now, completed_by_subject_id = $id WHERE singleton_id = 1 AND completed_utc IS NULL; UPDATE core_instance SET bootstrap_completed_utc = $now WHERE singleton_id = 1 AND bootstrap_completed_utc IS NULL;";
                state.Parameters.AddWithValue("$now", now);
                state.Parameters.AddWithValue("$id", subjectIdText);
                if (await state.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 2)
                    throw new CorePersistenceException("Core bootstrap state changed concurrently; the operation was rolled back.");
            }
            await InsertAuditAsync(connection, transaction, "bootstrap.admin.completed", now, subjectIdText, subjectIdText, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return CoreBootstrapResult.Created;
        }
        catch (SqliteException)
        {
            return CoreBootstrapResult.Failed;
        }
    }

    public async Task<CoreIdentity?> CreateLocalAccountAsync(
        Guid actorSubjectId,
        string username,
        string normalizedUsername,
        string passwordHash,
        int iterations,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await BeginImmediateAsync(connection, cancellationToken).ConfigureAwait(false);
        if (!await IsAuthorizedAsync(connection, transaction, actorSubjectId, CoreIdentityRules.IdentityManagePermission, cancellationToken).ConfigureAwait(false))
            return null;

        var subjectId = Guid.NewGuid();
        var subjectIdText = subjectId.ToString("N");
        var now = FormatUtc(nowUtc);
        try
        {
            await using (var identity = connection.CreateCommand())
            {
                identity.Transaction = transaction;
                identity.CommandText = "INSERT INTO core_identities(subject_id, subject_kind, username, username_normalized, status, created_utc, updated_utc) VALUES ($id, $kind, $username, $normalized, 'active', $now, $now);";
                identity.Parameters.AddWithValue("$id", subjectIdText);
                identity.Parameters.AddWithValue("$kind", CoreIdentityRules.LocalUserKind);
                identity.Parameters.AddWithValue("$username", username);
                identity.Parameters.AddWithValue("$normalized", normalizedUsername);
                identity.Parameters.AddWithValue("$now", now);
                await identity.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            await InsertCredentialAsync(connection, transaction, subjectIdText, passwordHash, iterations, now, cancellationToken).ConfigureAwait(false);
            await InsertAuditAsync(connection, transaction, "identity.created", now, actorSubjectId.ToString("N"), subjectIdText, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new CoreIdentity(subjectId, username, normalizedUsername, CoreIdentityRules.LocalUserKind, CoreAccountStatus.Active, nowUtc, nowUtc);
        }
        catch (SqliteException)
        {
            return null;
        }
    }

    public async Task<StoredCoreIdentity?> FindByUsernameAsync(string normalizedUsername, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT i.subject_id, i.username, i.username_normalized, i.subject_kind, i.status, i.created_utc, i.updated_utc,
                   c.hash_format, c.algorithm, c.iterations, c.password_hash
            FROM core_identities i LEFT JOIN core_credentials c ON c.subject_id = i.subject_id
            WHERE i.username_normalized = $normalized;
            """;
        command.Parameters.AddWithValue("$normalized", normalizedUsername);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return null;
        if (reader.IsDBNull(7) || reader.IsDBNull(8) || reader.IsDBNull(9) || reader.IsDBNull(10))
            return new StoredCoreIdentity(ParseIdentity(reader), null, null, null, null);
        return new StoredCoreIdentity(ParseIdentity(reader), reader.GetString(10), reader.GetString(7), reader.GetString(8), reader.GetInt32(9));
    }

    public async Task<CoreIdentity?> FindByIdAsync(Guid subjectId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT subject_id, username, username_normalized, subject_kind, status, created_utc, updated_utc FROM core_identities WHERE subject_id = $id;";
        command.Parameters.AddWithValue("$id", subjectId.ToString("N"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ParseIdentity(reader) : null;
    }

    public async Task ReplaceCredentialHashAsync(Guid subjectId, string oldHash, string newHash, int iterations, DateTimeOffset nowUtc, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE core_credentials SET hash_format = 'aspnet-identity-v3', algorithm = 'PBKDF2-HMAC-SHA512', iterations = $iterations, password_hash = $hash, updated_utc = $now WHERE subject_id = $id AND password_hash = $oldHash;";
        command.Parameters.AddWithValue("$iterations", iterations);
        command.Parameters.AddWithValue("$hash", newHash);
        command.Parameters.AddWithValue("$now", FormatUtc(nowUtc));
        command.Parameters.AddWithValue("$id", subjectId.ToString("N"));
        command.Parameters.AddWithValue("$oldHash", oldHash);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> SetAccountStatusAsync(Guid actorSubjectId, Guid subjectId, CoreAccountStatus status, DateTimeOffset nowUtc, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await BeginImmediateAsync(connection, cancellationToken).ConfigureAwait(false);
        if (!await IsAuthorizedAsync(connection, transaction, actorSubjectId, CoreIdentityRules.IdentityManagePermission, cancellationToken).ConfigureAwait(false))
            return false;
        var id = subjectId.ToString("N");
        if (status == CoreAccountStatus.Disabled && await IsSoleActiveAdministratorAsync(connection, transaction, id, cancellationToken).ConfigureAwait(false))
            return false;

        var now = FormatUtc(nowUtc);
        await using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = "UPDATE core_identities SET status = $status, updated_utc = $now WHERE subject_id = $id;";
            update.Parameters.AddWithValue("$status", status == CoreAccountStatus.Active ? "active" : "disabled");
            update.Parameters.AddWithValue("$now", now);
            update.Parameters.AddWithValue("$id", id);
            if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1) return false;
        }
        if (status == CoreAccountStatus.Disabled)
        {
            await using var revoke = connection.CreateCommand();
            revoke.Transaction = transaction;
            revoke.CommandText = "UPDATE core_sessions SET revoked_utc = COALESCE(revoked_utc, $now) WHERE subject_id = $id;";
            revoke.Parameters.AddWithValue("$now", now);
            revoke.Parameters.AddWithValue("$id", id);
            await revoke.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await InsertAuditAsync(connection, transaction, status == CoreAccountStatus.Disabled ? "identity.disabled" : "identity.enabled", now, actorSubjectId.ToString("N"), id, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async Task<bool> DeleteAccountAsync(Guid actorSubjectId, Guid subjectId, DateTimeOffset nowUtc, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await BeginImmediateAsync(connection, cancellationToken).ConfigureAwait(false);
        if (!await IsAuthorizedAsync(connection, transaction, actorSubjectId, CoreIdentityRules.IdentityManagePermission, cancellationToken).ConfigureAwait(false))
            return false;
        var id = subjectId.ToString("N");
        if (await IsSoleActiveAdministratorAsync(connection, transaction, id, cancellationToken).ConfigureAwait(false)) return false;
        var now = FormatUtc(nowUtc);
        await InsertAuditAsync(connection, transaction, "identity.deleted", now, actorSubjectId.ToString("N"), id, cancellationToken).ConfigureAwait(false);
        await using var delete = connection.CreateCommand();
        delete.Transaction = transaction;
        delete.CommandText = "DELETE FROM core_identities WHERE subject_id = $id;";
        delete.Parameters.AddWithValue("$id", id);
        if (await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1) return false;
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async Task<bool> IsAuthorizedAsync(Guid subjectId, string permissionId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await IsAuthorizedAsync(connection, null, subjectId, permissionId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> CreateRoleAsync(Guid actorSubjectId, string roleId, DateTimeOffset nowUtc, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await BeginImmediateAsync(connection, cancellationToken).ConfigureAwait(false);
        if (!await IsAuthorizedAsync(connection, transaction, actorSubjectId, CoreIdentityRules.AuthorizationManagePermission, cancellationToken).ConfigureAwait(false)) return false;
        try
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO core_roles(role_id, is_system, created_utc) VALUES ($id, 0, $now);";
            command.Parameters.AddWithValue("$id", roleId);
            command.Parameters.AddWithValue("$now", FormatUtc(nowUtc));
            if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1) return false;
            await InsertAuditAsync(connection, transaction, "role.created", FormatUtc(nowUtc), actorSubjectId.ToString("N"), null, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (SqliteException) { return false; }
    }

    public async Task<bool> RegisterPermissionAsync(Guid actorSubjectId, string permissionId, DateTimeOffset nowUtc, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await BeginImmediateAsync(connection, cancellationToken).ConfigureAwait(false);
        if (!await IsAuthorizedAsync(connection, transaction, actorSubjectId, CoreIdentityRules.AuthorizationManagePermission, cancellationToken).ConfigureAwait(false)) return false;
        try
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO core_permissions(permission_id, created_utc) VALUES ($id, $now);";
            command.Parameters.AddWithValue("$id", permissionId);
            command.Parameters.AddWithValue("$now", FormatUtc(nowUtc));
            if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1) return false;
            await InsertAuditAsync(connection, transaction, "permission.registered", FormatUtc(nowUtc), actorSubjectId.ToString("N"), null, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (SqliteException) { return false; }
    }

    public async Task<bool> GrantPermissionToRoleAsync(Guid actorSubjectId, string roleId, string permissionId, DateTimeOffset nowUtc, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await BeginImmediateAsync(connection, cancellationToken).ConfigureAwait(false);
        if (!await IsAuthorizedAsync(connection, transaction, actorSubjectId, CoreIdentityRules.AuthorizationManagePermission, cancellationToken).ConfigureAwait(false)) return false;
        try
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "INSERT OR IGNORE INTO core_role_permissions(role_id, permission_id, granted_utc) VALUES ($role, $permission, $now);";
            command.Parameters.AddWithValue("$role", roleId);
            command.Parameters.AddWithValue("$permission", permissionId);
            command.Parameters.AddWithValue("$now", FormatUtc(nowUtc));
            var exists = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
            if (exists) await InsertAuditAsync(connection, transaction, "role.permission.granted", FormatUtc(nowUtc), actorSubjectId.ToString("N"), null, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return exists;
        }
        catch (SqliteException) { return false; }
    }

    public async Task<bool> AssignRoleAsync(Guid actorSubjectId, Guid subjectId, string roleId, DateTimeOffset nowUtc, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await BeginImmediateAsync(connection, cancellationToken).ConfigureAwait(false);
        if (!await IsAuthorizedAsync(connection, transaction, actorSubjectId, CoreIdentityRules.AuthorizationManagePermission, cancellationToken).ConfigureAwait(false)) return false;
        try
        {
            await AssignRoleWithinTransactionAsync(connection, transaction, subjectId.ToString("N"), roleId, FormatUtc(nowUtc), cancellationToken).ConfigureAwait(false);
            await InsertAuditAsync(connection, transaction, "role.assigned", FormatUtc(nowUtc), actorSubjectId.ToString("N"), subjectId.ToString("N"), cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (SqliteException) { return false; }
    }

    public async Task<CoreSession?> CreateSessionAsync(Guid subjectId, DateTimeOffset nowUtc, TimeSpan lifetime, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await BeginImmediateAsync(connection, cancellationToken).ConfigureAwait(false);
        var subjectText = subjectId.ToString("N");
        await using (var exists = connection.CreateCommand())
        {
            exists.Transaction = transaction;
            exists.CommandText = "SELECT EXISTS(SELECT 1 FROM core_identities WHERE subject_id = $id AND status = 'active');";
            exists.Parameters.AddWithValue("$id", subjectText);
            if (Convert.ToInt32(await exists.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture) != 1) return null;
        }

        var tokenBytes = RandomNumberGenerator.GetBytes(32);
        var token = Convert.ToBase64String(tokenBytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var tokenHash = SHA256.HashData(tokenBytes);
        CryptographicOperations.ZeroMemory(tokenBytes);
        var sessionId = Guid.NewGuid().ToString("N");
        var created = FormatUtc(nowUtc);
        var expires = FormatUtc(nowUtc.Add(lifetime));
        await using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = "INSERT INTO core_sessions(session_id, subject_id, token_hash, created_utc, expires_utc, revoked_utc) VALUES ($session, $subject, $hash, $created, $expires, NULL);";
            insert.Parameters.AddWithValue("$session", sessionId);
            insert.Parameters.AddWithValue("$subject", subjectText);
            insert.Parameters.Add("$hash", SqliteType.Blob).Value = tokenHash;
            insert.Parameters.AddWithValue("$created", created);
            insert.Parameters.AddWithValue("$expires", expires);
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new CoreSession(token, subjectId, nowUtc, nowUtc.Add(lifetime));
    }

    public async Task<CoreSessionValidation> ValidateSessionAsync(byte[] tokenHash, DateTimeOffset nowUtc, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT s.subject_id, s.expires_utc
            FROM core_sessions s JOIN core_identities i ON i.subject_id = s.subject_id
            WHERE s.token_hash = $hash AND s.revoked_utc IS NULL AND s.expires_utc > $now AND i.status = 'active';
            """;
        command.Parameters.Add("$hash", SqliteType.Blob).Value = tokenHash;
        command.Parameters.AddWithValue("$now", FormatUtc(nowUtc));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return new CoreSessionValidation(false, null, null);
        return new CoreSessionValidation(true, Guid.ParseExact(reader.GetString(0), "N"), ParseUtc(reader.GetString(1)));
    }

    public async Task<bool> RevokeSessionAsync(Guid actorSubjectId, byte[] tokenHash, DateTimeOffset nowUtc, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await BeginImmediateAsync(connection, cancellationToken).ConfigureAwait(false);
        string? owner = null;
        await using (var lookup = connection.CreateCommand())
        {
            lookup.Transaction = transaction;
            lookup.CommandText = "SELECT subject_id FROM core_sessions WHERE token_hash = $hash AND revoked_utc IS NULL;";
            lookup.Parameters.Add("$hash", SqliteType.Blob).Value = tokenHash;
            owner = Convert.ToString(await lookup.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
        }
        if (owner is null) return false;
        if (!string.Equals(owner, actorSubjectId.ToString("N"), StringComparison.Ordinal) &&
            !await IsAuthorizedAsync(connection, transaction, actorSubjectId, CoreIdentityRules.SessionRevokePermission, cancellationToken).ConfigureAwait(false))
            return false;

        var now = FormatUtc(nowUtc);
        await using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = "UPDATE core_sessions SET revoked_utc = $now WHERE token_hash = $hash AND revoked_utc IS NULL;";
            update.Parameters.AddWithValue("$now", now);
            update.Parameters.Add("$hash", SqliteType.Blob).Value = tokenHash;
            if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1) return false;
        }
        await InsertAuditAsync(connection, transaction, "session.revoked", now, actorSubjectId.ToString("N"), owner, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async Task<int> InvalidateAllSessionsAsync(DateTimeOffset nowUtc, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await BeginImmediateAsync(connection, cancellationToken).ConfigureAwait(false);
        var now = FormatUtc(nowUtc);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE core_sessions SET revoked_utc = $now WHERE revoked_utc IS NULL;";
        command.Parameters.AddWithValue("$now", now);
        var count = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        if (count > 0) await InsertAuditAsync(connection, transaction, "sessions.invalidated.local-recovery", now, null, null, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return count;
    }

    public async Task RecordHttpSecurityEventAsync(string eventCode, Guid? subjectId, DateTimeOffset nowUtc, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await BeginImmediateAsync(connection, cancellationToken).ConfigureAwait(false);
        await InsertAuditAsync(connection, transaction, eventCode, FormatUtc(nowUtc), null,
            subjectId?.ToString("N"), cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<bool> IsBootstrapPendingAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT CASE WHEN
                i.bootstrap_completed_utc IS NULL
                AND b.completed_utc IS NULL
                AND NOT EXISTS (SELECT 1 FROM core_subject_roles sr WHERE sr.role_id = $adminRole)
                AND NOT EXISTS (SELECT 1 FROM core_security_audit a WHERE a.event_code = 'bootstrap.admin.completed')
            THEN 1 ELSE 0 END
            FROM core_instance i CROSS JOIN core_bootstrap_state b
            WHERE i.singleton_id = 1 AND b.singleton_id = 1;
            """;
        command.Parameters.AddWithValue("$adminRole", CoreIdentityRules.AdministratorRole);
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result is not null and not DBNull && Convert.ToInt32(result, CultureInfo.InvariantCulture) == 1;
    }

    private static async Task<bool> IsAuthorizedAsync(SqliteConnection connection, SqliteTransaction? transaction, Guid subjectId, string permissionId, CancellationToken cancellationToken)
    {
        if (!CoreIdentityRules.IsValidIdentifier(permissionId)) return false;
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT EXISTS(
                SELECT 1 FROM core_identities i
                JOIN core_subject_roles sr ON sr.subject_id = i.subject_id
                JOIN core_role_permissions rp ON rp.role_id = sr.role_id
                JOIN core_permissions p ON p.permission_id = rp.permission_id
                WHERE i.subject_id = $subject AND i.status = 'active' AND p.permission_id = $permission);
            """;
        command.Parameters.AddWithValue("$subject", subjectId.ToString("N"));
        command.Parameters.AddWithValue("$permission", permissionId);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture) == 1;
    }

    private static async Task<bool> IsSoleActiveAdministratorAsync(SqliteConnection connection, SqliteTransaction transaction, string subjectId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT EXISTS(
                SELECT 1 FROM core_identities i
                JOIN core_subject_roles sr ON sr.subject_id = i.subject_id
                WHERE i.subject_id = $subject AND i.status = 'active' AND sr.role_id = $admin)
            AND (
                SELECT COUNT(*) FROM core_identities i
                JOIN core_subject_roles sr ON sr.subject_id = i.subject_id
                WHERE i.status = 'active' AND sr.role_id = $admin) <= 1;
            """;
        command.Parameters.AddWithValue("$subject", subjectId);
        command.Parameters.AddWithValue("$admin", CoreIdentityRules.AdministratorRole);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture) == 1;
    }

    private static async Task AssignRoleWithinTransactionAsync(SqliteConnection connection, SqliteTransaction transaction, string subjectId, string roleId, string now, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO core_subject_roles(subject_id, role_id, assigned_utc) VALUES ($subject, $role, $now);";
        command.Parameters.AddWithValue("$subject", subjectId);
        command.Parameters.AddWithValue("$role", roleId);
        command.Parameters.AddWithValue("$now", now);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task InsertCredentialAsync(SqliteConnection connection, SqliteTransaction transaction, string subjectId, string passwordHash, int iterations, string now, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO core_credentials(subject_id, hash_format, algorithm, iterations, password_hash, updated_utc) VALUES ($id, 'aspnet-identity-v3', 'PBKDF2-HMAC-SHA512', $iterations, $hash, $now);";
        command.Parameters.AddWithValue("$id", subjectId);
        command.Parameters.AddWithValue("$iterations", iterations);
        command.Parameters.AddWithValue("$hash", passwordHash);
        command.Parameters.AddWithValue("$now", now);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task InsertAuditAsync(SqliteConnection connection, SqliteTransaction transaction, string eventCode, string now, string? actorSubjectId, string? subjectId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO core_security_audit(event_code, occurred_utc, actor_subject_id, subject_id) VALUES ($event, $now, $actor, $subject);";
        command.Parameters.AddWithValue("$event", eventCode);
        command.Parameters.AddWithValue("$now", now);
        command.Parameters.AddWithValue("$actor", (object?)actorSubjectId ?? DBNull.Value);
        command.Parameters.AddWithValue("$subject", (object?)subjectId ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var pragmas = connection.CreateCommand();
            pragmas.CommandText = "PRAGMA foreign_keys=ON; PRAGMA synchronous=FULL; PRAGMA busy_timeout=5000;";
            await pragmas.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static async Task<SqliteTransaction> BeginImmediateAsync(SqliteConnection connection, CancellationToken cancellationToken) =>
        await Task.Run(() => connection.BeginTransaction(deferred: false), cancellationToken).ConfigureAwait(false);

    private static CoreIdentity ParseIdentity(SqliteDataReader reader) => new(
        Guid.ParseExact(reader.GetString(0), "N"),
        reader.GetString(1),
        reader.GetString(2),
        reader.GetString(3),
        reader.GetString(4) == "active" ? CoreAccountStatus.Active : CoreAccountStatus.Disabled,
        ParseUtc(reader.GetString(5)),
        ParseUtc(reader.GetString(6)));

    internal static string FormatUtc(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    private static DateTimeOffset ParseUtc(string value) => DateTimeOffset.ParseExact(value, "O", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}

internal sealed record StoredCoreIdentity(CoreIdentity Identity, string? PasswordHash, string? HashFormat, string? Algorithm, int? Iterations);
