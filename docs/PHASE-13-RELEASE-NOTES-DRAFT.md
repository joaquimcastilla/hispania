# HISPANIA Phase 13 — Platform Maturity

Draft only. No tag, GitHub Release or merge is created. Publish only after final CI and separate authorization.

## Identity & Authorization

Persistent users, PBKDF2 credentials, hashed opaque sessions, expiry/revocation, roles, permissions and one-time atomic bootstrap. Domain and Admin APIs enforce server-side grants; Web handles login, isolated permissions and expired sessions. Legacy admin bearer remains restricted to documented compatibility routes.

## Durable Events

Independent Platform-owned Event store, bounded registered JSON envelopes, stable subscribers, per-subscriber attempts, retries/dead-letter/replay and restart lease recovery. Delivery remains at-least-once.

## Jobs

Independent durable Background Jobs, registered trusted handlers, scheduling, idempotency keys, attempts, cooperative cancellation/timeouts, retry/dead-letter and restart recovery. Job state remains owned by Platform regardless of executor.

## Remote execution

Allowlisted Platform capabilities use the existing generic WorkerAgent and mTLS coordinator transport. Remote-only/preferred policies, leases, generations, fencing and worker crash/restart recovery preserve Job ownership. Media FFmpeg remains a separate provider and retains its existing local/remote regression coverage.

## Storage

Opaque provider handles, resolver contracts and initial filesystem provider; legacy handle compatibility, traversal/reparse protection, streamed content and explicit compensation/orphan semantics. Assets own metadata and Audit records administrative changes. Inventory retains opaque AssetRefs and its own database.

## Observability

Correlation/causation and W3C context across HTTP, Events, Jobs and WorkerAgent; aggregate runtime metrics; public liveness/readiness and protected detailed health. Safe degraded summaries recover after store faults. Hardening sanitizes HTTP failure responses and removes raw provider/extension exception material from failure logs.

## Admin

Static declarative shell for Overview, Users, Roles, Events, Jobs, Observability, Assets and Modules, with Modern/Classic parity. Direct routes and mutations are permission protected; session expiry removes loaded administrative data. Manual refresh and bounded lists; ID-only Asset lookup and honest unavailable module generation.

## Validation

Final acceptance requires Linux/Windows Validation for the exact closure SHA, Platform-only and complete supported builds/suites, PostgreSQL-live Linux, Web/Modern/Classic, architectural boundaries, real production-server and executor crash recovery, mTLS workers, FFmpeg and Media/Inventory regression. The shared closure gate also requires six adversarial error cases and 20 repetitions of named concurrency tests with no missing discovery, failures or skips. Exact results and workflow link are supplied by the final closure report.

## Known limitations

At-least-once Events/Jobs; no exactly-once or cross-store atomicity; single coordinator; trusted-code execution rather than a hostile-code sandbox; runtime-only metrics without TSDB/exporter; initial filesystem storage without remote object storage; static frontend loading; partial legacy bearer and public diagnostic compatibility; no SSO/MFA, cron, Notifications or full ERP. This phase introduces no new domain features and does not start Phase 14.
