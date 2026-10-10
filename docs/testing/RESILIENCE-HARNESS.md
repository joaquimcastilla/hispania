# Resilience Harness

## Entry point

Run `pwsh -NoProfile -File tools/test-phase24-operational-resilience.ps1 -Profile Smoke -Seed 24001 -OutputDirectory .artifacts/phase24`. PowerShell 7 and the .NET 10 SDK are required. Smoke is 60 seconds, Short is 900 seconds, and Soak is 7200 seconds. Extended is 8 hours and requires `-ConfirmExtended`.

Optional bounds: `-MaxMemoryMb`, `-MaxCpuPercent`, `-MaxConcurrency`, and `-WorkloadIntervalMilliseconds` (100–60000; default 1000). A campaign manifest has `schemaVersion: 1`, a validated campaign ID, duration, seed, unique workload/fault IDs, explicit resource limits, and a bounded repeat interval. Unknown workload/fault IDs, duplicate IDs, missing or out-of-range limits, excessive duration, and ambiguous output locations fail closed.

## Isolation and artifacts

The harness creates a unique child directory and a random ownership marker. It starts only its own Server child on loopback with campaign-owned config paths and synthetic data. Cleanup targets are canonicalized beneath that directory and the evidence directory is retained. No service outside this process tree is stopped. The harness does not use existing user databases or libraries.

Artifacts: `campaign-manifest.json`, `campaign-summary.json`, `campaign-report.md`, `metrics.jsonl`, `fault-timeline.json`, `invariants.json`, `artifact-checksums.json`, `campaign-exit.json`, and `cleanup.json`. JSON metrics identify real, derived, or unavailable values. Reports record the tested git SHA and environment. Any missing required evidence or failed invariant yields nonzero exit.

## Workload and interpretation

All enabled workloads repeat for the entire configured window. Health sends concurrent requests to the campaign Server's `/healthz` and `/readyz`. Jobs repeatedly create, idempotently confirm, complete and reopen synthetic SQLite jobs. Events repeatedly publish, reopen, deliver and verify synthetic events. Jobs and event rows are pruned after their checks pass to bound temporary database size. Storage repeatedly writes, verifies and deletes deterministic synthetic bytes. Worker coverage repeats the in-process coordinator state machine for registration, lease expiry, reassignment and stale-generation rejection; this does not use a remote WorkerAgent. The report includes aggregate counts and per-interval request/error/timeout counts, latency percentiles, CPU, working set, disk, and invariant checks. Latency percentiles use a fixed-size histogram to bound memory.

The report identifies the exact Git SHA, profile, seed, platform, configured and sustained durations, workload coverage, and cleanup. Smoke produces an early operational signal, not long-term leak evidence. Server managed heap remains INCONCLUSIVE: the harness samples Server process working set and CPU through the operating system, while `GC.GetTotalMemory` applies only to the harness. It cannot prove that a production SLA is met. Campaign success says nothing about PostgreSQL until an authenticated PostgreSQL campaign has actually run.
