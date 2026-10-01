using System.Net.Http.Headers;
using Hispania.Platform.Contracts.Authorization;
using Hispania.Platform.Contracts.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace Hispania.Playback.Tests;

internal static class DomainAuthorizationTestHost
{
    public const string SessionToken = "integration-test-session-token-with-sufficient-length";
    private static readonly UserId User = new(Guid.Parse("00000000-0000-0000-0000-000000000001"));

    public static void AddAuthorizedUser(IServiceCollection services)
    {
        services.AddSingleton<IIdentityQueryV1, IdentityQuery>();
        services.AddSingleton<IAuthorizationServiceV1, AuthorizationService>();
    }

    public static void Authenticate(HttpClient client) => client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Session", SessionToken);

    private sealed class IdentityQuery : IIdentityQueryV1
    {
        public ValueTask<User?> GetUserAsync(UserId id, CancellationToken cancellationToken = default) => ValueTask.FromResult<User?>(id == User ? new User(User, "integration-test", "Integration Test", UserStatus.Enabled, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow) : null);
        public ValueTask<User?> GetUserByUsernameAsync(string username, CancellationToken cancellationToken = default) => ValueTask.FromResult<User?>(null);
        public ValueTask<IReadOnlyList<User>> ListUsersAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult<IReadOnlyList<User>>([]);
        public ValueTask<Session?> ValidateSessionAsync(string token, CancellationToken cancellationToken = default) => ValueTask.FromResult<Session?>(token == SessionToken ? new Session(SessionId.New(), User, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(1), null) : null);
        public ValueTask<User?> GetCurrentUserAsync(string token, CancellationToken cancellationToken = default) => ValueTask.FromResult<User?>(null);
    }

    private sealed class AuthorizationService : IAuthorizationServiceV1
    {
        private static readonly HashSet<string> Permissions = new([
            "media.library.read", "media.playback.use", "media.admin", "inventory.read", "inventory.write"
        ], StringComparer.Ordinal);
        public ValueTask<bool> HasPermissionAsync(UserId userId, string permissionId, CancellationToken cancellationToken = default) => ValueTask.FromResult(Permissions.Contains(permissionId));
        public ValueTask<IReadOnlySet<string>> GetEffectivePermissionsAsync(UserId userId, CancellationToken cancellationToken = default) => ValueTask.FromResult<IReadOnlySet<string>>(Permissions);
        public ValueTask<AuthorizationDecision> RequirePermissionAsync(UserId? userId, string permissionId, CancellationToken cancellationToken = default) => ValueTask.FromResult(userId is null ? new AuthorizationDecision(false, false) : new AuthorizationDecision(true, Permissions.Contains(permissionId)));
    }
}
