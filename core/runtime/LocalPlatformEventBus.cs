using System.Diagnostics;
using Hispania.Platform.Contracts.Events;

namespace Hispania.Runtime;

/// <summary>Sequential, in-process event dispatch. Each handler failure is isolated from later handlers.</summary>
public sealed class LocalPlatformEventBus(IPlatformEventMetrics? metrics = null) : IPlatformEventPublisher
{
    private readonly Dictionary<Type, List<HandlerEntry>> handlers = [];
    private readonly object sync = new();
    private readonly IPlatformEventMetrics metrics = metrics ?? NullPlatformEventMetrics.Instance;

    public IDisposable Subscribe<TEvent>(IPlatformEventSubscriber<TEvent> handler) where TEvent : IPlatformEvent
    {
        ArgumentNullException.ThrowIfNull(handler);
        var entry = new HandlerEntry(handler, (message, cancellationToken) => handler.HandleAsync((EventEnvelope<TEvent>)message, cancellationToken));
        List<HandlerEntry> entries;
        lock (sync)
        {
            if (!handlers.TryGetValue(typeof(TEvent), out entries!)) handlers.Add(typeof(TEvent), entries = []);
            entries.Add(entry);
        }
        return new Subscription(() => { lock (sync) entries.Remove(entry); });
    }

    public async ValueTask<EventPublishResult> PublishAsync<TEvent>(TEvent @event, EventContext? context = null, CancellationToken cancellationToken = default)
        where TEvent : IPlatformEvent
    {
        ArgumentNullException.ThrowIfNull(@event);
        if (string.IsNullOrWhiteSpace(@event.EventType) || @event.Version < 1) throw new ArgumentException("Events require a type and a positive schema version.", nameof(@event));
        HandlerEntry[] snapshot;
        lock (sync) snapshot = handlers.TryGetValue(typeof(TEvent), out var entries) ? entries.ToArray() : [];
        if (snapshot.Length == 0) return new EventPublishResult(0, 0, []);
        var envelope = new EventEnvelope<TEvent>(@event, new EventContext(context?.CorrelationId ?? Hispania.Platform.Contracts.Observability.PlatformCorrelationV1.Current?.CorrelationId ?? Activity.Current?.TraceId.ToString())
        {
            CausationId = context?.CausationId ?? Hispania.Platform.Contracts.Observability.PlatformCorrelationV1.Current?.OperationId
        });
        var failures = new List<EventHandlerFailure>();
        var succeeded = 0;
        foreach (var entry in snapshot)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var timer = Stopwatch.StartNew();
            Exception? failure = null;
            try { await entry.Invoke(envelope, cancellationToken).ConfigureAwait(false); succeeded++; }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception exception) { failure = exception; failures.Add(new EventHandlerFailure(entry.Handler.GetType().FullName ?? entry.Handler.GetType().Name, exception.GetType().Name)); }
            finally { timer.Stop(); metrics.HandlerCompleted(@event.EventType, failure is null, timer.Elapsed); }
        }
        return new EventPublishResult(snapshot.Length, succeeded, failures);
    }

    private sealed record HandlerEntry(object Handler, Func<object, CancellationToken, ValueTask> Invoke);
    private sealed class Subscription(Action dispose) : IDisposable
    {
        private Action? dispose = dispose;
        public void Dispose() => Interlocked.Exchange(ref dispose, null)?.Invoke();
    }
}
