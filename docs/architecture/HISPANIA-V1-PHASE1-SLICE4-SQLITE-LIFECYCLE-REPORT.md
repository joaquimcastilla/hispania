# HISPANIA v1.0 — PHASE 1 SLICE 4 REPORT

## A. STATUS

**PASS — BOUNDED TO THE WINDOWS X64 CORE HOST, REAL PUBLISH, AND SELECTED REGRESSION.** SQLite persistence initializes before HTTP, survives restart, applies a transactional schema migration, and fails closed on tested invalid/corrupt/incompatible storage. This is not Linux/ARM64 validation or release approval.

## B. ROADMAP STATUS

Roadmap remains **4/14 — 28.6%**. Phase 1 Slice 4/10 is complete at the bounded scope in this report; Slice 5 and all later work remain pending separate authorization.

```text
[✓] Phase 0 — Architecture Freeze
[✓] 0.1 — Baseline Reconciliation
[✓] 0.2 — Baseline Consolidation
[✓] 0.3 — Phase 1 Readiness
[~] 1 — Empty Core
    [✓] Slice 1 — Compatibility Baseline
    [✓] Slice 2 — Core Allowlist & Manifest
    [✓] Slice 3 — Independent Core Host
    [✓] Slice 4 — SQLite & Lifecycle
    [ ] Slice 5 — Identity & Authorization
    [ ] Slice 6 — Minimal Secure Endpoints
    [ ] Slice 7 — Module Manager & Trust
    [ ] Slice 8 — Artifact Isolation
    [ ] Slice 9 — Cross-platform Publishing
    [ ] Slice 10 — Final Acceptance
[ ] 2 — Module System & SDK
[ ] 3 — Inventory Module
[ ] 4 — Coordinator & Distributed Workers
[ ] 5 — Direct Data Plane & Gateway
[ ] 6 — Media Module
[ ] 7 — Identity, Permissions & Clients
[ ] 8 — Administration & Recovery
[ ] 9 — Cross-platform Validation
[ ] 10 — Security & Release v1.0
```

## C. PREFLIGHT

Worktree `C:\Users\joaqu\Documents\HISPANIA-v1-baseline-consolidation`, branch `codex/hispania-v1-baseline-consolidation`. Starting HEAD was `3beb15c32d6cf977c7e7e6cb83611e69b68243ec`, the Slice 3 documentation closeout; Slice 3 implementation commits were `af1356a7743f5a46d19955be1b3eded18175354d` and `8d810fdd3ba23604ee113ee02767a05c2789b9ff`. The worktree was clean before Slice 4; there was no in-progress Git operation and no upstream. The freeze, allowlist/validator, Slice 3 report, and evidence/rights records were reviewed. The 15 original worktrees were preserved (16 listed including this authorized worktree). The preservation manifest was rechecked: 49 live source files and 49 backups matched their recorded SHA-256 values (98 file checks, zero mismatches). `LICENSE.md` remains SHA-256 `75EAE29E043C82B168894AFAAA148ADE2E9A43B4A72513FF18CFD22434F9BD3D`.

## D. PERSISTENCE ARCHITECTURE

The minimal provider-neutral `ICorePersistence` contract and `SqliteCorePersistence` adapter reside in `apps/core`; a separate project was not justified by the present graph. Core composition explicitly selects SQLite for v1.0. The contract exposes only initialization, stable technical instance state, and shutdown checkpoint behavior. It owns no module/domain data. `apps/core` has no project references and no domain services. No ORM was added; the adapter uses `Microsoft.Data.Sqlite` ADO.NET APIs. PostgreSQL Core is out of scope; the existing Library/Media PostgreSQL provider was not modified and remains module-owned.

## E. SQLITE SCHEMA

Schema version 1 has two tables:

- `core_schema_migrations(version INTEGER PRIMARY KEY CHECK(version > 0), applied_utc TEXT NOT NULL)` records applied migration versions.
- `core_instance(singleton_id INTEGER PRIMARY KEY CHECK(singleton_id = 1), installation_id TEXT UNIQUE NOT NULL, created_utc TEXT NOT NULL)` stores one stable installation UUID and creation timestamp.

The first migration creates both structures and the instance row. Migrations are ordered, recorded in the same transaction, and repeat startup verifies the current schema and state. No users, credentials, sessions, permissions, module settings, Inventory, Media, jobs, or worker tables were introduced.

## F. CONFIGURATION

`HISPANIA_CORE_DATA_DIRECTORY` configures the data root. The default is the OS user's LocalApplicationData `Hispania/Core` directory; the database filename is fixed as `core.db`. The current working directory is not used. Relative paths, paths inside the publish directory, and detected reparse-point database/root paths are rejected. The directory is created when needed. Unix directory/file modes 0700/0600 are applied in code; Linux behavior was not independently exercised. On Windows, inherited ACLs are used and were not independently audited. No administrator/root privilege is required by the tested path. A published-process check with a relative path exited 2 before listening and emitted only a sanitized configuration error.

## G. LIFECYCLE

`CorePersistenceLifecycle` is registered as a hosted service implementing `IHostedLifecycleService`. Its `StartingAsync` initializes storage before the host starts Kestrel; `StoppingAsync` runs the passive checkpoint. Each connection is short-lived and pooling is disabled. A real framework-dependent published process initialized SQLite, served only the existing loopback `GET /health/live` route, returned 200 with `{"status":"healthy"}`, rejected POST with 405, and returned 404 for `/admin`. Ctrl+C produced the stop and checkpoint logs. The process was started twice against the same database; the schema and installation identity remained stable and the database SHA-256 remained unchanged across the restart check.

## H. CONCURRENCY & TRANSACTIONS

SQLite uses WAL, `synchronous=FULL`, foreign-key enforcement, a 5,000 ms busy timeout, private cache, and disabled connection pooling. Schema initialization/migrations use an immediate serializable transaction; migration failure rolls back. A process-local keyed semaphore prevents concurrent initialization by multiple Core hosts in one process; SQLite's write lock and timeout bound cross-process writer contention. The busy-lock test confirms the configured timeout path. SQLite remains a single-writer database: no multi-master/high-availability guarantee, no shared-network-file multi-Coordinator support, and no distributed Worker database role is claimed. Shutdown requests `wal_checkpoint(PASSIVE)`; an incomplete passive checkpoint is logged safely and does not discard data.

## I. FAILURE HANDLING

Startup checks `PRAGMA quick_check`, migration history, current schema shape, and `PRAGMA foreign_key_check`. Future schema versions, incomplete history, unknown tables without migration history, corruption, invalid instance metadata, invalid paths, and SQLite/open errors fail startup without automatic deletion or destructive repair. Migration failure rolls back transactionally. Errors exposed by the host are sanitized and do not contain database paths or instance identifiers. Health remains a liveness-only response and does not reveal persistence details. Recovery is operator-led; automatic restoration is not implemented.

## J. DEPENDENCY GRAPH

Resolved exact runtime packages: direct `Microsoft.Data.Sqlite` 10.0.12; transitives `Microsoft.Data.Sqlite.Core` 10.0.12 and `SQLitePCLRaw.bundle_e_sqlite3`, `.core`, `.lib.e_sqlite3`, and `.provider.e_sqlite3` 2.1.12. No EF Core or PostgreSQL package is in the Core graph. Cached package metadata lists Microsoft packages as MIT and SQLitePCLRaw packages as Apache-2.0; the bundled SQLite native source is identified as public domain by the reviewed package metadata. This is local review evidence, not redistribution clearance. The real `win-x64` publish includes `e_sqlite3.dll` (file version 3.53.3.0), 1,978,880 bytes, SHA-256 `B7385D722C83FB52142A00477A726723745916D22A555711EE89834C1111FB2E`. Package and artifact details are in [the SQLite package inventory](HISPANIA-V1-CORE-SQLITE-PACKAGE-INVENTORY.csv).

## K. REAL PUBLISH VALIDATION

Published `net10.0`, `win-x64`, framework-dependent with `dotnet publish apps/core/Hispania.Core.csproj -c Release -r win-x64 --self-contained false`. The real artifact has nine runtime files: `Hispania.Core.deps.json`, `Hispania.Core.dll`, `Hispania.Core.exe`, `Hispania.Core.runtimeconfig.json`, `Microsoft.Data.Sqlite.dll`, `SQLitePCLRaw.batteries_v2.dll`, `SQLitePCLRaw.core.dll`, `SQLitePCLRaw.provider.e_sqlite3.dll`, and `e_sqlite3.dll`. The versioned fail-closed allowlist and actual `.deps.json` graph validated with no issues. Final manifest content ID: `9C003F039BD33299FC10A7EE3643E2ED7C87627391C3B1F3C81CED882EC4E547`; manifest SHA-256 `A6805ABBFBEC402A1D8AD5DC462692E52EF4030F8AB9FFA0B444DD4FA69B0322`. No Inventory, Media, FFmpeg, WorkerAgent, Npgsql/PostgreSQL, or six excluded historical hashes were found in the Core publish. The validator now accepts only the exact selected-RID native SQLite asset mapping from `.deps.json` to the known flattened publish filename; negative tests continue to reject a missing native file and forbidden EF Core packages.

## L. TEST EXECUTION

Environment: Windows 11 x64, .NET SDK 10.0.401, .NET runtime 10.0.12. Core Release build: 0 warnings, 0 errors. Core suite: **27/27 passed**, 0 failed/skipped; final TRX SHA-256 `1B8BA213B00C5DBBCA66100814936D1EF3CAF5CA1F42BA2D6480CD6D9C510BA3`. Artifact-validator suite: **26/26 passed**, 0 failed/skipped; final TRX SHA-256 `BDED13D1CC3365E58E417C260243A817C2194ACA42F04161C26DF38C9958A0FD`. Added coverage includes new/restart/concurrent initialization, writer timeout, migration rollback, future/corrupt schemas, invalid paths, and real native asset policy cases. All databases are synthetic temporary files.

The selected Slice 1 regression is **83/83 passed**: Inventory 29/29 (TRX SHA-256 `38FA61D01141FD381115CAD4254699031FEBF64E525082E47B1FF4EA58D1B3DD`), Deployment 35/35 (`35658E9DBBF7A3327E5EAFEF78CDC223474EA908FD042AC9CEE3E066C4AB3EE4`), and Runtime 19/19 (`77EA29F7FE8AD75B32635BE82030C8C24B3CA80E6355CC05AAA8D27CB639248A`). Final test TRXs and logs are under `%TEMP%\hispania-slice4-20261017\final-verified`; regression build intermediates used the repository's ignored `bin`/`obj` folders. No test databases contained production data. Log SHA-256: Core `B9B66BDE2C8CAF80BE0AEFCED0926168D1C5D7BF322186BFEF0133B5E4225B4A`, Validator `3219E78290973387D1986F4C69353B0964E5B667850E5347FC2740543578BE96`, Inventory `129D7C4FCA079E2157CED3DA4E78CB2CFE2DF87B16768766C87E25CC24268E99`, Deployment `38871E2FA0D035F730EFA1853EDECFDFA3C2F66F2B9293C9396A0B8BFBB9ED2C`, Runtime `293BB4CFFB5C6ED6BF07C04BFBBF6B815B6BA1F9885B00A4F6CB7BFB67FF5087`. Runtime/deployment restore logs: `0C14316100E67D800B2D7642E8FC450C4D2660B474DDA5534722D20562EBC1EB` and `1189B3CACC9B68797967765C5E37A81BA8B64F1C5E267C6496D0504EF554AB92`.

## M. REGRESSION

The exact bounded selection from Slice 1 was rerun: 29 Inventory/Platform/storage, 35 Deployment/Hosting, and 19 Runtime/module tests; total **83/83**, matching the Slice 1 baseline and Slice 3 selected regression. This is not the entire repository test suite. The full `tests/inventory` dependency graph builds the pre-existing `apps/server` project as a referenced dependency; no source or tracked `apps/server` file changed.

## N. RIGHTS & LICENSES

The new persistence code and tests are first-party work for bounded local development/validation. Exact package metadata and native artifact identity are recorded in the package inventory and rights register. `LICENSE.md` is unchanged. No redistribution, relicensing, release, or third-party notice clearance is implied.

## O. DOCUMENTATION

Created this report and `HISPANIA-V1-CORE-SQLITE-PACKAGE-INVENTORY.csv`; updated the Evidence Ledger, Core artifact allowlist, dependency classification, and bounded contributor-rights register. Historical reports and public contracts were not rewritten. No changes were made to `apps/server` source, historical test source, or `LICENSE.md`.

## P. DEFERRED VALIDATION

Linux x64 and ARM64 publish/execution and native asset hashes remain unverified; Unix permission behavior was not run. Windows ACL inheritance was not audited. Windows service-manager installation, SIGTERM, backup/restore tooling, online backup API, crash/power-loss fault injection, and advanced recovery are deferred. A future backup implementation must use SQLite's consistent backup mechanism or equivalent WAL-aware procedure; copying only an active `.db` while ignoring `-wal`/`-shm` is not a valid backup. No Linux/ARM64 or release support claim is made.

## Q. INTEGRITY

Preflight and final checks retained all 15 original worktrees and revalidated 49 source/backup pairs (98 SHA-256 comparisons, zero mismatches). `LICENSE.md` SHA-256 is unchanged. `git diff --check` passed. A scoped review confirms only the new Core code/tests and Slice 4 validator/policy/evidence files changed; no `apps/server` source, public contracts, historical tests, or license file changed. Secret-pattern scanning of new staged content is recorded with the closeout checks. No push was made; the branch still has no upstream.

## R. COMMITS

- `365c2eb8e905e33f4528e87aa495ecac5c790759` — `feat(core): add SQLite persistence and migrations`.
- Validator, allowlist, package inventory/classification, and rights evidence commit: recorded in the final task response.
- Slice 4 report and EV-045 documentation commit: recorded in the final task response.

## S. FINAL STATE

Branch `codex/hispania-v1-baseline-consolidation`; final HEAD and clean worktree are recorded in the final task response. No upstream is configured. No push, merge, tag, release, or Slice 5 work occurred.

## T. NEXT ACTION

**SLICE 4 PASS — AWAIT NEXT PROMPT.** Stop here. Slice 5 requires a separate owner prompt.
