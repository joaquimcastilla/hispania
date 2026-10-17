# HISPANIA v1.0 — Step 0.3-R4 Evidence and Slice 1 Inputs

**Audit HEAD:** `626bb45a4e5d4c3f3d2caa3dc5272a6144113d15`
**Audit date:** 2026-10-17
**Phase 1:** NOT AUTHORIZED. No implementation was started.

## Roadmap status

```text
HISPANIA v1.0 — ROADMAP STATUS
[████░░░░░░░░░░░░░░░░] 3/14 — 21,4 %
[✓] Phase 0 — Architecture Freeze
[✓] 0.1 — Baseline Reconciliation Audit
[✓] 0.2 — Baseline Consolidation
    [✓] Preservation — 49/49 SHA-256
    [✓] Owner Freeze integrated
    [✓] WIP Semantic Reconciliation — R1
    [✓] Gate I — Identity and Authorization
    [✓] Gate II — Platform Events and Persistence
    [✓] Gate III — Inventory Integration
    [✓] Gate IV — Platform-only and Hosting
    [✓] Gate V — Operational Evidence
    [✓] Owner Acceptance
[~] 0.3 — Phase 1 Readiness
    [✓] Initial Readiness Audit
    [✓] R1 — Dependency, Provenance and Runtime Audit
    [✓] R2 — Entry Gates, Rights Boundaries and Owner Decisions
    [✓] R3 — Owner Decisions and ten-slice plan
    [~] R4 — Evidence Recovery and Slice 1 Rights (BLOCKED — VALIDATION INCOMPLETE)
    [ ] Final Readiness Acceptance
[ ] 1   — Empty Core
[ ] 2   — Module System & SDK
[ ] 3   — Inventory Module
[ ] 4   — Coordinator & Distributed Workers
[ ] 5   — Direct Data Plane & Gateway
[ ] 6   — Media Module
[ ] 7   — Identity, Permissions & Clients
[ ] 8   — Administration & Recovery
[ ] 9   — Cross-platform Validation
[ ] 10  — Security & Release v1.0
```

Roadmap progress remains 3/14; Phase 1 has not started and remains NOT AUTHORIZED.

## A. Status

**R4: BLOCKED — VALIDATION INCOMPLETE.** Static identification of the relevant test code and candidate inputs is complete. Test discovery and execution were not performed: the owner-approved D1/D5 conditions require sufficient rights for the exact source, test, fixture, and package inputs, and the current register does not establish those rights. The candidate test project has a much broader compile graph than its test filter. Running discovery would compile that graph and would not avoid the rights/use question.

## B. Test selection and historical claim

The original Prompt 0.3 and the R3 plan do not give an exact command, filter, test-case list, source SHA, or TRX for the reported 22/22. The 22/22 total therefore remains an unverified historical claim (Evidence Ledger EV-032); it is not a discovered count and is not reproduced here.

Static source inspection at the audit HEAD found these candidate class files in the single `tests/inventory/Hispania.Inventory.Tests.csproj` test assembly:

| Area | Test source |
|---|---|
| Identity | `tests/inventory/PlatformIdentityTests.cs` — `PlatformIdentityTests` |
| Authorization | `tests/inventory/PlatformAuthorizationTests.cs` — `PlatformAuthorizationTests` |
| Events | `tests/inventory/DurablePlatformEventTests.cs` — `DurablePlatformEventTests` |
| Jobs | `tests/inventory/PlatformJobsTests.cs` — `PlatformJobsTests` |
| Platform SQLite | `tests/inventory/PlatformPersistenceTests.cs` — `PlatformPersistenceTests` |

**Exact test count:** NOT DISCOVERED. `dotnet test --list-tests` and filtered execution were not run. Source attributes alone are not reported as an exact xUnit case count.

The R3 Slice 1 plan also names Runtime Debug (historical 219/219), Phase 19 lifecycle (76/76), Phase 19 backup (2/2), and Deployment (35/35). These are distinct suites and historical evidence; they are not asserted to be equivalent to the missing 22/22.

## C. Execution evidence

| Field | Result |
|---|---|
| Authorized source HEAD | `626bb45a4e5d4c3f3d2caa3dc5272a6144113d15` |
| Host | Windows 11, x64 (`10.0.26200`, `win-x64`) |
| .NET SDK/runtime | SDK `10.0.401`; runtime `10.0.12` |
| Command/filter | NOT RUN — owner rights evidence prerequisite is unresolved |
| Discovered / passed / failed / skipped | NOT DISCOVERED / NOT RUN / NOT RUN / NOT RUN |
| TRX path/hash/duration | None; no run occurred |
| Reason/limitation | D1/D5 condition; test-project compilation includes a broad source and dependency graph with unverified contributor authority and third-party package terms. The compatibility Consumer07 binary is not required by these five selected classes, but no compatibility tests were invoked. |

The following command is a candidate only, not executed or validated. Run after rights coverage is recorded, with artifacts directed outside the checkout:

```powershell
$runRoot = Join-Path $env:TEMP ("hispania-r4-platform-" + [guid]::NewGuid().ToString("N"))
dotnet test tests/inventory/Hispania.Inventory.Tests.csproj -c Release --filter "FullyQualifiedName~PlatformIdentityTests|FullyQualifiedName~PlatformAuthorizationTests|FullyQualifiedName~DurablePlatformEventTests|FullyQualifiedName~PlatformJobsTests|FullyQualifiedName~PlatformPersistenceTests" --artifacts-path (Join-Path $runRoot "artifacts") --results-directory (Join-Path $runRoot "results") --logger "trx;LogFileName=platform-slice1.trx"
```

This filtered command still compiles the project references and all included test source. It is not an isolated five-file build.

## D. Slice 1 input inventory

The first slice in R3 is a **compatibility and regression baseline**, not a new Core implementation. The actual tests/build inputs cannot be reduced to only the five test classes because MSBuild compiles the full project and traverses its project references.

| Path/component | Slice 1 use | Provenance evidence | Rights state | Disposition |
|---|---|---|---|---|
| `tests/inventory/Hispania.Inventory.Tests.csproj` | Candidate assembly for five Platform test groups | Tracked project file; direct references inspected at audit HEAD | OWNER ATTESTATION REQUIRED | Do not restore/build/discover/run until covered. |
| Five Platform test source files in §B | Candidate filtered test cases | Tracked C# source; no per-file contributor/assignment record | OWNER ATTESTATION REQUIRED | Exact files and linked helpers need authority evidence. |
| Remaining `tests/inventory/*.cs` files | Compiled into the same test assembly even when filtered | Tracked test sources; project contains no `Compile Remove` for them | OWNER ATTESTATION REQUIRED | Include in build-input scope or use a rights-cleared narrower test project under a separately authorized task; do not edit tests now. |
| Direct references: `modules/extension-demo/Hispania.ExtensionDemo.csproj`; `contracts/extension-demo/Hispania.ExtensionDemo.Contracts.csproj`; `application/platform/jobs/Hispania.Platform.Jobs.Application.csproj`; `application/platform/storage/Hispania.Platform.Storage.Application.csproj`; `infrastructure/platform/storage-filesystem/Hispania.Platform.Storage.Filesystem.csproj`; `infrastructure/platform/worker-agent-jobs/Hispania.Platform.WorkerAgentJobs.csproj`; `apps/server/Hispania.Server.csproj` (build-only); `apps/worker-agent/Hispania.WorkerAgent.csproj` (build-only); `application/authorization/Hispania.Authorization.Application.csproj`; `application/identity/Hispania.Identity.Application.csproj`; `application/inventory/Hispania.Inventory.Application.csproj`; `infrastructure/persistence/inventory-sqlite/Hispania.Inventory.Persistence.Sqlite.csproj`; `infrastructure/platform/persistence-sqlite/Hispania.Platform.Persistence.Sqlite.csproj`; `core/runtime/Hispania.Runtime.csproj`; `core/domain/Hispania.Media.Domain.csproj`; `application/library/Hispania.Library.Application.csproj`; `application/metadata/Hispania.Metadata.Application.csproj`; `application/playback/Hispania.Playback.Application.csproj`; `infrastructure/transcoding-ffmpeg/Hispania.Transcoding.FFmpeg.csproj`; `infrastructure/persistence/Hispania.Library.Persistence.Sqlite.csproj`; `application/platform/observability/Hispania.Platform.Observability.Application.csproj`; `tests/closure-process-host/Hispania.Closure.ProcessHost.csproj`; `infrastructure/persistence/Hispania.Library.Persistence.PostgreSql.csproj`; `tests/backup-process-host/Hispania.Backup.ProcessHost.csproj`; `infrastructure/persistence/distributed-work/Hispania.DistributedWork.Persistence.Sqlite.csproj`; `tools/phase24/Hispania.ResilienceHarness.csproj` | Transitive build/reference inputs for candidate project | All are tracked projects/source; no exact per-project rights clearance in register | OWNER ATTESTATION REQUIRED | Graph includes Server, Media, PostgreSQL, WorkerAgent, Inventory, process hosts and resilience harness; excluded from Core by D3/D4, but not cleared for building this existing suite. |
| `Microsoft.AspNetCore.App` framework reference | Test/referenced project build/runtime | Installed shared framework; SDK reports .NET 10.0.12 | DOCUMENTED runtime identity; framework terms not reviewed here | Framework-dependent local test execution only; no redistribution. |
| `Microsoft.NET.Test.Sdk` 17.11.1; `xunit` 2.9.2; `xunit.runner.visualstudio` 2.8.2 | Test discovery/execution | Versions in test project; package inventory metadata only | THIRD-PARTY PERMISSION REQUIRED / REVIEW REQUIRED | Verify package terms/notices for intended local use; metadata is not legal review. |
| NuGet transitive graph, including packages from all references | Restore/build/test | Not resolved because discovery/build was not run | UNKNOWN | Exact packages, terms, and native/runtime assets remain undiscovered. |
| Test-created SQLite databases and temporary directories | Runtime test data | Test source creates synthetic per-test temp state | DOCUMENTED as intended synthetic behavior by source inspection | If run later, force temp roots outside repository and confirm cleanup. |
| Six D4 unknown-rights artifacts | Not needed by the five candidate class files as statically inspected; may be used by other suites | R3 inventory/hashes | EXCLUDE | Do not run compatibility suites or use these artifacts without separate rights evidence. |
| `tests/unit/Hispania.Runtime.Tests.csproj`, linked fixtures, Runtime/contracts/module fixture projects and schema JSON | R3 separately lists Runtime and Phase 19 suites | Project includes module fixtures, process-host projects, JSON fixtures, Runtime and several domain project refs; Phase 19 invokes an independent historical reader | OWNER ATTESTATION REQUIRED; historical reader/artifact EXCLUDE until rights established | Prior 219/219, 76/76 and 2/2 TRXs remain evidence at their recorded source, not current-HEAD validation. |
| `tests/deployment/Hispania.Deployment.Tests.csproj` and its transitive inputs | Historical Deployment 35/35 suite in R3 plan | EV-009 records earlier command/result | OWNER ATTESTATION REQUIRED before rerun | Keep historical claim separate; no R4 rerun. |

The inventory is sufficient to show that the candidate existing test assembly is broader than the proposed Core allowlist. No Core code is implemented in this step; D4 exclusions remain in force.

## E. Minimum rights evidence and owner action

The current `LICENSE.md` grants local, personal, non-commercial use only for material the named rights holder owns or is authorized to license. R3 D1 conditions internal development/test on sufficient existing rights; D5 forbids presuming ownership from Git identity/history. The register does not document authority for the source/test projects above. Package inventory metadata is not a substitute for package terms or an authority record.

To unblock test discovery and execution, the owner must provide one bounded statement covering whether they own or are otherwise authorized to use the exact HISPANIA project/source inputs listed in §D for private local non-commercial test/build activity at this HEAD, and identify known exceptions or third-party contributions. This does not need to claim ownership where uncertain; unknown inputs can instead be excluded. Separately, allow restore/use of the exact resolved NuGet packages under their applicable terms, or provide an offline cleared package set. No distribution rights are requested or inferred.

## F. Safe Slice 1 plan

### Allowlist after rights are evidenced

1. Reproduce the five Platform class groups listed in §B using an isolated, rights-cleared test harness/project. First prefer an existing narrow project if identified by owner evidence; do not edit the current suite in this R4 task.
2. Add Runtime, Phase 19, backup, or Deployment suites only as separately selected rows, each with exact source SHA, command, TRX and rights coverage. Do not equate them with the five Platform groups or with 22/22.
3. Use only synthetic test-created database contents; direct all output, intermediate files, TRX and logs to a unique external temp root.
4. Exclude the six unknown-rights artifacts, historical reader binaries, real user data, Media/Inventory assets and production service/database dependencies unless a future evidence review clears a necessary item.

### Test protections and acceptance

- Preserve `apps/server`, contracts, tests and existing database files byte-for-byte; never point a test at a non-temporary data path.
- Set `BaseOutputPath`, `BaseIntermediateOutputPath`, TRX/results, and test-owned state to the external temporary root; verify the repo remains clean after execution.
- Record source HEAD, OS, SDK, exact command/filter, discovery count, pass/fail/skip, TRX SHA-256 and duration. Report the five groups separately from Runtime/Phase19/Deployment historical suites.
- **PASS:** every selected applicable case passes, no relevant case is hidden by a filter, discovered count and TRX agree, and all inputs have rights evidence. Name and explain every failure or skip.
- **STOP:** missing rights coverage; unexpected source/project/package input; access to real services/data; writes outside temp; excluded artifact required; or a test failure indicating an existing regression.
- **Rollback:** no repository mutation is part of this baseline slice. Stop the process, preserve external logs/TRX, remove only that run's unique temporary outputs after recording evidence, and leave source/worktrees/backups untouched.
- **Dependencies to next slice:** owner rights evidence for exact inputs; reproducible baseline acceptance; then final Step 0.3 owner acceptance and separate express Phase 1 authorization.

## G. R4 conclusion

The compatibility/regression slice has a safe route, but its current broad test graph is not covered by the evidence presently recorded, and discovery/execution gates are incomplete. Readiness is therefore **BLOCKED — VALIDATION INCOMPLETE**, with the bounded owner evidence request in §E. This does not find that the source is improperly used; it means authority is not documented sufficiently under approved D1/D5. Phase 1 remains NOT AUTHORIZED.

## H. Rights and provenance summary

- HISPANIA source/test project authority for exact Slice 1 build inputs: OWNER ATTESTATION REQUIRED; not established by Git author fields or repository license alone.
- Historical compatibility binaries: EXCLUDE from Slice 1 unless separately cleared; no compatibility suite ran.
- NuGet test/runtime packages: THIRD-PARTY PERMISSION REQUIRED / REVIEW REQUIRED; exact graph and terms remain unresolved because no restore ran.
- Synthetic DB state: test source indicates isolated temporary databases; actual cleanup and paths remain unverified until an authorized run.
- No ownership, assignment, AI provenance, third-party permission, or distribution rights have been invented or inferred.

## I. Safe first-slice plan

The allowlist, exclusions, external artifact rules, PASS/STOP criteria, rollback boundary and dependencies are specified in §F. In summary: first obtain exact rights coverage, then discover and run only the selected class groups in a rights-cleared isolated harness, preserve the checkout, report every discovered case/skip/failure, and keep separate historical suites separate. Do not start a Core project as part of this slice.

## J. Readiness reassessment

**BLOCKED — VALIDATION INCOMPLETE.** The missing exact test evidence and the inability to execute safely under the current rights record are specific to the R4 gate. This does not claim a technical test failure or source misuse. It also does not satisfy Step 0.3 owner acceptance.

## K. Changes

Documentation only: this R4 report, Evidence Ledger EV-036/EV-037, Readiness Blockers R4 addendum, and the contributor rights register's bounded Slice 1 entries. No code, test, contract, license, asset, binary, worktree, or backup was changed. Git commit details are reported in the task completion summary.

## L. Integrity

Preflight: 15 original worktrees and 49 preserved files. Recheck at finalization; `git diff --check`, changed-path scope, license hash/status, and clean post-commit worktree are required. No push is planned.

## M. Final HEAD

Recorded after final documentation commit, if a documentation-only commit is created; otherwise the initial HEAD remains. Phase 1 remains NOT AUTHORIZED.

## N. Next action

**BLOCKED — VALIDATION INCOMPLETE.** Owner evidence for the exact local private-use source/test inputs (and any exceptions), plus package terms/cleared package graph, is required before test discovery and execution. After that evidence and test run, the owner must separately accept Step 0.3 and authorize Phase 1.
