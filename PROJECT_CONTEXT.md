# HISPANIA — Project Context

This is the current implementation context for developers and coding agents. Read it with the architecture, compatibility policy, security model and relevant operational runbook before making changes. `PROGRESS-LOG.md` is the historical record; this file describes the accepted repository state at the 0.7.0 RC1 preparation baseline.

## Product and release state

HISPANIA is a self-hosted, modular application platform. Its shared Platform mechanisms support independently owned domain suites. The default deployment is one Server process; optional WorkerAgents execute explicitly authorized capabilities on separate processes or hosts.

- Distribution candidate: `0.7.0-rc.1` (pre-1.0); authoritative value is root `version.json`.
- The current 25-phase roadmap is complete. Historical phases were accepted incrementally; Phase 25's final Linux acceptance passed on `main` at `8817b912b8f1748977f9145942d2dc30ad492b8e` (GitHub Actions run `38064897483`).
- RC1 is being prepared on `release/0.7.0-rc.1-preparation`. A successful package build for this preparation must use its final clean commit; prior manifests and checksums are evidence only for their recorded commits.
- Verified distribution targets: Windows x64 and Linux x64. Linux's final Phase 25 package was built and run natively on Ubuntu 24.04 x64. ARM64, Raspberry Pi, macOS and other targets are not validated.
- No tag or GitHub Release exists for RC1. Do not tag, publish or describe it as stable without explicit user authorization.
- No Public Stable contracts. Compatibility classes and technical versions are independent of distribution SemVer.

## Goals and deliberate non-goals

The project aims to provide an extensible, operator-controlled host for media and general business domains. Keep the kernel and Platform independent of domain implementations. Prefer versioned contracts and owner-controlled persistence over cross-domain implementation references.

The platform is a modular monolith, not a microservice system. Inventory is a limited domain proof, not a complete ERP. The runtime does not provide an adversarial code sandbox. Do not claim exactly-once execution, cross-store atomic rollback, distributed high availability or power-loss guarantees without new, scoped evidence.

## Repository map

| Path | Responsibility |
|---|---|
| `contracts/` | Versioned Module System, Platform, Media, Inventory, playback and extension contracts |
| `core/kernel/` | Small shared kernel boundary |
| `core/runtime/` | Module registry/lifecycle, package inspection and lifecycle, supervision, worker coordination and runtime services |
| `core/domain/` | Media concepts and domain model |
| `application/` | Use cases for Media, Inventory, Identity, Authorization, Jobs, Storage, Maintenance and Observability |
| `infrastructure/` | Hosting, SQLite/PostgreSQL persistence, filesystem storage, metadata and worker adapters |
| `apps/server/` | ASP.NET Core composition root, API, Web hosting, admin and installation integration |
| `apps/worker-agent/`, `apps/ffmpeg-worker/`, `apps/module-host/` | Worker and module process hosts |
| `apps/deployment/`, `apps/cli/` | Installation/deployment CLI and project tools |
| `apps/web/` | React/TypeScript Modern and Classic Web Shells |
| `modules/`, `extensions/` | First-party modules and examples |
| `tests/` | Unit, integration, compatibility, security and real-process tests |
| `tools/` | Production publication, package preflight, phase-focused harnesses and CI helpers |
| `deployment/` | Windows/Linux service templates and installation scripts |
| `docs/` | Architecture, compatibility, security, operations, phase evidence and ADRs |

`Hispania.sln` is the full solution. `Hispania.Platform.slnf` is a dependency-boundary filter that builds Platform contracts/runtime/adapters without domain implementations; it does not assert independent deployment for every domain.

## Architecture and domain boundaries

The Server is the composition root. Platform supplies module lifecycle/supervision, host trust, authentication/authorization, audit, durable Events and Jobs, Assets/Storage, Maintenance, health and runtime metrics/tracing. The host composes Media and Inventory through contracts. Cross-domain behavior belongs in explicit composition adapters, not direct domain-to-domain implementation references.

Media includes local library/catalog and scan, metadata providers and enrichment, search, playback planning/execution, streaming and an FFmpeg provider. The current planner supports direct play and conservative transcoding paths; experimental A/B/Shadow infrastructure applies only to declared compatible playback capabilities. It is not a universal domain capability.

Inventory owns Item, Warehouse, Location, Stock and StockMovement behavior and a dedicated SQLite store. It may hold opaque Asset references. It does not depend on Media.

The Web Shell ships Modern and Classic bundles. Both use the same registered screens/routes and server authorization. Dynamic package Web contributions are signed/declarative schema content with fixed authorized operations; packages do not provide executable JavaScript, raw HTML or arbitrary URLs.

## Modules, extensions and trust

Module manifests describe identity, versions, contracts, runtime and package compatibility. The `.hispania` installer verifies archive structure, hashes, compatibility and host policy. Signature enforcement is host-policy dependent. Package Format 1 historical packages and Format 2 dependency packages have their own format generations.

In-process modules execute with Server privileges. `AssemblyLoadContext` is not isolation. Out-of-process ModuleHost adds process failure/resource boundaries; OS process limits are not a hostile-code sandbox. Do not use untrusted native extensions on a security assumption that the runtime does not enforce.

## WorkerAgents and distributed work

WorkerAgent is a generic host for explicitly permitted capabilities. Workers authenticate to the coordinator with mTLS and advertise authorized nodes/capabilities. Jobs use leases and generations; stale results are fenced. FFmpeg is a Media capability provider, not the identity of the generic worker stack. Transfers use bounded job-scoped grants and digest verification.

The coordinator is a single instance with SQLite state. There is no multi-master consensus or general HA. Delivery/retry semantics are at-least-once; workers and handlers need idempotent behavior. Do not describe the system as exactly-once.

## Persistence, storage and recovery

Platform, Inventory, Media and the coordinator own separate stores. Platform stores, Inventory and distributed coordination use SQLite. Media has SQLite and an optional PostgreSQL provider. PostgreSQL support for Media does not generalize to Platform stores or coordinator state. Filesystem storage uses opaque handles and containment rules; remote object storage is not currently included.

Persistence versions/migrations belong to their owners. Upgrades are forward-only per store and readiness blocks when a required migration fails. Backups are offline, managed-layout bundles with hashes and owner-specific verification. Restore across independent stores is not globally atomic. Old executables are not a safe rollback target after an unverified schema migration. See `docs/PHASE-17-BACKUP-RESTORE.md`, `docs/operations/UPGRADE.md`, `docs/operations/UPGRADE-ROLLBACK.md` and `docs/operations/ROLLBACK.md`.

## Identity, security and administration

Identity uses local accounts, PBKDF2-HMAC-SHA256 password derivation and opaque sessions whose hashes are persisted. Authorization uses explicit RBAC permissions; Session guards protect current Admin and domain routes. Some explicitly documented legacy endpoints still accept a compatibility bearer. It is not equivalent to the Session/RBAC system and is not accepted by the consolidated Events/Jobs/Observability/Admin projections.

Security controls include package integrity and optional publisher signatures, endpoint policy inventory, bounded requests, generic safe errors, mTLS worker identity, authorization, leases/fencing, process limits, secret references and safe administrative projections. Each has a specific boundary; none establishes general hostile-code isolation. Security reporting instructions are in `SECURITY.md`.

## Jobs, Events, assets and observability

Events and Jobs have durable SQLite stores and retry/lease state. Delivery is at-least-once; no cross-owner transaction or exactly-once effect is promised. Assets separate metadata from bytes; the current provider is filesystem-backed. Audit is append-only in its SQLite owner store. Health/readiness, runtime metrics and tracing are in-process observability; an external metrics backend or TSDB is not included.

## Web and operations

The Server serves both Modern and Classic static bundles. Session/RBAC policies apply to APIs and direct UI routes. Administrative projections are bounded and omit payloads, secrets and raw failure bodies. Operators use the production configuration/secret reference model and included deployment CLI. Service templates exist for systemd and Windows SCM, but Phase 25 native package acceptance did not install a native service-manager unit; do not represent service certification as complete.

## Distribution and CI

`version.json` is the sole product distribution identity. Do not synchronize the RC suffix into NuGet contracts, assembly defaults, package format generations, worker protocols, schemas or the private Web npm metadata. Production publication is `tools/publish-production.ps1`; only `win-x64` and `linux-x64` are accepted RIDs. It publishes Server, WorkerAgent, ModuleHost, FFmpeg Worker and Deployment, includes operational templates, generates a per-file manifest and sidecars, then runs the deployment verifier. Packages require the .NET 10 ASP.NET Core x64 runtime; they are not self-contained.

CI definitions live in `.github/workflows/`. Phase 25 native Linux acceptance builds from a clean commit, verifies exact SHA/RID, publishes a second time and compares package directory contents, then installs/runs `--check` and starts the Server outside the checkout. The existing manual acceptance workflow is also allowlisted for `release/0.7.0-rc.1-preparation` so the final candidate can receive the same native Linux check before review. It does not validate ARM64, authenticated PostgreSQL, service-manager installation, managed Server heap/GC under Phase 24 campaigns or active fault injection in those Short/Soak campaigns.

For Phase 25, the accepted source was `8817b912b8f1748977f9145942d2dc30ad492b8e`; Linux runner was `hispania-linux-ci`, Ubuntu 24.04 x64. Workflow and artifact references, plus the boundary of Windows and Phase 24 evidence, are in `docs/phase25-release-readiness.md`.

## Versioning and contracts

The distribution is `0.7.0-rc.1`. Distribution SemVer is distinct from assembly/package identities and contract/API/persistence/protocol generations. Do not rename or renumber contracts to match the RC. The compatibility catalog `docs/compatibility/contracts.json` and `docs/VERSIONING.md` enumerate current version axes. No type marked Experimental or Stable Candidate is thereby Public Stable.

## Roadmap state and next work

Phases 1–25 of the active roadmap are complete and accepted incrementally. RC1 preparation documents and packages that result; it does not open a Phase 26. Later work should be proposed as product improvements only after the candidate review, with explicit scope, compatibility impact and evidence. The old exploratory roadmap has been retained as historical context in `docs/ROADMAP.md`; its incomplete statuses are not the current Phase 1–25 state.

## Working rules for future changes

1. Inspect the current code, tests, workflow and owner documentation before editing; this context is not a substitute for source.
2. Preserve domain boundaries, migration ownership, exact capability compatibility and current security limits.
3. Reuse accepted phase evidence when changes do not affect its scope. Run only targeted checks for changed code and requirements.
4. Do not claim support outside the tested x64 targets or improve a support label based only on source portability.
5. Never include credentials, private keys, personal addresses, environment dumps or secrets in source, manifests, artifacts or logs.
6. Do not publish/tag/release or change repository visibility without explicit user authorization.

## Canonical references

- `README.md` — public project overview and entry points.
- `docs/ARCHITECTURE.md`, `docs/MODULE_SYSTEM.md` — system and extension boundaries.
- `docs/COMPATIBILITY-POLICY.md`, `docs/VERSIONING.md`, `docs/SUPPORT-MATRIX.md` — version and support claims.
- `docs/SECURITY_MODEL.md`, `SECURITY.md`, `docs/security/` — security boundaries and reporting.
- `docs/operations/` — operator runbooks.
- `docs/phase25-release-readiness.md` — Phase 25 acceptance evidence and RC package provenance.
- `PROGRESS-LOG.md` — append-only historical progress and validation record.
