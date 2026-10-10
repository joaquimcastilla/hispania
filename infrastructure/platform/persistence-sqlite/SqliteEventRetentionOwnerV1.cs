using System.Globalization;
using Hispania.Platform.Contracts.Maintenance;
using Microsoft.Data.Sqlite;

namespace Hispania.Platform.Persistence.Sqlite;

/// <summary>Events owns completed delivery history. Pending, processing and dead-letter rows remain protected.</summary>
public sealed class SqliteEventRetentionOwnerV1 : IRetentionOwnerV1
{
    private readonly string connectionString;
    private readonly TimeProvider clock;
    public SqliteEventRetentionOwnerV1(string path, TimeProvider? clock = null)
    {
        connectionString = new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWrite,
            Pooling = false }.ToString();
        this.clock = clock ?? TimeProvider.System;
    }
    public MaintenanceOwnerIdV1 OwnerId => new("platform.events");
    public RetentionCapabilitiesV1 Capabilities => new(true, false, false, 500, true);

    public async ValueTask<RetentionPlanV1> PlanAsync(RetentionPolicyV1 policy, DateTimeOffset evaluatedAtUtc,
        DateTimeOffset expiresAtUtc, CancellationToken cancellationToken = default)
    {
        Validate(policy, evaluatedAtUtc, expiresAtUtc);
        if (evaluatedAtUtc > clock.GetUtcNow()) throw new InvalidOperationException("Retention evaluation cannot use a future clock.");
        if (!policy.Enabled) return new(Guid.NewGuid(), OwnerId, policy, evaluatedAtUtc, expiresAtUtc,
            0, 0, null, null, null, "Event retention is disabled.", []);
        await using var c = await OpenAsync(cancellationToken); await using var q = c.CreateCommand();
        q.CommandText = $"SELECT COUNT(*),MIN(e.OccurredAt),MAX(e.OccurredAt) FROM Events e WHERE {EligibleSql}";
        Bind(q, policy, evaluatedAtUtc);
        await using var r = await q.ExecuteReaderAsync(cancellationToken); await r.ReadAsync(cancellationToken);
        var candidates = r.GetInt64(0);
        DateTimeOffset? oldest = r.IsDBNull(1) ? null : Parse(r.GetString(1));
        DateTimeOffset? newest = r.IsDBNull(2) ? null : Parse(r.GetString(2));
        await r.DisposeAsync();
        await using var totalQuery = c.CreateCommand(); totalQuery.CommandText = "SELECT COUNT(*) FROM Events";
        var total = Convert.ToInt64(await totalQuery.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
        return new(Guid.NewGuid(), OwnerId, policy, evaluatedAtUtc, expiresAtUtc, candidates,
            total - candidates, null, oldest, newest,
            "Events with only delivered, aged deliveries; pending, processing and dead-lettered events are protected.",
            ["Delivery attempts are removed with eligible events; dead letters remain available for inspection and replay."]);
    }

    public async ValueTask<RetentionBatchResultV1> ExecuteBatchAsync(RetentionPlanV1 plan,
        CancellationToken cancellationToken = default)
    {
        if (plan.Owner != OwnerId || !plan.Policy.Enabled) throw new InvalidOperationException("Event retention plan is not executable.");
        Validate(plan.Policy, plan.EvaluatedAtUtc, plan.ExpiresAtUtc);
        var now = clock.GetUtcNow();
        if (now < plan.EvaluatedAtUtc || now > plan.ExpiresAtUtc)
            throw new InvalidOperationException("Event retention plan is stale or the clock moved backwards.");
        await using var c = await OpenAsync(cancellationToken); await using var tx = c.BeginTransaction(deferred: false);
        var ids = new List<string>();
        await using (var q = c.CreateCommand())
        {
            q.Transaction = tx;
            q.CommandText = $"SELECT e.EventId FROM Events e WHERE {EligibleSql} ORDER BY e.OccurredAt,e.EventId LIMIT $limit";
            Bind(q, plan.Policy, plan.EvaluatedAtUtc); q.Parameters.AddWithValue("$limit", plan.Policy.BatchSize);
            await using var r = await q.ExecuteReaderAsync(cancellationToken);
            while (await r.ReadAsync(cancellationToken)) ids.Add(r.GetString(0));
        }
        var affected = 0;
        foreach (var id in ids)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // The event predicate is checked again under the write lock before every mutation.
            await using var attempts = c.CreateCommand(); attempts.Transaction = tx;
            attempts.CommandText = $"DELETE FROM DeliveryAttempts WHERE DeliveryId IN (SELECT d.DeliveryId FROM Deliveries d JOIN Events e ON e.EventId=d.EventId WHERE e.EventId=$id AND {EligibleSql})";
            attempts.Parameters.AddWithValue("$id", id); Bind(attempts, plan.Policy, plan.EvaluatedAtUtc);
            await attempts.ExecuteNonQueryAsync(cancellationToken);
            await using var deliveries = c.CreateCommand(); deliveries.Transaction = tx;
            deliveries.CommandText = $"DELETE FROM Deliveries WHERE EventId IN (SELECT e.EventId FROM Events e WHERE e.EventId=$id AND {EligibleSql})";
            deliveries.Parameters.AddWithValue("$id", id); Bind(deliveries, plan.Policy, plan.EvaluatedAtUtc);
            await deliveries.ExecuteNonQueryAsync(cancellationToken);
            await using var ev = c.CreateCommand(); ev.Transaction = tx;
            ev.CommandText = "DELETE FROM Events WHERE EventId=$id AND OccurredAt<=$cutoff AND NOT EXISTS(SELECT 1 FROM Deliveries d WHERE d.EventId=$id)";
            ev.Parameters.AddWithValue("$id", id); ev.Parameters.AddWithValue("$cutoff", Cutoff(plan.Policy, plan.EvaluatedAtUtc));
            affected += await ev.ExecuteNonQueryAsync(cancellationToken);
        }
        await tx.CommitAsync(cancellationToken);
        return new(ids.Count, affected, null, ids.Count == plan.Policy.BatchSize, ids.Count - affected);
    }

    private const string EligibleSql = "e.OccurredAt<=$cutoff AND EXISTS(SELECT 1 FROM Deliveries d WHERE d.EventId=e.EventId) AND NOT EXISTS(SELECT 1 FROM Deliveries d WHERE d.EventId=e.EventId AND (d.Status<>2 OR d.DeliveredAt IS NULL OR d.DeliveredAt>$cutoff))";
    private static void Bind(SqliteCommand q, RetentionPolicyV1 policy, DateTimeOffset evaluatedAtUtc)
        => q.Parameters.AddWithValue("$cutoff", Cutoff(policy, evaluatedAtUtc));
    private static string Cutoff(RetentionPolicyV1 policy, DateTimeOffset evaluatedAtUtc)
        => Dt(evaluatedAtUtc - policy.MinimumAge!.Value);
    private void Validate(RetentionPolicyV1 policy, DateTimeOffset evaluatedAtUtc, DateTimeOffset expiresAtUtc)
    {
        policy.Validate(Capabilities);
        if (policy.Enabled && policy.MinimumAge is null) throw new ArgumentException("Event retention requires a minimum age.", nameof(policy));
        if (policy.MinimumAge is { } age && evaluatedAtUtc < DateTimeOffset.MinValue.Add(age))
            throw new ArgumentException("Event retention age underflows the UTC clock.", nameof(policy));
        if (evaluatedAtUtc.Offset != TimeSpan.Zero || expiresAtUtc.Offset != TimeSpan.Zero ||
            expiresAtUtc <= evaluatedAtUtc || expiresAtUtc - evaluatedAtUtc > TimeSpan.FromMinutes(10))
            throw new ArgumentException("Event retention requires a UTC plan with a ten-minute expiry.");
    }
    private async ValueTask<SqliteConnection> OpenAsync(CancellationToken ct)
    { var c = new SqliteConnection(connectionString); await c.OpenAsync(ct); return c; }
    private static string Dt(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    private static DateTimeOffset Parse(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
