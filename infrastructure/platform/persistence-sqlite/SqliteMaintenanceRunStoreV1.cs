using System.Globalization;
using System.Text.Json;
using Hispania.Platform.Contracts.Maintenance;
using Hispania.Platform.Maintenance.Application;
using Microsoft.Data.Sqlite;

namespace Hispania.Platform.Persistence.Sqlite;

/// <summary>Owner-neutral operation journal. It never reads or mutates owner data.</summary>
public sealed class SqliteMaintenanceRunStoreV1 : IMaintenanceRunStoreV1
{
    private readonly string connectionString;

    public SqliteMaintenanceRunStoreV1(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        connectionString = new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false }.ToString();
        using var connection = new SqliteConnection(connectionString);
        Hispania.Persistence.Schema.SqliteSchemaUpgrade.Open(connection, "Platform.Maintenance");
        Hispania.Persistence.Schema.SqliteSchemaUpgrade.Initialize(connection, "Platform.Maintenance",
            "PRAGMA journal_mode=WAL; CREATE TABLE IF NOT EXISTS maintenance_plans(plan_id TEXT PRIMARY KEY,owner_id TEXT NOT NULL,policy_json TEXT NOT NULL,evaluated_utc TEXT NOT NULL,expires_utc TEXT NOT NULL,candidates INTEGER NOT NULL,protected INTEGER NOT NULL,estimated_bytes INTEGER,oldest_utc TEXT,newest_utc TEXT,reason TEXT NOT NULL,warnings_json TEXT NOT NULL); CREATE TABLE IF NOT EXISTS maintenance_runs(run_id TEXT PRIMARY KEY,plan_id TEXT NOT NULL REFERENCES maintenance_plans(plan_id),owner_id TEXT NOT NULL,operation INTEGER NOT NULL,state INTEGER NOT NULL,started_utc TEXT NOT NULL,completed_utc TEXT,batches INTEGER NOT NULL,candidates INTEGER NOT NULL,affected INTEGER NOT NULL,estimated_bytes_freed INTEGER,error_category TEXT,correlation_id TEXT,lease_until_utc TEXT); CREATE INDEX IF NOT EXISTS ix_maintenance_runs_owner_state ON maintenance_runs(owner_id,state,lease_until_utc); CREATE INDEX IF NOT EXISTS ix_maintenance_runs_started ON maintenance_runs(started_utc);");
    }

    public async ValueTask SavePlanAsync(RetentionPlanV1 plan, CancellationToken cancellationToken)
    {
        await using var c = await OpenAsync(cancellationToken); await using var q = c.CreateCommand();
        q.CommandText = "INSERT INTO maintenance_plans(plan_id,owner_id,policy_json,evaluated_utc,expires_utc,candidates,protected,estimated_bytes,oldest_utc,newest_utc,reason,warnings_json) VALUES($id,$owner,$policy,$evaluated,$expires,$candidates,$protected,$bytes,$oldest,$newest,$reason,$warnings)";
        q.Parameters.AddWithValue("$id", Id(plan.PlanId)); q.Parameters.AddWithValue("$owner", plan.Owner.Value);
        q.Parameters.AddWithValue("$policy", JsonSerializer.Serialize(plan.Policy));
        q.Parameters.AddWithValue("$evaluated", Dt(plan.EvaluatedAtUtc)); q.Parameters.AddWithValue("$expires", Dt(plan.ExpiresAtUtc));
        q.Parameters.AddWithValue("$candidates", plan.CandidateCount); q.Parameters.AddWithValue("$protected", plan.ProtectedCount);
        q.Parameters.AddWithValue("$bytes", Db(plan.EstimatedBytes)); q.Parameters.AddWithValue("$oldest", DbDt(plan.OldestCandidateUtc));
        q.Parameters.AddWithValue("$newest", DbDt(plan.NewestCandidateUtc)); q.Parameters.AddWithValue("$reason", plan.Reason);
        q.Parameters.AddWithValue("$warnings", JsonSerializer.Serialize(plan.Warnings));
        await q.ExecuteNonQueryAsync(cancellationToken);
    }

    public async ValueTask<RetentionPlanV1?> GetPlanAsync(Guid planId, CancellationToken cancellationToken)
    {
        await using var c = await OpenAsync(cancellationToken); await using var q = c.CreateCommand();
        q.CommandText = "SELECT plan_id,owner_id,policy_json,evaluated_utc,expires_utc,candidates,protected,estimated_bytes,oldest_utc,newest_utc,reason,warnings_json FROM maintenance_plans WHERE plan_id=$id";
        q.Parameters.AddWithValue("$id", Id(planId)); await using var r = await q.ExecuteReaderAsync(cancellationToken);
        if (!await r.ReadAsync(cancellationToken)) return null;
        return new(Guid.Parse(r.GetString(0)), new(r.GetString(1)),
            JsonSerializer.Deserialize<RetentionPolicyV1>(r.GetString(2)) ?? throw new InvalidDataException("Retention policy is corrupt."),
            Parse(r.GetString(3)), Parse(r.GetString(4)), r.GetInt64(5), r.GetInt64(6),
            r.IsDBNull(7) ? null : r.GetInt64(7), r.IsDBNull(8) ? null : Parse(r.GetString(8)),
            r.IsDBNull(9) ? null : Parse(r.GetString(9)), r.GetString(10),
            JsonSerializer.Deserialize<string[]>(r.GetString(11)) ?? []);
    }

    public async ValueTask<MaintenanceRunV1> BeginAsync(RetentionPlanV1 plan, string? correlationId,
        DateTimeOffset nowUtc, DateTimeOffset leaseUntilUtc, CancellationToken cancellationToken)
    {
        await using var c = await OpenAsync(cancellationToken); await using var tx = c.BeginTransaction(deferred: false);
        await using (var stale = c.CreateCommand())
        {
            stale.Transaction = tx;
            stale.CommandText = "UPDATE maintenance_runs SET state=$interrupted,completed_utc=$now,lease_until_utc=NULL,error_category='LeaseExpired' WHERE owner_id=$owner AND state=$running AND lease_until_utc<=$now";
            stale.Parameters.AddWithValue("$interrupted", (int)MaintenanceRunStateV1.Interrupted);
            stale.Parameters.AddWithValue("$running", (int)MaintenanceRunStateV1.Running);
            stale.Parameters.AddWithValue("$owner", plan.Owner.Value); stale.Parameters.AddWithValue("$now", Dt(nowUtc));
            await stale.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var active = c.CreateCommand())
        {
            active.Transaction = tx; active.CommandText = "SELECT COUNT(*) FROM maintenance_runs WHERE owner_id=$owner AND state=$running";
            active.Parameters.AddWithValue("$owner", plan.Owner.Value); active.Parameters.AddWithValue("$running", (int)MaintenanceRunStateV1.Running);
            if (Convert.ToInt64(await active.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) != 0)
                throw new InvalidOperationException("Another maintenance run is active for this owner.");
        }
        var run = new MaintenanceRunV1(Guid.NewGuid(), plan.Owner, MaintenanceOperationTypeV1.Retention,
            MaintenanceRunStateV1.Running, nowUtc, null, 0, plan.CandidateCount, 0, null, null, correlationId);
        await using var q = c.CreateCommand(); q.Transaction = tx;
        q.CommandText = "INSERT INTO maintenance_runs(run_id,plan_id,owner_id,operation,state,started_utc,batches,candidates,affected,correlation_id,lease_until_utc) VALUES($run,$plan,$owner,$operation,$state,$started,0,$candidates,0,$correlation,$lease)";
        q.Parameters.AddWithValue("$run", Id(run.RunId)); q.Parameters.AddWithValue("$plan", Id(plan.PlanId));
        q.Parameters.AddWithValue("$owner", run.Owner.Value); q.Parameters.AddWithValue("$operation", (int)run.Operation);
        q.Parameters.AddWithValue("$state", (int)run.State); q.Parameters.AddWithValue("$started", Dt(nowUtc));
        q.Parameters.AddWithValue("$candidates", run.Candidates); q.Parameters.AddWithValue("$correlation", Db(correlationId));
        q.Parameters.AddWithValue("$lease", Dt(leaseUntilUtc)); await q.ExecuteNonQueryAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken); return run;
    }

    public async ValueTask<MaintenanceRunV1> CheckpointAsync(Guid runId, RetentionBatchResultV1 batch,
        DateTimeOffset leaseUntilUtc, CancellationToken cancellationToken)
    {
        await using var c = await OpenAsync(cancellationToken); await using var q = c.CreateCommand();
        q.CommandText = "UPDATE maintenance_runs SET batches=batches+1,affected=affected+$affected,estimated_bytes_freed=CASE WHEN $bytes IS NULL THEN estimated_bytes_freed ELSE COALESCE(estimated_bytes_freed,0)+$bytes END,lease_until_utc=$lease WHERE run_id=$id AND state=$running";
        q.Parameters.AddWithValue("$affected", batch.Affected); q.Parameters.AddWithValue("$bytes", Db(batch.EstimatedBytesFreed));
        q.Parameters.AddWithValue("$lease", Dt(leaseUntilUtc)); q.Parameters.AddWithValue("$id", Id(runId));
        q.Parameters.AddWithValue("$running", (int)MaintenanceRunStateV1.Running);
        if (await q.ExecuteNonQueryAsync(cancellationToken) != 1) throw new InvalidOperationException("Maintenance run was fenced.");
        return await ReadAsync(c, runId, cancellationToken);
    }

    public async ValueTask<MaintenanceRunV1> FinishAsync(Guid runId, MaintenanceRunStateV1 state,
        string? errorCategory, DateTimeOffset nowUtc, CancellationToken cancellationToken)
    {
        if (state is not (MaintenanceRunStateV1.Completed or MaintenanceRunStateV1.Failed or MaintenanceRunStateV1.Cancelled))
            throw new ArgumentException("Invalid terminal maintenance state.", nameof(state));
        await using var c = await OpenAsync(cancellationToken); await using var q = c.CreateCommand();
        q.CommandText = "UPDATE maintenance_runs SET state=$state,completed_utc=$now,error_category=$error,lease_until_utc=NULL WHERE run_id=$id AND state=$running";
        q.Parameters.AddWithValue("$state", (int)state); q.Parameters.AddWithValue("$now", Dt(nowUtc));
        q.Parameters.AddWithValue("$error", Db(errorCategory)); q.Parameters.AddWithValue("$id", Id(runId));
        q.Parameters.AddWithValue("$running", (int)MaintenanceRunStateV1.Running);
        if (await q.ExecuteNonQueryAsync(cancellationToken) != 1) throw new InvalidOperationException("Maintenance run was fenced.");
        return await ReadAsync(c, runId, cancellationToken);
    }

    public async ValueTask<IReadOnlyList<MaintenanceRunV1>> ListAsync(int limit, CancellationToken cancellationToken)
    {
        await using var c = await OpenAsync(cancellationToken); await using var q = c.CreateCommand();
        q.CommandText = "SELECT run_id,owner_id,operation,state,started_utc,completed_utc,batches,candidates,affected,estimated_bytes_freed,error_category,correlation_id FROM maintenance_runs ORDER BY started_utc DESC,run_id DESC LIMIT $limit";
        q.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 100));
        var results = new List<MaintenanceRunV1>(); await using var r = await q.ExecuteReaderAsync(cancellationToken);
        while (await r.ReadAsync(cancellationToken)) results.Add(Read(r)); return results;
    }

    private static async ValueTask<MaintenanceRunV1> ReadAsync(SqliteConnection c, Guid runId, CancellationToken ct)
    {
        await using var q = c.CreateCommand();
        q.CommandText = "SELECT run_id,owner_id,operation,state,started_utc,completed_utc,batches,candidates,affected,estimated_bytes_freed,error_category,correlation_id FROM maintenance_runs WHERE run_id=$id";
        q.Parameters.AddWithValue("$id", Id(runId)); await using var r = await q.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? Read(r) : throw new InvalidOperationException("Maintenance run disappeared.");
    }

    private static MaintenanceRunV1 Read(SqliteDataReader r) => new(Guid.Parse(r.GetString(0)), new(r.GetString(1)),
        (MaintenanceOperationTypeV1)r.GetInt32(2), (MaintenanceRunStateV1)r.GetInt32(3), Parse(r.GetString(4)),
        r.IsDBNull(5) ? null : Parse(r.GetString(5)), r.GetInt32(6), r.GetInt64(7), r.GetInt64(8),
        r.IsDBNull(9) ? null : r.GetInt64(9), r.IsDBNull(10) ? null : r.GetString(10),
        r.IsDBNull(11) ? null : r.GetString(11));
    private async ValueTask<SqliteConnection> OpenAsync(CancellationToken ct)
    { var c = new SqliteConnection(connectionString); await c.OpenAsync(ct); return c; }
    private static string Id(Guid value) => value.ToString("D");
    private static string Dt(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    private static DateTimeOffset Parse(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
    private static object Db(object? value) => value ?? DBNull.Value;
    private static object DbDt(DateTimeOffset? value) => value is { } timestamp ? Dt(timestamp) : DBNull.Value;
}
