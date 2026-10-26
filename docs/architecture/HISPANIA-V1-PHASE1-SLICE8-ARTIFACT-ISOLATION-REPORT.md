# HISPANIA v1.0 — Phase 1 Slice 8: Artifact Isolation

## Status

**DONE — OWNER ACCEPTED PASS for Slice 8, within the ratified Windows x64 scope and documented limitations.** Steps 0–5 PASS with historical limitations; Step 6 PASS WITH LIMITED SCOPE; Steps 7.1–7.5 PASS. Step 1 implements fail-closed storage-path and reparse-point checks for the Core data root, module staging, package store, recovery and deletion paths. Step 2 adds archive preflight validation and adversarial extraction evidence. Step 3 adds transaction recovery, cross-process serialization and concurrent-reader evidence; corrupt or incomplete persisted journals now fail closed with a controlled `InvalidDataException`. Step 4 verifies file ownership before recursive package deletion and records a deletion/retention matrix. An earlier direct-symlink attempt was SKIP because the token lacked the required privilege; five independent junction checks passed. PI-35 was later separately verified PASS in elevated PowerShell, with the elevated-run and sentinel-content limitations retained below. Slice 7 remains DONE / ACCEPTED BY OWNER; Phase 1 remains IN PROGRESS; global progress remains 4/14 — 28.6%. Step 6 is **PASS CON ALCANCE LIMITADO** by owner decision (2026-10-19). Slice 8 is **DONE / OWNER ACCEPTED PASS** on 2026-10-26, bounded to the ratified Windows x64 scope; Slice 9 remains NOT STARTED.

## Step 0 — Accepted artifact identity

Artifact: `%TEMP%\hispania-slice7-accepted-20261018-publish`<br>
Artifact ID: `hispania.core`<br>
Source commit: `591dc17db24f0afd3ad066c96b0b1db1494d0150` (exists locally and is an ancestor of the current HEAD)<br>
Target: `net10.0`, `win-x64`, framework-dependent<br>
Manifest generation time: `2026-10-18T11:40:03.4594152+00:00`<br>
Content ID: `44B43088658F2F70FE8A0745EA5DFC3B1519CBF10B9347F3F4B88835EEDB3B12`<br>
Manifest SHA-256: `22D05298F7329BE8C2892BDEE33EBE71C6851EBC5D7A6B6BBF2A49C34F45FE87`

The machine-readable baseline at [core-artifact-baseline-win-x64.json](evidence/slice8/core-artifact-baseline-win-x64.json) records all relative file paths, sizes and SHA-256 digests. The manifest lists 18 files; the complete on-disk inventory contains those 18 plus the manifest itself, for 19 files total. There were no additional, missing or modified files, and no reparse points were found.

| Identity | Relative path | SHA-256 |
|---|---|---|
| Core executable | `Hispania.Core.exe` | `C0AC7D9FD3CEE15473D2CBF084667469BC023597704C55550AB703DBE0905F43` |
| Core assembly | `Hispania.Core.dll` | `2B9AC69B5C4718F3F1590CEBC7AD927CFD8428EAE3B82545531B44693B13812E` |
| ModuleHost assembly | `module-host/Hispania.ModuleHost.dll` | `1A6BEFD8ADBA5BF4EE056DB21C368D148AFB5AFB7ADEC2360E564F24D32C5349` |

### Baseline evidence digests

- Inventory JSON SHA-256: `0473080A339C2E96AB7C66E1085ECFE468D8891039DEAF74DA90DC96013C68E5`
- Reusable checker SHA-256: `79F9F63E1C7C55D0C0FB6D2875573515A0D506EEE1FA398B5949C7ED526B03D1`
- Artifact validator: `passed=true`, `issues=[]`, policy `hispania-core-v1.0.0`, content ID agrees with the manifest and recorded Slice 7 identity.

## Reusable before/after comparison

[verify-slice8-artifact-baseline.ps1](../../tools/verify-slice8-artifact-baseline.ps1) performs read-only inventory and hash comparison in its default mode. It verifies the pinned manifest digest and identity, compares the full file set against the captured baseline, and reports `added`, `missing`, and `modified` paths separately. It fails closed on invalid/duplicate manifest paths and reparse points. It does not write to the artifact root.

Run the same command immediately before and after each lifecycle action, retaining each sanitized JSON output:

```powershell
$artifact = Join-Path $env:TEMP 'hispania-slice7-accepted-20261018-publish'
$baseline = 'docs/architecture/evidence/slice8/core-artifact-baseline-win-x64.json'
pwsh -NoProfile -File tools/verify-slice8-artifact-baseline.ps1 `
  -ArtifactRoot $artifact -BaselinePath $baseline
```

Capture a `PASS` result before the operation, perform exactly one operation against an isolated Core data directory, then repeat the command. Required operations are install, activate, Core restart, deactivate and remove. A passing pair must have unchanged content ID and manifest SHA-256 and empty `added`, `missing`, and `modified` arrays. Preserve both outputs; do not rebuild the accepted publish between comparisons.

The checker's `-SelfTest` option copied the accepted publish into a unique temporary directory and independently detected one added file, one deleted file and one modified file. It removed only its own uniquely named temporary directory and verified that the directory was absent afterward. No adversarial package was constructed or installed.

## Validation and limits

- Manifest/inventory agreement: **PASS** (18 listed, 19 total including manifest).
- Artifact validator: **PASS**, no issues.
- Baseline compare against accepted publish: **PASS**, no additions, deletions or modifications.
- Checker parser: **PASS** in PowerShell 7.
- Checker self-test: **PASS** for added, deleted and altered file detection; temporary self-test directory absent after completion.
- `git diff --check`: **PASS**.
- Accepted publish was not rebuilt or modified. No production or real Core lifecycle operations were performed in this step.

The inventory is a baseline for artifact drift, not an operating-system security boundary. The verifier does not prevent writes, remove ACL rights, or protect against a hostile process racing filesystem checks. Slice 8 must not describe hash comparison as runtime immutability or as a sandbox.

## Repository state

Branch: `codex/hispania-v1-baseline-consolidation`<br>
Starting and ending HEAD: `ef7365bb5e7d7551cb1093519743cc1885757b5b`<br>
Step 1 resolution worktree: `.`. Initial and final HEAD: `ef7365bb5e7d7551cb1093519743cc1885757b5b`; branch: `codex/hispania-v1-baseline-consolidation`. The worktree is **not clean**: it contains the storage-boundary implementation and tests, two publisher-trust test expectation updates, this Slice 8 report, and the EV-050 supplement, alongside pre-existing pending documentation, Slice 7 evidence, and smoke/baseline scripts. All pre-existing pending files and failed/retry test outputs were preserved. No merge, rebase, branch change, commit, push, tag, release or Slice 9 work was performed. The accepted publish and frozen contracts were not modified.

## Recommendation

Step 0 authorized Step 1 only. Step 1 is validated at its bounded Windows x64 scope. Step 2 requires its own authorization. Keep permission isolation and sandboxing out of this claim; a separate OS security boundary would require a distinct design and approval.

## Step 1 — Storage Boundary (2026-10-18)

### Implementation

Added `StoragePathGuard` in the shared runtime. It uses full path normalization plus filesystem attribute checks on every existing component. Any reparse point on a checked path fails closed; Windows path comparisons are case-insensitive. Root containment is checked at component boundaries, so similarly prefixed sibling paths do not count as descendants. Managed recursive deletion first enumerates the target tree without traversing links and refuses deletion if any reparse point is present. Core configuration rejects DATA_DIRECTORY that overlaps the application publish in either direction, and Core rechecks the boundary before initializing storage. Upload staging, package staging, package state, installed package paths, recovery and retention cleanup use the guard.

This policy is intentionally conservative: legitimate symlink/junction-based storage paths are rejected. It does not resolve a link to bless its destination; it rejects links in the route. The package file format, publisher trust, permission grants and ModuleHost isolation contracts were not changed.

### Step 1 acceptance matrix

| # | Acceptance case | Result | Evidence / limitation |
|---|---|---|---|
| 1 | Valid external DATA_DIRECTORY | PASS | `StorageConfigurationAllowsExternalRootAndSimilarPrefixDirectory` |
| 2 | DATA_DIRECTORY directly in Core publish | PASS | Existing `StorageConfigurationRequiresAbsolutePathOutsidePublishDirectory` |
| 3 | DATA_DIRECTORY via external junction; DATA_DIRECTORY redirected toward Core publish | PASS | `StoragePathGuardRejectsDataDirectoryJunctionBeforeBootstrapWrites`; `DataDirectoryJunctionRedirectingToCorePublishIsRejectedWithoutChangingPublishFiles` |
| 4 | Redirected staging ancestor | PASS | `CoreModuleManagerRejectsJunctionAtStagingBeforeReadingOrWriting`; package stream stays unread and external tree stays unchanged |
| 5 | Package store junction to outside | PASS | `ModuleStoreJunctionIsRejectedBeforeDiscoveryOrInstallationWrites`; both operations reject before writing |
| 6 | Similar-prefix sibling directories | PASS | Exact root-boundary containment and external-root test |
| 7 | Windows path case differences | PASS | Case-variant path inside publish is rejected |
| 8 | Relative and absolute paths | PASS | Existing relative-path rejection plus valid absolute external path |
| 9 | Cleanup with links and unrelated files | PASS | `RecursivePackageRemovalRejectsJunctionAndPreservesBothTrees`; uninstall rejects the package tree before recursive deletion |
| 10 | Fail closed when route cannot be verified | PASS | A file used as a path ancestor is rejected; inaccessible OS ACL scenario is not simulated |

### Validation results and blocker resolution (2026-10-18)

- Junction tests: **5 PASS, 0 FAIL, 0 SKIP**. The test helper invokes PowerShell `New-Item -ItemType Junction` in test-owned temporary directories. It removes only each test junction with `Remove-Item -LiteralPath`; before/after snapshots of every file in its external target must match. The cases cover external DATA_DIRECTORY (no SQLite file created), DATA_DIRECTORY redirected to the accepted Core publish (all file hashes unchanged), a `module-staging` junction (upload stream remains unread), a junction at the module-store root (Discover and Install reject before writes), and a nested junction in an installed package (uninstall rejects before recursive deletion; package and external markers remain intact). The helper's PowerShell version was 7.6.5.
- Symlink test: **0 PASS, 0 FAIL, 1 SKIP**. Existing `Directory.CreateSymbolicLink` test remains intact and skipped. The owner reported under PowerShell 7.6.7 that creation failed for lack of the required privilege; the literal exception text and native error code were not provided. The skip message records this reported cause and explicitly distinguishes the separate junction tests. No Windows setting or permission was changed.
- `dotnet test tests/core/Hispania.Core.Tests.csproj --no-restore`: **58 passed, 1 skipped, 0 failed** (59 total). The sole skip is the direct symlink API test; the five junction cases pass.
- `dotnet test tests/core/Hispania.Core.Tests.csproj --no-restore --filter "FullyQualifiedName~CorePersistenceTests.StorageConfiguration|FullyQualifiedName~CorePersistenceTests.StoragePathGuard"`: **4 passed, 1 skipped, 0 failed**. The one skip is the direct symlink API test.
- `dotnet test tests/core/Hispania.Core.Tests.csproj --no-restore --filter FullyQualifiedName~Slice7ModuleLifecycleTests`: **4/4 passed**.
- Final package regression after the junction tests: `pwsh -NoProfile -File tools/test-phase19-package-lifecycle.ps1 .artifacts/phase19-gate-step1-junctions` (script inspected before execution; positional parameter `ResultsDirectory`): **PackageLifecycleIntegrationTests 76/76 PASS; Phase19PackageBackupTests 2/2 PASS**. The script prepares the pinned historical reader and supplies `HISPANIA_PHASE19_OLD_READER`; the historical Runtime test is not a standalone test without this explicit prerequisite. Results are retained at `.artifacts/phase19-gate-step1-junctions/phase19-package-lifecycle.trx` (SHA-256 `9FFAF72C8FFD9C66BBADCC95BBFD9B7A784923C0468D51AC6D9CFA5B8D269E4C`) and `.artifacts/phase19-gate-step1-junctions/phase19-package-backup.trx` (SHA-256 `B29F98BC656AD925365C20782970224E01B482471E0174F1820B6C4D90015616`). The script only created its new output root and build/test outputs; earlier failed/retry output roots were preserved.
- The first inspected historical-reader run exposed a Step 1 regression: `PlanPackageSetAsync` created the package directory during a planning-only operation, breaking two assertions that planning must not mutate storage. The minimum fix validates existing paths/tree without creating the directory. The fresh final lifecycle run then passed all 76 integration tests and both backup tests. No package trust code or policy was changed.
- Two Runtime tests had stale expectations. `ModuleHostPlatformJobContributionTests.InvalidSignatureAndMissingAllowlistAreRejected` failed with `InvalidOperationException: Package publisher signature is invalid.` from `ModuleExtensionCatalog.ValidatePublisherTrust` → `ExtensionPackageInstaller.InstallAsync` → `ModuleExtensionCatalog.InstallAsync`, before its old assertion that the invalid package could be installed and activation later rejected. `ModuleInfrastructureTests.OutOfProcessGreetingRunsThroughPublicContractAndRecoversAfterWorkerCrash` failed with `InvalidOperationException: Publisher 'hispania.unknown.editor' is not trusted by host policy.` at the same install trust boundary; its old flow expected installation before update. Both contradict fail-closed publisher verification at install. The tests now assert install rejection; the separate valid-signature/missing-allowlist activation rejection and retained trusted generation checks remain. Targeted rerun: **2/2 PASS**.
- Complete `dotnet test tests/unit/Hispania.Runtime.Tests.csproj --no-restore`, with `HISPANIA_PHASE19_OLD_READER` set only for the test process to the prepared pinned reader: **219 passed, 0 skipped, 0 failed**. The environment variable was restored/removed in a `finally` block.
- An initial Core `--no-restore` attempt failed before test execution with `NETSDK1047` because the historical-reader test preparation left `apps/module-host/obj/project.assets.json` without the `net10.0/win-x64` target. `dotnet restore tests/core/Hispania.Core.Tests.csproj -r win-x64` restored the expected target; the subsequent Core run above passed. This was build-assets state, not a source/test failure.
- Junction creation/removal was exercised by the five tests above. Only the direct symbolic-link API variant remains skipped, with the owner's privilege diagnosis; no junction or symlink diagnostic directory outside the tests' unique temporary roots was touched.
- Core and runtime projects built as part of these test runs. No Slice 8 publish was created. The accepted artifact was checked before and after the work with the Step 0 comparer; its content ID, manifest digest and complete inventory remained unchanged (see below).
- Accepted artifact validator: **PASS**, `issues=[]`, policy `hispania-core-v1.0.0`; content ID `44B43088658F2F70FE8A0745EA5DFC3B1519CBF10B9347F3F4B88835EEDB3B12`.
- Step 0 read-only comparer after the tests: **PASS**, content ID and manifest SHA-256 unchanged; `added=[]`, `missing=[]`, `modified=[]`.
- `git diff --check`: **PASS** (only line-ending normalization warnings were emitted).

### Step 1 source digests

SHA-256 values for the implemented boundary and its tests:

| File | SHA-256 |
|---|---|
| `core/runtime/StoragePathGuard.cs` | `FC536C9C32B9B0996E6DC6F89D5465E7FCF9B5D8871FF258DDB7A1E6828D53CF` |
| `apps/core/Configuration/CoreStorageOptions.cs` | `29CBB67C71AF90647797A13B121722F1FF28E032F67BB24875DECBBB337DC336` |
| `apps/core/Modules/CoreModuleManager.cs` | `7FD40714F0563EE9C38414B6014A2E244E9171FA47A7C82FE94258CF39DF0CC6` |
| `apps/core/Persistence/SqliteCorePersistence.cs` | `818E0B4C6F73CC6A8AB449F174FB7EE96207FFFC0E2DC19CB05A7A5471D7E378` |
| `core/runtime/ExtensionPackageInstaller.cs` | `36C32771C7B1C0554B5AE49E71C12C31A6241D555E7013957B517724FC86531A` |
| `core/runtime/ModuleExtensionCatalog.cs` | `05EFABDB0263CF2498D766F2110F64144E52D6B268065B0E37B18149A4CEBA68` |
| `core/runtime/PackageSetLifecycle.cs` | `3F207F403DF48B3F1DD366B734D0C18829DCC31A9FEC724F042C1A269AEF9CE6` |
| `core/runtime/PackageRetentionOwnerV1.cs` | `99BA1A13E2725F228A2E749B4CD1C81E0152357A2CD00C8F3DAB9BFFC8BEC6D7` |
| `tests/core/CorePersistenceTests.cs` | `BCEFB8EC82EC14AC4A6F9859D497FD50D64B1022494199844D5E5578E865206C` |
| `tests/unit/ModuleHostPlatformJobContributionTests.cs` | `42D2936E1322B81010F2F7552BB5C819D253EC13F4FC56BFBC9446EF9977F43C` |
| `tests/unit/ModuleInfrastructureTests.cs` | `B540AD5A9D6C7A6C0B72ABA6EA0CC638C0D1F5AD516FA481B2CAFC00839CF4B2` |

### Artifact integrity after Step 1

The read-only baseline comparison after code/tests returned `passed=true`, content ID `44B43088658F2F70FE8A0745EA5DFC3B1519CBF10B9347F3F4B88835EEDB3B12`, manifest SHA-256 `22D05298F7329BE8C2892BDEE33EBE71C6851EBC5D7A6B6BBF2A49C34F45FE87`, and empty `added`, `missing`, and `modified` arrays. The publish was not rebuilt, opened for writes, or modified.

### Security limits and decision

The checks detect existing reparse points and reject ambiguous/unverifiable paths; they do not eliminate TOCTOU races if another process replaces a checked component between validation and file operation. Junction reparse behavior is dynamically verified for the required data, staging, module-store, publish-redirection and recursive-removal cases. The direct symbolic-link API variant remains unverified. No handle-relative/native Windows file APIs, ACL changes, identity changes, or sandboxing were introduced. ModuleHost remains an operational fault-isolation boundary, not a security sandbox.

**Step 1 status: PASS.** All required storage-boundary behaviors passed using real junction reparse points, all three Runtime failures are resolved, and Core, fixture lifecycle, package lifecycle and artifact integrity checks pass. The separate symbolic-link API test remains SKIP because the owner-reported token lacks the required privilege; this does not leave a critical storage path untested because the same production guard was exercised at each required location using real junctions. The exact symbolic-link exception/HResult was not supplied. No commit, push, publish, Slice 9 or Step 2 work was performed. Starting uncommitted files and earlier test outputs were preserved.

## Step 2 — Adversarial extraction (2026-10-18)

### Scope and diagnosis

Reviewed `PackageArchiveInspector`, `ExtensionPackageInstaller`, `StoragePathGuard`, `PackageSetLifecycle`, existing ZIP validation, signature, publisher-trust, fixture and package lifecycle tests. Publisher verification remains fail-closed and separate from permissions and execution. The installer copies a bounded compressed stream, validates metadata before staging, extracts with `FileMode.CreateNew` into a unique staging directory, hashes actual bytes, checks the managed entry assembly, and only publishes with `Directory.Move` after every file succeeds. Its failure path checks that staging has no reparse points before recursive cleanup. `StoragePathGuard` rejects existing route/tree reparse points and blocks paths outside the managed root; it does not close concurrent path replacement (TOCTOU) races.

Two preflight gaps were demonstrated in the existing code:

1. Both readers skipped ZIP names ending in `/` before validating the name or checking link attributes. A crafted directory entry could therefore contain traversal/ambiguous Windows names or carry symlink attributes without being rejected at preflight. Such directory entries were not themselves extracted, but their acceptance violated the archive validation boundary.
2. Distinct file entries such as `node` and `node/child.bin` were not recognized as a file/directory topology collision until extraction attempted to create the child directory. Staging prevented publication and cleanup was safe, but the entire archive was not rejected before extraction began. The inspector and installer also had separate path rules, allowing their preflight behavior to drift.

### Minimal correction

`PackageArchiveInspector.ValidateArchiveEntries` is now the shared preflight for both inspection and installation. It validates every entry including explicit directories; rejects duplicate names with Windows case-insensitive comparison, file/directory topology collisions, symbolic-link attributes on files or directories, unsafe Windows names and path segments; and enforces the existing 512-entry and 256 MiB expanded-size bounds. The installer retains its 128 MiB streaming compressed-size bound and runtime actual-byte bound. No signed package format, manifest/signature contract, publisher policy, authorization, public endpoint, or Core behavior changed. Extraction still writes to private staging, uses create-new semantics, verifies hashes and assembly format, then atomically moves the completed directory into the package store.

### Adversarial matrix

Full case list and prior/new coverage are recorded in [EV-050 Step 2 adversarial matrix](evidence/slice8/step2-adversarial-extraction-matrix.md). The new controlled matrix has **22/22 PASS** against both the inspector and installer where applicable. It includes traversal and absolute paths, slash/backslash combinations, reserved Windows names including superscript device suffixes, exact and case-insensitive duplicates, file/directory collisions, unsafe and symlink directory entries, file symlinks, 513 entries, forged expanded lengths above the limit without creating a large file, inconsistent declared length, truncated/corrupt ZIP, invalid/inconsistent manifests, external canary hash preservation, and a payload hash failure after extraction begins. The latter left no staging or new package directory and preserved the hash snapshot of a previously installed package. The matrix uses small synthetic archives; it does not create a 128 MiB compressed fixture or inflate a 256 MiB payload.

Existing `Phase21SecurityHardeningTests` already cover basic traversal, case-collision and random central-directory input; `PackageLifecycleIntegrationTests` cover forged manifest lengths and package-set integrity; `Slice7ModuleLifecycleTests` cover tampered payload, corrupt archive, incompatible host and invalid signature. New cases extend those checks across both inspector and installer and add directory-entry and topology validation. Existing publisher-trust tests remain in force and pass.

### Regression and artifact evidence

Final result files are under `.artifacts/slice8-step2-final-20261018/` (adversarial matrix, StoragePathGuard, full Core, Slice 7 fixture, publisher trust, and full Runtime); the inspected package lifecycle preparation script wrote its separate results under `.artifacts/slice8-step2-retry-20261018/`. Exact counts and SHA-256 digests are in EV-050 and the matrix evidence file. Results: adversarial 22/22; StoragePathGuard-focused 6 PASS / 1 SKIP; Core 58 PASS / 1 SKIP; Slice 7 fixture 4/4; publisher trust 1/1; full Runtime 241/241; package lifecycle 76/76 and package backup/restore 2/2. The single SKIP is the existing direct symlink-creation test; Step 1's five real junction checks remain the verified Windows reparse-point evidence. No test failed.

The accepted `net10.0/win-x64` publish was checked before and after the work with `tools/verify-slice8-artifact-baseline.ps1`; the after comparison has `added=[]`, `missing=[]`, `modified=[]`, `selfTest=[]`, content ID `44B43088658F2F70FE8A0745EA5DFC3B1519CBF10B9347F3F4B88835EEDB3B12`, and manifest SHA-256 `22D05298F7329BE8C2892BDEE33EBE71C6851EBC5D7A6B6BBF2A49C34F45FE87`. The artifact validator passed with `issues=[]`. No accepted publish file was written or rebuilt.

### Limits and status

Windows x64 is the only executed platform. The symlink-specific test remains skipped because the current token lacks the privilege for `Directory.CreateSymbolicLink`; actual junction-based storage-boundary tests pass. The 128 MiB compressed-package upper boundary was reviewed in the streaming code but not stress-tested to the threshold; a full-size memory-consuming fixture was intentionally not created. The absolute expanded-size rejection was dynamically tested using patched ZIP metadata, not a multi-hundred-megabyte decompression. These are test-size limits, not known bypasses. Concurrent filesystem replacement remains outside the guard's guarantee.

**Step 2 status at its close: PASS for bounded Windows x64 adversarial extraction validation.** Slice 8 remains IN PROGRESS. Branch: `codex/hispania-v1-baseline-consolidation`; initial and final HEAD for the earlier Step 2 work: `ef7365bb5e7d7551cb1093519743cc1885757b5b`. No commit, push, merge, rebase, publish, or cleanup of prior evidence was performed. The worktree was already dirty before the current step; all pre-existing changes and temporary evidence were preserved.

## Step 3 — Transactions and concurrency

### Existing transaction model and diagnosis

`PackageSetLifecycle` already uses a per-catalog semaphore and an exclusive `mutation.lock` file for cross-process mutations. Plans are prepared against a current set; apply acquires both gates, verifies that the planned current graph is still authoritative, writes a journal with previous/next states, stages and verifies candidate packages, publishes the catalog through a flushed sidecar plus replacement move, then activates and health-checks the new graph. On operation failure it restores the prior graph. Startup recovery serializes on the same lock, rolls incomplete operations back, and starts the committed set when the journal is complete. Readers use the catalog/journal state to report `RecoveryRequired` during an incomplete publication rather than presenting it as a normal healthy graph.

Inspection found a malformed persisted journal could deserialize with a missing `previous`/`next` graph or null package entries. Those cases were not explicitly validated and could surface as an incidental null-reference failure instead of a controlled fail-closed data error. `ValidateJournal` and `ValidateStoredGraph` now reject those invalid shapes with `InvalidDataException`; no public contract or persisted format changed. The associated test verifies rejected recovery and mutation leave the authoritative catalog bytes unchanged.

### Transaction and concurrency matrix

The complete case list and expected outcomes are in [Step 3 transaction/concurrency matrix](evidence/slice8/step3-transactions-concurrency.md). The dedicated TRX has **18/18 PASS**: eight real process termination points (`Planned`, `Staged`, `Draining`, `Committed`, `Activating`, `Validated`, `RollingBack`, `Completed`); six multi-process/concurrent-state cases; three corrupt/incomplete journal shapes; and one locked staging cleanup/recovery case. At every interrupted boundary, recovery is run again and owned staging cleanup is verified. The `Completed` boundary keeps the new committed version; earlier incomplete boundaries restore the previous version.

The full package lifecycle suite adds six journal checkpoint failure cases and three storage-write failures (`stage-write`, `catalog-write`, `journal-write`), as well as stale-plan and changed-staged-bytes rejection. It passed **89/89**; the independent installation backup/restore suite passed **2/2**. The concurrency probes use two test-owned processes and synchronize plans against the same catalog generation before release. Same-module competing installs, different-module installs, install/removal, update/activation, atomic catalog observation and lock-waiting recovery all passed.

### Regression and artifact evidence

Current Step 3 results are retained in `.artifacts/slice8-step3-20261018/`: transaction/concurrency **18/18**, package lifecycle **89/89**, backup/restore **2/2**, Core **58 PASS / 1 SKIP**, and Runtime **254/254**. The Core skip is the existing direct symbolic-link creation case; junction boundary coverage from Step 1 remains **5/5 PASS**. One initial full Runtime attempt had a single Windows memory-probe exit-code mismatch; the memory/CPU probe then passed in isolation (2/2), and the complete Runtime rerun passed 254/254. No persistent runtime failure remains.

The accepted artifact was not rebuilt or written. The after-comparison is retained at [artifact-after.json](evidence/slice8/step3/artifact-after.json): `added=[]`, `missing=[]`, `modified=[]`, content ID `44B43088658F2F70FE8A0745EA5DFC3B1519CBF10B9347F3F4B88835EEDB3B12`, manifest SHA-256 `22D05298F7329BE8C2892BDEE33EBE71C6851EBC5D7A6B6BBF2A49C34F45FE87`. The comparator self-test is retained at [artifact-comparator-selftest.json](evidence/slice8/step3/artifact-comparator-selftest.json) and detects added, deleted and modified files while cleaning only its own uniquely named temporary copy. The allowlist validator returned `passed=true`, `issues=[]` with policy `hispania-core-v1.0.0`.

### Limits and status

This verifies application-level injected failures and process termination at durable journal checkpoints on Windows x64. It does not simulate sudden power loss or termination at every instruction during sidecar serialization/replacement, and it does not claim filesystem-independent durability semantics. Existing limits remain: no TOCTOU guarantee against a concurrent external filesystem replacement; the direct symlink test remains skipped for the current token; no Linux or ARM64 execution. No change was made to SQLite persistence schema, transaction contract, accepted Core artifact, publisher trust, package signature format or public API. Failure injection checkpoints are test-only hooks, and changes are confined to package lifecycle recovery validation/tests plus evidence documentation.

**Step 3 status: PASS for bounded Windows x64 transaction and concurrency validation. Slice 8 remains IN PROGRESS; Steps 0–3 only. Step 4 and Slice 9 have not started.** Branch: `codex/hispania-v1-baseline-consolidation`; HEAD remains `ef7365bb5e7d7551cb1093519743cc1885757b5b`. `git diff --check` passed. The worktree remains dirty from earlier Slice 8 work and this task; no commit, push, merge, rebase, publication, or unrelated cleanup was performed.

## Step 4 — Safe deletion and ownership (2026-10-18)

### Ownership model and correction

Package retention and `CleanupPackageHistoryAsync` already protected current/previous known-good graphs, checked active generations, validated content-addressed archive hashes, required staging markers, and rejected reparse points before recursive deletion. Direct uninstall already rejected active/registered packages and dependency use. The missing check was file-level ownership inside a package version directory: the manifest payload hashes were verified, but an unlisted file could previously be removed by recursive deletion.

The minimal correction adds an exact inventory check before package-directory deletion: only `module.json` and the manifest-listed payload files are accepted, using case-insensitive comparison on Windows. Uninstall and retention/cleanup validate the stored manifest and payload hashes; retention and cleanup also require the installed manifest to match the inspected retained artifact. Unknown files or an unprovable package manifest now stop deletion with `InvalidDataException`. Marker absence/corruption/path-like content does not grant cleanup ownership. No package format, trust, catalog, journal, permission, or public contract changed.

Detailed 18-case ownership/deletion matrix: [EV-050 Step 4 safe deletion matrix](evidence/slice8/step4-safe-deletion-matrix.md). It maps valid inactive uninstall, active/unload constraints, shared-store isolation, current/previous retention, foreign-file canaries, absent/corrupt/manipulated markers, reparse-point refusal and external junction target preservation, path-prefix rejection, locked/interrupted cleanup, retry/idempotency, missing packages and tampered package data to named tests. The new checks for unknown files in an uninstall target and in a retention candidate passed while preserving SHA-256 canaries and package directories.

### Regression and artifact evidence

The focused ownership matrix passed **6/6**, TRX `.artifacts/slice8-step4-20261018/deletion-matrix.trx`, SHA-256 `98F20C29228FA102313E680D6417002C7AE16A3C67DD54108D3A176805B41A98`. Package lifecycle plus historical-reader regression passed **94/94**, TRX `.artifacts/slice8-step4-rerun-20261018/phase19-package-lifecycle.trx`, SHA-256 `C805E338F08067CF224545DF3E7142E38B63DBA11DFF67C1D434A36BD6416312`; package backup/restore passed **2/2**, SHA-256 `AC912F487FC689954C8934E7F4C7227FE1A4C599D9FB52D9C2E64184576068CB`. Core passed **59**, with **1 SKIP** and no failures, TRX SHA-256 `4E33657632864CD4C28E9B1C0DEAF75AEB1FBF1EF08A70B3682D3879BF7ED0FC`; the skip is direct symlink creation under the current token, while five junction checks remain PASS from Step 1. The full Runtime suite passed **259/259**, SHA-256 `94D445CD63B5EF33C8C5DF2B6F3F732DCF90D39E06B5EA5E5146212DFF392D9D`. Slice 7 fixture lifecycle passed **4/4**, SHA-256 `1CEE5FBC6117992C00BF63C8CA2C0978314800C21610D62DB25B46AEA0900977`. Exact paths and outcomes are listed in EV-050 and the Step 4 matrix evidence.

The accepted Core publish remains at content ID `44B43088658F2F70FE8A0745EA5DFC3B1519CBF10B9347F3F4B88835EEDB3B12`, manifest SHA-256 `22D05298F7329BE8C2892BDEE33EBE71C6851EBC5D7A6B6BBF2A49C34F45FE87`; the before/after comparison reports no added, missing, or modified files, and the allowlist validator reports `passed=true`, `issues=[]`. The publish was not rebuilt or modified.

### Limits and status

Scope is Windows x64. The direct symlink API remains SKIP for the recorded privilege limitation; junction coverage and target canary checks pass. The path checks do not claim race-free handle-based protection against concurrent replacement (TOCTOU). Power-loss, Linux and ARM64 behavior remain unverified. Existing uncommitted work from prior steps was preserved; no commit, push, merge, rebase, or unrelated cleanup was performed. Slice 8 remains IN PROGRESS; Step 6 is **PASS CON ALCANCE LIMITADO** by owner decision (2026-10-19). Slice 8 remains **IN PROGRESS**; Step 7 and Slice 9 are not started.

**Step 4 status: PASS at bounded Windows x64 scope.** Slice 8 remains IN PROGRESS; Step 5 and Slice 9 have not started. Branch is `codex/hispania-v1-baseline-consolidation`; HEAD remains `ef7365bb5e7d7551cb1093519743cc1885757b5b`. No commit or push was made.

## Step 5 — Module loading and execution (2026-10-18)

### Runtime selection policy — pre-decision baseline

The package contract and runtime catalog currently support `dotnet-inprocess-trusted` and `dotnet-outofprocess`. `PackageSetLifecycle.ValidateArchiveHost` and `ModuleExtensionCatalog.Validate` accept both; `CreateLoadedModule` dispatches to the collectible `ExtensionAssemblyLoadContext` for the former and `OutOfProcessModule` for the latter. The Core module-manager facade validates the trusted publisher, fixture identity, package integrity and compatibility but has no Core-specific runtime allowlist. A trusted signed fixture package can therefore select either runtime through Core's current catalog path. No runtime-selection policy is enforced before activation beyond signature/publisher trust, allowlisted module identity, manifest validation and the supported-runtime values.

This behavior predates Step 5 and is consistent with the manifest's explicit execution-mode field and note that existing packages remain in-process. At the time of this baseline, no restriction had been approved. In-process native code runs with Core process privileges and can read host-accessible resources, stall or terminate Core, and retain process state. An `AssemblyLoadContext` is an assembly-resolution and unload mechanism, not a security boundary. OOP adds process supervision, crash containment and operating-system resource controls; it is not a sandbox and does not isolate code from resources the operating system otherwise grants it.

### Loading, dependencies and failures

Added test-only signed package fixtures whose two builds reference the same private assembly simple name at versions 1.0.0.0 and 2.0.0.0. The new matrix installs and activates both modules under both runtimes. In-process tests observe distinct collectible ALCs, package-local resolution, no private dependency in the Core default ALC, and correct version-specific behavior. OOP tests observe each ModuleHost process returning its package-specific version result while the Core/test process default context remains free of that dependency. Missing and actually API-incompatible dependencies fail startup in both modes, leaving the module unregistered and unavailable. The v2 fixture deliberately changes a method signature because version inequality alone can be compatible under .NET assembly binding.

Unload tests show that in-process catalog deactivation waits for active leases; a separately retained ALC causes `UnloadAsync` to return `false`, and after releasing that reference the ALC is collectible. OOP unload stops its ModuleHost process. Existing coverage also verifies signed module installation/execution/drain, startup failure, OOP worker exit/degraded state/explicit reactivation recovery, Core health after a fixture failure, persisted module state across Core restart and Windows Job Object resource enforcement. The OOP limits bound memory, CPU time/rate, process count, concurrent requests and request duration as implemented; these are operational limits, not a malicious-code boundary.

### Baseline regression and evidence before owner policy resolution

The initial Step 5 matrix and scope limits are retained in [EV-050 Step 5 evidence](evidence/slice8/step5/module-loading-execution-matrix.md). At that pre-decision baseline the results were: dependency/runtime matrix **6/6 PASS**, ModuleHost **9/9 PASS**, Core **59 PASS / 1 SKIP / 0 FAIL**, Slice 7 fixture lifecycle **4/4 PASS**, package lifecycle **94/94 PASS**, package backup/restore **2/2 PASS**, and full Runtime **265/265 PASS**. Updated owner-policy regression counts appear in the resolution section below. The Core skip is the known direct symlink privilege case; junction coverage from Step 1 remains separate and passing. A prior full Runtime attempt recorded one intermittent OOP timeout-versus-process-exit assertion failure (261/262); it passed isolated reruns and subsequent complete runs. That run is retained as a test/supervision timing risk.

The accepted Windows x64 publish passed the baseline comparator before and after: no added, missing or modified files, content ID `44B43088658F2F70FE8A0745EA5DFC3B1519CBF10B9347F3F4B88835EEDB3B12`, manifest SHA-256 `22D05298F7329BE8C2892BDEE33EBE71C6851EBC5D7A6B6BBF2A49C34F45FE87`. The allowlist validator passed with `issues=[]`. No ModuleHost process remained after validation. `git diff --check` passed. Eighteen synthetic test scratch directories from earlier test authoring attempts remain under `%TEMP%`; a cleanup operation against only those inspected roots was blocked before execution by tool policy. The final test helper successfully cleaned its own new roots. No accepted artifact, contracts, trust policy, permissions, package format, or production Core code was changed.

### Owner policy resolution and final validation (2026-10-18)

The owner approved `dotnet-outofprocess` as the only runtime allowed for installable modules managed by Core. This does not constrain internal HISPANIA components or in-process implementations outside the managed extension system. The manifest retains both existing runtime values and the signed package format is unchanged.

Core configures the module catalog with the OOP-only runtime policy. Shared validation rejects `dotnet-inprocess-trusted` before package staging, transaction-state writes, direct activation, restart recovery, and assembly loading. Recovery checks all enabled entries before activating any package, so a legacy persisted in-process package remains inactive and Core continues running. The HTTP installation and enable routes return the controlled error code `module_runtime_not_allowed`. Generic runtime catalog tests continue to exercise the in-process ALC for technical compatibility; no ALC or internal Core loading behavior was changed.

New tests pass for signed in-process rejection before installation, direct Core-catalog activation rejection for a legacy package, and restart recovery refusing an enabled persisted in-process package. Full Core is **62 PASS / 1 SKIP / 0 FAIL**; focused policy tests **3/3**; ModuleHost **10/10**; Package Lifecycle **94/94**; package backup/restore **2/2**; full Runtime **265/265**. The Core SKIP is the documented symbolic-link privilege limitation; Step 1's five junction checks pass. A previously observed OOP timeout-versus-worker-exit test flake passed isolated reruns and subsequent full suites, including the current full Runtime run. It remains a test scheduling/supervision risk. Exact TRX SHA-256 values are in the updated [EV-050 Step 5 matrix](evidence/slice8/step5/module-loading-execution-matrix.md); raw results are under `.artifacts/slice8-step5-policy-20261018/`.

The accepted publish was compared before and after. Both checks passed with no added, missing or modified files, content ID `44B43088658F2F70FE8A0745EA5DFC3B1519CBF10B9347F3F4B88835EEDB3B12`, manifest SHA-256 `22D05298F7329BE8C2892BDEE33EBE71C6851EBC5D7A6B6BBF2A49C34F45FE87`, and identical comparer JSON SHA-256 `64A73CACB74F0B05BD679B72A90365FA5164EFF376B6AF9EDE65500FE79CD5FE`. The validator passed with `issues=[]` and policy `hispania-core-v1.0.0`. The artifact was not rebuilt or modified. No ModuleHost processes remained. The 18 older synthetic temp directories remain untouched as instructed.

**Step 5 status: PASS at bounded Windows x64 scope.** Slice 8 remains IN PROGRESS; Step 6 and Slice 9 have not started. Global progress remains 4/14 — 28.6%. No commit or push was made; the worktree retains pre-existing changes from earlier authorized steps.

## Step 6 — Artifact Fitness (2026-10-18)

### Scope and three separate results

Step 6 tested the current, locally modified worktree as a new Windows x64 candidate. It did not rebuild, overwrite, or replace the historical Slice 7 reference. The three properties remain separate:

| Property | Result | Evidence |
|---|---|---|
| Historical accepted artifact integrity | PASS | Before/after comparisons match the recorded content ID and manifest SHA-256; `added=[]`, `missing=[]`, `modified=[]`. |
| New candidate structural and published-process fitness | PARTIAL | Allowlist and manifest validation pass; published smoke is 15/15, but the required automated Runtime regression has a reproducible in-process ALC cleanup failure, and a module capability response could not be tested through the current Core public API. |
| Reproducibility and distribution dependencies | PARTIAL | Two publishes from the same dirty worktree, SDK and machine matched all 18 payload files. A clean-machine build/run was not available; this does not establish clean-environment reproducibility. |

### Candidate build and identity

The candidate was built into a new, exclusive `%TEMP%` directory, outside the repository and separate from the accepted artifact:

`%TEMP%\hispania-slice8-step6-candidate-187c3f92ba8042298b28ef05452c939f-publish`

Command:

```powershell
dotnet publish apps/core/Hispania.Core.csproj --configuration Release --runtime win-x64 --self-contained false --output "$candidate" -p:Deterministic=true -p:ContinuousIntegrationBuild=true -p:DebugType=none -p:DebugSymbols=false --verbosity minimal
```

The failed first command attempt and its validator diagnostics are retained in the ignored `.artifacts/slice8-step6-20261018/` work area; it is not the candidate. The successful command produced Release `net10.0`, RID `win-x64`, framework-dependent output. The machine had SDK `10.0.401` and installed `Microsoft.NETCore.App` / `Microsoft.AspNetCore.App` runtime `10.0.12`.

The artifact manifest records `sourceCommit=ef7365bb5e7d7551cb1093519743cc1885757b5b`, target `net10.0/win-x64`, mode `framework-dependent`, and candidate content ID `BCA8F79A51A2705CCD4DE10AF12ED819EE4AD426B8E74048EAB25E0FE0F11CD8`. **The source worktree was dirty.** Therefore `sourceCommit` identifies its HEAD only; it does not imply that the candidate was built from that commit alone. The candidate file inventory/content ID and the recorded worktree state are the precise build evidence.

The manifest lists 18 allowlisted files; the on-disk candidate has those 18 plus `core-artifact-manifest.json` (19 total). The validator returned `passed=true`, `issues=[]`, policy `hispania-core-v1.0.0`. Core executable SHA-256 is `389714A3F515207A2D1A7452096C5D2E17DD33D7C653DFF7B77CC4C2B7D249AE`; Core assembly is `E6581155E12B9EAB0403C87472EF3304EFFCF36F362BADD6627FB4FBAC5CF959`; published ModuleHost assembly is `B022F55113A10257FF0A3596EBBDA13A32C126EAF7A65DC897CEF17F64A16976`. The candidate manifest SHA-256 is `A336746DA28F172A7E4B84F7E5577AA5F27BC90CB8FDF2D312B74BB5B4AEAB6C`.

The structure scan found no `.pdb`, `.trx`, `.pfx`, `.pem`, `.db`, or SQLite files in the candidate, no absolute repository path in its textual JSON/config files, and no files absent from the manifest. Contracts and native SQLite runtime assets are present. Full per-file paths, sizes and SHA-256 digests are preserved in [candidate inventory](evidence/slice8/step6/candidate-inventory.json); the full candidate manifest is [candidate manifest](evidence/slice8/step6/candidate-core-artifact-manifest.json).

### Published candidate smoke

The candidate ran from its temporary publish directory, with that directory as process working/content root. A fresh Core data directory and synthetic bootstrap identity were used; SQLite logged schema version 3; HTTPS listened at loopback and `/health/live` returned healthy. The sanitized smoke JSON records 15/15 PASS, Core exit code `0`, no residual Core/ModuleHost processes, unchanged candidate files, and successful cleanup. It is retained as [published candidate smoke](evidence/slice8/step6/published-candidate-smoke-sanitized.json), SHA-256 `F43444469CBDD5558563C1C569430449BE45CAD7ACFAF5CD3BAEEFA1C000DE1D`.

The smoke also signed an otherwise valid `dotnet-inprocess-trusted` package with the ephemeral trusted test publisher. Published Core rejected it with `module_runtime_not_allowed` before it appeared in module state. The OOP fixture installed, listed trusted/authorized/disabled, activated healthy, persisted enabled/healthy state through restart, then disabled and uninstalled. A controlled fixture startup failure returned the expected conflict while Core liveness stayed healthy and the fixture remained removable. Temporary credentials, signing key, certificate, package, SQLite and cookies were held in memory or confined to the smoke-owned root; that root's cleanup was verified. `secretsPrinted=false`.

There is no `/health/ready` route in the current Core contract; successful startup completes the SQLite lifecycle before `GET /health/live` is exposed, so the smoke used startup plus healthy liveness as the available readiness evidence. The current public Core API also has no capability invocation/dispatch endpoint. The fixture used for this smoke advertises no callable capability. Therefore a successful module response through published Core is **NOT VERIFIED**; the smoke proves process activation and health, not request/response dispatch.

### Reproducibility and environment dependence

A second Release `net10.0/win-x64` framework-dependent publish was produced at a separate temporary path from the same worktree and SDK. All 18 payload files matched the candidate manifest by path, size and SHA-256; there were no extra files, and the resulting content ID was the same. The comparison is retained in [same-environment reproducibility](evidence/slice8/step6/same-environment-reproducibility.json), SHA-256 `BF1C66F4215D86BF18019F2173A665DCE81C9DF33F36364727E7B072A17DB519`.

The runtime artifact is framework-dependent: `dotnet` plus .NET 10 and ASP.NET Core 10 shared runtimes are required on Windows x64. The OOP ModuleHost is launched by `dotnet`; SQLite's native `e_sqlite3.dll` is bundled. The Core's default ASP.NET Data Protection behavior used the current Windows profile's `ASP.NET\DataProtection-Keys` directory during the smoke; two existing keys had timestamps from 2026-10-17 and no new key file was observed at the post-smoke check. The smoke did not use a clean Windows profile, VM or container. Thus independence from repository files is demonstrated for the running published Core process, while independence from user-profile/framework state and reproducibility on a clean host remain NOT VERIFIED.

### Regression results and limitation

- Core, with the required `--runtime win-x64`: **62 PASS / 1 SKIP / 0 FAIL**, TRX SHA-256 `E0113AD4D09387A0DBA544FCCE3F52A61AB60AED75AD78BD65998AB76E902772`. The SKIP is the pre-existing direct symbolic-link privilege case; the five junction checks remain separately PASS.
- Focused Slice 7 lifecycle: **7/7 PASS**, TRX SHA-256 `0D0698E64518BF2127CF82249648EC171F16626C50A15A60EAEF2B3CD7B1EA35`.
- Focused publisher trust: **1/1 PASS**, TRX SHA-256 `0A791E5A4AC6986B69F405C1B11D7480E9152E5D0DAB07AEAB3C2F14947E208F`.
- ModuleHost: **10/10 PASS**, TRX SHA-256 `5AA18227D9067A2F822F53D19253F0AA935EFCED98B845A290F662CA2776B3A4`.
- Package lifecycle/historical reader: **94/94 PASS**, TRX SHA-256 `C9E1C44AF548BBBD1ACE79122EE454695C2EEBD67CE4380A8C37D8B3AC0CD577`; backup/restore **2/2 PASS**, SHA-256 `DE6E982234D42E84B623C3874A4E1DFAE05D90FE609AEFAAC8B89F5A724B968A`.
- Full Runtime: **264 PASS / 1 FAIL / 0 SKIP**, TRX SHA-256 `21005D3831C8964E05948B5D511A0C181BF6C7CE9F4662F933120B10EAFFFDF4`. The failure is `PrivateModuleDependencyIsolationTests.PackagesResolveSamePrivateAssemblyIdentityIndependentlyAndUnloadAfterLeaseRelease(runtime: dotnet-inprocess-trusted)`: its `finally` cleanup receives `UnauthorizedAccessException` deleting `Hispania.PrivateFixture.Dependency.dll` on Windows, despite the weak-reference unload check completing. The focused two-case test was repeated three times; each run passed the OOP case and failed the in-process cleanup case. This is reproducible test/ALC cleanup debt and blocks Step 6 PASS. No functional code was changed to mask it.

A Core test invocation without a RID failed at build setup with `NETSDK1047` because the referenced ModuleHost requires `net10.0/win-x64`; the correct RID-specific invocation above passed its test set. No accepted artifact was rebuilt.

### Historical artifact comparison

The historical accepted artifact was compared before and after the candidate build, tests and smoke. Both comparisons pass with `added=[]`, `missing=[]`, `modified=[]`, original content ID `44B43088658F2F70FE8A0745EA5DFC3B1519CBF10B9347F3F4B88835EEDB3B12`, and original manifest SHA-256 `22D05298F7329BE8C2892BDEE33EBE71C6851EBC5D7A6B6BBF2A49C34F45FE87`. Before/after machine-readable results are retained in [historical baseline before](evidence/slice8/step6/historical-baseline-before.json) and [historical baseline after](evidence/slice8/step6/historical-baseline-after.json). The accepted artifact was not written or rebuilt.

### Files, state and decision

Added [published artifact fitness smoke](../../tools/test-slice8-published-artifact-fitness.ps1), which is pinned to the unique Step 6 candidate directory pattern and checks candidate identity/inventory before and after execution. Evidence files are under `docs/architecture/evidence/slice8/step6/`; raw TRX, command logs and publish output are under ignored `.artifacts/slice8-step6-20261018/`. The repository was already dirty before Step 6; the path-level status and SHA-256 snapshot is retained in [source worktree state](evidence/slice8/step6/source-worktree-state.json), and that work was preserved. No commit, push, merge, rebase, historical artifact replacement, cleanup of the 18 older temp roots, Step 7, or Slice 9 was performed. Roadmap remains 4/14 — 28.6%.

**Step 6 status: PARTIAL.** The distribution candidate builds, validates, runs from outside the repository and passes the published lifecycle smoke. Step 6 cannot pass while the Runtime suite has a reproducible failure, the capability response criterion is not implemented/exercisable through current public Core interfaces, and clean-host independence remains unverified.

The full Runtime failure also left four test-created `%TEMP%\hispania-slice8-private-deps-*` fixture roots: `233f858b61e142a193fd22fc9ef36946`, `eafcaa9524c740b0adfcac6a42823032`, `5e48e21846d041a8bab563063beb3cf8`, and `8cd79a9da6544e65bc07102122fbd329`. A recursive cleanup request for these exact test roots was rejected by tool policy before execution; no filesystem deletion occurred and no alternative command was attempted. The 18 older temporary roots were left intact. These four owned test leftovers are retained and require owner cleanup.

### Step 6 ALC unload regression follow-up

The owner authorized diagnosis and correction of the Windows in-process collectible ALC cleanup failure. The test now executes load/lease/unload work in a separate no-inline helper frame, captures a weak reference for the second context, probes extracted DLLs with an exclusive read handle after unload, and reports the owned temp root and HRESULT when cleanup fails. The helper-frame change did not fix deletion. Both context weak-reference checks completed, and the exclusive read-open probe passed, while recursive deletion still failed with `UnauthorizedAccessException` (`0x80070005`) on `Hispania.PrivateFixture.Dependency.dll`. File attributes and inherited ACL inspected after test exit were ordinary; those observations do not explain the denial at failure time. The precise Windows delete restriction remains unknown.

Ten additional in-process focused executions failed 10/10 during cleanup; OOP passed 1/1. Dependency isolation class: 5 PASS / 1 FAIL. Core: 62 PASS / 1 existing symlink SKIP; ModuleHost: 10/10; Package Lifecycle/historical reader: 94/94; backup/restore: 2/2. Full Runtime with current diagnostic behavior: 262 PASS / 3 FAIL. In addition to the targeted unload case, two in-process missing/incompatible dependency cases also fail cleanup when retries are removed. A temporary production change to await the existing ALC unload condition on startup failure did not fix those cases and was reverted; no product-runtime change from this investigation remains.

TRX files and digests are under ignored `.artifacts/slice8-step6-alc-investigation/`; see [EV-050 ALC unload diagnosis](evidence/slice8/step6/alc-unload-diagnosis.md). Historical artifact before/after checks remain PASS with content ID `44B43088658F2F70FE8A0745EA5DFC3B1519CBF10B9347F3F4B88835EEDB3B12`, manifest SHA-256 `22D05298F7329BE8C2892BDEE33EBE71C6851EBC5D7A6B6BBF2A49C34F45FE87`, and no file differences. The temp inventory now contains 56 matching roots; none were removed. Step 6 remains PARTIAL; Slice 8 remains IN PROGRESS; Step 7 and Slice 9 were not started.

### Step 6 Windows ALC deletion diagnosis — 2026-10-18

A diagnostic-only harness compared collectible ALC unload/delete in-process, child-process exit then parent delete, copy/delete without loading, and ordinary-file control; it also loaded the real v1/v2 private-dependency fixtures. All new scenarios passed, including exclusive-open, collected weak references, native `DeleteFileW`, recursive cleanup, and no Restart Manager process. This does not reproduce the original recursive cleanup `UnauthorizedAccessException`/`0x80070005`, so its native failing operation and cause remain NOT VERIFIED. A retained prior root showed ordinary NTFS attributes/ACL after the fact; this is not causal evidence. No production fix was made, and no correction-triggered regression suite was run. The 56 prior temp roots were preserved; historical accepted artifact baseline still matches with the original content ID and manifest hash. ALC unload substep and Step 6 remain PARTIAL.

### Paso 6 — testhost original ALC failure capture (2026-10-19)

The original Runtime testhost reproduced the cleanup denial at `Directory.Delete(root, recursive:true)`. Missing dependency denied deletion of the module DLL; incompatible dependency denied deletion of the private dependency DLL. Both emitted `UnauthorizedAccessException`/`0x80070005` (Win32 mapping 5). At that instant, a weak reference to the collectible ALC was alive and the fixture assembly remained loaded in it. File `DELETE` open, exclusive-read open, parent `FILE_DELETE_CHILD` open, ACL and attributes all passed; no ModuleHost process was present. This distinguishes the failure from absent DELETE permissions and ordinary file-handle sharing.

The production failed-start path in `ModuleExtensionCatalog.LoadAndStartAsync` invokes `candidate.Unload()` but returns the startup error without awaiting ALC collection; test cleanup follows immediately. A test-only strong-reference bug in the successful unload test was fixed (clear the second context reference before `UnloadAsync`); that positive case passed 10/10. Missing and incompatible failed-activation cleanup still fail when run individually or sequentially. Full dependency-isolation class: 4 PASS / 2 FAIL. Full Runtime with standard parallelism: 264 PASS / 1 FAIL. Core: 62 PASS / 1 existing symlink SKIP. ModuleHost: 10/10 PASS. Historical Core baseline remains unchanged.

No production code was modified because the required lifecycle correction needs explicit owner authorization. Eleven newly generated failed-run roots remain in addition to the 56 initial roots; none were deleted. Step 6 remains PARTIAL and no Step 7/Slice 9 work was started.

### Step 6 ALC failed-start lifetime verification — 2026-10-19

The testhost deletion failure was traced to the test retaining `ModuleStartupException` until after package-tree cleanup. Its inner startup exception retained stack metadata from the collectible fixture assembly. While held, `WeakReference.IsAlive` remained true and Windows recursive deletion returned `UnauthorizedAccessException` (`0x80070005`). After asserting the original exception type and releasing the test's strong reference, the ALC collection check and owned-tree cleanup passed. `ModuleExtensionCatalog.LoadAndStartAsync` already disposes and clears the failed candidate before calling `AssemblyLoadContext.Unload()`; collection remains eventual while the caller retains the original failure. No production code or activation contract changed. The test now observes unloading after releasing its exception reference.

Positive unload, missing dependency, and incompatible dependency each passed 10/10, with one TRX per run. The full isolation class passed 6/6; OOP isolation 3/3; Core 62 PASS / 1 known symlink SKIP; ModuleHost 10/10; Package Lifecycle 94/94; backup/restore 46/46 and package backup/restore 2/2; publisher trust 32/32; full Runtime 265/265. Exact hashes and paths are recorded in [ALC correction evidence](evidence/slice8/step6/alc-correction-results-20261019.json); raw TRX/logs remain under ignored `.artifacts/slice8-step6-alc-correction-20261019/`.

The historical accepted Core baseline passed before and after with content ID `44B43088658F2F70FE8A0745EA5DFC3B1519CBF10B9347F3F4B88835EEDB3B12`, manifest SHA-256 `22D05298F7329BE8C2892BDEE33EBE71C6851EBC5D7A6B6BBF2A49C34F45FE87`, and empty differences. All 67 previously inventoried temp roots remain; two roots created by the diagnostic reproduction also remain, for 69 total. No roots or existing evidence were deleted. `git diff --check` passed; no commit or push was made.

**ALC test-lifecycle gate: PASS — OWNER ACCEPTED 2026-10-19.** The owner accepts that `Unload()` requests collectible ALC unload, collection may occur later, and consumer-held references (including the original activation exception) may temporarily defer collection. The original activation error remains available for diagnosis; immediate release is not guaranteed. This records existing runtime behavior and does not alter frozen contracts or the OOP-only policy. **Step 6 remains PARTIAL** for the separately documented published capability-response and clean-environment distribution limits, both still NOT VERIFIED. No Step 7 or Slice 9 work started; roadmap remains 4/14 — 28.6%.

### Environment-independence follow-up — 2026-10-19

Authorized verification of candidate `BCA8F79A51A2705CCD4DE10AF12ED819EE4AD426B8E74048EAB25E0FE0F11CD8` stopped before launching Core. Read-only validation found its 18 payload files consistent with the candidate manifest (manifest SHA-256 `A336746DA28F172A7E4B84F7E5577AA5F27BC90CB8FDF2D312B74BB5B4AEAB6C`; `net10.0` / `win-x64` / framework-dependent). The actual Core configuration supports the existing data directory and HTTPS certificate settings but exposes no Data Protection key-ring location. ASP.NET Core's default uses the Windows profile-backed key store; consequently the requested isolated run cannot meet the no-profile-reuse condition. No process was started and no candidate copy, database, certificate, or temporary runtime configuration was created. No fallback or undocumented override was used.

The historical accepted Slice 7 artifact comparison passed before this follow-up with empty added/missing/modified sets and its original content ID and manifest digest. The pre-existing 69 temporary roots remain untouched. Result: **PARTIAL**; this check confirms candidate integrity but not isolation from profile state or a clean machine. Continuation requires an authorized, supported Data Protection isolation mechanism; no production change was made. No code, candidate, historic artifact, roadmap, commit, or remote state changed.

### Step 6 — Configurable Data Protection key isolation (2026-10-19)

**Implementation.** Added optional setting `HISPANIA_CORE_DATA_PROTECTION_KEYS_DIRECTORY`. If absent, Core does not register an explicit repository and keeps ASP.NET Core's prior default behavior. If present, this setting is currently supported only on Windows: configuration requires an absolute path outside the publish directory, checks path components with `StoragePathGuard`, creates the directory, verifies write/delete access with an owned probe, and fails closed on errors. The host uses `PersistKeysToFileSystem` and explicitly calls `ProtectKeysWithDpapi`; it does not fall back to the profile repository. It does not copy or migrate keys. No public package/module contract changed.

ASP.NET Core documents that choosing an explicit key repository disables automatic at-rest encryption discovery. The explicit Windows DPAPI protector preserves the existing Windows default's at-rest protection. Evidence verifies `<encryptedSecret>` with `DpapiXmlDecryptor`; no plaintext `<secret>` element was present. Directory isolation is not itself encryption. DPAPI is scoped to the current Windows user context; this proves separation from the ASP.NET Core profile key-ring directory, not independence from the account's DPAPI capability or a clean Windows identity.

**Tests.** New configuration tests: **8/8 PASS** (unset/default, relative and empty path rejection, publish overlap rejection, file-parent/inaccessible path, ACL-denied directory with original ACL restored, valid path/probe removal, DPAPI-protected persisted key and decrypt after host rebuild, and no test payload/protected value in captured Data Protection logs). Full Core: **70 PASS / 1 SKIP / 0 FAIL**; the skip is the existing direct symlink-creation privilege limitation. ModuleHost: **10/10 PASS**. Package lifecycle without an RID override: **58/58 PASS**. Publisher trust focus: **1/1 PASS**. Full Runtime: **264 PASS / 1 FAIL / 0 SKIP**. The failure is `PrivateModuleDependencyIsolationTests.PackagesResolveSamePrivateAssemblyIdentityIndependentlyAndUnloadAfterLeaseRelease(runtime: dotnet-inprocess-trusted)`, cleanup `UnauthorizedAccessException` / `0x80070005` on `Hispania.PrivateFixture.Dependency.dll`; this is retained as a regression failure, not masked by the owner-accepted eventual ALC semantics. An initial package-lifecycle invocation with `-r win-x64` used a testhost-derived helper path with a duplicated `net10.0` segment; the expected no-RID invocation passed all 58 tests.

**New candidate and isolated published smoke.** Published a new Release `net10.0/win-x64`, framework-dependent candidate; the prior candidate and Slice 7 accepted artifact were not modified. Candidate root: `%TEMP%\hispania-slice8-step6-candidate-9e471e99e46a48739e72d507afa7baa3-publish`. A separate exclusive copy was used for execution: `%TEMP%\hispania-slice8-step6-candidate-46043923704b4a3ebe7a27f61f9b625a-publish`. Both have content ID `9459242A34B042402F79D140CEED7D16BDEB149616EC7B08573188A34D31BED0`; manifest SHA-256 `8B4196461AB0EA5A8E5BF92931EC0756839E82F82208309B00539032E83185DD`; 18/18 payload files match, validator `passed=true`, `issues=[]`, policy `hispania-core-v1.0.0`. Core EXE SHA-256 `389714A3F515207A2D1A7452096C5D2E17DD33D7C653DFF7B77CC4C2B7D249AE`; Core DLL `2DA56522904C0C4A1ABA417FA0594BE1E8F8F712161C7A42003154B4E7FA7A6D`; ModuleHost `4457DBDB9C5657865B1C7A4313A23B53D6709F3249E3B2083D60C11F7CEB583E`.

The isolated-copy smoke passed **17/17 twice**. The sanitized second-run result is [published-candidate-smoke-dp-isolated-20261019.json](evidence/slice8/step6/published-candidate-smoke-dp-isolated-20261019.json), SHA-256 `6722D2D88D918B53C6D6745F560EA4481167288747C3A735B7B2A1D36D7F8C12`. It confirms a key file was written under the run-owned `data-protection-keys` directory, DPAPI metadata, restart persistence, module install/activation/OOP-only rejection/disable/uninstall, Core exit `0`, no residual Core/ModuleHost process, candidate unchanged, and run-root cleanup; `secretsPrinted=false`. The candidate manifest and full per-file hashes are in [candidate-dp-isolated-manifest-20261019.json](evidence/slice8/step6/candidate-dp-isolated-manifest-20261019.json); summarized identities and gate results are in [candidate-dp-isolated-summary-20261019.json](evidence/slice8/step6/candidate-dp-isolated-summary-20261019.json), SHA-256 `D439C1D853462626B9DBBCE4F8A7975459158B196B9E7C402DF233AEC02C6B37`.

Core and published ModuleHost ran from the isolated copied artifact outside the repository. The smoke harness still uses the repository SDK/project to build its harmless fixture into its own temporary root; this is a test-harness build dependency, not a runtime Core/ModuleHost file dependency. No clean machine or separate Windows identity was tested. The default ASP.NET Data Protection key-ring directory was not configured or selected by the candidate; explicit key persistence points to the isolated test root. The DPAPI protector remains bound to the current Windows account.

**Historical artifact and temporary roots.** Slice 7 historical baseline passed before and after, with empty added/missing/modified arrays, unchanged content ID `44B43088658F2F70FE8A0745EA5DFC3B1519CBF10B9347F3F4B88835EEDB3B12` and manifest SHA-256 `22D05298F7329BE8C2892BDEE33EBE71C6851EBC5D7A6B6BBF2A49C34F45FE87`. All 69 historical private-dependency temp roots were preserved. The Runtime failure created one additional root, bringing the observed count to 70; it was retained at `%TEMP%\hispania-slice8-private-deps-abbd6690e1e94816a097d0af1cb6e46d`. The two new candidate directories remain as evidence; the smoke-owned runtime root and certificate/key material were cleaned.

**Disposition: PARTIAL.** Configurable key-ring isolation and the published Windows x64 lifecycle smoke pass. The full Runtime suite has one failing in-process dependency cleanup test; clean-machine / different-user independence is not established, and the public capability response remains unavailable through current Core APIs. Step 6 remains PARTIAL; Slice 8 remains IN PROGRESS. No Step 7, Slice 9, commit, push, or roadmap change.

Framework behavior reference: Microsoft documents that an explicit key repository disables automatic at-rest protection and recommends explicitly configuring a protector; see [ASP.NET Core key storage providers](https://learn.microsoft.com/en-us/aspnet/core/security/data-protection/implementation/key-storage-providers?view=aspnetcore-10.0) and [key encryption at rest](https://learn.microsoft.com/en-us/aspnet/core/security/data-protection/implementation/key-encryption-at-rest?view=aspnetcore-10.0). The implemented Windows DPAPI protector is user-context-bound.

### Runtime ALC stability recheck — 2026-10-19

The ALC test failure recurred: 20 individual invocations each had the in-process row fail and OOP row pass; 10 class runs each had 5 PASS / 1 FAIL; and 5 full Runtime runs each had 264 PASS / 1 FAIL. At pre-delete, one of two collectible-context WeakReferences remained alive, no fixture assembly was enumerable, and the DELETE/exclusive-read probes opened. Recursive deletion failed with `UnauthorizedAccessException` (`0x80070005`). This is a recurrence of the Windows cleanup symptom, but the current success path has no activation exception retained; equivalence to the earlier exception-lifetime cause is not established. A test-only diagnostic now preserves before/after snapshots in the TRX. Production code remains unchanged.

Core: 70 PASS / 1 NOT EXECUTED for the known symlink privilege limitation. ModuleHost: 10/10 PASS. Publisher trust: 32/32 PASS. Package Lifecycle: 94/94 without RID; 79 PASS / 15 FAIL with RID because the test harness constructs an incorrect ProcessHost path under RID. Data Protection: 8/8 PASS. No direct Data Protection dependency or CoreHost startup exists in the failing Runtime test. The 69 roots in the prior inventory all remain; total current count is 106, including 37 outside that inventory (one pre-existing and 36 added during this recheck). Historical Core content ID and manifest hash are unchanged and the before/after comparator lists are empty. Full TRX hashes and commands are indexed in [runtime stabilization evidence](evidence/slice8/step6/runtime-stabilization-20261019.json); raw TRX files are retained under `.artifacts/slice8-step6-runtime-stabilization-20261019/`.

**Step 6 remains PARTIAL.** No production correction is justified yet, and no Step 7 or Slice 9 work started.

### Step 6 ALC async-frame differential follow-up — 2026-10-19

The owner-authorized test-only experiments found that explicitly clearing lease locals after disposal did not resolve the cleanup failure, and disabling the before-delete diagnostic did not change it. Moving lease acquisition, module calls/assertions, and disposal to a synchronous `[NoInlining]` helper did: the narrower variant kept unload and WeakReference observation in the caller's async frame and still passed. It passed **5/5 focused invocations** (each covers in-process and OOP) and the complete isolation class **6/6**. This demonstrates the lease-bearing async-frame boundary affects the symptom; it does not identify the exact object/GC root retaining the second ALC. No GC-root tool was available; no tool was installed and no dump was collected. No production code, contracts, or OOP-only policy changed.

Post-gate regressions: Runtime **265/265 PASS**; Core **70 PASS / 1 SKIP / 0 FAIL** (the known direct symlink privilege limitation); ModuleHost **10/10 PASS**; Package Lifecycle **94/94 PASS**; Publisher Trust **32/32 PASS**. Historical Slice 7 artifact comparison passed before and after with content ID `44B43088658F2F70FE8A0745EA5DFC3B1519CBF10B9347F3F4B88835EEDB3B12`, manifest SHA-256 `22D05298F7329BE8C2892BDEE33EBE71C6851EBC5D7A6B6BBF2A49C34F45FE87`, and empty added/missing/modified lists. All 106 temporary roots present at start remain; two failed diagnostic roots were retained, current total 108. TRX paths/digests and source hash are recorded in [ALC reference investigation](evidence/slice8/step6/alc-reference-investigation-20261019.json); analysis is in [ALC unload diagnosis](evidence/slice8/step6/alc-unload-diagnosis.md). **ALC focused cleanup gate: PASS; exact GC root: NOT VERIFIED; Step 6 remains PARTIAL** for the separate public capability-response and clean-environment evidence gaps. No Step 7, Slice 9, commit, or push.

### Step 6 public capability invocation audit — 2026-10-19

The Core publish exposes health, authentication and module administration routes only. Module administration authenticates a Core session, checks read/manage permission, and requires same-origin plus antiforgery for mutations. There is no Core endpoint that accepts a capability invocation or returns its result. The only fixture Core allows in v1.0 (`hispania.slice7.fixture`) declares no capabilities, and Core constructs `ModuleExtensionCatalog` without an `IPlatformJobContributionRegistryV1`.

The Runtime does support typed capability use inside a host: `ICapabilityResolver` and `ModuleExtensionCapabilityExecutor`; OOP invocation travels as framed request/response messages over a current-user-only named pipe, with per-launch token and identity/protocol negotiation, manifest declaration checks, timeout/cancellation and controlled worker failure. The existing Worker Agent uses this internally for distributed work. Separately, `apps/server` has permission-guarded Platform Jobs APIs and mTLS worker endpoints; those provide asynchronous allowlisted jobs and result metadata, not generic invocation from the published Core. The server path is not part of the Core candidate.

Existing signed OOP integration tests passed **2/2**, demonstrating internal invocation and result delivery, cancellation/timeout and ModuleHost crash recovery. No published-Core invocation was attempted because no compatible public route exists. This is **NOT VERIFIED** for the published artifact. Both retained candidates independently passed the artifact validator: `BCA8F79A51A2705CCD4DE10AF12ED819EE4AD426B8E74048EAB25E0FE0F11CD8` and `9459242A34B042402F79D140CEED7D16BDEB149616EC7B08573188A34D31BED0`. The historical Slice 7 Core baseline remains unchanged; 108 private-dependency temp roots remain preserved. See [capability audit evidence](evidence/slice8/step6/capability-invocation-audit-20261019.json), [Artifact Fitness matrix](evidence/slice8/step6/artifact-fitness-matrix.md), and [EV-050](HISPANIA-V1-EVIDENCE-LEDGER.md).

Possible future routes are: expose a product-specific typed application endpoint with explicit authz, payload validation, timeouts and audit; use the existing asynchronous authorized Platform Jobs flow for allowlisted job types; or keep resolution internal and expose only application-owned APIs. Do not provide an arbitrary CLR type/method dispatcher. This audit does not select an option or authorize implementation. **Step 6 remains PARTIAL** if a client-visible module capability response is an acceptance criterion; an API decision belongs to a separately authorized implementation scope. No code or frozen contract changed; no commit, push, Step 7 or Slice 9.

## Step 6 — Consolidated Artifact Fitness acceptance review — 2026-10-19

**State: PARTIAL.** The owner ratified that internal OOP capability invocation and response satisfy Artifact Fitness without a generic public Core HTTP invocation API. No endpoint or frozen contract was added.

### Acceptance matrix

| Criterion | Status | Evidence and limits |
|---|---|---|
| Windows x64 candidate is structurally valid against the allowlist and manifest | PASS | Latest content ID `9459242A34B042402F79D140CEED7D16BDEB149616EC7B08573188A34D31BED0`; validator rerun: `passed=true`, `issues=[]`; manifest SHA-256 `8B4196461AB0EA5A8E5BF92931EC0756839E82F82208309B00539032E83185DD`. |
| Published lifecycle and OOP ModuleHost operation | PASS | Prior sanitized published smoke passed 17/17 twice; internal capability invocation/response 2/2 and dependency isolation 6/6 passed in previously recorded runs. |
| Public client invocation and response through Core | LIMITACIÓN ACEPTADA — ratificada por el propietario | No public generic invocation endpoint exists. Internal OOP request/response is verified; published-Core client invocation was not tested. |
| Historical Slice 7 artifact remains unchanged | PASS | Comparator rerun: content ID `44B43088658F2F70FE8A0745EA5DFC3B1519CBF10B9347F3F4B88835EEDB3B12`; manifest SHA `22D05298F7329BE8C2892BDEE33EBE71C6851EBC5D7A6B6BBF2A49C34F45FE87`; no added, missing or modified files. |
| Execution outside repository | PASS, scoped | Core/ModuleHost executed from an isolated publish copy; the smoke fixture build still uses repository SDK/source. |
| Execution under another Windows identity | NOT VERIFIED | No alternate identity was tested; DPAPI is tied to the current user context. |
| Execution on a clean machine | NOT VERIFIED | No clean machine was used; candidate is framework-dependent and requires .NET 10 / ASP.NET Core 10. |
| Clean-source provenance / reproducible build from clean checkout | NOT VERIFIED | Candidate came from a worktree with uncommitted changes. Its manifest and hashes validate its contents, not a clean checkout build. |
| Direct symlink case | NOT VERIFIED | Core 70 PASS / 1 SKIP; the skipped case requires a Windows symlink privilege unavailable in the recorded run. |
| Package Lifecycle with RID | PASS after correction | Full filter passed 94/94 without RID and 94/94 with `win-x64`; the prior 79/15 result is retained as pre-fix evidence. |
| Latest recorded regressions | PASS, prior recorded runs | Runtime 265/265; Core 70 PASS / 1 SKIP; ModuleHost 10/10; Package Lifecycle 94/94; Publisher Trust 32/32; dependency isolation 6/6; OOP invocation 2/2. Not rerun as part of this documentation-only task. |

### Limitations and residual risks

- Core has no public generic capability invocation API. The fixture smoke is built using repository SDK/source.
- No execution under a second Windows identity or on a clean machine has been demonstrated.
- Data Protection uses DPAPI in the current user context; isolating the key directory does not make keys portable across users.
- The latest candidate was built from a worktree with uncommitted changes.
- The direct Core symlink test remains SKIP. The previous RID harness failure is corrected; its failing TRX remains preserved as pre-fix history.
- Out-of-process execution provides process and failure isolation, not a sandbox against malicious native modules.

### Integrity and worktree

This review reran the latest candidate validator and historical baseline comparator. The latest candidate passed with no issues; the historical artifact comparison passed with empty `added`, `missing`, and `modified` arrays. No candidate, historical artifact, temporary directory, source code or contract was modified by this consolidation. The repository already had a dirty worktree before these documentation edits; this task adds documentation only. The current TEMP scan found 114 directories matching `hispania-slice8-*`; no directories were created or removed in this task. Earlier evidence records the 108-root preserved inventory.

**Disposition at that audit point:** Step 6 remained PARTIAL. This was superseded by the explicit owner closure recorded below: Step 6 is now **PASS CON ALCANCE LIMITADO**, while the listed criteria remain NOT VERIFIED and Slice 8 remains IN PROGRESS.

## Step 6 — Package Lifecycle win-x64 harness correction — 2026-10-19

The owner ratified the proposed acceptance that internal OOP capability invocation/response is sufficient and that a generic public Core invocation API is not required. This is a scope decision only; no endpoint or contract changed. Alternate Windows identity, clean machine and clean checkout remain NOT VERIFIED; the direct symlink case remains SKIP; OOP is not a sandbox. Step 6 remains PARTIAL.

The RID failure came from treating `Parent.Name` of `AppContext.BaseDirectory` as the build Configuration. In a no-RID test output the parent is `Release`; for `Release/net10.0/win-x64` output the parent is `net10.0`. The harness then appended `net10.0` again, resolving `bin/net10.0/net10.0` instead of `bin/Release/net10.0/win-x64`. The ModuleHost fixture path used the same faulty assumption.

Changed only `tests/unit/PackageLifecycleIntegrationTests.cs`: helper resolution now takes the actual output suffix relative to `tests/unit/bin`, applies it to the referenced ProcessHost and ModuleHost project output folders, and checks the exact resulting file exists. There is no arbitrary fallback lookup. Core, ModuleHost, Runtime and public contracts were not changed by this correction.

Validation results: Package Lifecycle full filter **94/94 PASS without RID** and **94/94 PASS with `win-x64`**; directly affected integration class **58/58 PASS in each mode**; Runtime **265/265 PASS**; Core **70 PASS / 1 SKIP / 0 FAIL** (known direct symlink privilege limit); ModuleHost **10/10 PASS**; Publisher Trust / `ModuleInfrastructureTests` **32/32 PASS** (additional publisher-named focus **3/3 PASS**). Commands: `dotnet test tests/unit/Hispania.Runtime.Tests.csproj --configuration Release --no-restore --filter FullyQualifiedName~PackageLifecycle` and the same command with `--runtime win-x64`. Runtime/Core/ModuleHost/Publisher Trust used the filtered commands recorded in this report and EV-050. TRX files are retained under `tests/unit/TestResults/` and `tests/core/TestResults/`; their SHA-256 values are recorded in EV-050.

The historical Core baseline passed both before and after: content ID `44B43088658F2F70FE8A0745EA5DFC3B1519CBF10B9347F3F4B88835EEDB3B12`, manifest SHA-256 `22D05298F7329BE8C2892BDEE33EBE71C6851EBC5D7A6B6BBF2A49C34F45FE87`, no added/missing/modified files. No historical temporary directory was removed; current TEMP inventory is 114 directories matching `hispania-slice8-*` (the previously inventoried 108 roots remain preserved). This correction does not close Artifact Fitness; Step 6 remains **PARTIAL** for the explicitly unverified evidence limits above.

## Formal closure — Step 6 Artifact Fitness — 2026-10-19

**Paso 6 = PASS CON ALCANCE LIMITADO.** This is the owner's explicit acceptance for the validated Windows x64 candidate, not a claim that unverified criteria passed. **Slice 8 = IN PROGRESS.** Step 7 and Slice 9 have not started.

## Step 7.1 — Published Integration Gate contract (2026-10-19)

Step 7.1 defines the acceptance contract and evidence map only. The full criterion matrix, proposed blocking policy, evidence provenance rules and proposed execution plan for Steps 7.2–7.5 are in [Published Integration Gate](evidence/slice8/step7/published-integration-gate.md). The contract distinguishes internal component tests, integration from an exact published candidate, repository/SDK independence, path/configuration isolation, security/failure behavior, and reproducibility/provenance. It carries forward R3/Architecture Freeze §13 product exclusions and the Windows x64 scope.

Read-only verification on 2026-10-19: the latest candidate validator returned `passed=true`, `issues=[]`, content ID `9459242A34B042402F79D140CEED7D16BDEB149616EC7B08573188A34D31BED0`; manifest SHA-256 `8B4196461AB0EA5A8E5BF92931EC0756839E82F82208309B00539032E83185DD`. The historical Slice 7 baseline comparator returned `passed=true`, content ID `44B43088658F2F70FE8A0745EA5DFC3B1519CBF10B9347F3F4B88835EEDB3B12`, manifest SHA-256 `22D05298F7329BE8C2892BDEE33EBE71C6851EBC5D7A6B6BBF2A49C34F45FE87`, with empty `added`, `missing`, `modified`, and `selfTest` arrays. These are artifact-integrity checks; they do not rerun integration tests.

The gate matrix keeps alternate Windows identity, clean Windows machine, clean-checkout provenance, direct symlink execution and byte-identical clean rebuild as **NOT VERIFIED**. Public generic capability invocation remains **NOT APPLICABLE** under the owner's accepted Step 6 scope. DPAPI remains tied to the current Windows user; the existing candidate came from a dirty worktree; OOP is not a security sandbox. 114 `hispania-slice8-*` TEMP directories were observed and preserved. No temporary directory was created or removed.

**Step 7.1: PASS — documentation contract prepared.** Steps 7.2–7.5 have not been executed. Slice 8 remains **IN PROGRESS**; global roadmap remains 4/14 — 28.6%. No production code, tests, frozen contracts, artifact, or roadmap were modified. No commit or push was made. Existing worktree changes were preserved.

### Step 7.1 ratification audit — PI-04 and acceptance policy (2026-10-20)

This audit supersedes the prior Step 7.1 “PASS — contract prepared” snapshot below; that earlier status described document preparation before the requested PI-04 and policy ratification audit.

The [Published Integration Gate](evidence/slice8/step7/published-integration-gate.md) now reproduces the six exact R2 paths and SHA-256 values and records an individual manifest path/hash result for each. All six paths and hashes are absent from both the latest candidate manifest and the historical Slice 7 manifest. This is a per-artifact inventory result only; it is not rights clearance.

PI-04 remains **NOT VERIFIED as an aggregate**. The candidate validator checks manifest paths/hashes, allowed files and declared dependency metadata. The original R3 plan separately requires package-file, assembly/reference-graph, route, migration and content scans. Existing evidence does not demonstrate all those scan dimensions individually. The contract lists the exact R3 and allowlist exclusions, per-check evidence, result and residual risk; no grouped PASS is claimed. R3-8 (Kernel absent from declared manifest dependencies) and R3-9 (no current local modifications under listed legacy Media/Inventory/Server paths) pass only at those stated scopes.

The matrix now classifies all 36 PI criteria. Proposed preconditions to start 7.2 are PI-01–PI-03 green, owner ratification of the gate blocker policy, authorized candidate/source selection and rights scope. Proposed Step 7 close blockers are PI-01–PI-30 plus PI-33 and PI-35; PI-31, PI-32 and PI-34 are proposed non-blocking only for the bounded Windows x64 scope with explicit limitations; PI-36 is not applicable per the owner's accepted Step 6 scope. These are recommendations awaiting owner ratification, not an approved risk acceptance. PI-35 is proposed as a close blocker unless the owner explicitly accepts the symlink SKIP, because Freeze §13 disallows required skips.

Maintained `NOT VERIFIED`: execution with another Windows identity, clean machine, clean checkout, byte-identical rebuild and direct symlink. No user, VM, or environment was created. The candidate validator and historical baseline checks were rerun read-only and remain PASS with the recorded IDs/digests. 114 `hispania-slice8-*` directories remain untouched. No production, test, frozen-contract, artifact or historical-evidence file changed; no commit/push; Step 7.2–7.5 and Slice 9 remain unstarted. **Step 7.1 result: PARTIAL pending owner ratification of the proposed policy and full PI-04 exclusion audit.** Slice 8 remains IN PROGRESS.

### Accepted evidence

- Candidate integrity, manifest and allowlist: validator `passed=true`, `issues=[]`, content ID `9459242A34B042402F79D140CEED7D16BDEB149616EC7B08573188A34D31BED0`, manifest SHA-256 `8B4196461AB0EA5A8E5BF92931EC0756839E82F82208309B00539032E83185DD`.
- Published Core/ModuleHost execution outside the repository and OOP lifecycle: sanitized published smoke 17/17 twice, including isolated data storage, DPAPI-protected Data Protection keys, persistence across restart, module lifecycle and controlled failure handling. Fixture build used repository sources and SDK.
- Internal OOP invocation and response: 2/2 integration checks. A generic public Core invocation API is explicitly outside the accepted Artifact Fitness scope.
- Regressions: Runtime 265/265; Core 70 PASS / 1 SKIP; ModuleHost 10/10; Package Lifecycle 94/94 without RID and 94/94 with `win-x64`; Publisher Trust 32/32.
- Historical Slice 7 Core: content ID `44B43088658F2F70FE8A0745EA5DFC3B1519CBF10B9347F3F4B88835EEDB3B12`, manifest SHA-256 `22D05298F7329BE8C2892BDEE33EBE71C6851EBC5D7A6B6BBF2A49C34F45FE87`; comparator `added=[]`, `missing=[]`, `modified=[]`, `selfTest=[]`.

### Limitations retained as NOT VERIFIED

- Execution under another Windows identity.
- Execution on a clean machine.
- Reproduction from a clean checkout.
- Direct symlink case (existing Core test remains SKIP for the documented Windows privilege restriction).

DPAPI remains bound to the Windows user context. The smoke harness builds its fixture using repository sources and SDK. The candidate was produced from a worktree with uncommitted changes. OOP-only is process/failure isolation and is not a security sandbox. Do not describe the candidate as portable, reproducible from clean source, or independent of every Windows profile based on this evidence.

The 114 `hispania-slice8-*` temporary directories were preserved. Earlier failures and their corrections remain in the evidence history. Candidate and historical Core were validated read-only during formal closure; neither was overwritten or rebuilt.

## Step 7.2 — Static deployable-package preparation — 2026-10-20

**Scope:** static inspection and read-only verification only, under the owner's explicit authorization. No new publication, build, process launch, smoke, install, product/test/contract modification, cleanup, commit or push was performed.

### Candidate and integrity

Inspected `%TEMP%\hispania-slice8-step6-candidate-9e471e99e46a48739e72d507afa7baa3-publish`: `hispania.core`, `net10.0`, `win-x64`, framework-dependent, manifest sourceCommit `ef7365bb5e7d7551cb1093519743cc1885757b5b`, content ID `9459242A34B042402F79D140CEED7D16BDEB149616EC7B08573188A34D31BED0`, manifest SHA-256 `8B4196461AB0EA5A8E5BF92931EC0756839E82F82208309B00539032E83185DD`. Its inventory has 18 payload entries plus the manifest (19 actual files), with no missing, unexpected or altered payload paths/sizes/hashes. The full inventory is retained in `evidence/slice8/step7/candidate-inventory-9459242a34b042402f79d140ceed7d16bdeb149616ec7b08573188a34d31bed0.json`, SHA-256 `6EBE9633D2F38FD5B8D4A1A67B29AA16B74489E84EADD665DA7F8A6600369AB5`.

The Core artifact validator passed (`passed=true`, `issues=[]`) and returned the expected content ID. Independent PE metadata inspection found `e_sqlite3.dll` is native and has no CLI metadata, although the manifest says `fileType=managedAssembly`. Therefore hash/content integrity passes but **PI-01 is FAIL for semantic manifest/payload type agreement**. The defect was not changed because this scope forbids artifact modification or a new publish.

The Slice 7 historical comparator passed before/after this inspection: content ID `44B43088658F2F70FE8A0745EA5DFC3B1519CBF10B9347F3F4B88835EEDB3B12`, manifest SHA-256 `22D05298F7329BE8C2892BDEE33EBE71C6851EBC5D7A6B6BBF2A49C34F45FE87`, and `added=[]`, `missing=[]`, `modified=[]`, `selfTest=[]`.

### R2/R3 exclusion audit and PI-04

Each of the six R2 path/SHA pairs is absent from both the candidate and historical manifests; each matching source file is present in the repository at its CSV SHA-256. Static checks examined the full candidate inventory/allowlist/dependency metadata, recursive project references, PE references and embedded resource names for all 13 DLLs, runtime configs, route literals, and ASCII/UTF-16LE content. They found no named Media, Inventory, Server, ERP, AI, Npgsql/PostgreSQL, EF, worker/distributed-work, TestHost/test, historical compatibility, npm or named media/demo dependency/reference/content. The nine extracted route templates are only Core auth/module and `/health/live` endpoints. Core has no embedded resources; the resource names in dependencies are SQLite/NuGet resources and `ILLink.Descriptors.xml`. Migration markers map to Core-owned `core_schema_migrations` and SQLite code; no Media migration was found. Per-exclusion results and limitations are recorded in the Published Integration Gate.

**PI-04 = PASS for the named static exclusion scan of this exact candidate.** The package list, graph, references, routes, migration/resource names, and content checks requested by the R3 plan were individually completed; all six R2 files/hashes are excluded from both retained artifacts. This is not rights clearance, a semantic audit of arbitrary machine code, or proof that the separate legacy Server launches. Such limitations do not change the result for the specific artifact-exclusion criterion. PI-04's close blocker is satisfied at this bounded static scope.

### Deployment notes (inspected, not executed)

The candidate requires Windows x64 with .NET 10 `Microsoft.NETCore.App` and `Microsoft.AspNetCore.App` shared frameworks; ModuleHost is framework-dependent and launched by Core via `dotnet`. Launch `Hispania.Core.exe` from the package directory; the default ModuleHost path is `module-host\Hispania.ModuleHost.dll`. Core is HTTPS-only and loopback-only, default `127.0.0.1:5070`; liveness is `GET https://127.0.0.1:5070/health/live`; no readiness endpoint was found. TLS requires the configured absolute certificate path and accessible private key. `HISPANIA_CORE_DATA_DIRECTORY` selects the isolated writable SQLite/state root; absent that variable Core defaults to `%LOCALAPPDATA%\Hispania\Core`. The optional `HISPANIA_CORE_DATA_PROTECTION_KEYS_DIRECTORY` must be absolute and writable; Windows DPAPI protects keys for the current identity. Without it the framework's profile store is used. This does not establish cross-user key portability. Publisher trust is configured through `HISPANIA_CORE_MODULES__TRUSTEDPUBLISHERKEYS__<publisherId>` and revocation entries; signing private keys must remain outside the package. Bootstrap is interactive (`Hispania.Core.exe bootstrap-admin`), with synthetic credentials entered in the console. Stop with Ctrl+C and verify process exit. The complete config, permissions, data/key, HTTPS and scope notes are in the gate document. No deployment was attempted.

### Clean-source provenance proposal and status

The candidate manifest's `sourceCommit` equals HEAD, but the repository worktree is dirty, so it is not a clean-source mapping. A future procedure is documented in the gate: preserve the current dirty state; isolate only reviewed authorized changes; create a separately authorized local commit; build from a second clean worktree at that commit; verify it is clean before a unique new publish; retain the validator/inventory and compare the historical baseline. This was not executed. **PI-33 remains NOT VERIFIED**.

Owner-ratified PI policy is now reflected in the 36-row gate: PI-01–PI-03 allowed starting 7.2; PI-31/32 are non-blocking only for the tested Windows x64 user/environment; PI-33 blocks closure; PI-34 is non-blocking absent a byte-reproducibility promise; PI-35 remains blocking and its SKIP is not PASS; PI-36 is not applicable; other blocking classifications remain as ratified. The 114 historical `hispania-slice8-*` temp directories remain preserved.

**Step 7.2 result: PARTIAL.** PI-04 is PASS at the bounded static scope. PI-01 has the native PE/fileType mismatch; PI-33 is NOT VERIFIED; and PI-35 remains NOT VERIFIED. Other criteria outside this static substep retain their recorded statuses. No 7.3–7.5 or Slice 9 work started. Phase 1 stays IN PROGRESS; roadmap stays 4/14 — 28.6%.

### PI-33 local source worktree audit — 2026-10-20

Read-only preflight: branch `codex/hispania-v1-baseline-consolidation`, HEAD `ef7365bb5e7d7551cb1093519743cc1885757b5b`, no upstream configured, 24 tracked files modified, 53 untracked files, no staged or deleted paths, and no Git merge/rebase/cherry-pick/revert operation markers. The detailed per-path A/B/C/D classification, dependencies, inclusion/exclusion risks and proposed source commit procedure are in the [Published Integration Gate PI-33 audit](evidence/slice8/step7/published-integration-gate.md#pi-33--local-source-worktree-audit-and-clean-provenance-plan--2026-10-20). It identifies Core/Runtime source, publisher/validator, regression tests, Slice 8 evidence, and historical Slice 7 material separately; no `git add -A` or broad worktree selection is proposed.

The ignored-path scan reported 14,123 ignored entries, mostly generated `bin/`, `obj/`, TestResults, frontend output and `.artifacts`. The `.artifacts` tree has 25 directories and approximately 2,399 ignored paths, including prior Phase 19/Slice 8 test outputs. These are not source inputs to a clean candidate; retained evidence is to be preserved. The configured ignored operator token file is absent. No secret values were printed; filename/private-key/bearer-marker checks found no matching literal in changed/untracked files. The 114 `hispania-slice8-*` TEMP directories remain preserved.

Artifact checks remained read-only: the latest Slice 8 candidate inventory still matches all 19 files and its manifest SHA-256 `8B4196461AB0EA5A8E5BF92931EC0756839E82F82208309B00539032E83185DD` and content ID `9459242A34B042402F79D140CEED7D16BDEB149616EC7B08573188A34D31BED0`; the Slice 7 historical baseline comparer passes with empty added/missing/modified lists, content ID `44B43088658F2F70FE8A0745EA5DFC3B1519CBF10B9347F3F4B88835EEDB3B12`, and manifest SHA-256 `22D05298F7329BE8C2892BDEE33EBE71C6851EBC5D7A6B6BBF2A49C34F45FE87`. The corrected semantic validator still finds the two stale file types in both retained manifests, as recorded under PI-01; their bytes and prior cryptographic evidence are unchanged.

Proposed procedure only: owner selects exact A/B/C paths; separately authorizes an explicit-path local commit; separately authorizes a clean worktree at that commit; build and publish into a new unique directory from that clean tree; record tree/tool/dependency identity; validate manifest, PI-01 and PI-04; then document the result in a later documentation-only change. No commit, worktree, index change, new publish, production/test change or ignored/temp cleanup was performed in this audit. **PI-33 remains NOT VERIFIED; Step 7.2 remains PARTIAL.**

### PI-01 semantic file classification correction — 2026-10-20

The existing manifest schema already permits `nativeRuntime`; no frozen schema or contract change was needed. The publisher previously used the `.dll` suffix (except under `runtimes/`) to label a file `managedAssembly`, so native root-level `e_sqlite3.dll` was mislabeled. The same extension/path heuristic also left the native `Hispania.Core.exe` labeled `content`.

The publisher now inspects PE headers and CLI metadata: PE assemblies with CLI metadata are `managedAssembly` (including managed `.exe` files); valid native PE DLLs are `nativeRuntime`; native PE executables are `applicationHost`; `.deps.json` and `.runtimeconfig.json` keep their dedicated types; `.so`/`.dylib` remain `nativeRuntime`; other permitted files are `content`. Invalid PE images with `.dll`/`.exe` suffixes fail closed. The validator independently derives the same expected type from the payload and rejects mismatches while retaining hash, size, allowlist, path, file-set and content-ID checks. The hash and content-ID algorithms were not changed; `fileType` remains part of the canonical file record used by content identity, so a future corrected manifest will have a newly computed content ID.

Focused validator suite: **34/34 PASS**, including managed DLL/native DLL positive cases, both reciprocal mismatch rejections, managed `.exe`, allowed content, unknown type, and unchanged payload hashes/canonical identity on validation. The CLI integration test also passed within that suite. No new publish was produced.

Read-only validation with the corrected validator reports the retained Slice 8 candidate as `passed=false` for `Hispania.Core.exe` (`content` vs `applicationHost`) and `e_sqlite3.dll` (`managedAssembly` vs `nativeRuntime`). The historical Slice 7 artifact reports the same semantic discrepancies. Both artifacts' prior cryptographic evidence remains valid and unchanged: the Slice 8 candidate still has content ID `9459242A34B042402F79D140CEED7D16BDEB149616EC7B08573188A34D31BED0`, manifest SHA-256 `8B4196461AB0EA5A8E5BF92931EC0756839E82F82208309B00539032E83185DD`, and its 19-file inventory matches the retained inventory; the historical comparator still passes with content ID `44B43088658F2F70FE8A0745EA5DFC3B1519CBF10B9347F3F4B88835EEDB3B12`, manifest SHA-256 `22D05298F7329BE8C2892BDEE33EBE71C6851EBC5D7A6B6BBF2A49C34F45FE87`, and empty added/missing/modified lists. No historical manifests or payloads were rewritten. This is a semantic-conformance finding, not retroactive invalidation of their byte-integrity evidence.

**PI-01 remains FAIL for both retained manifests until a new authorized candidate is published and passes semantic validation.** The previous PI-01 validator result (`passed=true`) is retained as historical evidence of the validator gap. The 114 `hispania-slice8-*` temporary directories remain present. No production Core, frozen contract, retained artifact, or temporary directory was modified. Step 7.2 remains PARTIAL; no 7.3–7.5 or Slice 9 work started.

### PI-33 — Selección para futuro commit de procedencia (2026-10-20)

El contraste read-only confirma 77 rutas: 24 modificadas + 53 no rastreadas, 0 staged y 0 eliminadas. Se propone incluirlas todas: 15 fuentes/herramientas, 22 pruebas/fixtures/harnesses, 37 documentos/evidencias Slice 8 y 3 registros de aceptación histórica Slice 7. La lista exacta, clasificación, exclusiones, riesgos, comandos PowerShell propuestos (no ejecutados) y pruebas posteriores están en el [Published Integration Gate, apéndice PI-33](evidence/slice8/step7/published-integration-gate.md#pi-33--selección-explícita-para-commit-local-de-procedencia-2026-10-20).

Se excluyen outputs ignorados, temporales (114 conservados), artefactos publicados y material secreto/local; ninguna ruta estándar actual queda sin clasificar. Hay 14,123 rutas ignoradas y no se borró ninguna. El escaneo de marcadores sensibles dio 0 coincidencias; dos documentos contienen una ruta de perfil Windows, riesgo de privacidad previo a publicación externa.

Rama `codex/hispania-v1-baseline-consolidation`, HEAD `ef7365bb5e7d7551cb1093519743cc1885757b5b`, sin upstream. La candidata retenida coincide con su inventario 19/19 (content ID `9459242A34B042402F79D140CEED7D16BDEB149616EC7B08573188A34D31BED0`, manifest SHA `8B4196461AB0EA5A8E5BF92931EC0756839E82F82208309B00539032E83185DD`). El comparador del Core histórico pasa sin diferencias (content ID `44B43088658F2F70FE8A0745EA5DFC3B1519CBF10B9347F3F4B88835EEDB3B12`, manifest SHA `22D05298F7329BE8C2892BDEE33EBE71C6851EBC5D7A6B6BBF2A49C34F45FE87`). PI-01 sigue FAIL semántico para los manifests retenidos.

No se ejecutaron `git add`, commit, push ni publicación. PI-33 continúa NOT VERIFIED hasta que exista commit autorizado y candidata desde checkout limpio. Paso 7.2 permanece PARTIAL.
## Step 7.4 — Owner acceptance and gate reconciliation (2026-10-26)

Owner-ratified current matrix: PI-01–PI-30 PASS, PI-31/PI-32/PI-34 NOT VERIFIED (non-blocking only under limited Windows x64 scope), PI-33 PASS, PI-35 PASS, PI-36 NOT APPLICABLE. This is 32 PASS / 3 NOT VERIFIED / 1 NOT APPLICABLE. The PI matrix, blocker evidence register, evidence index and risk register are retained in the isolated consolidation package as supporting records.

The current Slice 8 candidate is Content ID `700E403E74F566B5D47E9F079907EE978EED46D3C4315DFD0D5EA43DD9BF7D18`, source commit `de790c970ba9773f667613d752a699e526fad296`, tree `9e666734baf74d2904c1ad80d471597c65e20c34`, manifest SHA-256 `1CB4D94FBC60B9BE2CE55A38317116B7B38B3D5C6C4682BBD8A693EB50C1A21B`. A read-only validator returned passed=true/issues=[]; 18 manifest-listed payload files and the manifest reconcile to 19 on-disk files. The historical Slice 7 baseline matches its pinned 19-file inventory and digest.

PI-35 owner-reported successful run is separately identified in the evidence index. It used elevated PowerShell; its test checks sentinel existence rather than content hash, and the summary does not embed the runner digest. These limits are explicit and do not expand the owner-ratified PASS claim.

Residual risk retained: historical Runtime child-memory probe exit `0xC00000FD` remains unexplained despite later passing focused and complete runs. PI-31/32/34 remain NOT VERIFIED. OOP is not a sandbox. The owner expressly accepted Step 7.4 as **PASS within the ratified scope** on 2026-10-26. PI-31/PI-32/PI-34 remain NOT VERIFIED; the historical Runtime `0xC00000FD` remains unexplained; lack of byte-identical reproducibility remains documented; OOP is not a sandbox. Step 7.5 remains NOT STARTED and Slice 8 remains IN PROGRESS.

### Step 7.5 — Final evidence and closure review (2026-10-26)

**Result: PASS.** The current PI matrix, blocker register, candidate inventory, Step 7.4 owner acceptance and EV-050 are reconciled. The 32 ratified blockers are PASS within scope. PI-31/32/34 remain NOT VERIFIED and non-blocking only within the ratified Windows x64 scope; PI-36 remains NOT APPLICABLE. Historical FAIL/SKIP and superseded statuses remain dated historical evidence.

The frozen candidate is unchanged: Content ID `700E403E74F566B5D47E9F079907EE978EED46D3C4315DFD0D5EA43DD9BF7D18`; manifest SHA-256 `1CB4D94FBC60B9BE2CE55A38317116B7B38B3D5C6C4682BBD8A693EB50C1A21B`; payload inventory 18/18 verified. The two original source worktrees are clean at commit `de790c970ba9773f667613d752a699e526fad296` and tree `9e666734baf74d2904c1ad80d471597c65e20c34`. `git diff --check` passed. No tests were repeated.

Residual risks remain documented: PI-35 ran elevated and its sentinel assertion checks existence rather than content hash; Runtime `0xC00000FD` remains unexplained; byte-identical reproducibility is NOT VERIFIED; OOP is not a sandbox; claims remain limited to the ratified Windows x64 context. **GO to request owner acceptance for formal Slice 8 closure.** Slice 8 remains IN PROGRESS pending that acceptance; Step 7.5 is complete; Slice 9 has not started.

### Formal owner acceptance — Slice 8 closure (2026-10-26)

The owner expressly accepted Slice 8 as **PASS** within the ratified Windows x64 scope and documented limitations, covering Steps 0–7.5. The 32/32 blocking PI criteria remain PASS at their recorded evidence scope; PI-31/32/34 remain NOT VERIFIED, PI-36 remains NOT APPLICABLE, and all previously recorded risk limits remain in force. **Slice 8: DONE / OWNER ACCEPTED PASS. Phase 1: IN PROGRESS. Roadmap: 4/14 — 28.6%, unchanged. Slice 9: NOT STARTED.** No tests were repeated and the candidate was not modified.