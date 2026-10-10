using System.Security.Cryptography;
using System.Text;
using Hispania.Platform.Contracts.Audit;
using Hispania.Platform.Contracts.Authorization;
using Hispania.Platform.Contracts.Identity;

namespace Hispania.Authorization.Application;

public interface IPlatformAuthorizationStore
{
    ValueTask<Role?> GetRoleAsync(RoleId roleId, CancellationToken cancellationToken);
    ValueTask<IReadOnlyList<Role>> ListRolesAsync(CancellationToken cancellationToken);
    ValueTask<Role?> GetRoleByNameAsync(string normalizedName, CancellationToken cancellationToken);
    ValueTask<bool> CreateRoleAsync(Role role, CancellationToken cancellationToken);
    ValueTask<bool> UpdateRoleAsync(Role role, CancellationToken cancellationToken);
    ValueTask<IReadOnlyList<string>> GetRolePermissionsAsync(RoleId roleId, CancellationToken cancellationToken);
    ValueTask<bool> AddPermissionAsync(RoleId roleId, string permissionId, CancellationToken cancellationToken);
    ValueTask<bool> RemovePermissionAsync(RoleId roleId, string permissionId, CancellationToken cancellationToken);
    ValueTask<IReadOnlyList<Role>> GetUserRolesAsync(UserId userId, CancellationToken cancellationToken);
    ValueTask<bool> AssignRoleAsync(UserId userId, RoleId roleId, CancellationToken cancellationToken);
    ValueTask<bool> RemoveRoleAsync(UserId userId, RoleId roleId, CancellationToken cancellationToken);
    ValueTask<bool> BootstrapAdministratorAsync(UserId userId, IReadOnlyList<string> permissions, CancellationToken cancellationToken);
}

public sealed class AuthorizationService(IPlatformAuthorizationStore store, IIdentityQueryV1 identity) : IAuthorizationServiceV1
{
    public async ValueTask<bool> HasPermissionAsync(UserId userId, string permissionId, CancellationToken cancellationToken = default)
    {
        var user = await identity.GetUserAsync(userId, cancellationToken).ConfigureAwait(false);
        if (user is not { Status: UserStatus.Enabled } || !IsValidPermission(permissionId)) return false;
        var permissions = await GetEffectivePermissionsAsync(userId, cancellationToken).ConfigureAwait(false);
        return permissions.Contains(permissionId) || (permissions.Contains(PlatformPermissionIds.Admin) && permissionId.StartsWith("platform.", StringComparison.Ordinal));
    }

    public async ValueTask<IReadOnlySet<string>> GetEffectivePermissionsAsync(UserId userId, CancellationToken cancellationToken = default)
    {
        var user = await identity.GetUserAsync(userId, cancellationToken).ConfigureAwait(false);
        if (user is not { Status: UserStatus.Enabled }) return new HashSet<string>(StringComparer.Ordinal);
        var permissions = new HashSet<string>(StringComparer.Ordinal);
        foreach (var role in await store.GetUserRolesAsync(userId, cancellationToken).ConfigureAwait(false))
            permissions.UnionWith(await store.GetRolePermissionsAsync(role.Id, cancellationToken).ConfigureAwait(false));
        return permissions;
    }

    public async ValueTask<AuthorizationDecision> RequirePermissionAsync(UserId? userId, string permissionId, CancellationToken cancellationToken = default)
    {
        if (userId is null) return new(false, false);
        var user = await identity.GetUserAsync(userId.Value, cancellationToken).ConfigureAwait(false);
        return user is not { Status: UserStatus.Enabled } ? new(false, false) : new(true, await HasPermissionAsync(userId.Value, permissionId, cancellationToken).ConfigureAwait(false));
    }

    internal static bool IsValidPermission(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 128 && value.Split('.').Length is >= 2 and <= 8 && value.Split('.').All(part => part.Length is >= 1 and <= 32 && part.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_'));
}

public sealed class AuthorizationAdministrationService(IPlatformAuthorizationStore store, IIdentityQueryV1 identity, IPlatformAuditWriter? audit = null, string? configuredBootstrapToken = null) : IAuthorizationAdministrationV1
{
    private const string AdministratorRoleName = "administrator";
    public async ValueTask<AuthorizationOperationResult<Role>> CreateRoleAsync(string name, string description, string? correlationId = null, CancellationToken cancellationToken = default)
    {
        if (!TryNormalizeRole(name, out var normalized) || normalized == AdministratorRoleName || !ValidDescription(description)) return new(null, AuthorizationFailure.InvalidInput);
        var now = DateTimeOffset.UtcNow; var role = new Role(RoleId.New(), normalized, description.Trim(), now, now);
        if (!await store.CreateRoleAsync(role, cancellationToken).ConfigureAwait(false)) return new(null, AuthorizationFailure.DuplicateRoleName);
        await AuditAsync("role-created", role.Id.Value.ToString("D"), correlationId, cancellationToken).ConfigureAwait(false);
        return new(role, AuthorizationFailure.None);
    }

    public async ValueTask<AuthorizationOperationResult<Role>> UpdateRoleAsync(RoleId roleId, string name, string description, string? correlationId = null, CancellationToken cancellationToken = default)
    {
        if (!TryNormalizeRole(name, out var normalized) || normalized == AdministratorRoleName && (await store.GetRoleAsync(roleId, cancellationToken).ConfigureAwait(false))?.Name != AdministratorRoleName || !ValidDescription(description)) return new(null, AuthorizationFailure.InvalidInput);
        var role = await store.GetRoleAsync(roleId, cancellationToken).ConfigureAwait(false);
        if (role is null) return new(null, AuthorizationFailure.NotFound);
        if (role.Name == AdministratorRoleName && normalized != AdministratorRoleName) return new(null, AuthorizationFailure.Conflict);
        var updated = role with { Name = normalized, Description = description.Trim(), UpdatedAt = DateTimeOffset.UtcNow };
        if (!await store.UpdateRoleAsync(updated, cancellationToken).ConfigureAwait(false)) return new(null, AuthorizationFailure.DuplicateRoleName);
        await AuditAsync("role-updated", roleId.Value.ToString("D"), correlationId, cancellationToken).ConfigureAwait(false);
        return new(updated, AuthorizationFailure.None);
    }

    public ValueTask<Role?> GetRoleAsync(RoleId roleId, CancellationToken cancellationToken = default) => store.GetRoleAsync(roleId, cancellationToken);
    public ValueTask<IReadOnlyList<Role>> ListRolesAsync(CancellationToken cancellationToken = default) => store.ListRolesAsync(cancellationToken);
    public ValueTask<IReadOnlyList<string>> GetRolePermissionsAsync(RoleId roleId, CancellationToken cancellationToken = default) => store.GetRolePermissionsAsync(roleId, cancellationToken);
    public ValueTask<IReadOnlyList<Role>> GetUserRolesAsync(UserId userId, CancellationToken cancellationToken = default) => store.GetUserRolesAsync(userId, cancellationToken);

    public async ValueTask<bool> HasOtherEnabledAdministratorsAsync(UserId exceptUserId, CancellationToken cancellationToken = default)
    {
        foreach (var user in await identity.ListUsersAsync(cancellationToken).ConfigureAwait(false))
        {
            if (user.Id == exceptUserId || user.Status != UserStatus.Enabled) continue;
            if ((await store.GetUserRolesAsync(user.Id, cancellationToken).ConfigureAwait(false)).Any(role => role.Name == AdministratorRoleName)) return true;
        }
        return false;
    }

    public async ValueTask<AuthorizationOperationResult<bool>> AddPermissionAsync(RoleId roleId, string permissionId, string? correlationId = null, CancellationToken cancellationToken = default)
    {
        if (!AuthorizationService.IsValidPermission(permissionId)) return new(false, AuthorizationFailure.InvalidInput);
        if (await store.GetRoleAsync(roleId, cancellationToken).ConfigureAwait(false) is null) return new(false, AuthorizationFailure.NotFound);
        if (permissionId == PlatformPermissionIds.Admin) return new(false, AuthorizationFailure.Conflict);
        var added = await store.AddPermissionAsync(roleId, permissionId, cancellationToken).ConfigureAwait(false);
        if (added) await AuditAsync("role-permission-added", roleId.Value.ToString("D"), correlationId, cancellationToken, permissionId).ConfigureAwait(false);
        return new(added, AuthorizationFailure.None);
    }

    public async ValueTask<AuthorizationOperationResult<bool>> RemovePermissionAsync(RoleId roleId, string permissionId, string? correlationId = null, CancellationToken cancellationToken = default)
    {
        if (!AuthorizationService.IsValidPermission(permissionId)) return new(false, AuthorizationFailure.InvalidInput);
        if (await store.GetRoleAsync(roleId, cancellationToken).ConfigureAwait(false) is null) return new(false, AuthorizationFailure.NotFound);
        if (permissionId == PlatformPermissionIds.Admin) return new(false, AuthorizationFailure.Conflict);
        var removed = await store.RemovePermissionAsync(roleId, permissionId, cancellationToken).ConfigureAwait(false);
        if (removed) await AuditAsync("role-permission-removed", roleId.Value.ToString("D"), correlationId, cancellationToken, permissionId).ConfigureAwait(false);
        return new(removed, AuthorizationFailure.None);
    }

    public async ValueTask<AuthorizationOperationResult<bool>> AssignRoleAsync(UserId userId, RoleId roleId, string? correlationId = null, CancellationToken cancellationToken = default)
    {
        if (await identity.GetUserAsync(userId, cancellationToken).ConfigureAwait(false) is null || await store.GetRoleAsync(roleId, cancellationToken).ConfigureAwait(false) is null) return new(false, AuthorizationFailure.NotFound);
        var assigned = await store.AssignRoleAsync(userId, roleId, cancellationToken).ConfigureAwait(false);
        if (assigned) await AuditAsync("user-role-assigned", userId.Value.ToString("D"), correlationId, cancellationToken, roleId.Value.ToString("D")).ConfigureAwait(false);
        return new(assigned, AuthorizationFailure.None);
    }

    public async ValueTask<AuthorizationOperationResult<bool>> RemoveRoleAsync(UserId userId, RoleId roleId, string? correlationId = null, CancellationToken cancellationToken = default)
    {
        var roles = await store.GetUserRolesAsync(userId, cancellationToken).ConfigureAwait(false);
        var target = roles.FirstOrDefault(role => role.Id == roleId);
        if (target is null) return new(false, AuthorizationFailure.NotFound);
        if (target.Name == AdministratorRoleName && !await HasOtherEnabledAdministratorsAsync(userId, cancellationToken).ConfigureAwait(false)) return new(false, AuthorizationFailure.Conflict);
        var removed = await store.RemoveRoleAsync(userId, roleId, cancellationToken).ConfigureAwait(false);
        if (removed) await AuditAsync("user-role-removed", userId.Value.ToString("D"), correlationId, cancellationToken, roleId.Value.ToString("D")).ConfigureAwait(false);
        return new(removed, AuthorizationFailure.None);
    }

    public async ValueTask<bool> BootstrapAdministratorAsync(UserId userId, string bootstrapToken, CancellationToken cancellationToken = default)
    {
        var expectedBytes = Encoding.UTF8.GetBytes(bootstrapToken ?? string.Empty);
        var configuredBytes = Encoding.UTF8.GetBytes(configuredBootstrapToken ?? string.Empty);
        if (configuredBytes.Length < 32 || expectedBytes.Length != configuredBytes.Length || !CryptographicOperations.FixedTimeEquals(expectedBytes, configuredBytes)) return false;
        if (await identity.GetUserAsync(userId, cancellationToken).ConfigureAwait(false) is not { Status: UserStatus.Enabled }) return false;
        var created = await store.BootstrapAdministratorAsync(userId, PlatformPermissionIds.BuiltIn, cancellationToken).ConfigureAwait(false);
        if (created) await AuditAsync("bootstrap-admin-created", userId.Value.ToString("D"), null, cancellationToken).ConfigureAwait(false);
        return created;
    }

    private async ValueTask AuditAsync(string action, string resourceId, string? correlationId, CancellationToken cancellationToken, string? value = null)
    {
        if (audit is null) return;
        var metadata = value is null ? new Dictionary<string, string>() : new Dictionary<string, string> { ["permissionOrRoleId"] = value };
        await audit.WriteAsync(new(DateTimeOffset.UtcNow, null, correlationId, "platform.authorization", action, "authorization", resourceId, "success", metadata), cancellationToken).ConfigureAwait(false);
    }

    private static bool TryNormalizeRole(string? name, out string normalized)
    {
        var input = name?.Trim() ?? string.Empty;
        var valid = input.Length is >= 3 and <= 64 && input.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' or ' ');
        normalized = valid ? string.Join('-', input.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries)) : string.Empty;
        return valid && normalized.Length >= 3;
    }
    private static bool ValidDescription(string? description) => description is not null && description.Length <= 500;
}
