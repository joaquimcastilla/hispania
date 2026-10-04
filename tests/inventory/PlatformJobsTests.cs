using System.Text.Json;
using System.Globalization;
using Hispania.Platform.Contracts.Jobs;
using Hispania.Platform.Jobs.Application;
using Hispania.Platform.Persistence.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Hispania.Inventory.Tests;

public sealed class PlatformJobsTests
{
    [Fact]
    public async Task JobsPersistIdempotentlyAndKeepScheduledWorkUnclaimedUntilDue()
    {
        using var temp = new JobTemp(); var path = temp.File("jobs.sqlite");
        var registry = Registry(); var options = new PlatformJobOptionsV1(MaximumAttempts: 3);
        PlatformJobCreateResultV1 created;
        var store = new SqlitePlatformJobStore(path);
        {
            var service = new PlatformJobService(store, registry, options);
            var request = new CreatePlatformJobV1(PlatformTestHashHandlerV1.TypeId, "{\"text\":\"abc\",\"algorithm\":\"sha256\"}", DateTimeOffset.UtcNow.AddHours(1), IdempotencyKey: "hash:abc");
            created = await service.CreateAsync(request);
            var duplicate = await service.CreateAsync(request);
            Assert.True(duplicate.Duplicate); Assert.Equal(created.Job.JobId, duplicate.Job.JobId);
            Assert.Null(await store.AcquireNextAsync(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(5), default));
        }
        var reopened = new SqlitePlatformJobStore(path);
        {
            var job = await reopened.GetAsync(created.Job.JobId, default);
            Assert.NotNull(job); Assert.Equal(PlatformJobStatusV1.Pending, job.Status); Assert.Equal("hash:abc", job.IdempotencyKey);
            var rows = await reopened.ListAsync(null, 20, default); Assert.Single(rows); Assert.DoesNotContain("abc", JsonSerializer.Serialize(rows));
        }
    }

    [Fact]
    public async Task RegisteredHashJobReturnsDeterministicSafeMetadata()
    {
        var handler = new PlatformTestHashHandlerV1();
        var result = await handler.ExecuteAsync(new("abc", "sha256"), CancellationToken.None);
        Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", result!.Metadata!["hash"]);
        await Assert.ThrowsAsync<InvalidDataException>(async () => await handler.ExecuteAsync(new("abc", "md5"), CancellationToken.None));
    }

    [Fact]
    public async Task RemoteOnlyJobWaitsUntilRemoteExecutorIsEligibleForAcquisition()
    {
        using var temp = new JobTemp(); var store = new SqlitePlatformJobStore(temp.File("jobs.sqlite")); var now = DateTimeOffset.UtcNow;
        var job = new PlatformJobV1(Guid.NewGuid(), PlatformTestHashHandlerV1.TypeId, PlatformJobStatusV1.Pending,
            now, now, null, null, 0, 3, null, "trace-remote-only", null, null,
            "{\"text\":\"abc\",\"algorithm\":\"sha256\"}", null, null, "local", null, PlatformJobExecutionPolicyV1.RemoteOnly);
        await store.CreateAsync(job, default);
        Assert.Null(await store.AcquireNextAsync(now, TimeSpan.FromSeconds(5), [PlatformJobExecutionPolicyV1.LocalOnly, PlatformJobExecutionPolicyV1.RemotePreferred], default));
        Assert.Equal(PlatformJobStatusV1.Pending, (await store.GetAsync(job.JobId, default))!.Status);
        Assert.NotNull(await store.AcquireNextAsync(now, TimeSpan.FromSeconds(5), [PlatformJobExecutionPolicyV1.RemoteOnly], default));
    }

    [Fact]
    public async Task CompetingSqliteExecutorsAcquireAJobOnlyOnce()
    {
        using var temp = new JobTemp(); var path = temp.File("jobs.sqlite");
        var first = new SqlitePlatformJobStore(path); var second = new SqlitePlatformJobStore(path);
        var created = await Service(first).CreateAsync(HashRequest()); var now = DateTimeOffset.UtcNow;
        var acquisitions = await Task.WhenAll(first.AcquireNextAsync(now, TimeSpan.FromSeconds(10), default).AsTask(), second.AcquireNextAsync(now, TimeSpan.FromSeconds(10), default).AsTask());
        Assert.Single(acquisitions, x => x is not null);
        Assert.Single(await first.GetAttemptsAsync(created.Job.JobId, default));
    }

    [Fact]
    public async Task ExpiredRunningLeaseRecoversAsAnotherAtLeastOnceAttempt()
    {
        using var temp = new JobTemp(); var path = temp.File("jobs.sqlite");
        var beforeCrash = new SqlitePlatformJobStore(path); var created = await Service(beforeCrash).CreateAsync(HashRequest());
        var first = await beforeCrash.AcquireNextAsync(DateTimeOffset.UtcNow, TimeSpan.FromMilliseconds(40), default); Assert.NotNull(first);
        await Task.Delay(80);
        var afterRestart = new SqlitePlatformJobStore(path);
        var recovered = await afterRestart.AcquireNextAsync(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(3), default);
        Assert.NotNull(recovered); Assert.Equal(2, recovered.AttemptCount);
        var attempts = await afterRestart.GetAttemptsAsync(created.Job.JobId, default);
        Assert.Collection(attempts, a => Assert.Equal(PlatformJobAttemptOutcomeV1.Recovered, a.Outcome), a => Assert.Null(a.CompletedAt));
        await afterRestart.CompleteAsync(created.Job.JobId, 2, "{}", default);
        Assert.Equal(PlatformJobStatusV1.Succeeded, (await afterRestart.GetAsync(created.Job.JobId, default))!.Status);
        Assert.False(await afterRestart.CompleteAsync(created.Job.JobId, 1, "{\"late\":true}", default));
        Assert.Equal("{}", (await afterRestart.GetAsync(created.Job.JobId, default))!.ResultMetadataJson);
    }

    [Fact]
    public async Task RetryBackoffDeadLetterAndManualRequeueAreDurable()
    {
        using var temp = new JobTemp(); var store = new SqlitePlatformJobStore(temp.File("jobs.sqlite"));
        var created = await Service(store).CreateAsync(HashRequest(maxAttempts: 2));
        var first = await store.AcquireNextAsync(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(5), default); Assert.NotNull(first);
        await store.FailAsync(first.JobId, first.AttemptCount, PlatformJobAttemptOutcomeV1.Failed, "safe summary", DateTimeOffset.UtcNow.AddHours(1), default);
        Assert.Equal(PlatformJobStatusV1.Pending, (await store.GetAsync(first.JobId, default))!.Status);
        Assert.Null(await store.AcquireNextAsync(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(5), default));
        var second = await store.AcquireNextAsync(DateTimeOffset.UtcNow.AddHours(1), TimeSpan.FromSeconds(5), default); Assert.NotNull(second);
        await store.FailAsync(second.JobId, second.AttemptCount, PlatformJobAttemptOutcomeV1.Failed, "safe summary", DateTimeOffset.UtcNow.AddSeconds(1), default);
        Assert.Equal(PlatformJobStatusV1.DeadLettered, (await store.GetAsync(second.JobId, default))!.Status);
        Assert.Equal(2, (await store.GetAttemptsAsync(second.JobId, default)).Count);
        Assert.Single(await store.ListPendingTerminalAuditsAsync(10, default));
        await store.MarkTerminalAuditRecordedAsync(second.JobId, default); Assert.Empty(await store.ListPendingTerminalAuditsAsync(10, default));
        var reopened = new SqlitePlatformJobStore(temp.File("jobs.sqlite")); Assert.Equal(PlatformJobStatusV1.DeadLettered, (await reopened.GetAsync(second.JobId, default))!.Status);
        Assert.True(await store.RequeueAsync(second.JobId, default));
        Assert.Equal(PlatformJobStatusV1.Pending, (await store.GetAsync(second.JobId, default))!.Status);
        Assert.False(await store.RequeueAsync(second.JobId, default));
    }

    [Fact]
    public async Task PendingJobCancelsImmediatelyWithoutCreatingAnAttempt()
    {
        using var temp = new JobTemp(); var store = new SqlitePlatformJobStore(temp.File("jobs.sqlite")); var service = Service(store);
        var created = await service.CreateAsync(HashRequest());
        Assert.True(await service.CancelAsync(created.Job.JobId));
        Assert.Equal(PlatformJobStatusV1.Cancelled, (await service.GetAsync(created.Job.JobId))!.Status);
        Assert.Empty(await service.GetAttemptsAsync(created.Job.JobId));
        Assert.Null(await store.AcquireNextAsync(DateTimeOffset.UtcNow.AddHours(1), TimeSpan.FromSeconds(2), default));
    }

    [Fact]
    public async Task LocalExecutorRetriesThenSucceedsAndHonorsCooperativeCancelAndTimeout()
    {
        using var temp = new JobTemp(); var path = temp.File("jobs.sqlite"); var store = new SqlitePlatformJobStore(path);
        var retryHandler = new RetryHandler(2); var registry = new PlatformJobHandlerRegistryV1().Register(retryHandler);
        var options = new PlatformJobOptionsV1(MaximumAttempts: 3, RetryDelay: TimeSpan.FromMilliseconds(10), ExecutionLease: TimeSpan.FromSeconds(2), PollInterval: TimeSpan.FromMilliseconds(5));
        using var dispatcher = new LocalPlatformJobDispatcher(store, registry, options, NullLogger<LocalPlatformJobDispatcher>.Instance);
        var service = new PlatformJobService(store, registry, options, execution: dispatcher);
        await dispatcher.StartAsync(default);
        try
        {
            var retried = await service.CreateAsync(new(RetryHandler.TypeId, "{}", MaxAttempts: 3));
            await EventuallyAsync(async () => (await service.GetAsync(retried.Job.JobId))?.Status == PlatformJobStatusV1.Succeeded);
            Assert.Equal(3, (await service.GetAttemptsAsync(retried.Job.JobId)).Count);

            var waitingHandler = new CooperativeHandler(); registry.Register(waitingHandler);
            var running = await service.CreateAsync(new(CooperativeHandler.TypeId, "{}")); await waitingHandler.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(await service.CancelAsync(running.Job.JobId));
            await EventuallyAsync(async () => (await service.GetAsync(running.Job.JobId))?.Status == PlatformJobStatusV1.Cancelled);
            Assert.Equal(PlatformJobAttemptOutcomeV1.Cancelled, Assert.Single(await service.GetAttemptsAsync(running.Job.JobId)).Outcome);

            var timeoutHandler = new CooperativeHandler(TimeoutHandlerTypeId); registry.Register(timeoutHandler);
            var timeout = await service.CreateAsync(new(TimeoutHandlerTypeId, "{}", MaxAttempts: 1, Timeout: TimeSpan.FromMilliseconds(150)));
            await EventuallyAsync(async () => (await service.GetAsync(timeout.Job.JobId))?.Status == PlatformJobStatusV1.DeadLettered);
            Assert.Equal(PlatformJobAttemptOutcomeV1.TimedOut, Assert.Single(await service.GetAttemptsAsync(timeout.Job.JobId)).Outcome);
        }
        finally { await dispatcher.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task RemoteOnlyCancellationAndTimeoutStayOwnedByPlatformJobStore()
    {
        using var temp = new JobTemp(); var store = new SqlitePlatformJobStore(temp.File("jobs.sqlite")); var remote = new BlockingRemoteJobExecutor();
        var registry = Registry(); var options = new PlatformJobOptionsV1(MaximumAttempts: 1, PollInterval: TimeSpan.FromMilliseconds(5), ExecutionLease: TimeSpan.FromSeconds(2));
        using var dispatcher = new LocalPlatformJobDispatcher(store, registry, options, NullLogger<LocalPlatformJobDispatcher>.Instance, remote: remote);
        var service = new PlatformJobService(store, registry, options, execution: dispatcher, remote: remote);
        await dispatcher.StartAsync(default);
        try
        {
            var cancelled = await service.CreateAsync(new(PlatformTestHashHandlerV1.TypeId, "{}", ExecutionPolicy: PlatformJobExecutionPolicyV1.RemoteOnly));
            Assert.Equal(cancelled.Job.JobId, await remote.StartedJobs.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(30)));
            Assert.True(await service.CancelAsync(cancelled.Job.JobId));
            Assert.False(await store.ExtendLeaseAsync(cancelled.Job.JobId, DateTimeOffset.UtcNow.AddMinutes(1), default));
            await EventuallyAsync(async () => (await service.GetAsync(cancelled.Job.JobId))?.Status == PlatformJobStatusV1.Cancelled, TimeSpan.FromSeconds(30));
            Assert.Equal(PlatformJobAttemptOutcomeV1.Cancelled, Assert.Single(await service.GetAttemptsAsync(cancelled.Job.JobId)).Outcome);

            var timedOut = await service.CreateAsync(new(PlatformTestHashHandlerV1.TypeId, "{}", MaxAttempts: 1,
                Timeout: TimeSpan.FromSeconds(1), ExecutionPolicy: PlatformJobExecutionPolicyV1.RemoteOnly));
            Assert.Equal(1, timedOut.Job.TimeoutSeconds);
            // Observe dispatch separately from terminal persistence; a loaded CI host may queue the
            // second job after cancellation. Its execution timeout remains one second.
            Assert.Equal(timedOut.Job.JobId, await remote.StartedJobs.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(30)));
            await EventuallyAsync(async () => (await service.GetAsync(timedOut.Job.JobId))?.Status == PlatformJobStatusV1.DeadLettered, TimeSpan.FromSeconds(30));
            Assert.Equal(PlatformJobAttemptOutcomeV1.TimedOut, Assert.Single(await service.GetAttemptsAsync(timedOut.Job.JobId)).Outcome);
        }
        finally { await dispatcher.StopAsync(CancellationToken.None); }
    }

    private static PlatformJobService Service(IPlatformJobStoreV1 store) => new(store, Registry(), new PlatformJobOptionsV1());
    private static PlatformJobHandlerRegistryV1 Registry() => new PlatformJobHandlerRegistryV1().Register(new PlatformTestHashHandlerV1());
    private static CreatePlatformJobV1 HashRequest(int maxAttempts = 3) => new(PlatformTestHashHandlerV1.TypeId, "{\"text\":\"abc\",\"algorithm\":\"sha256\"}", MaxAttempts: maxAttempts);
    private const string TimeoutHandlerTypeId = "platform.test.timeout";
    private static async Task EventuallyAsync(Func<Task<bool>> predicate, TimeSpan? observationTimeout = null)
    {
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        while (elapsed.Elapsed < (observationTimeout ?? TimeSpan.FromSeconds(5)))
        { if (await predicate()) return; await Task.Delay(10); }
        Assert.True(await predicate(), "Expected durable job state was not reached.");
    }

    private sealed class RetryHandler(int failures) : IPlatformJobHandlerV1<Dictionary<string, string>>
    {
        public const string TypeId = "platform.test.retry"; private int calls;
        public string JobType => TypeId;
        public ValueTask<PlatformJobResultMetadataV1?> ExecuteAsync(Dictionary<string, string> payload, CancellationToken cancellationToken)
        { if (Interlocked.Increment(ref calls) <= failures) throw new InvalidOperationException("temporary"); return ValueTask.FromResult<PlatformJobResultMetadataV1?>(new("text/plain", null, new Dictionary<string,string> { ["call"] = calls.ToString(CultureInfo.InvariantCulture) })); }
    }
    private sealed class CooperativeHandler(string jobType = "platform.test.cancel") : IPlatformJobHandlerV1<Dictionary<string, string>>
    {
        public const string TypeId = "platform.test.cancel"; public string JobType => jobType;
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask<PlatformJobResultMetadataV1?> ExecuteAsync(Dictionary<string, string> payload, CancellationToken cancellationToken)
        { Started.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); return null; }
    }
    private sealed class BlockingRemoteJobExecutor : IPlatformRemoteJobExecutorV1
    {
        public System.Threading.Channels.Channel<Guid> StartedJobs { get; } = System.Threading.Channels.Channel.CreateUnbounded<Guid>();
        public string? GetCapability(string jobType) => jobType == PlatformTestHashHandlerV1.TypeId ? "platform.jobs.hash@1.0" : null;
        public bool IsAvailable(string jobType) => jobType == PlatformTestHashHandlerV1.TypeId;
        public async ValueTask<PlatformRemoteJobExecutionV1?> ExecuteAsync(PlatformJobV1 job, int attemptNumber, CancellationToken cancellationToken)
        { StartedJobs.Writer.TryWrite(job.JobId); await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); return null; }
        public bool Cancel(Guid jobId) => true;
    }
    private sealed class JobTemp : IDisposable
    {
        private readonly string directory = Path.Combine(Path.GetTempPath(), "hispania-jobs-" + Guid.NewGuid().ToString("N"));
        public JobTemp() => Directory.CreateDirectory(directory);
        public string File(string name) => Path.Combine(directory, name);
        public void Dispose() { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
}
