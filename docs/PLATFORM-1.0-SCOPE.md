# Platform 1.0 scope

This document defines the stabilization target, not a current 1.0 declaration. HISPANIA remains 0.7.0 / M3. Owners must select precise catalog surfaces and pass category-specific API, old-consumer, populated-upgrade, restore and deployment/security gates before publication. Feature existence alone is insufficient.

## Required for 1.0

- Existing Windows/Linux x64 Platform runtime composition and dependency independence; explicit module discovery, lifecycle and capability selection.
- A selected, published Platform/Module SDK surface with source/binary baselines, trusted InProcess and supervised OutOfProcess package lifecycle, rejection of incompatible formats/protocols, and documented trust/resource limitations.
- Local Identity query/command and session semantics, current Authorization/RBAC service, opaque AssetId/AssetReference, filesystem storage and their owned SQLite stores.
- Durable Events and Jobs with explicit at-least-once, idempotency, cancellation, lease/retry/dead-letter and payload evolution promises for selected types, not every test handler.
- Existing single coordinator and allowlisted retry-safe WorkerAgent behavior, including stale-result fencing and documented recovery limits. Remote transport must earn old-peer gates before becoming stable.
- Safe audit, bounded correlation/metrics/health and selected administrative HTTP projections; static Modern/Classic Platform screens with declared parity and auth behavior.
- Support matrix, operational guidance and verified data upgrade/backup/restore policies for required owned stores. These are future publication gates, not Phase 14 implementations.

The Media-owned Direct Play/HTTP Range result is a Stable Candidate dependency used by the integrated distribution, not a requirement to stabilize the whole Media suite or include Media in Platform-only builds.

## Experimental but shipped

Unselected Web/Admin DTOs, payload handlers and codec details; test hash/delay jobs; experimentation/shadow routing details; generic worker protocol details before negotiation/old-peer evidence; PostgreSQL Library provider; Media transcoding/catalog/enrichment; Inventory ERP proof; demo greeting contracts. Linux ARM64 remains theoretically supported with no validation claim. Current package/module SDK details are experimental until the selected scope passes its gates.

## Explicitly out of scope

Dynamic Web modules; multi-coordinator HA; consensus; general S3/object storage or second provider; complete ERP; complete Media suite; SSO; advanced MFA; multi-tenancy; autoscaling; package dependency graph expansion; hostile-code sandbox; general workflows/cron; TSDB/exporter ecosystem. Windows Service/systemd installers, secret store and deployment implementations are outside Phase 14; operational policy must be decided before a future supported release without treating every aspirational feature as required.

## Candidate gates

| Candidate | Current evidence | Still needed before stable publication |
| --- | --- | --- |
| Identity query/command | PlatformIdentityTests persistence, duplicate users, disabled users, session expiration/revocation | API baseline review, actual old compiled/source client, populated DB upgrade, backup/restore, abuse/rate/deployment policy |
| Authorization/RBAC service | PlatformAuthorizationTests live role/grant changes and atomic bootstrap; HTTP permission gates | Transitive DTO baseline, old client, populated grants upgrade, restore, privilege/deployment policy |
| AssetId / AssetReference | Inventory opaque references; Storage provider regression and legacy handles | CLR + serialization fixture, cross-release opaque identity retention, populated metadata upgrade/restore |
| Direct Play / HTTP Range result | HttpDirectPlayIntegrationTests full, bounded/open/suffix ranges, malformed/multiple rejection and restart | Selected result API baseline, old HTTP consumer fixture, header/error/behavior specification and supported input matrix |
| Fencing invariant | DistributedWorkCoordinatorTests and coordinator/process restart reject expired/cancelled/old generation results | Cross-release coordinator/worker replay fixture, crash/race failure matrix and explicit retry-safe side-effect policy; invariant stability must not freeze all worker DTOs |

## Kernel decision

Option B: `core/kernel/Hispania.Kernel.csproj` is an empty SDK library with no source or project references; production projects do not reference it. `core/runtime` owns actual registry/lifecycle/resolution. ADR-0030's “minimal host/runtime primitives” description is imprecise for that empty project. Propose removal of the empty project and solution entries in a later maintenance change; retain it in Phase 14 to avoid unrelated solution churn. Do not invent responsibilities or add functionality to justify a placeholder. “Kernel” may still describe the architectural role implemented by Runtime, not an implementation in this empty assembly.

## Phase 17 — backup implementation status

Distribution 0.7.0 adds experimental Backup Format Generation 1, without a Public Stable promotion. Required closure is a coherent complete backup and destructive clean-host reconstruction with semantic recovery and mixed-provider evidence. The current offline managed-root implementation is under validation; it does not establish 1.0 readiness. [Phase 17](PHASE-17-BACKUP-RESTORE.md) records exact exclusions and remaining acceptance. No Phase 18 work is introduced.
