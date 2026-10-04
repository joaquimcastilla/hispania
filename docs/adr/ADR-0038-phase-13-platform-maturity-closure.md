# ADR-0038: Phase 13 Platform maturity closure

## Status

**Phase 13 — CLOSED — validated on Linux and Windows**

Functional validated SHA: `75e10edd560e396a6e509a386d5aae5dd7928827`. [Validation #37230450104](https://github.com/joaquimcastilla/hispania/actions/runs/37230450104) completed successfully on Linux and Windows. The subsequent closure commit updates documentation only; `phase-13-complete` identifies that final checkpoint. Merge to `main` remains a separate task; Phase 14 has not started.

## Decision

Close the existing Platform foundation through hardening and evidence, without adding domain features. Identity/RBAC, Durable Events, Jobs, remote execution, Storage/Assets, Audit, Observability and Admin retain the contracts and ownership established by ADR-0031 through ADR-0037. The server remains the composition root. Platform and generic WorkerAgent have no Media/Inventory implementation dependency; a recursive project graph and compiled-assembly tests enforce those boundaries.

Events and Jobs keep independent stores, dispatchers and at-least-once semantics. Recovery evidence includes a production-server graceful/abrupt restart scenario across Identity, grants, Inventory, Assets, Events, Jobs and Audit; a killed real executor fixture with both Running/Processing leases, recovered by the production server; and the existing mTLS WorkerAgent crash/restart/fencing regression. Uncommitted work and exactly-once behavior are not guaranteed.

Sanitize HTTP/provider/extension failures at the server boundary. Preserve authorization and response status policy; do not log raw exception messages/stacks, physical package directories, credentials or transport payloads. Retain existing explicit legacy bearer compatibility, inventory it precisely, and never expand it to session-only Jobs, Events, Observability or consolidated Admin projections.

Require one shared closure gate on Linux and Windows: process recovery, architectural independence, adversarial error tests and 20 successful repetitions of named concurrency cases. Keep all existing Web, domain, FFmpeg, distributed, Platform-only and PostgreSQL-live gates. Missing discovery and skips fail the critical gates.

## Consequences and limits

The project gains a validated closure checkpoint, definitive release notes and an annotated tag/GitHub Release. This records the implemented foundation without claiming enterprise completeness. Merge to main remains separate. Remain explicit about at-least-once behavior, single coordinator, trusted-code boundaries, initial filesystem storage, runtime-only metrics, static frontend loading, partial legacy bearer/public diagnostics and limited Inventory. SSO/MFA, TSDB/exporters, remote object storage, Notifications, cron, multi-coordinator and ERP expansion require later decisions.

See the [closure/security audit](../PHASE-13-CLOSURE.md), [complete project graph](../PHASE-13-DEPENDENCIES.md) and [definitive release notes](../PHASE-13-RELEASE-NOTES.md). Functional validation and the documentation-only checkpoint distinction are recorded above.
