# Soak Campaign Operations

Soak (2 hours) and Extended (8 hours) are defined for later manual operation and are not part of the local implementation run. Use a dedicated test machine, a clean branch build, a synthetic dataset, a reviewed resource budget, and an output volume with adequate free space. Never point a campaign at personal libraries or an existing HISPANIA data directory.

Start with Smoke, then Short in the target CI runner environment. Capture the exact SHA and runner/runtime context. Schedule Soak only after CI Smoke/Short and the explicit campaign plan pass review. Extended also requires `-ConfirmExtended`. Do not run these profiles automatically for every commit.

Interpret a sustained resource trend with repeated windows and a warm-up period. Short runs and final RSS alone cannot establish that a memory leak exists or is absent. Compare workload success, error/timeout rate, p50/p95/p99 latency, queue depth/age, CPU, working set, managed heap, GC, threads, handles, disk, and recovery time with the versioned environment thresholds in the campaign summary. Missing data remains unavailable, never zero. Preserve reports and checksums; remove only campaign-owned temporary runtime stores after confirming cleanup evidence.

PostgreSQL requires authenticated Windows/Linux CI against a temporary database, real migrations, integrity checks and teardown. A missing PostgreSQL service is skipped/unavailable and must not be reported as passed.
