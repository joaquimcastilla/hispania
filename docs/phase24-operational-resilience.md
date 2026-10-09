# Phase 24 — Operational Resilience

Phase 24 adds an isolated local harness for bounded soak and controlled recovery work. It is test infrastructure; it does not change runtime service contracts or claim production high availability.

## Local implementation

`tools/test-phase24-operational-resilience.ps1` builds the existing Server and the harness, then launches a campaign-owned Server process bound to an ephemeral loopback port. Data paths, worker state, media roots, SQLite stores and generated assets point inside the campaign directory. The runner checks `/healthz` and functional `/readyz`, exercises existing durable SQLite Jobs and Events, writes/verifies/deletes synthetic filesystem assets, and exercises the existing coordinator's lease generation fencing. Each campaign uses a reproducible seed, validated limits, timeout, cancellation, ownership marker, cleanup, JSON and Markdown reports, JSONL metrics, invariant results, fault timeline and SHA-256 evidence manifest.

The current local profiles are Smoke (60 seconds), Short (10 minutes), Soak (2 hours), and Extended (8 hours). Extended requires `-ConfirmExtended`. Smoke is the only profile intended for this implementation phase. The selected duration is an explicit upper bound; profile limits can be overridden with the script parameters.

## Results and limits

Campaign PASS is evidence only for the recorded execution. Smoke is too short to establish leak absence; memory trend results are INCONCLUSIVE. Managed heap and GC measurements describe the harness process. Server RSS, CPU time and thread count are sampled from the owned process; metrics not exposed on the host are labelled unavailable. The runner does not claim exactly-once delivery, global transactional rollback, distributed coordinator HA, or an RPO for unconfirmed work. PostgreSQL is intentionally not simulated and must be verified by authenticated CI.

See [harness reference](testing/RESILIENCE-HARNESS.md), [soak operations](operations/SOAK-TESTING.md), [fault injection](operations/FAULT-INJECTION.md), and [ADR-0049](adr/ADR-0049-operational-resilience.md).
