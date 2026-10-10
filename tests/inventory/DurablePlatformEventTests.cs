using Hispania.Platform.Contracts.Events;
using Hispania.Platform.Persistence.Sqlite;
using Xunit;

namespace Hispania.Inventory.Tests;

public sealed class DurablePlatformEventTests
{
    [Fact]
    public async Task ConcurrentPublishAndDispatchKeepOneAcquisitionPerDeliveryAndSqliteIntegrity()
    {
        var root = Path.Combine(Path.GetTempPath(), "hispania-event-concurrency-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var bus = new SqliteDurablePlatformEventBus(Path.Combine(root, "events.sqlite"));
            var calls = new System.Collections.Concurrent.ConcurrentDictionary<Guid, int>();
            bus.DeliveryHandler = (envelope, _, _) => { calls.AddOrUpdate(envelope.EventId, 1, (_, count) => count + 1); return ValueTask.CompletedTask; };
            await Task.WhenAll(Enumerable.Range(0, 32).Select(index => Task.Run(async () =>
                await bus.PublishAsync(new SampleEvent(index.ToString(System.Globalization.CultureInfo.InvariantCulture)), "concurrency", ["consumer.concurrent"]))));
            await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(async () => await bus.DispatchOnceAsync())));
            Assert.Equal(32, calls.Count); Assert.All(calls.Values, count => Assert.Equal(1, count));
            var deliveries = await bus.ListDeliveriesAsync(limit: 100);
            Assert.Equal(32, deliveries.Count);
            Assert.All(deliveries, row => { Assert.Equal(DurableDeliveryStatus.Delivered, row.Status); Assert.Equal(1, row.AttemptCount); });
            using var db = new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = Path.Combine(root, "events.sqlite"), Pooling = false }.ToString());
            db.Open(); using var command = db.CreateCommand(); command.CommandText = "PRAGMA integrity_check";
            Assert.Equal("ok", command.ExecuteScalar());
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task PublishSurvivesReopenAndSubscribersHaveIndependentDeliveryState()
    {
        var path = Path.Combine(Path.GetTempPath(), $"hispania-durable-events-{Guid.NewGuid():N}.sqlite");
        try
        {
            using (var original = new SqliteDurablePlatformEventBus(path, new DurableEventOptionsV1(MaximumAttempts: 3, RetryDelay: TimeSpan.FromMilliseconds(5))))
            {
            var id = await original.PublishAsync(new SampleEvent("value"), "test", ["consumer.a", "consumer.b"], new("corr"), "cause");
            using var reopened = new SqliteDurablePlatformEventBus(path, new DurableEventOptionsV1(MaximumAttempts: 3, RetryDelay: TimeSpan.FromMilliseconds(5)));
            Assert.NotNull(await reopened.GetEventAsync(id));
            var firstSubscriberAttempted = false;
            reopened.DeliveryHandler = (_, subscriber, _) =>
            {
                if (subscriber == "consumer.a" && !firstSubscriberAttempted) { firstSubscriberAttempted = true; return ValueTask.FromException(new InvalidOperationException("temporary")); }
                return ValueTask.CompletedTask;
            };
            await reopened.DispatchOnceAsync();
            var rows = await reopened.ListDeliveriesAsync();
            Assert.Equal(DurableDeliveryStatus.Pending, Assert.Single(rows, x => x.SubscriberId == "consumer.a").Status);
            Assert.Equal(DurableDeliveryStatus.Delivered, Assert.Single(rows, x => x.SubscriberId == "consumer.b").Status);
            Assert.Equal(1, Assert.Single(rows, x => x.SubscriberId == "consumer.b").AttemptCount);
            Assert.Equal("corr", (await reopened.GetEventAsync(id))!.Event.CorrelationId);
            Assert.Equal("cause", (await reopened.GetEventAsync(id))!.Event.CausationId);
            await Task.Delay(10);
            await reopened.DispatchOnceAsync();
            rows = await reopened.ListDeliveriesAsync();
            Assert.Equal(DurableDeliveryStatus.Delivered, Assert.Single(rows, x => x.SubscriberId == "consumer.a").Status);
            Assert.Equal(2, Assert.Single(rows, x => x.SubscriberId == "consumer.a").AttemptCount);
            Assert.Equal(1, Assert.Single(rows, x => x.SubscriberId == "consumer.b").AttemptCount);
            }
        }
        finally { foreach (var file in Directory.GetFiles(Path.GetDirectoryName(path)!, Path.GetFileName(path) + "*")) File.Delete(file); }
    }

    [Fact]
    public async Task ExhaustionStaysDeadLetteredUntilExplicitReplayAndRetainsAttemptHistory()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"hispania-dead-events-{Guid.NewGuid():N}"); Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "events.sqlite");
        try
        {
            using (var bus = new SqliteDurablePlatformEventBus(path, new DurableEventOptionsV1(MaximumAttempts: 2, RetryDelay: TimeSpan.Zero)))
            {
            await bus.PublishAsync(new SampleEvent("value"), "test", ["consumer.dead"]);
            bus.DeliveryHandler = (_, _, _) => ValueTask.FromException(new InvalidOperationException("sensitive payload detail"));
            await bus.DispatchOnceAsync(); await bus.DispatchOnceAsync();
            var dead = Assert.Single(await bus.ListDeliveriesAsync(DurableDeliveryStatus.DeadLettered));
            Assert.Equal(2, dead.AttemptCount);
            Assert.DoesNotContain("sensitive", dead.LastError, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(2, (await bus.GetDeliveryAttemptsAsync(dead.DeliveryId)).Count);
            using var reopened = new SqliteDurablePlatformEventBus(path, new DurableEventOptionsV1(MaximumAttempts: 2, RetryDelay: TimeSpan.Zero));
            Assert.Single(await reopened.ListDeliveriesAsync(DurableDeliveryStatus.DeadLettered));
            Assert.True(await reopened.ReplayDeadLetterAsync(dead.DeliveryId));
            reopened.DeliveryHandler = (_, _, _) => ValueTask.CompletedTask;
            await reopened.DispatchOnceAsync();
            Assert.Equal(DurableDeliveryStatus.Delivered, Assert.Single(await reopened.ListDeliveriesAsync()).Status);
            Assert.Equal(3, (await reopened.GetDeliveryAttemptsAsync(dead.DeliveryId)).Count);
            }
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task InterruptedProcessingLeaseIsRecoveredAfterStoreReopen()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"hispania-processing-events-{Guid.NewGuid():N}"); Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "events.sqlite"); var options = new DurableEventOptionsV1(MaximumAttempts: 3, RetryDelay: TimeSpan.Zero, ProcessingLease: TimeSpan.FromMilliseconds(20));
        try
        {
            Guid eventId;
            using (var first = new SqliteDurablePlatformEventBus(path, options))
            {
                eventId = await first.PublishAsync(new SampleEvent("value"), "test", ["consumer.recover"]);
                using var cancellation = new CancellationTokenSource(); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                first.DeliveryHandler = async (_, _, ct) => { entered.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, ct); };
                var dispatch = first.DispatchOnceAsync(cancellation.Token).AsTask(); await entered.Task; cancellation.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => dispatch);
                Assert.Equal(DurableDeliveryStatus.Processing, Assert.Single(await first.ListDeliveriesAsync()).Status);
            }
            await Task.Delay(40);
            using (var restarted = new SqliteDurablePlatformEventBus(path, options))
            {
                restarted.DeliveryHandler = (_, _, _) => ValueTask.CompletedTask;
                await restarted.DispatchOnceAsync();
                Assert.Equal(DurableDeliveryStatus.Delivered, Assert.Single(await restarted.ListDeliveriesAsync()).Status);
                Assert.Single((await restarted.GetEventAsync(eventId))!.Deliveries);
            }
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private sealed record SampleEvent(string Value) : IPlatformEvent { public string EventType => "test.sample"; public int Version => 1; }
}
