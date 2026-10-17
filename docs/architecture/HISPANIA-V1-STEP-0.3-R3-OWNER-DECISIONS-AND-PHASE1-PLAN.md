# HISPANIA v1.0 — Step 0.3-R3 Owner Decisions and Phase 1 Execution Plan

**Decision record:** R3 records owner decisions D1–D6 from the explicit owner prompt.
**Readiness:** recommendation is **CONDITIONAL — SPECIFIC PRE-IMPLEMENTATION ISSUE**; see §3.
**Phase 1:** NOT AUTHORIZED. This is planning only; no project, test, code, or license was changed.

## 1. Owner decisions 0.3-D1 through 0.3-D6

All six are **OWNER APPROVED** with the exact scope and conditions below. The record is based on the owner's written instruction; it is not a digital signature, legal opinion, rights attestation, or distribution approval.

| ID | State | Approved scope and conditions |
|---|---|---|
| 0.3-D1 — Internal development | **APPROVED** | Phase 1 may be developed/tested internally only after Step 0.3 passes and the owner separately authorizes its start. Internal use only; reuse is subject to sufficient rights; no public publication, third-party code/binary distribution, release, or installation over production data. This decision does not itself authorize Phase 1. |
| 0.3-D2 — §12 interpretation | **APPROVED** | All seven §12 items are Phase 1 implementation/acceptance requirements. Before implementation: do not transform `apps/server`; keep development additive; preserve existing contracts; define the admin security boundary before enabling mutation endpoints. The seven requirements must be implemented and validated during Phase 1; Phase 1 cannot pass without all seven. This clarifies the Freeze prospectively without rewriting its original text. |
| 0.3-D3 — Core dependencies | **APPROVED — PROVISIONAL** | Approves the proposed allowlist in [Core Dependency Allowlist](HISPANIA-V1-CORE-DEPENDENCY-ALLOWLIST.md). During Phase 1 verify direct/transitive dependencies and final per-RID artifact; reject accidental domain references; review incorporated-component licenses; retain a reproducible manifest. This is not legal permission to distribute or final artifact acceptance. |
| 0.3-D4 — Unknown-rights components | **APPROVED** | Exclude the five historical DLLs and the demo MP4 listed in [R2 inventory](HISPANIA-V1-UNKNOWN-RIGHTS-COMPONENTS.csv) from Empty Core; keep them in historical locations; do not alter existing tests. Phase 1 must test their absence from the Core artifact. This does not clear their rights for any other profile/distribution. |
| 0.3-D5 — Contributor authority | **APPROVED** | Create and maintain a provenance/permission register for code and assets. Do not presume all commits belong to the owner, that all code can be relicensed, that assets may be redistributed, or that generated components have sufficient rights. Record authors, sources, permissions and licenses where evidence exists. Do not invent authorization or ownership statements. Do not incorporate material lacking sufficient rights into the new Core before clarification. |
| 0.3-D6 — License and distribution | **APPROVED** | Keep `LICENSE.md` unchanged. AGPLv3 Core / Apache-2.0 SDK remains the architectural target, not completed relicensing. No public publication/distribution until sufficient authority is evidenced; dependency, asset and binary licenses reviewed; notices prepared; legal compatibility reviewed; and the owner expressly approves distribution. No release or package publication. |

## 2. Freeze clarification and Phase 1 gates

The seven Freeze §12 items retain their original wording. By D2, their absence is not a reason to classify all seven as pre-code blockers. They are deliverables and acceptance conditions inside Phase 1, with explicit preconditions:

- **Before Phase 1 implementation:** Step 0.3 owner acceptance; separate express Phase 1 authorization; scope remains internal/additive; no `apps/server` conversion; select the rights-cleared source/test inputs for each planned slice; keep existing public contracts unchanged. The allowlist has provisional owner approval; exact project graph and manifest are deliverables in Phase 1.
- **Before administrative mutation endpoints:** define the secure bootstrap/auth boundary and fail-closed access policy.
- **Before module fixture validation/activation:** explicitly configure a trusted fixture publisher and separate permission grants; signature is not proof of safety; OOP/ALC are not sandboxes.
- **During Phase 1:** create the clean Core root; publishable allowlist/manifest; prove Media/Inventory/providers/demo dependencies absent; implement and exercise empty boot; secure admin boundary; package/state layout; signed trusted fixture flow and failure isolation; SQLite Core persistence contract/provider.
- **Before Phase 1 acceptance:** all seven §12 requirements tested; package/artifact graph and tests pass; clean Core contains no Media/Inventory or the six excluded artifacts; backup/restore boundary from §13 is satisfied or an owner-approved 1.1 checkpoint exists; applicable Windows/Linux and ARM64 smoke requirements pass when artifacts are available.
- **Before any public distribution/release:** rights and authority for every shipped component; intended licenses applied only to cleared files; dependency and native payload notices/source obligations; compatibility/link/load/IPC review; explicit owner distribution approval. D1/D6 prohibit public distribution before these gates.
- **Deferred:** sustained Raspberry Pi Coordinator qualification, full ARM64 hardware qualification, PostgreSQL Core, Media/Inventory extraction, untrusted-native sandbox/WASM, Marketplace and Data Plane implementation stay in their approved later phases. The Phase 1 ARM64 native smoke is not deferred where §13 requires it once an executable artifact exists.

The complete §12/§16 source classification remains in [Readiness Blockers](HISPANIA-V1-READINESS-BLOCKERS.md); D2 now resolves the previous timing ambiguity without amending the Freeze's historical text.

## 3. Final readiness assessment

**Current recommendation: CONDITIONAL — SPECIFIC PRE-IMPLEMENTATION ISSUE.**

The owner has resolved the six policy questions and D2 classifies §12. No missing Core composition root, manifest, empty-boot test, or fixture is now a pre-code blocker; these are Phase 1 work. No unresolved technical failure in the available Runtime Debug evidence blocks starting a new Core.

One concrete pre-implementation condition remains: the owner has not yet identified a rights-cleared subset of existing HISPANIA source and test inputs for the approved first slices. `LICENSE.md` grants personal/local/non-commercial use only for material the named owner owns or is authorized to license. D1 explicitly conditions reuse on sufficient rights and D5 forbids incorporating material with insufficient rights. R3 therefore does not treat all current Runtime/contracts/tests as cleared by the approval itself. Before the first slice reuses or compiles existing source, the provenance register must identify the files/projects and evidence that cover that internal use. Any unclear file is excluded until clarified. This is a bounded rights-evidence condition, not a technical failure and not a request to change the license.

After that condition is satisfied, Step 0.3 still needs the owner's explicit **acceptance of the final readiness report**, followed by a separate express authorization to start Phase 1. R3 gives neither.

## 4. Incremental Phase 1 execution plan — ten slices

Slice names and exact project names are planning proposals; no files/projects are created by this document. Use isolated, reviewable commits. Each slice must pass before the next begins. Precondition for every slice: prior slice accepted, no public distribution, no production data, and only rights-cleared source/fixtures in that slice.

### Slice 1 — Compatibility and regression baseline

- **Objective:** establish a reproducible before/after baseline and protect existing contracts, identity, authorization, persistence, runtime and deployment behavior.
- **Planned projects/files:** existing relevant test projects and their documented commands; new Core fitness tests may be planned but not added until this slice is authorized. Keep legacy test source and fixtures untouched.
- **Dependencies/contracts:** selected rights-cleared baseline source/test set; provider/runtime contracts; historical artifacts only if rights evidence permits their test use. Do not execute the unknown-rights binaries without that evidence.
- **Tests:** evidence-preserving selected suites: Runtime 219/219 Debug, Phase 19 lifecycle 76/76 and backup 2/2 where reproducibly available; Identity/Authorization/Events/SQLite 22/22 only after locating its evidence or rerunning on an authorized future Phase 1 task; Deployment 35/35 historical suite. Do not represent historical runs as current HEAD.
- **PASS:** selected suite matrix has exact source SHA, command, TRX/hash and limitations; no unexpected regression; prohibited/uncleared fixtures are not run.
- **Rollback:** revert only this slice's future test-harness/config changes; preserve reports and all historical fixtures.
- **Risks:** test evidence may be tied to different commits; unknown-rights fixtures; full test selection may import domain projects.
- **Evidence:** approved rights list for test inputs, command manifest, baseline test reports and source SHAs.
- **Before this slice:** D1's internal-only boundary; Step 0.3 acceptance and Phase 1 authorization; D5 rights register must cover every executed input.

### Slice 2 — Core allowlist and reproducible artifact manifest

- **Objective:** freeze actual project/reference/content allowlist and fail build/publish on forbidden dependencies or payloads.
- **Planned projects/files:** solution/solution filter; proposed Core project manifest; narrowly scoped graph/content validation script and documentation; no modifications to `apps/server`.
- **Dependencies/contracts:** D3 provisional allowlist; exact rights-cleared projects from Slice 1; pinned .NET SDK/dependency graph; provider-neutral Core persistence contract direction.
- **Tests:** project-reference graph assertions; package/content manifest check; negative checks for Media, Inventory, PostgreSQL Core, demo assets, five DLLs and MP4.
- **PASS:** deterministic manifest; no forbidden references or content; dependency graph and direct/transitive licenses recorded.
- **Rollback:** revert only new manifest/validator/solution-filter changes; do not remove or rewrite existing server references.
- **Risks:** Runtime transitive breadth; accidental shared build content; generated framework/native package files.
- **Evidence:** checked-in manifest, graph report, package inventory and repeatable command output.
- **Before this slice:** Slice 1 baseline accepted; D3 allowlist approved provisionally; rights coverage for each existing project.

### Slice 3 — New `apps/core` composition root, boot and configuration

- **Objective:** create an additive, clean Core host with health/readiness and shutdown; no business modules.
- **Planned projects/files:** new `apps/core` host project and Core-only configuration/bootstrap scaffolding; names illustrative pending actual design.
- **Dependencies/contracts:** approved manifest; ASP.NET shared framework; minimal reviewed Runtime/Platform contracts only; no `apps/server` project reference.
- **Tests:** build/publish Core; boot/health/shutdown in isolated temporary state; assert no modules and no Media/Inventory routes or assemblies.
- **PASS:** clean empty boot/restart; health available; no dependency on legacy server, module availability or Internet.
- **Rollback:** remove the new Core project/host files as a slice; keep the server and existing data untouched.
- **Risks:** accidentally inheriting server defaults, state paths, or web assets; configuration secrets leakage.
- **Evidence:** package hash, graph/content report, process logs and health assertions.
- **Before this slice:** Slices 1–2 pass; only rights-cleared projects reused.

### Slice 4 — Core SQLite persistence and lifecycle

- **Objective:** implement the smallest provider-neutral Core state contract and its official v1.0 SQLite provider; validate schema lifecycle/reopen.
- **Planned projects/files:** new Core persistence contract/provider projects or narrowly reviewed existing provider components; Core-owned migrations and persistence tests. Do not change Media/Library SQLite/PostgreSQL schemas.
- **Dependencies/contracts:** D3; `Microsoft.Data.Sqlite` and SQLitePCLRaw graph for target RIDs; approved Core state path; rights-cleared persistence/runtime source.
- **Tests:** provider contract tests, create/reopen/restart, migration and corrupt/invalid-state behavior, backup/restore boundary; prove SQLite provider behind provider-neutral contract.
- **PASS:** Core persists/reopens only its state; no PostgreSQL Core dependency; migration/backup behavior meets §13 boundary.
- **Rollback:** restore only the isolated test data directory from its pre-slice snapshot; revert Core-only schema/provider changes; never touch production DBs.
- **Risks:** native SQLite payload varies by RID; old Platform SQLite provider drags unrelated applications; schema compatibility.
- **Evidence:** migration manifest, test report, per-RID publish file list, dependency notices and backup/restore logs.
- **Before this slice:** Slices 1–3 pass; native/managed dependency rights and target RID payloads recorded.

### Slice 5 — Identity, bootstrap and authorization

- **Objective:** implement one-time secure local admin bootstrap, sessions and minimum default-deny authorization for Core administration.
- **Planned projects/files:** new Core identity/authorization composition and bootstrap services; reuse only rights-cleared application contracts/services; no change to current server auth behavior.
- **Dependencies/contracts:** identity/session/auth/audit contracts; SQLite Core store; secret handling and permission boundary frozen before route exposure.
- **Tests:** one-time bootstrap races, no default credentials, no Internet requirement, session issuance/revocation, default-deny and authorization unit/integration cases.
- **PASS:** no unauthenticated admin mutation path; bootstrap is one-use and secrets are protected; existing contracts remain compatible.
- **Rollback:** revert Core-only service composition and restore isolated test state; no production identity DB migration.
- **Risks:** exposing mutating APIs before policy enforcement; bootstrap race or credential disclosure.
- **Evidence:** security test TRX, route-policy inventory, sanitized logs and state cleanup proof.
- **Before this slice:** Slice 4 pass; security boundary approved before endpoints are bound.

### Slice 6 — Minimal admin and operational endpoints

- **Objective:** expose health/readiness, authenticated administrator operations, empty module list and minimal audit.
- **Planned projects/files:** Core-only API endpoint/DTO/validation/audit files and endpoint integration tests.
- **Dependencies/contracts:** Slices 3–5; existing public contracts remain unchanged; Core-specific DTOs must be versioned and not leak provider types.
- **Tests:** anonymous read/mutation policy, authenticated operations, readiness dependency behavior, audit records, restart and empty list.
- **PASS:** protected mutations reject unauthenticated/unauthorized requests; health/readiness semantics accurate; no module/domain route appears by default.
- **Rollback:** remove Core-only endpoint registrations; no legacy routes changed.
- **Risks:** route collision, information disclosure, inadvertent public access or coupling to module presence.
- **Evidence:** endpoint matrix, HTTP integration TRX and captured package route inventory.
- **Before this slice:** D2 security boundary is fixed; Slice 5 security behavior passes.

### Slice 7 — Module Manager, publisher trust and fixture lifecycle

- **Objective:** install/disable a harmless, signed fixture from an explicitly trusted test publisher; survive fixture failure without taking down Core.
- **Planned projects/files:** Core-only Module Manager composition, trust configuration and fixture package/source generated from rights-cleared code; ModuleHost executable as approved by manifest.
- **Dependencies/contracts:** ModuleSystem contracts/runtime; RSA-PSS verification; D2/Owner Decision 9.2 trust tiers; publisher trust distinct from permissions.
- **Tests:** trusted fixture install/disable/restart/failure isolation; unknown signer, revoked signer, tampered package/hash/signature and incompatible host fail-closed before execution.
- **PASS:** only the explicit trusted fixture is activated; failure remains isolated; no claim of hostile-code sandbox; no fixture inherits permissions by signature.
- **Rollback:** disable/uninstall fixture and restore Core-owned test state; preserve package evidence; no global cleanup.
- **Risks:** treating signature or OOP/ALC as safety; accidental package sourcing/network dependence; unbounded permissions.
- **Evidence:** test signer identifier (private key kept out of repo), package hashes, trust decisions and isolated process logs.
- **Before this slice:** Slices 1–6 pass; publisher explicitly configured and fixture rights recorded.

### Slice 8 — Artifact isolation and forbidden-content proof

- **Objective:** demonstrate clean Core artifact has no Media/Inventory/provider/demo components and none of the six R2 unknown-rights artifacts.
- **Planned projects/files:** artifact inspection/fitness checks and generated publish manifest only; no mutation of historical files/tests.
- **Dependencies/contracts:** Slice 2 manifest and Slice 7 trust/package outputs; D4 exclusions.
- **Tests:** scan package file list, assembly/reference graph, routes, migrations and content for forbidden IDs/paths; assert absence of all six R2 paths/hashes.
- **PASS:** Core artifact excludes all listed DLLs/MP4 and all business modules; tests fail on reintroduction.
- **Rollback:** revert only the new validator/manifest, not the source files or historical corpus.
- **Risks:** hidden content from project globs, transitive content-copy targets or module-host output.
- **Evidence:** signed-off file list, hashes and successful negative assertions.
- **Before this slice:** Slices 2 and 7 pass; any package content source is rights-cleared.

### Slice 9 — Cross-platform publish and available platform smoke

- **Objective:** build/publish the same Core manifest for Windows x64 and Linux x64 and add Linux ARM64 native smoke when an executable artifact/runner is available.
- **Planned projects/files:** Core publish profiles/CI matrix and temporary validation scripts; not service installation or Pi sustained tests.
- **Dependencies/contracts:** clean package from Slice 8; pinned SDK/RIDs; SQLite native assets/notices; available Windows/Linux/ARM64 runners.
- **Tests:** reproducibility/hash, startup, admin auth, SQLite persistence/restart, fixture lifecycle where supported; ARM64 smoke only with native runner as §13 states.
- **PASS:** required available targets pass with exact runner/RID; no x64-only dependency; ARM64 not falsely certified if runner unavailable.
- **Rollback:** remove only new Core artifacts in temporary output and revert Core-specific CI/profile changes; no machine services installed.
- **Risks:** native `e_sqlite3` RID selection, OS-specific permissions, framework runtime availability.
- **Evidence:** per-RID SBOM/notices, package hashes, runner identity and TRX/logs.
- **Before this slice:** Slices 1–8 pass; no unresolved package license/native payload issue for tested artifact.

### Slice 10 — Final Phase 1 acceptance review

- **Objective:** verify every §12/§13 acceptance criterion, rights boundary, rollback and owner authorization record; present evidence for owner acceptance.
- **Planned projects/files:** acceptance report, Evidence Ledger updates, package hashes and final allowlist; no code changes unless returned to a prior slice under a new authorization.
- **Dependencies/contracts:** completed Slices 1–9; D1–D6; original Freeze; legal review only for any proposed distribution (distribution remains prohibited absent separate approval).
- **Tests:** regression matrix and Core clean artifact/process/auth/persistence/trust/backup/platform gates; retain exact skipped/unavailable platform conditions.
- **PASS:** seven §12 requirements and all §13 PASS criteria complete; no forbidden dependency/artifact; existing product regressions preserved; owner accepts Phase 1 closure separately.
- **Rollback:** if any gate fails, do not mark Phase 1 complete; return to the specific slice, preserve artifact/logs, and keep the last known-good Core package for local comparison.
- **Risks:** treating historical test results as current, ignoring rights, calling cross-publish native support, or confusing Phase 1 acceptance with release readiness.
- **Evidence:** source SHA, package hashes, manifests, full TRX files, rights register, per-RID notices and explicit owner acceptance.
- **Before this slice:** all previous slices pass; public distribution still blocked pending D6 release conditions and separate owner approval.

## 5. Regression evidence inventory at R3

| Evidence | State | Source/evidence limitation |
|---|---|---|
| Runtime Debug 219/219 | Historical evidence, TRX verified in R2 | `%TEMP%\hispania-r1-20261016\trx-temp-root-debug-with-reader\runtime-temp-root-debug-with-reader.trx`; R1 temporary tracked-source copy, not R3 HEAD. |
| Phase 19 lifecycle 76/76 and backup 2/2 | Historical evidence, TRX verified in R2 | R1 temporary copy TRX files; not rerun at R3. |
| Identity/Authorization/Events/SQLite 22/22 | Reported in the R3 prompt as earlier evidence; a matching TRX/log or exact command/source SHA was not found in the checked-in R2 ledger, workspace documents or available named R1 TRX files | Do not count as independently verified or current-HEAD evidence. Locate original report/TRX or rerun in an authorized task before relying on it. |
| Deployment/Hosting 35/35 | Historical result recorded in Evidence Ledger EV-009 | Focused `tests/deployment/Hispania.Deployment.Tests.csproj` result on candidate SHA recorded there; no R3 rerun and not current HEAD proof. |
| Runtime Release | NOT PASS | Prior recorded Release run was 214/219 with five configuration/prerequisite failures. |
| Nine Deployment Process cases | BLOCKED / NOT RUN | Evidence Ledger lists exact prerequisites; predecessor artifacts, disposable service runners and authenticated disposable PostgreSQL are not provided. |
| Native ARM64 / live PostgreSQL current Core / SCM / systemd / remote Worker / Data Gateway | NOT VALIDATED for R3 Core; later-phase or artifact-dependent | No Core artifact exists. Historical evidence does not become current-HEAD support. |

No test suite was run for R3 because documentation and owner-decision recording do not change production code and the prompt disallows implementation. Preserve every historical source SHA and limitation.
