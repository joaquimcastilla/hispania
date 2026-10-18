using Hispania.Core.Hosting;
using Hispania.Core.Identity;
using Hispania.Core.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.Data.Sqlite;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Hispania.Core.Tests;

public sealed class CoreIdentityTests
{
    private const string LongPassword = "correct horse battery staple 42!";

    [Fact]
    public async Task BootstrapIsExplicitSingleUseAndPersistsIdentityCredentialAndSessionState()
    {
        using var directory = new TemporaryDirectory();
        await using var app = Build(directory.Path);
        var persistence = app.Services.GetRequiredService<ICorePersistence>();
        await persistence.InitializeAsync(CancellationToken.None);
        var bootstrap = app.Services.GetRequiredService<ICoreBootstrapService>();
        var identities = app.Services.GetRequiredService<ICoreIdentityService>();
        var authorization = app.Services.GetRequiredService<ICoreAuthorizationService>();
        var sessions = app.Services.GetRequiredService<ICoreSessionService>();

        Assert.True(await bootstrap.IsPendingAsync(CancellationToken.None));
        Assert.Equal(CoreBootstrapResult.Created, await bootstrap.CreateInitialAdministratorAsync("Owner.Admin", LongPassword, CancellationToken.None));
        Assert.False(await bootstrap.IsPendingAsync(CancellationToken.None));
        Assert.Equal(CoreBootstrapResult.AlreadyCompleted, await bootstrap.CreateInitialAdministratorAsync("Other.Admin", LongPassword, CancellationToken.None));

        var authenticated = await identities.AuthenticateAsync("owner.admin", LongPassword, CancellationToken.None);
        Assert.True(authenticated.Succeeded);
        var admin = authenticated.Identity!;
        Assert.True(await authorization.IsAuthorizedAsync(admin.SubjectId, CoreIdentityRules.IdentityManagePermission, CancellationToken.None));
        Assert.False(await authorization.IsAuthorizedAsync(Guid.NewGuid(), "core.unknown", CancellationToken.None));

        var session = await sessions.CreateAsync(admin.SubjectId, DateTimeOffset.UtcNow, TimeSpan.FromHours(1), CancellationToken.None);
        Assert.NotNull(session);
        Assert.True((await sessions.ValidateAsync(session!.Token, DateTimeOffset.UtcNow, CancellationToken.None)).IsValid);
        Assert.False((await sessions.ValidateAsync(session.Token, session.ExpiresUtc, CancellationToken.None)).IsValid);
        Assert.True(await sessions.RevokeAsync(admin.SubjectId, session.Token, DateTimeOffset.UtcNow, CancellationToken.None));
        Assert.False((await sessions.ValidateAsync(session.Token, DateTimeOffset.UtcNow, CancellationToken.None)).IsValid);

        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(directory.Path, "core.db"),
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT c.password_hash, hex(s.token_hash) FROM core_credentials c JOIN core_sessions s ON s.subject_id = c.subject_id LIMIT 1;";
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        var hash = reader.GetString(0);
        var tokenHash = reader.GetString(1);
        Assert.DoesNotContain(LongPassword, hash, StringComparison.Ordinal);
        Assert.DoesNotContain(session.Token, tokenHash, StringComparison.Ordinal);
        Assert.NotEqual(session.Token, hash);
    }

    [Fact]
    public async Task DefaultDenyAndDisabledAccountRevokeSessionsAndCredentials()
    {
        using var directory = new TemporaryDirectory();
        await using var app = Build(directory.Path);
        await app.Services.GetRequiredService<ICorePersistence>().InitializeAsync(CancellationToken.None);
        var bootstrap = app.Services.GetRequiredService<ICoreBootstrapService>();
        var identity = app.Services.GetRequiredService<ICoreIdentityService>();
        var authz = app.Services.GetRequiredService<ICoreAuthorizationService>();
        var sessions = app.Services.GetRequiredService<ICoreSessionService>();
        await bootstrap.CreateInitialAdministratorAsync("owner", LongPassword, CancellationToken.None);
        var admin = (await identity.AuthenticateAsync("OWNER", LongPassword, CancellationToken.None)).Identity!;

        Assert.False(await authz.IsAuthorizedAsync(Guid.NewGuid(), CoreIdentityRules.IdentityReadPermission, CancellationToken.None));
        Assert.False((await identity.AuthenticateAsync("missing", LongPassword, CancellationToken.None)).Succeeded);
        Assert.False((await identity.AuthenticateAsync("owner", "wrong password value", CancellationToken.None)).Succeeded);

        var session = await sessions.CreateAsync(admin.SubjectId, DateTimeOffset.UtcNow, TimeSpan.FromMinutes(5), CancellationToken.None);
        Assert.NotNull(session);
        Assert.False(await identity.SetAccountStatusAsync(Guid.NewGuid(), admin.SubjectId, CoreAccountStatus.Disabled, CancellationToken.None));
        Assert.False(await identity.SetAccountStatusAsync(admin.SubjectId, admin.SubjectId, CoreAccountStatus.Disabled, CancellationToken.None));
        Assert.True((await sessions.ValidateAsync(session!.Token, DateTimeOffset.UtcNow, CancellationToken.None)).IsValid);
    }

    [Fact]
    public async Task IdentityNormalizationAndPasswordHashingUseIndependentCredentialSalts()
    {
        using var directory = new TemporaryDirectory();
        await using var app = Build(directory.Path);
        await app.Services.GetRequiredService<ICorePersistence>().InitializeAsync(CancellationToken.None);
        var bootstrap = app.Services.GetRequiredService<ICoreBootstrapService>();
        var identity = app.Services.GetRequiredService<ICoreIdentityService>();
        var authorization = app.Services.GetRequiredService<ICoreAuthorizationService>();
        var sessions = app.Services.GetRequiredService<ICoreSessionService>();
        await bootstrap.CreateInitialAdministratorAsync("owner", LongPassword, CancellationToken.None);
        var owner = (await identity.AuthenticateAsync("owner", LongPassword, CancellationToken.None)).Identity!;

        const string userPassword = "different password long enough 43$";
        var one = await identity.CreateLocalAccountAsync(owner.SubjectId, " Alice ", userPassword, CancellationToken.None);
        var peer = await identity.CreateLocalAccountAsync(owner.SubjectId, "bob", userPassword, CancellationToken.None);
        var two = await identity.CreateLocalAccountAsync(owner.SubjectId, "alice", userPassword, CancellationToken.None);
        Assert.NotNull(one);
        Assert.NotNull(peer);
        Assert.Null(two);
        Assert.True((await identity.AuthenticateAsync("ALICE", userPassword, CancellationToken.None)).Succeeded);
        Assert.False((await identity.AuthenticateAsync("alice", "not the password", CancellationToken.None)).Succeeded);

        var session = await sessions.CreateAsync(one!.SubjectId, DateTimeOffset.UtcNow, TimeSpan.FromMinutes(5), CancellationToken.None);
        Assert.NotNull(session);
        Assert.True(await identity.SetAccountStatusAsync(owner.SubjectId, one.SubjectId, CoreAccountStatus.Disabled, CancellationToken.None));
        Assert.False((await identity.AuthenticateAsync("alice", userPassword, CancellationToken.None)).Succeeded);
        Assert.False((await sessions.ValidateAsync(session!.Token, DateTimeOffset.UtcNow, CancellationToken.None)).IsValid);
        Assert.False(await authorization.IsAuthorizedAsync(one.SubjectId, CoreIdentityRules.IdentityManagePermission, CancellationToken.None));

        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(directory.Path, "core.db"), Mode = SqliteOpenMode.ReadOnly, Pooling = false
        }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT c.password_hash FROM core_credentials c JOIN core_identities i ON i.subject_id = c.subject_id WHERE i.username_normalized IN ('ALICE', 'BOB') ORDER BY i.username_normalized;";
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        var firstHash = reader.GetString(0);
        Assert.True(await reader.ReadAsync());
        var secondHash = reader.GetString(0);
        Assert.NotEqual(firstHash, secondHash);
    }

    [Fact]
    public async Task ConcurrentBootstrapHasExactlyOneWinner()
    {
        using var directory = new TemporaryDirectory();
        await using var app = Build(directory.Path);
        await app.Services.GetRequiredService<ICorePersistence>().InitializeAsync(CancellationToken.None);
        var bootstrap = app.Services.GetRequiredService<ICoreBootstrapService>();
        var results = await Task.WhenAll(
            bootstrap.CreateInitialAdministratorAsync("owner-one", LongPassword, CancellationToken.None),
            bootstrap.CreateInitialAdministratorAsync("owner-two", LongPassword, CancellationToken.None));
        Assert.Equal(1, results.Count(result => result == CoreBootstrapResult.Created));
        Assert.Equal(1, results.Count(result => result == CoreBootstrapResult.AlreadyCompleted));
    }

    [Fact]
    public async Task LocalRecoveryInvalidatesAllPersistedSessions()
    {
        using var directory = new TemporaryDirectory();
        await using var app = Build(directory.Path);
        await app.Services.GetRequiredService<ICorePersistence>().InitializeAsync(CancellationToken.None);
        await app.Services.GetRequiredService<ICoreBootstrapService>().CreateInitialAdministratorAsync("owner", LongPassword, CancellationToken.None);
        var admin = (await app.Services.GetRequiredService<ICoreIdentityService>().AuthenticateAsync("owner", LongPassword, CancellationToken.None)).Identity!;
        var sessions = app.Services.GetRequiredService<ICoreSessionService>();
        var session = await sessions.CreateAsync(admin.SubjectId, DateTimeOffset.UtcNow, TimeSpan.FromHours(1), CancellationToken.None);
        Assert.NotNull(session);
        Assert.Equal(1, await sessions.InvalidateAllForLocalRecoveryAsync(DateTimeOffset.UtcNow, CancellationToken.None));
        Assert.False((await sessions.ValidateAsync(session!.Token, DateTimeOffset.UtcNow, CancellationToken.None)).IsValid);
        Assert.Equal(0, await sessions.InvalidateAllForLocalRecoveryAsync(DateTimeOffset.UtcNow, CancellationToken.None));
    }

    [Fact]
    public async Task FailedBootstrapRollsBackAllIdentityAndMarkerChanges()
    {
        using var directory = new TemporaryDirectory();
        await using var app = Build(directory.Path);
        await app.Services.GetRequiredService<ICorePersistence>().InitializeAsync(CancellationToken.None);
        await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(directory.Path, "core.db"), Mode = SqliteOpenMode.ReadWrite, Pooling = false
        }.ToString()))
        {
            await connection.OpenAsync();
            await using var trigger = connection.CreateCommand();
            trigger.CommandText = "CREATE TRIGGER reject_bootstrap_audit BEFORE INSERT ON core_security_audit WHEN NEW.event_code = 'bootstrap.admin.completed' BEGIN SELECT RAISE(ABORT, 'injected failure'); END;";
            await trigger.ExecuteNonQueryAsync();
        }

        var bootstrap = app.Services.GetRequiredService<ICoreBootstrapService>();
        Assert.Equal(CoreBootstrapResult.Failed, await bootstrap.CreateInitialAdministratorAsync("owner", LongPassword, CancellationToken.None));
        Assert.True(await bootstrap.IsPendingAsync(CancellationToken.None));
        await using var verify = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(directory.Path, "core.db"), Mode = SqliteOpenMode.ReadOnly, Pooling = false
        }.ToString());
        await verify.OpenAsync();
        await using var query = verify.CreateCommand();
        query.CommandText = "SELECT (SELECT COUNT(*) FROM core_identities), (SELECT COUNT(*) FROM core_credentials), (SELECT COUNT(*) FROM core_subject_roles), (SELECT bootstrap_completed_utc IS NULL FROM core_instance WHERE singleton_id = 1);";
        await using var reader = await query.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(0, reader.GetInt32(0));
        Assert.Equal(0, reader.GetInt32(1));
        Assert.Equal(0, reader.GetInt32(2));
        Assert.Equal(1, reader.GetInt32(3));
    }

    [Fact]
    public async Task CorePersistenceMigratesRealSlice4SchemaWithoutChangingInstallationIdentity()
    {
        using var directory = new TemporaryDirectory();
        Directory.CreateDirectory(directory.Path);
        var installationId = Guid.NewGuid();
        await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(directory.Path, "core.db"),
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ToString()))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE core_schema_migrations (version INTEGER NOT NULL PRIMARY KEY CHECK (version > 0), applied_utc TEXT NOT NULL); INSERT INTO core_schema_migrations VALUES (1, '2026-10-17T00:00:00.0000000+00:00'); CREATE TABLE core_instance (singleton_id INTEGER NOT NULL PRIMARY KEY CHECK (singleton_id = 1), installation_id TEXT NOT NULL UNIQUE, created_utc TEXT NOT NULL); INSERT INTO core_instance VALUES (1, $id, '2026-10-17T00:00:00.0000000+00:00');";
            command.Parameters.AddWithValue("$id", installationId.ToString("N"));
            await command.ExecuteNonQueryAsync();
        }

        await using var app = Build(directory.Path);
        await app.Services.GetRequiredService<ICorePersistence>().InitializeAsync(CancellationToken.None);
        var state = await app.Services.GetRequiredService<ICorePersistence>().GetInstanceStateAsync(CancellationToken.None);
        Assert.Equal(installationId, state.InstallationId);
        await using var migrated = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(directory.Path, "core.db"), Mode = SqliteOpenMode.ReadOnly, Pooling = false
        }.ToString());
        await migrated.OpenAsync();
        await using var verify = migrated.CreateCommand();
        verify.CommandText = "SELECT (SELECT COUNT(*) FROM core_schema_migrations), (SELECT COUNT(*) FROM core_identities), (SELECT COUNT(*) FROM core_bootstrap_state);";
        await using var reader = await verify.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(3, reader.GetInt32(0));
        Assert.Equal(0, reader.GetInt32(1));
        Assert.Equal(1, reader.GetInt32(2));
    }

    [Fact]
    public async Task ExplicitRolePermissionGrantWorksAndNonAdministratorCannotEscalate()
    {
        using var directory = new TemporaryDirectory();
        await using var app = Build(directory.Path);
        await app.Services.GetRequiredService<ICorePersistence>().InitializeAsync(CancellationToken.None);
        var bootstrap = app.Services.GetRequiredService<ICoreBootstrapService>();
        var identities = app.Services.GetRequiredService<ICoreIdentityService>();
        var authorization = app.Services.GetRequiredService<ICoreAuthorizationService>();
        await bootstrap.CreateInitialAdministratorAsync("owner", LongPassword, CancellationToken.None);
        var owner = (await identities.AuthenticateAsync("owner", LongPassword, CancellationToken.None)).Identity!;
        var user = await identities.CreateLocalAccountAsync(owner.SubjectId, "reader", "reader password is long 2026!", CancellationToken.None);
        Assert.NotNull(user);
        Assert.False(await authorization.IsAuthorizedAsync(user!.SubjectId, "module.catalog.read", CancellationToken.None));
        Assert.Null(await identities.CreateLocalAccountAsync(user.SubjectId, "escalated", LongPassword, CancellationToken.None));
        Assert.False(await authorization.AssignRoleAsync(owner.SubjectId, user.SubjectId, "role.missing", CancellationToken.None));
        Assert.False(await authorization.CreateRoleAsync(user.SubjectId, "role.reader", CancellationToken.None));
        Assert.False(await authorization.RegisterPermissionAsync(user.SubjectId, "module.catalog.read", CancellationToken.None));
        Assert.True(await authorization.CreateRoleAsync(owner.SubjectId, "role.reader", CancellationToken.None));
        Assert.True(await authorization.RegisterPermissionAsync(owner.SubjectId, "module.catalog.read", CancellationToken.None));
        Assert.True(await authorization.GrantPermissionToRoleAsync(owner.SubjectId, "role.reader", "module.catalog.read", CancellationToken.None));
        Assert.True(await authorization.AssignRoleAsync(owner.SubjectId, user.SubjectId, "role.reader", CancellationToken.None));
        Assert.True(await authorization.IsAuthorizedAsync(user.SubjectId, "module.catalog.read", CancellationToken.None));
        Assert.False(await authorization.AssignRoleAsync(user.SubjectId, owner.SubjectId, CoreIdentityRules.AdministratorRole, CancellationToken.None));
    }

    [Fact]
    public async Task AuthenticationRehashesOlderSupportedCostAndSurvivesCoreRestart()
    {
        using var directory = new TemporaryDirectory();
        Guid subjectId;
        await using (var first = Build(directory.Path))
        {
            await first.Services.GetRequiredService<ICorePersistence>().InitializeAsync(CancellationToken.None);
            var bootstrap = first.Services.GetRequiredService<ICoreBootstrapService>();
            var identities = first.Services.GetRequiredService<ICoreIdentityService>();
            await bootstrap.CreateInitialAdministratorAsync("owner", LongPassword, CancellationToken.None);
            var owner = (await identities.AuthenticateAsync("owner", LongPassword, CancellationToken.None)).Identity!;
            var account = await identities.CreateLocalAccountAsync(owner.SubjectId, "persisted", "persisted password long 2026!", CancellationToken.None);
            Assert.NotNull(account);
            subjectId = account!.SubjectId;

            var oldHasher = new PasswordHasher<CoreIdentity>(Microsoft.Extensions.Options.Options.Create(new PasswordHasherOptions
            {
                CompatibilityMode = PasswordHasherCompatibilityMode.IdentityV3,
                IterationCount = 100_000
            }));
            var oldHash = oldHasher.HashPassword(account, "persisted password long 2026!");
            await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = Path.Combine(directory.Path, "core.db"), Mode = SqliteOpenMode.ReadWrite, Pooling = false
            }.ToString());
            await connection.OpenAsync();
            await using var update = connection.CreateCommand();
            update.CommandText = "UPDATE core_credentials SET iterations = 100000, password_hash = $hash WHERE subject_id = $id;";
            update.Parameters.AddWithValue("$hash", oldHash);
            update.Parameters.AddWithValue("$id", subjectId.ToString("N"));
            await update.ExecuteNonQueryAsync();

            Assert.True((await identities.AuthenticateAsync("persisted", "persisted password long 2026!", CancellationToken.None)).Succeeded);
        }

        await using var restarted = Build(directory.Path);
        await restarted.Services.GetRequiredService<ICorePersistence>().InitializeAsync(CancellationToken.None);
        var afterRestart = await restarted.Services.GetRequiredService<ICoreIdentityService>().AuthenticateAsync("persisted", "persisted password long 2026!", CancellationToken.None);
        Assert.True(afterRestart.Succeeded);
        Assert.Equal(subjectId, afterRestart.Identity!.SubjectId);
        await using var verifyConnection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(directory.Path, "core.db"), Mode = SqliteOpenMode.ReadOnly, Pooling = false
        }.ToString());
        await verifyConnection.OpenAsync();
        await using var verify = verifyConnection.CreateCommand();
        verify.CommandText = "SELECT iterations FROM core_credentials WHERE subject_id = $id;";
        verify.Parameters.AddWithValue("$id", subjectId.ToString("N"));
        Assert.Equal(220_000, Convert.ToInt32(await verify.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture));
        await verifyConnection.CloseAsync();
        await using var writable = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(directory.Path, "core.db"), Mode = SqliteOpenMode.ReadWrite, Pooling = false
        }.ToString());
        await writable.OpenAsync();
        await using var malformed = writable.CreateCommand();
        malformed.CommandText = "UPDATE core_credentials SET password_hash = 'malformed' WHERE subject_id = $id;";
        malformed.Parameters.AddWithValue("$id", subjectId.ToString("N"));
        await malformed.ExecuteNonQueryAsync();
        Assert.False((await restarted.Services.GetRequiredService<ICoreIdentityService>().AuthenticateAsync("persisted", "persisted password long 2026!", CancellationToken.None)).Succeeded);
    }

    private static WebApplication Build(string dataDirectory) => CoreHost.Build(new Dictionary<string, string?>
    {
        ["LISTEN_ADDRESS"] = "127.0.0.1",
        ["PORT"] = "5070",
        ["DATA_DIRECTORY"] = dataDirectory
    }, httpEnabled: false);

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "hispania-core-identity-tests", Guid.NewGuid().ToString("N"));
        public void Dispose() { if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true); }
    }
}
