# Phase 24 — Operational Resilience

Phase 24 adds an isolated local harness for bounded soak and controlled recovery work. It is test infrastructure; it does not change runtime service contracts or claim production high availability.

## Local implementation

`tools/test-phase24-operational-resilience.ps1` builds the existing Server and the harness, then launches a campaign-owned Server process bound to an ephemeral loopback port. Data paths, worker state, media roots, SQLite stores and generated assets point inside the campaign directory. The runner checks `/healthz` and functional `/readyz`, exercises existing durable SQLite Jobs and Events, writes/verifies/deletes synthetic filesystem assets, and exercises the existing coordinator's lease generation fencing. Each campaign uses a reproducible seed, validated limits, timeout, cancellation, ownership marker, cleanup, JSON and Markdown reports, JSONL metrics, invariant results, fault timeline and SHA-256 evidence manifest.

The current local profiles are Smoke (60 seconds), Short (15 minutes), Soak (2 hours), and Extended (8 hours). Extended requires `-ConfirmExtended`. Each enabled workload repeats for the complete profile window with bounded concurrency and a configurable 100–60000 ms interval. Profile limits can be overridden with the script parameters.

## Results and limits

Campaign PASS requires the full sustained window and successful repeated core workloads. Smoke is too short to establish leak absence; memory trend results are INCONCLUSIVE. Managed heap and GC measurements describe the harness process. Server RSS (working set), CPU time and thread count are sampled from the owned process; Server managed heap remains unavailable without runtime diagnostics attachment and is reported INCONCLUSIVE. Worker coverage uses an in-process synthetic coordinator, not a remote WorkerAgent. The runner does not claim exactly-once delivery, global transactional rollback, distributed coordinator HA, or an RPO for unconfirmed work. PostgreSQL is intentionally not simulated and must be verified by authenticated CI.

The Phase 24 worktree records four accepted Short and Soak campaigns on Windows and Linux at source SHA `f2c5798b2c3c973deacf2cad2e7cc6d89720c247`. Their extracted campaign directories and summaries remain in that original worktree, not this candidate; Gate V checked each campaign's internal checksum manifest (40 files total, no mismatches). The reported external ZIP hashes could not be independently recomputed because the ZIP files are not present here. See the [HISPANIA v1.0 evidence ledger](architecture/HISPANIA-V1-EVIDENCE-LEDGER.md) for provenance and limits. These campaigns do not establish remote WorkerAgent operation, PostgreSQL behavior, fault-injection recovery, or production readiness.

See [harness reference](testing/RESILIENCE-HARNESS.md), [soak operations](operations/SOAK-TESTING.md), [fault injection](operations/FAULT-INJECTION.md), and [ADR-0049](adr/ADR-0049-operational-resilience.md).
