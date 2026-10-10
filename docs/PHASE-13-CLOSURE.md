# Phase 13 — Hardening and closure audit

**Phase 13 — CLOSED — validated on Linux and Windows**

Branch: `feature/phase-13-10-hardening-closure`. Functional validated SHA: `75e10edd560e396a6e509a386d5aae5dd7928827`. [Validation #37230450104](https://github.com/joaquimcastilla/hispania/actions/runs/37230450104) completed successfully on Linux and Windows. The subsequent closure commit updates documentation only; `phase-13-complete` identifies that final checkpoint. Merge to `main` remains a separate task; Phase 14 has not started.

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

## Ownership and dependency audit

Platform owns Identity, Authorization, Durable Events, Background Jobs, remote execution contracts/adapters, Storage, Assets, Audit and Observability. The generic WorkerAgent loads explicitly trusted capability providers. Media and Inventory own their domain state and consume public Platform contracts; only the server composition root joins their implementations. There is no Platform-to-domain dependency. Domain implementation assemblies do not import each other or the Authorization application. Each domain decides business behavior; Platform supplies mechanisms.

[Every project and package reference](PHASE-13-DEPENDENCIES.md) was inventoried. The new recursive Platform project-graph test visits all references from `Hispania.Platform.slnf`, including transitive dependencies, and rejects domain/server/test implementations. Existing compiled-assembly tests verify Identity, Authorization, Events, Jobs, Storage, Observability, the remote adapter and WorkerAgent, plus Media/Inventory boundaries. The test-only crash host is outside Platform-only and never referenced by production.

Independent stores: `identity.sqlite` (Identity plus RBAC/bootstrap tables), `events.sqlite`, `jobs.sqlite`, `assets.sqlite`, `audit.sqlite`, Inventory SQLite, Media persistence and distributed coordinator state. Event and Job stores have independent schemas, lease/retry/attempt semantics and dispatchers. No cross-store FK or atomic domain/Event/Audit transaction is promised.

## Process, durability and recovery evidence

- `RealServerRestartsKeepInventoryAssetsAuditAndPlayableVideo`: real production server processes, user/login, current role grants, BP48 Inventory, opaque AssetRefs and content, playable video and Audit. It now also persists a scheduled Job and captures durable Event IDs in the same scenario. Graceful restart retains session/grants and committed state; cancellation is persisted; abrupt server termination retains that terminal Job, Event history, revoked session, Inventory stock, Assets and Audit. Health/readiness/Observability are checked around both restarts. Cancelled Jobs deliberately cannot be retried.
- `RealCrashRecoversRunningJobAndProcessingEventTogetherWithoutSharingStores`: a test-only real process uses production stores and commits a Running Job and Processing Event, acknowledges both leases, then is killed without cleanup. The production server starts against those files and recovers both to Succeeded/Delivered. The test asserts recovered/succeeded Job attempts, rejection of a stale attempt result, Event attempt count, SQLite integrity and separated schemas. This is an executor-process crash followed by production-server recovery; it does not pretend that the server's own normal hash handler was artificially blocked.
- `IndependentMtlsWorkerExecutesOopCapabilityAndRecoversRetrySafeJobWithFencing`: existing real WorkerAgent, mTLS, allowlisted Platform capability, crash/restart, lease/generation fencing and successful final remote Platform Job. Includes real FFmpeg/transport regression. Coordinator restart coverage remains required.
- Fixtures kill process trees on failure, wait for exit, stop HTTP hosts, dispose certificates/streams and delete isolated roots. No fixed port, private certificate or DB is committed. Port selection is loopback/ephemeral. Windows Job Objects and Linux process-limit regressions retain their existing platform gates.

Operations not committed before a crash are not guaranteed to survive. Events and Jobs remain at-least-once; handlers must tolerate repeated delivery/execution. One supported coordinator/dispatcher ownership model is retained.

## HTTP authorization inventory

| Surface | Classification and authority |
|---|---|
| Identity login | Public authentication; generic failure, bounded password input |
| Identity me/logout | Authenticated, valid `Session` only; bearer is not a session |
| Identity user creation | `platform.users.write`; bounded legacy bearer compatibility |
| Authorization bootstrap-admin | Separate configured Bootstrap secret; atomic, one-time; no default token |
| Authorization users, status, assignments | users.read / users.write; session permission checks or explicit legacy bearer compatibility |
| Authorization roles, updates, grants | roles.read / roles.write / roles.permissions.write; same explicit legacy compatibility |
| Jobs list/detail/attempts | `platform.jobs.read`, Session only |
| Jobs create/cancel/retry | `platform.jobs.manage`, Session only; registered types only |
| Event deliveries/health | `platform.events.read`, Session only; list excludes payload |
| Event raw detail/attempts/replay | `platform.events.manage`, Session only; raw detail is privileged |
| Observability/details | `platform.observability.read`, Session only |
| Consolidated Admin Overview/Assets/Modules | observability.read / assets.write / platform.admin, Session only; safe projections |
| Asset upload/delete | `platform.assets.write`; bounded legacy bearer compatibility |
| Asset metadata/content | Public only when Asset PublicRead policy allows; private assets require the configured legacy bearer (existing compatibility) |
| Audit query | `platform.audit.read`; bounded legacy bearer compatibility |
| Inventory reads | `inventory.read`, Session only |
| Inventory mutations | `inventory.write`; explicitly retained legacy bearer compatibility |
| Media catalog/metadata/stream | media.library.read / media.playback.use, Session only |
| Media metadata mutations/editor | `media.admin`, Session only |
| Media Asset playback/stream | `media.playback.use`, Session only |
| Worker transport | mTLS peer pin/node authorization plus assigned Job/lease/generation checks; no admin bearer bypass |
| Artwork delivery | Existing public cached artwork policy |
| healthz, readyz, api health/live and ready | Public coarse probes, no sensitive detail |
| internal metadata-enrichment, module-test, modules/health, capabilities/transcoding/health | Existing public diagnostic compatibility surface; not a new Admin API; product policy unchanged |
| test shutdown | Registered only with explicit Testing:ShutdownToken; separate fixed-time token |
| internal/admin/modules routes below | Existing bearer-only legacy administration, including advanced diagnostics/package/trust/worker/experiment operations |

All current endpoint files and Program mappings were reviewed. No new endpoint is authorized by session presence alone. The new error middleware does not bypass authentication or change successful route policy. The public module health projection excludes runtime Detail, which can contain provider/extension exception messages; descriptor/state/timestamps remain public. Its real HTTP regression injects credential/path material and proves exclusion. Public diagnostic compatibility endpoints and bearer-only module administration remain explicit limitations; the consolidated Admin projection is the safer session/RBAC surface.

## Exact legacy bearer inventory

Default `RequirePermission` permits the configured Modules:AdminToken only on the following routes; all other uses opt out explicitly:

- `POST /api/identity/users`.
- Authorization: `GET /api/authorization/users`, `PATCH /api/authorization/users/{userId}/status`, `GET /api/authorization/users/{userId}/roles`, `POST` and `DELETE /api/authorization/users/{userId}/roles/{roleId}`, `GET` and `POST /api/authorization/roles`, `PUT /api/authorization/roles/{roleId}`, `PUT` and `DELETE /api/authorization/roles/{roleId}/permissions/{permissionId}`.
- Inventory: `POST /api/inventory/items`, `POST /api/inventory/warehouses`, `POST /api/inventory/warehouses/{warehouseId}/locations`, `POST /api/inventory/receive`, `POST /api/inventory/issue`, `POST /api/inventory/transfer`, `POST /api/inventory/items/{itemId}/assets`, `DELETE /api/inventory/items/{itemId}/assets/{assetId}`.
- `POST /api/assets`, `DELETE /api/assets/{id}`, `GET /internal/admin/audit`.
- Explicit private-Asset bearer check: `GET /api/assets/{id}` and `GET /api/assets/{id}/content`; public Assets remain public by policy.

The module group has its own bearer-only filter:

- `GET /internal/admin/modules`
- `GET /internal/admin/modules/remote-workers`
- `POST /internal/admin/modules/remote-workers/{nodeId}/authorize`
- `PUT /internal/admin/modules/remote-workers/{nodeId}/certificate`
- `DELETE /internal/admin/modules/remote-workers/{nodeId}`
- `POST /internal/admin/modules/remote-workers/{nodeId}/enabled`
- `GET /internal/admin/modules/distributed-jobs`
- `GET /internal/admin/modules/extensions`
- `GET /internal/admin/modules/publishers`
- `PUT /internal/admin/modules/publishers/{publisherId}`
- `DELETE /internal/admin/modules/publishers/{publisherId}`
- `POST /internal/admin/modules/extensions/packages`
- `POST /internal/admin/modules/extensions/{id}/activate`
- `POST /internal/admin/modules/extensions/{id}/deactivate`
- `POST /internal/admin/modules/extensions/{id}/unload`
- `POST /internal/admin/modules/extensions/{id}/update/{version}`
- `POST /internal/admin/modules/extensions/{id}/uninstall/{version}`
- `GET /internal/admin/modules/extensions/{id}/greeting`
- `POST /internal/admin/modules/playback-planner/{targetModuleId}`
- `GET /internal/admin/modules/playback-experiments`
- `POST /internal/admin/modules/playback-experiments`
- `POST /internal/admin/modules/playback-experiments/{experimentId}/suspend`
- `POST /internal/admin/modules/playback-experiments/{experimentId}/variants/{moduleId}/suspend`
- `POST /internal/admin/modules/playback-experiments/{experimentId}/variants/{moduleId}/retire`
- `POST /internal/admin/modules/playback-experiments/{experimentId}/rollback`
- `POST /internal/admin/modules/playback-experiments/{experimentId}/finish`
- `GET /internal/admin/modules/playback-experiments/{experimentId}/metrics`

No bearer expansion occurred in 13.10. Jobs, Events, Observability, safe Admin projections, Inventory reads and Media routes reject legacy bearer. A bearer token cannot authenticate Identity me/logout or be substituted into the Session scheme.

## Credentials, bootstrap, errors and logging

Identity uses PBKDF2-HMAC-SHA256, 600,000 iterations, independent cryptographically random 16-byte salts and 32-byte hashes, fixed-time verification, and bounded password length. Opaque random session tokens are stored only as SHA256 hashes. Expiry/revocation and disabled users are checked by session validation. Existing tests cover reopen, generic failures, expiry, password change, disable/revoke, and concurrent username uniqueness.

Bootstrap requires explicit environment/operator configuration, checks the configured secret in fixed time, and commits the bootstrap marker/admin grant atomically. It is one-time under concurrent use; its SQLite table contains no secret. Tests inspect persisted schema and Audit output. There are no default production credentials, no fixed PostgreSQL password and no persisted bootstrap secret.

The logging audit covers Platform applications/persistence/storage, server endpoint filters/composition, remote adapter, worker hosting and core runtime failure paths. Structured Platform events log outcomes, bounded categories and correlation/resource IDs; none passes payload/password/session/Authorization values. New hardening removes raw exceptions and physical package directories from legacy module installation/operation logs and raw provider exceptions from background metadata/index failure logs. Module error bodies become generic while preserving 400/409/500 statuses. The legacy module filter now derives the actual result status before result execution, so returned 400/409 failures are audited as failed instead of incorrectly completed/200. A server error boundary returns coarse ProblemDetails and logs only exception type; six adversarial subsystem cases assert no password/token/root/SQL/connection-string/private-key/raw-payload markers in response or captured logs. If a response already started, the connection is aborted instead of appending an exception body.

Worker transport validates mTLS and lease authority; structured safe failures and existing remote correlation tests remain required. Configured paths and advanced legacy module diagnostics are operator-facing compatibility data, not part of the safe Admin projections. The global audit also checked input validation and dangerous logging patterns; no plaintext Authorization/password/private-key logging is intentionally accepted.

## Observability and Admin semantics

Liveness is a cheap process probe and stays healthy when one store fails. Readiness becomes 503 for an inaccessible critical store; dead-letter/unavailable-worker states can be Degraded. Detailed health is permission protected, sanitized, and recomputed so recovery returns Healthy. The Admin process fixture faults Events and Jobs independently, verifies live/ready behavior and safe degraded Overview, restores each store and verifies Healthy.

Metrics have a fixed finite name registry in `PlatformMetricNamesV1`; no tag API or per-user/job/event/asset/correlation metric names. Counters use aggregate counts; gauges report current totals; histograms use milliseconds. Correlation/resource IDs belong in traces/logs, never metric dimensions. No high-cardinality labels, TSDB, exporter or history store is introduced. W3C parent/trace-state plus correlation and immediate causation propagate through HTTP, Event envelopes, Jobs, remote envelopes/WorkerAgent and Audit; both sampled and unsampled flows have regression coverage.

Admin's static route registry retains the exact permission map from 13.9. Isolated Jobs/Events/Users readers, forbidden direct routes, read/manage mutations, 401 cleanup after already-loaded data, bounded error handling and safe projections are tested in Modern and Classic. Refresh is manual; Overview avoids duplicated current-user requests or aggressive polling. Headings, labels, buttons, navigation and focus behavior have basic coverage, without claiming a formal WCAG audit. Assets remains ID lookup; module generation stays unavailable rather than fabricated.

## Stress, storage and SQLite

`tools/test-phase13-concurrency.ps1` requires 20 passes of ten named tests (200 passes): Platform bootstrap, concurrent normalized role creation/assignment and role updates, username uniqueness, Job acquisition, concurrent Event publish/acquisition, concurrent opaque filesystem writes, Media metadata unique-index race, retry-safe fencing, competing workers/late results and cancellation fencing. It fails on missing discovery, failure or skip, with no retry-to-green or added sleeps. The first local run overlaps the complete suite, exercising independent SQLite fixtures under load. Successful concurrent store operations plus integrity checks are required; a `database is locked` exception or deadlock fails the run.

Storage regression also covers opaque and legacy handles, traversal, symlink/reparse protection, unknown provider, missing object, write compensation and metadata-first delete/orphan semantics. Symlink creation uses the existing OS privilege handling; no new skip is added. Provider writes clean partials; Assets own metadata, provider resolution and compensation. Delete may leave an orphan if content deletion fails after metadata removal; this is documented rather than hidden or silently redesigned.

## Validation and snapshot

Local candidate snapshot: .NET 542 passed, 0 failed, 1 pre-existing PostgreSQL-live skip; Inventory/Platform 53, Playback 99, Runtime 44, Web 74. Platform-only/full builds and Modern/Classic have zero build warnings/errors. The explicit closure gate has four process/architecture/concurrency tests plus six HTTP error cases and 20 stress iterations. These counts are a dated closure snapshot, not contractual constants.

`.github/workflows/validation.yml` retains Linux/Windows, Platform-only, complete supported solution tests, Web/Modern/Classic, named Admin/Observability/FFmpeg/distributed gates and PostgreSQL-live Linux (migrations, constraints and Media persistence). Both jobs invoke one shared closure script, enforcing discovery and no skips. Linux's CrossPlatform filter excludes Inventory, which is tested separately; Windows tests the full solution. The only existing PostgreSQL-live skip is permitted where no PostgreSQL service is configured; Linux must execute it with ephemeral infrastructure. Final functional CI results and the full-log review are recorded above. The later documentation-only commit does not change the tested implementation.

README, Architecture, Module System, Progress Log and ADR-0031 through ADR-0037 were reviewed. Historical phase-specific decisions remain historical; ADR-0034 now links the later remote extension, ADR-0032 no longer claims CI is pending, and README no longer denies implemented Identity/RBAC or Durable Events. ADR-0038 records closure decisions. [Definitive release notes](PHASE-13-RELEASE-NOTES.md) record the validated checkpoint.

## Final limitations

At-least-once Events and Jobs; no exactly-once or cross-store atomicity; one coordinator; no multi-master, consensus or general distributed HA; no hostile-code sandbox; trusted in-process code and trusted worker providers; runtime-only metrics without TSDB/exporter; initial filesystem provider without remote object storage; static frontend loading; partial legacy bearer compatibility and existing public diagnostics; no SSO/MFA, cron, Notifications or full ERP. No new provider, product/domain feature, dynamic module loading or Phase 14 work is part of closure.

## Final metric naming registry

Names from the finite public registry; no dynamic identifier suffixes or labels:

- `hispania.events.backlog.dead_lettered`
- `hispania.events.backlog.retrying`
- `hispania.events.dead_lettered`
- `hispania.events.delivered`
- `hispania.events.failed`
- `hispania.events.pending`
- `hispania.events.processing`
- `hispania.events.published`
- `hispania.events.retrying`
- `hispania.http.duration`
- `hispania.identity.login.failure`
- `hispania.identity.login.success`
- `hispania.identity.sessions.active`
- `hispania.jobs.cancelled`
- `hispania.jobs.created`
- `hispania.jobs.dead_lettered`
- `hispania.jobs.duration`
- `hispania.jobs.execution.local`
- `hispania.jobs.execution.remote`
- `hispania.jobs.failed`
- `hispania.jobs.pending`
- `hispania.jobs.remote.fenced_results`
- `hispania.jobs.remote.retried`
- `hispania.jobs.remote.unavailable`
- `hispania.jobs.retried`
- `hispania.jobs.running`
- `hispania.jobs.succeeded`
- `hispania.storage.deletes`
- `hispania.storage.duration`
- `hispania.storage.failures`
- `hispania.storage.read.failure`
- `hispania.storage.reads`
- `hispania.storage.write.failure`
- `hispania.storage.writes`
- `hispania.workers.capabilities`
- `hispania.workers.connected`
- `hispania.workers.executions`
- `hispania.workers.failures`
