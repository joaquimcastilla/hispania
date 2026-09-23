using Hispania.Library.Persistence.EFCore;
using Hispania.Library.Persistence.Sqlite;
using Hispania.Library.Application;
using Hispania.Library.Contracts;
using Hispania.Library.LocalFileSystem;
using Hispania.Media.Domain.Identity;
using Hispania.Media.Domain.Enrichment;
using Hispania.Metadata.Contracts;
using Hispania.Media.Domain.Sources;
using Hispania.Playback.Application;
using Hispania.Playback.Contracts;
using Hispania.Server;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Hispania.Library.Persistence.Sqlite.Tests;

public sealed class SqlitePersistenceTests
{
    private static readonly string[] FolderFixtureTitles = ["Seduction and Snacks (2021)", "The Weather Man (2005)"];
    [Fact]
    public async Task FolderLibraryScanPersistsOnePlayableEntryPerVideoAcrossRescanAndRestart()
    {
        var database = Path.Combine(Path.GetTempPath(), $"hispania-folder-scan-{Guid.NewGuid():N}.sqlite");
        var root = Path.Combine(Path.GetTempPath(), $"hispania-folder-media-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, "Seduction and Snacks (2021)"));
        Directory.CreateDirectory(Path.Combine(root, "The Weather Man (2005)"));
        try
        {
            File.WriteAllBytes(Path.Combine(root, "Seduction and Snacks (2021)", "Seduction and Snacks (2021).mp4"), [1, 2, 3, 4]);
            File.WriteAllText(Path.Combine(root, "Seduction and Snacks (2021)", "2_English.srt"), "1\n00:00:00,000 --> 00:00:01,000\nfixture\n");
            File.WriteAllText(Path.Combine(root, "Seduction and Snacks (2021)", "3_English.srt"), "1\n00:00:00,000 --> 00:00:01,000\nfixture\n");
            File.WriteAllBytes(Path.Combine(root, "The Weather Man (2005)", "The Weather Man (2005).mkv"), [5, 6, 7]);

            var scope = new LibraryScanScope(["movies"]);
            Guid[] firstWorkIds;
            await using (var services = new ServiceCollection().AddSqliteLibraryPersistence(database).BuildServiceProvider())
            {
                var store = services.GetRequiredService<EfLibraryCatalogStore>();
                await services.GetRequiredService<SqliteMigrationRunner>().MigrateAndLoadAsync(store, default);
                var scanner = CreateFolderScanner(root, store, scope);
                var scan = await scanner.ScanAsync();
                Assert.Equal(new LibraryScanResult(2, 2, 0, 0), scan);
                var visible = store.GetAll();
                Assert.Equal(2, visible.Count);
                Assert.Equal(FolderFixtureTitles, visible.Select(x => x.DisplayName).Order().ToArray());
                Assert.All(visible, x => Assert.StartsWith("movies/", x.Media.Source.Locator.OpaqueValue, StringComparison.Ordinal));
                firstWorkIds = visible.Select(x => x.Media.Work.Id.Value).Order().ToArray();

                // Cataloging only discovers video; it neither imports sidecar subtitles nor assigns metadata.
                await using var db = await services.GetRequiredService<IDbContextFactory<LibraryCatalogDbContext>>().CreateDbContextAsync();
                Assert.Equal(2, await db.Imports.CountAsync());
                Assert.Equal(2, await db.Works.CountAsync());
                Assert.Equal(2, await db.MediaEntities.CountAsync());
                Assert.Equal(2, await db.Sources.CountAsync());
                Assert.Equal(0, await db.ExternalIdentifierAssociations.CountAsync());
                Assert.Equal(0, await db.ArtworkReferences.CountAsync());

                var rescan = await scanner.ScanAsync();
                Assert.Equal(new LibraryScanResult(2, 2, 0, 0), rescan);
                Assert.Equal(2, store.GetAll().Count);
                Assert.Equal(firstWorkIds, store.GetAll().Select(x => x.Media.Work.Id.Value).Order().ToArray());
                Assert.Equal(2, await db.Imports.CountAsync());
                Assert.Equal(2, await db.Works.CountAsync());
                Assert.Equal(0, await db.ExternalIdentifierAssociations.CountAsync());
            }

            await using (var restarted = new ServiceCollection().AddSqliteLibraryPersistence(database).BuildServiceProvider())
            {
                var store = restarted.GetRequiredService<EfLibraryCatalogStore>();
                await restarted.GetRequiredService<SqliteMigrationRunner>().MigrateAndLoadAsync(store, default);
                var scanner = CreateFolderScanner(root, store, scope);
                var afterRestart = await scanner.ScanAsync();
                Assert.Equal(new LibraryScanResult(2, 2, 0, 0), afterRestart);
                Assert.Equal(2, store.GetAll().Count);
                Assert.Equal(firstWorkIds, store.GetAll().Select(x => x.Media.Work.Id.Value).Order().ToArray());
                await using var db = await restarted.GetRequiredService<IDbContextFactory<LibraryCatalogDbContext>>().CreateDbContextAsync();
                Assert.Equal(2, await db.Imports.CountAsync());
                Assert.Equal(2, await db.Works.CountAsync());
                Assert.Equal(2, await db.MediaEntities.CountAsync());
                Assert.Equal(2, await db.Sources.CountAsync());
                Assert.Equal(0, await db.ExternalIdentifierAssociations.CountAsync());
                Assert.Equal(0, await db.ArtworkReferences.CountAsync());
            }
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            if (File.Exists(database)) File.Delete(database);
        }
    }

    [Fact]
    public async Task ManualDiscoveryAfterRestartImportsNewFoldersOnceAndDoesNotCatalogSidecars()
    {
        var database = Path.Combine(Path.GetTempPath(), $"hispania-manual-discovery-{Guid.NewGuid():N}.sqlite");
        var root = Path.Combine(Path.GetTempPath(), $"hispania-manual-media-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, "Alpha Film (2023)"));
        File.WriteAllBytes(Path.Combine(root, "Alpha Film (2023)", "Alpha Film (2023).mp4"), [1, 2, 3]);
        File.WriteAllText(Path.Combine(root, "Alpha Film (2023)", "Alpha Film (2023).es.srt"), "subtitle");
        File.WriteAllText(Path.Combine(root, "Alpha Film (2023)", "Alpha Film (2023).en.srt"), "subtitle");
        try
        {
            var scope = new LibraryScanScope(["movies"]);
            Guid[] firstIds;
            await using (var services = new ServiceCollection().AddSqliteLibraryPersistence(database).BuildServiceProvider())
            {
                var store = services.GetRequiredService<EfLibraryCatalogStore>();
                await services.GetRequiredService<SqliteMigrationRunner>().MigrateAndLoadAsync(store, default);
                var scanner = CreateFolderScanner(root, store, scope);
                Assert.Equal(new LibraryScanResult(1, 1, 0, 0), await scanner.ScanAsync());
                Assert.Single(store.GetAll());
                firstIds = store.GetAll().Select(x => x.Media.Work.Id.Value).ToArray();

                Directory.CreateDirectory(Path.Combine(root, "Beta Film (2024)"));
                File.WriteAllBytes(Path.Combine(root, "Beta Film (2024)", "Beta Film (2024).mkv"), [4, 5, 6]);
                Assert.Equal(new LibraryScanResult(2, 2, 0, 0), await scanner.ScanAsync());
                await store.LoadSnapshotAsync(scope.RootKeys);
                Assert.Equal(2, store.GetAll().Count);
                Assert.Equal(2, store.GetAll().Select(x => x.Media.Work.Id).Distinct().Count());
                await using var db = await services.GetRequiredService<IDbContextFactory<LibraryCatalogDbContext>>().CreateDbContextAsync();
                Assert.Equal(2, await db.Imports.CountAsync());
                Assert.Equal(2, await db.Works.CountAsync());
                Assert.Equal(2, await db.MediaEntities.CountAsync());
                Assert.Equal(2, await db.Sources.CountAsync());
                Assert.Empty(await db.ExternalIdentifierAssociations.ToListAsync());
                Assert.Equal(firstIds[0], store.GetAll().Single(x => x.DisplayName == "Alpha Film (2023)").Media.Work.Id.Value);
            }

            await using (var restarted = new ServiceCollection().AddSqliteLibraryPersistence(database).BuildServiceProvider())
            {
                var store = restarted.GetRequiredService<EfLibraryCatalogStore>();
                await restarted.GetRequiredService<SqliteMigrationRunner>().MigrateAndLoadAsync(store, default);
                var scanner = CreateFolderScanner(root, store, scope);
                Assert.Equal(new LibraryScanResult(2, 2, 0, 0), await scanner.ScanAsync());
                await store.LoadSnapshotAsync(scope.RootKeys);
                Assert.Equal(2, store.GetAll().Count);
                await using var db = await restarted.GetRequiredService<IDbContextFactory<LibraryCatalogDbContext>>().CreateDbContextAsync();
                Assert.Equal(2, await db.Imports.CountAsync());
                Assert.Equal(2, await db.Works.CountAsync());
                Assert.Equal(2, await db.MediaEntities.CountAsync());
                Assert.Equal(2, await db.Sources.CountAsync());
                Assert.Empty(await db.ExternalIdentifierAssociations.ToListAsync());
            }
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            if (File.Exists(database)) File.Delete(database);
        }
    }

    private static LibraryScanner CreateFolderScanner(string root, EfLibraryCatalogStore store, LibraryScanScope scope) =>
        new(new LocalFileSystemMediaDiscovery(new LocalLibraryRootConfiguration([new LocalLibraryRoot("movies", root)])), new FolderFixtureProbe(), new ProbedMediaMapper(), store, new ImportedPlayableMovieIdentityFactory(), scope);

    private sealed class FolderFixtureProbe : IMediaProbe
    {
        public ValueTask<ProbeOutcome> ProbeAsync(DiscoveredMediaCandidate candidate, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<ProbeOutcome>(new ProbeSucceeded(new ProbeResult(Path.GetExtension(candidate.File.RelativePath).TrimStart('.'), TimeSpan.FromSeconds(1), [new VideoStreamObservation(0, "h264", 1920, 1080, new ObservedRational(24, 1), 8, null), new AudioStreamObservation(1, "aac", 48000, 2, null, null)])));
    }

    [Fact]
    public async Task LegacyLatestHistoryBridgePreservesSettingsLocksDataAndProductVersionsAcrossTwoStarts()
    {
        var path = Path.Combine(Path.GetTempPath(), $"hispania-legacy-latest-{Guid.NewGuid():N}.sqlite"); var work = WorkId.New();
        try
        {
            await using (var initial = new ServiceCollection().AddSqliteLibraryPersistence(path).BuildServiceProvider())
            {
                await initial.GetRequiredService<SqliteMigrationRunner>().MigrateAndLoadAsync(initial.GetRequiredService<EfLibraryCatalogStore>(), default); var factory = initial.GetRequiredService<IDbContextFactory<LibraryCatalogDbContext>>(); await using var db = await factory.CreateDbContextAsync(); db.Works.Add(new WorkRecord { Id = work.Value, CategoryCode = "movie" }); await db.SaveChangesAsync(); await initial.GetRequiredService<IMetadataSettingsStore>().SetSettingsAsync(new MetadataSettings(work, "es-ES", "ES", false)); await initial.GetRequiredService<IMetadataSettingsStore>().SetLockAsync(new MetadataFieldLock(work, "description", true)); await initial.GetRequiredService<IMetadataSettingsStore>().SetLockAsync(new MetadataFieldLock(work, "genre", true)); await initial.GetRequiredService<IRichMetadataStore>().ApplyAsync(new RichMetadataWrite(EnrichmentTarget.For(work), new NormalizedMovieRichMetadata(["Action"], ["assassin"], [new("10", "Studio A")], [new("123", "Person")], [new("123", "Person", "cast", "Hero", null, 0)]), new MetadataProvenance(MetadataProvenanceOrigin.ExternalProvider, DateTimeOffset.UtcNow, new ExternalIdentifierProvider("tmdb"))));
                var identifier = new ExternalIdentifier(new ExternalIdentifierProvider("tmdb"), new ExternalIdentifierNamespace("movie"), "legacy-42"); var provenance = new MetadataProvenance(MetadataProvenanceOrigin.ExternalProvider, new DateTimeOffset(2026, 9, 14, 12, 0, 0, TimeSpan.Zero), identifier.Provider, identifier); var association = new ExternalIdentifierAssociation(ExternalIdentifierAssociationId.New(), identifier, EnrichmentTarget.For(work), new ExternalIdentifierAssociationLifecycleEntry(ExternalIdentifierAssociationStatus.Confirmed, provenance.ObservedAt, provenance)); var assertion = new MetadataAssertion(MetadataAssertionId.New(), EnrichmentTarget.For(work), new MetadataField("title"), MetadataValue.Text("Legacy Title"), provenance, MetadataAssertionStatus.Active); var applied = await initial.GetRequiredService<IMetadataEnrichmentStore>().ApplyAsync(new MetadataEnrichmentWrite([association], [assertion], [], null)); Assert.True(applied); var seeded = await initial.GetRequiredService<IMetadataEnrichmentStore>().LoadAsync(); Assert.Contains(seeded.Assertions, x => x.Id == assertion.Id && x.Target.WorkId == work && x.Field.Code == "title" && x.Value.TextValue == "Legacy Title" && x.Status == MetadataAssertionStatus.Active); Assert.Contains(seeded.Associations, x => x.Id == association.Id && x.Identifier.Value == "legacy-42" && x.Target.WorkId == work);
                await using var command = db.Database.GetDbConnection().CreateCommand(); await db.Database.OpenConnectionAsync(); command.CommandText = "UPDATE __EFMigrationsHistory SET MigrationId = CASE MigrationId WHEN '20260922000100_initial' THEN '0001_initial' WHEN '20260922000200_metadata_enrichment' THEN '0002_metadata_enrichment' WHEN '20260922000300_rich_metadata' THEN '0003_rich_metadata' WHEN '20260922000400_metadata_settings' THEN '0004_metadata_settings' ELSE MigrationId END"; await command.ExecuteNonQueryAsync();
            }
            async Task<string[]> StartAndReadHistory()
            {
                await using (var preServices = new ServiceCollection().AddSqliteLibraryPersistence(path).BuildServiceProvider())
                {
                    var preFactory = preServices.GetRequiredService<IDbContextFactory<LibraryCatalogDbContext>>(); await using var preDb = await preFactory.CreateDbContextAsync(); await preDb.Database.OpenConnectionAsync(); await using var preRaw = preDb.Database.GetDbConnection().CreateCommand(); preRaw.CommandText = "SELECT COUNT(*) FROM metadata_assertions"; Assert.Equal(1L, Convert.ToInt64(await preRaw.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture));
                }
                await using var services = new ServiceCollection().AddSqliteLibraryPersistence(path).BuildServiceProvider();
                await services.GetRequiredService<SqliteMigrationRunner>().MigrateAndLoadAsync(services.GetRequiredService<EfLibraryCatalogStore>(), default);
                var factory = services.GetRequiredService<IDbContextFactory<LibraryCatalogDbContext>>();
                await using var db = await factory.CreateDbContextAsync(); await db.Database.OpenConnectionAsync();
                await using var raw = db.Database.GetDbConnection().CreateCommand(); raw.CommandText = "SELECT COUNT(*) FROM metadata_assertions"; var rawCount = Convert.ToInt64(await raw.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture); Assert.True(rawCount > 0, $"raw metadata_assertions count after startup: {rawCount}");
                await using var command = db.Database.GetDbConnection().CreateCommand(); command.CommandText = "SELECT MigrationId || ':' || ProductVersion FROM __EFMigrationsHistory ORDER BY MigrationId"; var values = new List<string>(); await using var reader = await command.ExecuteReaderAsync(); while (await reader.ReadAsync()) values.Add(reader.GetString(0));
                var settings = await services.GetRequiredService<IMetadataSettingsStore>().GetSettingsAsync(work); Assert.Equal("es-ES", settings.PreferredMetadataLanguage); Assert.Equal("ES", settings.MetadataCountryRegion); Assert.Contains(await services.GetRequiredService<IMetadataSettingsStore>().GetLocksAsync(work), x => x.FieldCode == "description" && x.IsLocked); var rich = await services.GetRequiredService<IRichMetadataStore>().LoadAsync(EnrichmentTarget.For(work)); Assert.Contains("Action", rich.Genres); Assert.Contains(rich.Credits, x => x.CreditType == "cast"); var enrichment = await services.GetRequiredService<IMetadataEnrichmentStore>().LoadAsync(); var assertion = Assert.Single(enrichment.Assertions, x => x.Field.Code == "title"); Assert.Equal("Legacy Title", assertion.Value.TextValue); Assert.Equal(MetadataAssertionStatus.Active, assertion.Status); var external = Assert.Single(enrichment.Associations, x => x.Identifier.Value == "legacy-42"); Assert.Equal(work.Value, external.Target.WorkId!.Value); return values.ToArray();
            }
            var first = await StartAndReadHistory(); var second = await StartAndReadHistory(); Assert.Equal(first, second); Assert.Equal(["20260922000100_initial:10.0.12", "20260922000200_metadata_enrichment:10.0.12", "20260922000300_rich_metadata:10.0.12", "20260922000400_metadata_settings:10.0.12", "20260923000500_person_metadata:10.0.12"], first);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if (File.Exists(path)) File.Delete(path); }
    }
    [Fact]
    public async Task LegacyHistoryThroughRichMetadataIsBridgedAndOnlySettingsMigrationRuns()
    {
        var path = Path.Combine(Path.GetTempPath(), $"hispania-legacy-bridge-{Guid.NewGuid():N}.sqlite"); var work = WorkId.New();
        try
        {
            await using (var initial = new ServiceCollection().AddSqliteLibraryPersistence(path).BuildServiceProvider())
            {
                await initial.GetRequiredService<SqliteMigrationRunner>().MigrateAndLoadAsync(initial.GetRequiredService<EfLibraryCatalogStore>(), default);
                var factory = initial.GetRequiredService<IDbContextFactory<LibraryCatalogDbContext>>(); await using var db = await factory.CreateDbContextAsync(); db.Works.Add(new WorkRecord { Id = work.Value, CategoryCode = "movie" }); await db.SaveChangesAsync();
                await initial.GetRequiredService<IRichMetadataStore>().ApplyAsync(new RichMetadataWrite(EnrichmentTarget.For(work), new NormalizedMovieRichMetadata(["Action"], ["assassin"], [new("10", "Studio A")], [new("123", "Person")], [new("123", "Person", "cast", "Hero", null, 0)]), new MetadataProvenance(MetadataProvenanceOrigin.ExternalProvider, DateTimeOffset.UtcNow, new ExternalIdentifierProvider("tmdb"))));
                var identifier = new ExternalIdentifier(new ExternalIdentifierProvider("tmdb"), new ExternalIdentifierNamespace("movie"), "legacy-42"); var provenance = new MetadataProvenance(MetadataProvenanceOrigin.ExternalProvider, new DateTimeOffset(2026, 9, 14, 12, 0, 0, TimeSpan.Zero), identifier.Provider, identifier); var association = new ExternalIdentifierAssociation(ExternalIdentifierAssociationId.New(), identifier, EnrichmentTarget.For(work), new ExternalIdentifierAssociationLifecycleEntry(ExternalIdentifierAssociationStatus.Confirmed, provenance.ObservedAt, provenance)); var assertion = new MetadataAssertion(MetadataAssertionId.New(), EnrichmentTarget.For(work), new MetadataField("title"), MetadataValue.Text("Legacy Title"), provenance, MetadataAssertionStatus.Active); var applied = await initial.GetRequiredService<IMetadataEnrichmentStore>().ApplyAsync(new MetadataEnrichmentWrite([association], [assertion], [], null)); Assert.True(applied); var seeded = await initial.GetRequiredService<IMetadataEnrichmentStore>().LoadAsync(); Assert.Contains(seeded.Assertions, x => x.Id == assertion.Id && x.Target.WorkId == work && x.Field.Code == "title" && x.Value.TextValue == "Legacy Title" && x.Status == MetadataAssertionStatus.Active); Assert.Contains(seeded.Associations, x => x.Id == association.Id && x.Identifier.Value == "legacy-42" && x.Target.WorkId == work);
                await using var command = db.Database.GetDbConnection().CreateCommand(); await db.Database.OpenConnectionAsync(); command.CommandText = "DELETE FROM __EFMigrationsHistory WHERE MigrationId = '20260922000400_metadata_settings'; UPDATE __EFMigrationsHistory SET MigrationId = CASE MigrationId WHEN '20260922000100_initial' THEN '0001_initial' WHEN '20260922000200_metadata_enrichment' THEN '0002_metadata_enrichment' WHEN '20260922000300_rich_metadata' THEN '0003_rich_metadata' ELSE MigrationId END; DROP TABLE IF EXISTS metadata_settings; DROP TABLE IF EXISTS metadata_field_locks;"; await command.ExecuteNonQueryAsync();
            }
            await using var reopened = new ServiceCollection().AddSqliteLibraryPersistence(path).BuildServiceProvider(); await reopened.GetRequiredService<SqliteMigrationRunner>().MigrateAndLoadAsync(reopened.GetRequiredService<EfLibraryCatalogStore>(), default);
            var factoryAfter = reopened.GetRequiredService<IDbContextFactory<LibraryCatalogDbContext>>(); await using var verify = await factoryAfter.CreateDbContextAsync(); await using var history = verify.Database.GetDbConnection().CreateCommand(); await verify.Database.OpenConnectionAsync(); history.CommandText = "SELECT MigrationId FROM __EFMigrationsHistory ORDER BY MigrationId"; var ids = new List<string>(); await using var reader = await history.ExecuteReaderAsync(); while (await reader.ReadAsync()) ids.Add(reader.GetString(0)); Assert.Equal(["20260922000100_initial", "20260922000200_metadata_enrichment", "20260922000300_rich_metadata", "20260922000400_metadata_settings", "20260923000500_person_metadata"], ids);
            var snapshot = await reopened.GetRequiredService<IRichMetadataStore>().LoadAsync(EnrichmentTarget.For(work)); Assert.Contains("Action", snapshot.Genres); Assert.Contains(snapshot.Credits, x => x.CreditType == "cast"); Assert.Equal(work, (await reopened.GetRequiredService<IMetadataSettingsStore>().GetSettingsAsync(work)).WorkId);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if (File.Exists(path)) File.Delete(path); }
    }
    [Fact]
    public async Task StandardMigrationIdsSupportTargetingRichMetadataAndLatest()
    {
        var path = Path.Combine(Path.GetTempPath(), $"hispania-migration-target-{Guid.NewGuid():N}.sqlite");
        try
        {
            await using var services = new ServiceCollection().AddSqliteLibraryPersistence(path).BuildServiceProvider();
            var factory = services.GetRequiredService<IDbContextFactory<LibraryCatalogDbContext>>();
            await using (var db = await factory.CreateDbContextAsync()) await db.Database.MigrateAsync("20260922000300_rich_metadata");
            await using (var check = await factory.CreateDbContextAsync())
            {
                Assert.True(await check.Database.CanConnectAsync());
                await check.Database.OpenConnectionAsync(); await using var command = check.Database.GetDbConnection().CreateCommand(); command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='metadata_genres'"; Assert.Equal(1L, Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture));
            }
            await using var latest = await factory.CreateDbContextAsync(); await latest.Database.MigrateAsync();
            Assert.True(await latest.Database.CanConnectAsync());
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public async Task SqliteEnforcesImportKeyForeignKeyCoverageAndConfirmedIdentifierConstraints()
    {
        var path=Path.Combine(Path.GetTempPath(),$"hispania-{Guid.NewGuid():N}.sqlite");using var services=new ServiceCollection().AddSqliteLibraryPersistence(path).BuildServiceProvider();var store=services.GetRequiredService<EfLibraryCatalogStore>();await services.GetRequiredService<SqliteMigrationRunner>().MigrateAndLoadAsync(store,default);var factory=services.GetRequiredService<IDbContextFactory<LibraryCatalogDbContext>>();
        await using (var context=await factory.CreateDbContextAsync()) { context.Imports.Add(new ImportRecord{Id=Guid.NewGuid(),RootKey="r",RelativePath="a.mp4",StatusCode="active"});context.Imports.Add(new ImportRecord{Id=Guid.NewGuid(),RootKey="r",RelativePath="a.mp4",StatusCode="active"});await Assert.ThrowsAsync<DbUpdateException>(()=>context.SaveChangesAsync()); }
        await using (var context=await factory.CreateDbContextAsync()) { context.MediaEntities.Add(new MediaEntityRecord{Id=Guid.NewGuid(),WorkId=Guid.NewGuid(),CategoryCode="movie-realisation"});await Assert.ThrowsAsync<DbUpdateException>(()=>context.SaveChangesAsync()); }
        var timeline=Guid.NewGuid();var asset=Guid.NewGuid();await using (var context=await factory.CreateDbContextAsync()) { var work=Guid.NewGuid();var entity=Guid.NewGuid();context.Works.Add(new WorkRecord{Id=work,CategoryCode="movie"});context.MediaEntities.Add(new MediaEntityRecord{Id=entity,WorkId=work,CategoryCode="movie-realisation"});context.Timelines.Add(new TimelineRecord{Id=timeline,MediaEntityId=entity});context.Assets.Add(new AssetRecord{Id=asset,MediaKindCode="video"});context.Coverages.Add(new CoverageRecord{Id=Guid.NewGuid(),TimelineId=timeline,AssetId=asset,StateCode="covered",CoverageKindCode="full",AlignmentOffsetTicks=0});context.Coverages.Add(new CoverageRecord{Id=Guid.NewGuid(),TimelineId=timeline,AssetId=asset,StateCode="covered",CoverageKindCode="full",AlignmentOffsetTicks=0});await Assert.ThrowsAsync<DbUpdateException>(()=>context.SaveChangesAsync()); }
        await using (var context=await factory.CreateDbContextAsync()) { await context.Database.ExecuteSqlRawAsync("INSERT INTO external_identifier_associations (id,provider_code,namespace_code,identifier_value,current_status_code,target_kind,target_id) VALUES (randomblob(16),'p','n','v','confirmed','work',randomblob(16));");await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(()=>context.Database.ExecuteSqlRawAsync("INSERT INTO external_identifier_associations (id,provider_code,namespace_code,identifier_value,current_status_code,target_kind,target_id) VALUES (randomblob(16),'p','n','v','confirmed','work',randomblob(16));")); }
    }
    [Fact]
    // This covers a store failure after the transaction has begun but before a successful SaveChangesAsync.
    public async Task StoreFailureBeforeSuccessfulSavePreservesDatabaseAndReadSnapshot()
    {
        var path=Path.Combine(Path.GetTempPath(),$"hispania-{Guid.NewGuid():N}.sqlite");using var services=new ServiceCollection().AddSqliteLibraryPersistence(path).BuildServiceProvider();var store=services.GetRequiredService<EfLibraryCatalogStore>();await services.GetRequiredService<SqliteMigrationRunner>().MigrateAndLoadAsync(store,default);
        var scope=new LibraryScanScope(["movies"]);var snapshot=new LibrarySourceSnapshot(1,DateTimeOffset.UnixEpoch);var stable=new ImportedPlayableMovieIdentityFactory().Create();var oldKey=new ImportKey("movies","old.mp4");await CommitMapped(store,scope,oldKey,snapshot,stable);var before=Assert.Single(store.GetAll()).Id;
        var duplicate=new ImportedPlayableMovieIdentityFactory().Create();var keyA=new ImportKey("movies","a.mp4");var keyB=new ImportKey("movies","b.mp4");
        var entryA=Entry(keyA,snapshot,duplicate);var entryB=Entry(keyB,snapshot,duplicate);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>store.CommitCompletedScanAsync(new CompletedLibraryScan(DateTimeOffset.UtcNow,scope,[new MappedImport(keyA,snapshot,duplicate,entryA),new MappedImport(keyB,snapshot,duplicate,entryB)])).AsTask());
        Assert.Equal(before,Assert.Single(store.GetAll()).Id);
        Assert.False(Assert.Single(await store.ResolveImportIdentitiesAsync(scope,[new ImportIdentityRequest(keyA,snapshot)])).IsKnown);
    }
    [Fact]
    public async Task ReconciliationStatusesPreserveOrHideTheReadSnapshotAsSpecified()
    {
        var path = Path.Combine(Path.GetTempPath(), $"hispania-{Guid.NewGuid():N}.sqlite");
        using var services = new ServiceCollection().AddSqliteLibraryPersistence(path).BuildServiceProvider();
        var store = services.GetRequiredService<EfLibraryCatalogStore>();
        await services.GetRequiredService<SqliteMigrationRunner>().MigrateAndLoadAsync(store, default);
        var scope = new LibraryScanScope(["movies"]); var key = new ImportKey("movies", "film.mp4");
        var successful = new LibrarySourceSnapshot(200, DateTimeOffset.UnixEpoch); var identity = new ImportedPlayableMovieIdentityFactory().Create();
        await CommitMapped(store, scope, key, successful, identity);
        await store.CommitCompletedScanAsync(new CompletedLibraryScan(DateTimeOffset.UtcNow, scope, [new ProbeFailedImport(key, new LibrarySourceSnapshot(201, DateTimeOffset.UnixEpoch.AddHours(1)), "probe failed")]));
        var failed = Assert.Single(await store.ResolveImportIdentitiesAsync(scope, [new ImportIdentityRequest(key, successful)]));
        Assert.Equal(LibraryImportStatus.ProbeFailed, failed.LastKnownStatus); Assert.Equal(identity, failed.ReusableIdentity); Assert.Equal(identity.MediaEntityId, Assert.Single(store.GetAll()).Id);
        await store.CommitCompletedScanAsync(new CompletedLibraryScan(DateTimeOffset.UtcNow, scope, [new UnsupportedImport(key, successful, "unsupported")]));
        var unsupported = Assert.Single(await store.ResolveImportIdentitiesAsync(scope, [new ImportIdentityRequest(key, successful)]));
        Assert.Equal(LibraryImportStatus.Unsupported, unsupported.LastKnownStatus); Assert.Empty(store.GetAll());
        await store.CommitCompletedScanAsync(new CompletedLibraryScan(DateTimeOffset.UtcNow, scope, []));
        var disappeared = Assert.Single(await store.ResolveImportIdentitiesAsync(scope, [new ImportIdentityRequest(key, successful)]));
        Assert.Equal(LibraryImportStatus.Disappeared, disappeared.LastKnownStatus); Assert.Empty(store.GetAll());
        await CommitMapped(store, scope, key, successful, identity);
        Assert.Equal(identity.MediaEntityId, Assert.Single(store.GetAll()).Id);
    }

    [Fact]
    public async Task ChangedSnapshotDoesNotOfferThePreviousIdentity()
    {
        var path = Path.Combine(Path.GetTempPath(), $"hispania-{Guid.NewGuid():N}.sqlite"); using var services = new ServiceCollection().AddSqliteLibraryPersistence(path).BuildServiceProvider(); var store=services.GetRequiredService<EfLibraryCatalogStore>(); await services.GetRequiredService<SqliteMigrationRunner>().MigrateAndLoadAsync(store,default);
        var scope=new LibraryScanScope(["movies"]);var key=new ImportKey("movies","film.mp4");var oldSnapshot=new LibrarySourceSnapshot(200,DateTimeOffset.UnixEpoch);var oldIdentity=new ImportedPlayableMovieIdentityFactory().Create();await CommitMapped(store,scope,key,oldSnapshot,oldIdentity);
        var changed=new LibrarySourceSnapshot(201,DateTimeOffset.UnixEpoch);var resolution=Assert.Single(await store.ResolveImportIdentitiesAsync(scope,[new ImportIdentityRequest(key,changed)])); Assert.Null(resolution.ReusableIdentity);
        var newIdentity=new ImportedPlayableMovieIdentityFactory().Create();await CommitMapped(store,scope,key,changed,newIdentity);Assert.Equal(newIdentity.MediaEntityId,Assert.Single(store.GetAll()).Id);
    }

    [Fact]
    public async Task CompleteScanOnlyChangesImportsInsideItsScope()
    {
        var path=Path.Combine(Path.GetTempPath(),$"hispania-{Guid.NewGuid():N}.sqlite");using var services=new ServiceCollection().AddSqliteLibraryPersistence(path).BuildServiceProvider();var store=services.GetRequiredService<EfLibraryCatalogStore>();await services.GetRequiredService<SqliteMigrationRunner>().MigrateAndLoadAsync(store,default);
        var a=new LibraryScanScope(["movies-a"]);var b=new LibraryScanScope(["movies-b"]);var snapshot=new LibrarySourceSnapshot(1,DateTimeOffset.UnixEpoch);var keyA=new ImportKey("movies-a","a.mp4");var keyB=new ImportKey("movies-b","b.mp4");await CommitMapped(store,a,keyA,snapshot,new ImportedPlayableMovieIdentityFactory().Create());var idB=new ImportedPlayableMovieIdentityFactory().Create();await CommitMapped(store,b,keyB,snapshot,idB);
        await store.CommitCompletedScanAsync(new CompletedLibraryScan(DateTimeOffset.UtcNow,a,[]));
        var stateB=Assert.Single(await store.ResolveImportIdentitiesAsync(b,[new ImportIdentityRequest(keyB,snapshot)]));Assert.Equal(LibraryImportStatus.Active,stateB.LastKnownStatus);Assert.Equal(idB,stateB.ReusableIdentity);
    }

    [Theory]
    [InlineData(LibraryImportStatus.Active, "active")]
    [InlineData(LibraryImportStatus.Disappeared, "disappeared")]
    [InlineData(LibraryImportStatus.Unsupported, "unsupported")]
    [InlineData(LibraryImportStatus.ProbeFailed, "probe-failed")]
    public async Task ImportStatusesAreStoredAsStableTextCodes(LibraryImportStatus status, string expectedCode)
    {
        var path = Path.Combine(Path.GetTempPath(), $"hispania-{Guid.NewGuid():N}.sqlite");
        using var services = new ServiceCollection().AddSqliteLibraryPersistence(path).BuildServiceProvider();
        var store = services.GetRequiredService<EfLibraryCatalogStore>();
        await services.GetRequiredService<SqliteMigrationRunner>().MigrateAndLoadAsync(store, default);
        var scope = new LibraryScanScope(["movies"]);
        var key = new ImportKey("movies", "film.mp4");
        var snapshot = new LibrarySourceSnapshot(200, DateTimeOffset.UnixEpoch);
        var identity = new ImportedPlayableMovieIdentityFactory().Create();

        if (status == LibraryImportStatus.Active)
            await CommitMapped(store, scope, key, snapshot, identity);
        else
        {
            await CommitMapped(store, scope, key, snapshot, identity);
            IReadOnlyCollection<ObservedImportResult> observed = status switch
            {
                LibraryImportStatus.Disappeared => [],
                LibraryImportStatus.Unsupported => [new UnsupportedImport(key, snapshot, "unsupported")],
                LibraryImportStatus.ProbeFailed => [new ProbeFailedImport(key, snapshot, "probe failed")],
                _ => throw new ArgumentOutOfRangeException(nameof(status))
            };
            await store.CommitCompletedScanAsync(new CompletedLibraryScan(DateTimeOffset.UtcNow, scope, observed));
        }

        var factory = services.GetRequiredService<IDbContextFactory<LibraryCatalogDbContext>>();
        await using var context = await factory.CreateDbContextAsync();
        Assert.Equal(expectedCode, Assert.Single(context.Imports).StatusCode);
    }

    [Fact]
    public async Task CancellationAfterUnitOfWorkMutationRollsBackDatabaseAndKeepsReadSnapshot()
    {
        var path = Path.Combine(Path.GetTempPath(), $"hispania-{Guid.NewGuid():N}.sqlite");
        using var services = new ServiceCollection().AddSqliteLibraryPersistence(path).BuildServiceProvider();
        var store = services.GetRequiredService<EfLibraryCatalogStore>();
        await services.GetRequiredService<SqliteMigrationRunner>().MigrateAndLoadAsync(store, default);
        var scope = new LibraryScanScope(["movies"]);
        var snapshot = new LibrarySourceSnapshot(200, DateTimeOffset.UnixEpoch);
        var oldKey = new ImportKey("movies", "old.mp4");
        var oldIdentity = new ImportedPlayableMovieIdentityFactory().Create();
        await CommitMapped(store, scope, oldKey, snapshot, oldIdentity);
        var factory = services.GetRequiredService<IDbContextFactory<LibraryCatalogDbContext>>();
        using var cancellation = new CancellationTokenSource();
        var cancellingStore = new EfLibraryCatalogStore(factory, _ => { cancellation.Cancel(); return Task.CompletedTask; });
        await cancellingStore.LoadSnapshotAsync();
        var newKey = new ImportKey("movies", "new.mp4");
        var newIdentity = new ImportedPlayableMovieIdentityFactory().Create();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancellingStore.CommitCompletedScanAsync(
            new CompletedLibraryScan(DateTimeOffset.UtcNow, scope, [new MappedImport(newKey, snapshot, newIdentity, Entry(newKey, snapshot, newIdentity))]),
            cancellation.Token).AsTask());

        Assert.Equal(oldIdentity.MediaEntityId, Assert.Single(cancellingStore.GetAll()).Id);
        await using var context = await factory.CreateDbContextAsync();
        Assert.Single(context.Imports);
        Assert.Equal("active", Assert.Single(context.Imports).StatusCode);
        Assert.Single(context.Works);
        Assert.Single(context.MediaEntities);
        Assert.Single(context.Timelines);
        Assert.Equal(2, context.Assets.Count());
        Assert.Equal(2, context.Representations.Count());
        Assert.Equal(2, context.Bindings.Count());
        Assert.Equal(2, context.Coverages.Count());
    }

    [Fact]
    public async Task RehydratedCatalogPlansAndExecutesDirectPlayWithoutLosingIdentityOrSourceSafety()
    {
        var path = Path.Combine(Path.GetTempPath(), $"hispania-{Guid.NewGuid():N}.sqlite");
        var scope = new LibraryScanScope(["movies"]);
        var key = new ImportKey("movies", "film.mp4");
        var bytes = Enumerable.Range(0, 200).Select(value => (byte)value).ToArray();
        var snapshot = new LibrarySourceSnapshot(bytes.Length, DateTimeOffset.UnixEpoch);
        var identity = new ImportedPlayableMovieIdentityFactory().Create();
        await using (var first = new ServiceCollection().AddSqliteLibraryPersistence(path).BuildServiceProvider())
        {
            var firstStore = first.GetRequiredService<EfLibraryCatalogStore>();
            await first.GetRequiredService<SqliteMigrationRunner>().MigrateAndLoadAsync(firstStore, default);
            await CommitMapped(firstStore, scope, key, snapshot, identity);
        }

        using var restarted = new ServiceCollection().AddSqliteLibraryPersistence(path).BuildServiceProvider();
        var rehydrated = restarted.GetRequiredService<EfLibraryCatalogStore>();
        await restarted.GetRequiredService<SqliteMigrationRunner>().MigrateAndLoadAsync(rehydrated, default);
        IMediaCatalogReader catalog = restarted.GetRequiredService<IMediaCatalogReader>();
        var entry = Assert.Single(catalog.GetAll());
        var plan = Assert.IsType<DirectPlayPlanned>(new DirectPlayPlanner(catalog).Plan(entry.Id)).Plan;
        Assert.Equal(identity.MediaEntityId, plan.MediaEntityId);
        Assert.Equal(identity.MediaSourceId, plan.MediaSourceId);
        Assert.Equal(identity.MainVideo.TechnicalRepresentationId, plan.VideoTechnicalRepresentationId);
        Assert.Equal(identity.MainAudio.TechnicalRepresentationId, plan.AudioTechnicalRepresentationId);

        var executor = new DirectPlayExecutor(catalog, new ControlledSourceAccessor(bytes, DateTimeOffset.UnixEpoch));
        await using var full = await executor.ExecuteAsync(plan, new ByteRangeRequest.Full());
        var fullContent = Assert.IsType<FullContent>(full);
        Assert.Equal(bytes.Length, fullContent.TotalLength);
        using var fullOutput = new MemoryStream();
        await fullContent.Content.Stream.CopyToAsync(fullOutput);
        Assert.Equal(bytes, fullOutput.ToArray());

        await using var range = await executor.ExecuteAsync(plan, new ByteRangeRequest.Closed(100, 199));
        var partial = Assert.IsType<PartialContent>(range);
        Assert.Equal(100, partial.SelectedLength);
        using var partialOutput = new MemoryStream();
        await partial.Content.Stream.CopyToAsync(partialOutput);
        Assert.Equal(bytes[100..200], partialOutput.ToArray());

        await using var open = await executor.ExecuteAsync(plan, new ByteRangeRequest.From(100));
        Assert.Equal(100, Assert.IsType<PartialContent>(open).SelectedLength);

        var changedExecutor = new DirectPlayExecutor(catalog, new ControlledSourceAccessor(bytes, DateTimeOffset.UnixEpoch.AddSeconds(1)));
        await using var changed = await changedExecutor.ExecuteAsync(plan, new ByteRangeRequest.Full());
        Assert.IsType<SourceChanged>(changed);
    }

    [Fact]
    public async Task ReadinessIsMarkedOnlyAfterMigrationSnapshotLoadAndInitialScan()
    {
        var path = Path.Combine(Path.GetTempPath(), $"hispania-{Guid.NewGuid():N}.sqlite");
        var scope = new LibraryScanScope(["movies"]);
        var key = new ImportKey("movies", "film.mp4");
        var snapshot = new LibrarySourceSnapshot(200, DateTimeOffset.UnixEpoch);
        var identity = new ImportedPlayableMovieIdentityFactory().Create();
        using (var initial = new ServiceCollection().AddSqliteLibraryPersistence(path).BuildServiceProvider())
        {
            var initialStore = initial.GetRequiredService<EfLibraryCatalogStore>();
            await initial.GetRequiredService<SqliteMigrationRunner>().MigrateAndLoadAsync(initialStore, default);
            await CommitMapped(initialStore, scope, key, snapshot, identity);
        }

        using var restarted = new ServiceCollection().AddSqliteLibraryPersistence(path).BuildServiceProvider();
        var store = restarted.GetRequiredService<EfLibraryCatalogStore>();
        var readiness = new LibraryReadiness();
        await new PersistenceStartupService(restarted.GetRequiredService<SqliteMigrationRunner>(), store).StartAsync(default);
        Assert.False(readiness.IsReady);
        Assert.Equal(identity.MediaEntityId, Assert.Single(store.GetAll()).Id);

        var scanner = new LibraryScanner(new EmptyDiscovery(), new UnusedProbe(), new ProbedMediaMapper(), store, new ImportedPlayableMovieIdentityFactory(), scope);
        await new InitialLibraryScanService(scanner, readiness).StartAsync(default);
        Assert.True(readiness.IsReady);
    }

    private static async Task CommitMapped(EfLibraryCatalogStore store, LibraryScanScope scope, ImportKey key, LibrarySourceSnapshot snapshot, ImportedPlayableMovieIdentity identity)
    {
        var entry=Entry(key,snapshot,identity);
        await store.CommitCompletedScanAsync(new CompletedLibraryScan(DateTimeOffset.UtcNow,scope,[new MappedImport(key,snapshot,identity,entry)]));
    }
    private static MediaCatalogEntry Entry(ImportKey key, LibrarySourceSnapshot snapshot, ImportedPlayableMovieIdentity identity) => Assert.IsType<MediaMappingSucceeded>(new ProbedMediaMapper().Map(new DiscoveredMediaCandidate(new LibraryFileReference(key.RootKey,key.NormalizedRelativePath),snapshot.ByteLength,snapshot.LastModifiedUtc),Probe(),identity)).Entry;
    [Fact]
    public async Task FirstImportUnchangedRescanAndRestartRehydrateTheExactPlayableGraph()
    {
        var path = Path.Combine(Path.GetTempPath(), $"hispania-{Guid.NewGuid():N}.sqlite");
        var scope = new LibraryScanScope(["movies"]);
        var key = new ImportKey("movies", "film.mp4");
        var snapshot = new LibrarySourceSnapshot(200, DateTimeOffset.UnixEpoch);
        var identity = new ImportedPlayableMovieIdentityFactory().Create();
        await using (var services = new ServiceCollection().AddSqliteLibraryPersistence(path).BuildServiceProvider())
        {
            var store = services.GetRequiredService<EfLibraryCatalogStore>();
            await services.GetRequiredService<SqliteMigrationRunner>().MigrateAndLoadAsync(store, default);
            var first = Assert.Single(await store.ResolveImportIdentitiesAsync(scope, [new ImportIdentityRequest(key, snapshot)]));
            Assert.False(first.IsKnown);
            var entry = Assert.IsType<MediaMappingSucceeded>(new ProbedMediaMapper().Map(new DiscoveredMediaCandidate(new LibraryFileReference("movies", "film.mp4"), 200, DateTimeOffset.UnixEpoch), Probe(), identity)).Entry;
            await store.CommitCompletedScanAsync(new CompletedLibraryScan(DateTimeOffset.UtcNow, scope, [new MappedImport(key, snapshot, identity, entry)]));
            Assert.Equal(identity.MediaEntityId, Assert.Single(store.GetAll()).Id);
            var unchanged = Assert.Single(await store.ResolveImportIdentitiesAsync(scope, [new ImportIdentityRequest(key, snapshot)]));
            Assert.Equal(identity, unchanged.ReusableIdentity);
        }
        using var restarted = new ServiceCollection().AddSqliteLibraryPersistence(path).BuildServiceProvider();
        var rehydrated = restarted.GetRequiredService<EfLibraryCatalogStore>();
        await restarted.GetRequiredService<SqliteMigrationRunner>().MigrateAndLoadAsync(rehydrated, default);
        var result = Assert.Single(rehydrated.GetAll());
        Assert.Equal(identity.MediaEntityId, result.Id);
        Assert.Equal(identity.WorkId, result.Media.Work.Id);
        Assert.Equal(identity.TimelineId, result.Media.Timeline.Id);
        Assert.Equal(identity.MediaSourceId, result.Media.Source.Id);
        Assert.Equal(identity.MainVideo.AssetId, result.Media.VideoAsset.Id);
        Assert.Equal(identity.MainAudio.AssetId, result.Media.AudioAsset.Id);
        Assert.Equal(identity.MainVideo.TechnicalRepresentationId, result.Media.VideoAsset.TechnicalRepresentations.Single().Id);
        Assert.Equal(identity.MainAudio.TechnicalRepresentationId, result.Media.AudioAsset.TechnicalRepresentations.Single().Id);
        Assert.Single(result.Media.VideoAsset.TechnicalRepresentations.Single().SourceBindings);
        Assert.Single(result.Media.AudioAsset.TechnicalRepresentations.Single().SourceBindings);
        Assert.Equal(2, result.Media.Timeline.Coverages.Count);
    }

    private static ProbeSucceeded Probe() => new(new ProbeResult("mp4", TimeSpan.FromSeconds(2), [new VideoStreamObservation(0,"h264",1920,1080,new ObservedRational(24,1),8,null),new AudioStreamObservation(1,"aac",48000,2,null,null)]));

    private sealed class ControlledSourceAccessor(byte[] bytes, DateTimeOffset lastModified) : ISourceContentAccessor
    {
        public ValueTask<SourceContentAccessResult> OpenReadAsync(MediaSource source, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<SourceContentAccessResult>(new SourceContentOpened(
                new ReadableMediaContent(new MemoryStream(bytes, writable: false), new SourceContentFacts(bytes.Length, lastModified))));
    }

    private sealed class EmptyDiscovery : IMediaDiscovery
    {
        public async IAsyncEnumerable<DiscoveredMediaCandidate> DiscoverAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            cancellationToken.ThrowIfCancellationRequested();
            yield break;
        }
    }

    private sealed class UnusedProbe : IMediaProbe
    {
        public ValueTask<ProbeOutcome> ProbeAsync(DiscoveredMediaCandidate candidate, CancellationToken cancellationToken = default) => throw new InvalidOperationException("An empty scan must not probe media.");
    }
    [Fact]
    public async Task MigrationCreatesSchemaAndCanRunTwice()
    {
        var path = Path.Combine(Path.GetTempPath(), $"hispania-{Guid.NewGuid():N}.sqlite");
        ServiceProvider? services = null;
        try
        {
            services = new ServiceCollection().AddSqliteLibraryPersistence(path).BuildServiceProvider();
            var runner = services.GetRequiredService<SqliteMigrationRunner>();
            var store = services.GetRequiredService<EfLibraryCatalogStore>();
            await runner.MigrateAndLoadAsync(store, default);
            await runner.MigrateAndLoadAsync(store, default);
            Assert.True(File.Exists(path));
        }
        finally { services?.Dispose(); }
    }

    [Fact]
    public void ScalarConversionsRoundTripWithoutLoss()
    {
        var id = Guid.CreateVersion7();
        Assert.Equal(id, SqliteScalarConversions.FromCanonicalUuidBytes(SqliteScalarConversions.ToCanonicalUuidBytes(id)));
        var instant = new DateTimeOffset(2026, 9, 13, 12, 30, 45, TimeSpan.FromHours(2));
        var encoded = SqliteScalarConversions.ToDateTimeOffset(instant);
        Assert.Equal(instant, SqliteScalarConversions.FromDateTimeOffset(encoded.UtcTicks, encoded.OffsetMinutes));
        var decimalValue = 1234567890.123456789m;
        Assert.Equal(decimalValue, SqliteScalarConversions.FromDecimal(SqliteScalarConversions.ToDecimal(decimalValue)));
        Assert.Equal(TimeSpan.FromTicks(123), TimeSpan.FromTicks(123));
    }
}

public sealed class MetadataEnrichmentPersistenceTests
{
    [Fact]
    public async Task EnrichmentRoundTripsAcrossRestartAndIsIdempotent()
    {
        var path = Path.Combine(Path.GetTempPath(), $"hispania-metadata-{Guid.NewGuid():N}.sqlite");
        var target = EnrichmentTarget.For(WorkId.New());
        var identifier = new ExternalIdentifier(new ExternalIdentifierProvider("example"), new ExternalIdentifierNamespace("movie"), "42");
        var provenance = new MetadataProvenance(MetadataProvenanceOrigin.ExternalProvider, new DateTimeOffset(2026, 9, 14, 12, 0, 0, TimeSpan.FromHours(2)), identifier.Provider, identifier, .5m);
        var association = new ExternalIdentifierAssociation(ExternalIdentifierAssociationId.New(), identifier, target, new ExternalIdentifierAssociationLifecycleEntry(ExternalIdentifierAssociationStatus.Candidate, provenance.ObservedAt, provenance));
        association.TransitionTo(new ExternalIdentifierAssociationLifecycleEntry(ExternalIdentifierAssociationStatus.Confirmed, provenance.ObservedAt.AddMinutes(1), provenance));
        association.AddRelatedAssociation(new ExternalIdentifierAssociationRelation(ExternalIdentifierAssociationId.New(), ExternalIdentifierAssociationRelationKind.Alias, provenance, "Provider alias."));
        var assertions = new[]
        {
            Assertion(target, "text", MetadataValue.Text("Title"), provenance, MetadataAssertionStatus.Active),
            Assertion(target, "integer", MetadataValue.Integer(42), provenance, MetadataAssertionStatus.Active),
            Assertion(target, "decimal", MetadataValue.Decimal(12.34m), provenance, MetadataAssertionStatus.Active),
            Assertion(target, "boolean", MetadataValue.Boolean(true), provenance, MetadataAssertionStatus.Active),
            Assertion(target, "date", MetadataValue.Date(new DateOnly(2024, 2, 3)), provenance, MetadataAssertionStatus.Active),
            Assertion(target, "timestamp", MetadataValue.DateTimeOffset(provenance.ObservedAt), provenance, MetadataAssertionStatus.Active)
        };
        var artwork = new ArtworkReference(ArtworkReferenceId.New(), target, new ArtworkRole("poster"), new ArtworkLocator("https", "provider/image/42"), provenance);
        var refresh = new MetadataRefreshState(target, identifier.Provider, identifier, provenance.ObservedAt, provenance.ObservedAt, "mapping-1", "payload-1", MetadataRefreshStatus.Succeeded);
        var write = new MetadataEnrichmentWrite([association], assertions, [artwork], refresh);

        await using (var services = new ServiceCollection().AddSqliteLibraryPersistence(path).BuildServiceProvider())
        {
            await services.GetRequiredService<SqliteMigrationRunner>().MigrateAndLoadAsync(services.GetRequiredService<EfLibraryCatalogStore>(), default);
            var store = services.GetRequiredService<IMetadataEnrichmentStore>();
            Assert.True(await store.ApplyAsync(write));
            Assert.False(await store.ApplyAsync(write));
        }

        using var restarted = new ServiceCollection().AddSqliteLibraryPersistence(path).BuildServiceProvider();
        await restarted.GetRequiredService<SqliteMigrationRunner>().MigrateAndLoadAsync(restarted.GetRequiredService<EfLibraryCatalogStore>(), default);
        var snapshot = await restarted.GetRequiredService<IMetadataEnrichmentStore>().LoadAsync();
        var rehydrated = Assert.Single(snapshot.Associations);
        Assert.Equal(ExternalIdentifierAssociationStatus.Confirmed, rehydrated.CurrentStatus);
        Assert.Equal(2, rehydrated.LifecycleEntries.Count);
        Assert.Equal(provenance, rehydrated.LifecycleEntries[0].Evidence);
        Assert.Single(rehydrated.RelatedAssociations);
        Assert.Equal(6, snapshot.Assertions.Count);
        Assert.Contains(snapshot.Assertions, x => x.Value.Kind == MetadataValueKind.Integer && x.Value.IntegerValue == 42);
        Assert.Contains(snapshot.Assertions, x => x.Value.Kind == MetadataValueKind.Decimal && x.Value.DecimalValue == 12.34m);
        Assert.Contains(snapshot.Assertions, x => x.Value.Kind == MetadataValueKind.Boolean && x.Value.BooleanValue == true);
        Assert.Contains(snapshot.Assertions, x => x.Value.Kind == MetadataValueKind.Date && x.Value.DateValue == new DateOnly(2024, 2, 3));
        Assert.Equal(artwork.Locator, Assert.Single(snapshot.ArtworkReferences).Locator);
        Assert.Equal(refresh, Assert.Single(snapshot.RefreshStates));
    }

    private static MetadataAssertion Assertion(EnrichmentTarget target, string field, MetadataValue value, MetadataProvenance provenance, MetadataAssertionStatus status) => new(MetadataAssertionId.New(), target, new MetadataField(field), value, provenance, status);
}
