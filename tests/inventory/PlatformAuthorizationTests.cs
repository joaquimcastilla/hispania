using Hispania.Authorization.Application;
using Hispania.Identity.Application;
using Hispania.Platform.Contracts.Audit;
using Hispania.Platform.Contracts.Authorization;
using Hispania.Platform.Contracts.Identity;
using Hispania.Platform.Persistence.Sqlite;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Hispania.Inventory.Tests;

public sealed class PlatformAuthorizationTests
{
    private static readonly string[] BootstrapColumns = ["id", "consumed"];
    [Fact]
    public async Task AdministratorBootstrapRequiresConfiguredCorrectTokenIsAtomicOneTimeAndNeverPersistsSecret()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "identity.sqlite");
        var identity = new IdentityService(new SqliteIdentityStore(path));
        var created = await identity.CreateUserAsync("bootstrapuser", "Bootstrap User", "a sufficiently long passphrase");
        var store = new SqlitePlatformAuthorizationStore(path);
        var audit = new RecordingAuditWriter();
        var secret = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));

        Assert.False(await new AuthorizationAdministrationService(store, identity).BootstrapAdministratorAsync(created.Value!.Id, secret));
        Assert.False(await new AuthorizationAdministrationService(store, identity, configuredBootstrapToken: "").BootstrapAdministratorAsync(created.Value.Id, secret));
        Assert.False(await new AuthorizationAdministrationService(store, identity, configuredBootstrapToken: secret).BootstrapAdministratorAsync(created.Value.Id, "incorrect-token-value-000000000000"));

        var service = new AuthorizationAdministrationService(store, identity, audit, secret);
        var attempts = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => service.BootstrapAdministratorAsync(created.Value.Id, secret).AsTask()));
        Assert.Single(attempts, result => result);
        Assert.Single(await service.GetUserRolesAsync(created.Value.Id));
        Assert.Single(audit.Records, record => record.Action == "bootstrap-admin-created");
        Assert.DoesNotContain(secret, System.Text.Json.JsonSerializer.Serialize(audit.Records), StringComparison.Ordinal);

        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString());
        await connection.OpenAsync();
        await using var columns = connection.CreateCommand(); columns.CommandText = "PRAGMA table_info(authorization_bootstrap)";
        await using var reader = await columns.ExecuteReaderAsync();
        var names = new List<string>(); while (await reader.ReadAsync()) names.Add(reader.GetString(1));
        Assert.Equal(BootstrapColumns, names);
    }

    [Fact]
    public async Task RolesPermissionsAssignmentsAndEffectiveAccessPersistAndChangeForExistingSession()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "identity.sqlite");
        var identityStore = new SqliteIdentityStore(path);
        var identity = new IdentityService(identityStore);
        var created = await identity.CreateUserAsync("rbacuser", "RBAC User", "a sufficiently long passphrase");
        Assert.True(created.Succeeded);
        var session = await identity.LoginAsync("rbacuser", "a sufficiently long passphrase");
        Assert.True(session.Succeeded);

        var authStore = new SqlitePlatformAuthorizationStore(path);
        var administration = new AuthorizationAdministrationService(authStore, identity);
        var authorization = new AuthorizationService(authStore, identity);
        var roleResult = await administration.CreateRoleAsync("Media Readers", "Read access");
        Assert.True(roleResult.Succeeded);
        var role = Assert.IsType<Role>(roleResult.Value);
        Assert.Equal(AuthorizationFailure.DuplicateRoleName, (await administration.CreateRoleAsync("media-readers", "Duplicate")).Failure);
        Assert.True((await administration.AddPermissionAsync(role.Id, "platform.users.read")).Succeeded);
        Assert.True((await administration.AssignRoleAsync(created.Value!.Id, role.Id)).Succeeded);
        Assert.False((await administration.AssignRoleAsync(created.Value.Id, role.Id)).Value);
        Assert.True(await authorization.HasPermissionAsync(created.Value.Id, "platform.users.read"));
        Assert.False(await authorization.HasPermissionAsync(created.Value.Id, "platform.audit.read"));
        Assert.NotNull(await identity.ValidateSessionAsync(session.SessionToken!));

        var reopenedStore = new SqlitePlatformAuthorizationStore(path);
        var reopenedAdmin = new AuthorizationAdministrationService(reopenedStore, identity);
        var reopenedAuthorization = new AuthorizationService(reopenedStore, identity);
        Assert.Single(await reopenedAdmin.GetUserRolesAsync(created.Value.Id));
        Assert.Contains("platform.users.read", await reopenedAdmin.GetRolePermissionsAsync(role.Id));
        Assert.True(await reopenedAuthorization.HasPermissionAsync(created.Value.Id, "platform.users.read"));

        Assert.True((await reopenedAdmin.RemovePermissionAsync(role.Id, "platform.users.read")).Succeeded);
        Assert.NotNull(await identity.ValidateSessionAsync(session.SessionToken!));
        Assert.False(await reopenedAuthorization.HasPermissionAsync(created.Value.Id, "platform.users.read"));
        Assert.Empty(await reopenedAuthorization.GetEffectivePermissionsAsync(created.Value.Id));
        Assert.True((await identity.SetUserStatusAsync(created.Value.Id, UserStatus.Disabled)).Succeeded);
        Assert.False(await reopenedAuthorization.HasPermissionAsync(created.Value.Id, "platform.audit.read"));
        Assert.False((await reopenedAuthorization.RequirePermissionAsync(created.Value.Id, "platform.audit.read")).Authenticated);
        Assert.True((await reopenedAdmin.RemoveRoleAsync(created.Value.Id, role.Id)).Succeeded);
        Assert.Empty(await reopenedAdmin.GetUserRolesAsync(created.Value.Id));
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "hispania-authorization-" + Guid.NewGuid().ToString("N"));
        public TemporaryDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() { SqliteConnection.ClearAllPools(); if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true); }
    }

    private sealed class RecordingAuditWriter : IPlatformAuditWriter
    {
        public List<AuditRecord> Records { get; } = [];
        public ValueTask WriteAsync(AuditRecord record, CancellationToken cancellationToken = default) { Records.Add(record); return ValueTask.CompletedTask; }
    }
}
