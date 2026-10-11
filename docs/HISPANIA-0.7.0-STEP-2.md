# HISPANIA 0.7.0 — Step 2 validation record

**Status: PARTIAL.** This document records the bounded ARM64 CI evidence only. It does not accept or publish the pending Step 2 refactor, claim production-ready ARM64 installation, or start Step 2.1 or Step 3.

## CI evidence

- PR [#17](https://github.com/joaquimcastilla/hispania/pull/17) integrated only the Pi 3B+ manual smoke workflow, its fixed-input script and its operations documentation. It merged to `main` as `b2701aeb91144ea380f410ab298356677fdbd06f` after scope/security review.
- The first real Actions job [38138089991](https://github.com/joaquimcastilla/hispania/actions/runs/38138089991) passed on `hispania-arm64-ci` at the merge commit. It checked ARM64/.NET 10, fixed SHA-256 inputs, health/readiness, authentication, Platform routes, absent Media/Inventory routes, SQLite persistence and clean shutdown.
- The Pi 4B runner is separately installed and online. Its manual-only integration workflow is in a separate PR and remains unmerged pending additional authorization. An equivalent staging-only smoke over SSH passed; it is not reported as an Actions job.

## Scope limitations

The tested package is a working-tree artifact, not a clean source commit/release. It contains `Microsoft.Extensions.Hosting.WindowsServices.dll` and its dependency entry, so the strict WindowsServices exclusion is unresolved. Production installation/bootstrap/upgrade/backup/rollback, signed module lifecycle, executable product job handlers, worker mTLS, and a direct Data Plane are not claimed. The Pi 3 reports `vcgencmd get_throttled=0x50005`; no stress test or build was run.

The integration workflow and its exact script/artifact hashes, security boundaries, Pi inventory and metrics are documented in [ARM64-CI.md](operations/ARM64-CI.md). This validation record makes no product-code change and does not replace the pending Step 2 review.
