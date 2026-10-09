# ADR-0047: Owner-controlled retention, reconciliation and capacity observation

Status: Accepted for Phase 22 implementation

## Context

Durable Jobs, Events, Sessions, package generations, backup bundles and Asset content accumulate at different rates. Their deletion rules depend on owner semantics. Storage metadata and bytes can diverge because SQLite and the filesystem have no shared transaction. Operators also need capacity information before large writes.

## Decision

Platform defines versioned owner contracts for retention planning and bounded execution, reconciliation scans, and capacity snapshots. Each owner defines candidate and protected states and revalidates destructive work. A separate SQLite maintenance store keeps plans, leases, checkpoints and run history. The coordinator never performs cross-owner SQL. Maintenance is manual and disabled by default. Capacity pressure does not authorize deletion.

Job idempotency tombstones preserve duplicate detection after history pruning. Asset orphans use a logical quarantine with a grace period and no automatic byte deletion. Package cleanup protects current and previous KnownGood graphs and requires a staging ownership marker. Backup cleanup requires an explicit archive root, full bundle verification, latest verified and pre-upgrade protection, optional adjacent pins, and a bundle lock shared with restore. Audit and coordinator fencing state have no destructive retention path.

## Consequences

The system can make bounded, reviewable maintenance decisions without claiming cross-store atomicity. Deleted SQLite rows need not reduce database file size. Quarantine still uses space. Capacity snapshots race with external writers. A single coordinator and local file locks are assumed; this design does not provide HA or recovery from a hostile filesystem. Further owners and preflight integrations can be added without moving their eligibility SQL into Platform.
