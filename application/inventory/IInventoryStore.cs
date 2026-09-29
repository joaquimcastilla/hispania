using Hispania.Inventory.Contracts;

namespace Hispania.Inventory.Application;

public interface IInventoryStore : IInventoryQueryV1
{
    ValueTask SaveItemAsync(InventoryItem item, CancellationToken cancellationToken);
    ValueTask SaveWarehouseAsync(Warehouse warehouse, CancellationToken cancellationToken);
    ValueTask SaveLocationAsync(Location location, CancellationToken cancellationToken);
    ValueTask ApplyMovementAsync(StockMovement movement, CancellationToken cancellationToken);
}
