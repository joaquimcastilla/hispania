using System.Globalization;
using Hispania.Authorization.Application;
using Hispania.Platform.Contracts.Authorization;
using Hispania.Platform.Contracts.Identity;
using Microsoft.Data.Sqlite;

namespace Hispania.Platform.Persistence.Sqlite;

public sealed class SqlitePlatformAuthorizationStore : IPlatformAuthorizationStore
{
    private readonly string connectionString;
    public SqlitePlatformAuthorizationStore(string path)
    {
        connectionString = new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate, ForeignKeys = true }.ToString();
        using var connection = new SqliteConnection(connectionString); connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS authorization_roles(role_id TEXT NOT NULL PRIMARY KEY,name TEXT NOT NULL COLLATE BINARY UNIQUE,description TEXT NOT NULL,created_utc TEXT NOT NULL,updated_utc TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS ix_authorization_roles_name ON authorization_roles(name);
            CREATE TABLE IF NOT EXISTS authorization_role_permissions(role_id TEXT NOT NULL REFERENCES authorization_roles(role_id) ON DELETE CASCADE,permission_id TEXT NOT NULL,created_utc TEXT NOT NULL,PRIMARY KEY(role_id,permission_id));
            CREATE TABLE IF NOT EXISTS authorization_user_roles(user_id TEXT NOT NULL REFERENCES identity_users(user_id) ON DELETE CASCADE,role_id TEXT NOT NULL REFERENCES authorization_roles(role_id) ON DELETE CASCADE,assigned_utc TEXT NOT NULL,PRIMARY KEY(user_id,role_id));
            CREATE INDEX IF NOT EXISTS ix_authorization_user_roles_role ON authorization_user_roles(role_id,user_id);
            CREATE TABLE IF NOT EXISTS authorization_bootstrap(id INTEGER NOT NULL PRIMARY KEY CHECK(id=1),consumed INTEGER NOT NULL);
            INSERT OR IGNORE INTO authorization_bootstrap(id,consumed) VALUES(1,0);
            """;
        command.ExecuteNonQuery();
    }

    public async ValueTask<Role?> GetRoleAsync(RoleId roleId, CancellationToken cancellationToken) => await QueryRoleAsync("role_id", roleId.Value.ToString("D"), cancellationToken).ConfigureAwait(false);
    public async ValueTask<Role?> GetRoleByNameAsync(string normalizedName, CancellationToken cancellationToken) => await QueryRoleAsync("name", normalizedName, cancellationToken).ConfigureAwait(false);
    private async ValueTask<Role?> QueryRoleAsync(string column, string value, CancellationToken cancellationToken) => await WithConnection<Role?>(async c =>
    {
        await using var q = c.CreateCommand(); q.CommandText = $"SELECT role_id,name,description,created_utc,updated_utc FROM authorization_roles WHERE {column}=$value COLLATE BINARY"; q.Parameters.AddWithValue("$value", value);
        await using var r = await q.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false); return await r.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadRole(r) : null;
    }, cancellationToken).ConfigureAwait(false);

    public async ValueTask<IReadOnlyList<Role>> ListRolesAsync(CancellationToken cancellationToken) => await WithConnection<IReadOnlyList<Role>>(async c =>
    {
        var roles = new List<Role>(); await using var q = c.CreateCommand(); q.CommandText = "SELECT role_id,name,description,created_utc,updated_utc FROM authorization_roles ORDER BY name";
        await using var r = await q.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false); while (await r.ReadAsync(cancellationToken).ConfigureAwait(false)) roles.Add(ReadRole(r)); return roles;
    }, cancellationToken).ConfigureAwait(false);

    public async ValueTask<bool> CreateRoleAsync(Role role, CancellationToken cancellationToken)
    {
        try { return await WithConnection(async c => { await using var q = c.CreateCommand(); q.CommandText = "INSERT INTO authorization_roles VALUES($id,$name,$description,$created,$updated)"; BindRole(q, role); return await q.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1; }, cancellationToken).ConfigureAwait(false); }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19) { return false; }
    }
    public async ValueTask<bool> UpdateRoleAsync(Role role, CancellationToken cancellationToken)
    {
        try { return await WithConnection(async c => { await using var q = c.CreateCommand(); q.CommandText = "UPDATE authorization_roles SET name=$name,description=$description,updated_utc=$updated WHERE role_id=$id"; q.Parameters.AddWithValue("$name", role.Name); q.Parameters.AddWithValue("$description", role.Description); q.Parameters.AddWithValue("$updated", Fmt(role.UpdatedAt)); q.Parameters.AddWithValue("$id", role.Id.Value.ToString("D")); return await q.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1; }, cancellationToken).ConfigureAwait(false); }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19) { return false; }
    }

    public async ValueTask<IReadOnlyList<string>> GetRolePermissionsAsync(RoleId roleId, CancellationToken cancellationToken) => await WithConnection<IReadOnlyList<string>>(async c =>
    {
        var permissions = new List<string>(); await using var q = c.CreateCommand(); q.CommandText = "SELECT permission_id FROM authorization_role_permissions WHERE role_id=$role ORDER BY permission_id"; q.Parameters.AddWithValue("$role", roleId.Value.ToString("D"));
        await using var r = await q.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false); while (await r.ReadAsync(cancellationToken).ConfigureAwait(false)) permissions.Add(r.GetString(0)); return permissions;
    }, cancellationToken).ConfigureAwait(false);

    public async ValueTask<bool> AddPermissionAsync(RoleId roleId, string permissionId, CancellationToken cancellationToken) => await WithConnection(async c =>
    {
        await using var q = c.CreateCommand(); q.CommandText = "INSERT OR IGNORE INTO authorization_role_permissions VALUES($role,$permission,$created)"; q.Parameters.AddWithValue("$role", roleId.Value.ToString("D")); q.Parameters.AddWithValue("$permission", permissionId); q.Parameters.AddWithValue("$created", Fmt(DateTimeOffset.UtcNow)); return await q.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }, cancellationToken).ConfigureAwait(false);

    public async ValueTask<bool> RemovePermissionAsync(RoleId roleId, string permissionId, CancellationToken cancellationToken) => await WithConnection(async c =>
    {
        await using var q = c.CreateCommand(); q.CommandText = "DELETE FROM authorization_role_permissions WHERE role_id=$role AND permission_id=$permission"; q.Parameters.AddWithValue("$role", roleId.Value.ToString("D")); q.Parameters.AddWithValue("$permission", permissionId); return await q.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }, cancellationToken).ConfigureAwait(false);

    public async ValueTask<IReadOnlyList<Role>> GetUserRolesAsync(UserId userId, CancellationToken cancellationToken) => await WithConnection<IReadOnlyList<Role>>(async c =>
    {
        var roles = new List<Role>(); await using var q = c.CreateCommand(); q.CommandText = "SELECT r.role_id,r.name,r.description,r.created_utc,r.updated_utc FROM authorization_roles r JOIN authorization_user_roles ur ON ur.role_id=r.role_id WHERE ur.user_id=$user ORDER BY r.name"; q.Parameters.AddWithValue("$user", userId.Value.ToString("D"));
        await using var r = await q.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false); while (await r.ReadAsync(cancellationToken).ConfigureAwait(false)) roles.Add(ReadRole(r)); return roles;
    }, cancellationToken).ConfigureAwait(false);

    public async ValueTask<bool> AssignRoleAsync(UserId userId, RoleId roleId, CancellationToken cancellationToken)
    {
        try { return await WithConnection(async c => { await using var q = c.CreateCommand(); q.CommandText = "INSERT INTO authorization_user_roles VALUES($user,$role,$assigned)"; q.Parameters.AddWithValue("$user", userId.Value.ToString("D")); q.Parameters.AddWithValue("$role", roleId.Value.ToString("D")); q.Parameters.AddWithValue("$assigned", Fmt(DateTimeOffset.UtcNow)); return await q.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1; }, cancellationToken).ConfigureAwait(false); }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19) { return false; }
    }
    public async ValueTask<bool> RemoveRoleAsync(UserId userId, RoleId roleId, CancellationToken cancellationToken) => await WithConnection(async c =>
    {
        await using var q = c.CreateCommand(); q.CommandText = "DELETE FROM authorization_user_roles WHERE user_id=$user AND role_id=$role"; q.Parameters.AddWithValue("$user", userId.Value.ToString("D")); q.Parameters.AddWithValue("$role", roleId.Value.ToString("D")); return await q.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }, cancellationToken).ConfigureAwait(false);

    public async ValueTask<bool> BootstrapAdministratorAsync(UserId userId, IReadOnlyList<string> permissions, CancellationToken cancellationToken)
    {
        try
        {
            return await WithConnection(async c =>
            {
                await using var tx = await c.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
                await using (var q = c.CreateCommand())
                {
                    q.Transaction = (SqliteTransaction)tx; q.CommandText = "UPDATE authorization_bootstrap SET consumed=1 WHERE id=1 AND consumed=0";
                    if (await q.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1) return false;
                }
                var now = DateTimeOffset.UtcNow; var roleId = RoleId.New(); var role = new Role(roleId, "administrator", "Platform administrator", now, now);
                await using (var q = c.CreateCommand()) { q.Transaction = (SqliteTransaction)tx; q.CommandText = "INSERT INTO authorization_roles VALUES($id,$name,$description,$created,$updated)"; BindRole(q, role); await q.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false); }
                foreach (var permission in permissions)
                {
                    await using var q = c.CreateCommand(); q.Transaction = (SqliteTransaction)tx; q.CommandText = "INSERT INTO authorization_role_permissions VALUES($role,$permission,$created)"; q.Parameters.AddWithValue("$role", roleId.Value.ToString("D")); q.Parameters.AddWithValue("$permission", permission); q.Parameters.AddWithValue("$created", Fmt(now)); await q.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }
                await using (var q = c.CreateCommand()) { q.Transaction = (SqliteTransaction)tx; q.CommandText = "INSERT INTO authorization_user_roles VALUES($user,$role,$assigned)"; q.Parameters.AddWithValue("$user", userId.Value.ToString("D")); q.Parameters.AddWithValue("$role", roleId.Value.ToString("D")); q.Parameters.AddWithValue("$assigned", Fmt(now)); await q.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false); }
                await tx.CommitAsync(cancellationToken).ConfigureAwait(false); return true;
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19) { return false; }
    }

    private async Task<T> WithConnection<T>(Func<SqliteConnection, Task<T>> callback, CancellationToken cancellationToken)
    { await using var c = new SqliteConnection(connectionString); await c.OpenAsync(cancellationToken).ConfigureAwait(false); return await callback(c).ConfigureAwait(false); }
    private static Role ReadRole(SqliteDataReader r) => new(new(Guid.Parse(r.GetString(0))), r.GetString(1), r.GetString(2), Parse(r.GetString(3)), Parse(r.GetString(4)));
    private static void BindRole(SqliteCommand q, Role role) { q.Parameters.AddWithValue("$id", role.Id.Value.ToString("D")); q.Parameters.AddWithValue("$name", role.Name); q.Parameters.AddWithValue("$description", role.Description); q.Parameters.AddWithValue("$created", Fmt(role.CreatedAt)); q.Parameters.AddWithValue("$updated", Fmt(role.UpdatedAt)); }
    private static string Fmt(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    private static DateTimeOffset Parse(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture);
}
