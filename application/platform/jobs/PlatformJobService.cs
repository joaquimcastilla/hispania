using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Hispania.Platform.Contracts.Audit;
using Hispania.Platform.Contracts.Jobs;

namespace Hispania.Platform.Jobs.Application;

/// <summary>Application-owned persistence port; it is not part of the external Platform Job API.</summary>
public interface IPlatformJobStoreV1
{
    ValueTask<PlatformJobCreateResultV1> CreateAsync(PlatformJobV1 job, CancellationToken cancellationToken);
    ValueTask<PlatformJobV1?> GetAsync(Guid jobId, CancellationToken cancellationToken);
    ValueTask<IReadOnlyList<PlatformJobListItemV1>> ListAsync(PlatformJobStatusV1? status, int limit, CancellationToken cancellationToken);
    ValueTask<IReadOnlyList<PlatformJobAttemptV1>> GetAttemptsAsync(Guid jobId, CancellationToken cancellationToken);
    ValueTask<PlatformJobV1?> AcquireNextAsync(DateTimeOffset now, TimeSpan lease, IReadOnlyCollection<PlatformJobExecutionPolicyV1> policies, CancellationToken cancellationToken);
    ValueTask<PlatformJobV1?> AcquireNextAsync(DateTimeOffset now, TimeSpan lease, CancellationToken cancellationToken) =>
        AcquireNextAsync(now, lease, Enum.GetValues<PlatformJobExecutionPolicyV1>(), cancellationToken);
    ValueTask<bool> ExtendLeaseAsync(Guid jobId, DateTimeOffset expiresAt, CancellationToken cancellationToken);
    ValueTask<bool> CompleteAsync(Guid jobId, int attempt, string? resultJson, CancellationToken cancellationToken);
    ValueTask<bool> SetExecutionDetailsAsync(Guid jobId, int attempt, string executorKind, string? workerId, string? capability, long? generation, string? leaseId, CancellationToken cancellationToken);
    ValueTask FailAsync(Guid jobId, int attempt, PlatformJobAttemptOutcomeV1 outcome, string errorSummary, DateTimeOffset nextAttemptAt, CancellationToken cancellationToken);
    ValueTask<bool> RequestCancelAsync(Guid jobId, CancellationToken cancellationToken);
    ValueTask<bool> IsCancellationRequestedAsync(Guid jobId, CancellationToken cancellationToken);
    ValueTask<bool> RequeueAsync(Guid jobId, CancellationToken cancellationToken);
    ValueTask<IReadOnlyList<PlatformJobV1>> ListPendingTerminalAuditsAsync(int limit, CancellationToken cancellationToken);
    ValueTask MarkTerminalAuditRecordedAsync(Guid jobId, CancellationToken cancellationToken);
    ValueTask<PlatformJobHealthV1> GetHealthAsync(bool dispatcherActive, CancellationToken cancellationToken);
}

public sealed class PlatformJobService(IPlatformJobStoreV1 store, IPlatformJobHandlerRegistryV1 handlers,
    PlatformJobOptionsV1 options, IPlatformAuditWriter? audit = null, IPlatformJobExecutionControlV1? execution = null,
    PlatformJobMetricsV1? metrics = null, IPlatformRemoteJobExecutorV1? remote = null) : IPlatformJobServiceV1
{
    public async ValueTask<PlatformJobCreateResultV1> CreateAsync(CreatePlatformJobV1 request, CancellationToken cancellationToken = default)
    {
        if (!PlatformJobHandlerRegistryV1.IsValidJobType(request.JobType) || !handlers.Contains(request.JobType)) throw new ArgumentException("JobType is not registered.", nameof(request));
        if (!Enum.IsDefined(request.ExecutionPolicy) || request.ExecutionPolicy == PlatformJobExecutionPolicyV1.RemoteOnly && remote?.GetCapability(request.JobType) is null)
            throw new ArgumentException("Execution policy is unsupported for this registered JobType.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.PayloadJson) || Encoding.UTF8.GetByteCount(request.PayloadJson) > options.MaximumPayloadBytes) throw new ArgumentException("Payload is empty or exceeds the configured limit.", nameof(request));
        using (JsonDocument.Parse(request.PayloadJson)) { }
        if (request.IdempotencyKey is { Length: > 128 } || request.IdempotencyKey is not null && string.IsNullOrWhiteSpace(request.IdempotencyKey)) throw new ArgumentException("Idempotency key is invalid.", nameof(request));
        var maxAttempts = request.MaxAttempts ?? options.MaximumAttempts;
        if (maxAttempts is < 1 or > 100 || request.Timeout is { } timeout && (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromDays(1))) throw new ArgumentOutOfRangeException(nameof(request));
        var now = DateTimeOffset.UtcNow;
        var job = new PlatformJobV1(Guid.NewGuid(), request.JobType, PlatformJobStatusV1.Pending, now,
            (request.ScheduledAt ?? now).ToUniversalTime(), null, null, 0, maxAttempts,
            request.Timeout is null ? null : (int)Math.Ceiling(request.Timeout.Value.TotalSeconds),
            Limit(request.CorrelationId, 128), Limit(request.CausationId, 128), request.IdempotencyKey,
            request.PayloadJson, null, null, "local", null, request.ExecutionPolicy);
        var result = await store.CreateAsync(job, cancellationToken).ConfigureAwait(false);
        if (!result.Duplicate) { metrics?.Created(); await WriteAuditAsync("created", result.Job, cancellationToken, request.RequestedBy).ConfigureAwait(false); }
        await RefreshMetricsAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }
    public ValueTask<PlatformJobV1?> GetAsync(Guid jobId, CancellationToken cancellationToken = default) => store.GetAsync(jobId, cancellationToken);
    public ValueTask<IReadOnlyList<PlatformJobListItemV1>> ListAsync(PlatformJobStatusV1? status = null, int limit = 100, CancellationToken cancellationToken = default) => store.ListAsync(status, Math.Clamp(limit, 1, 200), cancellationToken);
    public ValueTask<IReadOnlyList<PlatformJobAttemptV1>> GetAttemptsAsync(Guid jobId, CancellationToken cancellationToken = default) => store.GetAttemptsAsync(jobId, cancellationToken);
    public async ValueTask<bool> CancelAsync(Guid jobId, string? requestedBy = null, CancellationToken cancellationToken = default)
    {
        var changed = await store.RequestCancelAsync(jobId, cancellationToken).ConfigureAwait(false);
        if (changed) _ = execution?.Cancel(jobId);
        if (changed && await store.GetAsync(jobId, cancellationToken).ConfigureAwait(false) is { } job) await WriteAuditAsync("cancelled", job, cancellationToken, requestedBy).ConfigureAwait(false);
        await RefreshMetricsAsync(cancellationToken).ConfigureAwait(false);
        return changed;
    }
    public async ValueTask<bool> RetryAsync(Guid jobId, string? requestedBy = null, CancellationToken cancellationToken = default)
    {
        var changed = await store.RequeueAsync(jobId, cancellationToken).ConfigureAwait(false);
        if (changed) { metrics?.Retried(); if (await store.GetAsync(jobId, cancellationToken).ConfigureAwait(false) is { } job) await WriteAuditAsync("manual-retry", job, cancellationToken, requestedBy).ConfigureAwait(false); }
        await RefreshMetricsAsync(cancellationToken).ConfigureAwait(false);
        return changed;
    }
    public async ValueTask<PlatformJobHealthV1> GetHealthAsync(CancellationToken cancellationToken = default)
    { var health = await store.GetHealthAsync(execution?.IsActive ?? false, cancellationToken).ConfigureAwait(false); metrics?.Refresh(health); return health; }
    private async ValueTask RefreshMetricsAsync(CancellationToken ct) { if (metrics is not null) metrics.Refresh(await store.GetHealthAsync(execution?.IsActive ?? false, ct).ConfigureAwait(false)); }
    private ValueTask WriteAuditAsync(string action, PlatformJobV1 job, CancellationToken ct, string? actorId = null) => audit?.WriteAsync(new(DateTimeOffset.UtcNow, actorId, job.CorrelationId, "platform.jobs", $"job.{action}", "job", job.JobId.ToString("D"), "succeeded", new Dictionary<string, string> { ["jobType"] = job.JobType }), ct) ?? ValueTask.CompletedTask;
    private static string? Limit(string? value, int max) => value is null ? null : value[..Math.Min(max, value.Length)];
}

public interface IPlatformJobExecutionControlV1 { bool IsActive { get; } bool Cancel(Guid jobId); }

public sealed class LocalPlatformJobDispatcher(IPlatformJobStoreV1 store, IPlatformJobHandlerRegistryV1 handlers,
    PlatformJobOptionsV1 options, Microsoft.Extensions.Logging.ILogger<LocalPlatformJobDispatcher> logger,
    IPlatformAuditWriter? audit = null, PlatformJobMetricsV1? metrics = null, IPlatformRemoteJobExecutorV1? remote = null) : Microsoft.Extensions.Hosting.BackgroundService, IPlatformJobExecutionControlV1
{
    private static readonly Action<ILogger, Exception?> IterationFailed = LoggerMessage.Define(LogLevel.Error, new EventId(1350, "PlatformJobDispatcherIterationFailed"), "Platform job dispatcher iteration failed.");
    private static readonly Action<ILogger, Guid, string, string, Exception?> JobFailed = LoggerMessage.Define<Guid, string, string>(LogLevel.Warning, new EventId(1351, "PlatformJobFailed"), "Platform job {JobId} ({JobType}) failed: {Error}");
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, CancellationTokenSource> running = new();
    public bool IsActive => !Stopping;
    private volatile bool Stopping;
    public bool Cancel(Guid jobId)
    {
        var remoteSignalled = remote?.Cancel(jobId) ?? false;
        var localSignalled = running.TryGetValue(jobId, out var token) && CancelToken(token);
        return remoteSignalled || localSignalled;
    }
    private static bool CancelToken(CancellationTokenSource token) { try { token.Cancel(); return true; } catch (ObjectDisposedException) { return false; } }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (metrics is not null) metrics.Refresh(await store.GetHealthAsync(IsActive, stoppingToken).ConfigureAwait(false));
                await WritePendingTerminalAuditsAsync(stoppingToken).ConfigureAwait(false);
                var runnablePolicies = remote is null
                    ? new[] { PlatformJobExecutionPolicyV1.LocalOnly, PlatformJobExecutionPolicyV1.RemotePreferred }
                    : remote.IsAvailable(PlatformTestHashHandlerV1.TypeId)
                        ? Enum.GetValues<PlatformJobExecutionPolicyV1>()
                        : new[] { PlatformJobExecutionPolicyV1.LocalOnly, PlatformJobExecutionPolicyV1.RemotePreferred };
                var job = await store.AcquireNextAsync(DateTimeOffset.UtcNow, options.EffectiveExecutionLease, runnablePolicies, stoppingToken).ConfigureAwait(false);
                if (job is null) { await Task.Delay(options.EffectivePollInterval, stoppingToken).ConfigureAwait(false); continue; }
                if (metrics is not null) metrics.Refresh(await store.GetHealthAsync(IsActive, stoppingToken).ConfigureAwait(false));
                await RunAsync(job, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { IterationFailed(logger, ex); await Task.Delay(options.EffectivePollInterval, stoppingToken).ConfigureAwait(false); }
        }
        Stopping = true;
    }

    private async Task RunAsync(PlatformJobV1 job, CancellationToken stoppingToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        if (!running.TryAdd(job.JobId, linked)) return;
        if (await store.IsCancellationRequestedAsync(job.JobId, stoppingToken).ConfigureAwait(false)) linked.Cancel();
        using var timeoutCts = job.TimeoutSeconds is > 0 ? new CancellationTokenSource(TimeSpan.FromSeconds(job.TimeoutSeconds.Value)) : null;
        using var effective = timeoutCts is null ? null : CancellationTokenSource.CreateLinkedTokenSource(linked.Token, timeoutCts.Token);
        var ct = effective?.Token ?? linked.Token;
        using var heartbeatStop = new CancellationTokenSource();
        var heartbeat = RenewLeaseAsync(job, heartbeatStop.Token);
        try
        {
            PlatformJobResultMetadataV1? metadata;
            if (job.ExecutionPolicy != PlatformJobExecutionPolicyV1.LocalOnly && remote?.GetCapability(job.JobType) is not null &&
                (job.ExecutionPolicy == PlatformJobExecutionPolicyV1.RemoteOnly || remote.IsAvailable(job.JobType)))
            {
                var remoteExecution = await remote.ExecuteAsync(job, job.AttemptCount, ct).ConfigureAwait(false);
                if (remoteExecution is null)
                {
                    if (job.ExecutionPolicy == PlatformJobExecutionPolicyV1.RemoteOnly) throw new RemoteWorkerUnavailableException();
                    metrics?.RemoteUnavailable();
                    metadata = await handlers.ExecuteAsync(job.JobType, job.PayloadJson, ct).ConfigureAwait(false);
                    await store.SetExecutionDetailsAsync(job.JobId, job.AttemptCount, "local", null, null, null, null, CancellationToken.None).ConfigureAwait(false);
                    metrics?.LocalExecution();
                }
                else
                {
                    metadata = remoteExecution.Result;
                    await store.SetExecutionDetailsAsync(job.JobId, job.AttemptCount, "remote", remoteExecution.WorkerId, remoteExecution.Capability, remoteExecution.Generation, remoteExecution.LeaseId, CancellationToken.None).ConfigureAwait(false);
                    metrics?.RemoteExecution();
                }
            }
            else
            {
                if (job.ExecutionPolicy == PlatformJobExecutionPolicyV1.RemoteOnly) throw new RemoteWorkerUnavailableException();
                if (job.ExecutionPolicy == PlatformJobExecutionPolicyV1.RemotePreferred && remote?.GetCapability(job.JobType) is not null) metrics?.RemoteUnavailable();
                metadata = await handlers.ExecuteAsync(job.JobType, job.PayloadJson, ct).ConfigureAwait(false);
                await store.SetExecutionDetailsAsync(job.JobId, job.AttemptCount, "local", null, null, null, null, CancellationToken.None).ConfigureAwait(false);
                metrics?.LocalExecution();
            }
            var result = JsonSerializer.Serialize(SanitizeResult(metadata));
            if (Encoding.UTF8.GetByteCount(result) > options.MaximumResultMetadataBytes) throw new InvalidDataException("Job result metadata exceeds the configured limit.");
            ct.ThrowIfCancellationRequested();
            if (!await store.CompleteAsync(job.JobId, job.AttemptCount, result, CancellationToken.None).ConfigureAwait(false))
            {
                metrics?.FencedResult();
                await store.FailAsync(job.JobId, job.AttemptCount, PlatformJobAttemptOutcomeV1.Cancelled, "Job was cancelled.", DateTimeOffset.UtcNow, CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            var timedOut = timeoutCts?.IsCancellationRequested == true;
            var outcome = stoppingToken.IsCancellationRequested && !timedOut ? PlatformJobAttemptOutcomeV1.Recovered : timedOut ? PlatformJobAttemptOutcomeV1.TimedOut : PlatformJobAttemptOutcomeV1.Cancelled;
            await store.FailAsync(job.JobId, job.AttemptCount, outcome,
                outcome == PlatformJobAttemptOutcomeV1.Recovered ? "Host stopped during execution; attempt will be recovered." : timedOut ? "Job timed out." : "Job was cancelled.", DateTimeOffset.UtcNow, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            if (job.ExecutionPolicy != PlatformJobExecutionPolicyV1.LocalOnly) metrics?.RemoteRetried();
            var nonRetryable = ex is InvalidDataException or JsonException;
            var error = SafeError(ex);
            var exhausted = nonRetryable || job.AttemptCount >= job.MaxAttempts;
            await store.FailAsync(job.JobId, job.AttemptCount, PlatformJobAttemptOutcomeV1.Failed, error,
                nonRetryable ? DateTimeOffset.MaxValue : DateTimeOffset.UtcNow + Backoff(options.EffectiveRetryDelay, job.AttemptCount), CancellationToken.None).ConfigureAwait(false);
            if (exhausted)
            {
                JobFailed(logger, job.JobId, job.JobType, error, null);
            }
            else metrics?.Retried();
        }
        finally
        {
            heartbeatStop.Cancel();
            try { await heartbeat.ConfigureAwait(false); } catch (OperationCanceledException) { }
            running.TryRemove(job.JobId, out _);
        }
    }
    private async Task RenewLeaseAsync(PlatformJobV1 job, CancellationToken ct)
    {
        var interval = TimeSpan.FromTicks(Math.Max(1, options.EffectiveExecutionLease.Ticks / 3));
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(interval, ct).ConfigureAwait(false);
            if (!await store.ExtendLeaseAsync(job.JobId, DateTimeOffset.UtcNow + options.EffectiveExecutionLease, ct).ConfigureAwait(false)) return;
        }
    }
    private async Task WritePendingTerminalAuditsAsync(CancellationToken ct)
    {
        if (audit is null) return;
        foreach (var job in await store.ListPendingTerminalAuditsAsync(50, ct).ConfigureAwait(false))
        {
            var dead = job.Status == PlatformJobStatusV1.DeadLettered;
            await audit.WriteAsync(new(DateTimeOffset.UtcNow, null, job.CorrelationId, "platform.jobs", dead ? "job.dead-lettered" : "job.failed", "job", job.JobId.ToString("D"), "failed", new Dictionary<string, string> { ["jobType"] = job.JobType, ["error"] = job.ErrorMetadata ?? "Job failed." }), ct).ConfigureAwait(false);
            await store.MarkTerminalAuditRecordedAsync(job.JobId, ct).ConfigureAwait(false);
        }
    }
    private static string SafeError(Exception ex)
    {
        var type = ex.GetType().Name;
        return $"Handler failed ({type[..Math.Min(type.Length, 96)]}).";
    }
    private static PlatformJobResultMetadataV1? SanitizeResult(PlatformJobResultMetadataV1? result)
    {
        if (result is null) return null;
        string[] sensitiveKeys = ["token", "secret", "password", "credential", "authorization", "content", "body", "payload"];
        var safeMetadata = (result.Metadata ?? new Dictionary<string, string>())
            .Where(pair => !sensitiveKeys.Any(sensitive => pair.Key.Contains(sensitive, StringComparison.OrdinalIgnoreCase)))
            .Take(32)
            .ToDictionary(pair => pair.Key[..Math.Min(pair.Key.Length, 64)], pair => pair.Value[..Math.Min(pair.Value.Length, 512)], StringComparer.OrdinalIgnoreCase);
        return result with
        {
            ContentType = result.ContentType is null ? null : result.ContentType[..Math.Min(result.ContentType.Length, 127)],
            OpaqueResultRef = result.OpaqueResultRef is null ? null : result.OpaqueResultRef[..Math.Min(result.OpaqueResultRef.Length, 256)],
            Metadata = safeMetadata
        };
    }
    private static TimeSpan Backoff(TimeSpan initial, int attempt) => TimeSpan.FromMilliseconds(Math.Min(300_000, initial.TotalMilliseconds * Math.Pow(2, Math.Clamp(attempt - 1, 0, 10))));
    public override Task StopAsync(CancellationToken cancellationToken) { Stopping = true; return base.StopAsync(cancellationToken); }
}

public sealed class RemoteWorkerUnavailableException : Exception { }

public sealed class PlatformJobMetricsV1 : IDisposable
{
    private readonly System.Diagnostics.Metrics.Meter meter = new("Hispania.Platform.Jobs");
    private readonly System.Diagnostics.Metrics.Counter<long> created;
    private readonly System.Diagnostics.Metrics.Counter<long> retried;
    private readonly System.Diagnostics.Metrics.Counter<long> localExecution;
    private readonly System.Diagnostics.Metrics.Counter<long> remoteExecution;
    private readonly System.Diagnostics.Metrics.Counter<long> remoteUnavailable;
    private readonly System.Diagnostics.Metrics.Counter<long> remoteRetried;
    private readonly System.Diagnostics.Metrics.Counter<long> fencedResults;
    private readonly System.Diagnostics.Metrics.ObservableGauge<long>[] gauges;
    private long pending, running, succeeded, failed, cancelled, deadLettered;
    public PlatformJobMetricsV1()
    {
        created = meter.CreateCounter<long>("jobs.created"); retried = meter.CreateCounter<long>("jobs.retried");
        localExecution = meter.CreateCounter<long>("jobs.execution.local"); remoteExecution = meter.CreateCounter<long>("jobs.execution.remote");
        remoteUnavailable = meter.CreateCounter<long>("jobs.remote.unavailable"); remoteRetried = meter.CreateCounter<long>("jobs.remote.retried"); fencedResults = meter.CreateCounter<long>("jobs.remote.fenced_results");
        gauges = [
            meter.CreateObservableGauge("jobs.pending", () => Interlocked.Read(ref pending)),
            meter.CreateObservableGauge("jobs.running", () => Interlocked.Read(ref running)),
            meter.CreateObservableGauge("jobs.succeeded", () => Interlocked.Read(ref succeeded)),
            meter.CreateObservableGauge("jobs.failed", () => Interlocked.Read(ref failed)),
            meter.CreateObservableGauge("jobs.cancelled", () => Interlocked.Read(ref cancelled)),
            meter.CreateObservableGauge("jobs.dead_lettered", () => Interlocked.Read(ref deadLettered))];
    }
    public void Created() => created.Add(1);
    public void Retried() => retried.Add(1);
    public void LocalExecution() => localExecution.Add(1);
    public void RemoteExecution() => remoteExecution.Add(1);
    public void RemoteUnavailable() => remoteUnavailable.Add(1);
    public void RemoteRetried() => remoteRetried.Add(1);
    public void FencedResult() => fencedResults.Add(1);
    public void Refresh(PlatformJobHealthV1 snapshot)
    { Interlocked.Exchange(ref pending, snapshot.Pending); Interlocked.Exchange(ref running, snapshot.Running); Interlocked.Exchange(ref succeeded, snapshot.Succeeded); Interlocked.Exchange(ref failed, snapshot.Failed); Interlocked.Exchange(ref cancelled, snapshot.Cancelled); Interlocked.Exchange(ref deadLettered, snapshot.DeadLettered); }
    public void Dispose() => meter.Dispose();
}
