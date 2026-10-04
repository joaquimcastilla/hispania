using System.Collections.Concurrent;
using Hispania.Platform.Contracts.Observability;
using System.Diagnostics;
using System.Text.Json;
using Hispania.ModuleSystem.Contracts;
using Hispania.Platform.Contracts.Jobs;
using Hispania.Runtime;

namespace Hispania.Platform.WorkerAgentJobs;

/// <summary>Host adapter over the existing single-coordinator worker protocol.</summary>
public sealed class DistributedPlatformJobExecutor(IDistributedWorkCoordinatorV1 coordinator, TimeSpan? pollInterval = null,
    TimeSpan? preferredWait = null, IPlatformMetricsV1? metrics = null) : IPlatformRemoteJobExecutorV1
{
    public const string HashJobType = PlatformTestHashHandlerV1.TypeId;
    public const string HashCapabilityId = "platform.jobs.hash";
    public const string HashCapabilityVersion = "1.0";
    public const string DelayJobType = PlatformTestDelayHandlerV1.TypeId;
    public const string DelayCapabilityId = "platform.jobs.delay";
    public const string DelayCapabilityVersion = "1.0";
    private static readonly DistributedCapabilityV1 HashCapability = new(HashCapabilityId, HashCapabilityVersion);
    private static readonly DistributedCapabilityV1 DelayCapability = new(DelayCapabilityId, DelayCapabilityVersion);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly TimeSpan poll = pollInterval ?? TimeSpan.FromMilliseconds(50);
    private readonly TimeSpan preferredWait = preferredWait ?? TimeSpan.FromMilliseconds(750);
    private readonly ConcurrentDictionary<Guid, string> active = new();

    private static DistributedCapabilityV1? CapabilityFor(string jobType) => jobType switch
    {
        HashJobType => HashCapability,
        DelayJobType => DelayCapability,
        _ => null
    };
    public string? GetCapability(string jobType) => CapabilityFor(jobType) is { } capability ? $"{capability.ContractId}@{capability.ContractVersion}" : null;
    public bool IsAvailable(string jobType)
    {
        var capability = CapabilityFor(jobType);
        if (capability is null) return false;
        var now = DateTimeOffset.UtcNow;
        var workers = coordinator.ListWorkers().Where(worker => worker.Authorized && worker.Enabled && worker.Connected &&
            worker.LastHeartbeatUtc >= now - TimeSpan.FromSeconds(30) && worker.Inventory?.Capabilities.Contains(capability) == true &&
            worker.AllowedCapabilities.Contains(capability)).ToArray();
        var activeByWorker = coordinator.ListJobs().Where(job => job.NodeId is not null && job.State is DistributedJobStateV1.Assigned or DistributedJobStateV1.Running)
            .GroupBy(job => job.NodeId!, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        return workers.Any(worker => !activeByWorker.TryGetValue(worker.NodeId, out var count) || count < worker.Inventory!.MaximumConcurrentJobs);
    }

    public async ValueTask<PlatformRemoteJobExecutionV1?> ExecuteAsync(PlatformJobV1 job, int attemptNumber, CancellationToken cancellationToken)
        => await ExecuteAsync(job, attemptNumber, static (_, _) => ValueTask.CompletedTask, cancellationToken).ConfigureAwait(false);

    public async ValueTask<PlatformRemoteJobExecutionV1?> ExecuteAsync(PlatformJobV1 job, int attemptNumber,
        Func<PlatformRemoteJobExecutionV1, CancellationToken, ValueTask> onStarted, CancellationToken cancellationToken)
    {
        var capability = CapabilityFor(job.JobType);
        if (capability is null) return null;
        if (!IsAvailable(job.JobType) && job.ExecutionPolicy == PlatformJobExecutionPolicyV1.RemotePreferred) return null;
        while (!IsAvailable(job.JobType)) await Task.Delay(poll, cancellationToken).ConfigureAwait(false);
        using var activity = PlatformTracingV1.Start("job.remote.dispatch", ActivityKind.Producer);
        metrics.BestEffort(sink => sink.Increment("hispania.workers.executions"));
        var remoteId = $"{job.JobId:N}-{attemptNumber}";
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(new { payloadJson = job.PayloadJson }, JsonOptions));
        var request = new DistributedJobRequestV1(remoteId, capability.ContractId, capability.ContractVersion, "Execute",
            document.RootElement.Clone(), RetrySafe: true, CorrelationId: job.CorrelationId, TimeoutSeconds: job.TimeoutSeconds, CausationId: job.CausationId, TraceParent: Activity.Current?.Id ?? job.TraceParent, TraceState: Activity.Current?.TraceStateString ?? job.TraceState);
        coordinator.Queue(request, allowRemote: true);
        active[job.JobId] = remoteId;
        var due = DateTimeOffset.UtcNow + preferredWait;
        var reportedStart = false;
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var snapshot = coordinator.ListJobs().SingleOrDefault(item => item.Request.JobId == remoteId);
                if (snapshot is null) throw new InvalidOperationException("Remote coordinator lost the queued capability request.");
                if (snapshot.State == DistributedJobStateV1.Completed)
                {
                    var result = snapshot.Result?.Deserialize<PlatformJobResultMetadataV1>()
                        ?? throw new InvalidDataException("Worker returned invalid Platform Job result metadata.");
                    return new(result, snapshot.NodeId ?? throw new InvalidDataException("Completed remote request has no worker identity."),
                        GetCapability(job.JobType)!, snapshot.Generation, $"{remoteId}:{snapshot.Generation}");
                }
                if (!reportedStart && snapshot.State is (DistributedJobStateV1.Assigned or DistributedJobStateV1.Running) && snapshot.NodeId is { } nodeId && snapshot.Generation > 0)
                {
                    await onStarted(new(null, nodeId, GetCapability(job.JobType)!, snapshot.Generation, $"{remoteId}:{snapshot.Generation}"), cancellationToken).ConfigureAwait(false);
                    reportedStart = true;
                }
                if (snapshot.State is DistributedJobStateV1.Failed or DistributedJobStateV1.Uncertain)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    throw new InvalidOperationException($"Remote capability attempt ended as {snapshot.State} ({snapshot.FailureCode ?? "no failure code"}).");
                }
                if (snapshot.State == DistributedJobStateV1.Cancelled)
                    throw new OperationCanceledException("Remote capability attempt was cancelled.", cancellationToken);
                if (job.ExecutionPolicy == PlatformJobExecutionPolicyV1.RemotePreferred && snapshot.State == DistributedJobStateV1.Pending && DateTimeOffset.UtcNow >= due)
                {
                    if (coordinator.CancelJob(remoteId)) return null;
                    // A worker claimed it concurrently; wait for that generation instead of racing a local fallback.
                }
                await Task.Delay(poll, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            _ = coordinator.CancelJob(remoteId);
            throw;
        }
        catch (Exception) { metrics.BestEffort(sink => sink.Increment("hispania.workers.failures")); throw; }
        finally { active.TryRemove(job.JobId, out _); }
    }

    public bool Cancel(Guid jobId) => active.TryGetValue(jobId, out var remoteId) && coordinator.CancelJob(remoteId);
}

/// <summary>Trusted, explicitly installed Platform test capability provider for the generic WorkerAgent.</summary>
public sealed class PlatformJobsCapabilityProviderV1 : IDistributedWorkerCapabilityProviderV1
{
    public IReadOnlyCollection<IDistributedCapabilityHandlerV1> CreateHandlers(DistributedWorkerCapabilityContextV1 context) => [new HashJobCapabilityHandler(), new DelayJobCapabilityHandler()];

    private sealed class HashJobCapabilityHandler : IDistributedCapabilityHandlerV1
    {
        private static readonly DistributedCapabilityV1 Capability = new(DistributedPlatformJobExecutor.HashCapabilityId, DistributedPlatformJobExecutor.HashCapabilityVersion);
        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
        private readonly PlatformTestHashHandlerV1 handler = new();
        public IReadOnlyCollection<DistributedCapabilityV1> Capabilities { get; } = [Capability];
        public async ValueTask<JsonElement> ExecuteAsync(DistributedJobRequestV1 request, long generation, CancellationToken cancellationToken)
        {
            if (request.ContractId != Capability.ContractId || request.ContractVersion != Capability.ContractVersion || request.Operation != "Execute")
                throw new InvalidDataException("Unsupported Platform Job capability operation.");
            if (!request.Payload.TryGetProperty("payloadJson", out var raw) || raw.ValueKind != JsonValueKind.String)
                throw new InvalidDataException("Platform Job capability payload is invalid.");
            var payload = raw.GetString() ?? throw new InvalidDataException("Platform Job capability payload is empty.");
            if (System.Text.Encoding.UTF8.GetByteCount(payload) > 262144) throw new InvalidDataException("Platform Job capability payload exceeds the configured limit.");
            var typed = JsonSerializer.Deserialize<PlatformTestHashPayloadV1>(payload, JsonOptions)
                ?? throw new InvalidDataException("Platform Job capability payload is invalid.");
            var result = await handler.ExecuteAsync(typed, cancellationToken).ConfigureAwait(false);
            return JsonSerializer.SerializeToElement(result, JsonOptions);
        }
    }

    private sealed class DelayJobCapabilityHandler : IDistributedCapabilityHandlerV1
    {
        private static readonly DistributedCapabilityV1 Capability = new(DistributedPlatformJobExecutor.DelayCapabilityId, DistributedPlatformJobExecutor.DelayCapabilityVersion);
        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
        public IReadOnlyCollection<DistributedCapabilityV1> Capabilities { get; } = [Capability];
        public async ValueTask<JsonElement> ExecuteAsync(DistributedJobRequestV1 request, long generation, CancellationToken cancellationToken)
        {
            if (request.ContractId != Capability.ContractId || request.ContractVersion != Capability.ContractVersion || request.Operation != "Execute")
                throw new InvalidDataException("Unsupported Platform test delay operation.");
            if (!request.Payload.TryGetProperty("payloadJson", out var raw) || raw.ValueKind != JsonValueKind.String)
                throw new InvalidDataException("Platform test delay payload is invalid.");
            var payloadJson = raw.GetString() ?? throw new InvalidDataException("Platform test delay payload is empty.");
            if (System.Text.Encoding.UTF8.GetByteCount(payloadJson) > 4096) throw new InvalidDataException("Platform test delay payload exceeds the configured limit.");
            var payload = JsonSerializer.Deserialize<PlatformTestDelayPayloadV1>(payloadJson, JsonOptions)
                ?? throw new InvalidDataException("Platform test delay payload is invalid.");
            var result = await new PlatformTestDelayHandlerV1().ExecuteAsync(payload, cancellationToken).ConfigureAwait(false);
            return JsonSerializer.SerializeToElement(result, JsonOptions);
        }
    }
}
