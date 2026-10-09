using System.Globalization;
using Hispania.Platform.Contracts.Maintenance;
using Microsoft.Data.Sqlite;

namespace Hispania.Platform.Persistence.Sqlite;

/// <summary>Prunes only successful old run rows; failures and the last success per owner remain diagnostic anchors.</summary>
public sealed class SqliteMaintenanceHistoryRetentionOwnerV1 : IRetentionOwnerV1
{
    private readonly string connectionString;
    private readonly TimeProvider clock;
    public SqliteMaintenanceHistoryRetentionOwnerV1(string path, TimeProvider? clock = null)
    {
        _ = new SqliteMaintenanceRunStoreV1(path);
        connectionString = new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWrite,
            Pooling = false }.ToString();
        this.clock = clock ?? TimeProvider.System;
    }
    public MaintenanceOwnerIdV1 OwnerId => new("platform.maintenance-history");
    public RetentionCapabilitiesV1 Capabilities => new(true, true, false, 500, true);

    public async ValueTask<RetentionPlanV1> PlanAsync(RetentionPolicyV1 policy,
        DateTimeOffset evaluatedAtUtc, DateTimeOffset expiresAtUtc, CancellationToken cancellationToken = default)
    {
        Validate(policy, evaluatedAtUtc, expiresAtUtc);
        if (!policy.Enabled)
            return new(Guid.NewGuid(), OwnerId, policy, evaluatedAtUtc, expiresAtUtc, 0, 0, null,
                null, null, "Maintenance history retention is disabled.", []);
        await using var c = await OpenAsync(cancellationToken);
        await using var q = c.CreateCommand();
        q.CommandText = $"SELECT COUNT(*),MIN(started_utc),MAX(started_utc) FROM ({EligibleSql})";
        Bind(q, policy, evaluatedAtUtc);
        await using var r = await q.ExecuteReaderAsync(cancellationToken);
        await r.ReadAsync(cancellationToken);
        var count = r.GetInt64(0);
        DateTimeOffset? oldest = r.IsDBNull(1) ? null : Parse(r.GetString(1));
        DateTimeOffset? newest = r.IsDBNull(2) ? null : Parse(r.GetString(2));
        await r.DisposeAsync();
        await using var total = c.CreateCommand(); total.CommandText = "SELECT COUNT(*) FROM maintenance_runs";
        var all = Convert.ToInt64(await total.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
        return new(Guid.NewGuid(), OwnerId, policy, evaluatedAtUtc, expiresAtUtc, count, all - count,
            null, oldest, newest,
            "Only aged successful runs beyond the per-owner recent count; active, failed, interrupted and last success are protected.",
            ["SQLite deletion reclaims rows for reuse, not necessarily filesystem bytes."]);
    }

    public async ValueTask<RetentionBatchResultV1> ExecuteBatchAsync(RetentionPlanV1 plan,
        CancellationToken cancellationToken = default)
    {
        if (plan.Owner != OwnerId || !plan.Policy.Enabled)
            throw new InvalidOperationException("Maintenance history plan is not executable.");
        Validate(plan.Policy, plan.EvaluatedAtUtc, plan.ExpiresAtUtc);
        var now = clock.GetUtcNow();
        if (now < plan.EvaluatedAtUtc || now > plan.ExpiresAtUtc)
            throw new InvalidOperationException("Maintenance history plan is stale or clock moved backwards.");
        await using var c = await OpenAsync(cancellationToken);
        await using var tx = c.BeginTransaction(deferred: false);
        var selected = new List<string>();
        await using (var q = c.CreateCommand())
        {
            q.Transaction = tx;
            q.CommandText = $"SELECT run_id FROM ({EligibleSql}) ORDER BY started_utc,run_id LIMIT $limit";
            Bind(q, plan.Policy, plan.EvaluatedAtUtc);
            q.Parameters.AddWithValue("$limit", plan.Policy.BatchSize);
            await using var r = await q.ExecuteReaderAsync(cancellationToken);
            while (await r.ReadAsync(cancellationToken)) selected.Add(r.GetString(0));
        }
        var affected = 0;
        foreach (var id in selected)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using var q = c.CreateCommand(); q.Transaction = tx;
            q.CommandText = $"DELETE FROM maintenance_runs WHERE run_id=$id AND run_id IN (SELECT run_id FROM ({EligibleSql}))";
            q.Parameters.AddWithValue("$id", id); Bind(q, plan.Policy, plan.EvaluatedAtUtc);
            affected += await q.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var plans = c.CreateCommand())
        {
            plans.Transaction = tx;
            plans.CommandText = "DELETE FROM maintenance_plans WHERE expires_utc<=$now AND NOT EXISTS(SELECT 1 FROM maintenance_runs r WHERE r.plan_id=maintenance_plans.plan_id)";
            plans.Parameters.AddWithValue("$now", now.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
            await plans.ExecuteNonQueryAsync(cancellationToken);
        }
        await tx.CommitAsync(cancellationToken);
        return new(selected.Count, affected, null, selected.Count == plan.Policy.BatchSize, selected.Count - affected);
    }

    private const string EligibleSql = "SELECT run_id,started_utc FROM (SELECT r.run_id,r.owner_id,r.state,r.started_utc,ROW_NUMBER() OVER(PARTITION BY r.owner_id ORDER BY r.started_utc DESC,r.run_id DESC) AS ordinal FROM maintenance_runs r) ranked WHERE state=2 AND started_utc<=$cutoff AND ordinal>$keep AND EXISTS(SELECT 1 FROM maintenance_runs newer WHERE newer.owner_id=ranked.owner_id AND newer.state=2 AND (newer.started_utc>ranked.started_utc OR (newer.started_utc=ranked.started_utc AND newer.run_id>ranked.run_id)))";
    private static void Bind(SqliteCommand q, RetentionPolicyV1 policy, DateTimeOffset at)
    {
        q.Parameters.AddWithValue("$cutoff", (at - policy.MinimumAge!.Value).ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        q.Parameters.AddWithValue("$keep", Math.Max(1, policy.MaximumRetainedCount ?? 20));
    }
    private void Validate(RetentionPolicyV1 policy, DateTimeOffset at, DateTimeOffset expires)
    {
        policy.Validate(Capabilities);
        if (policy.Enabled && policy.MinimumAge is null)
            throw new ArgumentException("Maintenance history retention requires a minimum age.", nameof(policy));
        if (policy.MinimumAge is { } age && at < DateTimeOffset.MinValue.Add(age))
            throw new ArgumentException("Maintenance history retention age underflows UTC.", nameof(policy));
        if (at.Offset != TimeSpan.Zero || expires.Offset != TimeSpan.Zero || expires <= at ||
            expires - at > TimeSpan.FromMinutes(10))
            throw new ArgumentException("Maintenance history requires a UTC plan with a ten-minute expiry.");
    }
    private async ValueTask<SqliteConnection> OpenAsync(CancellationToken ct)
    { var c = new SqliteConnection(connectionString); await c.OpenAsync(ct); return c; }
    private static DateTimeOffset Parse(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture);
}
