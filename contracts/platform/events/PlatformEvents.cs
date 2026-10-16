namespace Hispania.Platform.Contracts.Events;

/// <summary>A transport-neutral event with a stable name and explicit schema version.</summary>
public interface IPlatformEvent
{
    string EventType { get; }
    int Version { get; }
}

public sealed record EventContext(string? CorrelationId)
{
    /// <summary>Optional event or operation that directly caused this event.</summary>
    public string? CausationId { get; init; }
}

public sealed record EventEnvelope<TEvent>(TEvent Event, EventContext Context)
    where TEvent : IPlatformEvent;

public interface IPlatformEventSubscriber<TEvent> where TEvent : IPlatformEvent
{
    ValueTask HandleAsync(EventEnvelope<TEvent> message, CancellationToken cancellationToken);
}

public interface IPlatformEventPublisher
{
    ValueTask<EventPublishResult> PublishAsync<TEvent>(TEvent eventData, EventContext? context = null, CancellationToken cancellationToken = default)
        where TEvent : IPlatformEvent;
}

public sealed record EventPublishResult(int HandlerCount, int SucceededCount, IReadOnlyList<EventHandlerFailure> Failures)
{
    public bool Succeeded => Failures.Count == 0;
}

public sealed record EventHandlerFailure(string HandlerName, string ErrorType);

public interface IPlatformEventMetrics
{
    void HandlerCompleted(string eventType, bool succeeded, TimeSpan elapsed);
}

public sealed class NullPlatformEventMetrics : IPlatformEventMetrics
{
    public static NullPlatformEventMetrics Instance { get; } = new();
    private NullPlatformEventMetrics() { }
    public void HandlerCompleted(string eventType, bool succeeded, TimeSpan elapsed) { }
}
