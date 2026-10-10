using Hispania.Platform.Contracts.Maintenance;
using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Hispania.Platform.Maintenance.Application;

public interface IMaintenanceRunStoreV1
{
    ValueTask SavePlanAsync(RetentionPlanV1 plan, CancellationToken cancellationToken);
    ValueTask<RetentionPlanV1?> GetPlanAsync(Guid planId, CancellationToken cancellationToken);
    ValueTask<MaintenanceRunV1> BeginAsync(RetentionPlanV1 plan, string? correlationId,
        DateTimeOffset nowUtc, DateTimeOffset leaseUntilUtc, CancellationToken cancellationToken);
    ValueTask<MaintenanceRunV1> CheckpointAsync(Guid runId, RetentionBatchResultV1 batch,
        DateTimeOffset leaseUntilUtc, CancellationToken cancellationToken);
    ValueTask<MaintenanceRunV1> FinishAsync(Guid runId, MaintenanceRunStateV1 state,
        string? errorCategory, DateTimeOffset nowUtc, CancellationToken cancellationToken);
    ValueTask<IReadOnlyList<MaintenanceRunV1>> ListAsync(int limit, CancellationToken cancellationToken);
}

/// <summary>Single-coordinator manual maintenance. The durable store fences concurrent runs by owner.</summary>
public sealed class MaintenanceCoordinatorV1
{
    private static readonly Meter Meter = new("Hispania.Maintenance", "1.0");
    private static readonly Counter<long> Plans = Meter.CreateCounter<long>("hispania.maintenance.plans");
    private static readonly Counter<long> Runs = Meter.CreateCounter<long>("hispania.maintenance.runs");
    private static readonly Counter<long> Batches = Meter.CreateCounter<long>("hispania.maintenance.batches");
    private static readonly Counter<long> Affected = Meter.CreateCounter<long>("hispania.maintenance.affected");
    private static readonly Histogram<double> Duration = Meter.CreateHistogram<double>("hispania.maintenance.duration", "ms");
    private readonly Dictionary<string, IRetentionOwnerV1> owners;
    private readonly Dictionary<string, SemaphoreSlim> ownerGates;
    private readonly IMaintenanceRunStoreV1 store;
    private readonly TimeProvider clock;

    public MaintenanceCoordinatorV1(IEnumerable<IRetentionOwnerV1> owners, IMaintenanceRunStoreV1 store,
        TimeProvider? clock = null)
    {
        this.owners = new(StringComparer.Ordinal);
        foreach (var owner in owners)
        {
            if (string.IsNullOrWhiteSpace(owner.OwnerId.Value) || owner.OwnerId.Value.Length > 64 ||
                !owner.OwnerId.Value.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-'))
                throw new ArgumentException("Maintenance owner ID is invalid.", nameof(owners));
            if (!this.owners.TryAdd(owner.OwnerId.Value, owner))
                throw new ArgumentException("Duplicate maintenance owner ID.", nameof(owners));
        }
        ownerGates = this.owners.Keys.ToDictionary(key => key, _ => new SemaphoreSlim(1, 1), StringComparer.Ordinal);
        this.store = store;
        this.clock = clock ?? TimeProvider.System;
    }

    public IReadOnlyList<(MaintenanceOwnerIdV1 Owner, RetentionCapabilitiesV1 Capabilities)> ListOwners()
        => owners.Values.Select(o => (Owner: o.OwnerId, o.Capabilities)).OrderBy(o => o.Owner.Value, StringComparer.Ordinal).ToArray();

    public async ValueTask<RetentionPlanV1> PlanAsync(string ownerId, RetentionPolicyV1 policy,
        CancellationToken cancellationToken = default)
    {
        var owner = Resolve(ownerId);
        policy.Validate(owner.Capabilities);
        var now = clock.GetUtcNow();
        var plan = await owner.PlanAsync(policy, now, now.AddMinutes(10), cancellationToken);
        if (plan.Owner != owner.OwnerId || plan.EvaluatedAtUtc != now || plan.ExpiresAtUtc <= now)
            throw new InvalidOperationException("Retention owner returned an invalid plan.");
        await store.SavePlanAsync(plan, cancellationToken);
        Plans.Add(1, new KeyValuePair<string, object?>("owner", owner.OwnerId.Value));
        return plan;
    }

    public async ValueTask<MaintenanceRunV1> ExecuteAsync(Guid planId, int maximumBatches,
        string? correlationId = null, CancellationToken cancellationToken = default)
    {
        if (maximumBatches is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(maximumBatches));
        if (correlationId is { Length: > 128 }) throw new ArgumentException("Correlation ID is too long.", nameof(correlationId));
        var plan = await store.GetPlanAsync(planId, cancellationToken) ?? throw new KeyNotFoundException("Unknown retention plan.");
        var owner = Resolve(plan.Owner.Value);
        var gate = ownerGates[plan.Owner.Value];
        var timer = Stopwatch.StartNew();
        if (!await gate.WaitAsync(0, cancellationToken))
            throw new InvalidOperationException("Another maintenance run is active for this owner.");
        try
        {
        if (!plan.Policy.Enabled || !owner.Capabilities.SupportsExecution)
            throw new InvalidOperationException("Retention is disabled for this plan.");
        var now = clock.GetUtcNow();
        if (now < plan.EvaluatedAtUtc || now > plan.ExpiresAtUtc)
            throw new InvalidOperationException("Retention plan expired or clock moved backwards.");
        var run = await store.BeginAsync(plan, correlationId, now, now.AddMinutes(2), cancellationToken);
        try
        {
            for (var index = 0; index < maximumBatches; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var batch = await owner.ExecuteBatchAsync(plan, cancellationToken);
                run = await store.CheckpointAsync(run.RunId, batch, clock.GetUtcNow().AddMinutes(2), cancellationToken);
                Batches.Add(1, new KeyValuePair<string, object?>("owner", plan.Owner.Value));
                Affected.Add(batch.Affected, new KeyValuePair<string, object?>("owner", plan.Owner.Value));
                if (!batch.HasMore || batch.Selected == 0) break;
            }
            var completed = await store.FinishAsync(run.RunId, MaintenanceRunStateV1.Completed, null,
                clock.GetUtcNow(), cancellationToken);
            Runs.Add(1, new KeyValuePair<string, object?>("owner", plan.Owner.Value), new KeyValuePair<string, object?>("outcome", "completed"));
            return completed;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Runs.Add(1, new KeyValuePair<string, object?>("owner", plan.Owner.Value), new KeyValuePair<string, object?>("outcome", "cancelled"));
            await store.FinishAsync(run.RunId, MaintenanceRunStateV1.Cancelled, null,
                clock.GetUtcNow(), CancellationToken.None);
            throw;
        }
        catch (Exception ex)
        {
            Runs.Add(1, new KeyValuePair<string, object?>("owner", plan.Owner.Value), new KeyValuePair<string, object?>("outcome", "failed"));
            await store.FinishAsync(run.RunId, MaintenanceRunStateV1.Failed, ex.GetType().Name,
                clock.GetUtcNow(), CancellationToken.None);
            throw;
        }
        }
        finally
        {
            Duration.Record(timer.Elapsed.TotalMilliseconds, new KeyValuePair<string, object?>("owner", plan.Owner.Value));
            gate.Release();
        }
    }

    public ValueTask<IReadOnlyList<MaintenanceRunV1>> ListRunsAsync(int limit = 50,
        CancellationToken cancellationToken = default)
        => store.ListAsync(Math.Clamp(limit, 1, 100), cancellationToken);

    private IRetentionOwnerV1 Resolve(string ownerId) => owners.TryGetValue(ownerId, out var owner)
        ? owner : throw new KeyNotFoundException("Unknown maintenance owner.");
}
