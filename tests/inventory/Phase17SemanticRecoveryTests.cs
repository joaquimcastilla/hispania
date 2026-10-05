using System.Globalization;
using System.Text.Json;
using Hispania.Authorization.Application;
using Hispania.Identity.Application;
using Hispania.Platform.Contracts.Jobs;
using Hispania.Platform.Contracts.Events;
using Hispania.Platform.Persistence.Sqlite;
using Hispania.Platform.Storage.Filesystem;
using Hispania.DistributedWork.Persistence.Sqlite;
using Hispania.ModuleSystem.Contracts;
using Hispania.Runtime;
using Hispania.Inventory.Persistence.Sqlite;
using Hispania.Library.Persistence.Sqlite;
using Hispania.Library.Persistence.EFCore;
using Hispania.Media.Domain.Identity;
using Hispania.Media.Domain.Enrichment;
using Hispania.Metadata.Contracts;
using Hispania.Server;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Hispania.Inventory.Tests;

[Trait("Category", "Phase17BackupRestore")]
public sealed class Phase17SemanticRecoveryTests
{
    private static readonly string[] AdminPermissions = ["platform.authorization.manage", "platform.identity.read"];
    private static readonly JsonSerializerOptions ManifestJson = new() { PropertyNameCaseInsensitive = true };
    private static Guid Id(int value) => Guid.Parse("00000000-0000-0000-0000-" + value.ToString("x12", CultureInfo.InvariantCulture));
    private static async Task RestoreAsync(Phase17BackupRestoreTests.Installation fixture)
    {
        await BackupInstallation.CreateAsync(fixture.Source, fixture.Bundle, default);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(fixture.Source, true);
        await BackupInstallation.RestoreAsync(fixture.Bundle, fixture.Target, default);
    }

    [Fact]
    public async Task RestoredIdentityKeepsCredentialSessionsDisabledUsersPermissionsAndConsumedBootstrap()
    {
        using var fixture = new Phase17BackupRestoreTests.Installation(); await RestoreAsync(fixture);
        var path = Path.Combine(fixture.Target, "platform", "identity.sqlite"); var store = new SqliteIdentityStore(path);
        var identity = new IdentityService(store); var credentials = await store.GetCredentialAsync(new(Id(1)), default);
        var login = await identity.LoginAsync("TEST_ADMIN", "TEST-ONLY-Phase16-password"); Assert.True(login.Succeeded); Assert.Equal(Id(1), login.User!.Id.Value);
        Assert.Equal(credentials, await store.GetCredentialAsync(new(Id(1)), default));
        Assert.NotNull(await identity.ValidateSessionAsync("TEST-ONLY-valid-session"));
        Assert.Null(await identity.ValidateSessionAsync("TEST-ONLY-revoked-session")); Assert.Null(await identity.ValidateSessionAsync("TEST-ONLY-expired-session"));
        Assert.False((await identity.LoginAsync("test_disabled", "TEST-ONLY-Phase16-password")).Succeeded);
        var authorizationStore = new SqlitePlatformAuthorizationStore(path); var authorization = new AuthorizationService(authorizationStore, identity);
        Assert.Equal(AdminPermissions, (await authorization.GetEffectivePermissionsAsync(new(Id(1)))).Order(StringComparer.Ordinal));
        Assert.Empty(await authorization.GetEffectivePermissionsAsync(new(Id(3))));
        var administration = new AuthorizationAdministrationService(authorizationStore, identity, configuredBootstrapToken: "reprovisioned-bootstrap");
        Assert.False(await administration.BootstrapAdministratorAsync(new(Id(3)), "reprovisioned-bootstrap"));
    }

    [Fact]
    public async Task RestoredJobsRecoverRunningAndKeepScheduleIdempotencyAndHistory()
    {
        using var fixture = new Phase17BackupRestoreTests.Installation(); await RestoreAsync(fixture);
        var path = Path.Combine(fixture.Target, "platform", "jobs.sqlite"); var jobs = new SqlitePlatformJobStore(path);
        var original = (await jobs.GetAsync(Id(41), default))!;
        var duplicate = await jobs.CreateAsync(original with { JobId = Guid.NewGuid() }, default); Assert.True(duplicate.Duplicate); Assert.Equal(Id(41), duplicate.Job.JobId);
        Assert.Equal(PlatformJobStatusV1.Pending, (await jobs.GetAsync(Id(42), default))!.Status);
        Assert.Equal(PlatformJobStatusV1.Succeeded, (await jobs.GetAsync(Id(44), default))!.Status);
        using (var db = Phase17BackupRestoreTests.Open(path)) { using var q = db.CreateCommand(); q.CommandText = "UPDATE platform_jobs SET scheduled_at='2099-01-01T00:00:00.0000000+00:00' WHERE status=0"; q.ExecuteNonQuery(); }
        var acquired = await jobs.AcquireNextAsync(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1), default);
        Assert.NotNull(acquired); Assert.Equal(Id(43), acquired.JobId); Assert.Equal(2, acquired.AttemptCount);
        Assert.Equal(PlatformJobAttemptOutcomeV1.Recovered, (await jobs.GetAttemptsAsync(Id(43), default))[0].Outcome);
        Assert.Equal(PlatformJobStatusV1.DeadLettered, (await jobs.GetAsync(Id(47), default))!.Status);
    }

    [Fact]
    public async Task RestoredEventsRecoverProcessingWithSubscribersAttemptsAndDlq()
    {
        using var fixture = new Phase17BackupRestoreTests.Installation(); await RestoreAsync(fixture);
        using var events = new SqliteDurablePlatformEventBus(Path.Combine(fixture.Target, "platform", "events.sqlite"));
        Assert.Equal(6, (await events.ListDeliveriesAsync()).Count); Assert.Equal(2, (await events.GetKnownSubscribersAsync()).Count);
        events.DeliveryHandler = (_, _, _) => ValueTask.CompletedTask;
        await events.DispatchOnceAsync();
        var recovered = (await events.ListDeliveriesAsync()).Single(delivery => delivery.DeliveryId == Id(133));
        Assert.Equal(DurableDeliveryStatus.Delivered, recovered.Status); Assert.Equal(2, recovered.AttemptCount);
        Assert.Equal(2, (await events.GetDeliveryAttemptsAsync(Id(133))).Count);
        Assert.Equal(DurableDeliveryStatus.DeadLettered, (await events.ListDeliveriesAsync()).Single(delivery => delivery.DeliveryId == Id(135)).Status);
    }

    [Fact]
    public async Task RestoredCoordinatorAdvancesGenerationAndRejectsPreDisasterResult()
    {
        using var fixture = new Phase17BackupRestoreTests.Installation(); await RestoreAsync(fixture);
        using var store = new SqliteDistributedWorkStateStore(Path.Combine(fixture.Target, "coordinator.sqlite"));
        var old = store.Load()!.Jobs.Single(job => job.Request.JobId == "test-historical-work");
        var coordinator = new DistributedWorkCoordinator(store); const string node = "restored-worker", cert = "1717171717171717171717171717171717171717171717171717171717171717";
        var capability = new DistributedCapabilityV1("test.capability", "1.0"); coordinator.AuthorizeWorker(node, [capability], cert);
        Assert.True(coordinator.RegisterWorker(node, cert, new("test", "test", 2, 1024, 1, [], [capability])));
        var request = coordinator.ListJobs().Single(job => job.Request.JobId == old.Request.JobId).Request;
        var lease = coordinator.Assign(request, DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1), true)!;
        Assert.True(lease.Generation > old.Generation);
        Assert.False(coordinator.Complete(node, old.Request.JobId, old.Generation, JsonSerializer.SerializeToElement("pre-disaster")));
        Assert.True(coordinator.Start(node, old.Request.JobId, lease.Generation));
        Assert.True(coordinator.Complete(node, old.Request.JobId, lease.Generation, JsonSerializer.SerializeToElement("current")));
    }

    [Fact]
    public async Task RestoredAssetsAuditAndInventoryRemainReadableAndAppendableThroughOwners()
    {
        using var fixture = new Phase17BackupRestoreTests.Installation(); await RestoreAsync(fixture);
        var assets = new SqliteAssetMetadataRepository(Path.Combine(fixture.Target, "platform", "assets.sqlite")); var storage = new FilesystemStorageProviderV1(Path.Combine(fixture.Target, "storage"));
        foreach (var value in new[] { 51, 52 })
        {
            var asset = (await assets.GetAsync(new(Id(value))))!; await using var bytes = await storage.OpenReadAsync(new(asset.ContentHandle));
            Assert.NotNull(bytes); using var reader = new StreamReader(bytes); Assert.Equal(value == 51 ? "hello" : "world", await reader.ReadToEndAsync());
        }
        var audit = new SqlitePlatformAuditStore(Path.Combine(fixture.Target, "platform", "audit.sqlite"));
        Assert.Equal(2, (await audit.QueryAsync(new())).Items.Count);
        await audit.WriteAsync(new(DateTimeOffset.UtcNow, null, "restored", "test", "append", "test", "id", "success", new Dictionary<string, string>()));
        Assert.Equal(3, (await audit.QueryAsync(new())).Items.Count);
        var inventory = new InventorySqliteStore(Path.Combine(fixture.Target, "inventory.sqlite"));
        Assert.Equal(7, Assert.Single(await inventory.GetStockAsync()).Quantity); Assert.Equal(2, (await inventory.GetMovementsAsync(new(Id(61)))).Count);
        Assert.Equal(Id(51), Assert.Single(Assert.Single(await inventory.GetItemsAsync()).Assets).Id.Value);
        Assert.Single(await inventory.GetWarehousesAsync()); Assert.Single(await inventory.GetLocationsAsync());
    }

    [Fact]
    public async Task RestoredHistoricalMediaMigratesAndKeepsManualMetadataArtworkAndLocks()
    {
        using var fixture = new Phase17BackupRestoreTests.Installation(); await RestoreAsync(fixture);
        await using var provider = new ServiceCollection().AddSqliteLibraryPersistence(Path.Combine(fixture.Target, "media.sqlite")).BuildServiceProvider();
        await provider.GetRequiredService<SqliteMigrationRunner>().MigrateAndLoadAsync(provider.GetRequiredService<EfLibraryCatalogStore>(), default);
        var work = new WorkId(Id(71)); var metadata = provider.GetRequiredService<EfMetadataEnrichmentStore>(); Assert.True(await metadata.WorkExistsAsync(work));
        var snapshot = await metadata.LoadForTargetAsync(EnrichmentTarget.For(work)); Assert.Equal("Manual historical title", Assert.Single(snapshot.Assertions).Value.TextValue);
        Assert.Single(snapshot.ArtworkReferences); Assert.True((await provider.GetRequiredService<IMetadataSettingsStore>().GetSettingsAsync(work)).LockItemMetadata);
    }

    [Fact]
    public async Task InstalledSignedPackageBytesAndPublisherRevocationSurviveRestoreWithoutActivationPrivileges()
    {
        using var fixture = new Phase17BackupRestoreTests.Installation();
        var directory = new DirectoryInfo(AppContext.BaseDirectory); while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Hispania.sln"))) directory = directory.Parent;
        var corpus = Path.Combine(directory!.FullName, "compatibility");
        var manifest = JsonSerializer.Deserialize<ModuleManifest>(File.ReadAllText(Path.Combine(corpus, "manifests", "0.7", "greeting.json")), ManifestJson)!;
        var publicKey = File.ReadAllText(Path.Combine(corpus, "manifests", "0.7", "TEST-ONLY-publisher-public.pem"));
        var publisherKeys = new Dictionary<string, string> { ["TEST-ONLY-baseline-07"] = publicKey };
        var registry = new ModuleRegistry(); var runtime = new ModuleRuntime(registry);
        using (var source = new ModuleExtensionCatalog(Path.Combine(fixture.Source, "extensions"), new Version(1, 0, 0), runtime, registry, new Dictionary<string, Func<IModule>>(), [manifest.Id], trustedPublisherKeys: publisherKeys, requirePublisherSignature: true))
        {
            await using var package = File.OpenRead(Path.Combine(corpus, "packages", "0.7", "greeting.hispania")); await source.InstallAsync(package);
            Assert.Equal(PublisherSignatureStatus.Trusted, Assert.Single(source.Discover()).PublisherStatus);
        }
        var original = Directory.GetFiles(Path.Combine(fixture.Source, "extensions"), "*", SearchOption.AllDirectories)
            .ToDictionary(path => Path.GetRelativePath(fixture.Source, path), path => System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)));
        File.WriteAllText(Path.Combine(fixture.Source, "portable.json"), JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["Persistence:Provider"] = "sqlite", ["Modules:TrustedPublisherKeys:TEST-ONLY-baseline-07"] = publicKey,
            ["Modules:TrustedExtensionIds:0"] = manifest.Id, ["Modules:RevokedPublisherIds:0"] = "TEST-ONLY-baseline-07", ["Modules:RequirePublisherSignature"] = "true"
        }));
        await RestoreAsync(fixture);
        foreach (var (relative, hash) in original) Assert.Equal(hash, System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(Path.Combine(fixture.Target, relative))));
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Backup:ManagedRoot"] = fixture.Target }).Build();
        using (BackupInstallation.Configure(configuration)) { }
        Assert.Equal("TEST-ONLY-baseline-07", configuration["Modules:RevokedPublisherIds:0"]);
        var restoredRegistry = new ModuleRegistry(); var restoredRuntime = new ModuleRuntime(restoredRegistry);
        using var catalog = new ModuleExtensionCatalog(Path.Combine(fixture.Target, "extensions"), new Version(1, 0, 0), restoredRuntime, restoredRegistry, new Dictionary<string, Func<IModule>>(), [manifest.Id], trustedPublisherKeys: publisherKeys, revokedPublisherIds: [configuration["Modules:RevokedPublisherIds:0"]!], requirePublisherSignature: true);
        Assert.Equal(PublisherSignatureStatus.RevokedPublisher, Assert.Single(catalog.Discover()).PublisherStatus);
        await Assert.ThrowsAnyAsync<Exception>(() => catalog.ActivateAsync(manifest.Id, default)); Assert.Empty(restoredRegistry.List());
    }
}
