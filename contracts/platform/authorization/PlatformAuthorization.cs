using Hispania.Platform.Contracts.Identity;

namespace Hispania.Platform.Contracts.Authorization;

public readonly record struct RoleId(Guid Value) { public static RoleId New() => new(Guid.NewGuid()); }
public sealed record Role(RoleId Id, string Name, string Description, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);
public sealed record RoleAssignment(UserId UserId, RoleId RoleId, DateTimeOffset AssignedAt);
public sealed record AuthorizationDecision(bool Authenticated, bool Allowed);

public static class PlatformPermissionIds
{
    public const string Admin = "platform.admin";
    public const string UsersRead = "platform.users.read";
    public const string UsersWrite = "platform.users.write";
    public const string RolesRead = "platform.roles.read";
    public const string RolesWrite = "platform.roles.write";
    public const string RolesPermissionsWrite = "platform.roles.permissions.write";
    public const string AuditRead = "platform.audit.read";
    public const string AssetsWrite = "platform.assets.write";
    public const string EventsRead = "platform.events.read";
    public const string EventsManage = "platform.events.manage";
    public static IReadOnlyList<string> BuiltIn { get; } = [Admin, UsersRead, UsersWrite, RolesRead, RolesWrite, RolesPermissionsWrite, AuditRead, AssetsWrite, EventsRead, EventsManage];
}

public enum AuthorizationFailure { None, InvalidInput, DuplicateRoleName, NotFound, Conflict }
public sealed record AuthorizationOperationResult<T>(T? Value, AuthorizationFailure Failure)
{
    public bool Succeeded => Failure == AuthorizationFailure.None;
}

public interface IAuthorizationServiceV1
{
    ValueTask<bool> HasPermissionAsync(UserId userId, string permissionId, CancellationToken cancellationToken = default);
    ValueTask<IReadOnlySet<string>> GetEffectivePermissionsAsync(UserId userId, CancellationToken cancellationToken = default);
    ValueTask<AuthorizationDecision> RequirePermissionAsync(UserId? userId, string permissionId, CancellationToken cancellationToken = default);
}

public interface IAuthorizationAdministrationV1
{
    ValueTask<AuthorizationOperationResult<Role>> CreateRoleAsync(string name, string description, string? correlationId = null, CancellationToken cancellationToken = default);
    ValueTask<AuthorizationOperationResult<Role>> UpdateRoleAsync(RoleId roleId, string name, string description, string? correlationId = null, CancellationToken cancellationToken = default);
    ValueTask<Role?> GetRoleAsync(RoleId roleId, CancellationToken cancellationToken = default);
    ValueTask<IReadOnlyList<Role>> ListRolesAsync(CancellationToken cancellationToken = default);
    ValueTask<IReadOnlyList<string>> GetRolePermissionsAsync(RoleId roleId, CancellationToken cancellationToken = default);
    ValueTask<AuthorizationOperationResult<bool>> AddPermissionAsync(RoleId roleId, string permissionId, string? correlationId = null, CancellationToken cancellationToken = default);
    ValueTask<AuthorizationOperationResult<bool>> RemovePermissionAsync(RoleId roleId, string permissionId, string? correlationId = null, CancellationToken cancellationToken = default);
    ValueTask<IReadOnlyList<Role>> GetUserRolesAsync(UserId userId, CancellationToken cancellationToken = default);
    ValueTask<bool> HasOtherEnabledAdministratorsAsync(UserId exceptUserId, CancellationToken cancellationToken = default);
    ValueTask<AuthorizationOperationResult<bool>> AssignRoleAsync(UserId userId, RoleId roleId, string? correlationId = null, CancellationToken cancellationToken = default);
    ValueTask<AuthorizationOperationResult<bool>> RemoveRoleAsync(UserId userId, RoleId roleId, string? correlationId = null, CancellationToken cancellationToken = default);
    ValueTask<bool> BootstrapAdministratorAsync(UserId userId, string bootstrapToken, CancellationToken cancellationToken = default);
}
