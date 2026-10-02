# ADR-0034: Platform durable background Jobs

Status: Accepted (Phase 13.5 V1).

## Decision and boundary

Platform owns a general durable Job mechanism. An Event is a fact that happened; a Job is work that should be performed. Durable Events and Jobs use separate contracts, dispatchers, SQLite files, tables, retry state, and administrative APIs. An Event may cause a Job through a future explicit contract integration; neither subsystem substitutes for the other.

V1 executes only on the local Platform dispatcher. The server composition root explicitly registers trusted stable JobType identifiers to typed handlers. Persisted data never names CLR types, assemblies, commands, scripts, or executables. The demo `platform.test.hash` handler accepts text and only `sha256` or `sha512`, returning small result metadata. WorkerAgent has no Jobs implementation dependency; remote execution belongs to a later phase.

## Contracts and state

The versioned Job model contains JobId, stable JobType, status, creation/scheduled/start/completion timestamps, attempt and maximum counts, optional timeout, correlation/causation and idempotency identifiers, bounded JSON payload, bounded result metadata, safe error summary, local executor identity, and an execution lease. Public list views omit payload. Administrative detail also omits payload.

Public states are Pending, Running, Succeeded, Failed, Cancelled, and DeadLettered. Failed means a handler rejected the input or raised a non-retryable data/JSON error; an administrator may requeue it. DeadLettered means a retryable error or timeout consumed MaxAttempts; explicit administrative retry adds one permitted attempt and retains the existing attempt history. Pending includes scheduled or backoff work. `CompletedAt` is set only for a terminal state.

## Persistence, scheduling, and acquisition

Platform persistence owns `jobs.sqlite`, separate from Events, Identity, Inventory, and Media. `platform_jobs` stores Job records and `platform_job_attempts` stores append-style attempt rows with local executor identity and outcome. Creation and `(JobType, IdempotencyKey)` uniqueness are enforced in a short SQLite write transaction. The scope is the exact stable JobType plus key for the lifetime of its Job row; the duplicate response returns the original JobId even after completion. V1 performs no automatic deletion, so that key remains reserved until an operator-managed future retention policy removes the Job.

The store uses SQLite WAL, a busy timeout, and short immediate transactions for acquisition/finalization. Acquisition selects due Pending work or Running work with an expired lease, atomically increments AttemptCount, records an attempt, marks Running, and sets a lease. Handler execution occurs outside the transaction. The executor periodically renews the lease and commits the result/attempt outcome in a later short transaction. Competing executors coordinate through SQLite; in-memory locks are not the ownership guarantee. One local dispatcher is hosted by the server in V1; no multi-coordinator guarantee is claimed.

`ScheduledAt` is an absolute timestamp. Jobs run immediately by default or at a future time. Retry delay is exponential from the configured base and capped at five minutes. There is no recurrence or cron support.

## Delivery semantics, retry, cancellation, and timeout

Execution is **at-least-once**. If a process dies after a handler's external side effect and before its result commits, a later executor may perform the effect again. Handlers whose effects require deduplication must use their own idempotency/invariant. The Job idempotency key prevents duplicate Job creation; it does not make handler effects exactly-once.

At startup/dispatch, a stale Running lease is recovered: the unfinished prior attempt is marked Recovered, the Job becomes eligible for another attempt, and a new local attempt is created. V1 cannot determine whether an external effect occurred before the crash. Lease renewal allows a long healthy handler to continue beyond the initial lease.

Pending cancellation is immediate. Running cancellation first persists a cancellation request and then signals the local handler token; acquisition/finalization transactions close the race between request and execution. Cancellation is cooperative. A handler that ignores its token cannot be forcibly terminated, and its lease continues to renew while it remains in-process. Per-Job timeout requests cancellation and records a TimedOut attempt; retry/dead-letter policy then applies. No process is killed by timeout.

Result metadata is JSON bounded by configuration; large outputs belong in a future Asset/Storage capability and can be referenced with OpaqueResultRef. Error summaries contain only a bounded exception type summary, not arbitrary exception messages or payload data. Full payloads never appear in admin lists or audit metadata.

## Security, administration, and limitations

Administrative HTTP operations require an enabled Identity Session and Platform RBAC: `platform.jobs.read` for list/detail/attempts/health and `platform.jobs.manage` for create/cancel/retry. The legacy admin bearer is explicitly rejected. Creation accepts only a registered JobType. JSON size, JobType syntax, attempt count, timeout and idempotency key length are validated; SQL is parameterized and type deserialization uses an explicitly registered payload type. No shell, executable, arbitrary DLL, or script handler exists.

The Web admin route is `/admin/jobs`; it shows status, schedule, attempts, local executor and safe error summary, with cancel/retry controls gated by manage permission. Job creation and administrative changes write Platform Audit metadata without storing the payload. `jobs.created` and `jobs.retried` are counters; status instruments `jobs.pending`, `jobs.running`, `jobs.succeeded`, `jobs.failed`, `jobs.cancelled`, and `jobs.dead_lettered` are observable gauges. The Jobs health endpoint degrades only when the store is inaccessible or dispatcher is inactive; a failed Job alone does not make the whole server unhealthy.

Automatic retention, recurrence, workflow/DAGs, dependencies, remote execution, WorkerAgent integration, external queues, distributed schedulers, multiple coordinators, exactly-once execution, and forced process termination are out of scope. Store directories and SQLite files must be protected by the host operator. See Phase 13.6 for a possible future WorkerAgent adapter over the contracts.
