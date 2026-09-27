# ADR-0028: Generic Variant Experiment Routing

Status: Accepted; persistent recovery is implemented locally, with the updated remote workflow still pending.

## Context

HISPANIA needs one experiment mechanism reusable by versioned module capabilities. Playback planning is a promising first consumer because its public operation is synchronous and read-oriented. Other capabilities may write shared state or trigger external effects and cannot safely opt in by default.

## Decision

- The runtime owns a generic, contract-agnostic experiment router. Variants are identified by module ID, capability ID, exact contract version, and generation.
- Configuration is published as immutable revisions. Traffic weights use integer basis points and must total 10,000. The caller must retain the resolved implementation for the entire operation.
- Assignment keys are pseudonymized before reaching the assignment store. The default store is in-memory; durable assignment storage is an optional capability-owned adapter.
- Existing assignments remain pinned while their variant remains configured. A retired or unavailable assignment follows an explicit fallback policy; the router never retries an operation.
- Only capabilities with reviewed read-only contracts and enforceable restricted dependencies may use shadow execution. A generic wrapper cannot sandbox trusted native code.
- Scanner, migration, persistence, and metadata-writing capabilities remain ineligible until they have explicit isolation and ownership coordination.

## Current implementation boundary

The generic router now serves the Playback Planner HTTP path through its public contract, with authenticated admin routes, manual/shadow/traffic-split modes, versioned generation identities, stable pseudonymous assignments, health fallback, retirement, rollback, and structured metrics/export. The two current planners opt into all modes; shadow is constrained to the read-only `IMediaCatalogReader` dependency and returns the active result regardless of secondary failure. Admin configuration is available under `/internal/admin/modules/playback-experiments`; use POST with an experiment ID, `TrafficSplit`, two module IDs and integer basis-point weights totaling 10,000. GET returns current status; POST `/{id}/suspend`, `/{id}/rollback`, `/{id}/variants/{moduleId}/retire`, and `/{id}/finish` perform lifecycle operations; GET `/{id}/metrics` exports the active or completed result. The existing admin bearer authentication protects the route group.

Temporary assignments use the in-memory store and disappear at restart. Persistent experiments opt into `JsonPlaybackExperimentStore` through `PlaybackExperiments:StatePath`, require an operator-managed stable `PlaybackExperiments:AssignmentSalt` secret (minimum 32 characters), and explicitly send `persistent: true`. The atomic single-host document stores the active configuration revision, mode, fallback policy, exact module/contract versions and generations, disabled-variant state, and salted SHA-256 cohort hashes with 30-day expiry; it never stores raw user/session IDs or the salt. A salt fingerprint rejects recovery after key rotation. Multi-host use needs a coordinated `IPlaybackExperimentStore` implementation.

On startup, the service validates every persisted variant against the live registry, supervisor health, declared mode and shadow read boundary, exact module/contract versions, and current extension authorization. It never loads or reauthorizes an extension. An absent variant follows `RedistributeToHealthy` by removing its assignments and renormalizing weights (or selecting Manual when one remains); `Fail` leaves the experiment inactive. An incompatible or unauthorized variant blocks the whole experiment from resuming. Persisted disabled variants remain disabled. The admin status reports recovery state for operator action. Requests interrupted by process shutdown are terminal and are never replayed; no planner operation is retried. Metrics, active request leases and rollback history remain in memory, while the validated current configuration and eligible cohort assignments recover. In-flight request snapshots remain pinned during normal reconfiguration and retirement.

The default store is not a multi-host database or a hostile-code security boundary. The shadow path restricts injected dependencies to the reviewed read-only catalog interface and returns only the active result, but trusted native code could still produce hidden filesystem, network, database, or process effects. Other capabilities with shared writes remain ineligible without their own isolation and idempotency design. Local tests cover restart recovery, stable assignments, missing-variant fallback, incompatible-contract rejection, authenticated lifecycle, and concurrent real-planner traffic. GitHub Actions on the updated tree has not yet run.
