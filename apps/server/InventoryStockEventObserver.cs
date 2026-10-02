using Hispania.Inventory.Application;
using Hispania.Platform.Contracts.Events;

namespace Hispania.Server;

/// <summary>Small cross-domain proof handler; records only safe event identity metadata.</summary>
public sealed class InventoryStockEventObserver(ILogger<InventoryStockEventObserver> logger) : IDurablePlatformEventConsumer<StockChangedV1>
{
    private static readonly Action<ILogger, Guid, Exception?> HandledLog = LoggerMessage.Define<Guid>(LogLevel.Information, new EventId(21, "DurableInventoryStockHandled"), "Handled durable inventory event {EventId}");
    public ValueTask HandleAsync(PlatformEventEnvelopeV1 envelope, StockChangedV1 value, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        HandledLog(logger, envelope.EventId, null);
        return ValueTask.CompletedTask;
    }
}
