using Hispania.Platform.Contracts.Authorization;
using Hispania.Platform.Contracts.Identity;

namespace Hispania.Server;

public static class AuthorizationEndpoints
{
    public static IEndpointRouteBuilder MapAuthorizationEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/authorization").WithTags("Platform Authorization");

        group.MapPost("/bootstrap-admin", async (BootstrapAdministratorRequest request, HttpContext http, IAuthorizationAdministrationV1 admin, CancellationToken ct) =>
        {
            var header = http.Request.Headers.Authorization.ToString();
            if (!header.StartsWith("Bootstrap ", StringComparison.OrdinalIgnoreCase)) return Results.Unauthorized();
            var token = header[9..].Trim();
            var bootstrapped = await admin.BootstrapAdministratorAsync(new(request.UserId), token, ct);
            return bootstrapped ? Results.Ok(new { status = "bootstrap-complete" }) : Results.StatusCode(StatusCodes.Status403Forbidden);
        });

        group.MapGet("/users", async (IIdentityQueryV1 identity, IAuthorizationAdministrationV1 admin, CancellationToken ct) =>
        {
            var users = await identity.ListUsersAsync(ct);
            var result = new List<UserAdministrationView>(users.Count);
            foreach (var user in users) result.Add(new(user, await admin.GetUserRolesAsync(user.Id, ct)));
            return Results.Ok(result);
        }).AddEndpointFilter(AuthorizationEndpointFilters.RequirePermission(PlatformPermissionIds.UsersRead));

        group.MapPatch("/users/{userId:guid}/status", async (Guid userId, UserStatusRequest request, HttpContext http, IIdentityCommandV1 identity, IIdentityQueryV1 identityQuery, IAuthorizationAdministrationV1 admin, CancellationToken ct) =>
        {
            if (!request.Enabled)
            {
                var user = await identityQuery.GetUserAsync(new(userId), ct);
                if (user is not null && user.Status == UserStatus.Enabled && (await admin.GetUserRolesAsync(new(userId), ct)).Any(role => role.Name == "administrator") && !await admin.HasOtherEnabledAdministratorsAsync(new(userId), ct)) return Results.Conflict(new { error = "last_enabled_administrator" });
            }
            var result = await identity.SetUserStatusAsync(new(userId), request.Enabled ? UserStatus.Enabled : UserStatus.Disabled, Correlation(http), ct);
            return result.Succeeded ? Results.Ok(result.Value) : Results.NotFound();
        }).AddEndpointFilter(AuthorizationEndpointFilters.RequirePermission(PlatformPermissionIds.UsersWrite));

        group.MapGet("/users/{userId:guid}/roles", async (Guid userId, IAuthorizationAdministrationV1 admin, CancellationToken ct) =>
            Results.Ok(await admin.GetUserRolesAsync(new(userId), ct)))
            .AddEndpointFilter(AuthorizationEndpointFilters.RequirePermission(PlatformPermissionIds.UsersRead));

        group.MapPost("/users/{userId:guid}/roles/{roleId:guid}", async (Guid userId, Guid roleId, HttpContext http, IAuthorizationAdministrationV1 admin, IAuthorizationServiceV1 authorization, CancellationToken ct) =>
        {
            var role = await admin.GetRoleAsync(new(roleId), ct);
            if (role is null) return Results.NotFound();
            if (role.Name == "administrator" && !await IsAdministratorAsync(http, authorization, ct)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            var result = await admin.AssignRoleAsync(new(userId), new(roleId), Correlation(http), ct);
            return result.Failure switch
            {
                AuthorizationFailure.NotFound => Results.NotFound(),
                AuthorizationFailure.Conflict => Results.Conflict(),
                _ => result.Value == true ? Results.Ok() : Results.Conflict()
            };
        }).AddEndpointFilter(AuthorizationEndpointFilters.RequirePermission(PlatformPermissionIds.UsersWrite));

        group.MapDelete("/users/{userId:guid}/roles/{roleId:guid}", async (Guid userId, Guid roleId, HttpContext http, IAuthorizationAdministrationV1 admin, IAuthorizationServiceV1 authorization, CancellationToken ct) =>
        {
            var role = await admin.GetRoleAsync(new(roleId), ct);
            if (role is null) return Results.NotFound();
            if (role.Name == "administrator" && !await IsAdministratorAsync(http, authorization, ct)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            var result = await admin.RemoveRoleAsync(new(userId), new(roleId), Correlation(http), ct);
            return result.Failure switch
            {
                AuthorizationFailure.NotFound => Results.NotFound(),
                AuthorizationFailure.Conflict => Results.Conflict(),
                _ => result.Value == true ? Results.NoContent() : Results.NotFound()
            };
        }).AddEndpointFilter(AuthorizationEndpointFilters.RequirePermission(PlatformPermissionIds.UsersWrite));

        group.MapGet("/roles", async (IAuthorizationAdministrationV1 admin, CancellationToken ct) =>
        {
            var roles = await admin.ListRolesAsync(ct);
            var result = new List<RoleAdministrationView>(roles.Count);
            foreach (var role in roles) result.Add(new(role, await admin.GetRolePermissionsAsync(role.Id, ct)));
            return Results.Ok(result);
        }).AddEndpointFilter(AuthorizationEndpointFilters.RequirePermission(PlatformPermissionIds.RolesRead));

        group.MapPost("/roles", async (CreateRoleRequest request, HttpContext http, IAuthorizationAdministrationV1 admin, CancellationToken ct) =>
        {
            var result = await admin.CreateRoleAsync(request.Name, request.Description, Correlation(http), ct);
            return result.Failure switch
            {
                AuthorizationFailure.DuplicateRoleName => Results.Conflict(new { error = "role_name_unavailable" }),
                AuthorizationFailure.InvalidInput => Results.BadRequest(new { error = "invalid_role" }),
                _ => Results.Created($"/api/authorization/roles/{result.Value!.Id.Value:D}", result.Value)
            };
        }).AddEndpointFilter(AuthorizationEndpointFilters.RequirePermission(PlatformPermissionIds.RolesWrite));

        group.MapPut("/roles/{roleId:guid}", async (Guid roleId, UpdateRoleRequest request, HttpContext http, IAuthorizationAdministrationV1 admin, CancellationToken ct) =>
        {
            var result = await admin.UpdateRoleAsync(new(roleId), request.Name, request.Description, Correlation(http), ct);
            return result.Failure switch
            {
                AuthorizationFailure.NotFound => Results.NotFound(),
                AuthorizationFailure.DuplicateRoleName => Results.Conflict(new { error = "role_name_unavailable" }),
                AuthorizationFailure.Conflict => Results.Conflict(),
                AuthorizationFailure.InvalidInput => Results.BadRequest(new { error = "invalid_role" }),
                _ => Results.Ok(result.Value)
            };
        }).AddEndpointFilter(AuthorizationEndpointFilters.RequirePermission(PlatformPermissionIds.RolesWrite));

        group.MapPut("/roles/{roleId:guid}/permissions/{permissionId}", async (Guid roleId, string permissionId, HttpContext http, IAuthorizationAdministrationV1 admin, CancellationToken ct) =>
        {
            var result = await admin.AddPermissionAsync(new(roleId), permissionId, Correlation(http), ct);
            return result.Failure switch { AuthorizationFailure.NotFound => Results.NotFound(), AuthorizationFailure.InvalidInput => Results.BadRequest(new { error = "invalid_permission" }), AuthorizationFailure.Conflict => Results.Conflict(), _ => Results.Ok(new { added = result.Value }) };
        }).AddEndpointFilter(AuthorizationEndpointFilters.RequirePermission(PlatformPermissionIds.RolesPermissionsWrite));

        group.MapDelete("/roles/{roleId:guid}/permissions/{permissionId}", async (Guid roleId, string permissionId, HttpContext http, IAuthorizationAdministrationV1 admin, CancellationToken ct) =>
        {
            var result = await admin.RemovePermissionAsync(new(roleId), permissionId, Correlation(http), ct);
            return result.Failure switch { AuthorizationFailure.NotFound => Results.NotFound(), AuthorizationFailure.InvalidInput => Results.BadRequest(new { error = "invalid_permission" }), AuthorizationFailure.Conflict => Results.Conflict(), _ => result.Value == true ? Results.NoContent() : Results.NotFound() };
        }).AddEndpointFilter(AuthorizationEndpointFilters.RequirePermission(PlatformPermissionIds.RolesPermissionsWrite));

        return endpoints;
    }

    private static async ValueTask<bool> IsAdministratorAsync(HttpContext http, IAuthorizationServiceV1 authorization, CancellationToken cancellationToken)
    {
        if (AuthorizationEndpointFilters.IsLegacyAdmin(http)) return true;
        var userId = AuthorizationEndpointFilters.AuthenticatedUserId(http);
        return userId is not null && await authorization.HasPermissionAsync(userId.Value, PlatformPermissionIds.Admin, cancellationToken);
    }
    private static string? Correlation(HttpContext http) => http.Request.Headers["X-Correlation-ID"].FirstOrDefault() ?? System.Diagnostics.Activity.Current?.TraceId.ToString();
    public sealed record BootstrapAdministratorRequest(Guid UserId);
    public sealed record UserStatusRequest(bool Enabled);
    public sealed record CreateRoleRequest(string Name, string Description);
    public sealed record UpdateRoleRequest(string Name, string Description);
    public sealed record UserAdministrationView(User User, IReadOnlyList<Role> Roles);
    public sealed record RoleAdministrationView(Role Role, IReadOnlyList<string> Permissions);
}
