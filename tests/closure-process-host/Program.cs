using System.Text.Json;
using Hispania.Inventory.Application;
using Hispania.Platform.Contracts.Events;
using Hispania.Platform.Contracts.Jobs;
using Hispania.Platform.Jobs.Application;
using Hispania.Platform.Persistence.Sqlite;

// Test-only crash victim. Production binaries never reference this project.
var root = args.Single();
var jobs = new SqlitePlatformJobStore(Path.Combine(root, "jobs.sqlite"));
var registry = new PlatformJobHandlerRegistryV1().Register(new PlatformTestHashHandlerV1());
var service = new PlatformJobService(jobs, registry, new());
var created = await service.CreateAsync(new(PlatformTestHashHandlerV1.TypeId,
    "{\"text\":\"closure-crash\",\"algorithm\":\"sha256\"}", CorrelationId: "closure-crash"));
var acquired = await jobs.AcquireNextAsync(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(1), default)
    ?? throw new InvalidOperationException("Crash fixture could not acquire its job.");
if (acquired.JobId != created.Job.JobId) throw new InvalidOperationException("Unexpected crash fixture job.");
using var events = new SqliteDurablePlatformEventBus(Path.Combine(root, "events.sqlite"),
    new DurableEventOptionsV1(ProcessingLease: TimeSpan.FromSeconds(1)));
var eventId = await events.PublishAsync(new StockChangedV1(new(Guid.NewGuid()), new(Guid.NewGuid()), 1),
    "closure.fixture", ["platform.inventory.stock-observer"], new("closure-crash"));
var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
events.DeliveryHandler = async (_, _, ct) => { entered.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, ct); };
var dispatch = events.DispatchOnceAsync().AsTask();
await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
Console.WriteLine(JsonSerializer.Serialize(new { jobId = acquired.JobId, eventId }));
await Console.Out.FlushAsync();
await dispatch; // Parent kills this process while both committed leases remain in flight.
