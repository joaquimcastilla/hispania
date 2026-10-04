using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text.Json;
using Hispania.Identity.Application;
using Hispania.ModuleSystem.Contracts;
using Hispania.Platform.Contracts.Audit;
using Hispania.Platform.Contracts.Events;
using Hispania.Platform.Contracts.Jobs;
using Hispania.Platform.Contracts.Observability;
using Hispania.Platform.Jobs.Application;
using Hispania.Platform.Observability.Application;
using Hispania.Platform.Persistence.Sqlite;
using Hispania.Runtime;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Hispania.Inventory.Tests;

public sealed class PlatformObservabilityTests
{
    [Fact]
    public async Task UnsampledDurableFlowRetainsTraceParentForFutureListeners()
    {
        var directory = Path.Combine(Path.GetTempPath(), "hispania-observability-unsampled-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        const string traceParent = "00-11111111111111111111111111111111-2222222222222222-00";
        try
        {
            var jobs = new PlatformJobService(new SqlitePlatformJobStore(Path.Combine(directory, "jobs.sqlite")), new PlatformJobHandlerRegistryV1().Register(new PlatformTestHashHandlerV1()), new());
            using var bus = new SqliteDurablePlatformEventBus(Path.Combine(directory, "events.sqlite"));
            PlatformJobV1? job = null;
            bus.DeliveryHandler = async (_, _, ct) => job = (await jobs.CreateAsync(new(PlatformTestHashHandlerV1.TypeId, "{}"), ct)).Job;
            Guid id;
            using (PlatformCorrelationV1.Push(new("flow-unsampled", OperationId: "request", TraceParent: traceParent))) id = await bus.PublishAsync(new TestEvent(), "test", ["consumer"]);
            Assert.Equal(traceParent, (await bus.GetEventAsync(id))!.Event.TraceParent);
            await bus.DispatchOnceAsync(); Assert.Equal(traceParent, job!.TraceParent); Assert.Equal("flow-unsampled", job.CorrelationId);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task FailedHandlerLogsRemainStructuredAndSafeWithBrokenMetricsSink()
    {
        var directory = Path.Combine(Path.GetTempPath(), "hispania-observability-logs-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        try
        {
            var store = new SqlitePlatformJobStore(Path.Combine(directory, "jobs.sqlite"));
            var registry = new PlatformJobHandlerRegistryV1().Register(new SecretFailingHandler());
            var options = new PlatformJobOptionsV1(MaximumAttempts: 1, PollInterval: TimeSpan.FromMilliseconds(10));
            var logger = new RecordingLogger(); using var metrics = new PlatformJobMetricsV1(new ThrowingMetrics());
            using var dispatcher = new LocalPlatformJobDispatcher(store, registry, options, logger, metrics: metrics);
            var jobs = new PlatformJobService(store, registry, options, metrics: metrics);
            PlatformJobV1 created;
            using (PlatformCorrelationV1.Push(new("flow-safe", OperationId: "request-safe"))) created = (await jobs.CreateAsync(new("test.failure", "{}"))).Job;
            await dispatcher.StartAsync(default);
            try
            {
                var deadline = Stopwatch.StartNew();
                while ((await jobs.GetAsync(created.JobId))!.Status != PlatformJobStatusV1.DeadLettered && deadline.Elapsed < TimeSpan.FromSeconds(5)) await Task.Delay(20);
                Assert.Equal(PlatformJobStatusV1.DeadLettered, (await jobs.GetAsync(created.JobId))!.Status);
            }
            finally { await dispatcher.StopAsync(default); }
            Assert.Contains(logger.Events, entry => entry.Name == "JobFailed");
            Assert.DoesNotContain("password-secret", string.Join("\n", logger.Events.Select(entry => entry.Message)), StringComparison.Ordinal);
            Assert.Contains(logger.Scopes, scope => scope.TryGetValue("CorrelationId", out var correlation) && Equals(correlation, "flow-safe") && Equals(scope["Component"], "Jobs"));
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task IdentityMetricsAndSessionGaugeSourceTrackRealLoginAndRevocationWithoutSecrets()
    {
        var directory = Path.Combine(Path.GetTempPath(), "hispania-observability-identity-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        try
        {
            using var metrics = new PlatformMetricsV1(); var audit = new RecordingAudit();
            var store = new SqliteIdentityStore(Path.Combine(directory, "identity.sqlite")); var identity = new IdentityService(store, audit, metrics: metrics);
            await identity.CreateUserAsync("testuser", "Test User", "correct-password-secret");
            Assert.False((await identity.LoginAsync("testuser", "incorrect-password-secret")).Succeeded);
            var loggedIn = await identity.LoginAsync("testuser", "correct-password-secret"); Assert.True(loggedIn.Succeeded);
            Assert.Equal(1, await store.CountActiveSessionsAsync());
            Assert.Equal(1, Assert.Single(metrics.Snapshot(), metric => metric.Name == "hispania.identity.login.success").Value);
            Assert.Equal(1, Assert.Single(metrics.Snapshot(), metric => metric.Name == "hispania.identity.login.failure").Value);
            await identity.LogoutAsync(loggedIn.SessionToken!); Assert.Equal(0, await store.CountActiveSessionsAsync());
            var auditJson = JsonSerializer.Serialize(audit.Records); Assert.DoesNotContain("password-secret", auditJson, StringComparison.Ordinal); Assert.DoesNotContain(loggedIn.SessionToken!, auditJson, StringComparison.Ordinal);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task MetricsAreExactUnderConcurrencyAndRejectUnboundedNamesAndNegativeValues()
    {
        using var metrics = new PlatformMetricsV1();
        await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(() =>
        {
            for (var i = 0; i < 1000; i++) metrics.Increment("hispania.jobs.created");
        })));
        Assert.Equal(16000, Assert.Single(metrics.Snapshot(), row => row.Name == "hispania.jobs.created").Value);
        metrics.Gauge("hispania.jobs.running", 4); metrics.Gauge("hispania.jobs.running", 0);
        Assert.Equal(0, Assert.Single(metrics.Snapshot(), row => row.Name == "hispania.jobs.running").Value);
        metrics.Observe("hispania.jobs.duration", 10); metrics.Observe("hispania.jobs.duration", 20);
        var timing = Assert.Single(metrics.Snapshot(), row => row.Name == "hispania.jobs.duration"); Assert.Equal(15, timing.Value); Assert.Equal(2, timing.Count);
        Assert.Throws<ArgumentException>(() => metrics.Increment("hispania.jobs." + Guid.NewGuid()));
        Assert.Throws<ArgumentException>(() => metrics.Increment("hispania.jobs.created", -1));
        Assert.Throws<ArgumentException>(() => metrics.Gauge("hispania.jobs.running", -1));
        Assert.Throws<ArgumentException>(() => metrics.Observe("hispania.jobs.duration", double.NaN));
    }

    [Fact]
    public void ExporterFailureDoesNotEscapeCriticalMetrics()
    {
        using var metrics = new PlatformMetricsV1();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, active) => { if (instrument.Meter.Name == "Hispania.Platform") active.EnableMeasurementEvents(instrument); };
        listener.SetMeasurementEventCallback<long>((_, _, _, _) => throw new InvalidOperationException("collector-secret"));
        listener.Start();
        metrics.Increment("hispania.jobs.created");
        Assert.Equal(1, Assert.Single(metrics.Snapshot(), row => row.Name == "hispania.jobs.created").Value);
        new ThrowingMetrics().BestEffort(sink => sink.Increment("hispania.jobs.created"));
    }

    [Fact]
    public async Task HealthRecoversAndSanitizesStoreFailureWithoutStickyState()
    {
        var status = PlatformHealthStatusV1.Healthy; var fail = false;
        var health = new PlatformHealthV1([new DelegatePlatformHealthCheckV1("Store", _ => fail ? throw new IOException("password-secret /physical/root") : ValueTask.FromResult(new PlatformHealthCheckResultV1("Store", status, "state")))]);
        Assert.Equal(PlatformHealthStatusV1.Healthy, (await health.CheckAsync()).Status);
        status = PlatformHealthStatusV1.Degraded; Assert.Equal(PlatformHealthStatusV1.Degraded, (await health.CheckAsync()).Status);
        fail = true; var failed = await health.CheckAsync(); Assert.Equal(PlatformHealthStatusV1.Unhealthy, failed.Status);
        Assert.DoesNotContain("secret", JsonSerializer.Serialize(failed), StringComparison.OrdinalIgnoreCase);
        fail = false; status = PlatformHealthStatusV1.Healthy; Assert.Equal(PlatformHealthStatusV1.Healthy, (await health.CheckAsync()).Status);
    }

    [Fact]
    public async Task OperationPublishesEventWhichCreatesJobAndAuditWithStableCorrelationAndImmediateCause()
    {
        using var listener = new ActivityListener { ShouldListenTo = source => source.Name == "Hispania.Platform", Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded };
        ActivitySource.AddActivityListener(listener);
        using var requestActivity = PlatformTracingV1.Start("test.http");
        var directory = Path.Combine(Path.GetTempPath(), "hispania-observability-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        try
        {
            using var metrics = new PlatformMetricsV1();
            var audit = new RecordingAudit();
            var jobs = new PlatformJobService(new SqlitePlatformJobStore(Path.Combine(directory, "jobs.sqlite")), new PlatformJobHandlerRegistryV1().Register(new PlatformTestHashHandlerV1()), new(), audit, metrics: new(metrics));
            using var bus = new SqliteDurablePlatformEventBus(Path.Combine(directory, "events.sqlite")) { Metrics = metrics };
            PlatformJobV1? created = null;
            bus.DeliveryHandler = async (envelope, _, ct) =>
            {
                Assert.Equal(envelope.EventId.ToString("D"), PlatformCorrelationV1.Current?.OperationId);
                created = (await jobs.CreateAsync(new(PlatformTestHashHandlerV1.TypeId, "{\"text\":\"abc\",\"algorithm\":\"sha256\"}"), ct)).Job;
            };
            Guid eventId;
            using (PlatformCorrelationV1.Push(new("flow-A", OperationId: "request-command")))
                eventId = await bus.PublishAsync(new TestEvent(), "test", ["test.job-consumer"], new EventContext(null));
            Assert.Null(PlatformCorrelationV1.Current);
            await bus.DispatchOnceAsync();
            var envelope = (await bus.GetEventAsync(eventId))!.Event;
            Assert.Equal("flow-A", envelope.CorrelationId); Assert.Equal("request-command", envelope.CausationId);
            Assert.True(ActivityContext.TryParse(envelope.TraceParent, envelope.TraceState, out var eventTrace)); Assert.Equal(requestActivity!.TraceId, eventTrace.TraceId);
            Assert.True(ActivityContext.TryParse(created!.TraceParent, created.TraceState, out var jobTrace)); Assert.Equal(requestActivity.TraceId, jobTrace.TraceId);
            Assert.Equal(created.TraceParent, (await jobs.GetAsync(created.JobId))!.TraceParent);
            Assert.Equal("flow-A", created.CorrelationId); Assert.Equal(eventId.ToString("D"), created.CausationId);
            Assert.Equal("flow-A", Assert.Single(audit.Records).CorrelationId);
            Assert.Null(PlatformCorrelationV1.Current);
            Assert.Equal(1, Assert.Single(metrics.Snapshot(), m => m.Name == "hispania.events.delivered").Value);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task CorrelationScopesAreIsolatedAndRestoredAcrossConcurrentAsyncFlows()
    {
        await Task.WhenAll(Enumerable.Range(0, 32).Select(async i =>
        {
            using var scope = PlatformCorrelationV1.Push(new("flow-" + i, OperationId: "command"));
            await Task.Yield(); Assert.Equal("flow-" + i, PlatformCorrelationV1.Current?.CorrelationId);
            using (PlatformCorrelationV1.Push(new("child-" + i))) { await Task.Yield(); Assert.Equal("child-" + i, PlatformCorrelationV1.Current?.CorrelationId); }
            Assert.Equal("flow-" + i, PlatformCorrelationV1.Current?.CorrelationId);
        }));
        Assert.Null(PlatformCorrelationV1.Current);
    }

    [Fact]
    public async Task RemoteWorkerRestoresEnvelopeCorrelationCausationAndW3CParentWithoutPayloadLogging()
    {
        using var listener = new ActivityListener { ShouldListenTo = source => source.Name == "Hispania.Platform", Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded };
        ActivitySource.AddActivityListener(listener);
        using var parent = PlatformTracingV1.Start("test.remote.dispatch");
        var handler = new RecordingRemoteHandler();
        var worker = new CompositeDistributedCapabilityExecutor(new EmptyRemoteExecutor(), [handler]);
        var request = new DistributedJobRequestV1("job-1", "test.capability", "1.0", "Execute", JsonSerializer.SerializeToElement(new { password = "sensitive" }), CorrelationId: "flow-A", CausationId: "event-1", TraceParent: parent!.Id);
        // Serialize the protocol envelope, as the HTTP worker transport does.
        var transported = JsonSerializer.Deserialize<DistributedJobRequestV1>(JsonSerializer.Serialize(request))!;
        await worker.ExecuteAsync(transported, 1, default);
        Assert.Equal("flow-A", handler.Context!.CorrelationId); Assert.Equal("event-1", handler.Context.CausationId);
        Assert.Equal(parent.TraceId, handler.TraceId); Assert.Equal(parent.SpanId, handler.ParentSpanId);
        Assert.Null(PlatformCorrelationV1.Current);
    }

    [Theory]
    [InlineData("")]
    [InlineData("contains spaces")]
    [InlineData("contains\r\nheaders")]
    public void InvalidCorrelationMetadataIsRejected(string id) => Assert.False(PlatformCorrelationV1.IsValidId(id));

    private sealed record TestEvent : IPlatformEvent { public string EventType => "test.observability"; public int Version => 1; }
    private sealed class RecordingAudit : IPlatformAuditWriter
    {
        public List<AuditRecord> Records { get; } = [];
        public ValueTask WriteAsync(AuditRecord record, CancellationToken cancellationToken = default) { Records.Add(record); return ValueTask.CompletedTask; }
    }
    private sealed class ThrowingMetrics : IPlatformMetricsV1
    {
        public void Increment(string name, long amount = 1) => throw new IOException("secret");
        public void Gauge(string name, long value) => throw new IOException("secret");
        public void Observe(string name, double milliseconds) => throw new IOException("secret");
        public IReadOnlyList<PlatformMetricSnapshotV1> Snapshot() => [];
    }
    private sealed class RecordingRemoteHandler : IDistributedCapabilityHandlerV1
    {
        public IReadOnlyCollection<DistributedCapabilityV1> Capabilities => [new("test.capability", "1.0")];
        public CorrelationContextV1? Context { get; private set; }
        public ActivityTraceId TraceId { get; private set; }
        public ActivitySpanId ParentSpanId { get; private set; }
        public ValueTask<JsonElement> ExecuteAsync(DistributedJobRequestV1 request, long generation, CancellationToken cancellationToken)
        { Context = PlatformCorrelationV1.Current; TraceId = Activity.Current!.TraceId; ParentSpanId = Activity.Current.ParentSpanId; return ValueTask.FromResult(JsonSerializer.SerializeToElement(new { succeeded = true })); }
    }
    private sealed class EmptyRemoteExecutor : IDistributedCapabilityExecutorV1
    {
        public IReadOnlyCollection<DistributedCapabilityV1> ListCapabilities() => [];
        public ValueTask<JsonElement> ExecuteAsync(DistributedJobRequestV1 request, CancellationToken cancellationToken) => throw new InvalidOperationException();
    }
    private sealed record EmptyPayload;
    private sealed class SecretFailingHandler : IPlatformJobHandlerV1<EmptyPayload>
    {
        public string JobType => "test.failure";
        public ValueTask<PlatformJobResultMetadataV1?> ExecuteAsync(EmptyPayload payload, CancellationToken cancellationToken) => throw new InvalidOperationException("password-secret bearer-token bootstrap-secret");
    }
    private sealed class RecordingLogger : ILogger<LocalPlatformJobDispatcher>
    {
        public List<(string? Name, string Message)> Events { get; } = [];
        public List<IReadOnlyDictionary<string, object?>> Scopes { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull { if (state is IReadOnlyDictionary<string, object?> scope) Scopes.Add(scope); return null; }
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) => Events.Add((eventId.Name, formatter(state, exception)));
    }
}
