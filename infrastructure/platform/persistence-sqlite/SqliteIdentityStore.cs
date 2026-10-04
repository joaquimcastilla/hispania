using System.Globalization;
using Hispania.Identity.Application;
using Hispania.Platform.Contracts.Identity;
using Microsoft.Data.Sqlite;

namespace Hispania.Platform.Persistence.Sqlite;

public sealed class SqliteIdentityStore : IIdentityStore
{
    private readonly string _connectionString;

    public SqliteIdentityStore(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate, ForeignKeys = true }.ToString();
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS identity_users(user_id TEXT NOT NULL PRIMARY KEY, username TEXT NOT NULL COLLATE BINARY UNIQUE, display_name TEXT NOT NULL, status INTEGER NOT NULL, created_utc TEXT NOT NULL, updated_utc TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS ix_identity_users_username ON identity_users(username);
            CREATE TABLE IF NOT EXISTS identity_credentials(user_id TEXT NOT NULL PRIMARY KEY REFERENCES identity_users(user_id) ON DELETE CASCADE, algorithm TEXT NOT NULL, iterations INTEGER NOT NULL, salt TEXT NOT NULL, password_hash TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS identity_sessions(session_id TEXT NOT NULL PRIMARY KEY, user_id TEXT NOT NULL REFERENCES identity_users(user_id) ON DELETE CASCADE, token_hash TEXT NOT NULL UNIQUE, created_utc TEXT NOT NULL, expires_utc TEXT NOT NULL, revoked_utc TEXT NULL);
            CREATE INDEX IF NOT EXISTS ix_identity_sessions_active_user ON identity_sessions(user_id, expires_utc, revoked_utc);
            CREATE INDEX IF NOT EXISTS ix_identity_sessions_token_hash ON identity_sessions(token_hash);
            """;
        command.ExecuteNonQuery();
    }

    public async ValueTask<User?> GetUserAsync(UserId id, CancellationToken cancellationToken) =>
        await WithConnection(async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT username,display_name,status,created_utc,updated_utc FROM identity_users WHERE user_id=$id";
            command.Parameters.AddWithValue("$id", id.Value.ToString("D"));
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadUser(id, reader) : null;
        }, cancellationToken).ConfigureAwait(false);

    public async ValueTask<User?> GetUserByUsernameAsync(string normalizedUsername, CancellationToken cancellationToken) =>
        await WithConnection(async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT user_id,username,display_name,status,created_utc,updated_utc FROM identity_users WHERE username=$username COLLATE BINARY";
            command.Parameters.AddWithValue("$username", normalizedUsername);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadUser(new(Guid.Parse(reader.GetString(0))), reader, 1) : null;
        }, cancellationToken).ConfigureAwait(false);

    public async ValueTask<IReadOnlyList<User>> ListUsersAsync(CancellationToken cancellationToken) =>
        await WithConnection(async connection =>
        {
            var users = new List<User>();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT user_id,username,display_name,status,created_utc,updated_utc FROM identity_users ORDER BY username";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) users.Add(ReadUser(new(Guid.Parse(reader.GetString(0))), reader, 1));
            return (IReadOnlyList<User>)users;
        }, cancellationToken).ConfigureAwait(false);

    public async ValueTask<bool> CreateUserAsync(User user, StoredCredential credential, CancellationToken cancellationToken)
    {
        try
        {
            await WithConnection(async connection =>
            {
                await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
                await using (var command = connection.CreateCommand())
                {
                    command.Transaction = (SqliteTransaction)transaction;
                    command.CommandText = "INSERT INTO identity_users VALUES($id,$username,$display,$status,$created,$updated)";
                    command.Parameters.AddWithValue("$id", user.Id.Value.ToString("D")); command.Parameters.AddWithValue("$username", user.Username); command.Parameters.AddWithValue("$display", user.DisplayName); command.Parameters.AddWithValue("$status", (int)user.Status); command.Parameters.AddWithValue("$created", Fmt(user.CreatedAt)); command.Parameters.AddWithValue("$updated", Fmt(user.UpdatedAt));
                    await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }
                await using (var command = connection.CreateCommand())
                {
                    command.Transaction = (SqliteTransaction)transaction;
                    command.CommandText = "INSERT INTO identity_credentials VALUES($id,$algorithm,$iterations,$salt,$hash)";
                    command.Parameters.AddWithValue("$id", user.Id.Value.ToString("D")); command.Parameters.AddWithValue("$algorithm", credential.Algorithm); command.Parameters.AddWithValue("$iterations", credential.Iterations); command.Parameters.AddWithValue("$salt", credential.Salt); command.Parameters.AddWithValue("$hash", credential.Hash);
                    await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return true;
            }, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19) { return false; }
    }

    public async ValueTask<bool> SetUserAsync(User user, CancellationToken cancellationToken) => await WithConnection(async connection =>
    {
        await using var command = connection.CreateCommand(); command.CommandText = "UPDATE identity_users SET display_name=$display,status=$status,updated_utc=$updated WHERE user_id=$id";
        command.Parameters.AddWithValue("$display", user.DisplayName); command.Parameters.AddWithValue("$status", (int)user.Status); command.Parameters.AddWithValue("$updated", Fmt(user.UpdatedAt)); command.Parameters.AddWithValue("$id", user.Id.Value.ToString("D"));
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }, cancellationToken).ConfigureAwait(false);

    public async ValueTask<StoredCredential?> GetCredentialAsync(UserId userId, CancellationToken cancellationToken) => await WithConnection(async connection =>
    {
        await using var command = connection.CreateCommand(); command.CommandText = "SELECT algorithm,iterations,salt,password_hash FROM identity_credentials WHERE user_id=$id"; command.Parameters.AddWithValue("$id", userId.Value.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? new StoredCredential(reader.GetString(0), reader.GetInt32(1), reader.GetString(2), reader.GetString(3)) : null;
    }, cancellationToken).ConfigureAwait(false);

    public async ValueTask SetCredentialAsync(UserId userId, StoredCredential credential, CancellationToken cancellationToken) => await WithConnection(async connection =>
    {
        await using var command = connection.CreateCommand(); command.CommandText = "UPDATE identity_credentials SET algorithm=$algorithm,iterations=$iterations,salt=$salt,password_hash=$hash WHERE user_id=$id";
        command.Parameters.AddWithValue("$algorithm", credential.Algorithm); command.Parameters.AddWithValue("$iterations", credential.Iterations); command.Parameters.AddWithValue("$salt", credential.Salt); command.Parameters.AddWithValue("$hash", credential.Hash); command.Parameters.AddWithValue("$id", userId.Value.ToString("D"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }, cancellationToken).ConfigureAwait(false);

    public async ValueTask<StoredSession?> GetSessionByTokenHashAsync(string tokenHash, CancellationToken cancellationToken) => await GetSessionAsync("token_hash", tokenHash, cancellationToken).ConfigureAwait(false);
    public async ValueTask<StoredSession?> GetSessionAsync(SessionId sessionId, CancellationToken cancellationToken) => await GetSessionAsync("session_id", sessionId.Value.ToString("D"), cancellationToken).ConfigureAwait(false);
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "This lookup delegates to the connection helper owned by the store instance.")]
    private async ValueTask<StoredSession?> GetSessionAsync(string column, string value, CancellationToken cancellationToken) => await WithConnection<StoredSession?>(async connection =>
    {
        await using var command = connection.CreateCommand(); command.CommandText = $"SELECT session_id,user_id,created_utc,expires_utc,revoked_utc,token_hash FROM identity_sessions WHERE {column}=$value"; command.Parameters.AddWithValue("$value", value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return null;
        var session = new Session(new(Guid.Parse(reader.GetString(0))), new(Guid.Parse(reader.GetString(1))), Parse(reader.GetString(2)), Parse(reader.GetString(3)), reader.IsDBNull(4) ? null : Parse(reader.GetString(4)));
        return new(session, reader.GetString(5));
    }, cancellationToken).ConfigureAwait(false);

    public async ValueTask CreateSessionAsync(StoredSession session, CancellationToken cancellationToken) => await WithConnection(async connection =>
    {
        await using var command = connection.CreateCommand(); command.CommandText = "INSERT INTO identity_sessions VALUES($id,$user,$hash,$created,$expires,NULL)";
        command.Parameters.AddWithValue("$id", session.Session.Id.Value.ToString("D")); command.Parameters.AddWithValue("$user", session.Session.UserId.Value.ToString("D")); command.Parameters.AddWithValue("$hash", session.TokenHash); command.Parameters.AddWithValue("$created", Fmt(session.Session.CreatedAt)); command.Parameters.AddWithValue("$expires", Fmt(session.Session.ExpiresAt));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false); return true;
    }, cancellationToken).ConfigureAwait(false);

    public async ValueTask<bool> RevokeSessionAsync(SessionId sessionId, DateTimeOffset revokedAt, CancellationToken cancellationToken) => await WithConnection(async connection =>
    {
        await using var command = connection.CreateCommand(); command.CommandText = "UPDATE identity_sessions SET revoked_utc=$now WHERE session_id=$id AND revoked_utc IS NULL"; command.Parameters.AddWithValue("$now", Fmt(revokedAt)); command.Parameters.AddWithValue("$id", sessionId.Value.ToString("D"));
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }, cancellationToken).ConfigureAwait(false);

    public async ValueTask<long> CountActiveSessionsAsync(CancellationToken cancellationToken = default) => await WithConnection(async connection =>
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM identity_sessions s JOIN identity_users u ON s.user_id=u.user_id WHERE s.revoked_utc IS NULL AND s.expires_utc > $now AND u.status=0";
        command.Parameters.AddWithValue("$now", Fmt(DateTimeOffset.UtcNow));
        return (long)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) ?? 0L);
    }, cancellationToken).ConfigureAwait(false);

    private async Task<T> WithConnection<T>(Func<SqliteConnection, Task<T>> action, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(_connectionString); await connection.OpenAsync(cancellationToken).ConfigureAwait(false); return await action(connection).ConfigureAwait(false);
    }
    private static User ReadUser(UserId id, SqliteDataReader reader, int offset = 0) => new(id, reader.GetString(offset), reader.GetString(offset + 1), (UserStatus)reader.GetInt32(offset + 2), Parse(reader.GetString(offset + 3)), Parse(reader.GetString(offset + 4)));
    private static string Fmt(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    private static DateTimeOffset Parse(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture);
}
