# Support matrix

This is a support target and evidence map, not a deployment certification. Validated means the named implemented scope has recorded execution evidence; Partially validated means some named checks pass with gaps; Theoretically supported means a plausible implementation path with no target execution proof; Experimental means shipped without a support promise; Unsupported means no implemented scope. All entries remain subject to the pre-1.0 policy.

| Surface | Current state | Evidence and limits | Target toward Platform 1.0 |
| --- | --- | --- | --- |
| Windows x64 | Validated | Phase 13 closure Linux/Windows run #37230450104 at functional SHA 75e10edd; full .NET and Web, process tests. No service installer/deployment certification | Required tested host/runtime and generic workers |
| Linux x64 | Validated | Same closure run Ubuntu; cross-platform filter, PostgreSQL/FFmpeg/OOP; recorded two-host Windows↔Ubuntu x64 test in PROGRESS-LOG. Windows launcher excluded | Required tested host/runtime and generic workers |
| Linux ARM64 | Theoretically supported | No ARM64 run or physical-host evidence; .NET portability insufficient | Experimental until runner/physical execution and dependency coverage; not a 1.0 blocker |
| SQLite | Validated | Identity/Auth, Assets/Audit, Events/Jobs, coordinator restart/concurrency and Library regression | Required for Platform stores; populated upgrades/restore still gates |
| PostgreSQL | Partially validated | Library provider live PostgreSQL 16 gate on Linux; Platform stores remain SQLite | Experimental Media provider; not universal Platform backend requirement |
| Filesystem storage | Validated | Opaque/legacy handles, containment, compensation and missing-provider tests | Required existing provider; orphan maintenance/deployment limits explicit |
| InProcess | Validated | Trusted package activation, update/rollback, collectible loading | Required trusted mode; never a security sandbox |
| OutOfProcess | Validated | Generic greeting negotiation/crash recovery; Windows Job Object/Linux managed heap/prlimit limits | Required fault containment; hostile-code sandbox excluded |
| WorkerAgent | Validated | mTLS, restart, stale-generation rejection and two-host evidence | Existing retry-safe allowlisted work only; protocol remains experimental pending old-peer fixtures |
| Modern Web | Validated | Existing npm builds/tests and auth/Admin parity gates | Required static shell and selected Platform screens, explicit DTO scope |
| Classic Web | Validated | Same shared screen tests and separate artifact build | Required parity for declared Platform screens |
| Dynamic Web modules | Unsupported | Static route/build registration only | Explicitly outside 1.0 scope |

Recorded evidence is from Phase 13; Phase 14 local execution is separately reported in `PHASE-14-VALIDATION.md`. “Validated” does not imply stable APIs, long-term operation, old-binary compatibility, backup/restore or every security/deployment scenario. See [closure](PHASE-13-CLOSURE.md) and [scope](PLATFORM-1.0-SCOPE.md).

## Phase 17 — operational backup support under validation

Offline full bundles support the explicitly configured managed single-host root: Platform/Inventory SQLite, Media SQLite or logical PostgreSQL, filesystem Storage and installed extension bytes/public trust configuration. PostgreSQL 16 client tooling and externally reprovisioned libpq credentials are required by the Linux mixed-provider gate. Scattered source paths are preserved from the original host configuration through a machine-local descriptor; external Media source bytes are reprovisioned. No online/global-ACID/overwrite/merge/partial restore is claimed. See [Phase 17](PHASE-17-BACKUP-RESTORE.md); complete closure remains pending.
