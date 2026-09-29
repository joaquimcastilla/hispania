using Hispania.Inventory.Contracts;
using Hispania.Platform.Contracts.Assets;
using Hispania.Platform.Contracts.Audit;
using Hispania.Platform.Contracts.Events;
using System.Diagnostics;

namespace Hispania.Inventory.Application;

public sealed record ItemCreatedV1(ItemId ItemId, string Sku) : IPlatformEvent { public string EventType => "inventory.item-created"; public int Version => 1; }
public sealed record StockChangedV1(ItemId ItemId, LocationId LocationId, int OnHandQuantity) : IPlatformEvent { public string EventType => "inventory.stock-changed"; public int Version => 1; }
public sealed record StockMovementRecordedV1(StockMovement Movement) : IPlatformEvent { public string EventType => "inventory.stock-movement-recorded"; public int Version => 1; }

public sealed class InventoryService(IInventoryStore store, IPlatformEventPublisher events, IPlatformAuditWriter audit) : IInventoryQueryV1, IInventoryCommandV1
{
    public ValueTask<IReadOnlyList<InventoryItem>> GetItemsAsync(CancellationToken cancellationToken = default) => store.GetItemsAsync(cancellationToken);
    public ValueTask<InventoryItem?> GetItemAsync(ItemId id, CancellationToken cancellationToken = default) => store.GetItemAsync(id, cancellationToken);
    public ValueTask<IReadOnlyList<Warehouse>> GetWarehousesAsync(CancellationToken cancellationToken = default) => store.GetWarehousesAsync(cancellationToken);
    public ValueTask<IReadOnlyList<Location>> GetLocationsAsync(WarehouseId? warehouseId = null, CancellationToken cancellationToken = default) => store.GetLocationsAsync(warehouseId, cancellationToken);
    public ValueTask<IReadOnlyList<StockLevel>> GetStockAsync(ItemId? itemId = null, CancellationToken cancellationToken = default) => store.GetStockAsync(itemId, cancellationToken);
    public ValueTask<IReadOnlyList<StockMovement>> GetMovementsAsync(ItemId itemId, CancellationToken cancellationToken = default) => store.GetMovementsAsync(itemId, cancellationToken);

    public async ValueTask<InventoryItem> CreateItemAsync(string sku, string name, InventoryContext? context = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sku); ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var item = new InventoryItem(ItemId.New(), sku.Trim(), name.Trim(), []);
        await store.SaveItemAsync(item, cancellationToken);
        await AuditAsync("item.create", "item", item.Id.Value.ToString("D"), context, cancellationToken);
        await PublishAsync(new ItemCreatedV1(item.Id, item.Sku), context, cancellationToken);
        return item;
    }

    public async ValueTask<Warehouse> CreateWarehouseAsync(string code, string name, InventoryContext? context = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code); ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var warehouse = new Warehouse(WarehouseId.New(), code.Trim(), name.Trim());
        await store.SaveWarehouseAsync(warehouse, cancellationToken);
        await AuditAsync("warehouse.create", "warehouse", warehouse.Id.Value.ToString("D"), context, cancellationToken);
        return warehouse;
    }

    public async ValueTask<Location> CreateLocationAsync(WarehouseId warehouseId, string code, string name, InventoryContext? context = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code); ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (!(await store.GetWarehousesAsync(cancellationToken)).Any(x => x.Id == warehouseId)) throw new KeyNotFoundException("Warehouse not found.");
        var location = new Location(LocationId.New(), warehouseId, code.Trim(), name.Trim());
        await store.SaveLocationAsync(location, cancellationToken);
        await AuditAsync("location.create", "location", location.Id.Value.ToString("D"), context, cancellationToken);
        return location;
    }

    public ValueTask<StockMovement> ReceiveAsync(ItemId itemId, LocationId destination, int quantity, InventoryContext? context = null, CancellationToken cancellationToken = default) => MoveAsync(itemId, StockMovementKind.Receipt, quantity, null, destination, context, cancellationToken);
    public ValueTask<StockMovement> IssueAsync(ItemId itemId, LocationId source, int quantity, InventoryContext? context = null, CancellationToken cancellationToken = default) => MoveAsync(itemId, StockMovementKind.Issue, quantity, source, null, context, cancellationToken);
    public ValueTask<StockMovement> TransferAsync(ItemId itemId, LocationId source, LocationId destination, int quantity, InventoryContext? context = null, CancellationToken cancellationToken = default) => MoveAsync(itemId, StockMovementKind.Transfer, quantity, source, destination, context, cancellationToken);

    private async ValueTask<StockMovement> MoveAsync(ItemId itemId, StockMovementKind kind, int quantity, LocationId? source, LocationId? destination, InventoryContext? context, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(quantity);
        if (await store.GetItemAsync(itemId, cancellationToken) is null) throw new KeyNotFoundException("Item not found.");
        var locations = await store.GetLocationsAsync(cancellationToken: cancellationToken);
        if (source is { } sourceId && !locations.Any(x => x.Id == sourceId)) throw new KeyNotFoundException("Source location not found.");
        if (destination is { } destinationId && !locations.Any(x => x.Id == destinationId)) throw new KeyNotFoundException("Destination location not found.");
        if (kind == StockMovementKind.Transfer && source == destination) throw new ArgumentException("Source and destination must differ.");
        if (source is { } from && ((await store.GetStockAsync(itemId, cancellationToken)).FirstOrDefault(x => x.LocationId == from)?.Quantity ?? 0) < quantity) throw new InvalidOperationException("Insufficient stock.");
        var movement = new StockMovement(StockMovementId.New(), itemId, kind, quantity, source, destination, DateTimeOffset.UtcNow, context?.ActorId, context?.CorrelationId);
        await store.ApplyMovementAsync(movement, cancellationToken);
        await AuditAsync($"stock.{kind.ToString().ToLowerInvariant()}", "stock-movement", movement.Id.Value.ToString("D"), context, cancellationToken);
        foreach (var affectedLocation in new[] { source, destination }.Where(x => x.HasValue).Select(x => x!.Value).Distinct())
        {
            var onHand = (await store.GetStockAsync(itemId, cancellationToken)).FirstOrDefault(x => x.LocationId == affectedLocation)?.Quantity ?? 0;
            await PublishAsync(new StockChangedV1(itemId, affectedLocation, onHand), context, cancellationToken);
        }
        await PublishAsync(new StockMovementRecordedV1(movement), context, cancellationToken);
        return movement;
    }

    public async ValueTask<InventoryItem> AssociateAssetAsync(ItemId itemId, AssetReference asset, InventoryContext? context = null, CancellationToken cancellationToken = default) => await ChangeAssetAsync(itemId, asset, false, context, cancellationToken);
    public async ValueTask<InventoryItem> RemoveAssetAsync(ItemId itemId, AssetId assetId, InventoryContext? context = null, CancellationToken cancellationToken = default) => await ChangeAssetAsync(itemId, new AssetReference(assetId), true, context, cancellationToken);
    private async ValueTask<InventoryItem> ChangeAssetAsync(ItemId itemId, AssetReference asset, bool remove, InventoryContext? context, CancellationToken cancellationToken)
    {
        var item = await store.GetItemAsync(itemId, cancellationToken) ?? throw new KeyNotFoundException("Item not found.");
        var assets = item.Assets.Where(x => x.Id != asset.Id).ToList(); if (!remove) assets.Add(asset);
        var updated = item with { Assets = assets };
        await store.SaveItemAsync(updated, cancellationToken);
        await AuditAsync(remove ? "item.asset.remove" : "item.asset.associate", "item", item.Id.Value.ToString("D"), context, cancellationToken);
        return updated;
    }

    private ValueTask AuditAsync(string action, string resourceType, string resourceId, InventoryContext? context, CancellationToken token) => audit.WriteAsync(new AuditRecord(DateTimeOffset.UtcNow, context?.ActorId, context?.CorrelationId ?? Activity.Current?.TraceId.ToString(), "inventory", action, resourceType, resourceId, "succeeded", new Dictionary<string, string>()), token);
    private async ValueTask PublishAsync<T>(T @event, InventoryContext? context, CancellationToken token) where T : IPlatformEvent
    {
        try { await events.PublishAsync(@event, new EventContext(context?.CorrelationId ?? Activity.Current?.TraceId.ToString()), token); }
        catch (Exception exception) when (exception is not OperationCanceledException) { /* Event subscribers cannot roll back a committed inventory operation. */ }
    }
}
