using Hispania.Platform.Contracts.Assets;

namespace Hispania.Inventory.Contracts;

public readonly record struct ItemId(Guid Value) { public static ItemId New() => new(Guid.CreateVersion7()); }
public readonly record struct WarehouseId(Guid Value) { public static WarehouseId New() => new(Guid.CreateVersion7()); }
public readonly record struct LocationId(Guid Value) { public static LocationId New() => new(Guid.CreateVersion7()); }
public readonly record struct StockMovementId(Guid Value) { public static StockMovementId New() => new(Guid.CreateVersion7()); }

public sealed record InventoryItem(ItemId Id, string Sku, string Name, IReadOnlyList<AssetReference> Assets);
public sealed record Warehouse(WarehouseId Id, string Code, string Name);
public sealed record Location(LocationId Id, WarehouseId WarehouseId, string Code, string Name);
public sealed record StockLevel(ItemId ItemId, LocationId LocationId, int Quantity);
public enum StockMovementKind { Receipt, Issue, Transfer }
public sealed record StockMovement(StockMovementId Id, ItemId ItemId, StockMovementKind Kind, int Quantity, LocationId? SourceLocationId, LocationId? DestinationLocationId, DateTimeOffset Timestamp, string? ActorId, string? CorrelationId);
public sealed record InventoryContext(string? ActorId = null, string? CorrelationId = null);

public interface IInventoryQueryV1
{
    ValueTask<IReadOnlyList<InventoryItem>> GetItemsAsync(CancellationToken cancellationToken = default);
    ValueTask<InventoryItem?> GetItemAsync(ItemId id, CancellationToken cancellationToken = default);
    ValueTask<IReadOnlyList<Warehouse>> GetWarehousesAsync(CancellationToken cancellationToken = default);
    ValueTask<IReadOnlyList<Location>> GetLocationsAsync(WarehouseId? warehouseId = null, CancellationToken cancellationToken = default);
    ValueTask<IReadOnlyList<StockLevel>> GetStockAsync(ItemId? itemId = null, CancellationToken cancellationToken = default);
    ValueTask<IReadOnlyList<StockMovement>> GetMovementsAsync(ItemId itemId, CancellationToken cancellationToken = default);
}

public interface IInventoryCommandV1
{
    ValueTask<InventoryItem> CreateItemAsync(string sku, string name, InventoryContext? context = null, CancellationToken cancellationToken = default);
    ValueTask<Warehouse> CreateWarehouseAsync(string code, string name, InventoryContext? context = null, CancellationToken cancellationToken = default);
    ValueTask<Location> CreateLocationAsync(WarehouseId warehouseId, string code, string name, InventoryContext? context = null, CancellationToken cancellationToken = default);
    ValueTask<StockMovement> ReceiveAsync(ItemId itemId, LocationId destination, int quantity, InventoryContext? context = null, CancellationToken cancellationToken = default);
    ValueTask<StockMovement> IssueAsync(ItemId itemId, LocationId source, int quantity, InventoryContext? context = null, CancellationToken cancellationToken = default);
    ValueTask<StockMovement> TransferAsync(ItemId itemId, LocationId source, LocationId destination, int quantity, InventoryContext? context = null, CancellationToken cancellationToken = default);
    ValueTask<InventoryItem> AssociateAssetAsync(ItemId itemId, AssetReference asset, InventoryContext? context = null, CancellationToken cancellationToken = default);
    ValueTask<InventoryItem> RemoveAssetAsync(ItemId itemId, AssetId assetId, InventoryContext? context = null, CancellationToken cancellationToken = default);
}
