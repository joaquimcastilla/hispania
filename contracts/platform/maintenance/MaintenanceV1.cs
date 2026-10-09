namespace Hispania.Platform.Contracts.Maintenance;

/// <summary>Maintenance contracts are mechanisms. Each owner defines eligibility and protected state.</summary>
public readonly record struct MaintenanceOwnerIdV1(string Value);

public sealed record RetentionCapabilitiesV1(bool MinimumAge, bool MaximumRetainedCount, bool GracePeriod,
    int MaximumBatchSize, bool SupportsExecution);

public sealed record RetentionPolicyV1(bool Enabled = false, TimeSpan? MinimumAge = null,
    int? MaximumRetainedCount = null, TimeSpan? GracePeriod = null, int BatchSize = 100)
{
    public void Validate(RetentionCapabilitiesV1 capabilities)
    {
        if (BatchSize < 1 || BatchSize > capabilities.MaximumBatchSize || capabilities.MaximumBatchSize < 1)
            throw new InvalidOperationException("Retention batch size is outside the owner's bounds.");
        if (MinimumAge is { } age && (age < TimeSpan.Zero || age > TimeSpan.FromDays(36500) || !capabilities.MinimumAge))
            throw new InvalidOperationException("Retention minimum age is invalid or unsupported.");
        if (MaximumRetainedCount is { } count && (count < 0 || !capabilities.MaximumRetainedCount))
            throw new InvalidOperationException("Retention count is invalid or unsupported.");
        if (GracePeriod is { } grace && (grace < TimeSpan.Zero || grace > TimeSpan.FromDays(36500) || !capabilities.GracePeriod))
            throw new InvalidOperationException("Retention grace period is invalid or unsupported.");
        if (Enabled && !capabilities.SupportsExecution)
            throw new InvalidOperationException("This owner does not support destructive retention.");
    }
}

public sealed record RetentionPlanV1(Guid PlanId, MaintenanceOwnerIdV1 Owner, RetentionPolicyV1 Policy,
    DateTimeOffset EvaluatedAtUtc, DateTimeOffset ExpiresAtUtc, long CandidateCount,
    long ProtectedCount, long? EstimatedBytes, DateTimeOffset? OldestCandidateUtc,
    DateTimeOffset? NewestCandidateUtc, string Reason, IReadOnlyList<string> Warnings);

public sealed record RetentionBatchResultV1(int Selected, int Affected, long? EstimatedBytesFreed,
    bool HasMore, int RevalidatedOut);

/// <summary>Plan and execution use one owner-owned eligibility predicate; execution revalidates under its write lock.</summary>
public interface IRetentionOwnerV1
{
    MaintenanceOwnerIdV1 OwnerId { get; }
    RetentionCapabilitiesV1 Capabilities { get; }
    ValueTask<RetentionPlanV1> PlanAsync(RetentionPolicyV1 policy, DateTimeOffset evaluatedAtUtc,
        DateTimeOffset expiresAtUtc, CancellationToken cancellationToken = default);
    ValueTask<RetentionBatchResultV1> ExecuteBatchAsync(RetentionPlanV1 plan,
        CancellationToken cancellationToken = default);
}

public enum ReconciliationFindingKindV1 { MissingContent, OrphanedContent, Mismatch, Unknown }
public enum ReconciliationSeverityV1 { Information, Warning, Critical }
public enum ReconciliationAutoFixV1 { Informational, Manual, SafeAutoFix }
public sealed record ReconciliationFindingV1(string FindingId, MaintenanceOwnerIdV1 Owner,
    ReconciliationFindingKindV1 Kind, ReconciliationSeverityV1 Severity, DateTimeOffset ObservedAtUtc,
    string Reason, bool Quarantined, string SubjectId,
    DateTimeOffset? FirstObservedUtc = null, DateTimeOffset? LastObservedUtc = null,
    string? EvidenceSummary = null, string? SuggestedAction = null,
    ReconciliationAutoFixV1 AutoFix = ReconciliationAutoFixV1.Manual);
public sealed record ReconciliationScanV1(MaintenanceOwnerIdV1 Owner, DateTimeOffset StartedAtUtc,
    DateTimeOffset CompletedAtUtc, bool Complete, long Examined, IReadOnlyList<ReconciliationFindingV1> Findings,
    string? ContinuationToken);
public sealed record ReconciliationQuarantineEntryV1(string FindingId, string ContentHandle,
    string Reason, DateTimeOffset FirstObservedUtc);
public interface IReconciliationOwnerV1
{
    MaintenanceOwnerIdV1 OwnerId { get; }
    ValueTask<ReconciliationScanV1> ScanAsync(int maximumItems, string? continuationToken,
        CancellationToken cancellationToken = default);
}

public enum CapacityPressureLevelV1 { Unknown, Normal, Warning, Critical }
public sealed record CapacitySnapshotV1(MaintenanceOwnerIdV1 Source, DateTimeOffset ObservedAtUtc,
    long? TotalBytes, long? FreeBytes, long? OwnedBytes, CapacityPressureLevelV1 Pressure,
    bool Complete, string? Diagnostic)
{
    public long? UsedBytes => TotalBytes is { } total && FreeBytes is { } free && total >= 0 && free >= 0 && free <= total
        ? total - free : null;
}
public sealed record CapacityThresholdsV1(long ReserveBytes, double WarningFreeFraction,
    double CriticalFreeFraction)
{
    public void Validate()
    {
        if (ReserveBytes < 0 || !double.IsFinite(WarningFreeFraction) || !double.IsFinite(CriticalFreeFraction)
            || WarningFreeFraction <= 0 || WarningFreeFraction >= 1 || CriticalFreeFraction <= 0
            || CriticalFreeFraction >= WarningFreeFraction)
            throw new InvalidOperationException("Capacity thresholds are inconsistent or outside supported bounds.");
    }
}
public interface ICapacitySourceV1
{
    MaintenanceOwnerIdV1 SourceId { get; }
    ValueTask<CapacitySnapshotV1> ObserveAsync(CancellationToken cancellationToken = default);
}

public enum MaintenanceRunStateV1 { Planned, Running, Completed, Failed, Cancelled, Interrupted }
public enum MaintenanceOperationTypeV1 { Retention, Reconciliation }
public sealed record MaintenanceRunV1(Guid RunId, MaintenanceOwnerIdV1 Owner,
    MaintenanceOperationTypeV1 Operation, MaintenanceRunStateV1 State,
    DateTimeOffset StartedAtUtc, DateTimeOffset? CompletedAtUtc, int Batches,
    long Candidates, long Affected, long? EstimatedBytesFreed, string? ErrorCategory,
    string? CorrelationId);
