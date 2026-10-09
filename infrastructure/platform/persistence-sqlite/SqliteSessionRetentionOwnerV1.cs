using System.Globalization;
using Hispania.Platform.Contracts.Maintenance;
using Microsoft.Data.Sqlite;

namespace Hispania.Platform.Persistence.Sqlite;

/// <summary>Identity owns sessions; users, roles and credentials are never retention candidates.</summary>
public sealed class SqliteSessionRetentionOwnerV1 : IRetentionOwnerV1
{
    private readonly string connectionString;
    private readonly TimeProvider clock;
    public SqliteSessionRetentionOwnerV1(string path, TimeProvider? clock = null)
    {
        connectionString = new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWrite,
            ForeignKeys = true, Pooling = false }.ToString();
        this.clock = clock ?? TimeProvider.System;
    }
    public MaintenanceOwnerIdV1 OwnerId => new("platform.identity.sessions");
    public RetentionCapabilitiesV1 Capabilities => new(true, false, false, 500, true);

    public async ValueTask<RetentionPlanV1> PlanAsync(RetentionPolicyV1 policy, DateTimeOffset evaluatedAtUtc,
        DateTimeOffset expiresAtUtc, CancellationToken cancellationToken = default)
    {
        Validate(policy, evaluatedAtUtc, expiresAtUtc);
        if (evaluatedAtUtc > clock.GetUtcNow()) throw new InvalidOperationException("Retention evaluation cannot use a future clock.");
        if (!policy.Enabled) return new(Guid.NewGuid(), OwnerId, policy, evaluatedAtUtc, expiresAtUtc,
            0, 0, null, null, null, "Session retention is disabled.", []);
        await using var c = await OpenAsync(cancellationToken); await using var q = c.CreateCommand();
        q.CommandText = $"SELECT COUNT(*),MIN(created_utc),MAX(created_utc) FROM identity_sessions WHERE {EligibleSql}";
        Bind(q, policy, evaluatedAtUtc);
        await using var r = await q.ExecuteReaderAsync(cancellationToken); await r.ReadAsync(cancellationToken);
        var candidates = r.GetInt64(0);
        DateTimeOffset? oldest = r.IsDBNull(1) ? null : Parse(r.GetString(1));
        DateTimeOffset? newest = r.IsDBNull(2) ? null : Parse(r.GetString(2));
        await r.DisposeAsync();
        await using var count = c.CreateCommand(); count.CommandText = "SELECT COUNT(*) FROM identity_sessions";
        var total = Convert.ToInt64(await count.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
        return new(Guid.NewGuid(), OwnerId, policy, evaluatedAtUtc, expiresAtUtc, candidates,
            total - candidates, null, oldest, newest,
            "Expired or revoked session hashes outside the age horizon; active sessions remain protected.", []);
    }

    public async ValueTask<RetentionBatchResultV1> ExecuteBatchAsync(RetentionPlanV1 plan,
        CancellationToken cancellationToken = default)
    {
        if (plan.Owner != OwnerId || !plan.Policy.Enabled) throw new InvalidOperationException("Session retention plan is not executable.");
        Validate(plan.Policy, plan.EvaluatedAtUtc, plan.ExpiresAtUtc);
        var now = clock.GetUtcNow();
        if (now < plan.EvaluatedAtUtc || now > plan.ExpiresAtUtc)
            throw new InvalidOperationException("Session retention plan is stale or clock moved backwards.");
        await using var c = await OpenAsync(cancellationToken); await using var tx = c.BeginTransaction(deferred: false);
        await using var q = c.CreateCommand(); q.Transaction = tx;
        q.CommandText = $"DELETE FROM identity_sessions WHERE session_id IN (SELECT session_id FROM identity_sessions WHERE {EligibleSql} ORDER BY created_utc,session_id LIMIT $limit) AND {EligibleSql}";
        Bind(q, plan.Policy, plan.EvaluatedAtUtc); q.Parameters.AddWithValue("$limit", plan.Policy.BatchSize);
        var affected = await q.ExecuteNonQueryAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);
        return new(affected, affected, null, affected == plan.Policy.BatchSize, 0);
    }

    private const string EligibleSql = "created_utc<=$cutoff AND (expires_utc<=$cutoff OR (revoked_utc IS NOT NULL AND revoked_utc<=$cutoff))";
    private static void Bind(SqliteCommand q, RetentionPolicyV1 policy, DateTimeOffset evaluatedAtUtc)
        => q.Parameters.AddWithValue("$cutoff", Dt(evaluatedAtUtc - policy.MinimumAge!.Value));
    private void Validate(RetentionPolicyV1 policy, DateTimeOffset evaluatedAtUtc, DateTimeOffset expiresAtUtc)
    {
        policy.Validate(Capabilities);
        if (policy.Enabled && policy.MinimumAge is null) throw new ArgumentException("Session retention requires a minimum age.", nameof(policy));
        if (policy.MinimumAge is { } age && evaluatedAtUtc < DateTimeOffset.MinValue.Add(age))
            throw new ArgumentException("Session retention age underflows the UTC clock.", nameof(policy));
        if (evaluatedAtUtc.Offset != TimeSpan.Zero || expiresAtUtc.Offset != TimeSpan.Zero ||
            expiresAtUtc <= evaluatedAtUtc || expiresAtUtc - evaluatedAtUtc > TimeSpan.FromMinutes(10))
            throw new ArgumentException("Session retention requires a UTC plan with a ten-minute expiry.");
    }
    private async ValueTask<SqliteConnection> OpenAsync(CancellationToken ct)
    { var c = new SqliteConnection(connectionString); await c.OpenAsync(ct); return c; }
    private static string Dt(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    private static DateTimeOffset Parse(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
