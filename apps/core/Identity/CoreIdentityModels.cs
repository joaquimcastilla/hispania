using System.Text;

namespace Hispania.Core.Identity;

internal enum CoreAccountStatus
{
    Active,
    Disabled
}

internal sealed record CoreIdentity(
    Guid SubjectId,
    string Username,
    string NormalizedUsername,
    string SubjectKind,
    CoreAccountStatus Status,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc);

internal sealed record CoreAuthenticationResult(CoreIdentity? Identity)
{
    public bool Succeeded => Identity is not null;
}

internal sealed record CoreSession(string Token, Guid SubjectId, DateTimeOffset CreatedUtc, DateTimeOffset ExpiresUtc);

internal sealed record CoreSessionValidation(bool IsValid, Guid? SubjectId, DateTimeOffset? ExpiresUtc);

internal enum CoreBootstrapResult
{
    Created,
    AlreadyCompleted,
    InvalidInput,
    Failed
}

internal static class CoreIdentityRules
{
    public const string LocalUserKind = "local-user";
    public const string AdministratorRole = "core.admin";
    public const string IdentityReadPermission = "core.identity.read";
    public const string IdentityManagePermission = "core.identity.manage";
    public const string AuthorizationManagePermission = "core.authorization.manage";
    public const string SessionRevokePermission = "core.session.revoke";

    public static bool TryNormalizeUsername(string? username, out string displayName, out string normalized)
    {
        displayName = string.Empty;
        normalized = string.Empty;
        if (username is null) return false;

        string canonical;
        try { canonical = username.Normalize(NormalizationForm.FormKC).Trim(); }
        catch (ArgumentException) { return false; }
        if (canonical.Length is < 3 or > 64) return false;
        if (!IsAsciiAlphaNumeric(canonical[0]) || !canonical.All(IsUsernameCharacter)) return false;

        displayName = canonical;
        normalized = canonical.ToUpperInvariant();
        return true;
    }

    public static bool IsValidPassword(string? password)
    {
        if (password is null) return false;
        var runeCount = 0;
        foreach (var rune in password.EnumerateRunes())
        {
            runeCount++;
            if (runeCount > 128) return false;
        }
        return runeCount >= 15 && Encoding.UTF8.GetByteCount(password) <= 1024;
    }

    public static bool IsValidIdentifier(string? identifier)
    {
        if (identifier is null || identifier.Length is < 2 or > 128 || identifier[0] is < 'a' or > 'z') return false;
        var previousSeparator = false;
        for (var index = 1; index < identifier.Length; index++)
        {
            var character = identifier[index];
            if (character is >= 'a' and <= 'z' or >= '0' and <= '9')
            {
                previousSeparator = false;
                continue;
            }
            if (character is '.' or '_' or ':' or '-')
            {
                if (previousSeparator || index == identifier.Length - 1) return false;
                previousSeparator = true;
                continue;
            }
            return false;
        }
        return true;
    }

    private static bool IsUsernameCharacter(char character) =>
        IsAsciiAlphaNumeric(character) || character is '.' or '_' or '-';

    private static bool IsAsciiAlphaNumeric(char character) =>
        character is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9';
}

internal interface ICoreIdentityService
{
    Task<CoreIdentity?> CreateLocalAccountAsync(Guid actorSubjectId, string username, string password, CancellationToken cancellationToken);
    Task<CoreIdentity?> FindByIdAsync(Guid subjectId, CancellationToken cancellationToken);
    Task<CoreAuthenticationResult> AuthenticateAsync(string? username, string? password, CancellationToken cancellationToken);
    Task<bool> SetAccountStatusAsync(Guid actorSubjectId, Guid subjectId, CoreAccountStatus status, CancellationToken cancellationToken);
    Task<bool> DeleteAccountAsync(Guid actorSubjectId, Guid subjectId, CancellationToken cancellationToken);
}

internal interface ICoreAuthorizationService
{
    Task<bool> IsAuthorizedAsync(Guid subjectId, string permissionId, CancellationToken cancellationToken);
    Task<bool> CreateRoleAsync(Guid actorSubjectId, string roleId, CancellationToken cancellationToken);
    Task<bool> RegisterPermissionAsync(Guid actorSubjectId, string permissionId, CancellationToken cancellationToken);
    Task<bool> GrantPermissionToRoleAsync(Guid actorSubjectId, string roleId, string permissionId, CancellationToken cancellationToken);
    Task<bool> AssignRoleAsync(Guid actorSubjectId, Guid subjectId, string roleId, CancellationToken cancellationToken);
}

internal interface ICoreSessionService
{
    Task<CoreSession?> CreateAsync(Guid subjectId, DateTimeOffset nowUtc, TimeSpan lifetime, CancellationToken cancellationToken);
    Task<CoreSessionValidation> ValidateAsync(string? token, DateTimeOffset nowUtc, CancellationToken cancellationToken);
    Task<bool> RevokeAsync(Guid actorSubjectId, string token, DateTimeOffset nowUtc, CancellationToken cancellationToken);
    Task<int> InvalidateAllForLocalRecoveryAsync(DateTimeOffset nowUtc, CancellationToken cancellationToken);
}

internal interface ICoreHttpAuditService
{
    Task RecordAsync(string eventCode, Guid? subjectId, CancellationToken cancellationToken);
}

internal interface ICoreBootstrapService
{
    Task<bool> IsPendingAsync(CancellationToken cancellationToken);
    Task<CoreBootstrapResult> CreateInitialAdministratorAsync(string username, string password, CancellationToken cancellationToken);
}

/// <summary>Provider-neutral Core identity persistence operations; implementations own their storage model.</summary>
internal interface ICoreIdentityStore
{
    Task<bool> IsBootstrapPendingAsync(CancellationToken cancellationToken);
    Task<CoreBootstrapResult> BootstrapAdministratorAsync(string username, string normalizedUsername, string passwordHash, int iterations, DateTimeOffset nowUtc, CancellationToken cancellationToken);
    Task<CoreIdentity?> CreateLocalAccountAsync(Guid actorSubjectId, string username, string normalizedUsername, string passwordHash, int iterations, DateTimeOffset nowUtc, CancellationToken cancellationToken);
    Task<StoredCoreIdentity?> FindByUsernameAsync(string normalizedUsername, CancellationToken cancellationToken);
    Task<CoreIdentity?> FindByIdAsync(Guid subjectId, CancellationToken cancellationToken);
    Task ReplaceCredentialHashAsync(Guid subjectId, string oldHash, string newHash, int iterations, DateTimeOffset nowUtc, CancellationToken cancellationToken);
    Task<bool> SetAccountStatusAsync(Guid actorSubjectId, Guid subjectId, CoreAccountStatus status, DateTimeOffset nowUtc, CancellationToken cancellationToken);
    Task<bool> DeleteAccountAsync(Guid actorSubjectId, Guid subjectId, DateTimeOffset nowUtc, CancellationToken cancellationToken);
    Task<bool> IsAuthorizedAsync(Guid subjectId, string permissionId, CancellationToken cancellationToken);
    Task<bool> CreateRoleAsync(Guid actorSubjectId, string roleId, DateTimeOffset nowUtc, CancellationToken cancellationToken);
    Task<bool> RegisterPermissionAsync(Guid actorSubjectId, string permissionId, DateTimeOffset nowUtc, CancellationToken cancellationToken);
    Task<bool> GrantPermissionToRoleAsync(Guid actorSubjectId, string roleId, string permissionId, DateTimeOffset nowUtc, CancellationToken cancellationToken);
    Task<bool> AssignRoleAsync(Guid actorSubjectId, Guid subjectId, string roleId, DateTimeOffset nowUtc, CancellationToken cancellationToken);
    Task<CoreSession?> CreateSessionAsync(Guid subjectId, DateTimeOffset nowUtc, TimeSpan lifetime, CancellationToken cancellationToken);
    Task<CoreSessionValidation> ValidateSessionAsync(byte[] tokenHash, DateTimeOffset nowUtc, CancellationToken cancellationToken);
    Task<bool> RevokeSessionAsync(Guid actorSubjectId, byte[] tokenHash, DateTimeOffset nowUtc, CancellationToken cancellationToken);
    Task<int> InvalidateAllSessionsAsync(DateTimeOffset nowUtc, CancellationToken cancellationToken);
    Task RecordHttpSecurityEventAsync(string eventCode, Guid? subjectId, DateTimeOffset nowUtc, CancellationToken cancellationToken);
}
