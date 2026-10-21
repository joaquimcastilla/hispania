# EV-050 — Step 3 Transaction and Concurrency Matrix

Date: 2026-10-18<br>
Scope: Phase 1, Slice 8, Step 3 — Windows x64<br>
Branch: `codex/hispania-v1-baseline-consolidation`<br>
HEAD: `ef7365bb5e7d7551cb1093519743cc1885757b5b`

## Result

**PASS — 18/18 dedicated transaction, recovery, and concurrency cases.** Full package lifecycle: 89/89 PASS. Package backup/restore: 2/2 PASS. Core: 58 PASS, 1 SKIP, 0 FAIL. Runtime: 254/254 PASS. The single Core SKIP is the pre-existing direct symlink creation test; real junction checks are recorded in the Step 1 evidence. Slice 8 remains IN PROGRESS. No Step 4 or Slice 9 work began.

Dedicated test evidence: `.artifacts/slice8-step3-20261018/transaction-concurrency.trx`, SHA-256 `06B98600B797A072BE58EF8AE66B2DFE95DD1371139CD68C05961DF371289C9B`.

## Transaction and recovery cases

| Case | Expected behavior | Result |
|---|---|---:|
| Terminate owner at `Planned` | Recover previous catalog/package generation; repeat recovery safely | PASS |
| Terminate owner at `Staged` | Recover previous generation; clean operation-owned stage | PASS |
| Terminate owner at `Draining` | Recover previous generation; no partial activation | PASS |
| Terminate owner at `Committed` | Detect incomplete activation and restore previous generation | PASS |
| Terminate owner at `Activating` | Restore previous generation; stop incomplete next generation | PASS |
| Terminate owner at `Validated` | Restore previous generation because completion was not recorded | PASS |
| Terminate owner at `RollingBack` | Finish rollback to previous generation | PASS |
| Terminate owner at `Completed` | Keep committed new generation | PASS |
| Repeat recovery and cleanup after each termination point | Idempotent recovery; no owned staging directories remain | PASS |
| Inject malformed journal bytes | Fail closed with `InvalidDataException`; preserve authoritative catalog bytes | PASS |
| Journal omits previous/next state | Fail closed with `InvalidDataException`; preserve authoritative catalog bytes | PASS |
| Journal contains a null package entry | Fail closed with `InvalidDataException`; preserve authoritative catalog bytes | PASS |
| Hold staging ownership marker during cleanup | Failed cleanup rolls package graph back and leaves recoverable owned residue; after releasing handle, recovery/cleanup removes that residue | PASS |

The full package lifecycle TRX also passes six journal checkpoint failure cases (`Planned`, `Staged`, `Draining`, `Committed`, `Activating`, `Validated`) and three storage-write failure cases (`stage-write`, `catalog-write`, `journal-write`). Each asserts the prior graph remains authoritative. It also passes a stale-plan rejection and a candidate archive mutation after planning. Evidence: `.artifacts/slice8-step3-20261018/phase19-package-lifecycle.trx`, SHA-256 `9D618C938E2237C13EF65B962E73D39FA9D682879AACDC9B49A227ABABDC3E35`.

## Concurrency cases

| Case | Coordination and assertion | Result |
|---|---|---:|
| Two processes install different versions of the same module | Both plan from one generation; one commits and one is rejected as stale; installed graph is complete | PASS |
| Two processes install different modules | One stale loser cannot overwrite winner; explicit re-plan commits both modules without loss | PASS |
| Concurrent install and removal | Exactly one stale-plan outcome or valid serialization; final graph is one complete allowed generation | PASS |
| Concurrent update and enable policy | Catalog reader sees one complete generation; enabled and healthy state agree after recovery | PASS |
| Reader during publication | At `Committed`, API reports `RecoveryRequired` and exposes the complete committed generation whose files match package manifest/hashes | PASS |
| Recovery while another process owns the mutation lock | Recovery waits; after the test-owned owner is killed, it obtains the lock and completes rollback | PASS |

Each child process is created by the test, uses the same isolated package-store root and test-only public publisher keys, and is released through a shared barrier after its plan is prepared. No production process is stopped by these tests.

## Findings and change

The existing mutation path already serialized writers using a per-instance semaphore plus an exclusive cross-process `mutation.lock`, rejected stale plans after lock acquisition, staged and verified package bytes before publication, atomically replaced the catalog file, and recovered incomplete journal states by restoring the recorded prior set. No new lock, transaction format, public endpoint or persistence model was needed.

The demonstrated gap was validation of malformed journal graphs. Missing `Previous`/`Next` records or null graph entries could escape validation and become incidental null-reference failures. `PackageSetLifecycle.ValidateJournal` now rejects null prior/next graphs; `ValidateStoredGraph` rejects a null state, null package array, null entry/manifest and invalid digest. This produces controlled fail-closed `InvalidDataException` outcomes before mutation or recovery can publish state.

The process test harness now supports synchronized independent mutations. Its barrier diagnostics no longer eagerly await process output while the workers are intentionally blocked; checkpoint wait diagnostics have the same conditional behavior. One journal-corruption test now checks catalog-byte preservation without invoking a module through a corrupted journal. The process-termination matrix asserts the updated package version separately from the unchanged dependent package version.

## Broader regression evidence

| Suite | Result | TRX SHA-256 |
|---|---:|---|
| Dedicated transaction/concurrency | 18 PASS / 0 FAIL / 0 SKIP | `06B98600B797A072BE58EF8AE66B2DFE95DD1371139CD68C05961DF371289C9B` |
| Package lifecycle and historical reader | 89 PASS / 0 FAIL / 0 SKIP | `9D618C938E2237C13EF65B962E73D39FA9D682879AACDC9B49A227ABABDC3E35` |
| Package backup/restore | 2 PASS / 0 FAIL / 0 SKIP | `AA541D311B24C30BDE017E3C73DEA83CA5C867B91B1E028A80D7FA29186631F0` |
| Core (`net10.0/win-x64`) | 58 PASS / 0 FAIL / 1 SKIP | `162DFAEB39381F89CC2898E601284927E5EEC622E2183142A79A820661BA9556` |
| Runtime | 254 PASS / 0 FAIL / 0 SKIP | `F37D7CE05B6C5CB7327424CD21A636812A837DD5A0CFDDE1321A7961C2D95E23` |

The Runtime suite's first run had one memory-resource probe exit-code mismatch (`-1073741571` instead of the probe's expected `42`). The isolated resource-probe suite then passed 2/2 and the complete Runtime rerun passed 254/254. The transient first-run result is retained with the console/run history; no production resource policy was changed.

Artifact evidence: [after-test comparison](step3/artifact-after.json), SHA-256 `64A73CACB74F0B05BD679B72A90365FA5164EFF376B6AF9EDE65500FE79CD5FE`; [comparator self-test](step3/artifact-comparator-selftest.json), SHA-256 `3E2A09CA974C982BAD3DF996514836227E0C52900C2CF56F6C1BFB487DEE9B2F`. Comparison returned `added=[]`, `missing=[]`, `modified=[]` with content ID `44B43088658F2F70FE8A0745EA5DFC3B1519CBF10B9347F3F4B88835EEDB3B12` and manifest SHA-256 `22D05298F7329BE8C2892BDEE33EBE71C6851EBC5D7A6B6BBF2A49C34F45FE87`. Self-test detected added, deleted, and modified files and cleaned its uniquely named temporary copy. The allowlist validator returned `passed=true`, `issues=[]`.

## Limits

- The process-kill tests terminate workers at durable journal checkpoints. They do not simulate power loss or termination during an arbitrary instruction inside sidecar serialization or filesystem replacement.
- Atomic rename and flush behavior is exercised on this Windows x64 local filesystem. No cross-filesystem or network-filesystem guarantee is inferred.
- StoragePathGuard still does not claim protection from an external actor racing to replace filesystem components (TOCTOU).
- No changes were made to frozen contracts, SQLite schema, package signature format, publisher policy, accepted Core artifact or public APIs.
- No commit, push, merge, rebase, release, cleanup of earlier evidence, Step 4 or Slice 9 work was performed.
