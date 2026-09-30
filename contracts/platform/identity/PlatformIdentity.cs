namespace Hispania.Platform.Contracts.Identity;

public readonly record struct UserId(Guid Value) { public static UserId New() => new(Guid.NewGuid()); }
public readonly record struct SessionId(Guid Value) { public static SessionId New() => new(Guid.NewGuid()); }
public enum UserStatus { Enabled, Disabled }
public sealed record User(UserId Id, string Username, string DisplayName, UserStatus Status, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);
public sealed record Session(SessionId Id, UserId UserId, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt, DateTimeOffset? RevokedAt);
public sealed record AuthenticationResult(bool Succeeded, User? User, Session? Session, string? SessionToken)
{
    public static AuthenticationResult Failed() => new(false, null, null, null);
}
public sealed record IdentityOptions(TimeSpan? SessionLifetime = null)
{
    public TimeSpan EffectiveSessionLifetime => SessionLifetime ?? TimeSpan.FromHours(12);
}
public enum IdentityFailure { None, InvalidInput, DuplicateUsername, NotFound, Disabled, InvalidCredentials, InvalidSession }
public sealed record IdentityOperationResult<T>(T? Value, IdentityFailure Failure)
{
    public bool Succeeded => Failure == IdentityFailure.None;
}
public interface IIdentityQueryV1
{
    ValueTask<User?> GetUserAsync(UserId id, CancellationToken cancellationToken = default);
    ValueTask<User?> GetUserByUsernameAsync(string username, CancellationToken cancellationToken = default);
    ValueTask<IReadOnlyList<User>> ListUsersAsync(CancellationToken cancellationToken = default);
    ValueTask<Session?> ValidateSessionAsync(string token, CancellationToken cancellationToken = default);
    ValueTask<User?> GetCurrentUserAsync(string token, CancellationToken cancellationToken = default);
}
public interface IIdentityCommandV1
{
    ValueTask<IdentityOperationResult<User>> CreateUserAsync(string username, string displayName, string password, string? correlationId = null, CancellationToken cancellationToken = default);
    ValueTask<IdentityOperationResult<User>> SetUserStatusAsync(UserId id, UserStatus status, string? correlationId = null, CancellationToken cancellationToken = default);
    ValueTask<IdentityOperationResult<User>> UpdateDisplayNameAsync(UserId id, string displayName, string? correlationId = null, CancellationToken cancellationToken = default);
    ValueTask<IdentityOperationResult<User>> ChangePasswordAsync(UserId id, string newPassword, string? correlationId = null, CancellationToken cancellationToken = default);
    ValueTask<AuthenticationResult> LoginAsync(string username, string password, string? correlationId = null, CancellationToken cancellationToken = default);
    ValueTask<bool> LogoutAsync(string token, string? correlationId = null, CancellationToken cancellationToken = default);
}
