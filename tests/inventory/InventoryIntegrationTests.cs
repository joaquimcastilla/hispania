using Hispania.Inventory.Application;
using Hispania.Inventory.Contracts;
using Hispania.Inventory.Persistence.Sqlite;
using Hispania.Platform.Contracts.Assets;
using Hispania.Platform.Contracts.Events;
using Hispania.Runtime;
using Hispania.Platform.Persistence.Sqlite;
using Hispania.Platform.Storage.Application;
using Hispania.Platform.Storage.Filesystem;
using Xunit;

namespace Hispania.Inventory.Tests;

public sealed class InventoryIntegrationTests
{
    private static readonly int[] ExpectedQuantities = [9, 2];

    [Fact]
    public async Task InventoryPersistsStockEventsAuditAndOpaqueAssetReferences()
    {
        var root = Path.Combine(Path.GetTempPath(), $"hispania-inventory-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "inventory.sqlite");
        var store = new InventorySqliteStore(path);
        var events = new LocalPlatformEventBus();
        var audit = new InMemoryPlatformAuditWriter();
        var service = new InventoryService(store, events, audit);
        var itemEvents = 0;
        var stockEvents = 0;
        var movementEvents = 0;
        using var first = events.Subscribe(new Handler<ItemCreatedV1>((_, _) => { itemEvents++; return ValueTask.CompletedTask; }));
        using var broken = events.Subscribe(new Handler<ItemCreatedV1>((_, _) => throw new InvalidOperationException("subscriber unavailable")));
        using var second = events.Subscribe(new Handler<ItemCreatedV1>((_, _) => { itemEvents++; return ValueTask.CompletedTask; }));
        using var stockSubscriberA = events.Subscribe(new Handler<StockChangedV1>((_, _) => { stockEvents++; return ValueTask.CompletedTask; }));
        using var stockSubscriberB = events.Subscribe(new Handler<StockChangedV1>((_, _) => { stockEvents++; return ValueTask.CompletedTask; }));
        using var movementSubscriber = events.Subscribe(new Handler<StockMovementRecordedV1>((_, _) => { movementEvents++; return ValueTask.CompletedTask; }));

        var context = new InventoryContext("operator-4", "correlation-9");
        var item = await service.CreateItemAsync("BP48", "Battery Pack 48V", context);
        var warehouse = await service.CreateWarehouseAsync("MAD", "Madrid", context);
        var locationA = await service.CreateLocationAsync(warehouse.Id, "A-01", "Aisle A shelf 1", context);
        var locationB = await service.CreateLocationAsync(warehouse.Id, "B-02", "Aisle B shelf 2", context);
        await service.ReceiveAsync(item.Id, locationA.Id, 12, context);
        await service.TransferAsync(item.Id, locationA.Id, locationB.Id, 3, context);
        await service.IssueAsync(item.Id, locationB.Id, 1, context);

        Assert.Equal(2, itemEvents);
        Assert.Equal(8, stockEvents);
        Assert.Equal(3, movementEvents);
        Assert.Equal(ExpectedQuantities, (await service.GetStockAsync(item.Id)).OrderBy(x => x.LocationId == locationA.Id ? 0 : 1).Select(x => x.Quantity));
        Assert.Equal(3, (await service.GetMovementsAsync(item.Id)).Count);
        Assert.All(audit.Records, record => Assert.Equal("correlation-9", record.CorrelationId));
        Assert.Contains(audit.Records, record => record.ActorId == "operator-4" && record.ResourceType == "stock-movement");

        var platformAssets = new PersistentPlatformAssetStore(new StorageProviderRegistryV1("filesystem", [new FilesystemStorageProviderV1(Path.Combine(root, "asset-content"))]), new SqliteAssetMetadataRepository(Path.Combine(root, "assets.sqlite")), 1024 * 1024);
        await using var payload = new MemoryStream([0x25, 0x50, 0x44, 0x46]);
        var asset = await platformAssets.StoreAsync(AssetType.Document, "application/pdf", payload, new("erp.inventory", item.Id.Value.ToString("D")), new("owner"), new Dictionary<string, string> { ["label"] = "Datasheet" });
        await service.AssociateAssetAsync(item.Id, new AssetReference(asset.Id), context);
        var restartedService = new InventoryService(new InventorySqliteStore(path), events, audit);
        var restartedAssets = new PersistentPlatformAssetStore(new StorageProviderRegistryV1("filesystem", [new FilesystemStorageProviderV1(Path.Combine(root, "asset-content"))]), new SqliteAssetMetadataRepository(Path.Combine(root, "assets.sqlite")), 1024 * 1024);
        var reloaded = await restartedService.GetItemAsync(item.Id);
        Assert.NotNull(reloaded);
        var assetReference = Assert.Single(reloaded!.Assets);
        Assert.Equal(asset.Id, assetReference.Id);
        var resolved = await restartedAssets.GetMetadataAsync(assetReference.Id);
        Assert.Equal("application/pdf", resolved?.MimeType);
        Assert.Equal("Datasheet", resolved?.Metadata["label"]);
        await using var stream = await restartedAssets.OpenReadAsync(asset.Id);
        Assert.NotNull(stream);
        Assert.Equal(4, stream!.Length);
        Assert.All((await store.GetStockAsync(item.Id)), level => Assert.True(level.Quantity >= 0));
        Assert.Equal(ExpectedQuantities, (await restartedService.GetStockAsync(item.Id)).OrderBy(x => x.LocationId == locationA.Id ? 0 : 1).Select(x => x.Quantity));
    }

    private sealed class Handler<T>(Func<EventEnvelope<T>, CancellationToken, ValueTask> handle) : IPlatformEventSubscriber<T> where T : IPlatformEvent
    {
        public ValueTask HandleAsync(EventEnvelope<T> message, CancellationToken cancellationToken) => handle(message, cancellationToken);
    }
}
