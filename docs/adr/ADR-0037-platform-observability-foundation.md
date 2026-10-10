# ADR-0037: Platform Observability Foundation

- Status: Accepted for Phase 13.8 implementation
- Date: 2026-10-04
- Base: `06a33359f919de409f2ef378a19e9865bafe54de` (Phase 13.7)

## Decision and boundaries

Platform owns `CorrelationContextV1`, `IPlatformMetricsV1`, `IPlatformHealthV1`, `IPlatformHealthCheckV1`, and `PlatformTracingV1` in its existing contracts assembly. `Hispania.Platform.Observability.Application` supplies the runtime metric snapshot, health aggregation, storage instrumentation decorator, and Microsoft.Extensions.Logging conventions. Both assemblies are independent of Media and Inventory implementations. The Platform solution filter includes the application foundation.

The server is the composition root: it registers Identity, Events, Jobs, Storage and Workers checks. Its Media composition contributes library readiness through the same Platform health contract. Media and Inventory can contribute additional checks without Platform referencing their contracts or implementation assemblies. Inventory's existing event/audit adapters inherit HTTP correlation; this phase adds no business workflow or ERP feature.

## Correlation and causation

`CorrelationContextV1` has `CorrelationId`, optional `CausationId`, and optional `OperationId`, with optional W3C parent/state carriers. AsyncLocal scopes follow asynchronous execution and restore their predecessor on disposal. Correlation identifies a complete logical flow; causation identifies the immediate operation that caused an envelope; operation identifies the work currently executing.

HTTP uses `X-Correlation-ID`. IDs contain 1–128 ASCII letters/digits or `- _ . :`. Missing IDs are generated once. Multiple, oversized or invalid header values return 400. `Platform:Observability:AcceptClientCorrelation` defaults to true; false generates server IDs instead of trusting valid client values. IDs are opaque diagnostics, never authentication or authorization evidence. The canonical ID is returned in the response header and supplied to existing endpoint adapters. HTTP's request identifier is its operation ID.

Publish and create use explicit envelope metadata when provided, otherwise the ambient context. They do not put correlation into domain payload JSON. Event delivery establishes the event ID as the current operation. A Job created by that delivery inherits the same correlation and the event ID as causation. Job execution establishes the Job ID as its operation. Audit retains the correlation but remains a business/security record, not a technical log sink. Existing explicit IDs are retained; new Job metadata is validated rather than truncated.

Example: HTTP correlation A / operation R → Event E (correlation A, causation R) → Job J (correlation A, causation E) → Worker execution (correlation A, causation E, operation remote execution ID).

## Logs and security

Use Microsoft.Extensions.Logging, structured templates and operation scopes. Stable fields are `EventName` (or the corresponding named EventId), `CorrelationId`, `CausationId`, `Component`, `Operation`, safe `ResourceId`, `Outcome`, and `DurationMs` where meaningful. IDs are allowed in logs and traces, never aggregate metric labels. Stable diagnostic EventIds are JobStarted (13801), JobCompleted (13802), JobFailed (13803), EventDeadLettered (13804), WorkerConnected (13805), StorageFailure (13806), and LoginFailed (13807). The latter names are conventions/hooks for adapters; this phase does not rewrite all existing log sites.

Never log passwords, session/bearer tokens, bootstrap secrets, private keys, Authorization headers, credential hashes, or raw sensitive payloads. New instrumentation records IDs, fixed event names and aggregates only. Job handler errors remain type-only summaries; dispatcher iteration logs no longer forward arbitrary exception text. Invalid Job API responses use a fixed error code. Audit retains its existing bounded sensitive-key filtering. No universal content redactor is promised: adapters must select safe fields before logging. Regression tests cover a handler whose exception contains secrets and safe API/audit projections.

## Metrics V1

`PlatformMetricsV1` supplies a single `System.Diagnostics.Metrics.Meter` named `Hispania.Platform`, version 1.0.0, with counters, observable gauges and millisecond histograms. Counter/timing updates synchronize per instrument; gauges and value reads use Interlocked. Snapshots preserve each timing mean/count pair without a global metric lock; nonnegative counters/gauges and finite nonnegative timings are enforced. Snapshot histogram values are runtime arithmetic means plus counts, not stored distributions or percentiles.

There are no labels in V1. Names come from the immutable `PlatformMetricNamesV1` vocabulary; arbitrary names and identifier-derived names are rejected. Existing Job/Event metrics are replaced by the common instruments, not emitted in parallel under their old names.

| Component | Counter names (prefix `hispania.`) | Gauges / timings |
| --- | --- | --- |
| Identity | `identity.login.success`, `identity.login.failure` | `identity.sessions.active` |
| Events | `events.published`, `events.delivered`, `events.retrying`, `events.failed`, `events.dead_lettered` | `events.pending`, `events.processing`, `events.backlog.retrying`, `events.backlog.dead_lettered` |
| Jobs | `jobs.created`, `jobs.retried`, `jobs.execution.local`, `jobs.execution.remote`, `jobs.remote.unavailable`, `jobs.remote.retried`, `jobs.remote.fenced_results` | `jobs.pending`, `jobs.running`, `jobs.succeeded`, `jobs.failed`, `jobs.cancelled`, `jobs.dead_lettered`; `jobs.duration` |
| Storage | `storage.reads`, `storage.writes`, `storage.deletes`, `storage.failures`, `storage.read.failure`, `storage.write.failure` | `storage.duration` |
| Workers | `workers.executions`, `workers.failures` | `workers.connected`, `workers.capabilities` |
| HTTP | — | `http.duration` |

Event retry/dead-letter counters count transitions, while backlog gauges count current persisted rows. Job terminal gauges are reconstructed from its existing durable store. Active sessions exclude expired, revoked and disabled-user sessions. Worker executions/failures count coordinator-side Platform remote attempts; capability gauge counts distinct connected/authorized capabilities, not IDs. Storage counts provider calls (including failures); reads count opening streams, not later reads from a returned stream. Checks/snapshots refresh store and worker gauges; the existing Job dispatcher also refreshes its gauges. Gauges are observations, not globally transactional across stores.

Metrics are runtime-only: counters/timing aggregates reset on process restart, gauges are reconstructed on refresh, and no TSDB/history is provided. Metrics/exporter callbacks and production tracing scopes (sampling/start/stop listeners) are best effort and must not interrupt committed Events, Jobs, Identity or Storage work. Validation of the metric vocabulary is strict at the contract boundary; service adapters invoke sinks through the best-effort helper. Logging/tracing have no new durable I/O requirement. No payload serialization or persistence transaction is added solely for metrics.

## Health, liveness and readiness

Health is recomputed on every request, not sticky. Overall status is the worst registered check. Identity/store/provider access failures and inactive startup-critical Job dispatch are Unhealthy. Persisted event/job dead-letter backlogs are Degraded. RemoteOnly Jobs due for more than five minutes without an eligible Platform worker are Degraded; absence of workers alone is Healthy for local operation. Media initial library readiness contributes Degraded while starting. An individual failed event or job does not by itself make the platform Unhealthy. This composition has one primary storage provider; future secondary-provider checks may explicitly return Degraded.

`/api/health/live` reports process liveness. `/api/health` and `/api/health/ready` report health/readiness status only, with 503 for Unhealthy and 200 for Healthy/Degraded. Existing `/healthz` and `/readyz` remain compatible with their previous consumers. Health check failures expose fixed codes, not exception messages, stacks, connection strings or physical roots. Critical health probes are not best-effort telemetry: failed access contributes to readiness.

`/api/health/details` and `/api/platform/observability` require `platform.observability.read` (or the existing Platform administrator grant). Legacy bearer administration is explicitly disallowed. The observability endpoint returns health details, runtime metrics, aggregate event/job backlog, storage provider identity, and safe worker status/heartbeat/capabilities/running execution counts. It returns no payload collections, certificate fingerprints or credentials. Store backlog is unavailable rather than fabricated as zero when its query fails. `/admin/observability` is the small permission-protected view shared by Modern and Classic, with explicit manual refresh and restart semantics.

## Tracing and remote transport

`ActivitySource` `Hispania.Platform` version 1.0.0 supplies HTTP request, Event publish/delivery, short Job creation, Job execution, remote dispatch, Worker execution and Storage activities. Production adapters use `StartScope` to isolate sampling/start/stop callback failures and restore the parent Activity. It honors listeners' sampling decisions; no listener means no new sampled activity. Existing ASP.NET trace context remains usable. Correlation is a separate opaque business-flow identifier, not a substituted TraceId.

Events and Jobs add optional W3C parent/state envelope metadata via additive SQLite migrations; existing rows remain readable. Delivery and execution restore that parent. Unsampled flows retain the carrier independently of Activity creation, so transport does not depend on having a listener installed. The remote protocol carries only correlation, causation, TraceParent and TraceState, not arbitrary HTTP headers or baggage. The generic WorkerAgent restores the scope before invoking capability handlers. Optional fields preserve older peers' JSON compatibility. Correlation and causation survive restart only where stored in Events, Jobs or Audit; health and runtime metrics are reconstructed. Traces are sampling hooks, not a promise of permanent complete trace capture.

No Prometheus, OpenTelemetry SDK/exporter, Grafana, Jaeger, Tempo, Loki, Elasticsearch, Application Insights, Datadog or other backend is introduced. Future exporters can subscribe to the standard Meter/ActivitySource without changing domain contracts.

## Validation

Tests cover HTTP generation/echo/rejection, session-only administrative RBAC, correlated Job/Audit responses, operation→Event→Job causation, persisted trace metadata, serialized remote scope/parent restoration, concurrent metric increments, bounded vocabulary, negative/nonfinite rejection, failing collectors, health transition/recovery and secret-bearing handler failure. Existing Identity, Authorization, Events, local/remote Jobs, WorkerAgent/mTLS, Storage, Inventory, Media/Playback and Web regressions remain part of validation. The original local execution was Windows. Final Phase 13 Linux/Windows Validation #37230450104 passed at functional SHA `75e10edd560e396a6e509a386d5aae5dd7928827`, including the required Observability gate (17/17 on each OS); see the [closure evidence](../PHASE-13-CLOSURE.md).
