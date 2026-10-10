using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Hispania.Authorization.Application;
using Hispania.Identity.Application;
using Hispania.Platform.Contracts.Identity;
using Hispania.Platform.Contracts.Assets;
using Hispania.Platform.Contracts.Audit;
using Hispania.Platform.Contracts.Storage;
using Hispania.Platform.Contracts.Jobs;
using Hispania.Platform.Contracts.Maintenance;
using Hispania.Platform.Contracts.Events;
using Hispania.Platform.Persistence.Sqlite;
using Hispania.Platform.Storage.Filesystem;
using Hispania.Inventory.Persistence.Sqlite;
using Hispania.Inventory.Contracts;
using Hispania.DistributedWork.Persistence.Sqlite;
using Hispania.ModuleSystem.Contracts;
using Hispania.Runtime;
using Hispania.Library.Persistence.Sqlite;
using Hispania.Library.Persistence.EFCore;
using Hispania.Persistence.Schema;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Data.Sqlite;
using Xunit;
namespace Hispania.Compatibility.Tests;

[Trait("Category", "Phase16HistoricalDataUpgrade")]
public sealed partial class Phase16HistoricalDataUpgradeTests
{
    private static readonly string[] AdminPermissions=["platform.authorization.manage","platform.identity.read"];
    private static readonly string[] PartialPermissions=["platform.identity.read"];
    internal static Guid Id(int n) => Guid.Parse("00000000-0000-0000-0000-"+n.ToString("x12",CultureInfo.InvariantCulture));
    [Fact]
    public void FrozenHistoricalDataHasPinnedProvenanceMembershipAndHashes()
    {
        var bytes=File.ReadAllBytes(BaselineCorpus.PathFor("data-baseline-0.7.json"));
        Assert.Equal(HistoricalDataCorpus.ManifestHash,BaselineCorpus.Hash(bytes));
        using var manifest=JsonDocument.Parse(bytes);
        Assert.Equal(BaselineCorpus.SourceSha,manifest.RootElement.GetProperty("sourceSha").GetString());
        var rows=manifest.RootElement.GetProperty("artifacts").EnumerateArray().ToArray();Assert.Equal(15,rows.Length);
        var paths=rows.Select(r=>r.GetProperty("path").GetString()!).ToHashSet(StringComparer.Ordinal);Assert.Equal(15,paths.Count);
        foreach(var row in rows)
        {
            Assert.True(row.GetProperty("immutable").GetBoolean());Assert.NotEmpty(row.GetProperty("owner").EnumerateArray());
            var path=row.GetProperty("path").GetString()!;Assert.StartsWith("data/0.7/",path,StringComparison.Ordinal);
            Assert.DoesNotContain("..",path,StringComparison.Ordinal);Assert.DoesNotContain(':',path);
            Assert.Equal(row.GetProperty("sha256").GetString(),BaselineCorpus.Hash(File.ReadAllBytes(BaselineCorpus.PathFor(path))));
        }
        Assert.Equal(paths.Order(StringComparer.Ordinal),Directory.EnumerateFiles(HistoricalDataCorpus.PathFor(""),"*",SearchOption.AllDirectories).Select(p=>"data/0.7/"+Path.GetFileName(p)).Order(StringComparer.Ordinal));
    }
    [Theory]
    [InlineData("identity-authorization.sqlite")][InlineData("events.sqlite")][InlineData("jobs.sqlite")]
    [InlineData("assets.sqlite")][InlineData("audit.sqlite")][InlineData("inventory.sqlite")]
    [InlineData("coordinator.sqlite")][InlineData("media-initial.sqlite")][InlineData("media-library.sqlite")]
    public async Task HistoricalOwnerUpgradePreservesEveryOldRowColumnAndId(string file)
    {
        using var copy=new HistoricalDataCopy(file);var path=copy.PathFor(file);
        var before=SchemaObjects(path);
        await OpenOwnerAsync(file,path);
        HistoricalDataCorpus.AssertPreserved(file,path);
        var after=SchemaObjects(path);
        foreach(var row in before.Where(r=>r.Key!="__EFMigrationsHistory" && !r.Key.StartsWith("sqlite_",StringComparison.Ordinal)))
            if(!file.StartsWith("media-",StringComparison.Ordinal))Assert.Equal(row.Value,after[row.Key]);
        await OpenOwnerAsync(file,path);HistoricalDataCorpus.AssertPreserved(file,path);
        if(!file.StartsWith("media-",StringComparison.Ordinal))Assert.Equal(file=="identity-authorization.sqlite"?"2":"1",HistoricalDataCorpus.Scalar(path,"SELECT COUNT(*) FROM hispania_schema_history WHERE version=1 AND migration_id='0001_baseline07_adoption'"));
        else Assert.Equal("5",HistoricalDataCorpus.Scalar(path,"SELECT COUNT(*) FROM __EFMigrationsHistory"));
    }
    internal static async Task OpenOwnerAsync(string file,string path)
    {
        switch(file)
        {
            case "identity-authorization.sqlite":_ = new SqliteIdentityStore(path);_ = new SqlitePlatformAuthorizationStore(path);break;
            case "events.sqlite":using(new SqliteDurablePlatformEventBus(path)){}break;
            case "jobs.sqlite":_ = new SqlitePlatformJobStore(path);break;
            case "assets.sqlite":_ = new SqliteAssetMetadataRepository(path);break;
            case "audit.sqlite":_ = new SqlitePlatformAuditStore(path);break;
            case "inventory.sqlite":_ = new InventorySqliteStore(path);break;
            case "coordinator.sqlite":using(new SqliteDistributedWorkStateStore(path)){}break;
            case "media-initial.sqlite":case "media-library.sqlite":
                await using(var provider=new ServiceCollection().AddSqliteLibraryPersistence(path).BuildServiceProvider())
                    await provider.GetRequiredService<SqliteMigrationRunner>().MigrateAndLoadAsync(provider.GetRequiredService<EfLibraryCatalogStore>(),default);
                break;
            default:throw new ArgumentException("Unknown historical owner",nameof(file));
        }
    }
    private static Dictionary<string,string> SchemaObjects(string path)
    {
        using var db=HistoricalDataCorpus.Open(path);using var q=db.CreateCommand();q.CommandText="SELECT name,sql FROM sqlite_master WHERE sql IS NOT NULL ORDER BY name";
        using var r=q.ExecuteReader();var rows=new Dictionary<string,string>(StringComparer.Ordinal);while(r.Read())rows.Add(r.GetString(0),r.GetString(1));return rows;
    }
    [Fact]
    public async Task HistoricalPasswordsSessionsAndRbacKeepTheirSecurityMeaning()
    {
        using var copy=new HistoricalDataCopy("identity-authorization.sqlite");var path=copy.PathFor("identity-authorization.sqlite");
        var permissionsBefore=new Dictionary<int,string[]>();
        using(var historical=HistoricalDataCorpus.Open(path))
        {
            foreach(var user in new[]{1,2,3})
            {
                using var query=historical.CreateCommand();
                query.CommandText="SELECT DISTINCT p.permission_id FROM authorization_user_roles u JOIN authorization_role_permissions p ON p.role_id=u.role_id WHERE u.user_id=$user ORDER BY p.permission_id";
                query.Parameters.AddWithValue("$user",Id(user).ToString());
                using var rows=query.ExecuteReader();var permissions=new List<string>();while(rows.Read())permissions.Add(rows.GetString(0));
                permissionsBefore.Add(user,permissions.ToArray());
            }
        }
        var store=new SqliteIdentityStore(path);var identity=new IdentityService(store);var authStore=new SqlitePlatformAuthorizationStore(path);var auth=new AuthorizationService(authStore,identity);
        var credential=await store.GetCredentialAsync(new(Id(1)),default);
        var login=await identity.LoginAsync("TEST_ADMIN","TEST-ONLY-Phase16-password");Assert.True(login.Succeeded);Assert.Equal(Id(1),login.User!.Id.Value);
        Assert.Equal(credential,await store.GetCredentialAsync(new(Id(1)),default));
        Assert.False((await identity.LoginAsync("test_disabled","TEST-ONLY-Phase16-password")).Succeeded);
        Assert.NotNull(await identity.ValidateSessionAsync("TEST-ONLY-valid-session"));
        Assert.Null(await identity.ValidateSessionAsync("TEST-ONLY-revoked-session"));Assert.Null(await identity.ValidateSessionAsync("TEST-ONLY-expired-session"));
        Assert.Equal(AdminPermissions,(await auth.GetEffectivePermissionsAsync(new(Id(1)))).Order(StringComparer.Ordinal));
        Assert.Equal(PartialPermissions,await auth.GetEffectivePermissionsAsync(new(Id(2))));
        Assert.False(await auth.HasPermissionAsync(new(Id(2)),"platform.authorization.manage"));Assert.Empty(await auth.GetEffectivePermissionsAsync(new(Id(3))));
        foreach(var user in new[]{1,2,3})
            Assert.Equal(permissionsBefore[user],(await auth.GetEffectivePermissionsAsync(new(Id(user)))).Order(StringComparer.Ordinal));
        var admin=new AuthorizationAdministrationService(authStore,identity,configuredBootstrapToken:"TEST-ONLY-bootstrap");
        Assert.False(await admin.BootstrapAdministratorAsync(new(Id(3)),"TEST-ONLY-bootstrap"));Assert.Equal("1",HistoricalDataCorpus.Scalar(path,"SELECT consumed FROM authorization_bootstrap"));
    }
    [Fact]
    public async Task HistoricalJobsPreserveIdempotencyAttemptsAndRecoverExpiredRunningLease()
    {
        using var copy=new HistoricalDataCopy("jobs.sqlite");var path=copy.PathFor("jobs.sqlite");var jobs=new SqlitePlatformJobStore(path);
        HistoricalDataCorpus.AssertPreserved("jobs.sqlite",path);
        var original=await jobs.GetAsync(Id(41),default);Assert.NotNull(original);
        var duplicate=await jobs.CreateAsync(original with{JobId=Guid.NewGuid()},default);Assert.True(duplicate.Duplicate);Assert.Equal(Id(41),duplicate.Job.JobId);
        // Isolate the historical running job without deleting any evidence from the frozen fixture.
        HistoricalDataCorpus.Execute(path,"UPDATE platform_jobs SET scheduled_at='2099-01-01T00:00:00.0000000+00:00' WHERE status=0");
        var recovered=await jobs.AcquireNextAsync(DateTimeOffset.UtcNow,TimeSpan.FromMinutes(1),default);Assert.NotNull(recovered);Assert.Equal(Id(43),recovered.JobId);Assert.Equal(2,recovered.AttemptCount);
        var attempts=await jobs.GetAttemptsAsync(Id(43),default);Assert.Equal(2,attempts.Count);Assert.Equal(PlatformJobAttemptOutcomeV1.Recovered,attempts[0].Outcome);
        Assert.Equal(PlatformJobStatusV1.Pending,(await jobs.GetAsync(Id(42),default))!.Status);
        Assert.Equal(PlatformJobStatusV1.Cancelled,(await jobs.GetAsync(Id(46),default))!.Status);
        Assert.Equal(PlatformJobStatusV1.DeadLettered,(await jobs.GetAsync(Id(47),default))!.Status);
    }
    [Fact]
    public async Task HistoricalEventsRecoverProcessingWithoutLosingSubscribersOrAttempts()
    {
        using var copy=new HistoricalDataCopy("events.sqlite");var path=copy.PathFor("events.sqlite");using var events=new SqliteDurablePlatformEventBus(path);
        var delivered=new List<(Guid,string)>();events.DeliveryHandler=(e,s,_)=>{delivered.Add((e.EventId,s));return ValueTask.CompletedTask;};
        Assert.Equal(6,(await events.ListDeliveriesAsync()).Count);Assert.Equal(2,(await events.GetKnownSubscribersAsync()).Count);
        await events.DispatchOnceAsync();
        var recovered=(await events.ListDeliveriesAsync()).Single(d=>d.DeliveryId==Id(133));Assert.Equal(DurableDeliveryStatus.Delivered,recovered.Status);Assert.Equal(2,recovered.AttemptCount);
        Assert.Contains((Id(33),"test-a"),delivered);Assert.Contains((Id(32),"test-b"),delivered);
        Assert.Equal(2,(await events.GetDeliveryAttemptsAsync(Id(133))).Count);
        Assert.Equal(DurableDeliveryStatus.DeadLettered,(await events.ListDeliveriesAsync()).Single(d=>d.DeliveryId==Id(135)).Status);
        Assert.Equal("5",HistoricalDataCorpus.Scalar(path,"SELECT COUNT(*) FROM Events"));Assert.Equal("6",HistoricalDataCorpus.Scalar(path,"SELECT COUNT(*) FROM Deliveries"));
    }
    [Fact]
    public async Task AssetsLegacyStorageAuditAndInventoryRemainUsable()
    {
        using var assets=new HistoricalDataCopy("assets.sqlite");var path=assets.PathFor("assets.sqlite");var metadata=new SqliteAssetMetadataRepository(path);
        foreach(var file in new[]{new string('a',32)+".blob",new string('B',32)+".blob"})File.Copy(HistoricalDataCorpus.PathFor(file),assets.PathFor(file));
        var provider=new FilesystemStorageProviderV1(assets.DirectoryPath);
        foreach(var n in new[]{51,52})
        { var old=await metadata.GetAsync(new(Id(n)));Assert.NotNull(old);using var stream=await provider.OpenReadAsync(new(old.ContentHandle));Assert.NotNull(stream);using var reader=new StreamReader(stream);Assert.Equal(n==51?"hello":"world",await reader.ReadToEndAsync());Assert.DoesNotContain(assets.DirectoryPath,old.ContentHandle,StringComparison.Ordinal); }
        using var content=new MemoryStream(new byte[]{1});var write=await provider.WriteAsync(content,10);Assert.StartsWith("storage://filesystem/",write.Handle.Value,StringComparison.Ordinal);
        using var audit=new HistoricalDataCopy("audit.sqlite");var auditStore=new SqlitePlatformAuditStore(audit.PathFor("audit.sqlite"));
        Assert.Equal(2,(await auditStore.QueryAsync(new())).Items.Count);await auditStore.WriteAsync(new(DateTimeOffset.UtcNow,null,"test-new","test","append","test","id","success",new Dictionary<string,string>()));Assert.Equal(3,(await auditStore.QueryAsync(new())).Items.Count);
        using var inventory=new HistoricalDataCopy("inventory.sqlite");var inventoryStore=new InventorySqliteStore(inventory.PathFor("inventory.sqlite"));
        Assert.Equal(7,Assert.Single(await inventoryStore.GetStockAsync()).Quantity);Assert.Equal(2,(await inventoryStore.GetMovementsAsync(new(Id(61)))).Count);Assert.Equal(Id(51),Assert.Single(Assert.Single(await inventoryStore.GetItemsAsync()).Assets).Id.Value);
        Assert.Single(await inventoryStore.GetWarehousesAsync());Assert.Single(await inventoryStore.GetLocationsAsync());
        Assert.Throws<SqliteException>(()=>HistoricalDataCorpus.Execute(inventory.PathFor("inventory.sqlite"),"UPDATE stock SET quantity=-1"));
        Assert.Throws<SqliteException>(()=>HistoricalDataCorpus.Execute(inventory.PathFor("inventory.sqlite"),"INSERT INTO items VALUES('duplicate','TEST-HISTORICAL','duplicate')"));
        Assert.Throws<SqliteException>(()=>HistoricalDataCorpus.Execute(inventory.PathFor("inventory.sqlite"),"INSERT INTO stock VALUES('missing','missing',1)"));
    }
    [Fact]
    public void HistoricalCoordinatorHighWaterSurvivesUpgradeAndRejectsStaleResults()
    {
        using var copy=new HistoricalDataCopy("coordinator.sqlite");using var store=new SqliteDistributedWorkStateStore(copy.PathFor("coordinator.sqlite"));
        var old=store.Load()!;Assert.Equal(7,old.Jobs.Single(j=>j.Request.JobId=="test-historical-work").Generation);
        var coordinator=new DistributedWorkCoordinator(store);const string node="test-new-worker",cert="1616161616161616161616161616161616161616161616161616161616161616";
        var cap=new DistributedCapabilityV1("test.capability","1.0");coordinator.AuthorizeWorker(node,[cap],cert);
        Assert.True(coordinator.RegisterWorker(node,cert,new("test","test",2,1024,1,[],[cap])));
        var request=coordinator.ListJobs().Single(j=>j.Request.JobId=="test-historical-work").Request;var lease=coordinator.Assign(request,DateTimeOffset.UtcNow,TimeSpan.FromMinutes(1),true)!;
        Assert.Equal(8,lease.Generation);Assert.False(coordinator.Complete(node,request.JobId,7,JsonSerializer.SerializeToElement("stale")));
        Assert.True(coordinator.Start(node,request.JobId,lease.Generation));Assert.True(coordinator.Complete(node,request.JobId,lease.Generation,JsonSerializer.SerializeToElement("current")));
        Assert.False(coordinator.Fail(node,request.JobId,7,"TEST_STALE"));Assert.Equal(9,coordinator.ListJobs().Single(j=>j.Request.JobId=="test-historical-completed").Generation);
    }

    [Fact]
    public async Task Phase22MaintenanceNeverReducesCoordinatorGenerationTenHighWater()
    {
        using var copy = new HistoricalDataCopy("coordinator.sqlite");
        var path = copy.PathFor("coordinator.sqlite");
        using (var store = new SqliteDistributedWorkStateStore(path))
        {
            var state = store.Load()!;
            var jobs = state.Jobs.Select(job => job.Request.JobId == "test-historical-work"
                ? job with { Generation = 10, State = DistributedJobStateV1.Completed,
                    LeaseExpiresAtUtc = null, Result = JsonSerializer.SerializeToElement("generation-ten") }
                : job).ToArray();
            store.Save(state with { Jobs = jobs });
        }
        var journal = new SqliteMaintenanceRunStoreV1(copy.PathFor("maintenance.sqlite"));
        var now = DateTimeOffset.UtcNow;
        var plan = new RetentionPlanV1(Guid.NewGuid(), new("platform.jobs"), new(false), now,
            now.AddMinutes(10), 0, 1, null, null, null, "No deletion", []);
        await journal.SavePlanAsync(plan, default);
        using var restarted = new SqliteDistributedWorkStateStore(path);
        Assert.Equal(10, restarted.Load()!.Jobs.Single(job => job.Request.JobId == "test-historical-work").Generation);
        var coordinator = new DistributedWorkCoordinator(restarted);
        Assert.False(coordinator.Complete("test-new-worker", "test-historical-work", 9,
            JsonSerializer.SerializeToElement("stale")));
        Assert.Equal(10, restarted.Load()!.Jobs.Single(job => job.Request.JobId == "test-historical-work").Generation);
    }
}
