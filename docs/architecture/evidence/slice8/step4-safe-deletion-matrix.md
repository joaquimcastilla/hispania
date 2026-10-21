# EV-050 — Step 4 Safe Deletion and Ownership Matrix

Date: 2026-10-18<br>
Scope: Windows x64, test-owned temporary roots only. The accepted Core publish is compared read-only before and after.

## Ownership rules

- A package version directory is deletable only when it is inside the configured module store, contains a valid `module.json`, passes package-manifest validation and payload SHA-256 verification, and its complete file inventory is exactly `module.json` plus the files listed by that manifest. Windows inventory comparison is case-insensitive.
- Package identity is the validated module ID plus package version directory. Current and `previous.json` package graphs are protected from retention. Loaded/registered versions block uninstall or cleanup.
- Retained `.hispania` artifacts are owned by their content-addressed SHA-256 filename only after ZIP inspection confirms that digest; package deletion additionally requires the installed manifest to match the inspected artifact.
- A staging directory is eligible for retention only when it is directly beneath the managed staging root, its directory name is a GUID in `N` format, and a non-reparse `ownership.marker` contains exactly that GUID. Missing, unreadable, malformed, or path-like markers do not authorize deletion.
- Unknown files in package-version directories block deletion. Unrelated files outside a package-owned version directory are not enumerated as candidates. No filesystem check here claims complete protection against TOCTOU races.

## Matrix

| # | Scenario | Expected | Evidence/test | Result |
|---:|---|---|---|---|
| 1 | Valid inactive package uninstall | Delete verified owned files | `IndependentlyBuiltGreetingPackageInstallsLoadsUpdatesRollsBackDrainsAndUnloads` | PASS |
| 2 | Active/registered package uninstall | Reject before deletion | `RecursivePackageRemovalRejectsJunctionAndPreservesBothTrees`; `SignedGraphInstallsInertThenEnablesInOrderAndBlocksIndividualUninstall` | PASS |
| 3 | Different modules share a store | Cleanup one generation without deleting another module | `CleanupRetainsCurrentAndPreviousButRemovesOlderOwnedBytes` | PASS |
| 4 | Older generation referenced by known-good graph | Preserve for rollback | `CleanupRetainsCurrentAndPreviousButRemovesOlderOwnedBytes`; `RetentionPlanPrunesV1ButPreservesV2KnownGoodForRollback` | PASS |
| 5 | Foreign file inside package-version directory | Fail closed; preserve file and package | `UninstallRejectsUnknownFileAndPreservesItsCanary`; `RetentionRejectsUnknownFileAndPreservesPackageAndCanary` | PASS |
| 6 | Missing staging ownership marker | Preserve staging | `CleanupPreservesStagingWithMissingCorruptOrManipulatedOwnershipMarker[missing]` | PASS |
| 7 | Corrupt staging ownership marker | Preserve staging | Same theory `[corrupt]` | PASS |
| 8 | Marker contains external path | Preserve staging and canary | Same theory `[outside-root]` | PASS |
| 9 | Junction inside deletion tree | Reject before recursive delete | `RecursivePackageRemovalRejectsJunctionAndPreservesBothTrees` | PASS |
| 10 | Junction targets external files | External file hashes unchanged | Same Core test's owned junction helper snapshots target hashes before and after | PASS |
| 11 | Similar-prefix path outside allowed root | Reject path | `StoragePathGuard` path-boundary tests in `CorePersistenceTests` | PASS |
| 12 | Interrupted/locked staging cleanup | Leave recoverable staging; retry cleanup | `LockedStagingCleanupRollsBackAndLeavesRecoverableOwnedResidue` | PASS |
| 13 | Repeated cleanup | Second call is idempotent | `CleanupRetainsCurrentAndPreviousButRemovesOlderOwnedBytes` (calls cleanup twice) | PASS |
| 14 | Missing package uninstall | Return not-found without creating/deleting files | `UninstallOfMissingPackageIsIdempotent` | PASS |
| 15 | Foreign/unmarked staging residue | Leave untouched | Marker theory cases plus existing staging cleanup test | PASS |
| 16 | Recovery after deletion failure | Retry removes only verified owned residue; authoritative set remains valid | Locked staging cleanup + recovery test | PASS |
| 17 | Tampered package payload | Reject integrity before deletion | `ValidateBackupPackage` SHA verification; package integrity adversarial tests | PASS |
| 18 | Package directory has altered/mismatched manifest | Reject before deleting package or retained artifact | `EnsurePackageFilesAreOwned` and retained-artifact manifest comparison; exercised by package lifecycle regression | PASS |

## Implementation change

Before uninstall, cleanup, or retention recursively removes a package version directory, the runtime now reads and validates its manifest, verifies listed payload hashes, and requires an exact file inventory. Retention also requires the installed manifest to match the content-addressed retained artifact. This is a fail-closed ownership check; it does not alter package signatures, trust policy, persisted formats, or public contracts.

## Test-owned data and external canaries

Foreign-file tests create canaries only under unique temporary package fixtures and record/compare SHA-256 around failed operations. Junction target hash snapshots are owned by the existing Core test helper. The tests remove only their own temporary roots or junction links. No owner store, accepted publish, or prior evidence directory is used as a deletion target.

## Regression and artifact verification

- Focused deletion tests: **6/6 PASS**; `.artifacts/slice8-step4-20261018/deletion-matrix.trx`, SHA-256 `98F20C29228FA102313E680D6417002C7AE16A3C67DD54108D3A176805B41A98`.
- Core: **59 PASS / 1 SKIP / 0 FAIL**; `.artifacts/slice8-step4-rerun-20261018/core-final.trx`, SHA-256 `4E33657632864CD4C28E9B1C0DEAF75AEB1FBF1EF08A70B3682D3879BF7ED0FC`.
- Slice 7 fixture lifecycle: **4/4 PASS**; `.artifacts/slice8-step4-20261018/slice7-fixture.trx`, SHA-256 `1CEE5FBC6117992C00BF63C8CA2C0978314800C21610D62DB25B46AEA0900977`.
- Package lifecycle + historical reader: **94/94 PASS**; `.artifacts/slice8-step4-rerun-20261018/phase19-package-lifecycle.trx`, SHA-256 `C805E338F08067CF224545DF3E7142E38B63DBA11DFF67C1D434A36BD6416312`.
- Package backup/restore: **2/2 PASS**; `.artifacts/slice8-step4-rerun-20261018/phase19-package-backup.trx`, SHA-256 `AC912F487FC689954C8934E7F4C7227FE1A4C599D9FB52D9C2E64184576068CB`.
- Full Runtime (including publisher trust): **259/259 PASS**; `.artifacts/slice8-step4-20261018/runtime-final.trx`, SHA-256 `94D445CD63B5EF33C8C5DF2B6F3F732DCF90D39E06B5EA5E5146212DFF392D9D`.
- Accepted artifact before/after snapshots: each SHA-256 `64A73CACB74F0B05BD679B72A90365FA5164EFF376B6AF9EDE65500FE79CD5FE`; content ID and manifest hash unchanged; `added=[]`, `missing=[]`, `modified=[]`. Artifact validator: `passed=true`, `issues=[]`.
- Comparator self-test detected added, deleted, and modified files and cleaned its own copy; evidence SHA-256 `3E2A09CA974C982BAD3DF996514836227E0C52900C2CF56F6C1BFB487DEE9B2F`.
- `git diff --check`: PASS. No publish rebuild, commit, push, evidence deletion, Step 5, or Slice 9.

Scope remains Windows x64. The direct symlink creation test remains SKIP for the recorded privilege limitation; actual junction tests pass. TOCTOU replacement races, Linux and ARM64 remain unverified.
