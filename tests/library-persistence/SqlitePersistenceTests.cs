using Hispania.Library.Persistence.EFCore;
using Hispania.Library.Persistence.Sqlite;
using Hispania.Library.Application;
using Hispania.Library.Contracts;
using Hispania.Media.Domain.Identity;
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
