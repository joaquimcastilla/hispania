# Support matrix

This is a support target and evidence map, not a deployment certification. Validated means the named implemented scope has recorded execution evidence; Partially validated means some named checks pass with gaps; Theoretically supported means a plausible implementation path with no target execution proof; Experimental means shipped without a support promise; Unsupported means no implemented scope. All entries remain subject to the pre-1.0 policy.

| Surface | Current state | Evidence and limits | Target toward Platform 1.0 |
| --- | --- | --- | --- |
| Windows x64 | Validated for recorded distribution/process scenarios | Phase 25 clean package repeatability and 12 published-artifact/process cases are recorded at `9c7872e0e3e848fd820c3b8cb6641e2e50475a8e`; Phase 13 closure provides earlier Linux/Windows suite evidence. Native SCM service installation/least-privilege operation is not certified | Tested host/runtime and generic workers; service deployment needs separate disposable-host evidence |
| Linux x64 | Native package acceptance passed | Phase 25 run [#38064897483](https://github.com/joaquimcastilla/hispania/actions/runs/38064897483), exact clean SHA `8817b912b8f1748977f9145942d2dc30ad492b8e`, Ubuntu 24.04 x64; repeated contents, install, `--check`, outside-checkout start, HTTP 200, identity SQLite and cleanup. No systemd installation | Tested host/runtime and generic workers; system service deployment needs separate disposable-host evidence |
| Linux ARM64 | Not validated | No ARM64 run or physical-host evidence; .NET portability alone is insufficient | No support claim until runner/physical execution and dependency coverage |
| SQLite | Validated | Identity/Auth, Assets/Audit, Events/Jobs, coordinator restart/concurrency and Library regression | Required for Platform stores; populated upgrades/restore still gates |
| PostgreSQL | Partially validated | Library provider live PostgreSQL 16 gate on Linux; Platform stores remain SQLite | Experimental Media provider; not universal Platform backend requirement |
| Filesystem storage | Validated | Opaque/legacy handles, containment, compensation and missing-provider tests | Required existing provider; orphan maintenance/deployment limits explicit |
| InProcess | Validated | Trusted package activation, update/rollback, collectible loading | Required trusted mode; never a security sandbox |
| OutOfProcess | Validated | Generic greeting negotiation/crash recovery; Windows Job Object/Linux managed heap/prlimit limits | Required fault containment; hostile-code sandbox excluded |
| WorkerAgent | Validated | mTLS, restart, stale-generation rejection and two-host evidence | Existing retry-safe allowlisted work only; protocol remains experimental pending old-peer fixtures |
| Modern Web | Validated | Existing npm builds/tests and auth/Admin parity gates | Required static shell and selected Platform screens, explicit DTO scope |
| Classic Web | Validated | Same shared screen tests and separate artifact build | Required parity for declared Platform screens |
| Dynamic Web contributions | Experimental | Phase 20 signed declarative schema, shared Modern/Classic registry and fixed authorized operations; no executable package JavaScript or hostile-code browser sandbox | Remains experimental; scope defined in Phase 20 and Platform 1.0 plan |

Evidence is scoped to the named commit, target and scenario. The Phase 25 native Linux package acceptance supplements, but does not replace or expand, earlier phase-specific API, security, upgrade and domain evidence. “Validated” does not imply stable APIs, long-term operation, old-binary compatibility, backup/restore or every security/deployment scenario. See [Phase 25 evidence](phase25-release-readiness.md), [closure](PHASE-13-CLOSURE.md) and [Platform 1.0 scope](PLATFORM-1.0-SCOPE.md).

## Phase 17 — operational backup support under validation

Offline full bundles support the explicitly configured managed single-host root: Platform/Inventory SQLite, Media SQLite or logical PostgreSQL, filesystem Storage and installed extension bytes/public trust configuration. PostgreSQL 16 client tooling and externally reprovisioned libpq credentials are required by the Linux mixed-provider gate. Scattered source paths are preserved from the original host configuration through a machine-local descriptor; external Media source bytes are reprovisioned. No online/global-ACID/overwrite/merge/partial restore is claimed. See [Phase 17](PHASE-17-BACKUP-RESTORE.md); complete closure remains pending.

## Phase 18 production deployment

Publications target win-x64/linux-x64 with the .NET 10 ASP.NET Core runtime. Local artifact/process evidence is distinct from actual SCM/systemd and password-auth PostgreSQL operation. Mandatory Production Deployment CI must establish those native gates before operational support is called validated. Linux ARM64 remains experimental/unvalidated. See [Phase 18](PHASE-18-PRODUCTION-DEPLOYMENT.md) and [ADR-0043](adr/ADR-0043-configuration-secrets-and-production-deployment.md).


## Phase 19 package lifecycle

Format 2 adds authenticated package SemVer/dependencies while format 1 and immutable Phase 15 signatures remain supported. Local deterministic planning, a pinned graph, staged operations, current-policy rollback/recovery and Phase 17 graph verification remain experimental. Distribution 0.7.0, 24 axes and zero Public Stable are unchanged. See [Phase 19](PHASE-19-PACKAGE-LIFECYCLE.md) and [ADR-0044](adr/ADR-0044-package-dependencies-and-lifecycle.md); local acceptance and both-OS CI are separate evidence.


## Phase 20 — Dynamic Web Modules (Experimental)

[Phase 20](PHASE-20-DYNAMIC-WEB-MODULES.md) adds signed declarative Web Schema Generation 1 as independent compatibility axis 25. Package Format 2, distribution 0.7.0 and private npm 0.1.0 remain unchanged; zero contracts are Public Stable. Required UI uses existing host/CLR capability negotiation; optional future UI is blocked independently of a healthy backend. Modern and Classic share one existing routing/registry composition and fixed authorized Platform operations. No package JavaScript, raw HTML/CSS, remote URLs, Node runtime or hostile-code browser sandbox is introduced.

The package catalog, journal, trust/authorization and dependency graph remain authoritative; immutable UI projections and parsed-descriptor caches are ephemeral. Actual browser, portable restore and verified predecessor distribution-upgrade tests accompany preserved Phase 14–19 gates. Windows local evidence and pending Linux/native/live-PostgreSQL CI are distinguished in the local acceptance report. See [ADR 0045](adr/ADR-0045-dynamic-web-contributions.md), [author guide](DYNAMIC-WEB-AUTHOR-GUIDE.md) and [operator guide](DYNAMIC-WEB-OPERATOR-GUIDE.md).
