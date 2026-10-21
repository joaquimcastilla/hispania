# Slice 8 Step 2 — Adversarial Extraction Matrix

Date: 2026-10-18<br>
Scope: Windows x64, synthetic small ZIP fixtures, test builds only. The accepted Core publish was not used as a test target or modified.

## Coverage

The matrix test is `Hispania.Runtime.Tests.PackageArchiveAdversarialTests`. Its 22 cases passed. Rejection scenarios run through both `PackageArchiveInspector` and `ExtensionPackageInstaller`; post-rejection checks confirm the install destination was not published. The canary test hashes a file outside the install root before and after traversal attempts.

| Attack class | Existing coverage before Step 2 | Step 2 coverage | Result |
|---|---|---|---|
| `../` traversal | Inspector basic traversal | Installer and inspector; outside sentinel hash | PASS |
| Absolute POSIX, drive and UNC paths | Partial inspector coverage | Both preflights | PASS |
| Backslash and mixed separators | Basic backslash case | Backslash and mixed separator variants through both preflights | PASS |
| Exact duplicate entries | Not explicit | Duplicate payload and duplicate manifest names | PASS |
| File/directory topology collision | Not explicit | `node` plus `node/child.bin`; rejected before staging | PASS |
| Windows case-insensitive file collision | Inspector-only | Inspector and installer | PASS |
| Symlink entries | File symlink guard existed | File and explicit directory symlink attributes | PASS |
| Reserved/ambiguous Windows names | Installer path validation | Shared inspector/installer validation; `CON`, `COM1`, `COM¹`, ADS, trailing dot/space | PASS |
| Truncated/corrupt ZIP | Corrupt/random archive partial coverage | Truncated central directory and malformed bytes, both entry points | PASS |
| Inconsistent declared sizes | Forged manifest length coverage | Forged expanded length over limit and payload declared-length mismatch | PASS |
| Excess entries | Limit existed, no focused test | 513-entry synthetic archive | PASS |
| Disproportionate expansion / expanded limit | Limit existed, no focused test | Patched declared expansion of 256 MiB + 1; no large file or inflation | PASS |
| Compressed package upper bound | Streaming 128 MiB guard existed | Code-reviewed; not driven to the 128 MiB threshold to avoid a memory-heavy fixture | NOT RUN |
| Payload hash mismatch | Slice 7 tampered-payload rejection | Installer fails after staging write, with no final install | PASS |
| Invalid/inconsistent manifest | Existing negative manifest checks | Invalid JSON and payload inventory mismatch through both preflights | PASS |
| Mid-extraction failure and cleanup | Lifecycle integrity checks | Hash failure leaves no staging/new package and preserves prior installed snapshot | PASS |

The 22 test cases comprise 13 unsafe-path variants and nine focused facts covering duplicate/case collisions, topology and unsafe directory entries, file symlink, count limit, expanded limit, declared-length mismatch, truncation/corruption, manifest/inventory mismatch, and extraction hash-failure cleanup.

## Regression results

| Suite | Result | Evidence |
|---|---:|---|
| Adversarial matrix | 22 PASS / 0 FAIL / 0 SKIP | `adversarial-matrix.trx`; SHA-256 `B15A8C282B5E9CD5649CF206F7E9CD8B382BE0AB4B52BA1BC5CEA3BA52492DD2` |
| StoragePathGuard focused | 6 PASS / 0 FAIL / 1 SKIP | `storage-path-guard.trx`; SHA-256 `89E03132794DB80F32012AD085FDB657C15D6EE0BE7709EEC9ECBA1CAC4B136A` |
| Core full suite | 58 PASS / 0 FAIL / 1 SKIP | `core-full.trx`; SHA-256 `C3C4102A4ABCE5873E391C7E103B48D19DF7FD2BB716B262083E044E0450EB9C` |
| Slice 7 fixture lifecycle | 4 PASS / 0 FAIL / 0 SKIP | `slice7-fixture.trx`; SHA-256 `9D1D2C775A32B59949876E036FF3E335B5979F11C284EE87BA80B6832F1ED31E` |
| Publisher trust focused | 1 PASS / 0 FAIL / 0 SKIP | `publisher-trust.trx`; SHA-256 `1050C2FDE0FD28B8328E172D81ECB24086FA5F833F9F09194AFF3AE723F36048` |
| Runtime full suite | 241 PASS / 0 FAIL / 0 SKIP | `runtime-full.trx`; SHA-256 `FC0508D093BA6F09446EF19B5726083ED59BF6B1D599DBB37FA01E5712DFE942` |
| Package lifecycle | 76 PASS / 0 FAIL / 0 SKIP | `phase19-package-lifecycle.trx`; SHA-256 `96AA6A446F934624697D1573D2A0E7D526B906D857A9D32D5F77CDBF97324715` |
| Package backup/restore | 2 PASS / 0 FAIL / 0 SKIP | `phase19-package-backup.trx`; SHA-256 `221567E0B9BF456759CD1C6A1D83D4E781D4968C8BE37053C1BE1393203D9EED` |

TRX files are retained in `.artifacts/slice8-step2-final-20261018/` and `.artifacts/slice8-step2-retry-20261018/`. No pre-existing temporary evidence was removed. The one Core/StoragePathGuard SKIP is `StoragePathGuardDetectsLinksWithoutTraversingOrDeletingTheirTargets`, the direct symlink creation test blocked by the Windows token privilege; five junction-based checks are separately passing from Step 1.

## Accepted artifact integrity

The before and after calls to `tools/verify-slice8-artifact-baseline.ps1` passed. The after result was `added=[]`, `missing=[]`, `modified=[]`, `selfTest=[]`; content ID `44B43088658F2F70FE8A0745EA5DFC3B1519CBF10B9347F3F4B88835EEDB3B12`; manifest SHA-256 `22D05298F7329BE8C2892BDEE33EBE71C6851EBC5D7A6B6BBF2A49C34F45FE87`. The allowlist validator returned `passed=true`, `issues=[]`. No accepted publish file changed.

## Security limits

Extraction path validation rejects traversal, rooted names, alternate separators, Windows reserved names, duplicate/colliding entries and symlink attributes before staging. Payload extraction uses create-new files under a unique guarded staging directory; the package is published only after actual hashes and the managed assembly check pass. Failed extraction checks staging for reparse points and removes that private staging tree. The tests prove rejection and cleanup for these synthetic cases, not protection against a concurrent process swapping filesystem components after checks (TOCTOU). The 128 MiB compressed-stream threshold was code-reviewed but not exercised at full size; the 256 MiB expansion threshold was tested using synthetic ZIP metadata rather than inflating a large payload. No claim is made beyond the tested Windows x64 scope.
