# HISPANIA v1.0 — Core Dependency Allowlist (Step 0.3-R2/R3)

**Audit source HEAD:** `118840f934df0783de13bebcd7ea0ac0e66dc9fe`
**Status:** OWNER APPROVED — PROVISIONAL under Decision 0.3-D3; no Core project or artifact is implemented here.

**Slice 7 update (2026-10-18):** Runtime, ModuleHost and `NuGet.Versioning` are admitted narrowly for the signed fixture lifecycle. ModuleHost is packaged under `module-host/`; its out-of-process execution is fault isolation and supervision, not a security sandbox. The clean Windows x64 publish passes the exact-path allowlist and manifest checks, with source commit and artifact hashes in the [Slice 7 report](HISPANIA-V1-PHASE1-SLICE7-MODULE-MANAGER-REPORT.md). Published-process smoke and temporary-output cleanup remain pending; this does not authorize public distribution.

This is the bounded source-graph policy for the Phase 1 Empty Core contract in Architecture Freeze §13. Slice 7 implementation authorization admits only the entries explicitly marked allowed in the versioned JSON policy. Verify actual direct/transitive references, rights and per-RID publish contents against that policy. Public distribution remains blocked by D6.

## Candidate allowlist

| Layer | Candidate projects / framework | Evidence and boundary |
|---|---|---|
| Host | A new, clean Core composition root; `Microsoft.AspNetCore.App` | Freeze §13 requires a new root; do not convert `apps/server`. Framework reference is a shared runtime dependency, not a business-module reference. |
| Module runtime | `core/runtime/Hispania.Runtime.csproj`, only reviewed generic portions | Admitted for the package lifecycle, signature trust checks, package graph and OOP supervision used by Slice 7. It references ModuleSystem.Contracts, Platform.Contracts, `NuGet.Versioning` 7.9.0 and `Microsoft.AspNetCore.App`; no Media dependency is declared. Other Runtime features are not exposed as Core functionality by this slice. |
| Contracts | `contracts/module-system/Hispania.ModuleSystem.Contracts.csproj`; `contracts/platform/Hispania.Platform.Contracts.csproj`; only needed identity/authorization/audit/health/config/backup contracts | These projects have no direct NuGet dependencies. Include only contracts actually exercised by Empty Core. |
| Identity / authorization | `application/identity/Hispania.Identity.Application.csproj`; `application/authorization/Hispania.Authorization.Application.csproj` | Both currently reference Platform.Contracts only. Include only the minimal local authentication/bootstrap and administration path required by §13. |
| Module host | `apps/module-host/Hispania.ModuleHost.csproj` | Included under `module-host/` for the trusted fixture/OOP lifecycle; references ModuleSystem.Contracts and Platform.Contracts. OOP is fault isolation and supervision, not a security sandbox. |
| Core persistence | A new narrow, provider-neutral Core persistence contract and its SQLite provider | No dedicated Core persistence contract/provider exists in this baseline. Do not expose SQLite types in the Core public contract. Keep the abstraction limited to demonstrated Core-owned state operations. SQLite is the sole official Core provider in v1.0. |
| SQLite implementation dependency | Candidate `Microsoft.Data.Sqlite` 10.0.12 and transitive `Microsoft.Data.Sqlite.Core` 10.0.12, `SQLitePCLRaw.bundle_e_sqlite3`, `.core`, `.lib.e_sqlite3`, `.provider.e_sqlite3` 2.1.12 | Resolved dependency inventory metadata reports MIT for Microsoft.Data.Sqlite packages and Apache-2.0 for SQLitePCLRaw packages; this is metadata, not legal clearance. Actual transitive/publish graph and notices must be generated for the eventual artifact. |
| Runtime package dependency | `NuGet.Versioning` 7.9.0 | Required by the package graph planner used by Core; inventory metadata reports Apache-2.0, not legal verification. No internal-use restriction was found in the recorded package metadata; distribution notices remain a separate gate. |
| Test-only | New Core fitness/process tests and signed harmless fixture | Tests and fixture must not leak into the production package. A fixture signer is explicitly trusted by the test operator; signatures establish publisher/integrity, not safety. |

## Core package and rights risk register

The package/license values below are the R1 resolved inventory's declared metadata. “Evidence of rights” is not established for the HISPANIA source by that metadata; upstream package metadata is not a license review. No artifact-specific `dotnet publish` graph exists for the proposed Core.

| Dependency | Version | Proposed Core use | Declared license | Evidence of rights | Distribution intended | Risk |
|---|---:|---|---|---|---|---|
| .NET / `Microsoft.AspNetCore.App` shared framework | .NET 10 (runtime installed in R1: 10.0.12) | Host, HTTP/admin, dependency injection, logging APIs | .NET runtime/framework license; not separately verified in this review | Platform installation/runtime only; no HISPANIA redistribution permission inferred | framework-dependent Core initially, subject to Phase 1 artifact choice | Low package-graph complexity; distribution obligations depend on self-contained vs framework-dependent target. |
| `Hispania.Runtime` → `NuGet.Versioning` | 7.9.0 | Required module package/version range processing | Apache-2.0, reported by inventory metadata | Package metadata only; no upstream notice or artifact inclusion review | Present in the Slice 7 Core runtime graph | Confirm actual artifact hash and retain notice review as a public distribution gate. |
| New Core persistence contract | New project; version not selected | Provider-neutral Core-owned state operations | HISPANIA source target AGPLv3 per 9.11; current `LICENSE.md` is personal/non-commercial | File-level ownership/authority not established; no license change authorized | Public Core source/artifact intended eventually | **BLOCKER for reuse/relicensing/distribution** until source authority and license scope are resolved. |
| New SQLite provider → `Microsoft.Data.Sqlite` | 10.0.12 | Official Core v1.0 SQLite implementation | MIT, reported by metadata | Metadata only; no artifact-specific notice/source review | Expected runtime dependency/native payload in selected RID artifact | Evaluate MIT conditions plus the actual native payload and publish mode. |
| `Microsoft.Data.Sqlite.Core` | 10.0.12 (transitive) | SQLite provider core | MIT, reported by metadata | Metadata only | Expected transitive runtime dependency | Include only if graph confirms; review actual bundled files/notices. |
| `SQLitePCLRaw.bundle_e_sqlite3`, `.core`, `.lib.e_sqlite3`, `.provider.e_sqlite3` | 2.1.12 (transitive) | SQLite native provider and platform-specific SQLite library | Apache-2.0, reported by metadata | Metadata only; exact native RID assets not enumerated for Core | Expected in self-contained/native-target publish as applicable | Material native payload and per-RID obligations; generate final SBOM/notices from actual publish output and review upstream texts. |
| RSA-PSS / SHA-256 / cryptographic APIs | .NET BCL; no additional NuGet identified | Module package signature and hash checks | .NET framework terms | No separate HISPANIA package rights issue identified; runtime terms still apply | Framework dependency | No external crypto package in inspected contracts/runtime/module-host project graph; cryptographic validation is not safety or sandboxing. |
| Logging (`ILogger`), dependency injection, host APIs | `Microsoft.AspNetCore.App` shared framework | Runtime supervision/logging and host integration | Shared framework terms | Same framework runtime caveat above | Framework-dependent or included in self-contained artifact | No direct logging NuGet needed for current candidate graph. Confirm exact host graph later. |
| Test packages | TestHost 10.0.1; Microsoft.NET.Test.Sdk 17.11.1 / 17.14.1; xUnit 2.9.2/2.9.3; runner 2.8.2 and test-only transitives | Fitness/process tests only | Mixed; reported in inventory metadata | Metadata only | **Not** Core runtime distribution | Must not leak into production package; no exact test graph selected for future Core. |
| Media/Inventory/Workers and frontend packages | Existing project-specific versions in inventory; no Core versions proposed | None | Mixed, reported metadata; some license entries require additional review | Not applicable to proposed Core | Excluded | Any accidental project/content reference is a Core boundary failure; third-party package notices and native contents are outside this allowlist. |
| Legacy/demo binaries (five DLLs + MP4) | N/A | None | Unknown / unverified | No per-file grant located | Excluded from Core | Unknown rights; separate legacy test/server use remains unresolved. |

**Internal vs distribution risk:** package inventory inspection is analysis. NuGet restore/build/test or source reuse is conditional on applicable project rights and license scope; package metadata does not grant rights to HISPANIA code. Public Core/SDK/module package distribution is blocked until source authority, package notices, actual native RID payload and intended license boundaries have been reviewed. No compatibility or AGPL/Apache conclusion is made here.

## Explicit exclusions

- `apps/server`, its `Hispania.Hosting`/service packaging, demo routes and demo content.
- Media/Library/Playback/TMDB, Inventory, ERP, AI, FFmpeg/ffprobe, frontend assets and demo business modules.
- PostgreSQL/Npgsql and Library/Media provider/schema/migrations. The existing PostgreSQL provider belongs to Media; it is not Core support and must not be changed in Phase 1.
- `infrastructure/platform/persistence-sqlite` as an assumed drop-in Core provider: its current project references Platform.Contracts, Identity.Application, Authorization.Application, Platform Jobs and Maintenance applications. Reuse only after proving that this transitive surface is necessary; otherwise Phase 1 design should isolate the narrow Core SQLite provider.
- `infrastructure/platform/worker-agent-jobs`, distributed-work persistence, remote workers, and scheduler implementations unless an approved Core contract demonstrates a boot-critical dependency.
- CLI as a host dependency (it may be a client); tests, TestHost/xUnit/Test SDK; historical compatibility DLLs; MP4; module-owned stores/migrations; native/media binaries; npm packages.
- `core/kernel/Hispania.Kernel.csproj` by default: it is currently an empty library with no references/usages found outside its own project file. Add it only if concrete reviewed Core functionality requires it.

## Important graph caveats

1. The existing SQLite provider is Platform persistence, not a provider-neutral Core persistence implementation. Its direct project references bring identity, authorization, jobs and maintenance applications into the graph.
2. Runtime's project graph is not proof that all its runtime features belong in Core. Slice 7 admitted it for package lifecycle and trust composition only; an artifact manifest must continue to enforce the file and dependency allowlist.
3. Freeze §12 labels several missing structures as Phase 1 blockers, while §§13 and 16 place project creation, manifest/test delivery and other work within later Phase 1 preparation/implementation. See the R2 readiness addendum and owner packet; this proposal does not resolve that timing ambiguity.
4. No restore, build, test, publish or code change was performed for this proposal.

## Owner action requested

Slice 7 explicitly authorized local use of the existing contracts, Runtime, installer, catalog and ModuleHost for this bounded implementation; this is not an ownership, relicensing or distribution finding. The R5 owner attestation is retained as bounded local-development authority, and the new fixture's provenance is documented separately. Public Core/SDK/module distribution remains blocked pending D6 and a review of exact publish contents and notices; see [R3 owner decisions and execution plan](HISPANIA-V1-STEP-0.3-R3-OWNER-DECISIONS-AND-PHASE1-PLAN.md) and the Slice 7 closeout report.
