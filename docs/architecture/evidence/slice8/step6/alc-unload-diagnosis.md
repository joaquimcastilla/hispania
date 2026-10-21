# EV-050 Step 6 — Collectible ALC unload diagnosis

Date: 2026-10-18<br>
Scope: Windows x64, .NET 10, test-owned fixture roots only.

## Finding

`ExtensionLease.Dispose()` clears its contract reference before invoking the lease-release callback. `ModuleExtensionCatalog.UnloadAsync` deactivates and unregisters the module, removes its `LoadedExtension`, calls `LoadedExtension.Unload()` (`Module = null`, `Context.Unload()`, `Context = null`), then waits for the collectible context weak reference to die. The focused in-process test observes the first context weak reference as dead. A second explicit weak-reference check was added for v2 and also completed. The test is sequential and owns a unique root; no HISPANIA Core or ModuleHost processes remained after the test runs.

Package creation uses in-memory streams and disposes them. The installer disposes extraction streams. An exclusive read-open probe of every extracted DLL succeeded after both ALC weak references were dead, but recursive deletion still failed on the v2 `Hispania.PrivateFixture.Dependency.dll` with `UnauthorizedAccessException`, HRESULT `0x80070005`. After the testhost exited, the same retained file could be opened exclusively. File attributes were `Archive`; its inherited ACL showed owner Full Control and sandbox-user Modify with no explicit deny. The ACL/attribute checks happened after the failure and do not establish what denied the operation at that instant.

This rules out a normal incompatible `FileStream` share handle at probe time, but does **not** identify the Windows component or exact access rule that denied recursive deletion. A mapped-image/delete-sharing restriction, filesystem filter, or another delete-specific restriction remains unproven. No root cause is asserted.

The error also appears in two in-process missing/incompatible-dependency cases when cleanup runs without retries. Inspection found that `LoadAndStartAsync` calls `candidate.Unload()` in its startup-failure path but does not await the existing weak-reference unload condition. A temporary local change to await it did not remove those cleanup failures and was reverted. It is not recorded as a demonstrated root cause or retained product change.

## Diagnostic-only test changes

`PrivateModuleDependencyIsolationTests` now runs the catalog/lease work in a separate no-inline helper frame, observes the v2 collectible context through a weak reference, probes package DLLs with `FileShare.None` after unload, and reports the owned temp root and HRESULT on cleanup failure. The helper-frame change did not resolve deletion. No contract, signature format, runtime policy, Core, ModuleHost, or published artifact was changed. No arbitrary delay was added.

## Results

| Validation | Result | Evidence |
|---|---:|---|
| In-process unload repetitions | 0/10 PASS, 10/10 FAIL at owned-root deletion | `.artifacts/slice8-step6-alc-investigation/final-repeats/alc-inprocess-final-*.trx`; all report `0x80070005` on the private dependency DLL after context collection checks. |
| OOP focused case | 1/1 PASS | `.artifacts/slice8-step6-alc-investigation/alc-oop.trx`, SHA-256 `7A5E885917E1473A99627B2FBBB3D03511C187D59077C85B9A0564E1BE545BAC`. |
| Dependency isolation class | 5 PASS / 1 FAIL | `.artifacts/slice8-step6-alc-investigation/alc-isolation-suite.trx`, SHA-256 `165B8CC2F2B43E93427ADB35A28B6B8D51C19FB5E3430D55585833F317EFAB6E`. |
| Core (`win-x64`) | 62 PASS / 1 SKIP / 0 FAIL | `core/core.trx`, SHA-256 `F843B6B4FA431AD51DA7C342D8C68EBD1F15B0BFDEE27437E7D9DC27D93D6949`; existing symlink-privilege SKIP. |
| ModuleHost | 10/10 PASS | `modulehost/modulehost.trx`, SHA-256 `60CFA62D335C0FE775EF00B4C2FB6B5B8F11E38A51E5CC2F2DA84FF687E935AC`. |
| Package Lifecycle + historical reader | 94/94 PASS | `package-lifecycle/phase19-package-lifecycle.trx`, SHA-256 `AD91C3051329285EBD0B59F9D6B7C6E6E175DF51093A15645F06483DF122A4AD`. |
| Package backup/restore | 2/2 PASS | `package-lifecycle/phase19-package-backup.trx`, SHA-256 `06370459C1C90177BADF4DC779FA7DFFB867BC350A8AB363229ED169A4647BFA`. |
| Full Runtime, current test diagnostics | 262 PASS / 3 FAIL / 0 SKIP | `runtime-current/runtime-final-current.trx`, SHA-256 `8276DA1DC851F00DEE604EF4FC273C2152E34FF4AAB7F55C40C726ED4B47121C`. Failures are the positive unload case and two in-process failed-activation cleanup cases. |

The earlier full Runtime run was 264 PASS / 1 FAIL. Removing the old retry loop from cleanup diagnostics exposed two in-process startup-failure cleanup cases that those retries had hidden. The current failing outcomes are retained, not reported as passing. The ten repetition TRX hashes are listed in `.artifacts/slice8-step6-alc-investigation/trx-hashes.json`.

## Historical artifact and temporary roots

The accepted Slice 7 artifact comparison passed before and after: content ID `44B43088658F2F70FE8A0745EA5DFC3B1519CBF10B9347F3F4B88835EEDB3B12`, manifest SHA-256 `22D05298F7329BE8C2892BDEE33EBE71C6851EBC5D7A6B6BBF2A49C34F45FE87`, and `added=[]`, `missing=[]`, `modified=[]`. Outputs are in `.artifacts/slice8-step6-alc-investigation/historical-before.json` and `historical-after.json`. The historical artifact and prior candidate were not written.

The temp inventory currently contains 56 `hispania-slice8-private-deps-*` roots. It is recorded in `.artifacts/slice8-step6-alc-investigation/temp-inventory.json`. The 18 older roots and four roots present before this investigation were not removed. Newly created test roots were also preserved when cleanup failed. No alternate destructive cleanup was attempted. No Core or ModuleHost process remained after the runs.

## Disposition

**ALC unload substep: PARTIAL.** Collectible ALC weak-reference checks pass and OOP passes, but Windows recursive deletion remains reproducibly denied in-process. The exact delete-denial cause is not proven, and the Runtime suite is not green. Further work needs a Windows delete-handle/Restart Manager trace during the failing test or an isolated test-host lifecycle that establishes when the delete restriction clears. Step 6 remains PARTIAL; no Step 7 or Slice 9 work was started.

The three current full-Runtime failures are:

- `MissingOrIncompatiblePrivateDependencyFailsActivationWithoutRegistration(useIncompatibleDependency: false, runtime: dotnet-inprocess-trusted)` — cleanup denied access to `Hispania.PrivateFixture.ModuleV1.dll`.
- `MissingOrIncompatiblePrivateDependencyFailsActivationWithoutRegistration(useIncompatibleDependency: true, runtime: dotnet-inprocess-trusted)` — cleanup denied access to `Hispania.PrivateFixture.Dependency.dll`.
- `PackagesResolveSamePrivateAssemblyIdentityIndependentlyAndUnloadAfterLeaseRelease(runtime: dotnet-inprocess-trusted)` — cleanup denied access to `Hispania.PrivateFixture.Dependency.dll`.

All three report `0x80070005`; the exact exception messages and stack traces are retained in the final Runtime TRX.
`r`n
## Windows delete-denial comparison — 2026-10-18

A diagnostic-only .NET 10 harness was built under ignored `.artifacts/slice8-step6-windows-delete-diagnostic-69198b4754ed4c749967c1c9f38906b0/` and run against a new unique `%TEMP%\hispania-slice8-delete-diagnostic-*` root. It loaded both private dependency versions into separate collectible ALCs, loaded the actual v1/v2 fixture modules with `AssemblyDependencyResolver`, checked `WeakReference.IsAlive`, opened each DLL with `FileShare.None`, queried Restart Manager, called native `DeleteFileW` to capture the Win32 result, then exercised recursive cleanup. Scenario JSON: `diagnostic-result.json`, SHA-256 `49D55F39186FFAAC6FA02DF5211627043FEA4E1DE42DA71FF1F6CE15F25D22F4`; harness source SHA-256 `819601F128F263324672975ACD800D621B28A0953A1355BE748DD93322E5444C`.

| Isolated scenario | Result |
|---|---|
| A. Same process: two collectible contexts load v1/v2 private dependency assemblies, release references, unload, confirm both weak references dead, exclusive-open and delete | PASS; both native `DeleteFileW` calls succeeded, recursive cleanup succeeded |
| A2. Same process: load actual fixture module v1/v2 through `AssemblyDependencyResolver`, invoke fixture entry method, unload and confirm collection | PASS; both dependency DLLs opened exclusively and deleted; recursive cleanup succeeded |
| B. Child loads both dependency versions, unloads and exits 0; parent opens and deletes | PASS; both deletes succeeded; no Restart Manager process reported |
| C. Copy both dependency DLLs without loading, open and delete | PASS |
| D. Ordinary data file control, open and delete | PASS |

The fresh reproductions observed no `ACCESS_DENIED`; native error and HRESULT fields are null because `DeleteFileW` succeeded. Restart Manager returned no process for each file. Thus the earlier managed `UnauthorizedAccessException` HRESULT `0x80070005` corresponds to Win32 `ERROR_ACCESS_DENIED` (5), but the native operation that returned it was not captured during the original failure. The current harness demonstrates that neither collectible ALC loading nor an ordinary file's filesystem conditions inherently produce the denial; it does not identify a transient condition specific to the failing testhost.

Read-only inspection of a retained prior failure root found the named dependency DLL with `Archive` attributes, NTFS volume, owner `JOAQUIM\joaqu`, inherited `Modify` for the test user group and inherited `FullControl` for the owner; no deny ACE was present. This inspection was after the recorded failure and cannot establish ACL state at failure time. Process Monitor and `handle.exe` were not installed; no installation, elevation, ACL change, Defender change, or Windows configuration change was made. No competing handle was reported by Restart Manager at reproduction time; ordinary exclusive-open success does not rule out every filesystem filter or delete-specific race.

The original failing operation remains recursive `Directory.Delete` in test cleanup, with the exception identifying `Hispania.PrivateFixture.Dependency.dll`; previous evidence records 0x80070005. The direct native `DeleteFileW` attempt failed in no fresh scenario, so exact failing NT/Win32 syscall for the original event remains **NOT VERIFIED**. No production correction is justified by this evidence, and no arbitrary waits/retries or test exclusions were added. The new empty, test-owned diagnostic root was removed after confirming it contained no entries. The 56 historical `hispania-slice8-private-deps-*` roots remain untouched.

Historical accepted Core baseline comparison after this diagnostic: PASS; content ID `44B43088658F2F70FE8A0745EA5DFC3B1519CBF10B9347F3F4B88835EEDB3B12`, manifest SHA-256 `22D05298F7329BE8C2892BDEE33EBE71C6851EBC5D7A6B6BBF2A49C34F45FE87`, `added=[]`, `missing=[]`, `modified=[]`. Baseline result SHA-256: `64A73CACB74F0B05BD679B72A90365FA5164EFF376B6AF9EDE65500FE79CD5FE`.

Disposition remains **PARTIAL**. No correction was made because the root cause was not demonstrated. Existing regression evidence remains 10/10 in-process unload cleanup failures, OOP PASS, Core 62 PASS / 1 known symlink SKIP, ModuleHost 10/10 PASS, Package Lifecycle 94/94 PASS, backup/restore 2/2 PASS, and full Runtime 262 PASS / 3 FAIL (latest retained TRX in the prior section). Per the conditional validation instructions, no regressions were rerun because no correction was justified.

## Testhost-original diagnosis — 2026-10-19

The failure was reproduced in the original Runtime testhost and captured at the `Directory.Delete(root, recursive: true)` failure. The concrete operation was recursive removal of the loaded package image: missing-dependency activation denied deletion of `Hispania.PrivateFixture.ModuleV1.dll`; incompatible-dependency activation denied deletion of `Hispania.PrivateFixture.Dependency.dll`. Managed exception: `UnauthorizedAccessException`, HRESULT `0x80070005`, Win32 mapping `ERROR_ACCESS_DENIED` (5); stack: `System.IO.FileSystem.RemoveDirectoryRecursive` → `System.IO.FileSystem.RemoveDirectory` → test cleanup.

At the failure instant both cases showed a live collectible ALC weak reference and the fixture assembly still present in `AppDomain.CurrentDomain.GetAssemblies()` under that ALC. The incompatible case showed the module and private dependency loaded in the same collectible context. `CreateFileW` with `DELETE` access and all share flags opened each implicated file; the no-sharing `GENERIC_READ` probe also opened; the parent `FILE_DELETE_CHILD` probe opened. Attributes were `Archive`, matching ACLs showed inherited `FullControl` for the test token's owner SID, and no ModuleHost process was present. These checks rule out a simple missing DELETE ACL and a conventional conflicting file handle. The CLR recursive delete still returned access denied while the image's collectible ALC was alive.

The production path explains the timing: `ModuleExtensionCatalog.LoadAndStartAsync` catches startup failure and calls `candidate.Unload()` but does not wait for the returned ALC weak reference to die before `ActivateAsync` returns the failure. The test immediately enters `finally` and deletes the owned package. For both failure fixtures, `StartAsync` loads/uses the private dependency then throws (missing file or `MissingMethodException`). The ALC remained alive at cleanup. This is reproducible evidence of an asynchronous failed-activation unload race; the exact lower-level Windows image-section disposition call is not exposed by `Directory.Delete`, so no kernel syscall claim is made.

A separate test-only defect was found in the positive unload case: the test retained a strong `secondContext` local while calling `UnloadAsync`, which waits for collection. Clearing that reference before the wait fixed the test's own liveness condition. The corrected positive in-process case passed 10/10 fresh testhost runs. This test correction does not resolve the production failed-activation path.

| Run mode | Result |
|---|---|
| Positive in-process unload, individual fresh testhosts | 10/10 PASS after test-only reference release fix |
| Missing dependency, individual fresh testhost | 1 FAIL with live ALC / `0x80070005` |
| Incompatible dependency, individual fresh testhost | 1 FAIL with live ALC / `0x80070005` |
| Same testhost, selected class cases sequentially | positive case PASS; missing and incompatible cases FAIL (2 failures) |
| Full Runtime, default xUnit parallelism | 264 PASS / 1 FAIL / 0 SKIP; the incompatible in-process case failed with the same live-ALC diagnostic |
| Full dependency-isolation class | 4 PASS / 2 FAIL; the two failed activation cleanup cases above |
| Core | 62 PASS / 1 existing symlink privilege SKIP / 0 FAIL |
| ModuleHost-filtered Runtime tests | 10 PASS / 0 FAIL / 0 SKIP |

The full Runtime run's single failure under normal parallelism, compared with two failures when the affected cases ran alone or sequentially, demonstrates timing sensitivity; it does not implicate cross-test interference. Prior isolated harness scenarios continued to pass because they explicitly completed ALC release or exited the child process before deletion.

Raw TRX and console results are under ignored `.artifacts/slice8-step6-testhost-diagnostic-20261019/`. The full-runtime TRX SHA-256 is `0AD8C19A869AEB42DF828359E8DD8F9303E323AF8FDE9C89D79BCA74ED7C0769`. The Core and ModuleHost outputs are retained there as well. Historical baseline recheck passed after the runs: content ID `44B43088658F2F70FE8A0745EA5DFC3B1519CBF10B9347F3F4B88835EEDB3B12`, manifest SHA-256 `22D05298F7329BE8C2892BDEE33EBE71C6851EBC5D7A6B6BBF2A49C34F45FE87`, `added=[]`, `missing=[]`, `modified=[]`; result SHA-256 `64A73CACB74F0B05BD679B72A90365FA5164EFF376B6AF9EDE65500FE79CD5FE`.

The initial inventory had 56 roots; all 56 remain. Eleven additional roots from diagnostic failures remain untouched: `065c8682cf304455bd9eea9917d055f0`, `2b32f5abaae145879d53d3e724f33dbf`, `48900d7759b74c9daed016ce127f8d47`, `a399c713956a460e8d4cbeb76d2b31ff`, `b389dd23a0434bdeb72bcafbad6d82a7`, `ba0c1c2262df4925b071867c1570075e`, `c18676f404e54179b5cef1b723336247`, `dbbe216d5b344962a57dd31d1d8b1036`, `dc45d24c95fa4397b7aeb05275ea2a22`, `e76783ab631143a99170f469722ecc06`, and `ee919bf15d0c4016b7d9bc911f73d06b`. Total current count: 67. No historical or diagnostic failure root was cleaned.

**Disposition:** the cause is now localized to failed-activation cleanup returning while its collectible ALC remains alive. The minimal production lifecycle correction needs to ensure the failed candidate's ALC is no longer alive before the failure returns (using a condition-based unload check, not a fixed sleep). Production code was not changed; owner authorization is required before applying that correction. Step 6 remains PARTIAL pending that authorization and verification.

TRX SHA-256 details: dependency isolation class `1BB2CB22DD923941F3F64D4022FAE00A015CEA17D9E8DCEC9035F8663AD983F6`; Core `EE2AB43DF40DF96E83C451A9CB043962E887103986AE18233A6E948462BB61DB`; ModuleHost filter `4F1CEF03D37814E6D8B9D438AC676D82C64BD69886E23926D1D9038CD190F856`; incompatible case with DELETE/exclusive-open probes `12A53E0889F4E1A09E82E4D8C01686CF7685DBA28F4B383E86A9DFBA45C0E723`.

## Failed-activation exception lifetime verification — 2026-10-19

The differential reproduction identified the retained reference: the test kept the thrown `ModuleStartupException` alive through its `finally` cleanup. `ModuleRuntime.StartModuleAsync` wraps the fixture startup exception as `InnerException`; its stack metadata points into the collectible fixture assembly. While that exception local remained live, the ALC weak reference remained alive and deleting the loaded package image failed with `UnauthorizedAccessException` / `0x80070005`. After the test asserted the expected inner exception and cleared its local reference, the bounded test-side weak-reference collection check passed and recursive cleanup succeeded. This isolates the earlier failure to test teardown running while the reported activation exception still retained the collectible context.

`ModuleExtensionCatalog.LoadAndStartAsync` already calls `candidate.Unload()` on this failure route. `LoadedExtension.Unload()` disposes the module, clears its `Module` and `Context` references, and requests `AssemblyLoadContext.Unload()`. Collection is eventual while a caller retains an exception whose stack references the module. The investigation did not add production GC or a wait before rethrowing: such a wait cannot finish while that original exception remains live. Preserving the same activation exception and guaranteeing that its referenced ALC has already been collected before returning are incompatible without changing the activation-error contract. No public contract or exception behavior was changed.

The test now validates the failure type first, releases its exception reference, verifies collectible ALC death through `WeakReference`, and only then removes its own temporary package tree. This preserves the production exception and treats unload completion as the existing eventual CLR behavior. Positive unload, missing dependency, and incompatible dependency cases each passed 10/10 individual fresh testhosts. The complete isolation class passed 6/6; OOP isolation passed 3/3; full Runtime passed 265/265. Other regressions: Core 62 PASS / 1 existing symlink SKIP; ModuleHost 10/10; Package Lifecycle 94/94; Phase 17 backup/restore 46/46; Phase 19 package backup/restore 2/2; publisher trust 32/32. TRX files, per-run digests, and the run summary are under ignored `.artifacts/slice8-step6-alc-correction-20261019/`; the machine-readable digest index is [ALC correction results](alc-correction-results-20261019.json).

Historical Core comparison passed before and after with content ID `44B43088658F2F70FE8A0745EA5DFC3B1519CBF10B9347F3F4B88835EEDB3B12`, manifest SHA-256 `22D05298F7329BE8C2892BDEE33EBE71C6851EBC5D7A6B6BBF2A49C34F45FE87`, and no added, missing, or modified files. All 67 roots in the supplied historical inventory remain present. Two additional roots created by diagnostic runs remain preserved; current count is 69. No old or new root was deleted.

**Disposition — owner accepted, 2026-10-19:** the existing request-unload/eventual-collection behavior is accepted. `Unload()` requests unload; retained external references, including an activation exception retained by its consumer, can defer collection; immediate collection before returning the original activation error is not promised. The original error remains available for diagnosis. The ALC test-lifecycle regression is **RESOLVED** by correcting test teardown order, with all requested repetitions and regressions passing. This acceptance does not alter frozen contracts or the OOP-only policy. Step 6 remains PARTIAL for the separately documented published capability-response and clean-environment criteria.

## Runtime regression recurrence — 2026-10-19

The owner-requested recheck does **not** reproduce the previous successful stability result. The current test still separates catalog/lease work into a no-inline async frame, disposes both leases, clears the strong context locals, requests unload, checks collectible-context WeakReferences, and releases the startup exception in the failed-activation tests. The prior machine record reports 10/10 for each of the positive, missing-dependency, and incompatible-dependency cases, class 6/6, Runtime 265/265, but did not record a source-file hash; byte-for-byte comparison to that test source is therefore unavailable. The current positive-success case is the one failing; the previously diagnosed retained startup exception is not part of this success path.

Reproductions: **20 individual theory invocations, each 1 PASS / 1 FAIL** (the in-process row fails, the OOP row passes); **10 class invocations, each 5 PASS / 1 FAIL**; **5 full Runtime invocations, each 264 PASS / 1 FAIL**. Each failing result is the in-process case `PackagesResolveSamePrivateAssemblyIdentityIndependentlyAndUnloadAfterLeaseRelease`.

The failure remains `Directory.Delete(root, recursive: true)` on the owned package tree with `UnauthorizedAccessException`, HRESULT `0x80070005` / Win32 mapping 5. New pre-delete instrumentation retained in the failing TRX shows WeakReference liveness `[false,true]`, zero fixture assemblies enumerable from the current AppDomain, and successful DELETE-access and exclusive-read probes. There are no associated ModuleHost processes. This demonstrates one collectible-context object is still alive at cleanup while ordinary ACL/access probes succeed, but does **not** prove that the retained exception cause from the earlier failed-activation tests is responsible, nor identify the Windows component causing denial. No production fix is justified by the current evidence.

To preserve the state that the testhost previously suppressed, the test-only cleanup diagnostic now includes both the pre-delete snapshot and the post-failure snapshot in the thrown test error. The instrumented focused probe reproduced the same failure and captured the pre-delete liveness state. No activation assertion, production code, contract, or OOP-only policy changed.

Data Protection has no demonstrated causal relation: the failing test directly constructs and exercises `ModuleExtensionCatalog`; the Runtime test project does not reference the Core project or start `CoreHost`. Data Protection registration is in Core hosting and its dedicated tests are in the Core test project.

Regression outcomes: Core **70 PASS / 1 NOT EXECUTED** (existing symlink-privilege diagnostic says `Directory.CreateSymbolicLink` lacks the required privilege; junction coverage is separate); ModuleHost **10/10 PASS**; publisher-trust focus **32/32 PASS**; Package Lifecycle without RID **94/94 PASS**; Data Protection **8/8 PASS**; Runtime **264/1 FAIL in each of 5 runs**. Package Lifecycle with `-r win-x64` is **79 PASS / 15 FAIL** because the test harness constructs `bin/net10.0/net10.0/Hispania.PackageLifecycle.ProcessHost.dll` while the RID build emits `bin/Release/net10.0/win-x64/Hispania.PackageLifecycle.ProcessHost.dll`; this is a RID-sensitive test path calculation, not a distribution artifact failure.

The accepted Slice 7 artifact passed the baseline comparator before and after: original content ID and manifest SHA remain unchanged with empty added/missing/modified lists. The run began with 70 prefix-matched temp roots; all 69 names in the preceding historical inventory remain present. At the end, 106 roots exist: 37 outside that 69-root inventory (one predated this recheck; 36 were created during it, including 35 failed repetition runs and one instrumented focused probe). No roots were deleted. Evidence index: `runtime-stabilization-20261019.json`; raw TRX and console logs: `.artifacts/slice8-step6-runtime-stabilization-20261019/`.

**Disposition: PARTIAL.** The ALC cleanup regression has recurred and is reproducible; its exact cause is unresolved. No production change was made. Step 6 remains PARTIAL; no Step 7 or Slice 9 work started.

## Async-frame differential follow-up — 2026-10-19

The owner-authorized minimal experiments isolated the behavioral difference to the frame containing lease acquisition, module calls, assertions, and lease disposal. Explicitly setting the two lease locals to null after `Dispose()` did not fix the failure. Disabling the diagnostic snapshot did not fix it. A first variant that moved lease operations and unload observation into no-inline helper frames passed. A narrower D1 variant moved only lease operations into a synchronous `[NoInlining]` helper and left `UnloadAsync` plus the WeakReference checks in the caller's async frame; both runtime theory rows passed. The narrower variant then passed **5/5 focused invocations** (each invocation covered in-process and OOP rows) and the complete isolation class **6/6**.

This is evidence that the async frame which previously held lease-related locals was relevant to the cleanup symptom. It does **not** identify the exact object or GC root that retained the second ALC. No GC-root diagnostic tool (`dotnet-dump`, `dotnet-gcdump`, ProcDump, Handle, or WinDbg) was available on PATH. No tool was installed, and no process dump was collected. The test now uses the synchronous no-inline lease helper; production code, contracts, runtime policy, and assertions remain unchanged. Prior failed runs remain preserved.

Authorized regressions after the focused gate: Runtime **265/265 PASS**; Core **70 PASS / 1 SKIP / 0 FAIL** (the existing direct symbolic-link privilege limitation); ModuleHost **10/10 PASS**; Package Lifecycle **94/94 PASS**; Publisher Trust **32/32 PASS**. An initial Core `--no-restore -r win-x64` attempt stopped before tests with `NETSDK1047` because restored assets lacked that RID; the normal restore-enabled rerun passed 70 tests with the one known SKIP. TRX digests and paths are in [ALC reference investigation results](alc-reference-investigation-20261019.json); raw TRX remain under ignored `.artifacts/slice8-step6-alc-roots-20261019/`.

Historical artifact comparison passed before and after with the unchanged content ID and manifest SHA-256 and empty added/missing/modified arrays. All 106 roots present at the start were preserved; two failed diagnostic experiment roots were also preserved, bringing the current count to 108. The exact test source digest is `31246DE3ADDA01AEB5EA1AD06B7520CA81BB4EC759209E8A70484F3A3BEC915A`. **ALC focused cleanup gate: PASS for the bounded test change; exact GC root: NOT VERIFIED. Step 6 remains PARTIAL** for its other previously recorded Artifact Fitness limits. No Step 7/Slice 9 work, commit, or push.
