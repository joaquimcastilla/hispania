# HISPANIA v1.0 — Phase 0 Architecture Freeze

**Purpose:** freeze the architectural direction and define the acceptance contract for Phase 1 (Empty Core Composition Root). This is an analysis and planning document; it does not authorize implementation of Phase 1.

**Repository evidence:** starting HEAD for this update `e1ef5b51b66e0d77c2fbc8647f26d62276cff074`, branch `feature/phase-13-platform-maturity`, observed 2026-10-15. The product-code baseline remains `134076f5ba2894aba216e775c4588cc3c547ee31`; intervening commits changed only this Phase 0 document. `git status` showed pre-existing edits and untracked files in server endpoints, Platform events/identity, SQLite adapters, tests, and `docs/architecture/`. They were not used as shipped implementation evidence and were preserved. The previous audit document is untracked in this worktree; it is treated as the requested source, not overwritten. The working tree has no upstream configured for this branch (no `@{upstream}` resolved); remote state was not fetched.

**Phase 0 result:** **TECHNICAL PHASE 0: PASS** for the specified documentation outputs and Phase 1 contract. **OWNER FREEZE: PENDING** because the unresolved decisions in §9 require owner approval. No product code, APIs, databases, scheduler, Data Plane, runtime, or package format were changed; no tests were run.

## 1. Purpose and evidence rules

The primary input is [HISPANIA v1.0 Deep Architecture Audit](HISPANIA-V1-ROADMAP-AUDIT.md). It is a baseline of observations and proposals, not a set of owner-approved decisions. The principles in §2 and Owner Decisions 9.1–9.5 below are explicitly approved. Decisions 9.6–9.11 remain open; recommendations are technical advice only.

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

Phase 1 should not add generic scheduler scoring, Data Plane changes, PostgreSQL Core, WASM, Site Node, WOL, IoT, multi-site discovery, new tunneling, or module-specific persistence. It must preserve the approved network direction: private owner-managed infrastructure connectivity for multi-site Control Plane and separate HTTPS client access, without coupling Core to a VPN product or requiring public worker listeners. Implementing the multi-site network and CG-NAT connectivity is not part of the Empty Core gate. Reuse existing implementations where valid; create a separate composition root and artifact.

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
- A future validated sandbox (for example WASM/WASI or another technology) may be added without changing the Core principle of capability/permission enforcement.
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

HISPANIA v1.0 supports workers that the owner/operator explicitly authorizes and trusts. They may be in the Coordinator's LAN, another home or physical site, behind NAT or CG-NAT, connected through an operator-managed VPN, associated with a future local Site Node, or hosted on a remote server controlled by the owner. Physical location is not the trust boundary; explicit operator authorization and trust are. Support for those topologies is an outcome requirement. The supported network direction is recorded in 9.5; specific Data Plane routes remain open under 9.6.

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

The Control Plane may authorize, schedule, lease, observe, cancel, and audit while data bytes follow a different route. Do not assume the Control Plane overlay is the optimal Data Plane route. This decision guarantees client-facing HTTPS service access but does not require all bytes to traverse Core/Coordinator, require direct Worker-to-client streaming, or select source/Worker/result routes. Those paths are Owner Decision 9.6 and remain open.

#### Phase 1 implications

Phase 1 must preserve provider/product-neutral Core and Worker contracts, avoid dependencies on Tailscale/Headscale/WireGuard, avoid public Worker listeners, and not require a Worker public IP or DNS. It does not implement full multi-site networking, Site Node, new tunneling/NAT traversal, a public relay, or a new reverse proxy. It must preserve interfaces that allow the approved network direction to be implemented later; do not change Worker/Data Plane protocols unless strictly required by the Empty Core contract. No speculative network abstraction should be added without a concrete Phase 1 need.

The approved v1.0 network direction is **PRIVATE MULTI-SITE INFRASTRUCTURE + PUBLIC HTTPS CLIENT ACCESS**. This does not resolve Owner Decision 9.6 or prescribe a Data Plane optimization.

### 9.6 Data Plane scope

| Path | v1 decision proposal | Reason |
|---|---|---|
| Worker ↔ authorized storage/source | Required for reference workload where supported | Existing short-lived source grants/range-limited transfer form a useful base; no raw local paths in protocol. |
| Worker → Coordinator result | Required as baseline fallback | Existing bounded upload/hash/publish path is implemented; coordinator may receive result bytes for local cache. |
| Worker → client direct | Not required | Requires client-reachable worker, safe audience-bound URL/token, range/cancel/revocation and NAT handling. Optimize later with measurements. |
| Worker → Worker | Not required | Multi-hop trust, scheduling, transfer authorization and failure semantics add scope. |
| Coordinator relay fallback | Existing upload/cache path allowed for v1 | Coordinator may handle result bytes if protocol needs it; avoid routing source+result through it when direct source grants are possible. No promise of a universal direct stream. |

**Codex recommendation:** preserve existing FFmpeg transfer semantics, version and gate them in Phase 5; do not require peer-to-peer or direct client streaming for v1. **Decision: OPEN.**

### 9.7 ARM64 and hardware support

The current principal workflow at this HEAD runs Linux x64 and Windows x64; the repo contains historical ARM work/notes, but this branch's `.github/workflows/validation.yml` has no ARM64 job. No official hardware acceleration certification follows from capability labels or FFmpeg presence.

| Target | Proposed v1 status | Evidence / precondition |
|---|---|---|
| Linux x64 Coordinator | **OFFICIAL candidate** | Linux CI, .NET cross-platform suite, distributed/PG tests; release package/service/backup gates still required. |
| Windows x64 Coordinator | **OFFICIAL candidate** | Windows CI full build/tests and worker process paths; installer/service/update gates still required. |
| Generic Linux ARM64 Coordinator | **EXPERIMENTAL** until CI/lab gate | Build/runtime validation absent in current workflow. |
| Raspberry Pi 4B 8 GB Coordinator | **EXPERIMENTAL candidate** | Plausible baseline, but sustained storage/backup/worker/upgrade and memory profile not evidenced in current HEAD. May be promoted only after ARM64 lab acceptance. |
| Raspberry Pi 3B+ Coordinator | **OUT OF SCOPE** proposed | Low-memory support risk for ASP.NET + SQLite + admin; treat as potential agent experiment, not Coordinator target. |
| Linux x64 Worker | **OFFICIAL candidate** | Cross-platform worker and mTLS/process tests; release service/runtime setup still required. |
| Windows x64 Worker | **OFFICIAL candidate** | Windows integration/job object paths tested in CI; package/service gate remains. |
| ARM64 Worker | **EXPERIMENTAL** | Add ARM64 runner/device smoke before promotion. |
| CPU software worker | **OFFICIAL candidate** | FFmpeg software path exists, subject to release package gate. |
| GPU worker generally | **EXPERIMENTAL** | Current inventory labels do not demonstrate resource reservation or workload matrix. |
| NVIDIA GTX 950/1060 acceleration | **EXPERIMENTAL / OUT OF OFFICIAL SCOPE** | No driver/codec/version support matrix or CI evidence. Never infer from generic `gpu` label. |

**Codex recommendation:** publish Linux x64 and Windows x64 first when install/upgrade gates close; ARM64 and Pi4 remain experimental until physical acceptance; Pi3B+ Coordinator out of scope, optional CPU Worker experiment. **Decision: OPEN.**

### 9.8 Authentication for private data

Current HEAD docs/source indicate some admin mutation routes use a configured bearer, while Media and Inventory read routes are public and a general user/session/domain authorization system is not wired through the Shell. This behavior is not a safe default for a broadly exposed v1 server.

| Option | Tradeoff |
|---|---|
| A. Require authentication for private business data by default | Secure default; requires migration/setup and clients/session support. Anonymous mode can remain explicit for selected public content. |
| B. Keep LAN anonymous default | Easy local demo, but LAN is not a strong identity boundary and accidental exposure risks data. |
| C. Operator-selected default | Flexible but creates misconfiguration ambiguity and support burden. |

**Codex recommendation:** A. All private module data and mutations require authenticated identity by default; public access must be explicit per resource/module and visibly configured. Migration must preserve a bootstrap administrator and explain old public endpoints. **Decision: OPEN.**

### 9.9 WASM / sandbox

**Codex recommendation:** do not promise WASM in v1; do not support untrusted native modules. Existing OOP/collectible ALC are not sandboxes. Build trust tiers and OS-restricted accounts for trusted modules; defer a sandbox runtime until capability syscalls, filesystem/network isolation, quotas and platform conformance are real. **Decision: OPEN.**

### 9.10 Marketplace

**Codex recommendation:** marketplace/registry is v1.1+; v1 can distribute signed bundles through documented operator-managed sources. Marketplace needs reputation, ownership of publisher namespaces, revocation/incident response, moderation and reproducible artifacts. **Decision: OPEN.**

### 9.11 License boundary

No license change is proposed. Future technical choices affected by legal boundaries: whether SDK is separately licensed; whether third-party modules may link/distribute; whether proprietary modules can be packaged; bundle redistribution; registry/marketplace terms; trademarks and signing authority. Record owner/legal advice before publishing third-party SDK guarantees. **Decision: OPEN (legal policy only; no technical license change in Phase 0).**

### Pending owner decisions

- **9.6 Data Plane scope — OPEN**
- **9.7 ARM64 and hardware support — OPEN**
- **9.8 Authentication for private data — OPEN**
- **9.9 WASM / sandbox — OPEN**
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
- Full user roles/SSO and business authorization (but minimal secure platform admin is not deferrable).
- WASM sandbox, marketplace, public worker listeners, custom CG-NAT traversal/relay, Site Node, multi-coordinator, GPU scoring, AI, distributed models. The approved 9.5 network direction remains architectural; implementing multi-site connectivity is not part of Phase 1.
- Worker-to-client/worker-to-worker direct streaming; retain existing worker path without expanding it. The approved 9.5 network direction does not decide these Data Plane routes (9.6).
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
- Secure bootstrap administrator and authenticated Core-admin mutation routes, module list, health, readiness and minimal audit.
- A signed no-business fixture package from an explicitly trusted test publisher, covering install, disable, module failure isolation, Core restart and empty data after fixture removal/archive.
- Fail-closed package tests for unknown signer, revoked signer, tampered package/hash/signature, and incompatible host before code execution.
- Core does not claim OOP or collectible ALC makes hostile native code safe; no arbitrary untrusted modules or WASM/sandbox implementation in Phase 1.
- Phase 1 proves only the minimum trust path for the signed fixture. Complete per-resource permission enforcement remains a Phase 2 deliverable.
- Core-only package contents inspection; prove no Media/Inventory DLLs/migrations/routes/web assets.
- Reuse current tests where they validate behavior; add only the new Core integration/fitness tests necessary to meet this contract.
- Minimal versioned backup/restore of Core-owned state/config references, or split it into a narrowly scoped Phase 1.1 gate before Module/Inventory work; document a concrete owner-approved boundary.

### Out of scope

- Moving/refactoring `apps/server`; deleting or changing current API routes; extracting Media/Inventory; modifying their schemas or migrations.
- Scheduler scoring redesign, changing worker protocol, Data Plane or grant design, adding AI/GPU/NAS providers.
- Site Node, site registry/protocol, local worker discovery, WOL, IoT/MQTT/ESP32, locality scoring, public worker WAN listeners, public relay, new tunneling/traversal mechanisms, or a new reverse proxy. These remain outside Phase 1 even though the network direction is approved in 9.5.
- PostgreSQL Core; Marketplace, public registry, WASM, untrusted native modules, public WAN relay.
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
5. Install fixture from explicitly trusted test publisher → verify signature/integrity/host compatibility → enumerate → disable → force fixture failure → Core still healthy → restart.
6. Verify unknown signer, revoked signer, tampered artifact and incompatible host all fail closed before execution; retain package traversal/hash/signature and bounded supervisor regression coverage.
7. Test Core state backup/restore and reopen; include interrupted/invalid backup behavior within agreed boundary.
8. Run on Linux x64 and Windows x64; ARM64 build/smoke only if included in approved Phase 1 support target, otherwise track as Phase 8 experiment.
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
- Linux x64 and Windows x64 build/publish/process gates pass; no required skips or undocumented manual assumptions.
- Existing product CI remains green.
- No Media/Inventory routes, data, schema, tests, or behavior moved/changed as part of Core creation.
- Commit/PR evidence lists package hash, test report, support runner and any remaining owner decision.

## 14. Open questions to resolve before contract freeze

1. Design concrete network implementations that satisfy approved 9.5 multi-site private infrastructure and public HTTPS client access; no product-specific VPN or gateway is frozen by that decision.
2. Approve Data Plane paths required for v1, especially whether worker→storage direct is mandatory for supported sources.
3. Approve official hardware/platform matrix; current evidence does not justify Pi4/ARM64 as official yet.
4. Approve authentication-by-default for private data and migration policy from current anonymous reads.
5. Decide whether a WASM/other sandbox implementation enters v1; the approved 9.2 decision still excludes untrusted native modules and makes no malicious-code isolation promise.
6. Confirm marketplace scope for v1.
7. Decide CLI packaging/runtime expectation (side-by-side tool vs bundled) in Phase 1 design.
8. Confirm minimum Core own-state backup/restore included in Fase 1 or an owner-approved 1.1 gate before module extraction.
9. Obtain legal guidance on SDK, module redistribution and commercial/proprietary extensions without changing license in this phase.

### Gate summary

- Owner-approved architectural principles: **PASS** (provided in user request).
- Current-state evidence and important deltas: **PASS**, bounded to versioned HEAD; uncommitted work excluded.
- Core boundary/responsibility and forbidden graph: **PASS**.
- Code classification and reuse matrix: **PASS**.
- Fitness tests and phase allocation: **PASS**.
- Proposed platform/hardware support matrix: **PASS as proposal; no unsupported target asserted officially**.
- Independent version planes: **PASS as proposal; public ABI freeze deferred**.
- Phase 1 contract: **PASS**.
- Product code unchanged: **PASS**.
- Owner Decisions 9.1 Core Persistence, 9.2 Module Trust Model, 9.3 Third-party Module Promise, 9.4 Remote Worker Threat Model, and 9.5 Network Supported in v1: **APPROVED**.
- Owner-only selections 9.6–9.11: **OPEN** → **OWNER FREEZE: PENDING**.
