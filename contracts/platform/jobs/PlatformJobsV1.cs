using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Hispania.Platform.Contracts.Jobs;

public enum PlatformJobStatusV1 { Pending, Running, Succeeded, Failed, Cancelled, DeadLettered }
public enum PlatformJobAttemptOutcomeV1 { Succeeded, Failed, Cancelled, TimedOut, Recovered, Running }
public enum PlatformJobExecutionPolicyV1 { LocalOnly, RemotePreferred, RemoteOnly }

public sealed record PlatformJobV1(Guid JobId, string JobType, PlatformJobStatusV1 Status, DateTimeOffset CreatedAt,
    DateTimeOffset ScheduledAt, DateTimeOffset? StartedAt, DateTimeOffset? CompletedAt, int AttemptCount,
    int MaxAttempts, int? TimeoutSeconds, string? CorrelationId, string? CausationId, string? IdempotencyKey,
    string PayloadJson, string? ResultMetadataJson, string? ErrorMetadata, string Executor,
    DateTimeOffset? LeaseExpiresAt, PlatformJobExecutionPolicyV1 ExecutionPolicy = PlatformJobExecutionPolicyV1.LocalOnly,
    string? WorkerId = null, string? Capability = null, long? Generation = null, string? LeaseId = null, string? TraceParent = null, string? TraceState = null);
public sealed record PlatformJobListItemV1(Guid JobId, string JobType, PlatformJobStatusV1 Status,
    DateTimeOffset CreatedAt, DateTimeOffset ScheduledAt, DateTimeOffset? StartedAt, DateTimeOffset? CompletedAt,
    int AttemptCount, int MaxAttempts, string Executor, string? ErrorSummary,
    PlatformJobExecutionPolicyV1 ExecutionPolicy = PlatformJobExecutionPolicyV1.LocalOnly, string? WorkerId = null,
    string? Capability = null, long? Generation = null);
public sealed record PlatformJobAttemptV1(Guid AttemptId, Guid JobId, int AttemptNumber, DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt, PlatformJobAttemptOutcomeV1 Outcome, string? ErrorSummary, string Executor,
    string ExecutorKind = "local", string? WorkerId = null, string? Capability = null, long? Generation = null, string? LeaseId = null);
public sealed record PlatformJobResultMetadataV1(string? ContentType = null, string? OpaqueResultRef = null,
    IReadOnlyDictionary<string, string>? Metadata = null);
public sealed record PlatformJobOptionsV1(int MaximumPayloadBytes = 262144, int MaximumResultMetadataBytes = 8192,
    int MaximumAttempts = 5, TimeSpan? RetryDelay = null, TimeSpan? ExecutionLease = null,
    TimeSpan? PollInterval = null)
{
    public TimeSpan EffectiveRetryDelay => RetryDelay ?? TimeSpan.FromSeconds(2);
    public TimeSpan EffectiveExecutionLease => ExecutionLease ?? TimeSpan.FromMinutes(2);
    public TimeSpan EffectivePollInterval => PollInterval ?? TimeSpan.FromMilliseconds(500);
    public void Validate()
    {
        if (MaximumPayloadBytes is < 1 or > 16 * 1024 * 1024 || MaximumResultMetadataBytes is < 1 or > 64 * 1024 || MaximumAttempts is < 1 or > 100)
            throw new ArgumentOutOfRangeException(nameof(PlatformJobOptionsV1), "Job payload/result limits and attempt count are outside supported bounds.");
        if (RetryDelay is { } retry && retry < TimeSpan.Zero || ExecutionLease is { } lease && lease <= TimeSpan.Zero || PollInterval is { } poll && poll <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(PlatformJobOptionsV1), "Retry delay must be nonnegative and lease/poll intervals must be positive.");
    }
}
public sealed record CreatePlatformJobV1(string JobType, string PayloadJson, DateTimeOffset? ScheduledAt = null,
    int? MaxAttempts = null, TimeSpan? Timeout = null, string? CorrelationId = null, string? CausationId = null,
    string? IdempotencyKey = null, string? RequestedBy = null, PlatformJobExecutionPolicyV1 ExecutionPolicy = PlatformJobExecutionPolicyV1.LocalOnly);
public sealed record PlatformRemoteJobExecutionV1(PlatformJobResultMetadataV1? Result, string WorkerId,
    string Capability, long Generation, string LeaseId);
/// <summary>When HistoryPruned is true, Job is a minimal tombstone view; Get/attempt history is unavailable.</summary>
public sealed record PlatformJobCreateResultV1(PlatformJobV1 Job, bool Duplicate, bool HistoryPruned = false);
public sealed record PlatformJobHealthV1(bool StoreAccessible, bool DispatcherActive, long Pending, long Running,
    long Succeeded, long Failed, long Cancelled, long DeadLettered, DateTimeOffset? OldestRemoteOnlyPendingAt = null);

/// <summary>Durable job APIs. Jobs are commands/work to perform; Events are facts that occurred.</summary>
public interface IPlatformJobServiceV1
{
    ValueTask<PlatformJobCreateResultV1> CreateAsync(CreatePlatformJobV1 request, CancellationToken cancellationToken = default);
    ValueTask<PlatformJobV1?> GetAsync(Guid jobId, CancellationToken cancellationToken = default);
    ValueTask<IReadOnlyList<PlatformJobListItemV1>> ListAsync(PlatformJobStatusV1? status = null, int limit = 100, CancellationToken cancellationToken = default);
    ValueTask<IReadOnlyList<PlatformJobAttemptV1>> GetAttemptsAsync(Guid jobId, CancellationToken cancellationToken = default);
    ValueTask<bool> CancelAsync(Guid jobId, string? requestedBy = null, CancellationToken cancellationToken = default);
    ValueTask<bool> RetryAsync(Guid jobId, string? requestedBy = null, CancellationToken cancellationToken = default);
    ValueTask<PlatformJobHealthV1> GetHealthAsync(CancellationToken cancellationToken = default);
}

public interface IPlatformJobHandlerV1<TPayload> where TPayload : notnull
{
    string JobType { get; }
    ValueTask<PlatformJobResultMetadataV1?> ExecuteAsync(TPayload payload, CancellationToken cancellationToken);
}

public interface IPlatformJobHandlerRegistryV1
{
    bool Contains(string jobType);
    ValueTask<PlatformJobResultMetadataV1?> ExecuteAsync(string jobType, string payloadJson, CancellationToken cancellationToken);
}

/// <summary>Public bridge to an explicitly allowlisted remote capability; the implementation belongs to the host adapter.</summary>
public interface IPlatformRemoteJobExecutorV1
{
    string? GetCapability(string jobType);
    bool IsAvailable(string jobType);
    ValueTask<PlatformRemoteJobExecutionV1?> ExecuteAsync(PlatformJobV1 job, int attemptNumber, CancellationToken cancellationToken);
    ValueTask<PlatformRemoteJobExecutionV1?> ExecuteAsync(PlatformJobV1 job, int attemptNumber,
        Func<PlatformRemoteJobExecutionV1, CancellationToken, ValueTask> onStarted, CancellationToken cancellationToken) =>
        ExecuteAsync(job, attemptNumber, cancellationToken);
    bool Cancel(Guid jobId);
}

/// <summary>Explicit allowlisted registry; durable data never chooses a CLR type.</summary>
public sealed class PlatformJobHandlerRegistryV1 : IPlatformJobHandlerRegistryV1
{
    private readonly Dictionary<string, (Type PayloadType, Func<string, CancellationToken, ValueTask<PlatformJobResultMetadataV1?>> Execute)> handlers = new(StringComparer.Ordinal);
    public PlatformJobHandlerRegistryV1 Register<TPayload>(IPlatformJobHandlerV1<TPayload> handler) where TPayload : notnull
    {
        ArgumentNullException.ThrowIfNull(handler);
        if (!IsValidJobType(handler.JobType) || !handlers.TryAdd(handler.JobType, (typeof(TPayload), ExecuteTyped)))
            throw new InvalidOperationException("Job type is invalid or already registered.");
        return this;

        async ValueTask<PlatformJobResultMetadataV1?> ExecuteTyped(string json, CancellationToken token)
        {
            var payload = JsonSerializer.Deserialize<TPayload>(json, JsonSerializerOptions.Web) ?? throw new InvalidDataException("Job payload is invalid.");
            return await handler.ExecuteAsync(payload, token).ConfigureAwait(false);
        }
    }
    public bool Contains(string jobType) => handlers.ContainsKey(jobType);
    public ValueTask<PlatformJobResultMetadataV1?> ExecuteAsync(string jobType, string payloadJson, CancellationToken cancellationToken) =>
        handlers.TryGetValue(jobType, out var handler) ? handler.Execute(payloadJson, cancellationToken) : throw new InvalidOperationException("No trusted handler is registered for this JobType.");
    public static bool IsValidJobType(string? value) => value is { Length: >= 3 and <= 128 } && value.Split('.').Length is >= 2 and <= 8 && value.Split('.').All(part => part.Length is >= 1 and <= 32 && part.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'));
}

public sealed record PlatformTestHashPayloadV1(string Text, string Algorithm);
public sealed class PlatformTestHashHandlerV1 : IPlatformJobHandlerV1<PlatformTestHashPayloadV1>
{
    public const string TypeId = "platform.test.hash";
    public string JobType => TypeId;
    public ValueTask<PlatformJobResultMetadataV1?> ExecuteAsync(PlatformTestHashPayloadV1 payload, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(payload.Text)) throw new InvalidDataException("Text is required.");
        var bytes = Encoding.UTF8.GetBytes(payload.Text);
        var hash = payload.Algorithm switch
        {
            "sha256" => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
            "sha512" => Convert.ToHexString(SHA512.HashData(bytes)).ToLowerInvariant(),
            _ => throw new InvalidDataException("Unsupported hash algorithm.")
        };
        return ValueTask.FromResult<PlatformJobResultMetadataV1?>(new("text/plain", null, new Dictionary<string, string> { ["hash"] = hash }));
    }
}

/// <summary>Bounded cooperative delay used only to exercise remote cancellation and timeout paths.</summary>
public sealed record PlatformTestDelayPayloadV1(int DurationSeconds);
public sealed class PlatformTestDelayHandlerV1 : IPlatformJobHandlerV1<PlatformTestDelayPayloadV1>
{
    public const string TypeId = "platform.test.delay";
    public string JobType => TypeId;
    public async ValueTask<PlatformJobResultMetadataV1?> ExecuteAsync(PlatformTestDelayPayloadV1 payload, CancellationToken cancellationToken)
    {
        if (payload.DurationSeconds is < 1 or > 30) throw new InvalidDataException("Test delay must be between 1 and 30 seconds.");
        await Task.Delay(TimeSpan.FromSeconds(payload.DurationSeconds), cancellationToken).ConfigureAwait(false);
        return new("text/plain", null, new Dictionary<string, string> { ["completed"] = "true" });
    }
}
