using Hispania.Platform.Contracts.Events;
using Hispania.Platform.Persistence.Sqlite;
using Hispania.Runtime;

namespace Hispania.Server;

public sealed class DurablePlatformEventPublisher(IDurablePlatformEventBusV1 durable, DurableEventHandlerRegistryV1 registry, LocalPlatformEventBus local) : IPlatformEventPublisher
{
    public async ValueTask<EventPublishResult> PublishAsync<TEvent>(TEvent eventData, EventContext? context = null, CancellationToken cancellationToken = default) where TEvent : IPlatformEvent
    {
        var subscribers = registry.SubscribersFor(eventData.EventType, eventData.Version);
        if (subscribers.Count > 0)
            await durable.PublishAsync(eventData, "hispania.server", subscribers, context, cancellationToken: cancellationToken).ConfigureAwait(false);
        return await local.PublishAsync(eventData, context, cancellationToken).ConfigureAwait(false);
    }
}

public sealed class DurablePlatformEventDispatcher(SqliteDurablePlatformEventBus bus, DurableEventHandlerRegistryV1 registry) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { bus.DeliveryHandler = registry.DispatchAsync; await bus.DispatchOnceAsync(stoppingToken).ConfigureAwait(false); await Task.Delay(TimeSpan.FromMilliseconds(500), stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch { await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken).ConfigureAwait(false); }
        }
    }
}
