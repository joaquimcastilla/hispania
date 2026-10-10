using Hispania.Identity.Application;
using Hispania.Platform.Contracts.Audit;
using Hispania.Platform.Contracts.Identity;
using Hispania.Platform.Persistence.Sqlite;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Hispania.Inventory.Tests;

public sealed class PlatformIdentityTests
{
    [Fact]
    public async Task UserPasswordAndSessionSurviveStoreReconstructionAndRevocation()
    {
        using var directory = new TemporaryDirectory();
        var audit = new RecordingAuditWriter();
        var store = new SqliteIdentityStore(Path.Combine(directory.Path, "identity.sqlite"));
        var identity = new IdentityService(store, audit);
        var created = await identity.CreateUserAsync("Alice", "Alice Example", "correct horse battery staple", "trace-identity");
        Assert.True(created.Succeeded);
        var user = Assert.IsType<User>(created.Value);

        var login = await identity.LoginAsync("ALICE", "correct horse battery staple", "trace-identity");
        Assert.True(login.Succeeded);
        Assert.NotNull(login.SessionToken);
        Assert.Equal(user.Id, login.User!.Id);
        Assert.NotEqual("correct horse battery staple", (await store.GetCredentialAsync(user.Id, default))!.Hash);

        var persistedCredential = await store.GetCredentialAsync(user.Id, default);
        Assert.Equal("pbkdf2-sha256-v1", persistedCredential!.Algorithm);
        Assert.NotEqual("correct horse battery staple", persistedCredential.Salt);
        Assert.NotEqual("correct horse battery staple", persistedCredential.Hash);

        var reopened = new IdentityService(new SqliteIdentityStore(Path.Combine(directory.Path, "identity.sqlite")), audit);
        Assert.Equal(user.Id, (await reopened.GetUserByUsernameAsync("aLiCe"))!.Id);
        Assert.NotNull(await reopened.GetCurrentUserAsync(login.SessionToken));
        Assert.NotNull(await reopened.ValidateSessionAsync(login.SessionToken));
        Assert.True(await reopened.LogoutAsync(login.SessionToken));
        Assert.Null(await new IdentityService(new SqliteIdentityStore(Path.Combine(directory.Path, "identity.sqlite"))).ValidateSessionAsync(login.SessionToken));
        Assert.Contains(audit.Records, record => record.Action == "login-success" && record.CorrelationId == "trace-identity");
        Assert.Contains(audit.Records, record => record.Action == "user-created" && record.CorrelationId == "trace-identity");
        Assert.Contains(audit.Records, record => record.Action == "logout");
        Assert.Contains(audit.Records, record => record.Action == "session-revoked");
        Assert.DoesNotContain(audit.Records.SelectMany(record => record.Metadata), item => item.Key.Contains("token", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task UsernameIsCaseInsensitiveAndDatabasePreventsConcurrentDuplicates()
    {
        using var directory = new TemporaryDirectory();
        var identity = new IdentityService(new SqliteIdentityStore(Path.Combine(directory.Path, "identity.sqlite")));
        Assert.Equal(IdentityFailure.InvalidInput, (await identity.CreateUserAsync("ab", "Short", "a sufficiently long passphrase")).Failure);
        Assert.Equal(IdentityFailure.InvalidInput, (await identity.CreateUserAsync("too-short", "Short", "tiny")).Failure);
        Assert.Equal(IdentityFailure.InvalidInput, (await identity.CreateUserAsync("Kevin", "Kelvin", "long enough passphrase")).Failure);
        var first = await identity.CreateUserAsync("Alice", "Alice", "a sufficiently long passphrase");
        Assert.True(first.Succeeded);
        Assert.Equal("alice", first.Value!.Username);
        Assert.Equal(IdentityFailure.DuplicateUsername, (await identity.CreateUserAsync("ALICE", "Other", "another sufficiently long passphrase")).Failure);

        var outcomes = await Task.WhenAll(Enumerable.Range(0, 2).Select(index => identity.CreateUserAsync("parallel", $"Parallel {index}", "parallel sufficiently long passphrase").AsTask()));
        Assert.Single(outcomes, outcome => outcome.Succeeded);
        Assert.Single(outcomes, outcome => outcome.Failure == IdentityFailure.DuplicateUsername);
    }

    [Fact]
    public async Task PasswordChangeAndDisabledUserInvalidateAuthenticationAndSessions()
    {
        using var directory = new TemporaryDirectory();
        var audit = new RecordingAuditWriter();
        var identity = new IdentityService(new SqliteIdentityStore(Path.Combine(directory.Path, "identity.sqlite")), audit);
        var created = await identity.CreateUserAsync("bob", "Bob", "old sufficiently long password");
        var user = created.Value!;
        var login = await identity.LoginAsync("bob", "old sufficiently long password");
        Assert.True(login.Succeeded);
        Assert.True((await identity.ChangePasswordAsync(user.Id, "new sufficiently long password")).Succeeded);
        Assert.False((await identity.LoginAsync("bob", "old sufficiently long password")).Succeeded);
        Assert.True((await identity.LoginAsync("bob", "new sufficiently long password")).Succeeded);
        Assert.True((await identity.SetUserStatusAsync(user.Id, UserStatus.Disabled)).Succeeded);
        Assert.False((await identity.LoginAsync("bob", "new sufficiently long password")).Succeeded);
        Assert.Null(await identity.ValidateSessionAsync(login.SessionToken!));
        Assert.True((await identity.SetUserStatusAsync(user.Id, UserStatus.Enabled)).Succeeded);
        Assert.True((await identity.UpdateDisplayNameAsync(user.Id, "Robert")).Succeeded);
        Assert.Equal("Robert", (await identity.GetUserAsync(user.Id))!.DisplayName);
        Assert.True((await identity.LoginAsync("bob", "new sufficiently long password")).Succeeded);
        Assert.Contains(audit.Records, record => record.Action == "user-disabled");
        Assert.Contains(audit.Records, record => record.Action == "user-enabled");
        Assert.Contains(audit.Records, record => record.Action == "password-changed");
        Assert.Contains(audit.Records, record => record.Action == "login-failure");
    }

    [Fact]
    public async Task LoginFailuresAreGenericAndSessionsExpire()
    {
        using var directory = new TemporaryDirectory();
        var identity = new IdentityService(new SqliteIdentityStore(Path.Combine(directory.Path, "identity.sqlite")), options: new(TimeSpan.FromMilliseconds(1)));
        await identity.CreateUserAsync("carol", "Carol", "a sufficiently long passphrase");
        var wrongPassword = await identity.LoginAsync("carol", "wrong sufficiently long password");
        var unknownUser = await identity.LoginAsync("missing", "wrong sufficiently long password");
        Assert.Equal(wrongPassword.Succeeded, unknownUser.Succeeded);
        var successful = await identity.LoginAsync("carol", "a sufficiently long passphrase");
        await Task.Delay(30);
        Assert.Null(await identity.ValidateSessionAsync(successful.SessionToken!));
    }

    [Fact]
    public void IdentityAssembliesHaveNoDomainImplementationDependencies()
    {
        AssertNoDomainReference(typeof(IIdentityQueryV1).Assembly);
        AssertNoDomainReference(typeof(IdentityService).Assembly);
        AssertNoDomainReference(typeof(SqliteIdentityStore).Assembly);
    }

    private static void AssertNoDomainReference(System.Reflection.Assembly assembly)
    {
        var forbidden = new[] { "Media", "Inventory" };
        Assert.DoesNotContain(assembly.GetReferencedAssemblies(), reference => forbidden.Any(name => reference.Name?.Contains(name, StringComparison.OrdinalIgnoreCase) == true));
    }

    private sealed class RecordingAuditWriter : IPlatformAuditWriter
    {
        public List<AuditRecord> Records { get; } = [];
        public ValueTask WriteAsync(AuditRecord record, CancellationToken cancellationToken = default) { Records.Add(record); return ValueTask.CompletedTask; }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "hispania-identity-" + Guid.NewGuid().ToString("N"));
        public TemporaryDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() { SqliteConnection.ClearAllPools(); Directory.Delete(Path, recursive: true); }
    }
}
