using System.Globalization;
using Hispania.Platform.Contracts.Jobs;
using Hispania.Platform.Contracts.Maintenance;
using Hispania.Platform.Jobs.Application;
using Microsoft.Data.Sqlite;

namespace Hispania.Platform.Persistence.Sqlite;

/// <summary>Independent SQLite file/schema for durable Platform Jobs.</summary>
public sealed class SqlitePlatformJobStore : IPlatformJobStoreV1, IRetentionOwnerV1
{
    private readonly string connectionString;
    private readonly TimeProvider clock;
    private readonly TimeSpan idempotencyHorizon;
    public SqlitePlatformJobStore(string path, TimeProvider? clock = null, TimeSpan? idempotencyHorizon = null)
    {
        this.clock = clock ?? TimeProvider.System;
        this.idempotencyHorizon = idempotencyHorizon ?? TimeSpan.FromDays(365);
        if (this.idempotencyHorizon < TimeSpan.Zero || this.idempotencyHorizon > TimeSpan.FromDays(3650))
            throw new ArgumentOutOfRangeException(nameof(idempotencyHorizon));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        connectionString = new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString();
        using var connection = new SqliteConnection(connectionString); Hispania.Persistence.Schema.SqliteSchemaUpgrade.Open(connection, "Platform.Jobs");
        using var command = connection.CreateCommand();
        Hispania.Persistence.Schema.SqliteSchemaUpgrade.Initialize(connection, "Platform.Jobs", "PRAGMA journal_mode=WAL; CREATE TABLE IF NOT EXISTS platform_jobs(job_id TEXT PRIMARY KEY,job_type TEXT NOT NULL,status INTEGER NOT NULL,created_at TEXT NOT NULL,scheduled_at TEXT NOT NULL,started_at TEXT,completed_at TEXT,attempt_count INTEGER NOT NULL,max_attempts INTEGER NOT NULL,timeout_seconds INTEGER,correlation_id TEXT,causation_id TEXT,idempotency_key TEXT,payload_json TEXT NOT NULL,result_json TEXT,error_summary TEXT,executor TEXT NOT NULL,lease_expires_at TEXT,cancel_requested INTEGER NOT NULL DEFAULT 0,terminal_audit_recorded INTEGER NOT NULL DEFAULT 0,execution_policy INTEGER NOT NULL DEFAULT 0,worker_id TEXT,capability TEXT,generation INTEGER,lease_id TEXT); CREATE UNIQUE INDEX IF NOT EXISTS ux_platform_jobs_idempotency ON platform_jobs(job_type,idempotency_key) WHERE idempotency_key IS NOT NULL; CREATE INDEX IF NOT EXISTS ix_platform_jobs_due ON platform_jobs(status,scheduled_at,lease_expires_at); CREATE TABLE IF NOT EXISTS platform_job_attempts(attempt_id TEXT PRIMARY KEY,job_id TEXT NOT NULL REFERENCES platform_jobs(job_id),attempt_number INTEGER NOT NULL,started_at TEXT NOT NULL,completed_at TEXT,outcome INTEGER,error_summary TEXT,executor TEXT NOT NULL,executor_kind TEXT NOT NULL DEFAULT 'local',worker_id TEXT,capability TEXT,generation INTEGER,lease_id TEXT,UNIQUE(job_id,attempt_number));", [("platform_jobs", "execution_policy", "INTEGER NOT NULL DEFAULT 0"), ("platform_jobs", "worker_id", "TEXT"), ("platform_jobs", "capability", "TEXT"), ("platform_jobs", "generation", "INTEGER"), ("platform_jobs", "lease_id", "TEXT"), ("platform_jobs", "trace_parent", "TEXT"), ("platform_jobs", "trace_state", "TEXT"), ("platform_job_attempts", "executor_kind", "TEXT NOT NULL DEFAULT 'local'"), ("platform_job_attempts", "worker_id", "TEXT"), ("platform_job_attempts", "capability", "TEXT"), ("platform_job_attempts", "generation", "INTEGER"), ("platform_job_attempts", "lease_id", "TEXT")],
            [new Hispania.Persistence.Schema.SqliteSchemaMigration(2, "0002_retention_tombstones", (db, tx) => Hispania.Persistence.Schema.SqliteSchemaUpgrade.Execute(db, tx,
                "CREATE TABLE IF NOT EXISTS platform_job_idempotency_tombstones(job_type TEXT NOT NULL,idempotency_key TEXT NOT NULL,job_id TEXT NOT NULL,status INTEGER NOT NULL,completed_at TEXT NOT NULL,expires_at TEXT NOT NULL,PRIMARY KEY(job_type,idempotency_key)); CREATE INDEX IF NOT EXISTS ix_platform_job_tombstone_expiry ON platform_job_idempotency_tombstones(expires_at); CREATE INDEX IF NOT EXISTS ix_platform_jobs_retention ON platform_jobs(status,completed_at);"))]);
    }

    public async ValueTask<PlatformJobCreateResultV1> CreateAsync(PlatformJobV1 job, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken); await using var tx = connection.BeginTransaction(deferred: false);
        if (job.IdempotencyKey is not null)
        {
            await using var find = connection.CreateCommand(); find.Transaction = (SqliteTransaction)tx; find.CommandText = "SELECT job_id FROM platform_jobs WHERE job_type=$type AND idempotency_key=$key"; find.Parameters.AddWithValue("$type", job.JobType); find.Parameters.AddWithValue("$key", job.IdempotencyKey);
            var found = await find.ExecuteScalarAsync(cancellationToken);
            if (found is string id)
            {
                await using var read = connection.CreateCommand(); read.Transaction = (SqliteTransaction)tx; read.CommandText = "SELECT * FROM platform_jobs WHERE job_id=$id"; read.Parameters.AddWithValue("$id", id); await using var reader = await read.ExecuteReaderAsync(cancellationToken); var existing = await reader.ReadAsync(cancellationToken) ? ReadJob(reader) : throw new InvalidOperationException("Idempotency row disappeared inside its transaction."); await reader.DisposeAsync(); await tx.CommitAsync(cancellationToken); return new(existing, true);
            }
            await using var tombstone = connection.CreateCommand(); tombstone.Transaction = (SqliteTransaction)tx;
            tombstone.CommandText = "SELECT job_id,status,completed_at,expires_at FROM platform_job_idempotency_tombstones WHERE job_type=$type AND idempotency_key=$key";
            tombstone.Parameters.AddWithValue("$type", job.JobType); tombstone.Parameters.AddWithValue("$key", job.IdempotencyKey);
            await using var tombstoneReader = await tombstone.ExecuteReaderAsync(cancellationToken);
            if (await tombstoneReader.ReadAsync(cancellationToken))
            {
                var priorId = Guid.Parse(tombstoneReader.GetString(0));
                var priorStatus = (PlatformJobStatusV1)tombstoneReader.GetInt32(1);
                var completed = Parse(tombstoneReader.GetString(2));
                var expires = Parse(tombstoneReader.GetString(3));
                await tombstoneReader.DisposeAsync();
                if (expires > clock.GetUtcNow())
                {
                    var previous = job with { JobId = priorId, Status = priorStatus,
                        CreatedAt = completed, ScheduledAt = completed, CompletedAt = completed,
                        PayloadJson = "{}", ResultMetadataJson = null, IdempotencyKey = job.IdempotencyKey };
                    await tx.CommitAsync(cancellationToken);
                    return new(previous, true, true);
                }
                await using var expired = connection.CreateCommand(); expired.Transaction = (SqliteTransaction)tx;
                expired.CommandText = "DELETE FROM platform_job_idempotency_tombstones WHERE job_type=$type AND idempotency_key=$key AND expires_at<=$now";
                expired.Parameters.AddWithValue("$type", job.JobType); expired.Parameters.AddWithValue("$key", job.IdempotencyKey);
                expired.Parameters.AddWithValue("$now", Dt(clock.GetUtcNow()));
                await expired.ExecuteNonQueryAsync(cancellationToken);
            }
        }
        await using (var insert = connection.CreateCommand())
        {
            insert.Transaction = (SqliteTransaction)tx; insert.CommandText = "INSERT INTO platform_jobs(job_id,job_type,status,created_at,scheduled_at,attempt_count,max_attempts,timeout_seconds,correlation_id,causation_id,idempotency_key,payload_json,executor,execution_policy,trace_parent,trace_state) VALUES($id,$type,$status,$created,$scheduled,0,$max,$timeout,$correlation,$causation,$key,$payload,'local',$policy,$traceParent,$traceState)";
            insert.Parameters.AddWithValue("$id", Id(job.JobId)); insert.Parameters.AddWithValue("$type", job.JobType); insert.Parameters.AddWithValue("$status", (int)job.Status); insert.Parameters.AddWithValue("$created", Dt(job.CreatedAt)); insert.Parameters.AddWithValue("$scheduled", Dt(job.ScheduledAt)); insert.Parameters.AddWithValue("$max", job.MaxAttempts); insert.Parameters.AddWithValue("$timeout", Db(job.TimeoutSeconds)); insert.Parameters.AddWithValue("$correlation", Db(job.CorrelationId)); insert.Parameters.AddWithValue("$causation", Db(job.CausationId)); insert.Parameters.AddWithValue("$key", Db(job.IdempotencyKey)); insert.Parameters.AddWithValue("$payload", job.PayloadJson); insert.Parameters.AddWithValue("$policy", (int)job.ExecutionPolicy); insert.Parameters.AddWithValue("$traceParent", Db(job.TraceParent)); insert.Parameters.AddWithValue("$traceState", Db(job.TraceState)); await insert.ExecuteNonQueryAsync(cancellationToken);
        }
        await tx.CommitAsync(cancellationToken); return new(job, false);
    }

    public async ValueTask<PlatformJobV1?> GetAsync(Guid jobId, CancellationToken cancellationToken)
    { await using var c = await OpenAsync(cancellationToken); await using var q = c.CreateCommand(); q.CommandText = "SELECT * FROM platform_jobs WHERE job_id=$id"; q.Parameters.AddWithValue("$id", Id(jobId)); await using var r = await q.ExecuteReaderAsync(cancellationToken); return await r.ReadAsync(cancellationToken) ? ReadJob(r) : null; }

    public async ValueTask<IReadOnlyList<PlatformJobListItemV1>> ListAsync(PlatformJobStatusV1? status, int limit, CancellationToken cancellationToken)
    {
        await using var c = await OpenAsync(cancellationToken); await using var q = c.CreateCommand(); q.CommandText = "SELECT job_id,job_type,status,created_at,scheduled_at,started_at,completed_at,attempt_count,max_attempts,executor,error_summary,execution_policy,worker_id,capability,generation FROM platform_jobs WHERE ($status IS NULL OR status=$status) ORDER BY created_at DESC LIMIT $limit"; q.Parameters.AddWithValue("$status", status is null ? DBNull.Value : (int)status); q.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 200));
        var result = new List<PlatformJobListItemV1>(); await using var r = await q.ExecuteReaderAsync(cancellationToken); while (await r.ReadAsync(cancellationToken)) result.Add(new(Guid.Parse(r.GetString(0)), r.GetString(1), (PlatformJobStatusV1)r.GetInt32(2), Parse(r.GetString(3)), Parse(r.GetString(4)), NullableDate(r,5), NullableDate(r,6), r.GetInt32(7), r.GetInt32(8), r.GetString(9), r.IsDBNull(10) ? null : r.GetString(10), (PlatformJobExecutionPolicyV1)r.GetInt32(11), r.IsDBNull(12) ? null : r.GetString(12), r.IsDBNull(13) ? null : r.GetString(13), r.IsDBNull(14) ? null : r.GetInt64(14))); return result;
    }
    public async ValueTask<IReadOnlyList<PlatformJobAttemptV1>> GetAttemptsAsync(Guid jobId, CancellationToken cancellationToken)
    { await using var c = await OpenAsync(cancellationToken); await using var q = c.CreateCommand(); q.CommandText = "SELECT attempt_id,job_id,attempt_number,started_at,completed_at,outcome,error_summary,executor,executor_kind,worker_id,capability,generation,lease_id FROM platform_job_attempts WHERE job_id=$id ORDER BY attempt_number"; q.Parameters.AddWithValue("$id", Id(jobId)); var list = new List<PlatformJobAttemptV1>(); await using var r = await q.ExecuteReaderAsync(cancellationToken); while (await r.ReadAsync(cancellationToken)) list.Add(new(Guid.Parse(r.GetString(0)), Guid.Parse(r.GetString(1)), r.GetInt32(2), Parse(r.GetString(3)), NullableDate(r,4), r.IsDBNull(5) ? PlatformJobAttemptOutcomeV1.Running : (PlatformJobAttemptOutcomeV1)r.GetInt32(5), r.IsDBNull(6) ? null : r.GetString(6), r.GetString(7), r.GetString(8), r.IsDBNull(9) ? null : r.GetString(9), r.IsDBNull(10) ? null : r.GetString(10), r.IsDBNull(11) ? null : r.GetInt64(11), r.IsDBNull(12) ? null : r.GetString(12))); return list; }

    public async ValueTask<PlatformJobV1?> AcquireNextAsync(DateTimeOffset now, TimeSpan lease, IReadOnlyCollection<PlatformJobExecutionPolicyV1> policies, CancellationToken cancellationToken)
    {
        await using var c = await OpenAsync(cancellationToken); await using var tx = c.BeginTransaction(deferred: false);
        string? id = null; int previousAttempt = 0;
        await using (var cancelAttempt = c.CreateCommand())
        {
            cancelAttempt.Transaction = tx; cancelAttempt.CommandText = "UPDATE platform_job_attempts SET completed_at=$now,outcome=$cancelled,error_summary='Cancellation was requested before the execution lease expired.' WHERE completed_at IS NULL AND job_id IN (SELECT job_id FROM platform_jobs WHERE status=$running AND lease_expires_at<=$now AND cancel_requested=1)"; cancelAttempt.Parameters.AddWithValue("$now", Dt(now)); cancelAttempt.Parameters.AddWithValue("$cancelled", (int)PlatformJobAttemptOutcomeV1.Cancelled); cancelAttempt.Parameters.AddWithValue("$running", (int)PlatformJobStatusV1.Running); await cancelAttempt.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var cancelJob = c.CreateCommand())
        {
            cancelJob.Transaction = tx; cancelJob.CommandText = "UPDATE platform_jobs SET status=$cancelled,completed_at=$now,lease_expires_at=NULL WHERE status=$running AND lease_expires_at<=$now AND cancel_requested=1"; cancelJob.Parameters.AddWithValue("$cancelled", (int)PlatformJobStatusV1.Cancelled); cancelJob.Parameters.AddWithValue("$now", Dt(now)); cancelJob.Parameters.AddWithValue("$running", (int)PlatformJobStatusV1.Running); await cancelJob.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var expire = c.CreateCommand())
        {
            expire.Transaction = tx; expire.CommandText = "UPDATE platform_job_attempts SET completed_at=$now,outcome=$recovered,error_summary='Executor lease expired after the configured maximum attempts.' WHERE completed_at IS NULL AND job_id IN (SELECT job_id FROM platform_jobs WHERE status=$running AND lease_expires_at<=$now AND attempt_count>=max_attempts)"; expire.Parameters.AddWithValue("$now", Dt(now)); expire.Parameters.AddWithValue("$recovered", (int)PlatformJobAttemptOutcomeV1.Recovered); expire.Parameters.AddWithValue("$running", (int)PlatformJobStatusV1.Running); await expire.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var expire = c.CreateCommand())
        {
            expire.Transaction = tx; expire.CommandText = "UPDATE platform_jobs SET status=$dead,completed_at=$now,lease_expires_at=NULL,error_summary='Execution lease expired after the configured maximum attempts.',terminal_audit_recorded=0 WHERE status=$running AND lease_expires_at<=$now AND attempt_count>=max_attempts"; expire.Parameters.AddWithValue("$dead", (int)PlatformJobStatusV1.DeadLettered); expire.Parameters.AddWithValue("$now", Dt(now)); expire.Parameters.AddWithValue("$running", (int)PlatformJobStatusV1.Running); await expire.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var q = c.CreateCommand())
        {
            q.Transaction = tx; q.CommandText = "SELECT job_id,attempt_count FROM platform_jobs WHERE ((status=$pending AND scheduled_at<=$now) OR (status=$running AND lease_expires_at<=$now)) AND attempt_count<max_attempts AND execution_policy IN (SELECT value FROM json_each($policies)) ORDER BY scheduled_at,created_at LIMIT 1"; q.Parameters.AddWithValue("$pending", (int)PlatformJobStatusV1.Pending); q.Parameters.AddWithValue("$running", (int)PlatformJobStatusV1.Running); q.Parameters.AddWithValue("$now", Dt(now)); q.Parameters.AddWithValue("$policies", System.Text.Json.JsonSerializer.Serialize(policies.Select(static policy => (int)policy))); await using var r = await q.ExecuteReaderAsync(cancellationToken); if (await r.ReadAsync(cancellationToken)) { id = r.GetString(0); previousAttempt = r.GetInt32(1); }
        }
        if (id is null) { await tx.CommitAsync(cancellationToken); return null; }
        if (previousAttempt > 0)
        {
            await using var recover = c.CreateCommand(); recover.Transaction = tx; recover.CommandText = "UPDATE platform_job_attempts SET completed_at=$now,outcome=$outcome,error_summary='Executor lease expired; outcome may have occurred before recovery.' WHERE job_id=$id AND attempt_number=$number AND completed_at IS NULL"; recover.Parameters.AddWithValue("$now", Dt(now)); recover.Parameters.AddWithValue("$outcome", (int)PlatformJobAttemptOutcomeV1.Recovered); recover.Parameters.AddWithValue("$id", id); recover.Parameters.AddWithValue("$number", previousAttempt); await recover.ExecuteNonQueryAsync(cancellationToken);
        }
        var attempt = previousAttempt + 1;
        await using (var update = c.CreateCommand()) { update.Transaction = tx; update.CommandText = "UPDATE platform_jobs SET status=$running,attempt_count=$attempt,started_at=$now,completed_at=NULL,error_summary=NULL,lease_expires_at=$lease,cancel_requested=0,executor='local',worker_id=NULL,capability=NULL,generation=NULL,lease_id=NULL WHERE job_id=$id"; update.Parameters.AddWithValue("$running", (int)PlatformJobStatusV1.Running); update.Parameters.AddWithValue("$attempt", attempt); update.Parameters.AddWithValue("$now", Dt(now)); update.Parameters.AddWithValue("$lease", Dt(now + lease)); update.Parameters.AddWithValue("$id", id); await update.ExecuteNonQueryAsync(cancellationToken); }
        await using (var insert = c.CreateCommand()) { insert.Transaction = tx; insert.CommandText = "INSERT INTO platform_job_attempts(attempt_id,job_id,attempt_number,started_at,executor,executor_kind) VALUES($aid,$id,$number,$now,'local','local')"; insert.Parameters.AddWithValue("$aid", Id(Guid.NewGuid())); insert.Parameters.AddWithValue("$id", id); insert.Parameters.AddWithValue("$number", attempt); insert.Parameters.AddWithValue("$now", Dt(now)); await insert.ExecuteNonQueryAsync(cancellationToken); }
        await using var read = c.CreateCommand(); read.Transaction = tx; read.CommandText = "SELECT * FROM platform_jobs WHERE job_id=$id"; read.Parameters.AddWithValue("$id", id); await using var reader = await read.ExecuteReaderAsync(cancellationToken); var job = await reader.ReadAsync(cancellationToken) ? ReadJob(reader) : null; await reader.DisposeAsync(); await tx.CommitAsync(cancellationToken); return job;
    }
    public ValueTask<PlatformJobV1?> AcquireNextAsync(DateTimeOffset now, TimeSpan lease, CancellationToken cancellationToken) =>
        AcquireNextAsync(now, lease, Enum.GetValues<PlatformJobExecutionPolicyV1>(), cancellationToken);

    public async ValueTask<bool> ExtendLeaseAsync(Guid jobId, DateTimeOffset expiresAt, CancellationToken cancellationToken)
    { await using var c = await OpenAsync(cancellationToken); await using var q = c.CreateCommand(); q.CommandText = "UPDATE platform_jobs SET lease_expires_at=$lease WHERE job_id=$id AND status=$running AND cancel_requested=0"; q.Parameters.AddWithValue("$lease", Dt(expiresAt)); q.Parameters.AddWithValue("$id", Id(jobId)); q.Parameters.AddWithValue("$running", (int)PlatformJobStatusV1.Running); return await q.ExecuteNonQueryAsync(cancellationToken) == 1; }
    public async ValueTask<bool> CompleteAsync(Guid jobId, int attempt, string? resultJson, CancellationToken cancellationToken) => await FinishAsync(jobId, attempt, PlatformJobStatusV1.Succeeded, PlatformJobAttemptOutcomeV1.Succeeded, null, resultJson, cancellationToken);
    public async ValueTask<bool> SetExecutionDetailsAsync(Guid jobId, int attempt, string executorKind, string? workerId, string? capability, long? generation, string? leaseId, CancellationToken cancellationToken)
    {
        await using var c = await OpenAsync(cancellationToken); await using var tx = c.BeginTransaction(deferred: false);
        await using var job = c.CreateCommand(); job.Transaction = tx; job.CommandText = "UPDATE platform_jobs SET executor=$executor,worker_id=$worker,capability=$capability,generation=$generation,lease_id=$lease WHERE job_id=$id AND status=$running AND attempt_count=$attempt";
        Bind(job); var changed = await job.ExecuteNonQueryAsync(cancellationToken); if (changed != 1) { await tx.RollbackAsync(cancellationToken); return false; }
        await using var row = c.CreateCommand(); row.Transaction = tx; row.CommandText = "UPDATE platform_job_attempts SET executor=$executor,executor_kind=$kind,worker_id=$worker,capability=$capability,generation=$generation,lease_id=$lease WHERE job_id=$id AND attempt_number=$attempt AND completed_at IS NULL";
        Bind(row); row.Parameters.AddWithValue("$kind", executorKind); await row.ExecuteNonQueryAsync(cancellationToken); await tx.CommitAsync(cancellationToken); return true;
        void Bind(SqliteCommand command) { command.Parameters.AddWithValue("$executor", executorKind == "remote" ? "remote" : "local"); command.Parameters.AddWithValue("$worker", Db(workerId)); command.Parameters.AddWithValue("$capability", Db(capability)); command.Parameters.AddWithValue("$generation", Db(generation)); command.Parameters.AddWithValue("$lease", Db(leaseId)); command.Parameters.AddWithValue("$id", Id(jobId)); command.Parameters.AddWithValue("$running", (int)PlatformJobStatusV1.Running); command.Parameters.AddWithValue("$attempt", attempt); }
    }
    public async ValueTask FailAsync(Guid jobId, int attempt, PlatformJobAttemptOutcomeV1 outcome, string errorSummary, DateTimeOffset nextAttemptAt, CancellationToken cancellationToken)
    { var status = outcome == PlatformJobAttemptOutcomeV1.Cancelled ? PlatformJobStatusV1.Cancelled : outcome == PlatformJobAttemptOutcomeV1.Recovered ? PlatformJobStatusV1.Pending : nextAttemptAt == DateTimeOffset.MaxValue ? PlatformJobStatusV1.Failed : PlatformJobStatusV1.Pending; _ = await FinishAsync(jobId, attempt, status, outcome, errorSummary, null, cancellationToken, nextAttemptAt); }
    private async ValueTask<bool> FinishAsync(Guid jobId, int attempt, PlatformJobStatusV1 desired, PlatformJobAttemptOutcomeV1 outcome, string? error, string? result, CancellationToken ct, DateTimeOffset? next = null)
    {
        await using var c = await OpenAsync(ct); await using var tx = c.BeginTransaction(deferred: false); int max;
        await using (var read = c.CreateCommand()) { read.Transaction = tx; read.CommandText = "SELECT max_attempts FROM platform_jobs WHERE job_id=$id AND status=$running AND attempt_count=$attempt"; read.Parameters.AddWithValue("$id", Id(jobId)); read.Parameters.AddWithValue("$running", (int)PlatformJobStatusV1.Running); read.Parameters.AddWithValue("$attempt", attempt); var v = await read.ExecuteScalarAsync(ct); if (v is null) { await tx.RollbackAsync(ct); return false; } max = Convert.ToInt32(v, CultureInfo.InvariantCulture); }
        var final = desired == PlatformJobStatusV1.Pending && attempt >= max ? PlatformJobStatusV1.DeadLettered : desired; var completed = DateTimeOffset.UtcNow;
        int changed;
        await using (var q = c.CreateCommand()) { q.Transaction = tx; q.CommandText = "UPDATE platform_jobs SET status=$status,completed_at=$completed,scheduled_at=$scheduled,result_json=$result,error_summary=$error,lease_expires_at=NULL,cancel_requested=0,terminal_audit_recorded=CASE WHEN $status IN ($failed,$dead) THEN 0 ELSE terminal_audit_recorded END WHERE job_id=$id AND status=$running AND attempt_count=$attempt AND ($outcome<>$success OR cancel_requested=0)"; q.Parameters.AddWithValue("$status", (int)final); q.Parameters.AddWithValue("$failed", (int)PlatformJobStatusV1.Failed); q.Parameters.AddWithValue("$dead", (int)PlatformJobStatusV1.DeadLettered); q.Parameters.AddWithValue("$completed", final == PlatformJobStatusV1.Pending ? DBNull.Value : Dt(completed)); q.Parameters.AddWithValue("$scheduled", Dt(final == PlatformJobStatusV1.Pending ? next ?? completed : completed)); q.Parameters.AddWithValue("$result", Db(result)); q.Parameters.AddWithValue("$error", Db(error)); q.Parameters.AddWithValue("$id", Id(jobId)); q.Parameters.AddWithValue("$running", (int)PlatformJobStatusV1.Running); q.Parameters.AddWithValue("$attempt", attempt); q.Parameters.AddWithValue("$outcome", (int)outcome); q.Parameters.AddWithValue("$success", (int)PlatformJobAttemptOutcomeV1.Succeeded); changed = await q.ExecuteNonQueryAsync(ct); }
        if (changed == 0) { await tx.RollbackAsync(ct); return false; }
        await using (var q = c.CreateCommand()) { q.Transaction = tx; q.CommandText = "UPDATE platform_job_attempts SET completed_at=$completed,outcome=$outcome,error_summary=$error WHERE job_id=$id AND attempt_number=$attempt AND completed_at IS NULL"; q.Parameters.AddWithValue("$completed", Dt(completed)); q.Parameters.AddWithValue("$outcome", (int)outcome); q.Parameters.AddWithValue("$error", Db(error)); q.Parameters.AddWithValue("$id", Id(jobId)); q.Parameters.AddWithValue("$attempt", attempt); await q.ExecuteNonQueryAsync(ct); }
        await tx.CommitAsync(ct); return true;
    }
    public async ValueTask<bool> RequestCancelAsync(Guid jobId, CancellationToken cancellationToken)
    { await using var c = await OpenAsync(cancellationToken); await using var tx = c.BeginTransaction(deferred: false); var now = Dt(DateTimeOffset.UtcNow); await using var q = c.CreateCommand(); q.Transaction = tx; q.CommandText = "UPDATE platform_jobs SET status=CASE WHEN status=$pending THEN $cancelled ELSE status END,completed_at=CASE WHEN status=$pending THEN $now ELSE completed_at END,cancel_requested=CASE WHEN status=$running THEN 1 ELSE cancel_requested END WHERE job_id=$id AND status IN ($pending,$running)"; q.Parameters.AddWithValue("$pending", (int)PlatformJobStatusV1.Pending); q.Parameters.AddWithValue("$running", (int)PlatformJobStatusV1.Running); q.Parameters.AddWithValue("$cancelled", (int)PlatformJobStatusV1.Cancelled); q.Parameters.AddWithValue("$now", now); q.Parameters.AddWithValue("$id", Id(jobId)); var changed = await q.ExecuteNonQueryAsync(cancellationToken) == 1; await tx.CommitAsync(cancellationToken); return changed; }
    public async ValueTask<bool> IsCancellationRequestedAsync(Guid jobId, CancellationToken cancellationToken)
    { await using var c = await OpenAsync(cancellationToken); await using var q = c.CreateCommand(); q.CommandText = "SELECT cancel_requested FROM platform_jobs WHERE job_id=$id AND status=$running"; q.Parameters.AddWithValue("$id", Id(jobId)); q.Parameters.AddWithValue("$running", (int)PlatformJobStatusV1.Running); return Convert.ToInt32(await q.ExecuteScalarAsync(cancellationToken) ?? 0, CultureInfo.InvariantCulture) != 0; }
    public async ValueTask<bool> RequeueAsync(Guid jobId, CancellationToken cancellationToken)
    { await using var c = await OpenAsync(cancellationToken); await using var q = c.CreateCommand(); q.CommandText = "UPDATE platform_jobs SET status=$pending,scheduled_at=$now,completed_at=NULL,error_summary=NULL,max_attempts=MAX(max_attempts,attempt_count+1),terminal_audit_recorded=0 WHERE job_id=$id AND status IN ($failed,$dead)"; q.Parameters.AddWithValue("$pending", (int)PlatformJobStatusV1.Pending); q.Parameters.AddWithValue("$now", Dt(DateTimeOffset.UtcNow)); q.Parameters.AddWithValue("$id", Id(jobId)); q.Parameters.AddWithValue("$failed", (int)PlatformJobStatusV1.Failed); q.Parameters.AddWithValue("$dead", (int)PlatformJobStatusV1.DeadLettered); return await q.ExecuteNonQueryAsync(cancellationToken) == 1; }
    public async ValueTask<IReadOnlyList<PlatformJobV1>> ListPendingTerminalAuditsAsync(int limit, CancellationToken cancellationToken)
    { await using var c = await OpenAsync(cancellationToken); await using var q = c.CreateCommand(); q.CommandText = "SELECT * FROM platform_jobs WHERE status IN ($failed,$dead) AND terminal_audit_recorded=0 ORDER BY completed_at LIMIT $limit"; q.Parameters.AddWithValue("$failed", (int)PlatformJobStatusV1.Failed); q.Parameters.AddWithValue("$dead", (int)PlatformJobStatusV1.DeadLettered); q.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 200)); var result = new List<PlatformJobV1>(); await using var r = await q.ExecuteReaderAsync(cancellationToken); while (await r.ReadAsync(cancellationToken)) result.Add(ReadJob(r)); return result; }
    public async ValueTask MarkTerminalAuditRecordedAsync(Guid jobId, CancellationToken cancellationToken)
    { await using var c = await OpenAsync(cancellationToken); await using var q = c.CreateCommand(); q.CommandText = "UPDATE platform_jobs SET terminal_audit_recorded=1 WHERE job_id=$id AND status IN ($failed,$dead)"; q.Parameters.AddWithValue("$id", Id(jobId)); q.Parameters.AddWithValue("$failed", (int)PlatformJobStatusV1.Failed); q.Parameters.AddWithValue("$dead", (int)PlatformJobStatusV1.DeadLettered); await q.ExecuteNonQueryAsync(cancellationToken); }
    public async ValueTask<PlatformJobHealthV1> GetHealthAsync(bool dispatcherActive, CancellationToken cancellationToken)
    { await using var c = await OpenAsync(cancellationToken); await using var q = c.CreateCommand(); q.CommandText = "SELECT SUM(status=$pending),SUM(status=$running),SUM(status=$succeeded),SUM(status=$failed),SUM(status=$cancelled),SUM(status=$dead),MIN(CASE WHEN status=$pending AND execution_policy=2 THEN scheduled_at END) FROM platform_jobs"; q.Parameters.AddWithValue("$pending", (int)PlatformJobStatusV1.Pending); q.Parameters.AddWithValue("$running", (int)PlatformJobStatusV1.Running); q.Parameters.AddWithValue("$succeeded", (int)PlatformJobStatusV1.Succeeded); q.Parameters.AddWithValue("$failed", (int)PlatformJobStatusV1.Failed); q.Parameters.AddWithValue("$cancelled", (int)PlatformJobStatusV1.Cancelled); q.Parameters.AddWithValue("$dead", (int)PlatformJobStatusV1.DeadLettered); await using var r = await q.ExecuteReaderAsync(cancellationToken); await r.ReadAsync(cancellationToken); static long Count(SqliteDataReader row, int index) => row.IsDBNull(index) ? 0 : row.GetInt64(index); return new(true, dispatcherActive, Count(r,0), Count(r,1), Count(r,2), Count(r,3), Count(r,4), Count(r,5), r.IsDBNull(6) ? null : Parse(r.GetString(6))); }

    public MaintenanceOwnerIdV1 OwnerId => new("platform.jobs");
    public RetentionCapabilitiesV1 Capabilities => new(true, false, false, 500, true);

    public async ValueTask<RetentionPlanV1> PlanAsync(RetentionPolicyV1 policy, DateTimeOffset evaluatedAtUtc,
        DateTimeOffset expiresAtUtc, CancellationToken cancellationToken = default)
    {
        ValidatePlanRequest(policy, evaluatedAtUtc, expiresAtUtc);
        if (evaluatedAtUtc > clock.GetUtcNow())
            throw new InvalidOperationException("Retention evaluation cannot use a future clock.");
        if (!policy.Enabled)
            return new(Guid.NewGuid(), OwnerId, policy, evaluatedAtUtc, expiresAtUtc, 0, 0, null,
                null, null, "Job retention is disabled.", []);
        await using var c = await OpenAsync(cancellationToken);
        await using var q = c.CreateCommand();
        q.CommandText = $"SELECT COUNT(*),MIN(completed_at),MAX(completed_at) FROM platform_jobs WHERE {EligibleSql}";
        BindEligibility(q, policy, evaluatedAtUtc);
        await using var r = await q.ExecuteReaderAsync(cancellationToken);
        await r.ReadAsync(cancellationToken);
        var candidates = r.GetInt64(0);
        DateTimeOffset? oldest = r.IsDBNull(1) ? null : Parse(r.GetString(1));
        DateTimeOffset? newest = r.IsDBNull(2) ? null : Parse(r.GetString(2));
        await r.DisposeAsync();
        await using var count = c.CreateCommand(); count.CommandText = "SELECT COUNT(*) FROM platform_jobs";
        var total = Convert.ToInt64(await count.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
        await using var tombstones = c.CreateCommand();
        tombstones.CommandText = "SELECT COUNT(*),SUM(CASE WHEN expires_at<=$now THEN 1 ELSE 0 END) FROM platform_job_idempotency_tombstones";
        tombstones.Parameters.AddWithValue("$now", Dt(evaluatedAtUtc));
        await using var tombstoneRows = await tombstones.ExecuteReaderAsync(cancellationToken);
        await tombstoneRows.ReadAsync(cancellationToken);
        var allTombstones = tombstoneRows.GetInt64(0);
        var expiredTombstones = tombstoneRows.IsDBNull(1) ? 0 : tombstoneRows.GetInt64(1);
        return new(Guid.NewGuid(), OwnerId, policy, evaluatedAtUtc, expiresAtUtc, candidates + expiredTombstones,
            total - candidates + allTombstones - expiredTombstones, null, oldest, newest, "Terminal Job history older than the configured age and expired idempotency tombstones; failed Jobs require recorded audit.",
            ["Idempotency keys remain reserved until completion plus the configured horizon; history removal cannot undo handler side effects."]);
    }

    public async ValueTask<RetentionBatchResultV1> ExecuteBatchAsync(RetentionPlanV1 plan,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.Owner != OwnerId || !plan.Policy.Enabled) throw new InvalidOperationException("Job retention plan is not executable.");
        ValidatePlanRequest(plan.Policy, plan.EvaluatedAtUtc, plan.ExpiresAtUtc);
        var now = clock.GetUtcNow();
        if (now < plan.EvaluatedAtUtc || now > plan.ExpiresAtUtc)
            throw new InvalidOperationException("Job retention plan is stale or the clock moved backwards.");
        await using var c = await OpenAsync(cancellationToken);
        await using var tx = c.BeginTransaction(deferred: false);
        var rows = new List<(string Id, string Type, string? Key, int Status, DateTimeOffset Completed)>();
        await using (var q = c.CreateCommand())
        {
            q.Transaction = tx;
            q.CommandText = $"SELECT job_id,job_type,idempotency_key,status,completed_at FROM platform_jobs WHERE {EligibleSql} ORDER BY completed_at,job_id LIMIT $limit";
            BindEligibility(q, plan.Policy, plan.EvaluatedAtUtc);
            q.Parameters.AddWithValue("$limit", plan.Policy.BatchSize);
            await using var reader = await q.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                rows.Add((reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2),
                    reader.GetInt32(3), Parse(reader.GetString(4))));
        }
        var affected = 0;
        foreach (var row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Every row is revalidated in the write transaction, including audit and terminal state.
            await using var removeAttempts = c.CreateCommand(); removeAttempts.Transaction = tx;
            removeAttempts.CommandText = $"DELETE FROM platform_job_attempts WHERE job_id IN (SELECT job_id FROM platform_jobs WHERE job_id=$id AND {EligibleSql})";
            removeAttempts.Parameters.AddWithValue("$id", row.Id);
            BindEligibility(removeAttempts, plan.Policy, plan.EvaluatedAtUtc);
            await removeAttempts.ExecuteNonQueryAsync(cancellationToken);
            await using var removeJob = c.CreateCommand(); removeJob.Transaction = tx;
            removeJob.CommandText = $"DELETE FROM platform_jobs WHERE job_id=$id AND {EligibleSql}";
            removeJob.Parameters.AddWithValue("$id", row.Id);
            BindEligibility(removeJob, plan.Policy, plan.EvaluatedAtUtc);
            if (await removeJob.ExecuteNonQueryAsync(cancellationToken) != 1) continue;
            affected++;
            if (row.Key is null) continue;
            DateTimeOffset expires;
            try { expires = row.Completed + idempotencyHorizon; }
            catch (ArgumentOutOfRangeException) { expires = DateTimeOffset.MaxValue; }
            if (expires <= now) continue;
            await using var tombstone = c.CreateCommand(); tombstone.Transaction = tx;
            tombstone.CommandText = "INSERT INTO platform_job_idempotency_tombstones(job_type,idempotency_key,job_id,status,completed_at,expires_at) VALUES($type,$key,$id,$status,$completed,$expires)";
            tombstone.Parameters.AddWithValue("$type", row.Type); tombstone.Parameters.AddWithValue("$key", row.Key);
            tombstone.Parameters.AddWithValue("$id", row.Id); tombstone.Parameters.AddWithValue("$status", row.Status);
            tombstone.Parameters.AddWithValue("$completed", Dt(row.Completed)); tombstone.Parameters.AddWithValue("$expires", Dt(expires));
            await tombstone.ExecuteNonQueryAsync(cancellationToken);
        }
        var selectedTombstones = 0;
        if (rows.Count < plan.Policy.BatchSize)
        {
            var remaining = plan.Policy.BatchSize - rows.Count;
            await using var expired = c.CreateCommand(); expired.Transaction = tx;
            expired.CommandText = "SELECT job_type,idempotency_key FROM platform_job_idempotency_tombstones WHERE expires_at<=$now ORDER BY expires_at,job_type,idempotency_key LIMIT $limit";
            expired.Parameters.AddWithValue("$now", Dt(plan.EvaluatedAtUtc));
            expired.Parameters.AddWithValue("$limit", remaining);
            var keys = new List<(string Type, string Key)>();
            await using (var reader = await expired.ExecuteReaderAsync(cancellationToken))
                while (await reader.ReadAsync(cancellationToken)) keys.Add((reader.GetString(0), reader.GetString(1)));
            selectedTombstones = keys.Count;
            foreach (var (type, key) in keys)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await using var remove = c.CreateCommand(); remove.Transaction = tx;
                remove.CommandText = "DELETE FROM platform_job_idempotency_tombstones WHERE job_type=$type AND idempotency_key=$key AND expires_at<=$now";
                remove.Parameters.AddWithValue("$type", type); remove.Parameters.AddWithValue("$key", key);
                remove.Parameters.AddWithValue("$now", Dt(plan.EvaluatedAtUtc));
                affected += await remove.ExecuteNonQueryAsync(cancellationToken);
            }
        }
        await tx.CommitAsync(cancellationToken);
        var selected = rows.Count + selectedTombstones;
        return new(selected, affected, null, selected == plan.Policy.BatchSize, selected - affected);
    }

    private const string EligibleSql = "completed_at IS NOT NULL AND completed_at<=$cutoff AND status IN (2,3,4,5) AND (status NOT IN (3,5) OR terminal_audit_recorded=1) AND lease_expires_at IS NULL";
    private static void BindEligibility(SqliteCommand command, RetentionPolicyV1 policy, DateTimeOffset evaluatedAtUtc)
        => command.Parameters.AddWithValue("$cutoff", Dt(evaluatedAtUtc - policy.MinimumAge!.Value));
    private void ValidatePlanRequest(RetentionPolicyV1 policy, DateTimeOffset evaluatedAtUtc, DateTimeOffset expiresAtUtc)
    {
        policy.Validate(Capabilities);
        if (policy.Enabled && policy.MinimumAge is null) throw new ArgumentException("Job retention requires an explicit minimum age.", nameof(policy));
        if (policy.MinimumAge is { } age && evaluatedAtUtc < DateTimeOffset.MinValue.Add(age))
            throw new ArgumentException("Job retention age underflows the UTC clock.", nameof(policy));
        if (evaluatedAtUtc.Offset != TimeSpan.Zero || expiresAtUtc.Offset != TimeSpan.Zero ||
            expiresAtUtc <= evaluatedAtUtc || expiresAtUtc - evaluatedAtUtc > TimeSpan.FromMinutes(10))
            throw new ArgumentException("Job retention plan requires a UTC evaluation and expiry within ten minutes.");
    }

    private async ValueTask<SqliteConnection> OpenAsync(CancellationToken ct) { var c = new SqliteConnection(connectionString); await c.OpenAsync(ct); return c; }
    private static PlatformJobV1 ReadJob(SqliteDataReader r) => new(Guid.Parse(r.GetString(0)),r.GetString(1),(PlatformJobStatusV1)r.GetInt32(2),Parse(r.GetString(3)),Parse(r.GetString(4)),NullableDate(r,5),NullableDate(r,6),r.GetInt32(7),r.GetInt32(8),r.IsDBNull(9)?null:r.GetInt32(9),r.IsDBNull(10)?null:r.GetString(10),r.IsDBNull(11)?null:r.GetString(11),r.IsDBNull(12)?null:r.GetString(12),r.GetString(13),r.IsDBNull(14)?null:r.GetString(14),r.IsDBNull(15)?null:r.GetString(15),r.GetString(16),NullableDate(r,17),(PlatformJobExecutionPolicyV1)r.GetInt32(20),r.IsDBNull(21)?null:r.GetString(21),r.IsDBNull(22)?null:r.GetString(22),r.IsDBNull(23)?null:r.GetInt64(23),r.IsDBNull(24)?null:r.GetString(24), r.IsDBNull(r.GetOrdinal("trace_parent"))?null:r.GetString(r.GetOrdinal("trace_parent")), r.IsDBNull(r.GetOrdinal("trace_state"))?null:r.GetString(r.GetOrdinal("trace_state")));
    private static DateTimeOffset? NullableDate(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : Parse(r.GetString(i));
    private static DateTimeOffset Parse(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
    private static string Dt(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    private static string Id(Guid value) => value.ToString("D");
    private static object Db(object? value) => value ?? DBNull.Value;

}
