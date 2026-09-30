using Hispania.Authorization.Application;
using Hispania.Identity.Application;
using Hispania.Platform.Contracts.Authorization;
using Hispania.Platform.Contracts.Identity;
using Hispania.Platform.Persistence.Sqlite;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Hispania.Inventory.Tests;

public sealed class PlatformAuthorizationTests
{
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
}
