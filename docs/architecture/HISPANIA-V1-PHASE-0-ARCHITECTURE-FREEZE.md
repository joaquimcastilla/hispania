# HISPANIA v1.0 — Phase 0 Architecture Freeze

**Purpose:** freeze the architectural direction and define the acceptance contract for Phase 1 (Empty Core Composition Root). This is an analysis and planning document; it does not authorize implementation of Phase 1.

**Repository evidence:** starting HEAD for this update `b043be03be83caef431c53c6480bd6e9359666d2`, branch `feature/phase-13-platform-maturity`, observed 2026-10-15. The product-code baseline remains `134076f5ba2894aba216e775c4588cc3c547ee31`; intervening commits changed only this Phase 0 document. `git status` showed pre-existing edits and untracked files in server endpoints, Platform events/identity, SQLite adapters, tests, and `docs/architecture/`. They were not used as shipped implementation evidence and were preserved. A separate local Step 2.1 worktree/branch and ARM64 CI branches were inspected read-only; their relationship and evidence limits are recorded under 9.7. The previous audit document is untracked in this worktree; it is treated as the requested source, not overwritten. The working tree has no upstream configured for this branch (no `@{upstream}` resolved); remote state was not fetched.

**Phase 0 result:** **TECHNICAL PHASE 0: PASS** for the specified documentation outputs and Phase 1 contract. **OWNER FREEZE: PENDING** because the unresolved decisions in §9 require owner approval. No product code, APIs, databases, scheduler, Data Plane, runtime, or package format were changed; no tests were run.

## 1. Purpose and evidence rules

The primary input is [HISPANIA v1.0 Deep Architecture Audit](HISPANIA-V1-ROADMAP-AUDIT.md). It is a baseline of observations and proposals, not a set of owner-approved decisions. The principles in §2 and Owner Decisions 9.1–9.9 below are explicitly approved. Decisions 9.10–9.11 remain open; recommendations are technical advice only.

This document distinguishes:

- **Verified observation:** directly visible in the versioned tree at the HEAD above.
- **Owner-approved:** explicitly approved by the owner in the Phase 0 request.
- **Recommendation:** proposed engineering choice, not approved until the owner accepts it.
- **Open:** needs an owner decision before the relevant public contract or support promise is frozen.

The audit's principal conclusion stands: preserve reusable platform foundations and build a clean Core composition root in a later phase; do not rewrite the repository wholesale and do not gradually transform `apps/server` into Core by deleting business references. Two clarifications from this fresh HEAD inspection are important:

1. `apps/web/src/webModules.ts` already has a route/navigation registry. It is a **static registry compiled into the Web app**, with known Media and Inventory paths; there is no package-driven contribution loading in this HEAD. Thus the UI boundary is partially prepared, not absent.
2. `Hispania.Platform.slnf` is a **build filter**, not a Core distribution manifest. It includes extension-demo contracts and architecture-test fixture projects in addition to platform projects. Its successful build does not prove an installable Core package contains no demo, Media, or Inventory payloads.

The working tree has uncommitted additions that appear to introduce durable Platform events/identity. Those are out of scope for shipped-state claims in this document because they are not in HEAD. The existing Phase 1 plan must re-evaluate them when their owner commits or explicitly selects them.

## 2. Owner-approved principles

These are approved and are **not** open decisions:

1. HISPANIA has one Core, able to boot, be administered, upgraded, diagnosed, and recovered without functional modules.
2. All business functionality is a module: Media, Inventory, AI, ERP, automation, and future domains are not Core responsibilities.
3. Modules are replaceable behind versioned contracts. Contract/API compatibility, state compatibility, and data migration are separate guarantees.
4. `Minimal`, `Media`, `Business`, and `Complete` are profiles/bundles over the same Core; there is no second Complete architecture.
5. No wholesale rewrite. Reuse current subsystems only after they satisfy the frozen boundaries.
6. Transition direction: new Core composition root → Inventory as the first real module → Media extraction later. Phase 0 does not create that composition root or move either domain.

## 3. Future Core boundary and responsibility classification

“Core” below means platform behavior that must still work with zero business modules. Contracts are not implicitly runtime behavior: the Core may define a shared contract and choose not to ship a particular provider.

| Responsibility | Classification | Boundary and reason |
|---|---|---|
| Identity | CORE | Owns platform principals for administrators, modules, and workers. Domain-specific profile/ownership remains with modules. |
| Authentication | CORE | Verifies credentials and establishes actor/session; provider (local, OIDC, etc.) is replaceable. |
| Authorization | CORE | Enforces platform/module permission checks and policy decision points; business roles/rules can be module-owned. Deny by default. |
| Configuration | CORE | Loads platform settings, validates schema, provides scoped config API; business schema belongs to module. |
| Secrets integration | CORE + PROVIDER | Core provides secret references/access policy; OS vault or external secret store is a replaceable provider. Never package plaintext secrets. |
| Module lifecycle | CORE | Install/verify/enable/disable/upgrade/rollback/remove/archive state machine and dependency plan. |
| Module registry | CORE | Catalogue of installed module identity/version/state/contracts; no domain business catalog. |
| Package verification | CORE | Checks format, path safety, content hashes, signature, host compatibility before activation. |
| Publisher trust | CORE + OPERATOR TOOLING | Core evaluates trust/revocation; operator supplies roots, namespace grants, policy and review. Package signer does not get automatic permissions. |
| Audit | CORE | Append/inspect security and lifecycle records with redaction, actor, time, correlation, integrity policy. Domain event history stays in module. |
| Health | CORE | Platform, module, worker, persistence, and dependency health aggregation; modules provide health contracts. |
| Worker registry | CORE | Enrollment, identity, capabilities, online state, authorization, revocation and version inventory. Agent is WORKER. |
| Scheduling | CORE | Generic policy engine and durable Coordinator-owned task/lease state; module defines task semantics and requirements; pluggable scheduler is PROVIDER. |
| Leases/retries | CORE | Fencing, expiry, cancellation, retry-safe state and uncertainty behavior; task handler is module/provider-owned. |
| Grants | CORE | Creates and revokes narrowly scoped, short-lived authorization; storage/source provider validates and serves bytes. |
| Data Plane authorization | CORE | Authorizes task/node/destination/scope/TTL. Bulk transfer implementation is PROVIDER/WORKER; no business payload relay in generic Core path. |
| Platform persistence | CORE + PROVIDER | Owns Core config, identity, module inventory, trust and task metadata through a provider-neutral persistence contract. SQLite is the approved official Core provider for v1.0; PostgreSQL is not an official Core provider. |
| Module persistence | MODULE + PROVIDER | Module owns its schema/data and selects its DB provider; no implicit shared Core tables. |
| Web shell | CORE | Login, platform status, module registry/navigation extension points. Module pages are MODULE contributions under versioned UI contract and strict origin/CSP rules. |
| CLI | OPERATOR TOOLING | Operator-facing commands call stable Core APIs; CLI is not platform business logic. |
| Backup orchestration | CORE + OPERATOR TOOLING | Core coordinates snapshots/manifest/restore hooks; modules export owned data; CLI/UI operates and verifies backups. |
| Diagnostics / doctor | CORE + OPERATOR TOOLING | Core exposes diagnostic facts; `hispania doctor` gathers checks, redacts secrets and returns stable codes. |
| Module domain models/endpoints | MODULE | Core must not encode Media/Inventory/AI entities or business routes. |
| FFmpeg/TMDB/AI/NAS implementation | PROVIDER or MODULE | Concrete engine/provider is selected by declared capability and policy. |

### Empty Core minimum

An empty Core is an operable **platform**, not a useful media/ERP product. Its UI/CLI may report “no functional modules installed.” It must provide health, authenticated administration, configuration, platform persistence, module catalogue/lifecycle, audit, diagnostics, backup/restore of its own state, and stable version/capability APIs. Worker registry/scheduler interfaces should exist in Core; whether remote task execution itself is mandatory in the first Core composition root is set in §7 and the Phase 1 contract.

## 4. Module boundary

A module owns business policy, domain types, APIs, UI contributions, module configuration, schema/migrations, module health, and export/import. It consumes platform contracts for identity, authorization, storage references, audit, events, task scheduling, secrets, and diagnostics. A module may ship providers (for example TMDB) or require a provider capability; those implementations remain replaceable.

Modules must not:

- reference another module's implementation assembly, entities, tables, routes, or local files;
- create SQL foreign keys or cross-module transactions;
- assume another optional module exists unless the manifest declares a versioned dependency and installation plan resolves it;
- treat a publisher signature as a permission grant;
- report API compatibility as data compatibility;
- use Core's process identity as an unreviewed authority to access arbitrary filesystem, network, secrets, GPU, or process execution.

Module-to-module interaction uses shared contracts/events or a declared capability at runtime. Dependency graph must be acyclic and a module must remain disabled if a hard dependency is missing or incompatible. Optional dependencies cannot prevent the empty Core boot.

## 5. Forbidden dependencies and current HEAD violations

### Frozen future rules

These directed dependencies are **PROHIBITED**:

```text
Core → Media Domain / Library / Playback / Metadata / Streaming       PROHIBITED
Core → Inventory / ERP                                                PROHIBITED
Core → TMDB or another metadata vendor                                PROHIBITED
Core → concrete FFmpeg / NVIDIA / transcoding provider                 PROHIBITED
Core → AI provider or model                                             PROHIBITED
Core → concrete storage or module-owned DB schema                       PROHIBITED
Module A → Module B implementation                                      PROHIBITED
Platform Contracts → any business-domain assembly                       PROHIBITED
Cross-module DB FK / transaction                                        PROHIBITED
```

Core may depend on reviewed, domain-neutral platform/module/worker/persistence/identity/storage contracts. The contract assembly itself must not import business-domain types.

### Exact current projects needing classification

There is no project named `Hispania.Core` in HEAD. Therefore the present tree cannot literally contain a `Hispania.Core → Media` reference. The future Core must be a new composition root, not a rename of `apps/server`. If `apps/server` were treated as Core today, it violates the intended boundary by direct references including:

- Media business: `application/library/Hispania.Library.Application.csproj`, `application/playback/Hispania.Playback.Application.csproj`, `application/metadata/Hispania.Metadata.Application.csproj`, `core/domain/Hispania.Media.Domain.csproj`, `contracts/library`, `contracts/playback`, and Media module `modules/playback-conservative`.
- Concrete Media providers/adapters: `infrastructure/library/Hispania.Library.LocalFileSystem.csproj`, `infrastructure/ffprobe/Hispania.Library.FFprobe.csproj`, `infrastructure/transcoding-ffmpeg/Hispania.Transcoding.FFmpeg.csproj`, `infrastructure/metadata/Hispania.Metadata.Tmdb.csproj`, `apps/ffmpeg-worker`, and Library SQLite/PostgreSQL persistence projects.
- Business module: `application/inventory/Hispania.Inventory.Application.csproj`, `contracts/inventory/Hispania.Inventory.Contracts.csproj`, `infrastructure/persistence/inventory-sqlite/Hispania.Inventory.Persistence.Sqlite.csproj`.
- Development fixtures: `contracts/architecture-test`, `modules/architecture-test`, and `contracts/extension-demo` are direct server composition/test/demo references and must not leak into a production Core artifact.

Other important qualifications:

- `core/domain/Hispania.Media.Domain.csproj` is **Media domain despite the `core/` path**; folder naming does not make it platform Core.
- `contracts/playback` directly references Media Domain; `application/library`, `application/metadata`, and `application/playback` also reference Media Domain. These are valid Media-suite edges but forbidden from Core/shared-platform contracts.
- `core/runtime/Hispania.Runtime.csproj` directly references `contracts/module-system` and `contracts/platform`, not Media or Inventory implementations. It is a promising reuse candidate, but runtime breadth/dependencies still require review.
- `contracts/platform/Hispania.Platform.Contracts.csproj` has no project references in HEAD. `contracts/module-system` is similarly platform-owned. Keep architecture fitness tests to preserve that property.
- `apps/worker-agent` references Runtime and `contracts/extension-demo`; the agent itself is generic in role, but a production Worker package should not ship demo-only contract dependencies as mandatory payload.
- `Hispania.Platform.slnf` contains module-host, worker-agent, platform persistence, extension-demo contracts, and architecture-test projects. It excludes Media/Inventory implementation but is not a clean production Core package definition.
- Web `webModules.ts` provides a compile-time route registry for existing pages; Media and Inventory are bundled/static. It is not a forbidden assembly reference, but it is not dynamic module contribution loading.

No forbidden edge above was corrected during Phase 0.

## 6. Core Empty Contract and Phase 1 scope split

| Empty Core capability | Phase 1 requirement? | Rationale / later completion |
|---|---|---|
| Boot with zero functional modules | **Required** | Central Phase 1 acceptance gate. |
| Health/readiness endpoint | **Required** | Proves host and platform store status without business module. |
| Authenticated administrator bootstrap/login | **Required, minimal** | Establish secure admin boundary; full users/SSO/roles can mature later. Must not leave admin mutation anonymous. |
| Load and validate platform configuration | **Required** | Invalid config fails clearly; module config remains absent until installed. |
| Open Core-owned persistence | **Required** | A provider-neutral Core persistence contract is mandatory; SQLite is the approved official v1.0 provider. Schema version and health are visible. PostgreSQL Core is out of scope. |
| Register module runtime/catalogue | **Required** | Empty list is valid and observable. Reuse current ModuleRuntime/catalogue after dependency review. |
| Enumerate installed modules | **Required** | Empty result and health visible. |
| Install a compatible signed module | **Required at least for a harmless fixture module** | Proves the Core is extensible. Fixture uses a test publisher explicitly trusted by the operator. Not Media/Inventory installation or general third-party support. |
| Disable/deactivate a module | **Required for fixture** | Demonstrates failure isolation and lifecycle state. |
| Recover from module failure | **Required** | Core/admin remain available; restart/disable/rollback path bounded. |
| Worker administration | **Core API and empty state required; live multi-site execution may defer** | Worker registry contract is platform. Phase 1 need not implement remote connectivity/site topology; preserve current worker identity/capability boundaries and approved owner-managed trust model. Keep Core independent of concrete LAN/VPN/overlay products and do not require public worker listeners. |
| Scheduler/coordinator | **Core boundary/API and no-module initialization required; remote scheduling may defer** | Empty coordinator can have no task providers. Do not add new scoring or change scheduler in Phase 1. |
| Audit of admin/module lifecycle | **Required minimal** | Record actor/action/result/time/correlation for bootstrap and package lifecycle. |
| Restart and state recovery | **Required** | Core config, admin identity, module inventory, trust metadata persist. |
| Upgrade Core | **Required as a controlled in-place test or documented package flow** | No general multi-module transaction in Phase 1; must retain/reopen Core state. |
| Backup/restore Core-owned state | **Core contract required; minimal working flow required before public beta** | Phase 1 should include a simple versioned backup/restore for Core DB/config or explicitly stage it as a short follow-up before Fase 2. The audit's v1 asks for tested backup/restore. |
| Full module DB backup/export/migrations | **Later (Phase 3)** | Needs module ownership and compatibility format. |
| Media/Inventory routes and domain functionality | **Forbidden** | Modules arrive in later phases. |
| Marketplace / public registry | **Out of scope** | No need to boot empty Core. |
| Dynamic module UI | **Extension point only** | UI contribution contract is designed now; runtime loading can follow Inventory reference module phase. |

Phase 1 should not add generic scheduler scoring, Data Plane changes, PostgreSQL Core, a WASM runtime or hostile-code sandbox, Site Node, WOL, IoT, multi-site discovery, new tunneling, or module-specific persistence. It must preserve the approved network direction: private owner-managed infrastructure connectivity for multi-site Control Plane and separate HTTPS client access, without coupling Core to a VPN product or requiring public worker listeners. Implementing the multi-site network and CG-NAT connectivity is not part of the Empty Core gate. Reuse existing implementations where valid; create a separate composition root and artifact.

## 7. Architecture Fitness Tests

No fitness tests were implemented in Phase 0. This table gives intended phase and test shape.

| Test | Assertion | Phase | Notes |
|---|---|---|---|
| A — Core project reference rule | Build graph of designated Core projects has no references/transitive compile dependencies to Media domain/apps or adapters. | **Fase 1** | Use MSBuild evaluated references or project XML plus build output; allowlist composition boundary explicitly. |
| B — No Inventory dependency | Core graph/package excludes Inventory application, contracts, persistence, and UI. | **Fase 1** | Separate from Media check for diagnostics. |
| C — Core artifact assemblies | Published Core inventory has no Media assemblies. | **Fase 1** | Check assembly names and package manifest, not source folder names alone. |
| D — Core artifact Inventory | Published Core inventory has no Inventory assemblies. | **Fase 1** | Same artifact-level check. |
| E — Core migrations | Core artifact/schema contains no Media or Inventory migrations/tables. | **Fase 1** | Assert package contents and fresh DB schema ownership. |
| F — Core Web boundary | Empty Core routes/pages contain no Media or Inventory business routes; shell offers empty-state module registry. | **Fase 1** | Route list from host, and built Web output/manifest; don't mistake static webModules registry for runtime contribution. |
| G — Platform contracts are domain-neutral | Platform and ModuleSystem contract assembly reference graph has no domain assembly; namespace/type scan catches direct Media/Inventory symbols. | **Fase 1** | Existing Platform project boundary is a useful baseline. |
| H — Module A cannot reference B implementation | Architecture test fails project reference, assembly dependency or module manifest dependency to implementation; only public capability contract allowed. | **Fase 2/4** | Requires module package fixtures and dependency graph policy. |
| I — Cross-module SQL FK prohibited | Migration/schema integration inspection rejects FK target outside module-owned schema; separate DB connection prevents hidden joins. | **Fase 3** | No global SQL schema can prove this alone; contract plus per-module DB isolation. |
| J — Empty Core process journey | Clean boot → health → admin auth → module list empty → restart → health, with persistent state. | **Fase 1** | Separate process test, no in-memory test server only. |
| K — Module fault containment | Fixture crashes/hangs; Core health/admin survive and module can be disabled/recovered. | **Fase 1** | Tests restart/backoff bounds; platform-specific resource enforcement needs OS CI. |
| L — Package security baseline | Traversal, hash mismatch/tampering, revoked or unknown publisher, incompatible host all fail closed before execution. | **Fase 1** | Reuse current package test suite; full per-resource permission enforcement remains Phase 2. |
| M — Worker/Data Plane stale fencing | Old generation/grant/wrong worker/expired token cannot read/publish. | **Existing tests reuse; gate current SHA in Fase 1/5** | Phase 1 must not weaken existing validated guarantees, but no protocol rewrite. |
| N — Module dependency cycles | Install plan rejects cycles, unsatisfied minimum versions and missing hard dependency before mutation. | **Fase 2** | Existing manifests alone don't prove a complete solver. |

## 8. Current code classification and reuse matrix

### 8.1 Project/area classification

“Core future” means eligible for the future Core only after checking dependencies and package contents. `Legacy/transitional` does not mean delete; it means keep as current product composition until replaced.

| Project / area at HEAD | Future classification | Reason / Phase 1 treatment |
|---|---|---|
| `core/kernel/Hispania.Kernel.csproj` | Core candidate | Small base library with no project refs; reuse after scope review. |
| `core/runtime/Hispania.Runtime.csproj` | Core candidate | Runtime services and lifecycle; refs platform/module contracts only, though generic runtime breadth should be audited. |
| `core/domain/Hispania.Media.Domain.csproj` | Module (Media) | Domain is explicitly Media-owned despite path. Forbidden from Core. |
| `contracts/platform` | Shared Contract | Domain-neutral platform APIs; enforce with fitness test. |
| `contracts/module-system` | Shared Contract | Module/package/worker protocol; review split and version promises before freeze. |
| `contracts/library`, `contracts/metadata`, `contracts/playback` | Module contract (Media) | Public within Media suite; some reference Media types. Not Core shared contract. |
| `contracts/inventory` | Module contract (Inventory) | Inventory-owned API; references Platform as needed. |
| `contracts/transcoding-worker` | Shared worker capability contract / Media provider contract | Worker protocol may be genericized; current FFmpeg semantics remain provider-specific. |
| `contracts/extension-demo`, `contracts/architecture-test` | Demo/test fixture | Must not be mandatory production Core package content. |
| `application/library`, `application/metadata`, `application/playback` | Module (Media) | Media business use cases. |
| `application/inventory` | Module (Inventory) | Inventory business use cases. |
| `infrastructure/platform/persistence-sqlite` | Provider (Core platform) | SQLite implementation for platform contracts; Core may consume behind provider boundary. |
| `infrastructure/persistence/distributed-work` | Provider (Core platform) | Current Coordinator SQLite persistence; reuse with single-Coordinator limit. |
| `infrastructure/persistence/inventory-sqlite` | Provider (Inventory) | Inventory-owned DB schema. |
| `infrastructure/persistence/Hispania.Library.Persistence.*` | Provider (Media) | Library-specific schema/adapters, including PG provider. |
| `infrastructure/library`, `infrastructure/ffprobe`, `infrastructure/metadata/Tmdb` | Provider (Media) | Filesystem, analyzer, and vendor-specific implementation. |
| `infrastructure/transcoding-ffmpeg`, `apps/ffmpeg-worker` | Provider/Worker (Media) | Concrete transcoder and its execution process. |
| `apps/server` | Legacy/transitional product composition root | Current product host is business-heavy. Do not rename/morph to Core; new root in Fase 1. |
| `apps/module-host` | Core candidate / operator runtime | Generic OOP module host. Reuse after protocol/isolation review; not proof of sandbox. |
| `apps/worker-agent` | Worker | Generic remote agent; current project also references demo contract. Keep separate from Coordinator/Core package. |
| `apps/web` | Shared shell + current modules, transitional | Static registry and bundled pages; carve Core shell and module contributions later. |
| `apps/cli` | Operator tooling prototype | Current CLI is `tmdb-smoke`, not platform administration; don't package as Core capability yet. |
| `tools/dev-launcher` | Developer/operator tooling | Useful local launch harness; not production service installer. |
| `.github/workflows/validation.yml` | CI | Strong Linux/Windows tests, PG and web gates; ARM64/packaging/backup gates absent. |
| `modules/playback-conservative` | Module (Media provider) | Existing capability implementation; not Core. |
| `modules/extension-demo`, `modules/architecture-test` | Demo/test fixtures | Reuse for lifecycle/architecture tests, exclude from product bundles. |
| Deployment/service installers | Decision needed / incomplete at HEAD | No unified product deployment root evidenced in project inventory; inspect during Fase 1 package design. |

### 8.2 Reuse matrix

| Current component | Reuse | Adapt | Replace | Retire | Reason |
|---|:---:|:---:|:---:|:---:|---|
| Kernel | ✓ | ✓ |  |  | Small dependency-free base; retain only true platform primitives. |
| Runtime/module registry/lifecycle | ✓ | ✓ |  |  | Good boundary foundation; test disable/fault/lifecycle and remove demo coupling. |
| `.hispania` package format | ✓ | ✓ |  |  | Inert packages, manifest and digest are valuable; package format must evolve only by explicit versioned migration. |
| RSA package signatures / verification / revocation | ✓ | ✓ |  |  | Reuse crypto and canonicalization tests; add durable trust and publisher namespace authorization later. |
| Collectible ALC | ✓ | ✓ |  |  | Useful trusted in-process mode; never count as security isolation. |
| Out-of-process host/supervisor | ✓ | ✓ |  |  | Retain crash/cancel/protocol behavior; enforce OS service identity/resource policy before third-party claim. |
| WorkerAgent and mTLS contracts | ✓ | ✓ |  |  | Existing enrollment/lease base; detach demo assembly dependency and formalize supported protocol window. |
| Lease/fencing persistence | ✓ | ✓ |  |  | Keep single-coordinator model; integration-test restore and clarify uncertain work. |
| Scheduler |  | ✓ |  |  | Current selection is useful baseline but not general declarative resource scheduler; do not rewrite in Fase 1. |
| Media transfer broker/grants | ✓ | ✓ |  |  | Strong scoped transfer baseline for FFmpeg; protocol and provider surface remain Media-specific. |
| A/B variant router | ✓ | ✓ |  |  | Generic abstraction can serve compatible capabilities; retain read-only shadow restrictions. |
| Platform SQLite stores | ✓ | ✓ |  |  | Lightweight default; backup/checkpoint and owner/schema isolation need gates. |
| Library SQLite + PostgreSQL adapters | ✓ | ✓ |  |  | Reuse as Media-owned provider pair; do not imply PostgreSQL Core support. |
| Inventory SQLite | ✓ | ✓ |  |  | Preserve DB ownership and no cross-domain FK as first module data source. |
| `apps/server` |  | ✓ | ✓ |  | Keep as current application during transition; future Core is a separate composition root, then old composition is a bundle/host. |
| `Hispania.Platform.slnf` |  | ✓ |  |  | Keep as build boundary; create a distinct package manifest and empty-Core process test. |
| Web static `webModules.ts` registry | ✓ | ✓ |  |  | Reuse route ownership concept; replace compile-time list with Core shell registry and versioned contributions over time. |
| `apps/cli` |  | ✓ | ✓ |  | Keep smoke command useful for dev; create operator CLI surface separately without conflating vendor smoke. |
| Dev launcher | ✓ | ✓ |  |  | Reuse development UX/test fixtures; not service install/update mechanism. |
| Current CI workflow | ✓ | ✓ |  |  | Preserve full Linux/Windows/PG/Web gates; add ARM/package/content/backup gates. |

### 8.3 Reuse principles

- No blanket promise that all current code is retained; use the table as a starting assessment, then gates decide.
- Do not move concrete providers into Core simply because they are dependencies of the current server.
- Existing integration tests and manifests are test material even when demo projects are excluded from product artifacts.
- Keep compatibility adapters around old APIs while the new composition root is introduced; data migration precedes deletion of legacy paths.

## 9. OWNER DECISIONS

Recommendations below are not approvals. The owner should reply with selections or edits before the corresponding public contract/support statement is frozen. “Difficulty to change” means after v1 consumers/data exist.

### 9.1 Core persistence — APPROVED

**OWNER DECISION 9.1 — APPROVED**

For HISPANIA v1.0:

- SQLite is the officially supported Core persistence provider.
- Core must consume a provider-neutral persistence contract; SQLite is an implementation/provider, not the Core's public contract.
- The contract should be only as general as current Core needs require. Do not introduce a speculative persistence framework or provider surface unsupported by repository use cases.
- PostgreSQL is **not** an officially supported Core provider in v1.0. It may be added later without breaking public Core contracts.
- Modules own their data and may select their own persistence providers, including SQLite, PostgreSQL, or another backend, without imposing that choice on Core.
- The existing Library/Media PostgreSQL adapter belongs to the future Media module. It does not constitute PostgreSQL support for Core and must not be moved into or treated as a Core provider.

```text
HISPANIA Core
    -> Core Persistence Contract
         -> SQLite Provider (OFFICIAL v1.0)
         -> Future providers (NOT OFFICIAL v1.0)

Media -> module-owned persistence -> SQLite / PostgreSQL
Inventory, ERP, and other modules -> their own persistence policies
```

This decision selects SQLite-only as the official Core provider for v1.0. It does not constrain module-owned storage choices or approve changes to the existing Media PostgreSQL adapter.

### 9.2 Module Trust Model — APPROVED

| Option | Advantages | Disadvantages / short term | v1 effect | Future change difficulty |
|---|---|---|---|---|
**OWNER DECISION 9.2 — APPROVED**

For HISPANIA v1.0:

- First-party modules are supported.
- Native modules from publishers explicitly trusted by the operator are supported.
- Every installable module must be signed and pass HISPANIA's integrity, compatibility, and publisher-trust checks. The policy fails closed.
- A valid signature identifies the publisher and artifact integrity; it does **not** establish that the code is safe.
- Publisher trust and permissions granted to a module are separate decisions. Signing does not grant permissions.
- Third-party native modules are treated as trusted code only by the operator's explicit decision.
- Out-of-Process execution provides fault isolation, resource limits, and supervision; it is **not a security sandbox**. Collectible `AssemblyLoadContext` is not a sandbox either.
- Untrusted or adversarial native modules are **not supported in v1.0**. HISPANIA makes no malicious-code isolation promise for native modules.
- WASM and other sandbox runtimes are deferred beyond v1.0 with no committed delivery date. A future validated sandbox may be proposed without changing the Core principle of capability/permission enforcement.
- A module cannot run if its publisher is not authorized. It must declare requested permissions and gets no automatic filesystem, network, secret, database, scheduler, GPU, or other resource access merely because it is signed. The Core enforces each permission wherever resource enforcement exists; a permission without an enforcement path must not be represented as protected.

```text
Module Trust Tiers

Tier 1: First-party signed module
        -> trusted

Tier 2: Trusted signed publisher
        -> operator explicitly trusts publisher/code
        -> permissions still granted separately

Tier 3: Untrusted native module
        -> NOT SUPPORTED v1.0

Tier 4: Sandboxed untrusted module
        -> FUTURE, after sandbox validation
```

Security shorthand:

```yaml
Signed != Safe
Trusted Publisher != Full Permissions
OOP != Sandbox
ALC != Sandbox
```

This approval governs the trust model. It does not by itself define the breadth of the public module ecosystem; that scope is recorded separately in Owner Decision 9.3.

### 9.3 Third-party Module Promise — APPROVED

**OWNER DECISION 9.3 — APPROVED: Public API + limited ecosystem.**

HISPANIA v1.0 will expose a documented public module API for a limited ecosystem. The promise is bounded: it does not mean full support for arbitrary third-party modules, unrestricted publisher enrollment, or a public marketplace. Any third-party native module remains subject to the approved 9.2 trust model: signed, integrity/compatibility checked, and from a publisher the operator explicitly trusts. Marketplace scope remains separately open under 9.10. The SDK packaging, compatibility window, and onboarding/support process must be specified before inviting external module authors; this decision does not implement them.

| Option | Advantages | Disadvantages / short term | v1 effect | Future change difficulty |
|---|---|---|---|---|
| A. First-party modules only | Allows API churn during extraction. | No independent ecosystem; must avoid advertising stable external SDK. | Internal contracts can be versioned and tested. | Low to broaden before public consumers. |
| B. Public API, limited ecosystem | Establishes compatibility and lets a few trusted integrators test. | Support obligations and compatibility window begin; no marketplace required. | Publish SDK + sample + compatibility policy; curated trust. | Medium. |
| C. Full third-party support | Strong extensibility promise. | Need docs, registry, signer namespace, support, security response, upgrades, UI and data migrations. | Materially expands v1 scope. | High if later retracted. |

The selected option is **B**. The recommendation for a small supported API surface and curated/trusted publishers is consistent with the selected scope; it does not expand that scope into full third-party support.

### 9.4 Remote Worker Threat Model — APPROVED

**OWNER DECISION 9.4 — APPROVED: OWNER-MANAGED TRUSTED WORKERS.**

HISPANIA v1.0 supports workers that the owner/operator explicitly authorizes and trusts. They may be in the Coordinator's LAN, another home or physical site, behind NAT or CG-NAT, connected through an operator-managed VPN, associated with a future local Site Node, or hosted on a remote server controlled by the owner. Physical location is not the trust boundary; explicit operator authorization and trust are. Support for those topologies is an outcome requirement. The supported network direction is recorded in 9.5 and the approved Data Plane routes are recorded in 9.6.

The operator is trusting the worker machine, OS, administrator, execution environment, WorkerAgent software, and the resources accessible to that worker. A trusted remote worker may inspect the private bytes it is authorized to process. HISPANIA does not claim to protect those bytes from the administrator or a compromised OS of an authorized worker.

```text
mTLS != host integrity
mTLS != attestation
trusted worker != hostile worker
```

mTLS authenticates the node, protects the channel, and enables access control/revocation. It does not prove that the OS is uncompromised, prevent a remote administrator from inspecting data, prove hardware executed the expected program, or provide attestation/confidential computing. v1.0 does not support arbitrary Internet workers, hostile workers, open public compute, confidential execution, remote attestation, execution over an adversarial OS, or protection against an authorized worker's administrator.

For each job, the operator's trust in a worker does not grant blanket task authority. Keep these concepts separate:

| Concept | Meaning |
|---|---|
| Worker identity | Which enrolled node is connecting. |
| Worker trust | Whether the operator trusts the machine and its administrators/environment. |
| Allowed capabilities | Which operations this node is authorized to execute, e.g. FFmpeg or AI; this does not imply storage/admin. |
| Task authorization | Whether this worker may execute this specific task with these inputs and outputs. |

For private data, the operator accepts that an authorized worker can observe task bytes. HISPANIA should minimize the data sent, issue scoped and short-lived grants, avoid permanent source credentials and unnecessary physical paths, and revoke access when work ends or is revoked. Do not imply protection from the trusted worker's administrator.

v1.0 assumes explicit enrollment, unique worker identity, operator authorization, mTLS, per-node capability allowlists, revocation, certificate rotation, leases/fencing, and audit of enrollment/removal. Reuse existing primitives; do not redesign them in Phase 0. Trust level and capabilities stay distinct. Contracts should leave room for future worker trust classification, but Phase 0 does not add a `WorkerTrustLevel` enum or related implementation.

### 9.4.1 Multi-site direction — future architecture

HISPANIA is intended to span multiple physical sites:

```text
                    HISPANIA Core
                         |
               private control network
                 /       |       \\
             Site A    Site B    Site C
               |         |         |
            Site Node Site Node Site Node
               |         |         |
             Workers   Workers   Workers
```

A site represents a physical location or LAN. A future **HISPANIA Site Node** may be a lightweight persistent agent on a Raspberry Pi, mini PC, home server, or other always-on device. It is not necessarily a heavy execution Worker. Potential responsibilities include maintaining Core connectivity, representing network locality, local worker discovery/health, Wake-on-LAN, IoT/MQTT gateway, ESP32/sensor telemetry, site health, local control/relay where approved, locality reporting, and bootstrap/recovery support. This is an architectural direction only; no Site Node is implemented or required for v1.0/Phase 1 by this decision.

Wake-on-LAN, if added, should be executed by an authorized Site Node on its own LAN: Coordinator → Site Node wake request → local broadcast to Worker. The Coordinator should not depend on sending broadcasts into remote LANs. Do not implement WOL in Phase 0 or Phase 1.

IoT devices such as ESP32 should normally communicate over local Wi-Fi/MQTT or another local protocol with a Site Node, rather than each connecting directly to Core over the Internet. Future site capabilities/telemetry might be identified as `site.<site-id>.*`; no IoT behavior is part of this freeze.

Future scheduler policy may use a site identifier as a locality hint, e.g. prefer a GPU Worker in the same site as a NAS. Do not implement locality scoring now. Future phases must decide Site identity/registry, Site-to-Core protocol, Site Node package/runtime, local discovery, health/capabilities, WOL/IoT providers and multi-site scheduler hints. These are not Phase 1 P0 requirements.

Endpoint DNS names or public domains may be used for addressing, but a domain name is not a security boundary. Security continues to come from TLS/mTLS, authorization, certificate lifecycle/revocation, firewall and policy. Reverse SSH may be an operator bootstrap/recovery/troubleshooting tool; it is not the primary HISPANIA transport and must not become a Worker protocol or Data Plane dependency.

### 9.5 Network supported in v1 — APPROVED

**OWNER DECISION 9.5 — APPROVED: PRIVATE MULTI-SITE INFRASTRUCTURE + PUBLIC HTTPS CLIENT ACCESS.**

HISPANIA v1.0 treats Control Plane, Data Plane, client access, and administration/recovery as separate network surfaces. They need not use the same route, protocol, or exposure. The supported direction covers one or multiple homes/sites and LANs, owner-managed Workers/Site Nodes behind NAT or CG-NAT, and local or remote clients including mobile networks, TVs, tablets, computers, native apps, and browsers. No worker site is required to open inbound ports.

#### Control Plane and multi-site infrastructure

- Direct LAN connectivity is supported for local deployments.
- Remote/multi-site infrastructure connectivity uses an owner-managed private VPN/overlay or equivalent network. WireGuard, Tailscale, Headscale, or another equivalent are valid implementation choices, not contractual HISPANIA Core dependencies.
- The platform depends on the required properties: reachability between authorized nodes, node identity, TLS/mTLS, valid routes, and access policy. Product-specific connectivity remains outside Core domain contracts.
- Design for remote nodes to initiate outbound connections where practical. Workers and Site Nodes do not need public IPs, public DNS, or publicly exposed administrative listeners by default.
- Existing VPN/overlay infrastructure may provide NAT traversal, including for CG-NAT. HISPANIA v1.0 will not implement its own STUN, TURN, ICE, hole punching, or proprietary NAT traversal.
- Direct WAN listeners/port forwarding may be technically possible but are not recommended, required for official support, or a prerequisite for any Core capability. No HISPANIA global relay, public worker discovery, or Internet-wide worker federation is required.

#### Client access and public gateway

- Clients must be able to use installed services over LAN/Wi-Fi and, when the operator publishes them, over Internet HTTPS using browser or native clients, including 4G/5G access.
- Client access is separate from the private infrastructure network; ordinary service use must not require clients to join the infrastructure VPN.
- A public HTTPS Gateway/reverse-proxy surface is an architectural concept. It may be provided by HISPANIA or external infrastructure as designed later; Phase 0 does not implement a new proxy.
- DNS/domain is addressing, not a trust boundary (`DNS/domain != trust boundary`). Security must come from TLS, user authentication, authorization, session/token policy, firewall, and correct gateway configuration. DNS names are not node identity or authorization.

#### Administration, recovery, and local sites

- Prefer LAN or private VPN for administration. Public administration is not a v1.0 requirement; any future remote exposure needs explicit additional controls. Worker/Site Node internal endpoints are not made public merely because a domain exists.
- Reverse SSH may be used operationally for recovery, debugging, bootstrap, troubleshooting, or emergency access. `Reverse SSH != HISPANIA transport`; it is not a Worker protocol, scheduler/Data Plane contract, or normal Site protocol.
- Preserve the approved future Site Node direction: local LAN functions such as discovery, health, Wake-on-LAN, MQTT/IoT, ESP32 telemetry, locality, and recovery may be provided at the site. Do not implement these functions in Phase 0 or Phase 1. IoT devices need not each hold a HISPANIA certificate or join the global VPN; Wake-on-LAN is executed by an authorized Site Node on its own LAN, not by routed Internet broadcasts.

#### Data Plane boundary

The Control Plane may authorize, schedule, lease, observe, cancel, and audit while data bytes follow a different route. Do not assume the Control Plane overlay is the optimal Data Plane route. The approved Data Plane routes and independent Gateway fallback are recorded in Owner Decision 9.6. Phase 1 only preserves these boundaries; it does not implement the v1.0 Data Plane.

#### Phase 1 implications

Phase 1 must preserve provider/product-neutral Core and Worker contracts, avoid dependencies on Tailscale/Headscale/WireGuard, avoid public Worker listeners, and not require a Worker public IP or DNS. It does not implement the v1.0 Data Plane paths, Gateway, full multi-site networking, Site Node, new tunneling/NAT traversal, or a new reverse proxy. It must preserve the Control Plane/Data Plane boundary and leave room for versioned, module-neutral data transfer contracts; do not change Worker/Data Plane protocols unless strictly required by the Empty Core contract. No speculative network abstraction should be added without a concrete Phase 1 need.

The approved v1.0 network direction is **PRIVATE MULTI-SITE INFRASTRUCTURE + PUBLIC HTTPS CLIENT ACCESS**. The Data Plane policy and route requirements are specified in Owner Decision 9.6 below.

### 9.6 Data Plane Architecture — APPROVED

**OWNER DECISION 9.6 — APPROVED: DIRECT-FIRST DATA PLANE + INDEPENDENT DATA GATEWAY FALLBACK.**

HISPANIA v1.0 separates the Control Plane from the Data Plane. The Coordinator owns identity/authorization, Worker selection and scheduling, grants, leases/fencing, supervision, cancellation, audit, and selection/authorization of data routes. It must not be a mandatory proxy for large media, file, AI-model, or other bulk payloads.

#### Approved route categories

**A. Direct Data Plane — preferred when reachable and authorized**

```text
Storage → Worker → Client
Storage → Worker A → Worker B → Client
```

Source-to-Worker and authorized Worker-to-Worker traffic should be direct where a supported route exists. Direct Worker-to-Client delivery is required when the endpoint is reachable and the route is authorized.

**B. Private multi-site Data Plane**

```text
Site A Storage → private VPN/overlay → Site B Worker → authorized destination
```

Data may cross owner-managed LAN/VPN/overlay infrastructure without passing through the Coordinator process. Reachability and Data Plane authorization remain separate requirements.

**C. Independent Data Gateway — required fallback**

```text
Worker → authenticated data connection → independent Data Gateway → HTTPS → Client
```

When the client cannot reach a Worker directly, a public HTTPS Data Gateway must be available. It is an independently deployable infrastructure responsibility and may run on another machine; it must not require the Coordinator process or a Raspberry Pi Coordinator to relay all streams. Its implementation may later be a HISPANIA process, dedicated gateway service, or adapter over existing infrastructure. This decision freezes separation of process/resources, not a specific implementation technology. This Gateway is a data-delivery fallback, not a general public Worker listener or a HISPANIA-operated global relay.

#### Mandatory v1.0 acceptance objectives

1. Demonstrate a verifiable Control Plane/Data Plane separation.
2. Transfer supported source/storage input directly to a Worker over supported networks.
3. Deliver Worker output directly to a Client when the authorized endpoint is reachable.
4. Deliver publicly over HTTPS through an independent Data Gateway when direct Client-to-Worker delivery is not viable.
5. Authenticate and authorize every participant and route.
6. Use ephemeral grants scoped to operation, resource, and destination.
7. Verify expiry, revocation, and cancellation behavior.
8. Enforce concurrency/resource quotas, transfer limits, and backpressure.
9. Supervise work without transporting payloads through the Coordinator.
10. Fail closed when no authorized route is available; do not silently downgrade to insecure HTTP or an undisclosed Coordinator relay.
11. Test and instrument that the Coordinator is not acting as a data relay for routes declared direct or Gateway-delivered.
12. Validate consistency with approved network policy 9.5.
13. Support direct transfer between authorized Workers over LAN/private VPN for the v1.0 reference pipeline; this does not require arbitrary workflow orchestration.

These are acceptance objectives, not claims about current implementation. No objective is considered implemented without route-specific code and reproducible acceptance evidence.

#### Security and network constraints

- Data Plane channels are encrypted and authenticated. No silent fallback to HTTP is permitted.
- A transfer grant is not an administrative credential. A Worker receives only the task-scoped source/output access it needs, never global storage access by virtue of running a task.
- A Gateway receives no implicit administrative authority. It enforces route grant scope, expiry, revocation, destination, and authentication.
- Do not log secrets or complete tokens. Limit simultaneous transfers, bytes, and duration where applicable; propagate cancellation and backpressure.
- Do not require inbound connectivity at every home. For CG-NAT, use an existing private overlay or outbound authenticated connection to an accessible Gateway; direct routes are used only when possible. HISPANIA v1.0 does not require custom P2P NAT traversal, QUIC, STUN, TURN, or ICE.
- Coordinator and Workers may coordinate a bounded pipeline (e.g. NAS → FFmpeg Worker → AI Worker → destination) without Coordinator receiving content. Arbitrary workflow engines are out of scope unless separately approved.
- Expose Data Plane capabilities through versioned, module-neutral platform contracts. Modules request transfers through contracts; they do not create parallel Worker identity, issue administrative grants, depend on Coordinator-internal routes, or require Core to know Media/FFmpeg/AI.

#### Repository capability and gap assessment

Assessment is against committed starting HEAD `93b9204c88da6db3f502cbde6d7ae2911de6ae00`. Code inspection and existing test source were reviewed; tests were not run in this documentation update. “IMPLEMENTED” is limited to the named existing scenario, not generic v1.0 readiness. Uncommitted worktree changes are excluded from shipped-state evidence.

| Capability | Status at inspected HEAD | Evidence / gap |
|---|---|---|
| Coordinator scheduling, Worker identity, leases, fencing, heartbeat and cancellation | **PARTIAL** | `core/runtime/DistributedWorkCoordinator.cs` and `contracts/module-system/DistributedWorkerContracts.cs` provide a real but capability-specific distributed worker path. It is not a generic module task/data scheduler with all v1.0 route selection policy. |
| Short-lived source/result transfer grant | **PARTIAL** | `apps/server/DistributedMediaTransferRegistry.cs` binds transfer ID/token to job, expiry, source/result limits and source callback; range-bounded reads, result hash/size checks, opaque published handle and job revocation exist. Grant is served through Coordinator worker endpoints and is not yet a generic operation/resource/destination-scoped platform grant. |
| Worker transfer endpoints | **PARTIAL** | `apps/server/DistributedWorkerEndpoints.cs` serves `media/grant`, `media/source`, and `media/result` under the Coordinator's `/internal/worker/v1` HTTPS/mTLS surface. This is a Coordinator-mediated transfer route, not a separate direct Data Plane endpoint. |
| Source/storage → Worker direct | **NOT IMPLEMENTED** | `infrastructure/transcoding-ffmpeg/DistributedFfmpegCapabilityHandler.cs` downloads source blocks from Coordinator `media/source`; the source callback is brokered there. Existing implementation transfers bounded blocks but does not bypass Coordinator. |
| Remote FFmpeg source→Worker→result workflow | **IMPLEMENTED — NARROW MEDIA SCENARIO** | `DistributedFfmpegCapabilityHandler.cs` plus `tests/playback/DistributedWorkerProcessIntegrationTests.cs` cover the specific remote FFmpeg flow, using local temporary files and Coordinator-mediated source/result transfer. This does not prove the approved direct routes or independent Gateway. |
| Result upload, integrity, size caps, stale lease protection and interrupted upload cleanup | **IMPLEMENTED — NARROW MEDIA SCENARIO** | `DistributedMediaTransferRegistry.cs` and `tests/playback/DistributedMediaTransferTests.cs` cover bounded/hash-checked result publication, stale generation rejection, cleanup and cancellation behavior. Existing results are stored/published by Coordinator broker; limits are not a complete general quota/backpressure policy. |
| Worker → Client direct | **NOT IMPLEMENTED** | No versioned audience-bound Worker-to-Client transfer endpoint/grant was identified. Current remote FFmpeg results return through Coordinator broker/cache/playback path. |
| Independent Data Gateway → Client HTTPS | **NOT IMPLEMENTED** | No independently deployable Data Gateway process/contract or route was identified in committed code. Existing client HTTPS/server playback behavior does not demonstrate this approved fallback. |
| Worker → Worker over LAN/private VPN | **NOT IMPLEMENTED** | No peer transfer contract, authorization route, or end-to-end Worker-to-Worker pipeline was identified. The audited NAS→Worker→Worker→Client path remains planned. |
| Data Plane protocol version independent of Worker control RPC | **NOT IMPLEMENTED** | Worker protocol V1 exists, but no separate, negotiated/versioned generic Data Plane protocol for these routes was identified. |
| Cancellation/revocation across all approved routes | **PARTIAL** | Coordinator job cancellation, worker cancellation instructions, generation fencing, job grant revocation, and narrow interrupted-transfer tests exist. Gateway/client/peer-route revocation and end-to-end route-wide behavior are absent. |
| Streaming, ranges, backpressure, aggregate quotas and observability for all routes | **PARTIAL** | Source reads use capped blocks and result uploads stream to bounded temporary files; current Worker downloads the source to a local temporary file before processing. End-to-end direct stream, resumability policy, global concurrent-transfer quotas, route metrics and Gateway backpressure are not verified/implemented. |

The table records present evidence and gaps; it does not authorize implementation during Phase 0 or Phase 1.

#### Roadmap and Phase 1 boundary

Phase 1 is limited to preserving the Control Plane/Data Plane boundary and neutral/versionable contract seams needed later. It must not implement the direct route matrix, Worker-to-Worker data movement, or Data Gateway. Phase 5 is the explicit **Data Plane v1 implementation and validation gate** before v1.0 support: freeze protocol/grant semantics, implement direct and independent Gateway paths, exercise cancellation/revocation/limits/backpressure/fail-closed behavior under network and Worker failure, capture traffic proving Coordinator bypass, and gate the Media reference adapter. Phase 6 integrates Media through those platform contracts; Phase 9 must publish the validated route/support matrix. No existing implementation or test source alone passes these new acceptance gates.

### 9.7 ARM64 / Hardware Support — APPROVED

**OWNER DECISION 9.7 — APPROVED: MULTIPLATFORM CORE + ARM64 FIRST-CLASS COORDINATOR.**

An official target decision is not a validation result. Each target remains **OFFICIAL TARGET — PENDING VALIDATION** until its reproducible acceptance gates pass; only then may it be called **OFFICIALLY SUPPORTED — VALIDATED**. Keep **EXPERIMENTAL** and **OUT OF SCOPE** distinct. A build, capability flag, short smoke, or result for a different artifact/branch does not establish certification.

#### Approved support matrix

| Platform / role | v1.0 owner decision | Current validation status |
|---|---|---|
| Linux x64 Coordinator | **OFFICIAL TARGET** | Pending v1.0 release gates. |
| Windows x64 Coordinator | **OFFICIAL TARGET** | Pending v1.0 release gates. |
| Linux ARM64 Coordinator | **OFFICIAL TARGET** | Pending v1.0 release gates. |
| Raspberry Pi 4B 8 GB Coordinator | **PRIORITY OFFICIAL TARGET** | Pending Pi-specific Coordinator acceptance; existing Platform-only staging is partial evidence only. |
| Raspberry Pi 3B+ Coordinator | **OUT OF OFFICIAL SCOPE** | No v1.0 Coordinator support promise. |
| Linux x64 Worker | **OFFICIAL TARGET** | Pending v1.0 release gates. |
| Windows x64 Worker | **OFFICIAL TARGET** | Pending v1.0 release gates. |
| Linux ARM64 Worker | **OFFICIAL TARGET, SUBJECT TO VALIDATION** | Pending native Worker/control/data-path validation. |
| CPU software Worker | **OFFICIAL TARGET** | Pending package/workload validation; GPU is not required. |
| Generic GPU Worker | **EXPERIMENTAL** | No universal GPU support promise. |
| NVIDIA GTX 950 | **EXPERIMENTAL** | Reference hardware only; no codec/driver certification. |
| NVIDIA GTX 1060 6 GB | **EXPERIMENTAL** | Reference hardware only; no codec/driver certification. |
| ARM64 Site Node | **FUTURE TARGET** | Not implemented or required in Phase 1. |
| Raspberry Pi 3B+ as lightweight Worker/agent | **EXPERIMENTAL, IF INVESTIGATED** | Not a support promise; Pi 3B+ Coordinator remains out of scope. |

HISPANIA must keep the Coordinator useful on low-power hardware. Its responsibilities include identity/authentication/authorization, configuration, module lifecycle, Worker registry, scheduling, grants, leases/fencing, health, audit, Core persistence, supervision, and Data Plane coordination. It must not require a dedicated GPU, heavy media transcoding, local AI models, large RAM, media-scale storage, or a hardware vendor. Heavy work is delegated to Workers. This follows approved 9.6: the Coordinator is not a mandatory relay for bulk data.

The Core must not have mandatory dependencies on NVIDIA/CUDA, FFmpeg/NVENC/NVDEC, AI models, x64-only instructions, or Raspberry Pi-specific hardware. Hardware implementations belong behind neutral module/provider/Worker/adapter contracts. CPU software processing is the baseline official Worker target; advertised capabilities must reflect actual runtime availability.

#### Raspberry Pi 4B 8 GB Coordinator profile

Reference profile: Raspberry Pi 4B, 8 GB RAM, Linux ARM64, HISPANIA Coordinator, Core SQLite, Worker registry/scheduler, module lifecycle, Control Plane, and administration. USB SSD or M.2 storage may be used; installation must not require a particular SD card. Record the exact physical configuration, power, storage, and thermal profile during qualification.

Before promotion to **OFFICIALLY SUPPORTED — VALIDATED**, require reproducible evidence for:

- **Native ARM64:** clean Linux ARM64 build/publish and execution on Pi 4B; clean boot, authenticated administration, health/readiness, restart, and recovery.
- **Persistence:** SQLite durable writes, restart, backup, restore, integrity check, and recovery from interrupted operations.
- **Resources:** idle, registered-Worker, and administrative-load RAM/CPU/disk/log/concurrency measurements. Define acceptance thresholds before testing; this freeze invents no performance thresholds.
- **Workers:** multiple Worker registration, heartbeats, selection, leases, fencing, cancellation, revocation, disconnect/reconnect.
- **Multi-site:** Control Plane reachability over LAN and, where applicable, private VPN. Site Node is not a Phase 1 prerequisite.
- **Data Plane:** in Phase 5, demonstrate direct and independent-Gateway routes without making Pi 4 Coordinator carry payloads; measure Coordinator traffic.
- **Upgrade:** clean install, upgrade, restart, state persistence, supported rollback, and recovery from failed upgrade.
- **Stability:** sustained operation, service supervision, bounded memory behavior, disconnect handling, and safe restart.
- **Hardware:** stable power, temperature/throttling, storage health, and filesystem integrity. A short smoke is not a soak test.

Do not mark a gate passed without recording artifact/source identity, hardware/OS, commands or workflow, result, and relevant measurements and limitations.

#### ARM64 and hardware evidence reconciliation

A separate local worktree `step2-1-production-readiness` exists on branch `codex/hispania-0.7.0-step2-1-production-readiness`, at commit `1463ca5ce47866e8813a3499a34609c723808b2f`. It was clean when inspected. This commit is available locally (also named by a local `origin/...` tracking ref); no fetch was performed, so current remote state is unknown. It shares merge base `134076f5ba2894aba216e775c4588cc3c547ee31` with this Phase 0 branch and is not an ancestor of this branch. Its changes/evidence are not included in current product-code HEAD and must not be silently treated as shipped here. Local ARM64 CI branches/worktrees were also found: `codex/hispania-arm64-ci` at `71fbe890f08af0177f54041487df19097a23437e` and `codex/hispania-arm64-integration` at `d3a16e8bd89c5f431faa1b1c17324220c0591a10`. Inspection was read-only; no worktree was checked out, merged, or changed.

| Evidence item | Classification | What it establishes / limitation |
|---|---|---|
| Phase 0 product-code baseline `134076f5ba2894aba216e775c4588cc3c547ee31` and current Phase 0 branch | **VERIFIED IN CURRENT HEAD** | Existing validation workflow has Linux/Windows paths but no ARM64 job; it does not establish native Pi Coordinator support. |
| Step 2.1 candidate branch/commit `1463ca5...`; report says overall Step 2.1 **PARTIAL** | **VERIFIED IN OTHER LOCAL COMMIT** | Local source and reports exist, but the branch diverges from Phase 0 at the shared baseline. This is not a merged Phase 0 implementation or v1.0 release qualification. |
| Step 2.1 ARM64 publish/manifest scan | **REPORTED BUT NOT REPRODUCED** | `1463ca5:docs/HISPANIA-0.7.0-STEP-2-1.md` documents Linux ARM64 cross-publish, AArch64 SQLite binary, and artifact scans. Cross-publish is not native execution or release reproducibility. |
| Pi 4B native Platform-only production staging | **REPORTED BUT NOT REPRODUCED** | The Step 2.1 report records isolated install/check, bootstrap, readiness, authenticated endpoints, SQLite backup, same-version upgrade, restore-backed rollback, restart, and shutdown. The tested package came from a dirty worktree; Coordinator service/data/config/log/release paths were not accessed. See `1463ca5:docs/HISPANIA-0.7.0-STEP-2-1.md`, ARM64 validation sections. It is not v1.0 Core Coordinator qualification. |
| Pi 4B earlier SSH Platform-only smoke | **REPORTED BUT NOT REPRODUCED** | `1463ca5:docs/operations/PLATFORM-ONLY-ARM64.md` and `ARM64-LAB.md` record older working-tree artifact smoke for health/auth/SQLite restart; no stress/soak, production install lifecycle, or Coordinator service use. Artifact included a WindowsServices dependency at that time. |
| Pi 3B+ Platform-only smoke / manual Actions run | **REPORTED BUT NOT REPRODUCED** | `1463ca5:docs/operations/ARM64-CI.md` reports a bounded smoke success on a fixed working-tree artifact. Throttling/undervoltage `0x50005` was present; later Step 2.1-H skipped Pi 3 testing due to that preflight. Pi 3 Coordinator is now out of official scope; this does not validate ARM64 Worker support. |
| Pi 4 ARM64 integration workflow | **NOT VERIFIED for Step 2.1 artifact** | `1463ca5:docs/operations/ARM64-CI.md` documents a manual-only workflow whose provisioned hashes refer to an older artifact. Step 2.1 report says no Actions run validated its package on Pi 4. SSH staging is not an Actions result. |
| Pi 4 sustained Coordinator workload, multi-Worker control, Data Plane bypass, thermal/power/storage endurance | **NOT VERIFIED** | Reports explicitly say no stress workload and make no Data Plane/production deployment claim. These remain v1.0 acceptance gates. |
| Raspberry Pi 3B+ Coordinator | **NOT IMPLEMENTED / OUT OF SCOPE** | No official support is authorized. Lightweight Worker/agent remains only a possible experiment. |
| GPU / GTX 950 / GTX 1060 codec-driver matrix | **NOT VERIFIED** | No validated OS/driver/FFmpeg/codec/concurrency matrix; these remain experimental. |

Step 2.1 documentation also reports Windows x64 production-lifecycle validation on a dirty-source artifact, while native Linux x64 runner validation was unavailable. Its report documents incomplete/blocked gates and does not claim overall PASS. These results are useful follow-up evidence but do not erase branch, artifact, or scope boundaries. Future support evidence must reconcile to an identified clean source commit and release artifact.

#### Required release gate matrix

Every target below is required before full v1.0 release. A pass on one OS/architecture cannot substitute for another. Use each platform's native service/installer mechanisms; identical installation tooling is not required.

| Gate | Linux x64 | Windows x64 | Linux ARM64 / Pi 4B |
|---|---|---|---|
| Clean build | Required | Required | Required; native build or verified publish plus native run |
| Publish/package and artifact identity | Required | Required | Required |
| Native runtime | Required | Required | Required on ARM64; specifically Pi 4B for Coordinator profile |
| Service lifecycle | Required | Required | Required |
| Clean install | Required | Required | Required |
| Upgrade | Required | Required | Required |
| Restart/recovery | Required | Required | Required |
| SQLite persistence | Required | Required | Required |
| Backup/restore | Required | Required | Required |
| Worker enrollment | Required | Required | Required |
| Multi-Worker control | Required | Required | Required |
| Resource monitoring | Required | Required | Required, including Pi power/temperature/throttling/storage |
| Sustained operation | Required | Required | Required; a short smoke alone is insufficient |

Add OS-specific service, installer, rollback, filesystem, and hardware checks as appropriate. No mandatory skip may remain without a documented exception and explicit owner approval. If a required platform fails, mark it `TARGET NOT VALIDATED`; do not silently remove it from v1.0. Shipping with reduced support requires a separate explicit owner decision.

#### Phase 1 and roadmap impact

Phase 1 must avoid x64-only dependencies, preserve SQLite/Linux ARM64 compatibility, remain GPU- and FFmpeg-independent, keep the legacy server operating, and design tests for reuse on ARM64. Once an Empty Core artifact can execute, include a native Linux ARM64 build/publish and smoke at the earliest suitable checkpoint. This does not require completing the full Pi 4 sustained Coordinator qualification in Phase 1; that remains a release gate before v1.0.

The roadmap treats Linux x64, Windows x64, Linux ARM64, Pi 4B 8 GB Coordinator, and CPU software Workers as official targets pending their gates; GPUs remain experimental; Pi 3B+ Coordinator remains out of scope. Keep a physical-hardware qualification gate. Use the same Core architecture across supported platforms rather than creating a Raspberry Pi-specific Core.

### 9.8 Authentication for Private Data — APPROVED

**OWNER DECISION 9.8 — APPROVED: AUTHENTICATION REQUIRED BY DEFAULT + EXPLICIT PUBLIC ACCESS.**

HISPANIA v1.0 requires authentication by default, explicit authorization, permissions scoped by user/module/resource/operation, and anonymous access only when explicitly authorized. Local authentication must work without external identity providers or persistent Internet access. Sessions and device binding must be secure. User, device, module, Worker, and future Site Node identities are separate principals; LAN or VPN reachability does not grant access to private data.

```text
LAN access != authenticated identity
VPN connectivity != user authorization
Authenticated != authorized
Installed module != unrestricted access
Public endpoint != public private data
Route selection != authorization bypass
```

Keep four concepts distinct: **Identity** is who/what acts; **Authentication** proves that identity; **Authorization** decides what it may do; **Session** maintains and renews a valid authentication. Do not collapse them into a boolean `IsAuthenticated` policy.

#### Default access policy

Default is **deny**. A missing or unconfigured route policy must not make a route anonymous.

| Surface/resource | Approved v1.0 default |
|---|---|
| Core administration/configuration and module/user management | Authentication and explicit authorization required. |
| Worker enrollment | Explicit machine authorization; separate from user login. |
| Private module data, Media libraries/playback, Inventory, ERP, AI tasks, IoT commands | Authenticated identity plus resource/action authorization. |
| Module APIs | Private by default; platform policy must be applied server-side. |
| Public websites/shared resources | Anonymous only when explicitly configured, scoped, auditable, and revocable. |
| Health/readiness | Anonymous only for minimal non-sensitive status; operational detail remains protected. |

The Core owns platform identity/authentication, sessions, authorization decisions, user administration, revocation, audit, and secure bootstrap. Modules declare resources/actions/permissions through versioned neutral contracts; Core does not encode Media/ERP/Inventory/AI/IoT rules. Do not create incompatible identity systems per module or an oversized generic policy engine during Phase 1.

User authentication does not grant blanket module access. A signed/installed module does not gain private data permissions, and trusted native modules do not gain sandbox-like isolation. Preserve 9.2: module publisher trust, declared module capabilities, user identity/permissions, and per-operation authorization are separate controls. A module can access another module only through authorized platform contracts.

#### Local login and bootstrap

Local authentication is an official v1.0 capability. Login must not require Google, Microsoft, Apple, a cloud service, an external identity provider, or an Internet connection. OpenID Connect/corporate SSO may be added later but is not required in Phase 1.

The first administrator must be created through explicit, auditable bootstrap: no universal/default credentials; bootstrap is restricted to a secure initialization window/procedure; after first completion, anonymous administrator creation stays closed. Being on LAN is not sufficient. Define a secure administrative recovery procedure. Never use `admin/admin` or a password shared across installations.

Support multiple users with different permissions by module/resource/action; roles may be used where appropriate. Example permission strings such as `media.library.read`, `inventory.item.write`, or `ai.task.submit` are illustrative, not a frozen contract. Do not implement full RBAC/ABAC in Phase 1 without first defining the minimum contract.

#### Explicit public access

Anonymous access is permitted only per explicitly configured public resource/route, with a limited scope, audit trail, and revocation. No global switch may expose all private resources. Publishing a public website does not publish its database, administrative routes, or internal API. Public access must not be inferred from endpoint absence of an auth policy.

#### Web, mobile, desktop, and TV clients

Browser sessions must eventually define secure cookie attributes where cookies are used (`Secure`, `HttpOnly`, appropriate `SameSite`), CSRF protection for cookie-authenticated mutations, expiry, controlled renewal, logout/session invalidation, fixation protection, and brute-force controls. This Phase 0 decision does not select a concrete session technology.

Native mobile/desktop clients need secure login, credential storage via OS facilities where available, bounded session renewal/expiry, logout, and per-device revocation. Permanent non-expiring tokens are not the default.

For Smart TVs and limited-input devices, a future pairing flow should use a short-lived, one-use code, explicit approval by an already authenticated user, rate limits and anti-enumeration controls, correct device association, and revocation. A code displayed by the TV alone must not authorize access. Do not implement device pairing in Phase 1; retain it as a client/Media requirement.

#### Playback and Data Plane authorization

Playback requires both client authentication and authorization for the requested library/item. Access grants for Worker-direct, independent Data Gateway, Worker-to-Worker, and Storage-to-Worker routes must preserve the original user/resource/action authorization from 9.6. Authentication of a user and authorization of a transfer grant are separate responsibilities. Grants must be scoped, temporary, revocable, and bound to the operation/resource/destination; never give Worker or Gateway administrative credentials. Do not require every media segment to pass through the Coordinator if secure delegated validation is available, but revocation semantics must be explicit and testable.

Worker/Site Node machine identity remains distinct from user identity: explicit enrollment, mTLS, node authorization, capabilities, leases/fencing, and revocation. An authenticated Worker cannot perform arbitrary user or administrative operations.

All APIs apply authentication/authorization server-side, do not trust UI restrictions or a reverse proxy alone for business authorization, return consistent 401/403 behavior, avoid sensitive information leakage, and audit sensitive access. Distinguish `READ`, `CREATE`, `UPDATE`, `DELETE`, `EXECUTE`, and `ADMINISTER`; read permission implies no write/execute/admin permission.

Audit relevant login failures/successes, permission changes, session creation/revocation, device pairing, administrator changes, administrative actions, and sensitive operations. Never log passwords, secrets, complete tokens, recovery codes, or external-provider credentials. Retention and log protection remain later design work.

#### Current repository authentication inventory

This is a source review of committed starting product-code HEAD `134076f5ba2894aba216e775c4588cc3c547ee31` (Phase 0 commits since then changed documentation only). It is not runtime/test verification. The pre-existing uncommitted identity-related worktree is recorded separately below and is not counted as shipped behavior.

| Surface/capability | Classification at committed HEAD | Evidence and gap |
|---|---|---|
| Core/module administration | **PARTIALLY PROTECTED** | `/internal/admin/modules` uses configured `Modules:AdminToken` with fixed-time comparison and audits module admin operations. It is a shared static bearer, not local user/session identity or per-user authorization. |
| User identity, local login, sessions, user administration, roles/permissions | **NOT IMPLEMENTED** | No committed platform user/session service or login/bootstrap endpoints are wired through the server. |
| Secure initial administrator bootstrap/recovery | **NOT IMPLEMENTED** | A configured module admin token protects admin operations, but no committed one-time initial-user bootstrap/recovery flow exists. |
| Worker authentication/enrollment | **PARTIALLY PROTECTED** | Worker endpoints use HTTPS/mTLS certificate checks and coordinator node authorization; enrollment/authorization is an admin operation guarded by the shared bearer. This is machine identity, not user authentication. |
| Inventory read APIs | **CURRENTLY PUBLIC** | In committed `apps/server/InventoryEndpoints.cs`, item/warehouse/location/stock/movement reads have no user-auth policy. Mutation endpoints use the shared admin bearer, not per-user permissions. Asset access has a limited PublicRead/admin-token check. |
| Media library/list, playback, artwork, people, metadata reads and metadata edits | **CURRENTLY PUBLIC** | Committed routes in `DirectPlayEndpoints.cs`, `ArtworkDeliveryEndpoints.cs`, `PersonMetadataEndpoints.cs`, and `MetadataEditingEndpoints.cs` do not apply user authentication/authorization. Playback does not enforce library/item permissions. |
| Platform asset APIs | **PARTIALLY PROTECTED** | Upload/delete require the shared admin bearer; reads are public when the stored `PublicRead` flag is set and otherwise require that same admin bearer. This is not per-user/resource permission enforcement. |
| Health/readiness and internal diagnostics | **PARTIALLY PROTECTED / NOT VERIFIED** | `/healthz` and `/readyz` expose minimal status; current `/internal/*` health/diagnostic routes are mapped without a unified user policy. Information exposure has not been comprehensively inventoried/tested. |
| Public access policy | **PARTIALLY PROTECTED** | Platform Assets contain an explicit `PublicRead` field, but there is no consistent platform-wide private-by-default route policy. Existing public Media/Inventory routes predate this decision and must not be presumed public in v1.0. |
| Web/mobile/desktop sessions and Smart TV device pairing | **NOT IMPLEMENTED** | No committed secure browser session, native device credential lifecycle, device revocation/pairing protocol, or TV authorization flow was identified. |
| Authorization/audit across modules and Data Plane | **NOT IMPLEMENTED as a complete model** | Admin-operation audit and Worker/transfer machine controls exist in scoped paths; user/resource/action authorization is not propagated through Media/Inventory and 9.6 data routes. |

The current worktree contains **UNCOMMITTED / NOT VERIFIED** identity work: new platform identity contracts, SQLite identity persistence, `PlatformIdentityEndpoints.cs`, modifications to `Program.cs`, `PlatformEndpoints.cs`, and `InventoryEndpoints.cs`, and identity-related tests. These files are local pre-existing work, not committed HEAD; they were not modified or staged by this update. The work suggests local user/role/session/bootstrap and some Platform/Inventory permission paths, but it does not yet prove all Media routes, cookie/TV flows, default-deny behavior, device revocation, migration safety, or the full 9.8 contract. No tests were run here, so this work is not classified as validated or shipped.

#### Migration, acceptance, and phase impact

Do not change routes in Phase 0. Before v1.0, inventory current public/private/partially protected routes and tests that depend on anonymous access; migrate without exposing private data, preserve a secure admin bootstrap, and make selected public resources explicit. Existing public routes are not grandfathered automatically.

Release acceptance requires: secure first-admin bootstrap; local login with no Internet; default-deny private routes; distinct user/module/resource/operation permissions; negative unauthorized-operation tests; explicit public access only; session expiry/renewal/revocation; admin protection; playback/Data Plane grant authorization; machine/user identity separation; sensitive-operation audit; and client flows for browser, mobile/desktop, and TV pairing. Do not mark an item implemented without route-specific evidence and reproducible tests.

Phase 1 priorities are platform identity/authentication contracts, secure admin bootstrap, local authentication, minimum authorization, and protecting Empty Core administration. Do not build complete TV pairing, external SSO, Media-specific authorization, advanced ERP/IoT permissions, or an unnecessarily generic policy engine in Phase 1. Phase 2 integrates permission declarations/enforcement with module trust and lifecycle; Phase 5 binds user/resource authorization to Data Plane grants; Phase 6 applies it to Media libraries/playback; client phases deliver sessions and device pairing. Release gates include security tests and migration validation. This does not authorize Phase 1 implementation.

### 9.9 WASM / Untrusted Modules — APPROVED

**OWNER DECISION 9.9 — APPROVED: TRUSTED NATIVE MODULES ONLY — WASM AND UNTRUSTED NATIVE MODULES DEFERRED.**

HISPANIA v1.0 supports first-party native modules and trusted third-party native modules whose publishers the operator explicitly authorizes. Installable packages must pass signature, integrity, compatibility, and publisher-trust checks; installation and updates are controlled, and trust/module removal must be revocable. A valid signature establishes provenance and artifact integrity under the configured trust scheme; it does not prove the code is safe or bug-free. A signed package from an unknown or unauthorized publisher is not trusted automatically.

HISPANIA v1.0 does not officially support hostile or untrusted native code, arbitrary code execution by unknown publishers, a security sandbox for native modules, an official WASM runtime, or an open marketplace of unreviewed modules. WASM and other sandboxing for untrusted code are **FUTURE CONSIDERATION — NOT SUPPORTED IN v1.0**, with no delivery date committed. This does not decide Marketplace scope; 9.10 remains open.

Keep the trust and security layers distinct:

| Layer | What it provides | What it does not prove |
|---|---|---|
| Supply-chain trust | Package signature/integrity checks, publisher authorization and compatibility checks. | That authorized publisher code is safe, bug-free, or vulnerability-free. |
| Platform authorization | Permission checks for operations mediated by Core contracts. | Restrictions on every direct native OS/API call. A manifest declaration alone is not enforcement. |
| Runtime isolation | In-process load-context lifecycle or OOP fault isolation, supervision and configured operational limits. | A hostile-code security boundary. |
| Host security boundary | Effective OS controls on filesystem, network, credentials, privileges and resources. | No such comprehensive module sandbox is promised or verified for v1.0. |

```text
SIGNED != SAFE
TRUSTED PUBLISHER != BUG-FREE
AUTHENTICATED != AUTHORIZED
OOP != SECURITY SANDBOX
AssemblyLoadContext != SECURITY SANDBOX
```

First-party and trusted third-party modules follow the same minimum package checks: valid signature and integrity, authorized publisher, compatible host, controlled install/update/removal, versioned module contracts, and authorization for Core-mediated operations. The operator's publisher trust is an explicit decision; neither package signing nor installation grants blanket access to other modules or private data. Preserve the separation in 9.2, the limited ecosystem promise in 9.3, and user/resource authorization in 9.8.

Preserve the architecture seam `Core → versioned module contracts → runtime/host → trusted native module`, including module identity, lifecycle, explicit APIs, supervision, resource limits and capability declarations where currently supported. These seams may ease future runtime choices; they are not a WASM compatibility promise and do not justify speculative abstractions. Keep supported in-process and out-of-process mechanisms where valid. OOP improves resilience and decoupling but does not by itself prevent malicious OS access; declared manifest permissions are not effective restrictions unless an enforcement path exists.

#### WASM and future sandbox evaluation

Any future proposal must validate a concrete runtime and WASI/capability model; host API security; filesystem/network access; memory, CPU and execution-time limits; revocation; escape/abuse testing; update/security maintenance; Linux x64, Windows x64 and Linux ARM64 compatibility; and Raspberry Pi 4B memory/performance. WASM does not imply safety by itself. Do not select or implement a runtime in Phase 0 or require one in Phase 1.

General untrusted native support is out of scope for v1.0. A future proposal would need effective OS-level isolation, restricted process identity, filesystem and network isolation, privilege reduction, resource limits, secret separation, supervision and recovery, validated across supported operating systems. No specific mechanism is selected now.

#### ARM64, acceptance, roadmap, and Phase 1 impact

Decision 9.7 remains in force: the Core is multiplatform and ARM64 is a first-class Coordinator target. Trusted native modules avoid requiring a second runtime for v1.0 and reduce mandatory components, packaging, resource use and validation burden; they do not eliminate bugs, memory leaks or vulnerable dependencies. Raspberry Pi 4B keeps its independent stability, storage and resource qualification gates.

Before declaring official trusted-native module support, release evidence must cover signature/integrity verification, explicit publisher authorization, compatibility checks, controlled installation/update, trust revocation and module disable/removal, failure recovery and supervision for each supported execution mode, versioned contracts, Core-mediated authorization, tampered packages, unauthorized publishers, incompatible packages, and Linux x64/Windows x64/Linux ARM64 support gates. Do not require hostile-native containment tests as a v1.0 support claim, and do not declare a capability implemented without evidence.

Phase 1 preserves Empty Core, versioned contracts, module lifecycle, runtime/host separation, trust verification, platform authorization and ARM64 compatibility. It does not implement WASM/WASM SDK, a hostile-native sandbox, open marketplace, or new unapproved isolation mechanisms. Inventory remains the first planned real module for validating platform contracts; business modules must not become mandatory Core dependencies. Phase 2 matures module trust, permissions and the limited SDK. Later modules use the approved trusted-native model. Post-v1.0 sandbox/WASM evaluation is optional and has no committed date.

#### Repository module-runtime inventory at committed product-code HEAD

Static source review only; it is not a runtime/security validation. The product-code baseline is `134076f5ba2894aba216e775c4588cc3c547ee31`; subsequent commits through this update are documentation-only. Uncommitted work is excluded.

| Capability | Status | Evidence and boundary |
|---|---|---|
| `.hispania` package format and local installation | **IMPLEMENTED — BASELINE** | `ExtensionPackageInstaller` validates a local ZIP package, manifest, paths, payload SHA-256 hashes, size limits, and host compatibility, then stores verified bytes inertly. It does not load code during installation. This is not a general distribution service. |
| Package signatures | **IMPLEMENTED — BASELINE** | `ModulePackageSignature` verifies RSA-PSS/SHA-256 signatures over canonical manifest metadata including payload hashes. The signature implementation does not itself establish publisher trust or code safety. |
| Trusted/revoked publisher configuration | **PARTIAL** | Worker/server composition supplies trusted publisher keys and revoked publisher IDs; catalog activation checks trust separately from installation. Durable publisher namespace authorization, operator review workflow, and complete lifecycle/revocation operations are not established as a complete product policy. |
| Compatibility and package integrity | **IMPLEMENTED — BASELINE** | Manifest host-version ranges and file digests are validated. A valid package remains subject to trust and activation checks. |
| Controlled upgrade, rollback and removal lifecycle | **PARTIAL / NOT VERIFIED** | Versioned installation directories and module administration exist, but a complete supported update/rollback/recovery policy and its release evidence are not established by this source review. |
| `AssemblyLoadContext` | **IMPLEMENTED — BASELINE** | `apps/module-host` loads managed modules in a collectible context. This is lifecycle/load isolation, not a security sandbox. |
| Out-of-process host and supervisor | **IMPLEMENTED — BASELINE** | `apps/module-host`, `OutOfProcessModule` and worker composition provide process hosting, restart policy and configured operational resource limits. These do not constitute OS-enforced hostile-code containment. |
| Effective OS isolation of native module code | **NOT IMPLEMENTED / NOT VERIFIED** | No comprehensive filesystem, network, credential, privilege and resource sandbox for modules is established by the reviewed code. |
| Declared permissions and Core enforcement | **PARTIAL** | Manifests declare permissions and Core-mediated checks exist in scoped areas; enforcement is not universal for all native operations and module APIs. A declaration alone is not confinement. |
| Public SDK/compatibility and onboarding package | **NOT IMPLEMENTED as a complete public offering** | Contracts and sample modules exist; public SDK packaging, full compatibility policy, support/onboarding process remain future work under 9.3. |
| WASM/WASI runtime and untrusted native sandbox | **NOT IMPLEMENTED** | No supported WASM runtime or general hostile-native sandbox was identified. |

No tests were run for this documentation update; this table records source-level evidence and explicit gaps only.

### 9.10 Marketplace

**Codex recommendation:** marketplace/registry is v1.1+; v1 can distribute signed bundles through documented operator-managed sources. Marketplace needs reputation, ownership of publisher namespaces, revocation/incident response, moderation and reproducible artifacts. **Decision: OPEN.**

### 9.11 License boundary

No license change is proposed. Future technical choices affected by legal boundaries: whether SDK is separately licensed; whether third-party modules may link/distribute; whether proprietary modules can be packaged; bundle redistribution; registry/marketplace terms; trademarks and signing authority. Record owner/legal advice before publishing third-party SDK guarantees. **Decision: OPEN (legal policy only; no technical license change in Phase 0).**

### Pending owner decisions

- **9.10 Marketplace — OPEN**
- **9.11 License boundary — OPEN**

## 10. Version planes (proposal, not irreversible API freeze)

| Plane | Versioned independently? | Minimum proposed rule |
|---|---|---|
| Core version | Yes | SemVer for Core release; module compatibility must not be tied only to exact Core version. |
| Module API version | Yes | Capability/API ID and major; additive compatible changes within major; document deprecation/window before public SDK. |
| Module version | Yes | SemVer for module artifact/release; digest identifies exact immutable payload. |
| Package format version | Yes | `PackageFormatVersion` governs manifest/container/signature interpretation; separate from module release. |
| Worker protocol version | Yes | Negotiated min/max supported versions; golden fixtures and safe incompatible rejection. |
| Data Plane protocol version | Yes | Separate from worker control RPC; version grants, chunk/range, integrity, resume and limits. |
| UI contribution API version | Yes | Route/nav/settings/widget manifest compatibility; shell enforces CSP and no privileged arbitrary script. |
| Persistence schema version | Yes | Per owner (Core, module/provider) with migration history, backup checkpoint and rollback/forward recovery policy. |

Existing code already has module version, host range, package format integer, capability versions, and worker protocol negotiation. This is not yet one published compatibility policy. Before freezing a public API: produce sample consumer tests, define N/N-1 window, error taxonomy, deprecation and migration policy, binary/CLR compatibility constraints. Version numbering alone does not guarantee state/data compatibility.

## 11. Bundles model

A bundle is declarative and reproducible. It contains `coreVersion`, a list of module IDs and immutable versions/digests, config defaults (no secrets), dependency constraints, target platforms, and a profile label. It must not change Core code or select another architecture.

| Profile | Contents | Function |
|---|---|---|
| Core / Minimal | Core only | Empty platform administration; no Media or ERP business routes. |
| Core + Media | Core + Media module and required providers | Media server profile. |
| Core + Inventory | Core + Inventory module | Inventory profile, no Media dependency. |
| Core + Media + Inventory | Same Core + both module bundles | Candidate Complete profile; composition and permissions remain identical to individual bundles. |

Current reusable groundwork: module manifests/packages/signing; module catalogue/lifecycle; Platform-only build filter as an architecture check; separate Inventory and Library stores; static web route registry concept; worker protocol/provider loading. Missing: bundle lock file/catalogue, clean Core artifact manifest, dependency resolution plan, dynamic module route/UI lifecycle and install/rollback integration. None created in Phase 0.

## 12. Phase 1 debt: blockers and deferrable work

### BLOCKER for Fase 1

- No clean Core composition root/project/artifact currently exists; `apps/server` is not to be converted in place.
- Core-owned project allowlist and production artifact manifest have not been defined as build inputs.
- The current server composition contains direct Media/Inventory/providers/demo dependencies; these must remain outside the new root.
- Core empty boot/admin/persistence/module-list process journey has no acceptance test.
- Admin bootstrap/auth boundary must be selected sufficiently to prevent an unauthenticated module install/disable API.
- Product package/service/install layout and Core state/config paths need a Phase 1 design; developer launcher is not a production installer.
- Fixture publisher must be explicitly trusted by the test operator; no general publisher or third-party ecosystem trust is implied.

### CAN DEFER from Phase 1

- Media/Inventory extraction and module DB migration (later phases).
- SDK packaging, compatibility-window details, and curated external onboarding (Phase 9); the limited public API promise is approved, but this phase does not implement it.
- PostgreSQL Core provider is outside v1.0 by approved decision; this does not restrict module-owned provider choices.
- Full user roles, SSO, and business authorization. Per approved 9.8, local authentication, secure one-time bootstrap, default-deny private Core administration, and minimum admin authorization are not deferrable; external identity providers and complete module/resource policies remain later work.
- Sustained Pi 4B Coordinator and full platform/hardware qualification gates are Phase 8/9 release work; Phase 1 still adds an ARM64 Empty Core smoke once an executable artifact exists.
- WASM sandbox, marketplace, public worker listeners, custom CG-NAT traversal/relay, Site Node, multi-coordinator, GPU scoring, AI, distributed models. The approved 9.5 network direction remains architectural; implementing multi-site connectivity is not part of Phase 1.
- The approved 9.6 direct Data Plane and independent Gateway features; implementation belongs to the explicit Phase 5 validation gate, not Phase 1.
- UI package contribution execution; Phase 1 needs an empty shell and registry surface, not arbitrary dynamic UI scripts.
- Full cross-module backup/migration; Core own-state backup/restore contract remains in scope before v1 public readiness.

## 13. Exact Phase 1 contract — EMPTY CORE COMPOSITION ROOT

This section is the specification for the next phase. It is **not implementation authorization within Phase 0**.

### Objective

Create a new production composition root and installable Core artifact that starts from a clean data directory with no functional modules, authenticates an administrator, exposes health/status/module administration, opens Core-owned persistence, records lifecycle audit, and can install/disable a signed harmless fixture module without terminating or losing the Core. It must prove package contents and dependency graph contain no Media or Inventory business implementation.

### In scope

- New Core app/composition root and minimal Core Web/API surface; exact project names can be selected during Phase 1 implementation while respecting frozen boundaries.
- Explicit Core project allowlist and package/publish manifest; use Platform/ModuleSystem contracts and runtime only after reviewing transitive references.
- Reuse Kernel, Runtime/module catalogue/lifecycle, signature/package verification and OOP supervisor as accepted by reuse gates.
- Core-owned persistence contract and SQLite provider, following approved Owner Decision 9.1. The contract is provider-neutral and limited to demonstrated Core needs; SQLite is the only official Core provider in v1.0.
- Preserve Linux ARM64 publication/runtime compatibility from the start; do not add x64-only Core dependencies. Full hardware qualification is not a Phase 1 implementation requirement.
- Secure bootstrap administrator and authenticated Core-admin mutation routes, module list, health, readiness and minimal audit.
- Local Core authentication and the minimum provider-neutral identity/session/authorization seams needed to protect Core administration; bootstrap is one-time, secure, usable without Internet or an external identity provider, and fails closed. Do not add external SSO, TV/device pairing, or a broad generic policy engine.
- A signed no-business fixture package from an explicitly trusted test publisher, covering install, disable, module failure isolation, Core restart and empty data after fixture removal/archive.
- Fail-closed package tests for unknown signer, revoked signer, tampered package/hash/signature, and incompatible host before code execution.
- Core does not claim OOP or collectible ALC makes hostile native code safe; no arbitrary untrusted modules or WASM/sandbox implementation in Phase 1.
- Phase 1 proves only the minimum trust path for the signed fixture. Complete per-resource permission enforcement remains a Phase 2 deliverable.
- Core-only package contents inspection; prove no Media/Inventory DLLs/migrations/routes/web assets.
- Reuse current tests where they validate behavior; add only the new Core integration/fitness tests necessary to meet this contract.
- Minimal versioned backup/restore of Core-owned state/config references, or split it into a narrowly scoped Phase 1.1 gate before Module/Inventory work; document a concrete owner-approved boundary.

### Out of scope

- Moving/refactoring `apps/server`; deleting or changing current API routes; extracting Media/Inventory; modifying their schemas or migrations.
- Scheduler scoring redesign, changing worker protocol, implementing Data Plane routes or changing grant semantics, adding AI/GPU/NAS providers.
- Site Node, site registry/protocol, local worker discovery, WOL, IoT/MQTT/ESP32, locality scoring, public worker WAN listeners, public relay, new tunneling/traversal mechanisms, or a new reverse proxy. These remain outside Phase 1 even though the network direction is approved in 9.5.
- PostgreSQL Core; Marketplace, public registry, WASM, untrusted native modules, public WAN relay.
- External SSO, TV/device pairing, and complete Media/ERP/IoT per-resource authorization; Core administration still requires local authentication and explicit authorization.
- Any change to the existing Library/Media PostgreSQL provider or its schema/migrations; it remains Media-owned and is not part of Fase 1.
- SDK packaging and general external onboarding; Phase 1 may use the trusted fixture contract, while the approved limited public API is delivered through the later SDK/compatibility work.
- Releasing v1.0 or migrating existing user data.

### Project changes expected in Phase 1

Likely **create**: a Core host/composition project; Core admin API/shell project or module; a clean Core package/profile manifest; architecture fitness/process test project; build/package script for Core-only artifact. Likely **modify**: solution filters, CI workflow, module-host/runtime composition extensions, package outputs, minimal Core persistence/admin implementation, docs/support instructions. Likely **leave unchanged**: Media/Inventory projects, existing server composition, their endpoints and DBs, worker/Data Plane contracts. Final list must be recorded before code changes and must not silently add domain refs.

### Allowed dependencies

- .NET platform/runtime and required ASP.NET framework.
- `Hispania.Kernel` and reviewed generic portions of `Hispania.Runtime`.
- `Hispania.ModuleSystem.Contracts`, `Hispania.Platform.Contracts`, reviewed identity/auth/audit/health/config/backup contracts.
- provider-neutral Core persistence contract plus its SQLite provider, the single officially supported Core provider for v1.0. Keep the abstraction narrowly matched to actual Core storage operations.
- package verification/signature and module-host executable for explicitly trusted fixture modules; deny unknown/revoked publishers and tampered/incompatible packages before activation.
- operator CLI only as a client of Core API, not as Core composition dependency.

### Forbidden dependencies

- Any `core/domain` Media implementation; Library/Metadata/Playback contracts or apps; Inventory contract/app/persistence; TMDB; FFmpeg/transcoder; AI; ERP; demo business modules in product artifact.
- PostgreSQL Core provider, PostgreSQL-specific Core API/schema, or changes to the Media/Library PostgreSQL provider and migrations.
- Any module-owned migrations, routes, frontend page bundles, native provider binaries, or test fixtures in Core artifact.
- Runtime dependency on module availability for Core boot/readiness/admin/doctor/backup.
- Direct project references from one business module to another implementation.

### Required tests and CI

1. Compile and publish Core from a clean checkout with pinned inputs.
2. Project/reference graph fitness A/B/G and content fitness C/D/E.
3. Route/UI fitness F on clean Core artifact.
4. Process integration J: boot → health → authenticated admin → module list empty → restart → health.
   - Verify secure one-time bootstrap and local admin login work without Internet/external identity services; deny unauthenticated Core-admin mutations and ensure no default credential or implicit public access is created.
5. Install fixture from explicitly trusted test publisher → verify signature/integrity/host compatibility → enumerate → disable → force fixture failure → Core still healthy → restart.
6. Verify unknown signer, revoked signer, tampered artifact and incompatible host all fail closed before execution; retain package traversal/hash/signature and bounded supervisor regression coverage.
7. Test Core state backup/restore and reopen; include interrupted/invalid backup behavior within agreed boundary.
8. Run on Linux x64 and Windows x64; when an executable Empty Core artifact is available, also build/publish and run a native Linux ARM64 smoke covering boot, authenticated admin, health/readiness, SQLite persistence and restart. This Phase 1 smoke is not Pi 4 sustained Coordinator qualification; complete that before v1.0 release.
9. Keep current full product CI jobs passing; do not weaken/remove Media/Inventory regressions to make Core green.

### Migration/transition strategy

Keep existing `apps/server` runnable and unchanged as the legacy/product composition during Fase 1. Create Core alongside it. Reuse implementation libraries through neutral contracts; do not copy business services into Core. A temporary compatibility wrapper is acceptable only if it is visibly transitional and does not make Media/Inventory mandatory for Core. Existing users/data remain owned by current product until separate module migration plans are approved and tested. Do not dual-write to new Core DB. Publish a side-by-side package to a test directory/VM and verify clean install/rollback before discussing migration.

### Objective PASS criteria

- Clean Core package starts with no modules and no Media/Inventory artifacts.
- Health/status and authenticated administration work before/after restart.
- Module registry returns empty; a permitted signed fixture can install/disable and fail without taking down Core.
- Core persists its own state; backup/restore reopens a valid installation (or an explicit Phase 1.1 checkpoint is owner-approved before Phase 2 begins).
- Core persistence contract is provider-neutral and tested through the official SQLite provider; PostgreSQL Core is neither shipped nor required. Do not create a generic abstraction broader than the Core's actual operations.
- The existing Library/Media PostgreSQL provider and migrations are unchanged by Fase 1.
- No forbidden project/reference/runtime/package dependency; all fitness tests pass.
- Linux x64 and Windows x64 build/publish/process gates pass; ARM64 Empty Core native smoke passes when the executable artifact is available. No required skips or undocumented manual assumptions.
- Existing product CI remains green.
- No Media/Inventory routes, data, schema, tests, or behavior moved/changed as part of Core creation.
- Commit/PR evidence lists package hash, test report, support runner and any remaining owner decision.

## 14. Open questions to resolve before contract freeze

1. Design concrete network implementations that satisfy approved 9.5 multi-site private infrastructure and public HTTPS client access; no product-specific VPN or gateway is frozen by that decision.
2. Define route-by-route migration from current anonymous reads to the approved 9.8 default-deny policy, including explicit public-resource declarations and compatibility tests; this is implementation planning, not an open owner-policy decision.
3. For a possible post-v1.0 sandbox proposal, validate runtime, isolation, host APIs, resources and platform support against approved 9.9; this is future engineering evaluation, not a v1.0 support commitment or an open owner decision.
4. Confirm marketplace scope for v1.
5. Decide CLI packaging/runtime expectation (side-by-side tool vs bundled) in Phase 1 design.
6. Confirm minimum Core own-state backup/restore included in Fase 1 or an owner-approved 1.1 gate before module extraction.
7. Obtain legal guidance on SDK, module redistribution and commercial/proprietary extensions without changing license in this phase.

### Gate summary

- Owner-approved architectural principles: **PASS** (provided in user request).
- Current-state evidence and important deltas: **PASS**, bounded to versioned HEAD; uncommitted work excluded.
- Core boundary/responsibility and forbidden graph: **PASS**.
- Code classification and reuse matrix: **PASS**.
- Fitness tests and phase allocation: **PASS**.
- Owner-approved platform/hardware target matrix: **PASS; validation pending per target as recorded in 9.7**.
- Independent version planes: **PASS as proposal; public ABI freeze deferred**.
- Phase 1 contract: **PASS**.
- Product code unchanged: **PASS**.
- Owner Decisions 9.1 Core Persistence, 9.2 Module Trust Model, 9.3 Third-party Module Promise, 9.4 Remote Worker Threat Model, 9.5 Network Supported in v1, 9.6 Data Plane Architecture, 9.7 ARM64 / Hardware Support, 9.8 Authentication for Private Data, and 9.9 WASM / Untrusted Modules: **APPROVED**.
- Owner-only selections 9.10–9.11: **OPEN** → **OWNER FREEZE: PENDING**.
