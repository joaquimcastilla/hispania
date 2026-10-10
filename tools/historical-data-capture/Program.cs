using Hispania.Platform.Persistence.Sqlite;
using Hispania.Inventory.Persistence.Sqlite;
using Hispania.DistributedWork.Persistence.Sqlite;
using Hispania.Library.Persistence.Sqlite;
using Hispania.Library.Persistence.PostgreSql;
using Hispania.Library.Persistence.EFCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Data.Sqlite;
if (args.Length==2 && args[0]=="--experiment")
{
    if (Directory.Exists(args[1]))throw new InvalidOperationException("Capture requires new staging directory.");Directory.CreateDirectory(args[1]);
    CaptureExperiment(Path.Combine(args[1],"playback-experiment.json"));return;
}
if (args.Length == 2 && args[0] == "--verify")
{
    var staging=Path.GetFullPath(args[1]);
    var identity=new SqliteIdentityStore(Path.Combine(staging,"identity-authorization.sqlite"));
    var credential=await identity.GetCredentialAsync(new(Guid.Parse("00000000-0000-0000-0000-000000000001")),default);
    var verify=typeof(Hispania.Identity.Application.IdentityService).GetMethod("VerifyPassword",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Static)!;
    if (!(bool)verify.Invoke(null,["TEST-ONLY-Phase16-password",credential])!) throw new InvalidOperationException("Historical password verifier rejected populated fixture.");
    var metadata=new SqliteAssetMetadataRepository(Path.Combine(staging,"assets.sqlite"));
    var provider=new Hispania.Platform.Storage.Filesystem.FilesystemStorageProviderV1(staging);
    foreach (var id in new[]{"00000000-0000-0000-0000-000000000033","00000000-0000-0000-0000-000000000034"})
    {
        var asset=await metadata.GetAsync(new(Guid.Parse(id)));
        using var content=await provider.OpenReadAsync(new(asset!.ContentHandle));
        if (content is null || content.Length!=5)throw new InvalidOperationException("Historical storage reader rejected populated fixture.");
    }
    using(var state=new SqliteDistributedWorkStateStore(Path.Combine(staging,"coordinator.sqlite")))
        if (state.Load()!.Jobs[0].Request.ContractId!="test.capability")throw new InvalidOperationException("Historical coordinator rejected populated DTO shape.");
    var mediaOptions=new DbContextOptionsBuilder<LibraryCatalogDbContext>().UseSqlite($"Data Source={Path.Combine(staging,"media-library.sqlite")}", p=>p.MigrationsAssembly(typeof(SqliteLibraryPersistence).Assembly.FullName)).Options;
    var media=new EfMetadataEnrichmentStore(new CaptureContextFactory(mediaOptions),new CaptureConstraintClassifier());
    if (!await media.WorkExistsAsync(new Hispania.Media.Domain.Identity.WorkId(Guid.Parse("00000000-0000-0000-0000-000000000047"))))throw new InvalidOperationException("OLD Media app cannot read the seeded UUID representation; fixture instrumentation is invalid.");
    var mediaAssertions=await media.LoadForTargetAsync(Hispania.Media.Domain.Enrichment.EnrichmentTarget.For(new Hispania.Media.Domain.Identity.WorkId(Guid.Parse("00000000-0000-0000-0000-000000000047"))));
    if (mediaAssertions.Assertions.Single().Value.TextValue!="Manual historical title")throw new InvalidOperationException("OLD Media reader rejected metadata.");
    SqliteConnection.ClearAllPools();
    Console.WriteLine("Archived verifier accepted TEST ONLY password, old/new storage handles, coordinator DTOs and Media identities/manual metadata.");return;
}
var directory = Path.GetFullPath(args.Single());
if (Directory.Exists(directory)) throw new InvalidOperationException("Capture requires a NEW staging directory; it never repairs a frozen baseline.");
Directory.CreateDirectory(directory);
string FilePath(string name) => Path.Combine(directory, name + ".sqlite");
_ = new SqliteIdentityStore(FilePath("identity-authorization"));
_ = new SqlitePlatformAuthorizationStore(FilePath("identity-authorization"));
_ = new SqliteDurablePlatformEventBus(FilePath("events"));
_ = new SqlitePlatformJobStore(FilePath("jobs"));
_ = new SqliteAssetMetadataRepository(FilePath("assets"));
_ = new SqlitePlatformAuditStore(FilePath("audit"));
_ = new InventorySqliteStore(FilePath("inventory"));
using (var coordinator = new SqliteDistributedWorkStateStore(FilePath("coordinator"))) { }
foreach (var (name, target) in new[] { ("media-initial", "20260922000100_initial"), ("media-library", (string?)null) })
{
    var options = new DbContextOptionsBuilder<LibraryCatalogDbContext>().UseSqlite($"Data Source={FilePath(name)}", p => p.MigrationsAssembly(typeof(SqliteLibraryPersistence).Assembly.FullName)).Options;
    await using var db = new LibraryCatalogDbContext(options);
    await db.GetService<IMigrator>().MigrateAsync(target);
}
var pgOptions = new DbContextOptionsBuilder<LibraryCatalogDbContext>().UseNpgsql("Host=localhost;Database=capture_script_only;Username=TEST_ONLY", p => p.MigrationsAssembly(typeof(PostgreSqlLibraryPersistence).Assembly.FullName)).Options;
await using (var pg = new LibraryCatalogDbContext(pgOptions))
    await File.WriteAllTextAsync(Path.Combine(directory, "media-postgresql-schema.sql"), pg.GetService<IMigrator>().GenerateScript());
CaptureExperiment(Path.Combine(directory,"playback-experiment.json"));
SqliteConnection.ClearAllPools();
Console.WriteLine("Archived producer created historical schemas. Run the separate deterministic row population step before reviewing/freezing.");

static void CaptureExperiment(string path)
{
    var store=new Hispania.Server.JsonPlaybackExperimentStore(path);
    store.Save(new(1,"TEST-ONLY-historical-experiment",7,"disabled","test.planner","1.0",[new("test.module","1.0.0","test.planner","1.0",3,10000)],"test.module",null,"active-only",3,[],new string('1',64),DateTimeOffset.UnixEpoch));
    store.SaveAssignment("TEST-ONLY-historical-experiment",new string('2',64),new Hispania.Runtime.VariantIdentity("test.module","test.planner",new Version(1,0),3,new Version(1,0,0)),new DateTimeOffset(2099,1,1,0,0,0,TimeSpan.Zero));
}

internal sealed class CaptureContextFactory(DbContextOptions<LibraryCatalogDbContext> options) : IDbContextFactory<LibraryCatalogDbContext>
{
    public LibraryCatalogDbContext CreateDbContext() => new(options);
    public Task<LibraryCatalogDbContext> CreateDbContextAsync(CancellationToken cancellationToken=default) => Task.FromResult(CreateDbContext());
}

internal sealed class CaptureConstraintClassifier : IUniqueConstraintViolationClassifier
{ public bool IsConfirmedExternalIdentifierConflict(DbUpdateException exception) => false; }
