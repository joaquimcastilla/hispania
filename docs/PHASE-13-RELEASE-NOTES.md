# HISPANIA Phase 13 — Platform Maturity

**Phase 13 — CLOSED — validated on Linux and Windows**

Functional validated SHA: `75e10edd560e396a6e509a386d5aae5dd7928827`. [Validation #37230450104](https://github.com/joaquimcastilla/hispania/actions/runs/37230450104) completed successfully on Linux and Windows. The subsequent closure commit updates documentation only; `phase-13-complete` identifies that final checkpoint. Merge to `main` remains a separate task; Phase 14 has not started.

## Identity & Authorization

Persistent users, PBKDF2 credentials, hashed opaque sessions, expiry/revocation, roles, permissions and one-time atomic bootstrap. Authorization denies missing grants by default. Media and Inventory permissions support partial domain and administrative grants. Domain and Admin APIs enforce server-side grants; Web handles login, isolated permissions and expired sessions. Legacy admin bearer remains restricted to documented compatibility routes.

## Durable Events

Independent Platform-owned Event store, bounded registered JSON envelopes, stable subscribers, per-subscriber attempts, retries/dead-letter/replay and restart lease recovery. Delivery remains at-least-once.

## Background Jobs

Independent durable Background Jobs, registered trusted handlers, scheduling, idempotency keys, attempts, cooperative cancellation/timeouts, retry/dead-letter and restart recovery. Job state remains owned by Platform regardless of executor.

## Remote Job Execution

Allowlisted Platform capabilities use the existing generic WorkerAgent and mTLS coordinator transport. `LocalOnly`, `RemotePreferred` and `RemoteOnly` policies, leases, generations, fencing and worker crash/restart recovery preserve Job ownership. Media FFmpeg remains a separate provider and retains its existing local/remote regression coverage.

## Storage

Opaque provider handles, resolver contracts and initial filesystem provider; legacy handle compatibility, traversal/reparse protection, streamed content and explicit compensation/orphan semantics. Assets own metadata and Audit records administrative changes. Inventory retains opaque AssetRefs and its own database.

## Observability

Correlation/causation and W3C context across HTTP, Events, Jobs and WorkerAgent; ActivitySource/tracing and aggregate runtime metrics; public liveness/readiness and protected detailed health. Safe degraded summaries recover after store faults. Hardening sanitizes HTTP failure responses and removes raw provider/extension exception material from failure logs.

## Platform Admin

Static declarative shell for Overview, Users, Roles, Events, Jobs, Observability, Assets and Modules, with Modern/Classic parity. Direct routes and mutations are permission protected; session expiry removes loaded administrative data. Manual refresh and bounded lists; ID-only Asset lookup and honest unavailable module generation.

## Hardening & Validation

Evidence below applies to the functional SHA linked above; the final checkpoint adds documentation only.

| Validation evidence | Result |
|---|---|
| Linux CrossPlatform .NET | 485 passed, 0 failed, 0 skipped |
| Windows full .NET | 542 passed, 0 failed, 1 existing PostgreSQL-live skip |
| PostgreSQL-live | 1/1 on Linux; Persistence 66/66 |
| Inventory/Platform regression | 53/53 on each OS |
| Closure gate | 210/210 on each OS: 4 process/architecture cases, 6 safe-error cases, 200 stress passes |
| Concurrency stress | 20 repetitions on each OS; no failures, skips or retry-to-green |
| Playback / distributed WorkerAgent controls | 99/99 / 10/10 |
| Observability | 17/17 |
| Web / focused Admin Web | 74/74 / 39/39 on each OS |
| Admin HTTP | 2/2 on each OS |
| Platform-only / Modern / Classic builds | Passed on Linux and Windows |
| FFmpeg / Planner lifecycle | 6/6 / 6/6 on each OS |
| Windows Job Object | 1/1 |
| Build warnings / errors / GitHub annotations | 0 / 0 / 0 |

Counts overlap across focused gates and full suites; they must not be added into a unique-test total. Real server graceful/abrupt restart and killed executor recovery passed alongside mTLS WorkerAgent crash/restart, leases and fencing. Full CI log review found no real secret material. The sole Windows PostgreSQL-live skip is covered by the required Linux live test.

Hardening excludes raw provider/extension exception messages, physical paths and credentials from failure responses/logs and public module health. Recursive project references and compiled-assembly checks enforce Platform and generic WorkerAgent independence from Media/Inventory implementations.

## Known limitations

At-least-once Events/Jobs; no exactly-once or cross-store atomicity; single coordinator; no multi-master, consensus or general distributed HA; trusted-code execution rather than a hostile-code sandbox; runtime-only metrics without TSDB/exporter; initial filesystem storage without remote object storage; static frontend loading; partial legacy bearer and public diagnostic compatibility; no SSO/MFA, cron, Notifications or full ERP. This phase introduces no new domain features and does not start Phase 14.
