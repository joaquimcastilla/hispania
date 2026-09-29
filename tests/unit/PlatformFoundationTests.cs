using Hispania.Platform.Contracts.Events;
using Hispania.Runtime;
using Xunit;

namespace Hispania.Runtime.Tests;

public sealed class PlatformFoundationTests
{
    [Fact]
    public async Task LocalBusDispatchesAllHandlersAndIsolatesFailures()
    {
        var metrics = new RecordingMetrics();
        var bus = new LocalPlatformEventBus(metrics);
        var calls = new List<string>();
        using var first = bus.Subscribe(new Handler<TestEvent>((_, _) => { calls.Add("first"); return ValueTask.CompletedTask; }));
        using var failing = bus.Subscribe(new Handler<TestEvent>((_, _) => throw new InvalidOperationException("boom")));
        using var last = bus.Subscribe(new Handler<TestEvent>((_, _) => { calls.Add("last"); return ValueTask.CompletedTask; }));

        var result = await bus.PublishAsync(new TestEvent("created"), new EventContext("corr-7"));

        Assert.Equal(["first", "last"], calls);
        Assert.Equal(3, result.HandlerCount);
        Assert.Equal(2, result.SucceededCount);
        Assert.Single(result.Failures);
        Assert.Equal(3, metrics.Count);
    }

    [Fact]
    public async Task LocalBusPropagatesCancellationToHandlers()
    {
        using var source = new CancellationTokenSource();
        var bus = new LocalPlatformEventBus();
        using var subscription = bus.Subscribe(new Handler<TestEvent>((_, token) =>
        {
            Assert.Equal(source.Token, token);
            source.Cancel();
            token.ThrowIfCancellationRequested();
            return ValueTask.CompletedTask;
        }));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await bus.PublishAsync(new TestEvent("created"), cancellationToken: source.Token));
    }

    [Fact]
    public void PlatformContractsHaveNoMediaAssemblyReference()
    {
        var references = typeof(IPlatformEvent).Assembly.GetReferencedAssemblies();
        Assert.DoesNotContain(references, reference => reference.Name?.Contains("Media", StringComparison.OrdinalIgnoreCase) == true);
        Assert.DoesNotContain(references, reference => reference.Name?.Contains("Playback", StringComparison.OrdinalIgnoreCase) == true);
    }

    private sealed record TestEvent(string Value) : IPlatformEvent
    {
        public string EventType => "test.created";
        public int Version => 1;
    }

    private sealed class Handler<T>(Func<EventEnvelope<T>, CancellationToken, ValueTask> callback) : IPlatformEventSubscriber<T> where T : IPlatformEvent
    {
        public ValueTask HandleAsync(EventEnvelope<T> message, CancellationToken cancellationToken) => callback(message, cancellationToken);
    }

    private sealed class RecordingMetrics : IPlatformEventMetrics
    {
        public int Count { get; private set; }
        public void HandlerCompleted(string eventType, bool succeeded, TimeSpan elapsed) => Count++;
    }
}
