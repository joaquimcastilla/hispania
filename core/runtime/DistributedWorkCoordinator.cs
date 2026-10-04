using Hispania.ModuleSystem.Contracts;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Hosting;

namespace Hispania.Runtime;

/// <summary>
/// Single-coordinator in-memory implementation of the distributed work state machine.
/// It deliberately does not claim durable or exactly-once execution guarantees.
/// </summary>
public sealed class DistributedWorkCoordinator : IDistributedWorkCoordinatorV1
{
    private readonly object gate = new();
    private readonly Dictionary<string, Worker> workers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Job> jobs = new(StringComparer.Ordinal);
    private readonly IDistributedWorkStateStoreV1? stateStore;

    public DistributedWorkCoordinator(IDistributedWorkStateStoreV1? stateStore = null)
    {
        this.stateStore = stateStore;
        var state = stateStore?.Load();
        if (state is null) return;
        foreach (var snapshot in state.Workers)
        {
            var worker = new Worker(snapshot.NodeId, snapshot.AllowedCapabilities, snapshot.CertificateThumbprint ?? string.Empty)
            {
                Authorized = snapshot.Authorized, Enabled = snapshot.Enabled, Connected = false,
                LastHeartbeatUtc = snapshot.LastHeartbeatUtc, Inventory = snapshot.Inventory
            };
            workers.Add(worker.NodeId, worker);
        }
        foreach (var snapshot in state.Jobs)
        {
            var job = new Job(snapshot.Request) { State = snapshot.State, NodeId = snapshot.NodeId, Generation = snapshot.Generation,
                LeaseExpiresAtUtc = null, Result = snapshot.Result, FailureCode = snapshot.FailureCode };
            if (snapshot.State is DistributedJobStateV1.Assigned or DistributedJobStateV1.Running)
                job.State = snapshot.Request.RetrySafe && !snapshot.Request.HasExternalSideEffects ? DistributedJobStateV1.Pending : DistributedJobStateV1.Uncertain;
            jobs.Add(snapshot.Request.JobId, job);
        }
        PersistLocked();
    }

    private void PersistLocked() => stateStore?.Save(new DistributedCoordinatorStateV1(
        workers.Values.Select(static worker => worker.Snapshot()).ToArray(), jobs.Values.Select(static job => job.Snapshot()).ToArray()));

    public IReadOnlyCollection<DistributedWorkerSnapshotV1> ListWorkers()
    {
        lock (gate) return workers.Values.Select(static worker => worker.Snapshot()).ToArray();
    }

    public IReadOnlyCollection<DistributedJobSnapshotV1> ListJobs()
    {
        lock (gate) return jobs.Values.Select(static job => job.Snapshot()).ToArray();
    }

    public void AuthorizeWorker(string nodeId, IReadOnlyCollection<DistributedCapabilityV1> allowedCapabilities, string certificateThumbprint)
    {
        ValidateId(nodeId, nameof(nodeId));
        ValidateThumbprint(certificateThumbprint);
        ArgumentNullException.ThrowIfNull(allowedCapabilities);
        if (allowedCapabilities.Any(static item => string.IsNullOrWhiteSpace(item.ContractId) || string.IsNullOrWhiteSpace(item.ContractVersion)))
            throw new ArgumentException("Allowed capabilities require contract IDs and versions.", nameof(allowedCapabilities));
        lock (gate)
        {
            if (workers.TryGetValue(nodeId, out var existing))
            {
                existing.Authorized = true;
                existing.AllowedCapabilities = allowedCapabilities.Distinct().ToArray();
                existing.CertificateThumbprint = NormalizeThumbprint(certificateThumbprint);
                existing.Enabled = true;
            }
            else workers.Add(nodeId, new Worker(nodeId, allowedCapabilities.Distinct().ToArray(), NormalizeThumbprint(certificateThumbprint)));
            PersistLocked();
        }
    }

    public void RotateWorkerCertificate(string nodeId, string certificateThumbprint)
    {
        ValidateThumbprint(certificateThumbprint);
        lock (gate)
        {
            if (!workers.TryGetValue(nodeId, out var worker) || !worker.Authorized) throw new KeyNotFoundException($"Authorized worker '{nodeId}' was not found.");
            worker.CertificateThumbprint = NormalizeThumbprint(certificateThumbprint);
            worker.Connected = false;
            PersistLocked();
        }
    }

    public bool AuthenticateWorker(string nodeId, string certificateThumbprint)
    {
        if (!TryNormalizeThumbprint(certificateThumbprint, out var normalized)) return false;
        lock (gate) return workers.TryGetValue(nodeId, out var worker) && worker.Authorized && worker.Enabled &&
            CryptographicOperations.FixedTimeEquals(Convert.FromHexString(worker.CertificateThumbprint), Convert.FromHexString(normalized));
    }

    public void SetWorkerEnabled(string nodeId, bool enabled)
    {
        lock (gate)
        {
            if (!workers.TryGetValue(nodeId, out var worker)) throw new KeyNotFoundException($"Worker '{nodeId}' is not registered.");
            worker.Enabled = enabled;
            PersistLocked();
        }
    }

    public void RevokeWorker(string nodeId)
    {
        lock (gate)
        {
            if (!workers.TryGetValue(nodeId, out var worker)) throw new KeyNotFoundException($"Worker '{nodeId}' is not registered.");
            worker.Authorized = false;
            worker.Enabled = false;
            worker.Connected = false;
            foreach (var job in jobs.Values.Where(job => job.NodeId == nodeId && job.State is DistributedJobStateV1.Assigned or DistributedJobStateV1.Running))
            {
                job.LeaseExpiresAtUtc = null;
                job.State = job.Request.RetrySafe && !job.Request.HasExternalSideEffects ? DistributedJobStateV1.Pending : DistributedJobStateV1.Uncertain;
            }
            PersistLocked();
        }
    }

    /// <summary>Registration is an inventory update, not an authorization mechanism.</summary>
    public bool RegisterWorker(string nodeId, string certificateThumbprint, DistributedWorkerInventoryV1 inventory)
    {
        ValidateId(nodeId, nameof(nodeId));
        ValidateInventory(inventory);
        lock (gate)
        {
            if (!workers.TryGetValue(nodeId, out var worker) || !worker.Authorized || !worker.Enabled ||
                !TryNormalizeThumbprint(certificateThumbprint, out var normalized) ||
                !CryptographicOperations.FixedTimeEquals(Convert.FromHexString(worker.CertificateThumbprint), Convert.FromHexString(normalized))) return false;
            if (inventory.Capabilities.Any(capability => !worker.AllowedCapabilities.Contains(capability))) return false;
            worker.Inventory = inventory;
            worker.Connected = true;
            worker.LastHeartbeatUtc = DateTimeOffset.UtcNow;
            worker.HeartbeatTimeout = TimeSpan.FromSeconds(30);
            PersistLocked();
            return true;
        }
    }

    public void Queue(DistributedJobRequestV1 request, bool allowRemote)
    {
        ValidateRequest(request);
        if (!allowRemote || request.HasExternalSideEffects) throw new InvalidOperationException("Remote execution is disabled or the operation declares external side effects.");
        lock (gate)
        {
            if (jobs.TryGetValue(request.JobId, out var existing))
            {
                if (!SameRequest(existing.Request, request)) throw new InvalidOperationException("A job ID cannot be reused for a different request.");
                return;
            }
            jobs.Add(request.JobId, new Job(request));
            PersistLocked();
        }
    }

    public bool DisconnectWorker(string nodeId, string certificateThumbprint)
    {
        lock (gate)
        {
            if (!workers.TryGetValue(nodeId, out var worker) || !worker.Authorized || !worker.Enabled ||
                !TryNormalizeThumbprint(certificateThumbprint, out var normalized) ||
                !CryptographicOperations.FixedTimeEquals(Convert.FromHexString(worker.CertificateThumbprint), Convert.FromHexString(normalized))) return false;
            worker.Connected = false;
            foreach (var job in jobs.Values.Where(job => job.NodeId == nodeId && job.State is DistributedJobStateV1.Assigned or DistributedJobStateV1.Running))
            {
                job.LeaseExpiresAtUtc = null;
                job.State = job.Request.RetrySafe && !job.Request.HasExternalSideEffects ? DistributedJobStateV1.Pending : DistributedJobStateV1.Uncertain;
            }
            PersistLocked();
            return true;
        }
    }

    public DistributedLeasedJobV1? LeaseNext(string nodeId, DateTimeOffset nowUtc, TimeSpan leaseDuration)
    {
        if (leaseDuration <= TimeSpan.Zero || leaseDuration > TimeSpan.FromHours(1)) throw new ArgumentOutOfRangeException(nameof(leaseDuration));
        lock (gate)
        {
            if (!workers.TryGetValue(nodeId, out var worker) || !worker.Authorized || !worker.Enabled || !worker.Connected || worker.Inventory is null ||
                worker.LastHeartbeatUtc < nowUtc - worker.HeartbeatTimeout || ActiveCount(nodeId) >= worker.Inventory.MaximumConcurrentJobs) return null;
            var job = jobs.Values.Where(candidate => candidate.State == DistributedJobStateV1.Pending &&
                    worker.Inventory.Capabilities.Contains(new DistributedCapabilityV1(candidate.Request.ContractId, candidate.Request.ContractVersion)))
                .OrderBy(static candidate => candidate.Request.JobId, StringComparer.Ordinal).FirstOrDefault();
            if (job is null) return null;
            job.Generation = checked(job.Generation + 1);
            job.NodeId = nodeId;
            job.State = DistributedJobStateV1.Assigned;
            job.LeaseExpiresAtUtc = nowUtc + leaseDuration;
            var lease = new DistributedJobLeaseV1(job.Request.JobId, nodeId, job.Generation, job.LeaseExpiresAtUtc.Value);
            PersistLocked();
            return new DistributedLeasedJobV1(lease, job.Request);
        }
    }

    public DistributedJobLeaseV1? Assign(DistributedJobRequestV1 request, DateTimeOffset nowUtc, TimeSpan leaseDuration, bool allowRemote, string? preferredNodeId = null)
    {
        ValidateRequest(request);
        if (leaseDuration <= TimeSpan.Zero || leaseDuration > TimeSpan.FromHours(1)) throw new ArgumentOutOfRangeException(nameof(leaseDuration));
        if (!allowRemote || request.HasExternalSideEffects) return null;
        lock (gate)
        {
            if (jobs.TryGetValue(request.JobId, out var prior))
            {
                if (prior.Request != request) throw new InvalidOperationException("A job ID cannot be reused for a different request.");
                if (prior.State is not DistributedJobStateV1.Pending) return null;
            }
            else
            {
                prior = new Job(request);
                jobs.Add(request.JobId, prior);
            }

            var candidates = workers.Values
                .Where(worker => worker.Authorized && worker.Enabled && worker.Connected && worker.Inventory is not null)
                .Where(worker => worker.LastHeartbeatUtc >= nowUtc - leaseDuration)
                .Where(worker => worker.Inventory!.Capabilities.Contains(new DistributedCapabilityV1(request.ContractId, request.ContractVersion)))
                .Where(worker => ActiveCount(worker.NodeId) < worker.Inventory!.MaximumConcurrentJobs)
                .OrderBy(worker => string.Equals(worker.NodeId, preferredNodeId, StringComparison.Ordinal) ? 0 : 1)
                .ThenBy(static worker => worker.NodeId, StringComparer.Ordinal)
                .FirstOrDefault();
            if (candidates is null) return null;

            prior.Generation = checked(prior.Generation + 1);
            prior.NodeId = candidates.NodeId;
            prior.State = DistributedJobStateV1.Assigned;
            prior.LeaseExpiresAtUtc = nowUtc + leaseDuration;
            PersistLocked();
            return new DistributedJobLeaseV1(request.JobId, candidates.NodeId, prior.Generation, prior.LeaseExpiresAtUtc.Value);
        }
    }

    public bool Heartbeat(string nodeId, IReadOnlyCollection<string> activeJobIds, DateTimeOffset nowUtc, TimeSpan leaseDuration)
    {
        ArgumentNullException.ThrowIfNull(activeJobIds);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(leaseDuration, TimeSpan.Zero);
        lock (gate)
        {
            if (!workers.TryGetValue(nodeId, out var worker) || !worker.Authorized || !worker.Enabled) return false;
            worker.Connected = true;
            worker.LastHeartbeatUtc = nowUtc;
            worker.HeartbeatTimeout = TimeSpan.FromTicks(checked(leaseDuration.Ticks * 2));
            foreach (var job in jobs.Values.Where(job => job.NodeId == nodeId && job.State is DistributedJobStateV1.Assigned or DistributedJobStateV1.Running))
            {
                if (activeJobIds.Contains(job.Request.JobId, StringComparer.Ordinal)) job.LeaseExpiresAtUtc = nowUtc + leaseDuration;
            }
            PersistLocked();
            return true;
        }
    }

    public bool Start(string nodeId, string jobId, long generation)
    {
        lock (gate)
        {
            if (!TryCurrentLease(nodeId, jobId, generation, out var job) || job.State != DistributedJobStateV1.Assigned) return false;
            job.State = DistributedJobStateV1.Running;
            PersistLocked();
            return true;
        }
    }

    public bool Complete(string nodeId, string jobId, long generation, JsonElement result)
    {
        lock (gate)
        {
            if (!TryCurrentLease(nodeId, jobId, generation, out var job) || job.State is not (DistributedJobStateV1.Assigned or DistributedJobStateV1.Running)) return false;
            job.State = DistributedJobStateV1.Completed;
            job.Result = result.Clone();
            job.LeaseExpiresAtUtc = null;
            PersistLocked();
            return true;
        }
    }

    public bool Fail(string nodeId, string jobId, long generation, string failureCode)
    {
        if (string.IsNullOrWhiteSpace(failureCode)) throw new ArgumentException("Failure code is required.", nameof(failureCode));
        lock (gate)
        {
            if (!TryCurrentLease(nodeId, jobId, generation, out var job) || job.State is not (DistributedJobStateV1.Assigned or DistributedJobStateV1.Running)) return false;
            job.State = DistributedJobStateV1.Failed;
            job.FailureCode = failureCode;
            job.LeaseExpiresAtUtc = null;
            PersistLocked();
            return true;
        }
    }

    public bool Cancel(string nodeId, string jobId, long generation)
    {
        lock (gate)
        {
            if (!TryCurrentLease(nodeId, jobId, generation, out var job) || job.State is not (DistributedJobStateV1.Assigned or DistributedJobStateV1.Running)) return false;
            job.State = DistributedJobStateV1.Cancelled;
            job.LeaseExpiresAtUtc = null;
            PersistLocked();
            return true;
        }
    }

    public bool CancelJob(string jobId)
    {
        lock (gate)
        {
            if (!jobs.TryGetValue(jobId, out var job) || job.State is not (DistributedJobStateV1.Pending or DistributedJobStateV1.Assigned or DistributedJobStateV1.Running)) return false;
            job.State = DistributedJobStateV1.Cancelled;
            job.LeaseExpiresAtUtc = null;
            PersistLocked();
            return true;
        }
    }

    public int ExpireLeases(DateTimeOffset nowUtc)
    {
        lock (gate)
        {
            var expired = 0;
            foreach (var job in jobs.Values.Where(job => job.LeaseExpiresAtUtc <= nowUtc && job.State is DistributedJobStateV1.Assigned or DistributedJobStateV1.Running))
            {
                job.LeaseExpiresAtUtc = null;
                job.State = job.Request.RetrySafe && !job.Request.HasExternalSideEffects
                    ? DistributedJobStateV1.Pending
                    : DistributedJobStateV1.Uncertain;
                expired++;
            }
            foreach (var worker in workers.Values)
            {
                if (worker.LastHeartbeatUtc is { } heartbeat && heartbeat < nowUtc - worker.HeartbeatTimeout) worker.Connected = false;
            }
            if (expired > 0) PersistLocked();
            return expired;
        }
    }

    private int ActiveCount(string nodeId) => jobs.Values.Count(job => job.NodeId == nodeId && job.State is DistributedJobStateV1.Assigned or DistributedJobStateV1.Running);
    private bool TryCurrentLease(string nodeId, string jobId, long generation, out Job job)
    {
        if (jobs.TryGetValue(jobId, out job!) && job.NodeId == nodeId && job.Generation == generation && job.LeaseExpiresAtUtc is { } expiresAt && expiresAt > DateTimeOffset.UtcNow) return true;
        job = null!;
        return false;
    }

    private static void ValidateId(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128) throw new ArgumentException("Identifier must contain 1 to 128 characters.", name);
    }

    private static bool SameRequest(DistributedJobRequestV1 left, DistributedJobRequestV1 right) =>
        left.JobId == right.JobId && left.ContractId == right.ContractId && left.ContractVersion == right.ContractVersion && left.Operation == right.Operation &&
        left.Payload.GetRawText() == right.Payload.GetRawText() && left.HasExternalSideEffects == right.HasExternalSideEffects &&
        left.RetrySafe == right.RetrySafe && left.CorrelationId == right.CorrelationId && left.TimeoutSeconds == right.TimeoutSeconds && left.CausationId == right.CausationId;

    private static string NormalizeThumbprint(string thumbprint) => thumbprint.Replace(":", string.Empty, StringComparison.Ordinal).Replace(" ", string.Empty, StringComparison.Ordinal).ToUpperInvariant();
    private static bool TryNormalizeThumbprint(string? thumbprint, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(thumbprint)) return false;
        var candidate = NormalizeThumbprint(thumbprint);
        if (candidate.Length != 64 || !candidate.All(Uri.IsHexDigit)) return false;
        normalized = candidate;
        return true;
    }
    private static void ValidateThumbprint(string thumbprint)
    {
        if (!TryNormalizeThumbprint(thumbprint, out _)) throw new ArgumentException("A SHA-256 certificate thumbprint (64 hexadecimal characters) is required.", nameof(thumbprint));
    }

    private static void ValidateRequest(DistributedJobRequestV1 request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateId(request.JobId, nameof(request.JobId));
        if (string.IsNullOrWhiteSpace(request.ContractId) || request.ContractId.Length > 256 || string.IsNullOrWhiteSpace(request.ContractVersion) || request.ContractVersion.Length > 64 || string.IsNullOrWhiteSpace(request.Operation) || request.Operation.Length > 128)
            throw new ArgumentException("A bounded contract ID and version are required.", nameof(request));
        if (request.TimeoutSeconds is <= 0 or > 86400) throw new ArgumentException("Distributed request timeout is outside the supported range.", nameof(request));
        if (JsonSerializer.SerializeToUtf8Bytes(request.Payload).Length > 1024 * 1024) throw new ArgumentException("Job payload exceeds the 1 MiB limit.", nameof(request));
    }

    private static void ValidateInventory(DistributedWorkerInventoryV1 inventory)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        if (string.IsNullOrWhiteSpace(inventory.OperatingSystem) || inventory.OperatingSystem.Length > 64 || string.IsNullOrWhiteSpace(inventory.Architecture) || inventory.Architecture.Length > 64 ||
            inventory.LogicalCpuCount is < 1 or > 4096 || inventory.AvailableMemoryBytes < 0 || inventory.MaximumConcurrentJobs is < 1 or > 1024 ||
            inventory.Capabilities.Count > 256 || inventory.Accelerators.Count > 64 || inventory.Capabilities.Any(static capability => string.IsNullOrWhiteSpace(capability.ContractId) || string.IsNullOrWhiteSpace(capability.ContractVersion)))
            throw new ArgumentException("Worker inventory is malformed or exceeds limits.", nameof(inventory));
    }

    private sealed class Worker(string nodeId, IReadOnlyList<DistributedCapabilityV1> allowedCapabilities, string certificateThumbprint)
    {
        public string NodeId { get; } = nodeId;
        public IReadOnlyList<DistributedCapabilityV1> AllowedCapabilities { get; set; } = allowedCapabilities;
        public string CertificateThumbprint { get; set; } = certificateThumbprint;
        public bool Authorized { get; set; } = true;
        public bool Enabled { get; set; } = true;
        public bool Connected { get; set; }
        public DateTimeOffset? LastHeartbeatUtc { get; set; }
        public TimeSpan HeartbeatTimeout { get; set; } = TimeSpan.FromSeconds(30);
        public DistributedWorkerInventoryV1? Inventory { get; set; }
        public DistributedWorkerSnapshotV1 Snapshot() => new(NodeId, Authorized, Enabled, Connected, LastHeartbeatUtc, Inventory, AllowedCapabilities, CertificateThumbprint);
    }

    private sealed class Job(DistributedJobRequestV1 request)
    {
        public DistributedJobRequestV1 Request { get; } = request;
        public DistributedJobStateV1 State { get; set; } = DistributedJobStateV1.Pending;
        public string? NodeId { get; set; }
        public long Generation { get; set; }
        public DateTimeOffset? LeaseExpiresAtUtc { get; set; }
        public JsonElement? Result { get; set; }
        public string? FailureCode { get; set; }
        public DistributedJobSnapshotV1 Snapshot() => new(Request, State, NodeId, Generation, LeaseExpiresAtUtc, Result, FailureCode);
    }
}

public sealed class DistributedWorkLeaseMonitor(IDistributedWorkCoordinatorV1 coordinator, TimeSpan? interval = null) : BackgroundService
{
    private readonly TimeSpan pollingInterval = interval ?? TimeSpan.FromSeconds(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(pollingInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            coordinator.ExpireLeases(DateTimeOffset.UtcNow);
    }
}
