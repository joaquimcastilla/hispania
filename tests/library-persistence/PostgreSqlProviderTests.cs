using Hispania.Library.Persistence.EFCore;
using Hispania.Library.Persistence.PostgreSql;
using Hispania.Library.Application;
using Hispania.Library.Contracts;
using Hispania.Library.Persistence.Sqlite;
using Hispania.Media.Domain.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Data.Sqlite;
using Npgsql;
using Xunit;

namespace Hispania.Library.Persistence.Sqlite.Tests;

public sealed class PostgreSqlProviderTests
{
    [Fact]
    public void ProviderModelUsesNativeUuidAndFilteredConfirmedIdentifierIndex()
    {
        var options = new DbContextOptionsBuilder<LibraryCatalogDbContext>().UseNpgsql("Host=localhost;Database=model_only;Username=none;Password=none").Options;
        using var context = new LibraryCatalogDbContext(options);
        var entity = Assert.Single(context.Model.GetEntityTypes(), x => x.GetTableName() == "external_identifier_associations");
        Assert.Equal("uuid", entity.FindProperty("Id")!.GetColumnType());
        var index = Assert.Single(entity.GetIndexes(), x => x.GetDatabaseName() == "ux_confirmed_external_identifier");
        Assert.True(index.IsUnique);
        Assert.Equal("current_status_code = 'confirmed'", index.GetFilter());
    }

    [Fact]
    public void GeneratedMigrationIsPostgreSqlSpecificAndAdditive()
    {
        var options = new DbContextOptionsBuilder<LibraryCatalogDbContext>()
            .UseNpgsql("Host=localhost;Database=script_only;Username=none;Password=none", provider => provider.MigrationsAssembly(typeof(PostgreSqlLibraryPersistence).Assembly.FullName))
            .Options;
        using var context = new LibraryCatalogDbContext(options);
        var sql = context.GetService<IMigrator>().GenerateScript();
        Assert.Contains("uuid", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("CREATE TABLE", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ux_confirmed_external_identifier", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("DROP TABLE", sql, StringComparison.OrdinalIgnoreCase);
    }

    [PostgreSqlIntegrationFact]
    public async Task PostgreSqlMigrationAndConnectionFailureRecoveryUseAnIsolatedSchema()
    {
        var baseConnectionString = Environment.GetEnvironmentVariable("HISPANIA_POSTGRES_TEST_CONNECTION");
        Assert.False(string.IsNullOrWhiteSpace(baseConnectionString));

        var adminBuilder = new NpgsqlConnectionStringBuilder(baseConnectionString);
        var schema = "hispania_test_" + Guid.NewGuid().ToString("N");
        await using (var admin = new NpgsqlConnection(adminBuilder.ConnectionString))
        {
            await admin.OpenAsync();
            await using var create = admin.CreateCommand();
            create.CommandText = $"CREATE SCHEMA \"{schema}\"";
            await create.ExecuteNonQueryAsync();
        }

        try
        {
            var isolated = new NpgsqlConnectionStringBuilder(baseConnectionString) { SearchPath = schema, Pooling = false }.ConnectionString;
            var unavailable = new NpgsqlConnectionStringBuilder(isolated) { Host = "127.0.0.1", Port = 1, Timeout = 1, Pooling = false }.ConnectionString;
            await using (var failedProvider = CreateProvider(unavailable))
            {
                var failedRunner = failedProvider.GetRequiredService<PostgreSqlMigrationRunner>();
                var failedStore = failedProvider.GetRequiredService<EfLibraryCatalogStore>();
                await Assert.ThrowsAnyAsync<NpgsqlException>(() => failedRunner.InitializeAsync(failedStore));
            }

            await using var recoveredProvider = CreateProvider(isolated);
            var runner = recoveredProvider.GetRequiredService<PostgreSqlMigrationRunner>();
            var store = recoveredProvider.GetRequiredService<EfLibraryCatalogStore>();
            await runner.InitializeAsync(store);
            await using var scope = recoveredProvider.CreateAsyncScope();
            var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<LibraryCatalogDbContext>>();
            await using var db = await factory.CreateDbContextAsync();
            Assert.True(await db.Database.CanConnectAsync());
            Assert.NotEmpty(await db.Database.GetAppliedMigrationsAsync());

            var sqlitePath = Path.Combine(Path.GetTempPath(), $"hispania-provider-equivalence-{Guid.NewGuid():N}.sqlite");
            try
            {
                await using var sqliteProvider = new ServiceCollection().AddSqliteLibraryPersistence(sqlitePath).BuildServiceProvider();
                var sqliteRunner = sqliteProvider.GetRequiredService<SqliteMigrationRunner>();
                var sqliteStore = sqliteProvider.GetRequiredService<EfLibraryCatalogStore>();
                await sqliteRunner.MigrateAndLoadAsync(sqliteStore, default);

                var sharedIdentity = new ImportedPlayableMovieIdentityFactory().Create();
                var pgResult = await ExerciseStoreAsync(store, recoveredProvider.GetRequiredService<IDbContextFactory<LibraryCatalogDbContext>>(), sharedIdentity);
                var sqliteResult = await ExerciseStoreAsync(sqliteStore, sqliteProvider.GetRequiredService<IDbContextFactory<LibraryCatalogDbContext>>(), sharedIdentity);
                Assert.Equal(sqliteResult, pgResult);
            }
            finally
            {
                SqliteConnection.ClearAllPools();
                if (File.Exists(sqlitePath)) File.Delete(sqlitePath);
            }
        }
        finally
        {
            await using var admin = new NpgsqlConnection(adminBuilder.ConnectionString);
            await admin.OpenAsync();
            await using var drop = admin.CreateCommand();
            drop.CommandText = $"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE";
            await drop.ExecuteNonQueryAsync();
        }
    }

    private static async Task<CatalogProjection> ExerciseStoreAsync(EfLibraryCatalogStore store, IDbContextFactory<LibraryCatalogDbContext> factory, ImportedPlayableMovieIdentity identity)
    {
        var scope = new LibraryScanScope(["movies"]);
        var key = new ImportKey("movies", "Provider Equivalence (2026)/Provider Equivalence (2026).mp4");
        var snapshot = new LibrarySourceSnapshot(1234, DateTimeOffset.UnixEpoch);
        var entry = Assert.IsType<MediaMappingSucceeded>(new ProbedMediaMapper().Map(
            new DiscoveredMediaCandidate(new LibraryFileReference(key.RootKey, key.NormalizedRelativePath), snapshot.ByteLength, snapshot.LastModifiedUtc),
            new ProbeSucceeded(new ProbeResult("mp4", TimeSpan.FromSeconds(2),
            [
                new VideoStreamObservation(0, "h264", 1920, 1080, new ObservedRational(24, 1), 8, null),
                new AudioStreamObservation(1, "aac", 48000, 2, null, null)
            ])), identity)).Entry;
        var scan = new CompletedLibraryScan(DateTimeOffset.UtcNow, scope, [new MappedImport(key, snapshot, identity, entry)]);
        await store.CommitCompletedScanAsync(scan);
        await store.CommitCompletedScanAsync(scan);
        await store.LoadSnapshotAsync(["movies"]);
        var read = Assert.Single(store.GetAll());
        Assert.Single(read.Media.VideoAsset.TechnicalRepresentations.Single().SourceBindings);
        Assert.Single(read.Media.AudioAsset.TechnicalRepresentations.Single().SourceBindings);

        await VerifyRollbackAsync(factory);
        await VerifyConfirmedIdentifierUniqueIndexAsync(factory);
        await VerifyConcurrentUniqueImportAsync(factory);
        return new CatalogProjection(read.DisplayName, read.Id.Value, read.Media.Work.Id.Value, read.Media.Source.Locator.OpaqueValue,
            read.Media.Timeline.Duration?.Value.Ticks, read.Media.VideoAsset.TechnicalRepresentations.Single().Profile.GetType().Name,
            read.Media.AudioAsset.TechnicalRepresentations.Single().Profile.GetType().Name, read.Media.Timeline.Coverages.Count);
    }

    private static async Task VerifyConfirmedIdentifierUniqueIndexAsync(IDbContextFactory<LibraryCatalogDbContext> factory)
    {
        async Task InsertAsync(Guid id)
        {
            await using var context = await factory.CreateDbContextAsync();
            context.ExternalIdentifierAssociations.Add(new ExternalIdentifierAssociationRecord
            {
                Id = id,
                ProviderCode = "provider-test",
                NamespaceCode = "movie",
                IdentifierValue = "shared-confirmed-id",
                CurrentStatusCode = "confirmed",
                TargetKind = "work",
                TargetId = Guid.NewGuid()
            });
            await context.SaveChangesAsync();
        }
        await InsertAsync(Guid.NewGuid());
        await Assert.ThrowsAsync<DbUpdateException>(() => InsertAsync(Guid.NewGuid()));
    }

    private static async Task VerifyRollbackAsync(IDbContextFactory<LibraryCatalogDbContext> factory)
    {
        await using (var context = await factory.CreateDbContextAsync())
        await using (var transaction = await context.Database.BeginTransactionAsync())
        {
            context.Imports.Add(new ImportRecord { Id = Guid.NewGuid(), RootKey = "transaction", RelativePath = "same.mp4", StatusCode = "active" });
            await context.SaveChangesAsync();
            context.Imports.Add(new ImportRecord { Id = Guid.NewGuid(), RootKey = "transaction", RelativePath = "same.mp4", StatusCode = "active" });
            await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
            await transaction.RollbackAsync();
        }
        await using var verify = await factory.CreateDbContextAsync();
        Assert.Equal(0, await verify.Imports.CountAsync(x => x.RootKey == "transaction"));
    }

    private static async Task VerifyConcurrentUniqueImportAsync(IDbContextFactory<LibraryCatalogDbContext> factory)
    {
        using var barrier = new Barrier(2);
        async Task<bool> InsertAsync(Guid id)
        {
            await using var context = await factory.CreateDbContextAsync();
            context.Imports.Add(new ImportRecord { Id = id, RootKey = "concurrent", RelativePath = "same.mp4", StatusCode = "active" });
            barrier.SignalAndWait(TimeSpan.FromSeconds(10));
            try { await context.SaveChangesAsync(); return true; }
            catch (DbUpdateException) { return false; }
        }
        var outcomes = await Task.WhenAll(Task.Run(() => InsertAsync(Guid.NewGuid())), Task.Run(() => InsertAsync(Guid.NewGuid())));
        Assert.Single(outcomes, succeeded => succeeded);
        await using var verify = await factory.CreateDbContextAsync();
        Assert.Equal(1, await verify.Imports.CountAsync(x => x.RootKey == "concurrent"));
    }

    private sealed record CatalogProjection(string Title, Guid MediaEntityId, Guid WorkId, string SourceLocator, long? DurationTicks,
        string VideoProfile, string AudioProfile, int CoverageCount);

    private static ServiceProvider CreateProvider(string connectionString)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddPostgreSqlLibraryPersistence(connectionString);
        return services.BuildServiceProvider();
    }
}

public sealed class PostgreSqlIntegrationFactAttribute : FactAttribute
{
    public PostgreSqlIntegrationFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("HISPANIA_POSTGRES_TEST_CONNECTION")))
            Skip = "Set HISPANIA_POSTGRES_TEST_CONNECTION to an explicitly disposable PostgreSQL test database.";
    }
}
