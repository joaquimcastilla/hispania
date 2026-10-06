using System.Diagnostics;
using Hispania.Library.Persistence.EFCore;
using Hispania.Library.Persistence.PostgreSql;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Hispania.Media.Domain.Identity;
using Hispania.Media.Domain.Enrichment;
using Hispania.Metadata.Contracts;
using Hispania.Library.Application;
using Hispania.Library.Contracts;
using Xunit;

namespace Hispania.Inventory.Tests;

public sealed class Phase17PostgreSqlFactAttribute : FactAttribute
{
    public Phase17PostgreSqlFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("HISPANIA_POSTGRES_TEST_CONNECTION")))
            Skip = "PostgreSQL tooling and isolated database configuration are required; mandatory Linux gate forbids this skip.";
    }
}

public sealed class Phase17PostgreSqlTests
{
    [Phase17PostgreSqlFact]
    [Trait("Category", "Phase17PostgreSql")]
    public async Task MixedSqlitePostgreSqlFilesystemDisasterRestoresWithReprovisionedCredentialsAndCurrentReader()
    {
        var connection = Environment.GetEnvironmentVariable("HISPANIA_POSTGRES_TEST_CONNECTION")!;
        var settings = new NpgsqlConnectionStringBuilder(connection) { Pooling = false };
        var sourceDb = "hispania_backup_source_" + Guid.NewGuid().ToString("N");
        var targetDb = "hispania_backup_target_" + Guid.NewGuid().ToString("N");
        await using var admin = new NpgsqlConnection(settings.ConnectionString); await admin.OpenAsync();
        async Task ExecuteAdmin(string sql) { await using var command = admin.CreateCommand(); command.CommandText = sql; await command.ExecuteNonQueryAsync(); }
        await ExecuteAdmin($"CREATE DATABASE \"{sourceDb}\""); await ExecuteAdmin($"CREATE DATABASE \"{targetDb}\"");
        try
        {
            using var fixture = new Phase17BackupRestoreTests.Installation();
            File.Delete(Path.Combine(fixture.Source, "media.sqlite"));
            File.WriteAllText(Path.Combine(fixture.Source, "portable.json"), "{\"Persistence:Provider\":\"postgresql\"}");
            var directory = new DirectoryInfo(AppContext.BaseDirectory); while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Hispania.sln"))) directory = directory.Parent;
            var repo = directory!.FullName;
            var sourceSettings = new NpgsqlConnectionStringBuilder(settings.ConnectionString) { Database = sourceDb };
            File.WriteAllText(Path.Combine(fixture.Source, "postgres-source.local.json"), System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, string> { ["Host"] = sourceSettings.Host!, ["Port"] = sourceSettings.Port.ToString(System.Globalization.CultureInfo.InvariantCulture), ["Database"] = sourceDb }));
            await using (var db = new NpgsqlConnection(sourceSettings.ConnectionString))
            {
                await db.OpenAsync();
                foreach (var file in new[] { "media-postgresql-schema.sql", "media-postgresql-data.sql" })
                {
                    await using var command = db.CreateCommand(); command.CommandText = File.ReadAllText(Path.Combine(repo, "compatibility", "data", "0.7", file)); await command.ExecuteNonQueryAsync();
                }
            }
            var work = new WorkId(Guid.Parse("00000000-0000-0000-0000-000000000047"));
            var importedIdentity = new ImportedPlayableMovieIdentityFactory().Create();
            var manualTitle = new MetadataAssertion(new MetadataAssertionId(Guid.NewGuid()), EnrichmentTarget.For(work), new MetadataField("title"), MetadataValue.Text("Manual PostgreSQL recovery title"), new MetadataProvenance(MetadataProvenanceOrigin.Manual, DateTimeOffset.UtcNow), MetadataAssertionStatus.Active, null);
            await using (var sourceProvider = new ServiceCollection().AddPostgreSqlLibraryPersistence(sourceSettings.ConnectionString).BuildServiceProvider())
            {
                await sourceProvider.GetRequiredService<PostgreSqlMigrationRunner>().InitializeAsync(sourceProvider.GetRequiredService<EfLibraryCatalogStore>());
                var key = new ImportKey("recovery-fixture", "Recovery Movie.mp4");
                var sourceSnapshot = new LibrarySourceSnapshot(1234, DateTimeOffset.UnixEpoch);
                var mapped = Assert.IsType<MediaMappingSucceeded>(new ProbedMediaMapper().Map(new DiscoveredMediaCandidate(new LibraryFileReference(key.RootKey, key.NormalizedRelativePath), sourceSnapshot.ByteLength, sourceSnapshot.LastModifiedUtc),
                    new ProbeSucceeded(new ProbeResult("mp4", TimeSpan.FromSeconds(2), [new VideoStreamObservation(0, "h264", 1920, 1080, new ObservedRational(24, 1), 8, null), new AudioStreamObservation(1, "aac", 48000, 2, null, null)])), importedIdentity)).Entry;
                await sourceProvider.GetRequiredService<EfLibraryCatalogStore>().CommitCompletedScanAsync(new CompletedLibraryScan(DateTimeOffset.UtcNow, new LibraryScanScope([key.RootKey]), [new MappedImport(key, sourceSnapshot, importedIdentity, mapped)]));
                await sourceProvider.GetRequiredService<IMetadataEnrichmentStore>().ApplyAsync(new MetadataEnrichmentWrite([], [manualTitle], [], null));
            }
            var before = await SnapshotAsync(sourceSettings.ConnectionString);
            var localBefore = Phase17BackupRestoreTests.Snapshot(fixture.Source);
            var dll = Path.Combine(repo, "apps", "server", "bin", "Debug", "net10.0", "Hispania.Server.dll");
            Assert.Equal(0, await CommandAsync(dll, sourceSettings, "--backup-create", fixture.Source, fixture.Bundle));
            await ExecuteAdmin($"DROP DATABASE \"{sourceDb}\" WITH (FORCE)");
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(fixture.Source, true);
            var targetSettings = new NpgsqlConnectionStringBuilder(settings.ConnectionString) { Database = targetDb };
            Assert.Equal(0, await CommandAsync(dll, targetSettings, "--backup-restore", fixture.Bundle, fixture.Target));
            Assert.Equal(localBefore, Phase17BackupRestoreTests.Snapshot(fixture.Target));
            Assert.Equal(before, await SnapshotAsync(targetSettings.ConnectionString));
            await using var provider = new ServiceCollection().AddPostgreSqlLibraryPersistence(targetSettings.ConnectionString).BuildServiceProvider();
            var runner = provider.GetRequiredService<PostgreSqlMigrationRunner>(); var store = provider.GetRequiredService<EfLibraryCatalogStore>();
            await runner.InitializeAsync(store);
            var catalogEntry = Assert.Single(store.GetAll());
            Assert.Equal(importedIdentity.WorkId, catalogEntry.Media.Work.Id);
            Assert.Equal(importedIdentity.MediaEntityId, catalogEntry.Id);
            Assert.Equal("recovery-fixture", catalogEntry.File.RootKey);
            Assert.Single(catalogEntry.Media.VideoAsset.TechnicalRepresentations.Single().SourceBindings);
            Assert.Single(catalogEntry.Media.AudioAsset.TechnicalRepresentations.Single().SourceBindings);
            Assert.True(await provider.GetRequiredService<IMetadataEnrichmentStore>().WorkExistsAsync(work));
            var metadata = await provider.GetRequiredService<IMetadataEnrichmentStore>().LoadForTargetAsync(EnrichmentTarget.For(work));
            Assert.Equal(manualTitle.Id, Assert.Single(metadata.Assertions).Id);
            Assert.Equal("Manual PostgreSQL recovery title", Assert.Single(metadata.Assertions).Value.TextValue);
            Assert.Equal(MetadataProvenanceOrigin.Manual, Assert.Single(metadata.Assertions).Provenance.Origin);
            var restoredSettings = await provider.GetRequiredService<IMetadataSettingsStore>().GetSettingsAsync(work);
            Assert.True(restoredSettings.LockItemMetadata); Assert.Equal("es", restoredSettings.PreferredMetadataLanguage); Assert.Equal("ES", restoredSettings.MetadataCountryRegion);
            await using (var restored = new NpgsqlConnection(targetSettings.ConnectionString))
            {
                await restored.OpenAsync(); await using var command = restored.CreateCommand();
                command.CommandText = "SELECT work_id FROM media_entities WHERE id='00000000-0000-0000-0000-000000000048'";
                Assert.Equal(work.Value, await command.ExecuteScalarAsync());
                command.CommandText = "INSERT INTO works SELECT * FROM works";
                Assert.Equal(PostgresErrorCodes.UniqueViolation, (await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync())).SqlState);
                command.CommandText = "UPDATE media_entities SET work_id='ffffffff-ffff-ffff-ffff-ffffffffffff'";
                Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, (await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync())).SqlState);
            }
            var factory = provider.GetRequiredService<IDbContextFactory<LibraryCatalogDbContext>>(); await using var current = await factory.CreateDbContextAsync();
            Assert.Equal("20260926104649_InitialPostgreSql", Assert.Single(await current.Database.GetAppliedMigrationsAsync()));
        }
        finally
        {
            await ExecuteAdmin($"DROP DATABASE IF EXISTS \"{sourceDb}\" WITH (FORCE)");
            await ExecuteAdmin($"DROP DATABASE IF EXISTS \"{targetDb}\" WITH (FORCE)");
        }
    }
    private static async Task<string[]> SnapshotAsync(string connection)
    {
        await using var db = new NpgsqlConnection(connection); await db.OpenAsync();
        await using var names = db.CreateCommand(); names.CommandText = "SELECT tablename FROM pg_tables WHERE schemaname='public' ORDER BY tablename";
        var tables = new List<string>(); await using (var reader = await names.ExecuteReaderAsync()) while (await reader.ReadAsync()) tables.Add(reader.GetString(0));
        var rows = new List<string>();
        foreach (var table in tables)
        {
            await using var query = db.CreateCommand(); query.CommandText = $"SELECT row_to_json(t)::text FROM \"{table.Replace("\"", "\"\"")}\" t";
            await using var reader = await query.ExecuteReaderAsync(); while (await reader.ReadAsync()) rows.Add(table + ":" + reader.GetString(0));
        }
        return rows.Order(StringComparer.Ordinal).ToArray();
    }
    private static async Task<int> CommandAsync(string dll, NpgsqlConnectionStringBuilder settings, params string[] arguments)
    {
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        start.ArgumentList.Add(dll); foreach (var argument in arguments) start.ArgumentList.Add(argument);
        start.Environment["PGHOST"] = settings.Host; start.Environment["PGPORT"] = settings.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        start.Environment["PGUSER"] = settings.Username; start.Environment["PGDATABASE"] = settings.Database; start.Environment["PGPASSWORD"] = settings.Password;
        using var process = Process.Start(start)!; var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(1)); await output; await error; return process.ExitCode;
    }
}
