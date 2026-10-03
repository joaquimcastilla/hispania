using System.Text.Json;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Hispania.ModuleSystem.Contracts;

/// <summary>A versioned capability that may be offered by a local or remote runtime.</summary>
public sealed record DistributedCapabilityV1(string ContractId, string ContractVersion);

/// <summary>Advisory worker inventory. The coordinator treats these values as scheduling hints.</summary>
public sealed record DistributedWorkerInventoryV1(
    string OperatingSystem,
    string Architecture,
    int LogicalCpuCount,
    long? AvailableMemoryBytes,
    int MaximumConcurrentJobs,
    IReadOnlyList<string> Accelerators,
    IReadOnlyList<DistributedCapabilityV1> Capabilities);

public enum DistributedJobStateV1 { Pending, Assigned, Running, Completed, Failed, Cancelled, Uncertain }

/// <summary>Generic operation envelope. Payload semantics belong to the named public capability contract.</summary>
public sealed record DistributedJobRequestV1(
    string JobId,
    string ContractId,
    string ContractVersion,
    string Operation,
    JsonElement Payload,
    bool HasExternalSideEffects = false,
    bool RetrySafe = false,
    string? CorrelationId = null,
    int? TimeoutSeconds = null);

public sealed record DistributedJobLeaseV1(
    string JobId,
    string NodeId,
    long Generation,
    DateTimeOffset ExpiresAtUtc);

public sealed record DistributedJobSnapshotV1(
    DistributedJobRequestV1 Request,
    DistributedJobStateV1 State,
    string? NodeId,
    long Generation,
    DateTimeOffset? LeaseExpiresAtUtc,
    JsonElement? Result,
    string? FailureCode);

public sealed record DistributedWorkerSnapshotV1(
    string NodeId,
    bool Authorized,
    bool Enabled,
    bool Connected,
    DateTimeOffset? LastHeartbeatUtc,
    DistributedWorkerInventoryV1? Inventory,
    IReadOnlyList<DistributedCapabilityV1> AllowedCapabilities,
    string? CertificateThumbprint);

public sealed record DistributedCoordinatorStateV1(IReadOnlyList<DistributedWorkerSnapshotV1> Workers, IReadOnlyList<DistributedJobSnapshotV1> Jobs);

/// <summary>Transactional single-coordinator state store. Implementations must serialize writes.</summary>
public interface IDistributedWorkStateStoreV1
{
    DistributedCoordinatorStateV1? Load();
    void Save(DistributedCoordinatorStateV1 state);
}

public sealed record DistributedLeasedJobV1(DistributedJobLeaseV1 Lease, DistributedJobRequestV1 Request);
public sealed record DistributedWorkerConnectRequestV1(int MinimumProtocolVersion, int MaximumProtocolVersion, DistributedWorkerInventoryV1 Inventory);
public sealed record DistributedWorkerConnectResponseV1(int SelectedProtocolVersion, IReadOnlyList<DistributedCapabilityV1> AuthorizedCapabilities);
public sealed record DistributedWorkerHeartbeatV1(IReadOnlyList<string> ActiveJobIds);
public sealed record DistributedWorkerHeartbeatResponseV1(IReadOnlyList<string> CancelledJobIds);
public sealed record DistributedWorkerStartV1(long Generation);
public sealed record DistributedWorkerCompletionV1(long Generation, JsonElement Result);
public sealed record DistributedWorkerFailureV1(long Generation, string FailureCode);

/// <summary>Short-lived access to exactly one job's source and result streams. Tokens are bearer secrets and must never be logged.</summary>
public sealed record DistributedMediaTransferGrantV1(string TransferId, string AccessToken, long SourceLength, long MaximumResultBytes, DateTimeOffset ExpiresAtUtc);

/// <summary>Host-side media transfer boundary; it contains no paths and is independent of any codec or filesystem.</summary>
public interface IDistributedMediaTransferBrokerV1
{
    DistributedMediaTransferGrantV1 RegisterSource(string jobId, long sourceLength, long maximumResultBytes,
        Func<CancellationToken, ValueTask<Stream>> openSource, TimeSpan lifetime);
    DistributedMediaTransferGrantV1? GetGrant(string jobId, string transferId);
    ValueTask<Stream?> OpenSourceBlockAsync(string jobId, string transferId, string accessToken, long offset, int length, CancellationToken cancellationToken = default);
    ValueTask<string?> ReceiveResultAsync(string jobId, long generation, string transferId, string accessToken,
        long declaredLength, string sha256, Stream content, Func<bool>? isLeaseCurrent = null,
        CancellationToken cancellationToken = default);
    ValueTask<Stream?> OpenPublishedResultAsync(string publishedHandle, CancellationToken cancellationToken = default);
    bool RemovePublishedResult(string publishedHandle);
    void Revoke(string jobId);
}

public interface IDistributedWorkCoordinatorV1
{
    IReadOnlyCollection<DistributedWorkerSnapshotV1> ListWorkers();
    IReadOnlyCollection<DistributedJobSnapshotV1> ListJobs();
    void AuthorizeWorker(string nodeId, IReadOnlyCollection<DistributedCapabilityV1> allowedCapabilities, string certificateThumbprint);
    void RotateWorkerCertificate(string nodeId, string certificateThumbprint);
    void RevokeWorker(string nodeId);
    void SetWorkerEnabled(string nodeId, bool enabled);
    bool AuthenticateWorker(string nodeId, string certificateThumbprint);
    bool RegisterWorker(string nodeId, string certificateThumbprint, DistributedWorkerInventoryV1 inventory);
    bool DisconnectWorker(string nodeId, string certificateThumbprint);
    void Queue(DistributedJobRequestV1 request, bool allowRemote);
    DistributedLeasedJobV1? LeaseNext(string nodeId, DateTimeOffset nowUtc, TimeSpan leaseDuration);
    DistributedJobLeaseV1? Assign(DistributedJobRequestV1 request, DateTimeOffset nowUtc, TimeSpan leaseDuration, bool allowRemote, string? preferredNodeId = null);
    bool Heartbeat(string nodeId, IReadOnlyCollection<string> activeJobIds, DateTimeOffset nowUtc, TimeSpan leaseDuration);
    bool Start(string nodeId, string jobId, long generation);
    bool Complete(string nodeId, string jobId, long generation, JsonElement result);
    bool Fail(string nodeId, string jobId, long generation, string failureCode);
    bool Cancel(string nodeId, string jobId, long generation);
    bool CancelJob(string jobId);
    int ExpireLeases(DateTimeOffset nowUtc);
}
