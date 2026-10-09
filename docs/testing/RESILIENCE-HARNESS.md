# Resilience Harness

## Entry point

Run `pwsh -NoProfile -File tools/test-phase24-operational-resilience.ps1 -Profile Smoke -Seed 24001 -OutputDirectory .artifacts/phase24`. PowerShell 7 and the .NET 10 SDK are required. Smoke is bounded at 60 seconds plus a 45-second startup/cleanup timeout. Short, Soak, and Extended profiles exist for later controlled campaigns; this implementation task does not run them. Extended requires `-ConfirmExtended`.

Optional bounds: `-MaxMemoryMb`, `-MaxCpuPercent`, and `-MaxConcurrency`. A campaign manifest has `schemaVersion: 1`, a validated campaign ID, maximum duration, seed, unique workload/fault IDs, and explicit resource limits. Unknown workload/fault IDs, duplicate IDs, missing or out-of-range limits, excessive duration, and ambiguous output locations fail closed.

## Isolation and artifacts

The harness creates a unique child directory and a random ownership marker. It starts only its own Server child on loopback with campaign-owned config paths and synthetic data. Cleanup targets are canonicalized beneath that directory and the evidence directory is retained. No service outside this process tree is stopped. The harness does not use existing user databases or libraries.

Artifacts: `campaign-manifest.json`, `campaign-summary.json`, `campaign-report.md`, `metrics.jsonl`, `fault-timeline.json`, `invariants.json`, `artifact-checksums.json`, `campaign-exit.json`, and `cleanup.json`. JSON metrics identify real, derived, or unavailable values. Reports record the tested git SHA and environment. Any missing required evidence or failed invariant yields nonzero exit.

## Workload and interpretation

Health sends concurrent requests to the actual campaign Server's `/healthz` and `/readyz`; it records count, error, timeout, latency percentiles and throughput. Jobs use the existing SQLite job store and hash handler, exercise idempotent creation, completion, and reopen persistence. Events use the durable event bus, reopen persistence, and delivered status/ID verification. Storage uses deterministic synthetic bytes and verifies SHA-256, metadata and deletion. Worker coverage uses the existing single-coordinator state machine to exercise registration, lease expiry, reassignment and stale-generation rejection.

Smoke produces an early operational signal, not long-term leak evidence. It cannot prove that every operation is covered or that a production SLA is met. Campaign success says nothing about PostgreSQL until an authenticated PostgreSQL campaign has actually run.
