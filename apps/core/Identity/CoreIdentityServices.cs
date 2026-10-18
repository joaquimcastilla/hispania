using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Hispania.Core.Persistence;
using Microsoft.AspNetCore.Identity;

namespace Hispania.Core.Identity;

internal sealed class CoreIdentityService : ICoreIdentityService
{
    public const string HashFormat = "aspnet-identity-v3";
    public const string HashAlgorithm = "PBKDF2-HMAC-SHA512";
    public const int Iterations = 220_000;
    private const int MinimumSupportedIterations = 100_000;
    private const int MaximumSupportedIterations = Iterations;
    private const int MinimumSaltBytes = 16;
    private const int MinimumSubkeyBytes = 16;
    private const int CurrentSubkeyBytes = 32;

    private readonly ICoreIdentityStore _store;
    private readonly IPasswordHasher<CoreIdentity> _passwordHasher;
    private readonly CoreIdentity _dummyIdentity = new(Guid.Empty, "dummy", "DUMMY", CoreIdentityRules.LocalUserKind, CoreAccountStatus.Active, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);
    private readonly string _dummyHash;

    public CoreIdentityService(ICoreIdentityStore store, IPasswordHasher<CoreIdentity> passwordHasher)
    {
        _store = store;
        _passwordHasher = passwordHasher;
        _dummyHash = _passwordHasher.HashPassword(_dummyIdentity, Convert.ToBase64String(RandomNumberGenerator.GetBytes(48)));
    }

    public async Task<CoreIdentity?> CreateLocalAccountAsync(Guid actorSubjectId, string username, string password, CancellationToken cancellationToken)
    {
        if (!CoreIdentityRules.TryNormalizeUsername(username, out var displayName, out var normalized) || !CoreIdentityRules.IsValidPassword(password)) return null;
        if (!await _store.IsAuthorizedAsync(actorSubjectId, CoreIdentityRules.IdentityManagePermission, cancellationToken).ConfigureAwait(false)) return null;
        var identity = new CoreIdentity(Guid.NewGuid(), displayName, normalized, CoreIdentityRules.LocalUserKind, CoreAccountStatus.Active, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        var hashed = await HashAsync(identity, password, cancellationToken).ConfigureAwait(false);
        return await _store.CreateLocalAccountAsync(actorSubjectId, displayName, normalized, hashed, Iterations, DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false);
    }

    public Task<CoreIdentity?> FindByIdAsync(Guid subjectId, CancellationToken cancellationToken) =>
        _store.FindByIdAsync(subjectId, cancellationToken);

    public async Task<CoreAuthenticationResult> AuthenticateAsync(string? username, string? password, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(password) || password.Length > 256)
        {
            await VerifyDummyAsync(_dummyIdentity, cancellationToken).ConfigureAwait(false);
            return new CoreAuthenticationResult(null);
        }

        if (!CoreIdentityRules.TryNormalizeUsername(username, out _, out var normalized))
        {
            await VerifyDummyAsync(password, cancellationToken).ConfigureAwait(false);
            return new CoreAuthenticationResult(null);
        }

        var stored = await _store.FindByUsernameAsync(normalized, cancellationToken).ConfigureAwait(false);
        if (stored?.PasswordHash is null || stored.HashFormat != HashFormat || stored.Algorithm != HashAlgorithm ||
            stored.Iterations is null || !IsSupportedHash(stored.PasswordHash, stored.Iterations.Value))
        {
            await VerifyDummyAsync(password, cancellationToken).ConfigureAwait(false);
            return new CoreAuthenticationResult(null);
        }

        var result = await Task.Run(
            () => SafeVerify(stored.Identity, stored.PasswordHash, password), cancellationToken).ConfigureAwait(false);
        if (result == PasswordVerificationResult.Failed || stored.Identity.Status != CoreAccountStatus.Active)
            return new CoreAuthenticationResult(null);

        if (result == PasswordVerificationResult.SuccessRehashNeeded)
        {
            var upgraded = await HashAsync(stored.Identity, password, cancellationToken).ConfigureAwait(false);
            await _store.ReplaceCredentialHashAsync(stored.Identity.SubjectId, stored.PasswordHash, upgraded, Iterations, DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false);
        }
        return new CoreAuthenticationResult(stored.Identity);
    }

    public Task<bool> SetAccountStatusAsync(Guid actorSubjectId, Guid subjectId, CoreAccountStatus status, CancellationToken cancellationToken) =>
        _store.SetAccountStatusAsync(actorSubjectId, subjectId, status, DateTimeOffset.UtcNow, cancellationToken);

    public Task<bool> DeleteAccountAsync(Guid actorSubjectId, Guid subjectId, CancellationToken cancellationToken) =>
        _store.DeleteAccountAsync(actorSubjectId, subjectId, DateTimeOffset.UtcNow, cancellationToken);

    internal async Task<CoreBootstrapResult> CreateInitialAdministratorAsync(string username, string password, CancellationToken cancellationToken)
    {
        if (!CoreIdentityRules.TryNormalizeUsername(username, out var displayName, out var normalized) || !CoreIdentityRules.IsValidPassword(password))
            return CoreBootstrapResult.InvalidInput;
        if (!await _store.IsBootstrapPendingAsync(cancellationToken).ConfigureAwait(false)) return CoreBootstrapResult.AlreadyCompleted;
        var identity = new CoreIdentity(Guid.NewGuid(), displayName, normalized, CoreIdentityRules.LocalUserKind, CoreAccountStatus.Active, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        var hash = await HashAsync(identity, password, cancellationToken).ConfigureAwait(false);
        return await _store.BootstrapAdministratorAsync(displayName, normalized, hash, Iterations, DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false);
    }

    private async Task<string> HashAsync(CoreIdentity identity, string password, CancellationToken cancellationToken) =>
        await Task.Run(() => _passwordHasher.HashPassword(identity, password), cancellationToken).ConfigureAwait(false);

    private async Task VerifyDummyAsync(CoreIdentity identity, CancellationToken cancellationToken) =>
        await VerifyDummyAsync(identity, Convert.ToBase64String(RandomNumberGenerator.GetBytes(24)), cancellationToken).ConfigureAwait(false);

    private async Task VerifyDummyAsync(string password, CancellationToken cancellationToken) =>
        await VerifyDummyAsync(_dummyIdentity, password, cancellationToken).ConfigureAwait(false);

    private async Task VerifyDummyAsync(CoreIdentity identity, string password, CancellationToken cancellationToken)
    {
        _ = await Task.Run(() => SafeVerify(identity, _dummyHash, password), cancellationToken).ConfigureAwait(false);
    }

    private PasswordVerificationResult SafeVerify(CoreIdentity identity, string hash, string password)
    {
        try { return _passwordHasher.VerifyHashedPassword(identity, hash, password); }
        catch (Exception exception) when (exception is FormatException or ArgumentException or CryptographicException or IndexOutOfRangeException)
        {
            return PasswordVerificationResult.Failed;
        }
    }

    private static bool IsSupportedHash(string encodedHash, int storedIterations)
    {
        if (storedIterations is < MinimumSupportedIterations or > MaximumSupportedIterations) return false;
        byte[] bytes;
        try { bytes = Convert.FromBase64String(encodedHash); }
        catch (FormatException) { return false; }
        try
        {
            if (bytes.Length < 13 + MinimumSaltBytes + MinimumSubkeyBytes || bytes[0] != 0x01) return false;
            var prf = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(1, 4));
            var iterations = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(5, 4));
            var saltLength = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(9, 4));
            if (saltLength > int.MaxValue) return false;
            var subkeyLength = bytes.Length - 13 - (int)saltLength;
            return prf == 2 && iterations == storedIterations && saltLength >= MinimumSaltBytes &&
                   subkeyLength >= MinimumSubkeyBytes && subkeyLength == CurrentSubkeyBytes;
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
}

internal sealed class CoreAuthorizationService(ICoreIdentityStore store) : ICoreAuthorizationService
{
    public Task<bool> IsAuthorizedAsync(Guid subjectId, string permissionId, CancellationToken cancellationToken) =>
        store.IsAuthorizedAsync(subjectId, permissionId, cancellationToken);

    public Task<bool> CreateRoleAsync(Guid actorSubjectId, string roleId, CancellationToken cancellationToken) =>
        CoreIdentityRules.IsValidIdentifier(roleId)
            ? store.CreateRoleAsync(actorSubjectId, roleId, DateTimeOffset.UtcNow, cancellationToken)
            : Task.FromResult(false);

    public Task<bool> RegisterPermissionAsync(Guid actorSubjectId, string permissionId, CancellationToken cancellationToken) =>
        CoreIdentityRules.IsValidIdentifier(permissionId)
            ? store.RegisterPermissionAsync(actorSubjectId, permissionId, DateTimeOffset.UtcNow, cancellationToken)
            : Task.FromResult(false);

    public Task<bool> GrantPermissionToRoleAsync(Guid actorSubjectId, string roleId, string permissionId, CancellationToken cancellationToken) =>
        CoreIdentityRules.IsValidIdentifier(roleId) && CoreIdentityRules.IsValidIdentifier(permissionId)
            ? store.GrantPermissionToRoleAsync(actorSubjectId, roleId, permissionId, DateTimeOffset.UtcNow, cancellationToken)
            : Task.FromResult(false);

    public Task<bool> AssignRoleAsync(Guid actorSubjectId, Guid subjectId, string roleId, CancellationToken cancellationToken) =>
        CoreIdentityRules.IsValidIdentifier(roleId)
            ? store.AssignRoleAsync(actorSubjectId, subjectId, roleId, DateTimeOffset.UtcNow, cancellationToken)
            : Task.FromResult(false);
}

internal sealed class CoreSessionService(ICoreIdentityStore store) : ICoreSessionService
{
    public const int TokenBytes = 32;
    public static readonly TimeSpan MaximumLifetime = TimeSpan.FromDays(30);

    public Task<CoreSession?> CreateAsync(Guid subjectId, DateTimeOffset nowUtc, TimeSpan lifetime, CancellationToken cancellationToken)
    {
        if (lifetime <= TimeSpan.Zero || lifetime > MaximumLifetime) return Task.FromResult<CoreSession?>(null);
        return store.CreateSessionAsync(subjectId, nowUtc, lifetime, cancellationToken);
    }

    public async Task<CoreSessionValidation> ValidateAsync(string? token, DateTimeOffset nowUtc, CancellationToken cancellationToken)
    {
        if (!TryHashToken(token, out var hash)) return new CoreSessionValidation(false, null, null);
        return await store.ValidateSessionAsync(hash, nowUtc, cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> RevokeAsync(Guid actorSubjectId, string token, DateTimeOffset nowUtc, CancellationToken cancellationToken)
    {
        if (!TryHashToken(token, out var hash)) return false;
        return await store.RevokeSessionAsync(actorSubjectId, hash, nowUtc, cancellationToken).ConfigureAwait(false);
    }

    public Task<int> InvalidateAllForLocalRecoveryAsync(DateTimeOffset nowUtc, CancellationToken cancellationToken) =>
        store.InvalidateAllSessionsAsync(nowUtc, cancellationToken);

    private static bool TryHashToken(string? token, out byte[] hash)
    {
        hash = [];
        if (token is null || token.Length != 43 || token.Any(character =>
                character is not (>= 'A' and <= 'Z') and not (>= 'a' and <= 'z') and not (>= '0' and <= '9') and not '-' and not '_'))
            return false;
        try
        {
            var bytes = Convert.FromBase64String(token.Replace('-', '+').Replace('_', '/') + "=");
            if (bytes.Length != TokenBytes) { CryptographicOperations.ZeroMemory(bytes); return false; }
            hash = SHA256.HashData(bytes);
            CryptographicOperations.ZeroMemory(bytes);
            return true;
        }
        catch (FormatException) { return false; }
    }
}

internal sealed class CoreHttpAuditService(ICoreIdentityStore store) : ICoreHttpAuditService
{
    private static readonly HashSet<string> AllowedEvents = new(StringComparer.Ordinal)
    {
        "http.login.succeeded",
        "http.login.rejected",
        "http.login.rate_limited",
        "http.session.logout",
        "http.session.rejected",
        "http.modules.install.succeeded",
        "http.modules.install.rejected",
        "http.modules.policy.changed",
        "http.modules.operation.failed"
    };

    public Task RecordAsync(string eventCode, Guid? subjectId, CancellationToken cancellationToken)
    {
        if (!AllowedEvents.Contains(eventCode)) throw new ArgumentOutOfRangeException(nameof(eventCode));
        return store.RecordHttpSecurityEventAsync(eventCode, subjectId, DateTimeOffset.UtcNow, cancellationToken);
    }
}

internal sealed class CoreBootstrapService(ICoreIdentityStore store, CoreIdentityService identityService) : ICoreBootstrapService
{
    public Task<bool> IsPendingAsync(CancellationToken cancellationToken) => store.IsBootstrapPendingAsync(cancellationToken);
    public Task<CoreBootstrapResult> CreateInitialAdministratorAsync(string username, string password, CancellationToken cancellationToken) =>
        identityService.CreateInitialAdministratorAsync(username, password, cancellationToken);
}
