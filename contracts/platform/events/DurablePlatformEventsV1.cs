using System.Text.Json;

namespace Hispania.Platform.Contracts.Events;

public sealed record PlatformEventEnvelopeV1(Guid EventId, string EventType, int Version, DateTimeOffset OccurredAt,
    string? CorrelationId, string? CausationId, string Producer, string PayloadJson);
public enum DurableDeliveryStatus { Pending, Processing, Delivered, DeadLettered }
public sealed record DurableDeliveryV1(Guid DeliveryId, Guid EventId, string SubscriberId, DurableDeliveryStatus Status,
    int AttemptCount, DateTimeOffset NextAttemptAt, string? LastError, DateTimeOffset? DeliveredAt, DateTimeOffset? DeadLetteredAt);
public sealed record DurableDeliveryViewV1(Guid DeliveryId, Guid EventId, string EventType, string SubscriberId,
    DurableDeliveryStatus Status, int AttemptCount, DateTimeOffset NextAttemptAt, string? LastError,
    DateTimeOffset? DeliveredAt, DateTimeOffset? DeadLetteredAt);
public sealed record DurableEventDetailV1(PlatformEventEnvelopeV1 Event, IReadOnlyList<DurableDeliveryV1> Deliveries);
public sealed record DurableDeliveryAttemptV1(Guid AttemptId, Guid DeliveryId, int AttemptNumber, DateTimeOffset FinishedAt, DurableDeliveryStatus Outcome, string? Error);
public sealed record DurableEventHealthV1(long Pending, long Processing, long Delivered, long DeadLettered, bool StoreAccessible);
public sealed record DurableEventOptionsV1(int MaximumPayloadBytes = 262144, int MaximumAttempts = 5,
    TimeSpan? RetryDelay = null, TimeSpan? ProcessingLease = null)
{
    public TimeSpan EffectiveRetryDelay => RetryDelay ?? TimeSpan.FromSeconds(2);
    public TimeSpan EffectiveProcessingLease => ProcessingLease ?? TimeSpan.FromMinutes(2);
}

/// <summary>Transport-neutral durable event store and dispatcher boundary. Payloads use UTF-8 JSON.</summary>
public interface IDurablePlatformEventBusV1
{
    ValueTask<Guid> PublishAsync<TEvent>(TEvent value, string producer, IReadOnlyCollection<string> subscribers,
        EventContext? context = null, string? causationId = null, CancellationToken cancellationToken = default) where TEvent : IPlatformEvent;
    ValueTask<int> DispatchOnceAsync(CancellationToken cancellationToken = default);
    ValueTask<IReadOnlyList<DurableDeliveryViewV1>> ListDeliveriesAsync(DurableDeliveryStatus? status = null, int limit = 100, CancellationToken cancellationToken = default);
    ValueTask<DurableEventDetailV1?> GetEventAsync(Guid eventId, CancellationToken cancellationToken = default);
    ValueTask<bool> ReplayDeadLetterAsync(Guid deliveryId, CancellationToken cancellationToken = default);
    ValueTask<IReadOnlyList<DurableDeliveryAttemptV1>> GetDeliveryAttemptsAsync(Guid deliveryId, CancellationToken cancellationToken = default);
    ValueTask<IReadOnlySet<string>> GetKnownSubscribersAsync(CancellationToken cancellationToken = default);
    ValueTask<DurableEventHealthV1> GetHealthAsync(CancellationToken cancellationToken = default);
}

public interface IDurablePlatformEventConsumer<TEvent> where TEvent : IPlatformEvent
{
    ValueTask HandleAsync(PlatformEventEnvelopeV1 envelope, TEvent value, CancellationToken cancellationToken = default);
}

public interface IDurableEventCodecRegistryV1
{
    bool TryGet(string eventType, int version, out Type payloadType);
    object? Deserialize(string eventType, int version, string payloadJson);
}

public sealed class DurableEventHandlerRegistryV1 : IDurableEventCodecRegistryV1
{
    private readonly Dictionary<(string Type, int Version), (Type Payload, Dictionary<string, Func<PlatformEventEnvelopeV1, object, CancellationToken, ValueTask>> Handlers)> entries = [];
    public DurableEventHandlerRegistryV1 Register<TEvent>(string eventType, int version, string subscriberId, IDurablePlatformEventConsumer<TEvent> handler) where TEvent : IPlatformEvent
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subscriberId);
        var key = (eventType, version);
        if (!entries.TryGetValue(key, out var entry)) entry = (typeof(TEvent), new(StringComparer.Ordinal));
        if (entry.Payload != typeof(TEvent) || !entry.Handlers.TryAdd(subscriberId, (envelope, payload, ct) => handler.HandleAsync(envelope, (TEvent)payload, ct)))
            throw new InvalidOperationException("Durable event type/subscriber registration is duplicated or inconsistent.");
        entries[key] = entry; return this;
    }
    public bool TryGet(string eventType, int version, out Type payloadType) { if (entries.TryGetValue((eventType, version), out var entry)) { payloadType = entry.Payload; return true; } payloadType = null!; return false; }
    public object? Deserialize(string eventType, int version, string payloadJson) => TryGet(eventType, version, out var type) ? JsonSerializer.Deserialize(payloadJson, type) : throw new InvalidDataException("No safe registered event codec.");
    public ValueTask DispatchAsync(PlatformEventEnvelopeV1 envelope, string subscriber, CancellationToken ct)
    {
        if (!entries.TryGetValue((envelope.EventType, envelope.Version), out var entry) || !entry.Handlers.TryGetValue(subscriber, out var handler)) throw new InvalidOperationException("No durable handler registered for this subscriber and event version.");
        var payload = Deserialize(envelope.EventType, envelope.Version, envelope.PayloadJson) ?? throw new InvalidDataException("Durable event payload is invalid.");
        return handler(envelope, payload, ct);
    }
    public IReadOnlySet<string> SubscribersFor(string eventType, int version) => entries.TryGetValue((eventType, version), out var entry) ? entry.Handlers.Keys.ToHashSet(StringComparer.Ordinal) : new HashSet<string>(StringComparer.Ordinal);
}
