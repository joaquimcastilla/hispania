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

/// <summary>Version 1 module boundary for a declared Platform Job contribution. Payloads remain opaque JSON at the IPC boundary.</summary>
public interface IPlatformJobContributionV1
{
    Task<PlatformJobResultMetadataV1?> ExecuteAsync(PlatformJobContributionRequestV1 request, CancellationToken cancellationToken);
}

public sealed record PlatformJobContributionRequestV1(string JobType, string PayloadJson);
public sealed class PlatformJobHandlerUnavailableException(string message) : Exception(message);

/// <summary>Lifecycle-aware extension to the existing allowlisted local handler registry.</summary>
public interface IPlatformJobContributionRegistryV1
{
    void ValidateModuleBatch(string moduleId, long generation, IReadOnlyCollection<string> jobTypes);
    void RegisterModuleBatch(string moduleId, long generation, IReadOnlyCollection<string> jobTypes,
        Func<string, string, CancellationToken, ValueTask<PlatformJobResultMetadataV1?>> execute,
        Func<bool> isAuthorized);
    void RemoveModuleBatch(string moduleId, long generation);
    void BeginModuleDrain(string moduleId, long generation);
    void ResumeModuleBatch(string moduleId, long generation);
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
public sealed class PlatformJobHandlerRegistryV1 : IPlatformJobHandlerRegistryV1, IPlatformJobContributionRegistryV1
{
    private readonly Dictionary<string, (Type PayloadType, Func<string, CancellationToken, ValueTask<PlatformJobResultMetadataV1?>> Execute)> handlers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ModuleContribution> contributions = new(StringComparer.Ordinal);
    private readonly object gate = new();
    public PlatformJobHandlerRegistryV1 Register<TPayload>(IPlatformJobHandlerV1<TPayload> handler) where TPayload : notnull
    {
        ArgumentNullException.ThrowIfNull(handler);
        lock (gate)
            if (!IsValidJobType(handler.JobType) || contributions.ContainsKey(handler.JobType) || !handlers.TryAdd(handler.JobType, (typeof(TPayload), ExecuteTyped)))
                throw new InvalidOperationException("Job type is invalid or already registered.");
        return this;

        async ValueTask<PlatformJobResultMetadataV1?> ExecuteTyped(string json, CancellationToken token)
        {
            var payload = JsonSerializer.Deserialize<TPayload>(json, JsonSerializerOptions.Web) ?? throw new InvalidDataException("Job payload is invalid.");
            return await handler.ExecuteAsync(payload, token).ConfigureAwait(false);
        }
    }
    public bool Contains(string jobType) { lock (gate) return handlers.ContainsKey(jobType) || contributions.ContainsKey(jobType); }
    public ValueTask<PlatformJobResultMetadataV1?> ExecuteAsync(string jobType, string payloadJson, CancellationToken cancellationToken)
    {
        Func<string, CancellationToken, ValueTask<PlatformJobResultMetadataV1?>>? localExecute = null;
        ModuleContribution? contribution = null;
        lock (gate)
        {
            if (handlers.TryGetValue(jobType, out var handler)) localExecute = handler.Execute;
            else if (contributions.TryGetValue(jobType, out contribution)) { }
            else throw new PlatformJobHandlerUnavailableException("No authorized handler is currently registered for this JobType.");
        }
        if (localExecute is not null) return localExecute(payloadJson, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return ExecuteContributionAsync(contribution!, jobType, payloadJson, cancellationToken);
    }

    public void ValidateModuleBatch(string moduleId, long generation, IReadOnlyCollection<string> jobTypes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(moduleId);
        ArgumentNullException.ThrowIfNull(jobTypes);
        if (generation < 1 || jobTypes.Count == 0 || jobTypes.Any(type => !IsValidJobType(type)) || jobTypes.Distinct(StringComparer.Ordinal).Count() != jobTypes.Count)
            throw new InvalidDataException("Module Job contribution batch is invalid.");
        lock (gate)
            foreach (var type in jobTypes)
                if (handlers.ContainsKey(type) || contributions.TryGetValue(type, out var existing) && existing.ModuleId != moduleId)
                    throw new InvalidOperationException("Job type is already registered by another handler or module generation.");
    }

    public void RegisterModuleBatch(string moduleId, long generation, IReadOnlyCollection<string> jobTypes,
        Func<string, string, CancellationToken, ValueTask<PlatformJobResultMetadataV1?>> execute, Func<bool> isAuthorized)
    {
        ArgumentNullException.ThrowIfNull(execute); ArgumentNullException.ThrowIfNull(isAuthorized);
        ValidateModuleBatch(moduleId, generation, jobTypes);
        lock (gate)
        {
            if (jobTypes.All(type => contributions.TryGetValue(type, out var current) && current.ModuleId == moduleId && current.Generation == generation)) return;
            foreach (var type in jobTypes)
                if (handlers.ContainsKey(type) || contributions.TryGetValue(type, out var current) &&
                    (current.ModuleId != moduleId || current.Generation != generation))
                    throw new InvalidOperationException("Job type is already registered by another handler or module generation.");
            var contribution = new ModuleContribution(moduleId, generation, execute, isAuthorized);
            foreach (var type in jobTypes) contributions.Add(type, contribution);
        }
    }

    public void RemoveModuleBatch(string moduleId, long generation)
    {
        lock (gate)
        {
            var matched = contributions.Where(pair => pair.Value.ModuleId == moduleId && pair.Value.Generation == generation).ToArray();
            foreach (var type in matched.Select(pair => pair.Key)) contributions.Remove(type);
            foreach (var contribution in matched.Select(pair => pair.Value).Distinct()) contribution.Dispose();
        }
    }

    public void BeginModuleDrain(string moduleId, long generation)
    {
        ModuleContribution[] matched;
        lock (gate) matched = contributions.Values.Where(item => item.ModuleId == moduleId && item.Generation == generation).Distinct().ToArray();
        foreach (var item in matched) item.Stop();
    }

    public void ResumeModuleBatch(string moduleId, long generation)
    { lock (gate) foreach (var item in contributions.Values.Where(item => item.ModuleId == moduleId && item.Generation == generation).Distinct()) item.Resume(); }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static async ValueTask<PlatformJobResultMetadataV1?> ExecuteContributionAsync(ModuleContribution contribution, string jobType, string payloadJson, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, contribution.Token);
        linked.Token.ThrowIfCancellationRequested();
        if (!contribution.IsAuthorized()) throw new PlatformJobHandlerUnavailableException("The module Job contribution is no longer authorized or active.");
        return await contribution.Execute(jobType, payloadJson, linked.Token).ConfigureAwait(false);
    }

    private sealed class ModuleContribution(string moduleId, long generation,
        Func<string, string, CancellationToken, ValueTask<PlatformJobResultMetadataV1?>> execute, Func<bool> isAuthorized) : IDisposable
    {
        private CancellationTokenSource stopped = new();
        public string ModuleId { get; } = moduleId;
        public long Generation { get; } = generation;
        private Func<string, string, CancellationToken, ValueTask<PlatformJobResultMetadataV1?>>? executeHandler = execute;
        private Func<bool>? authorizationCheck = isAuthorized;
        public Func<string, string, CancellationToken, ValueTask<PlatformJobResultMetadataV1?>> Execute => executeHandler ?? throw new ObjectDisposedException(nameof(ModuleContribution));
        public Func<bool> IsAuthorized => authorizationCheck ?? (static () => false);
        public CancellationToken Token { get { lock (this) return stopped.Token; } }
        public void Stop() { CancellationTokenSource current; lock (this) current = stopped; if (!current.IsCancellationRequested) current.Cancel(); }
        public void Resume() { lock (this) if (stopped.IsCancellationRequested) { stopped.Dispose(); stopped = new(); } }
        public void Dispose()
        {
            CancellationTokenSource current;
            lock (this) { current = stopped; executeHandler = null; authorizationCheck = null; }
            if (!current.IsCancellationRequested) current.Cancel();
            current.Dispose();
        }
    }
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
