using Hispania.Platform.Contracts.Observability;
using System.Security.Cryptography;
using System.Text;
using Hispania.Platform.Contracts.Audit;
using Hispania.Platform.Contracts.Identity;

namespace Hispania.Identity.Application;

public sealed record StoredCredential(string Algorithm, int Iterations, string Salt, string Hash);
public sealed record StoredSession(Session Session, string TokenHash);
public interface IIdentityStore
{
    ValueTask<User?> GetUserAsync(UserId id, CancellationToken cancellationToken);
    ValueTask<User?> GetUserByUsernameAsync(string normalizedUsername, CancellationToken cancellationToken);
    ValueTask<IReadOnlyList<User>> ListUsersAsync(CancellationToken cancellationToken);
    ValueTask<bool> CreateUserAsync(User user, StoredCredential credential, CancellationToken cancellationToken);
    ValueTask<bool> SetUserAsync(User user, CancellationToken cancellationToken);
    ValueTask<StoredCredential?> GetCredentialAsync(UserId userId, CancellationToken cancellationToken);
    ValueTask SetCredentialAsync(UserId userId, StoredCredential credential, CancellationToken cancellationToken);
    ValueTask<StoredSession?> GetSessionByTokenHashAsync(string tokenHash, CancellationToken cancellationToken);
    ValueTask<StoredSession?> GetSessionAsync(SessionId sessionId, CancellationToken cancellationToken);
    ValueTask<bool> CreateSessionAsync(StoredSession session, CancellationToken cancellationToken);
    ValueTask<bool> RevokeSessionAsync(SessionId sessionId, DateTimeOffset revokedAt, CancellationToken cancellationToken);
}

public sealed class IdentityService : IIdentityCommandV1, IIdentityQueryV1
{
    private const int Iterations = 600_000;
    private const int SaltBytes = 16;
    private const int HashBytes = 32;
    private const int MinimumPasswordLength = 12;
    private const int MaximumPasswordLength = 1024;
    private static readonly string DummyHash = HashPassword("identity-timing-placeholder").Hash;
    private readonly IPlatformMetricsV1? metrics;
    private readonly IIdentityStore store;
    private readonly IPlatformAuditWriter? audit;
    private readonly IdentityOptions _options;

    public IdentityService(IIdentityStore store, IPlatformAuditWriter? audit = null, IdentityOptions? options = null, IPlatformMetricsV1? metrics = null)
    {
        this.metrics = metrics;
        this.store = store;
        this.audit = audit;
        _options = options ?? new();
        if (_options.EffectiveSessionLifetime <= TimeSpan.Zero || _options.EffectiveSessionLifetime > TimeSpan.FromDays(365))
            throw new ArgumentOutOfRangeException(nameof(options), "Session lifetime must be greater than zero and no longer than 365 days.");
    }

    public ValueTask<User?> GetUserAsync(UserId id, CancellationToken cancellationToken = default) => store.GetUserAsync(id, cancellationToken);
    public async ValueTask<User?> GetUserByUsernameAsync(string username, CancellationToken cancellationToken = default) =>
        TryNormalizeUsername(username, out var normalized) ? await store.GetUserByUsernameAsync(normalized, cancellationToken).ConfigureAwait(false) : null;
    public ValueTask<IReadOnlyList<User>> ListUsersAsync(CancellationToken cancellationToken = default) => store.ListUsersAsync(cancellationToken);

    public async ValueTask<IdentityOperationResult<User>> CreateUserAsync(string username, string displayName, string password, string? correlationId = null, CancellationToken cancellationToken = default)
    {
        if (!TryNormalizeUsername(username, out var normalized) || string.IsNullOrWhiteSpace(displayName) || displayName.Trim().Length > 200 || !ValidPassword(password)) return new(null, IdentityFailure.InvalidInput);
        var now = DateTimeOffset.UtcNow;
        var user = new User(UserId.New(), normalized, displayName.Trim(), UserStatus.Enabled, now, now);
        if (!await store.CreateUserAsync(user, HashPassword(password), cancellationToken).ConfigureAwait(false)) return new(null, IdentityFailure.DuplicateUsername);
        await WriteAudit(user.Id, "user-created", "success", null, correlationId, cancellationToken).ConfigureAwait(false);
        return new(user, IdentityFailure.None);
    }

    public async ValueTask<IdentityOperationResult<User>> SetUserStatusAsync(UserId id, UserStatus status, string? correlationId = null, CancellationToken cancellationToken = default)
    {
        var user = await store.GetUserAsync(id, cancellationToken).ConfigureAwait(false);
        if (user is null) return new(null, IdentityFailure.NotFound);
        if (!Enum.IsDefined(status)) return new(null, IdentityFailure.InvalidInput);
        var updated = user with { Status = status, UpdatedAt = DateTimeOffset.UtcNow };
        if (!await store.SetUserAsync(updated, cancellationToken).ConfigureAwait(false)) return new(null, IdentityFailure.NotFound);
        await WriteAudit(id, status == UserStatus.Enabled ? "user-enabled" : "user-disabled", "success", null, correlationId, cancellationToken).ConfigureAwait(false);
        return new(updated, IdentityFailure.None);
    }

    public async ValueTask<IdentityOperationResult<User>> UpdateDisplayNameAsync(UserId id, string displayName, string? correlationId = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(displayName) || displayName.Trim().Length > 200) return new(null, IdentityFailure.InvalidInput);
        var user = await store.GetUserAsync(id, cancellationToken).ConfigureAwait(false);
        if (user is null) return new(null, IdentityFailure.NotFound);
        var updated = user with { DisplayName = displayName.Trim(), UpdatedAt = DateTimeOffset.UtcNow };
        if (!await store.SetUserAsync(updated, cancellationToken).ConfigureAwait(false)) return new(null, IdentityFailure.NotFound);
        await WriteAudit(id, "display-name-changed", "success", null, correlationId, cancellationToken).ConfigureAwait(false);
        return new(updated, IdentityFailure.None);
    }

    public async ValueTask<IdentityOperationResult<User>> ChangePasswordAsync(UserId id, string newPassword, string? correlationId = null, CancellationToken cancellationToken = default)
    {
        if (!ValidPassword(newPassword)) return new(null, IdentityFailure.InvalidInput);
        var user = await store.GetUserAsync(id, cancellationToken).ConfigureAwait(false);
        if (user is null) return new(null, IdentityFailure.NotFound);
        await store.SetCredentialAsync(id, HashPassword(newPassword), cancellationToken).ConfigureAwait(false);
        var updated = user with { UpdatedAt = DateTimeOffset.UtcNow };
        await store.SetUserAsync(updated, cancellationToken).ConfigureAwait(false);
        await WriteAudit(id, "password-changed", "success", null, correlationId, cancellationToken).ConfigureAwait(false);
        return new(updated, IdentityFailure.None);
    }

    public async ValueTask<AuthenticationResult> LoginAsync(string username, string password, string? correlationId = null, CancellationToken cancellationToken = default)
    {
        // Reject before UTF-8 conversion and PBKDF2; never silently truncate credentials.
        if (password is null || password.Length > MaximumPasswordLength)
        {
            metrics.BestEffort(sink => sink.Increment("hispania.identity.login.failure"));
            return AuthenticationResult.Failed();
        }
        correlationId ??= PlatformCorrelationV1.Current?.CorrelationId;
        var normalized = TryNormalizeUsername(username, out var name) ? name : string.Empty;
        var user = normalized.Length == 0 ? null : await store.GetUserByUsernameAsync(normalized, cancellationToken).ConfigureAwait(false);
        var credential = user is null ? null : await store.GetCredentialAsync(user.Id, cancellationToken).ConfigureAwait(false);
        var valid = VerifyPassword(password ?? string.Empty, credential ?? ParseCredential(DummyHash));
        if (user is null || credential is null || !valid || user.Status != UserStatus.Enabled)
        {
            metrics.BestEffort(sink => sink.Increment("hispania.identity.login.failure"));
            await WriteAudit(user?.Id, "login-failure", "failure", null, correlationId, cancellationToken).ConfigureAwait(false);
            return AuthenticationResult.Failed();
        }
        var rawToken = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var now = DateTimeOffset.UtcNow;
        var session = new Session(SessionId.New(), user.Id, now, now + _options.EffectiveSessionLifetime, null);
        if (!await store.CreateSessionAsync(new(session, TokenHash(rawToken)), cancellationToken).ConfigureAwait(false)) return AuthenticationResult.Failed();
        metrics.BestEffort(sink => sink.Increment("hispania.identity.login.success"));
        await WriteAudit(user.Id, "login-success", "success", session.Id.Value.ToString("D"), correlationId, cancellationToken).ConfigureAwait(false);
        return new(true, user, session, rawToken);
    }

    public async ValueTask<Session?> ValidateSessionAsync(string token, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 256) return null;
        var stored = await store.GetSessionByTokenHashAsync(TokenHash(token), cancellationToken).ConfigureAwait(false);
        if (stored is null || stored.Session.RevokedAt is not null || stored.Session.ExpiresAt <= DateTimeOffset.UtcNow) return null;
        var user = await store.GetUserAsync(stored.Session.UserId, cancellationToken).ConfigureAwait(false);
        return user?.Status == UserStatus.Enabled ? stored.Session : null;
    }

    public async ValueTask<User?> GetCurrentUserAsync(string token, CancellationToken cancellationToken = default)
    {
        var session = await ValidateSessionAsync(token, cancellationToken).ConfigureAwait(false);
        return session is null ? null : await store.GetUserAsync(session.UserId, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<bool> LogoutAsync(string token, string? correlationId = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 256) return false;
        var stored = await store.GetSessionByTokenHashAsync(TokenHash(token), cancellationToken).ConfigureAwait(false);
        if (stored is null) return false;
        var revoked = await store.RevokeSessionAsync(stored.Session.Id, DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false);
        if (revoked)
        {
            var sessionId = stored.Session.Id.Value.ToString("D");
            await WriteAudit(stored.Session.UserId, "session-revoked", "success", sessionId, correlationId, cancellationToken).ConfigureAwait(false);
            await WriteAudit(stored.Session.UserId, "logout", "success", sessionId, correlationId, cancellationToken).ConfigureAwait(false);
        }
        return revoked;
    }

    private async ValueTask WriteAudit(UserId? userId, string action, string result, string? sessionId, string? correlationId, CancellationToken cancellationToken)
    {
        correlationId ??= PlatformCorrelationV1.Current?.CorrelationId;
        if (audit is null) return;
        var metadata = sessionId is null ? new Dictionary<string, string>() : new Dictionary<string, string> { ["sessionId"] = sessionId };
        await audit.WriteAsync(new(DateTimeOffset.UtcNow, userId?.Value.ToString("D"), correlationId, "platform.identity", action, "user", userId?.Value.ToString("D") ?? "unknown", result, metadata), cancellationToken).ConfigureAwait(false);
    }

    private static bool TryNormalizeUsername(string? value, out string normalized)
    {
        var trimmed = value?.Trim() ?? string.Empty;
        var valid = trimmed.Length is >= 3 and <= 64 && trimmed.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-');
        normalized = valid ? trimmed.ToLowerInvariant() : string.Empty;
        return valid;
    }
    private static bool ValidPassword(string? password) => password is not null && password.Length is >= MinimumPasswordLength and <= MaximumPasswordLength;
    private static StoredCredential HashPassword(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, Iterations, HashAlgorithmName.SHA256, HashBytes);
        return new("pbkdf2-sha256-v1", Iterations, Convert.ToBase64String(salt), Convert.ToBase64String(hash));
    }
    private static bool VerifyPassword(string password, StoredCredential credential)
    {
        if (credential.Algorithm != "pbkdf2-sha256-v1" || credential.Iterations is < 100_000 or > 2_000_000) return false;
        try
        {
            var expected = Convert.FromBase64String(credential.Hash);
            var salt = Convert.FromBase64String(credential.Salt);
            if (expected.Length != HashBytes || salt.Length != SaltBytes) return false;
            var actual = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, credential.Iterations, HashAlgorithmName.SHA256, expected.Length);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (FormatException) { return false; }
    }
    private static StoredCredential ParseCredential(string hash) => new("pbkdf2-sha256-v1", Iterations, Convert.ToBase64String(new byte[SaltBytes]), hash);
    private static string TokenHash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}
