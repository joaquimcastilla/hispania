using Hispania.Platform.Contracts.Identity;
using Hispania.Platform.Contracts.Authorization;

namespace Hispania.Compatibility.Consumer07;

// Frozen test consumer compiled against contracts extracted from the validated SHA.
public static class Consumer
{
    private static void Require(bool condition, string outcome)
    {
        if (!condition) throw new InvalidOperationException("0.7 expectation failed: " + outcome);
    }

    public static async Task ExerciseAsync(IIdentityQueryV1 query, IIdentityCommandV1 command,
        IAuthorizationServiceV1 authorization, IAuthorizationAdministrationV1 administration)
    {
        const string password = "TEST-ONLY-baseline-07-password";
        var created = await command.CreateUserAsync("baseline07", "Baseline test", password);
        Require(created.Succeeded && created.Value is not null, "create");
        var user = created.Value!;
        Require(user.Status == UserStatus.Enabled, "enabled");
        Require((await query.GetUserByUsernameAsync("BASELINE07"))?.Id == user.Id, "query normalization");
        Require((await query.GetUserAsync(user.Id))?.DisplayName == "Baseline test", "query by ID");
        Require((await query.ListUsersAsync()).Any(value => value.Id == user.Id), "list");
        Require((await command.CreateUserAsync("BASELINE07", "Duplicate", password)).Failure == IdentityFailure.DuplicateUsername, "duplicate outcome");
        Require(!(await command.LoginAsync("missing07", password)).Succeeded, "unknown login");
        Require(!(await command.LoginAsync("baseline07", "TEST-ONLY-wrong-password")).Succeeded, "bad credentials");
        Require(!(await authorization.RequirePermissionAsync(null, PlatformPermissionIds.UsersRead)).Authenticated, "anonymous");
        var denied = await authorization.RequirePermissionAsync(user.Id, PlatformPermissionIds.UsersRead);
        Require(denied.Authenticated && !denied.Allowed, "default deny");
        var role = await administration.CreateRoleAsync("Baseline Readers", "TEST ONLY");
        Require(role.Succeeded && role.Value is not null, "role");
        Require((await administration.AddPermissionAsync(role.Value!.Id, PlatformPermissionIds.UsersRead)).Succeeded, "grant");
        Require((await administration.AssignRoleAsync(user.Id, role.Value.Id)).Succeeded, "assignment");
        Require((await administration.GetUserRolesAsync(user.Id)).Any(value => value.Id == role.Value.Id), "role representation");
        Require((await authorization.GetEffectivePermissionsAsync(user.Id)).Contains("platform.users.read"), "effective permissions");
        Require(await authorization.HasPermissionAsync(user.Id, "platform.users.read"), "allow");
        var login = await command.LoginAsync("baseline07", password);
        Require(login.Succeeded && login.Session is not null && login.SessionToken is not null, "login");
        Require(login.Session!.UserId == user.Id && login.Session.RevokedAt is null, "session shape");
        Require((await query.ValidateSessionAsync(login.SessionToken!))?.Id == login.Session.Id, "validate");
        Require(await command.LogoutAsync(login.SessionToken!), "revoke");
        Require(await query.ValidateSessionAsync(login.SessionToken!) is null, "revoked invalid");
        var second = await command.LoginAsync("baseline07", password);
        Require((await command.SetUserStatusAsync(user.Id, UserStatus.Disabled)).Succeeded, "disable");
        Require(!(await command.LoginAsync("baseline07", password)).Succeeded, "disabled login");
        Require(await query.GetCurrentUserAsync(second.SessionToken!) is null, "disabled session");
        Require(!(await authorization.GetEffectivePermissionsAsync(user.Id)).Any(), "disabled permissions");
        Require((await command.SetUserStatusAsync(user.Id, UserStatus.Enabled)).Succeeded, "reenable");
        Require((await administration.RemoveRoleAsync(user.Id, role.Value.Id)).Succeeded, "remove assignment");
        Require(!await authorization.HasPermissionAsync(user.Id, "platform.users.read"), "deny after removal");
        Require(!await administration.BootstrapAdministratorAsync(user.Id, "TEST-ONLY-incorrect-bootstrap"), "bootstrap wrong");
        Require(await administration.BootstrapAdministratorAsync(user.Id, "TEST-ONLY-baseline-07-bootstrap-token"), "bootstrap success");
        Require(!await administration.BootstrapAdministratorAsync(user.Id, "TEST-ONLY-baseline-07-bootstrap-token"), "bootstrap once");
        Require(await authorization.HasPermissionAsync(user.Id, PlatformPermissionIds.Admin), "bootstrap admin");
    }
}
