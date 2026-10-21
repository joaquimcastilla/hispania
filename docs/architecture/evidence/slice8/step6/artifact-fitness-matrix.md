# EV-050 Step 6 — Artifact Fitness

Date: 2026-10-18
Scope: Windows x64, `net10.0`, Release, framework-dependent. This step does not close Slice 8 and does not authorize Step 7.

## Three independent claims

| Claim | Status | Evidence |
|---|---|---|
| Historical Slice 7 artifact remains intact | PASS | Before/after baseline JSON in this folder both report `passed=true`, unchanged content ID `44B43088658F2F70FE8A0745EA5DFC3B1519CBF10B9347F3F4B88835EEDB3B12`, manifest SHA-256 `22D05298F7329BE8C2892BDEE33EBE71C6851EBC5D7A6B6BBF2A49C34F45FE87`, and empty `added`, `missing`, `modified`. |
| New candidate structural and published-process fitness | PASS CON ALCANCE LIMITADO | Latest candidate validates and published lifecycle/regressions pass at the accepted Windows x64 scope. Public generic invocation is excluded by owner decision; clean-machine, alternate-identity and clean-checkout criteria remain NOT VERIFIED. |
| Reproducibility/distribution dependencies | NOT VERIFIED beyond recorded scope | Same-host candidate evidence exists, but candidate came from a dirty worktree; clean-checkout reproduction and clean-machine/alternate-identity execution are NOT VERIFIED. No broad portability or profile-independence claim is made. |

## Candidate identity and build

Artifact path: `%TEMP%\hispania-slice8-step6-candidate-187c3f92ba8042298b28ef05452c939f-publish`<br>
Artifact ID: `hispania.core`<br>
Source HEAD recorded in manifest: `ef7365bb5e7d7551cb1093519743cc1885757b5b`<br>
Source state: dirty worktree; manifest commit identifies HEAD only. `source-worktree-state.json` records path-level status and hashes for the files present in the working tree.<br>
Target: `net10.0` / `win-x64` / Release / framework-dependent.<br>
Content ID: `BCA8F79A51A2705CCD4DE10AF12ED819EE4AD426B8E74048EAB25E0FE0F11CD8`<br>
Manifest SHA-256: `A336746DA28F172A7E4B84F7E5577AA5F27BC90CB8FDF2D312B74BB5B4AEAB6C`<br>
Validator: PASS, `issues=[]`, policy `hispania-core-v1.0.0`.

Command:

```powershell
dotnet publish apps/core/Hispania.Core.csproj --configuration Release --runtime win-x64 --self-contained false --output "$candidate" -p:Deterministic=true -p:ContinuousIntegrationBuild=true -p:DebugType=none -p:DebugSymbols=false --verbosity minimal
```

The manifest lists 18 files and the on-disk tree contains 19 files including the manifest. Core EXE SHA-256: `389714A3F515207A2D1A7452096C5D2E17DD33D7C653DFF7B77CC4C2B7D249AE`; Core DLL: `E6581155E12B9EAB0403C87472EF3304EFFCF36F362BADD6627FB4FBAC5CF959`; ModuleHost DLL: `B022F55113A10257FF0A3596EBBDA13A32C126EAF7A65DC897CEF17F64A16976`. Full inventory: [candidate-inventory.json](candidate-inventory.json). Full manifest: [candidate-core-artifact-manifest.json](candidate-core-artifact-manifest.json). Validator JSON: [candidate-validator-report.json](candidate-validator-report.json).

Structural scan: PASS. No extra/unlisted, missing, or changed files; no `.pdb`, `.trx`, `.pfx`, `.pem`, `.db`, or SQLite test files in candidate; no exact repository absolute path in textual config/JSON. The candidate contains Core, `module-host/Hispania.ModuleHost.dll` plus its `.deps.json`/`.runtimeconfig.json`, both contracts, SQLite managed/native assets and required manifests.

## Published candidate smoke

The smoke launched the candidate executable with the candidate folder as working/content root and used a fresh synthetic SQLite data root. It validated HTTPS and healthy liveness, publisher trust through host configuration, authentication/permissions, rejection of a signed `dotnet-inprocess-trusted` package with `module_runtime_not_allowed`, install/list/activate/health of the signed OOP fixture, persistence across Core restart, disable/uninstall, a controlled fixture startup failure with Core remaining healthy, graceful exit code 0, no residual Core/ModuleHost processes, unchanged candidate bytes and temporary-root cleanup.

Result: **15/15 PASS**, `overall=PASS`, `secretsPrinted=false`. Core logged schema version 3. SHA-256 values of the fixture assembly and normal/failure signed packages are in the sanitized summary. See [published-candidate-smoke-sanitized.json](published-candidate-smoke-sanitized.json), SHA-256 `F43444469CBDD5558563C1C569430449BE45CAD7ACFAF5CD3BAEEFA1C000DE1D`.

Two acceptance details cannot be claimed from this smoke:

- There is no `/health/ready` endpoint in the current public Core contract. Startup completes persistence initialization before listening; `/health/live` was the available health/readiness signal.
- Core currently exposes no module capability invocation endpoint, and the existing harmless Slice 7 fixture exposes no callable capability. A published module request/response round-trip is therefore **NOT VERIFIED**. This is a current interface limitation; no endpoint or contract was added.

The test runner builds the fixture using the repository SDK, but Core and ModuleHost processes execute from the published temporary folder. This establishes that the running published processes did not need repository files; it is not a clean-machine test.

## Reproducibility and external requirements

A second publish in a separate temporary directory from the same checkout and SDK matched **18/18** payload paths, sizes and SHA-256 digests; no extras; same candidate content ID. See [same-environment-reproducibility.json](same-environment-reproducibility.json), SHA-256 `BF1C66F4215D86BF18019F2173A665DCE81C9DF33F36364727E7B072A17DB519`.

The artifact is framework-dependent and requires a Windows x64 `dotnet` host plus .NET 10 and ASP.NET Core 10 shared frameworks. ModuleHost also launches through `dotnet`; SQLite native `e_sqlite3.dll` is bundled. The smoke logged use of the current Windows profile's ASP.NET Data Protection key directory; two existing key files had prior-day timestamps and no new key file was observed post-run. A clean Windows profile, VM or container was unavailable, so independence from user-profile state and clean-host reproducibility remain NOT VERIFIED. No cross-platform claim is made.

## Regression matrix

| Suite | Result | Evidence |
|---|---|---|
| Core (RID `win-x64`) | 62 PASS / 1 SKIP / 0 FAIL | TRX under `.artifacts/slice8-step6-20261018/test-results/core-win-x64/`; SHA-256 `E0113AD4D09387A0DBA544FCCE3F52A61AB60AED75AD78BD65998AB76E902772`. The skip is direct symlink creation requiring privilege; five junction tests remain separate PASS evidence. |
| Slice 7 lifecycle focus | 7/7 PASS | TRX SHA-256 `0D0698E64518BF2127CF82249648EC171F16626C50A15A60EAEF2B3CD7B1EA35`. |
| Publisher trust focus | 1/1 PASS | TRX SHA-256 `0A791E5A4AC6986B69F405C1B11D7480E9152E5D0DAB07AEAB3C2F14947E208F`. |
| ModuleHost | 10/10 PASS | TRX SHA-256 `5AA18227D9067A2F822F53D19253F0AA935EFCED98B845A290F662CA2776B3A4`. |
| Package lifecycle + historical reader | 94/94 PASS | TRX SHA-256 `C9E1C44AF548BBBD1ACE79122EE454695C2EEBD67CE4380A8C37D8B3AC0CD577`. |
| Package backup/restore | 2/2 PASS | TRX SHA-256 `DE6E982234D42E84B623C3874A4E1DFAE05D90FE609AEFAAC8B89F5A724B968A`. |
| Runtime full | 264 PASS / 1 FAIL / 0 SKIP | TRX SHA-256 `21005D3831C8964E05948B5D511A0C181BF6C7CE9F4662F933120B10EAFFFDF4`. Failing case: `PrivateModuleDependencyIsolationTests.PackagesResolveSamePrivateAssemblyIdentityIndependentlyAndUnloadAfterLeaseRelease(runtime: dotnet-inprocess-trusted)`. In the test's `finally`, recursive cleanup repeatedly receives Windows `UnauthorizedAccessException` deleting `Hispania.PrivateFixture.Dependency.dll`. |
| Runtime unload focused reruns | 3 runs, each 1 PASS / 1 FAIL | TRX digests: `547596F0C4195A13FEDDE2C443157C6CEE43DAAA86BDE95F30FD82E0E960AEDE`, `8E322D5326902E50D313F87216760A83EF451963AFE7197DE585D70D1DB8AE56`, `FA86A248E4AB216743C1A65E9E0325BF216D16B67914AB79C5E1F098CA2F15F8`. Each OOP case passed; each in-process case failed in cleanup. This repeated result blocks Step 6 PASS. |

The first Core test command without an explicit RID failed before tests with `NETSDK1047` because the ModuleHost project reference requires `net10.0/win-x64`. Re-running with `--runtime win-x64` passed the Core suite. No product or contract changes were made to address the test failure.

## Historical baseline and disposition

The accepted Slice 7 publish remains at its original content ID `44B43088658F2F70FE8A0745EA5DFC3B1519CBF10B9347F3F4B88835EEDB3B12` and manifest SHA-256 `22D05298F7329BE8C2892BDEE33EBE71C6851EBC5D7A6B6BBF2A49C34F45FE87`. Before and after comparisons are both PASS with `added=[]`, `missing=[]`, `modified=[]`; reports are in `historical-baseline-before.json` and `historical-baseline-after.json`. The accepted artifact was not rebuilt or written.

**Step 6: PARTIAL.** Candidate build, allowlist, manifest, published smoke, same-machine repeat publish and historical baseline pass. Remaining items: reproducible Runtime in-process ALC/temp cleanup test failure; capability request/response cannot be demonstrated through current Core APIs; clean-host/profile independence not verified. No Step 7, Slice 9, commit, push, or cleanup of the 18 previous temporary roots. Global roadmap remains 4/14 — 28.6%.

### ALC async-frame differential follow-up — 2026-10-19

The minimal test-only change moves lease acquisition, module invocation/assertions and disposal into a synchronous `[NoInlining]` helper; unload request and WeakReference observation remain in the caller async method. Clearing lease locals alone and disabling diagnostic snapshot formatting each still failed; the narrower helper variant passed, as did five focused invocations (both runtime rows per invocation) and the full isolation class (6/6). This localizes the influential lifetime boundary to the lease-bearing async frame, but does not prove the identity of a specific GC root. No production file changed. Runtime **265/265**, Core **70 PASS / 1 known symlink SKIP**, ModuleHost **10/10**, Package Lifecycle **94/94**, Publisher Trust **32/32**. Historical Core baseline unchanged. All 106 starting temp roots remain; two failed diagnostic roots were retained, total 108. Evidence and TRX digests: [ALC diagnosis](alc-unload-diagnosis.md) and [machine-readable result](alc-reference-investigation-20261019.json). **ALC regression gate PASS; exact GC root NOT VERIFIED; Step 6 remains PARTIAL** for independent outstanding criteria.

### Runtime test temporary directories

The failing Runtime test created four temporary roots that remained after its testhost exited. Exact paths:

- `%TEMP%\hispania-slice8-private-deps-233f858b61e142a193fd22fc9ef36946`
- `%TEMP%\hispania-slice8-private-deps-eafcaa9524c740b0adfcac6a42823032`
- `%TEMP%\hispania-slice8-private-deps-5e48e21846d041a8bab563063beb3cf8`
- `%TEMP%\hispania-slice8-private-deps-8cd79a9da6544e65bc07102122fbd329`

These are test-created fixture roots, not candidate contents. A recursive removal call limited to these exact paths was rejected by tool policy before execution; no deletion occurred and no alternate cleanup command was attempted. The 18 older `hispania-slice8-private-deps-*` directories were not touched. The four roots remain for explicit owner cleanup; this is recorded as test cleanup debt.

## ALC unload follow-up — 2026-10-18

Follow-up diagnosis: [collectible ALC unload diagnosis](alc-unload-diagnosis.md). Ten in-process repetitions all failed during deletion of the test-owned v2 dependency DLL; OOP passed. Both collectible contexts were observed dead via weak references, and an exclusive read-open probe succeeded, but recursive deletion still failed with HRESULT `0x80070005`. ACL and attribute observations after test exit were ordinary and do not establish the cause at failure time. No root cause is claimed.

Current regression results: dependency isolation **5 PASS / 1 FAIL**; Core **62 PASS / 1 SKIP**; ModuleHost **10/10 PASS**; Package Lifecycle **94/94 PASS**; backup/restore **2/2 PASS**; full Runtime **262 PASS / 3 FAIL**. The two additional failures are in-process startup-failure cleanup cases exposed when the old retry loop was removed for diagnosis. A temporary production wait-for-unload change did not resolve them and was reverted. The test now has a separate no-inline lifetime scope and additional diagnostics; those changes did not resolve deletion. Current full Runtime TRX SHA-256: `8276DA1DC851F00DEE604EF4FC273C2152E34FF4AAB7F55C40C726ED4B47121C`.

The historical artifact comparison passes before/after with the original content ID and empty differences. There are 56 test-prefix temp roots in the current inventory; the 18 older and four previously known roots were preserved. Newly created roots were also left intact after failed cleanup. **Step 6 and this ALC substep remain PARTIAL.**

### Windows deletion diagnosis — 2026-10-18

The isolated comparison (including actual fixture modules resolved through `AssemblyDependencyResolver`) passed in all same-process, child-process/parent-delete, unloaded-copy, and ordinary-data scenarios. Every native `DeleteFileW` succeeded; Restart Manager reported no process. A retained prior failure root had ordinary Archive attributes and inherited NTFS ACLs with Modify/FullControl and no deny ACE, inspected after the failure. These results do not reproduce or identify the original transient `0x80070005`; exact native failing operation remains unverified. No production correction was made. See [ALC unload diagnosis](alc-unload-diagnosis.md) for commands, hashes and scope. The 56 prior temp roots remain intact. Substep remains PARTIAL.

### Testhost-original failure diagnosis — 2026-10-19

At the original Runtime testhost cleanup point, `Directory.Delete(..., recursive:true)` failed on the package module DLL for the missing dependency case and the private dependency DLL for the incompatible case (`UnauthorizedAccessException`, HRESULT `0x80070005` / Win32 mapping 5). The implicated fixture assemblies remained in a live collectible ALC at that instant. `CreateFileW(DELETE, share-all)`, `CreateFileW(GENERIC_READ, share-none)`, and parent `FILE_DELETE_CHILD` probes all opened; ACLs were inherited FullControl, attributes Archive, and no ModuleHost process existed. This rules against missing ACL rights or a conventional conflicting file handle. `LoadAndStartAsync` calls `candidate.Unload()` on failed startup without waiting for its ALC weak reference to die; test cleanup then runs immediately. The evidence localizes the failure to that failed-activation unload race; production change has not been made pending owner authorization.

The positive unload test had a separate strong local reference held during `UnloadAsync`; clearing the local before waiting fixed that test-only defect and passed 10/10 fresh testhosts. Full Runtime under usual parallelism: 264 PASS / 1 FAIL (incompatible in-process cleanup); dependency isolation class: 4 PASS / 2 FAIL; Core: 62 PASS / 1 known symlink SKIP; ModuleHost filter: 10/10 PASS. Same-host sequential selected cases: positive PASS, two failure cases FAIL. Full details and TRX are in `alc-unload-diagnosis.md` and ignored `.artifacts/slice8-step6-testhost-diagnostic-20261019/`. The 56 pre-existing temporary roots and all 11 new failed-diagnostic roots were preserved. Step 6 remains PARTIAL.

TRX SHA-256: isolation class `1BB2CB22DD923941F3F64D4022FAE00A015CEA17D9E8DCEC9035F8663AD983F6`; Core `EE2AB43DF40DF96E83C451A9CB043962E887103986AE18233A6E948462BB61DB`; ModuleHost `4F1CEF03D37814E6D8B9D438AC676D82C64BD69886E23926D1D9038CD190F856`; final incompatible probe `12A53E0889F4E1A09E82E4D8C01686CF7685DBA28F4B383E86A9DFBA45C0E723`.

### Failed-activation exception lifetime verification — 2026-10-19

The prior cleanup failure was reproduced and then isolated to the test retaining its `ModuleStartupException` while deleting the package image. The exception preserves its module `InnerException` stack metadata; while held, the collectible ALC weak reference stayed alive and recursive deletion failed with `0x80070005`. Once the test asserted the expected failure and released the exception reference, the ALC was collected and cleanup passed. Production already requests unload and clears its module/context fields. No production code, contract, OOP policy, or historical artifact changed. The test cleanup order now waits for collection after releasing the error reference.

Results: positive unload 10/10; missing dependency 10/10; incompatible dependency 10/10; full isolation class 6/6; OOP isolation 3/3; Core 62 PASS / 1 known symlink SKIP; ModuleHost 10/10; Package Lifecycle 94/94; Phase 17 backup/restore 46/46; Phase 19 package backup/restore 2/2; publisher trust 32/32; full Runtime 265/265. TRX hashes and run inventory are in [ALC correction results](alc-correction-results-20261019.json); raw outputs are under `.artifacts/slice8-step6-alc-correction-20261019/`.

Historical artifact baseline passed before and after with unchanged content ID and manifest digest, and `added=[]`, `missing=[]`, `modified=[]`. All 67 previously inventoried temporary roots remain; two diagnostic roots were added and preserved, bringing the count to 69. No temporary roots were deleted. `git diff --check` passed. The owner accepted eventual ALC collection with the original activation exception preserved; this resolves the ALC gate without changing frozen contracts. Step 6 remains PARTIAL for the separately recorded capability-response and clean-environment limitations.

### Windows x64 environment-independence check — 2026-10-19

The previously recorded candidate was inspected read-only at `%TEMP%\hispania-slice8-step6-candidate-187c3f92ba8042298b28ef05452c939f-publish`. Its manifest identifies content ID `BCA8F79A51A2705CCD4DE10AF12ED819EE4AD426B8E74048EAB25E0FE0F11CD8`, source commit `ef7365bb5e7d7551cb1093519743cc1885757b5b`, `net10.0`, `win-x64`, framework-dependent. All 18 payload files matched manifest paths, sizes and SHA-256 values; no extra or missing payload files were found. Manifest SHA-256: `A336746DA28F172A7E4B84F7E5577AA5F27BC90CB8FDF2D312B74BB5B4AEAB6C`.

Execution was deliberately **not started**. Inspection of the published Core and current configuration code found no supported setting or explicit Data Protection key-ring path. ASP.NET Core Data Protection therefore uses its default Windows profile-backed key store. The existing configuration surface provides an isolated `HISPANIA_CORE_DATA_DIRECTORY` for SQLite/module storage and an HTTPS certificate path, but no configurable log directory (the host uses JSON console logging) or Data Protection directory. Starting Core would rely on or touch the usual profile key store, contrary to this check's isolation requirement. No candidate copy or temporary run environment was created; no keys were accessed and no workaround or undocumented environment override was attempted.

The historical Slice 7 baseline comparison passed immediately before this check: `added=[]`, `missing=[]`, `modified=[]`; content ID `44B43088658F2F70FE8A0745EA5DFC3B1519CBF10B9347F3F4B88835EEDB3B12`, manifest SHA-256 `22D05298F7329BE8C2892BDEE33EBE71C6851EBC5D7A6B6BBF2A49C34F45FE87`. The 69 inventoried `hispania-slice8-private-deps-*` historical temp roots were preserved. Candidate integrity is verified, but independence from the usual profile and a clean development machine remain **NOT VERIFIED**. Environment-isolation subcheck: **BLOCKED / NOT RUN** pending an authorized, supported way to configure Data Protection key persistence. No functional code, candidate, historical artifact, or frozen contract was changed.

### Step 6 — Configurable Data Protection key isolation (2026-10-19)

**Implementation.** Added optional setting `HISPANIA_CORE_DATA_PROTECTION_KEYS_DIRECTORY`. If absent, Core does not register an explicit repository and keeps ASP.NET Core's prior default behavior. If present, this setting is currently supported only on Windows: configuration requires an absolute path outside the publish directory, checks path components with `StoragePathGuard`, creates the directory, verifies write/delete access with an owned probe, and fails closed on errors. The host uses `PersistKeysToFileSystem` and explicitly calls `ProtectKeysWithDpapi`; it does not fall back to the profile repository. It does not copy or migrate keys. No public package/module contract changed.

ASP.NET Core documents that choosing an explicit key repository disables automatic at-rest encryption discovery. The explicit Windows DPAPI protector preserves the existing Windows default's at-rest protection. Evidence verifies `<encryptedSecret>` with `DpapiXmlDecryptor`; no plaintext `<secret>` element was present. Directory isolation is not itself encryption. DPAPI is scoped to the current Windows user context; this proves separation from the ASP.NET Core profile key-ring directory, not independence from the account's DPAPI capability or a clean Windows identity.

**Tests.** New configuration tests: **8/8 PASS** (unset/default, relative and empty path rejection, publish overlap rejection, file-parent/inaccessible path, ACL-denied directory with original ACL restored, valid path/probe removal, DPAPI-protected persisted key and decrypt after host rebuild, and no test payload/protected value in captured Data Protection logs). Full Core: **70 PASS / 1 SKIP / 0 FAIL**; the skip is the existing direct symlink-creation privilege limitation. ModuleHost: **10/10 PASS**. Package lifecycle without an RID override: **58/58 PASS**. Publisher trust focus: **1/1 PASS**. Full Runtime: **264 PASS / 1 FAIL / 0 SKIP**. The failure is `PrivateModuleDependencyIsolationTests.PackagesResolveSamePrivateAssemblyIdentityIndependentlyAndUnloadAfterLeaseRelease(runtime: dotnet-inprocess-trusted)`, cleanup `UnauthorizedAccessException` / `0x80070005` on `Hispania.PrivateFixture.Dependency.dll`; this is retained as a regression failure, not masked by the owner-accepted eventual ALC semantics. An initial package-lifecycle invocation with `-r win-x64` used a testhost-derived helper path with a duplicated `net10.0` segment; the expected no-RID invocation passed all 58 tests.

**New candidate and isolated published smoke.** Published a new Release `net10.0/win-x64`, framework-dependent candidate; the prior candidate and Slice 7 accepted artifact were not modified. Candidate root: `%TEMP%\hispania-slice8-step6-candidate-9e471e99e46a48739e72d507afa7baa3-publish`. A separate exclusive copy was used for execution: `%TEMP%\hispania-slice8-step6-candidate-46043923704b4a3ebe7a27f61f9b625a-publish`. Both have content ID `9459242A34B042402F79D140CEED7D16BDEB149616EC7B08573188A34D31BED0`; manifest SHA-256 `8B4196461AB0EA5A8E5BF92931EC0756839E82F82208309B00539032E83185DD`; 18/18 payload files match, validator `passed=true`, `issues=[]`, policy `hispania-core-v1.0.0`. Core EXE SHA-256 `389714A3F515207A2D1A7452096C5D2E17DD33D7C653DFF7B77CC4C2B7D249AE`; Core DLL `2DA56522904C0C4A1ABA417FA0594BE1E8F8F712161C7A42003154B4E7FA7A6D`; ModuleHost `4457DBDB9C5657865B1C7A4313A23B53D6709F3249E3B2083D60C11F7CEB583E`.

The isolated-copy smoke passed **17/17 twice**. The sanitized second-run result is [published-candidate-smoke-dp-isolated-20261019.json](published-candidate-smoke-dp-isolated-20261019.json), SHA-256 `6722D2D88D918B53C6D6745F560EA4481167288747C3A735B7B2A1D36D7F8C12`. It confirms a key file was written under the run-owned `data-protection-keys` directory, DPAPI metadata, restart persistence, module install/activation/OOP-only rejection/disable/uninstall, Core exit `0`, no residual Core/ModuleHost process, candidate unchanged, and run-root cleanup; `secretsPrinted=false`. The candidate manifest and full per-file hashes are in [candidate-dp-isolated-manifest-20261019.json](candidate-dp-isolated-manifest-20261019.json); summarized identities and gate results are in [candidate-dp-isolated-summary-20261019.json](candidate-dp-isolated-summary-20261019.json), SHA-256 `D439C1D853462626B9DBBCE4F8A7975459158B196B9E7C402DF233AEC02C6B37`.

Core and published ModuleHost ran from the isolated copied artifact outside the repository. The smoke harness still uses the repository SDK/project to build its harmless fixture into its own temporary root; this is a test-harness build dependency, not a runtime Core/ModuleHost file dependency. No clean machine or separate Windows identity was tested. The default ASP.NET Data Protection key-ring directory was not configured or selected by the candidate; explicit key persistence points to the isolated test root. The DPAPI protector remains bound to the current Windows account.

**Historical artifact and temporary roots.** Slice 7 historical baseline passed before and after, with empty added/missing/modified arrays, unchanged content ID `44B43088658F2F70FE8A0745EA5DFC3B1519CBF10B9347F3F4B88835EEDB3B12` and manifest SHA-256 `22D05298F7329BE8C2892BDEE33EBE71C6851EBC5D7A6B6BBF2A49C34F45FE87`. All 69 historical private-dependency temp roots were preserved. The Runtime failure created one additional root, bringing the observed count to 70; it was retained at `%TEMP%\hispania-slice8-private-deps-abbd6690e1e94816a097d0af1cb6e46d`. The two new candidate directories remain as evidence; the smoke-owned runtime root and certificate/key material were cleaned.

**Disposition: PARTIAL.** Configurable key-ring isolation and the published Windows x64 lifecycle smoke pass. The full Runtime suite has one failing in-process dependency cleanup test; clean-machine / different-user independence is not established, and the public capability response remains unavailable through current Core APIs. Step 6 remains PARTIAL; Slice 8 remains IN PROGRESS. No Step 7, Slice 9, commit, push, or roadmap change.

Framework behavior reference: Microsoft documents that an explicit key repository disables automatic at-rest protection and recommends explicitly configuring a protector; see [ASP.NET Core key storage providers](https://learn.microsoft.com/en-us/aspnet/core/security/data-protection/implementation/key-storage-providers?view=aspnetcore-10.0) and [key encryption at rest](https://learn.microsoft.com/en-us/aspnet/core/security/data-protection/implementation/key-encryption-at-rest?view=aspnetcore-10.0). The implemented Windows DPAPI protector is user-context-bound.

### Runtime/ALC stability recheck — 2026-10-19

The previously accepted ALC test-lifecycle result has regressed in the current Runtime outputs. Results: individual affected theory **20/20 invocations reproduced one in-process failure and one OOP pass**; class **10/10 invocations, each 5 PASS / 1 FAIL**; full Runtime **5/5 invocations, each 264 PASS / 1 FAIL**. The diagnostic now captures pre-delete and post-failure snapshots; at pre-delete one of two ALC WeakReferences remained alive, no fixture assemblies were enumerable, and DELETE/exclusive-read access probes opened. The exception is `UnauthorizedAccessException` / `0x80070005` at recursive test-root deletion. Exact underlying cause remains unproven; no production change was made. See [ALC diagnosis](alc-unload-diagnosis.md) and machine-readable [run evidence](runtime-stabilization-20261019.json).

Regressions: Core **70 PASS / 1 NOT EXECUTED** for the known symbolic-link privilege limitation; ModuleHost **10/10 PASS**; publisher trust **32/32 PASS**; Package Lifecycle **94/94 PASS without RID** and **79 PASS / 15 FAIL with RID**, where the failures are caused by the test harness resolving its process-host path as `net10.0/net10.0` although the build output is under `Release/net10.0/win-x64`; Data Protection **8/8 PASS**. This RID-sensitive path issue does not involve the published distribution. Data Protection has no demonstrated link to the ALC test, which does not start CoreHost.

The accepted historical artifact baseline passed before and after with its original content ID and manifest hash and no file differences. All 69 names in the preceding historical temp inventory remain present; 106 roots currently exist, including 37 outside that inventory (one pre-existing and 36 added during this recheck). No temp root was deleted. Step 6 stays **PARTIAL**.

## Public capability invocation audit — 2026-10-19

| Stage | Finding | Result |
|---|---|---|
| Module installation | Core administration supports installing the one approved Slice 7 fixture; prior published lifecycle smoke passed. | PASS |
| Module activation | Core administration can enable that fixture through the Core-managed catalog; prior published lifecycle smoke passed. | PASS |
| Core–ModuleHost communication | Runtime has an authenticated, current-user-only named-pipe protocol; prior published smoke and existing tests exercise it. | PASS |
| Capability invocation | Existing typed Runtime APIs and ModuleHost dispatch invoke declared contract operations. Existing OOP integration tests invoke and receive a result. Core's Slice 7 fixture declares no capabilities. | PASS_INTERNAL_ONLY |
| Client receives a capability response from published Core | Core has no public invocation route, and its catalog is not connected to the platform job contribution registry. There is no client request/response path to test in this artifact. | NOT VERIFIED |

Core HTTP routes are health, auth, and module administration (`GET list`, `POST install`, `POST enable/disable`, `DELETE remove`). Module reads require an authenticated session and `ModuleReadPermission`; mutations require `ModuleManagePermission`, same-origin and antiforgery checks. These routes install/operate modules; none invokes a capability. `CoreHost` constructs its `ModuleExtensionCatalog` with `jobContributionRegistry: null`, while the approved Slice 7 fixture has an empty `Provides` list.

The existing OOP path is internal: `ModuleExtensionCatalog` creates a typed `IModuleCapabilityProxyFactory` proxy; `OutOfProcessModule` sends bounded `request` frames over a current-user-only named pipe; a per-launch random token and protocol negotiation authenticate process/module identity; `ModuleHost` verifies the manifest's declared capability and contract type and returns a correlated response/error frame. The Worker Agent's `ModuleExtensionCapabilityExecutor` provides another internal invocation path for distributed work. These mechanisms do not create a public Core HTTP endpoint.

The broader `apps/server` has authorized platform-job APIs and distributed-worker mTLS endpoints. A client with `JobsManage` may submit an allowlisted job and query it with `JobsRead`; the API gives an accepted job/status/result-metadata workflow, not generic synchronous invocation of any module capability. The server catalog can receive platform-job contributions; this Core candidate does not wire that registry. This is a separate host surface and was not exercised as a substitute for Core capability invocation.

Tests run for this audit: existing signed OOP greeting and lifecycle/executor integration tests **2/2 PASS**. They verify internal OOP contract invocation/response, timeout/cancellation and recovery after a controlled ModuleHost crash; they do not prove a published-Core client path. The published invocation test was **NOT RUN** because static inspection established that no compatible public interface exists, as directed. Both retained candidate manifests validated with `passed=true`, `issues=[]`: content IDs `BCA8F79A51A2705CCD4DE10AF12ED819EE4AD426B8E74048EAB25E0FE0F11CD8` and `9459242A34B042402F79D140CEED7D16BDEB149616EC7B08573188A34D31BED0`. Historical Core comparison still has empty added/missing/modified lists. All 108 private-dependency temporary roots remain. Full routes, test digest and validation identities are in [capability invocation audit evidence](capability-invocation-audit-20261019.json).

Architecture options for a later owner-approved scope: (1) application-owned typed endpoint that invokes only a fixed capability contract with Core/platform authentication, authorization, request schema/size/time limits and audit; (2) reuse the existing asynchronous Platform Jobs route for allowlisted job types, with job permissions and result metadata; (3) keep capability resolution inside application services and expose only product-specific application endpoints. Do not expose arbitrary CLR type/method reflection as a generic public dispatcher. This audit does not choose among options. A published-Core client response remains a Step 6 Artifact Fitness gap if required by its acceptance criteria; implementing an API is a product/architecture decision and should be assigned to a later authorized implementation scope rather than silently added to this audit.

**Audit result: PARTIAL.** Internal OOP invocation is demonstrated. Public invocation through the published Core is absent and NOT VERIFIED. No code or contract changed; no commit, push, Step 7 or Slice 9.

## Consolidated Step 6 acceptance review — 2026-10-19

Disposition remains **PARTIAL**. The owner ratified that a generic public capability API is outside Artifact Fitness; prior failures and diagnostics remain preserved as history.

| Original acceptance area | Classification | Evidence / boundary |
|---|---|---|
| Windows x64 candidate publish, allowlist, manifest and payload structure | PASS demostrado | Latest candidate content ID `9459242A34B042402F79D140CEED7D16BDEB149616EC7B08573188A34D31BED0`; validator rerun in this review: `passed=true`, `issues=[]`, policy `hispania-core-v1.0.0`; manifest SHA-256 `8B4196461AB0EA5A8E5BF92931EC0756839E82F82208309B00539032E83185DD`. |
| Published Core and ModuleHost execute from isolated copy outside repository | PASS demostrado, con alcance limitado | The prior published smoke ran both from the copied publish. The fixture was built by the harness using repository SDK/source; runtime independence from repository files is demonstrated, a self-contained fixture build is not. |
| Published lifecycle: bootstrap/SQLite, HTTPS, install trusted signed OOP fixture, enable, restart persistence, disable/remove, controlled failure recovery, clean shutdown | PASS demostrado | Prior isolated candidate smoke evidence `published-candidate-smoke-dp-isolated-20261019.json`: 17/17 twice, sanitized; candidate unchanged and no residual Core/ModuleHost processes. |
| OOP ModuleHost launch, internal declared-capability request and response | PASS demostrado | Previously recorded OOP integration 2/2 and dependency isolation 6/6 PASS; internal request/response and controlled failure/recovery are covered. These are prior test results, not rerun in this documentation consolidation. |
| Module runtime policy | PASS demostrado | Core activation rejects `dotnet-inprocess-trusted`; OOP remains the only installable Core-managed runtime. OOP is process/failure isolation, not a security sandbox. |
| Public generic capability invocation from a client through Core | LIMITACIÓN ACEPTADA — RATIFICADA por el propietario (2026-10-19) | The owner ratified that this API is not required for Artifact Fitness. No such Core route exists, and no public invocation was tested. Internal OOP invocation is separately PASS. |
| Candidate unchanged and historical Slice 7 artifact intact | PASS demostrado | Latest candidate validator passed. Historical comparator rerun: content ID `44B43088658F2F70FE8A0745EA5DFC3B1519CBF10B9347F3F4B88835EEDB3B12`, manifest SHA-256 `22D05298F7329BE8C2892BDEE33EBE71C6851EBC5D7A6B6BBF2A49C34F45FE87`, `added=[]`, `missing=[]`, `modified=[]`, `selfTest=[]`. |
| Configured data and Data Protection key directories isolate the run from default profile key-ring storage | PASS demostrado, con alcance limitado | Earlier configuration tests 8/8 and isolated published smoke 17/17 twice verified explicit key-directory use and DPAPI persistence. DPAPI remains bound to the current Windows user context. |
| Run under another Windows identity | NOT VERIFIED | No second identity was used. |
| Execution on a clean Windows machine | NOT VERIFIED | No clean machine, VM or container was used. Framework-dependent candidate requires Windows x64 plus .NET 10 and ASP.NET Core 10 shared runtimes. |
| Reproducible distribution independent of a dirty development checkout | NOT VERIFIED | The latest candidate was produced from a worktree with uncommitted changes. Its manifest/content are internally valid; this does not establish clean-source provenance. Fixture construction also uses SDK/source from the repository. |
| Core symlink rejection on Windows | NOT VERIFIED | Core suite has 70 PASS / 1 SKIP; direct symlink creation remains skipped for the recorded Windows privilege limitation. Junction coverage is recorded separately in Step 1. |
| Package Lifecycle RID execution | PASS after harness correction | The complete filter passed 94/94 without RID and 94/94 with `win-x64`. The previous 79 PASS / 15 FAIL result is retained as pre-fix history. |
| Regression suites rerun for this correction | PASS, except documented SKIP | Runtime 265/265; Core 70 PASS / 1 SKIP; ModuleHost 10/10; Package Lifecycle 94/94 in both no-RID and win-x64 modes; Publisher Trust 32/32. |

### Consolidated disposition

The candidate and historical baseline integrity checks pass. The owner ratified the public-invocation limitation. Step 6 remains **PARTIAL** because alternate-identity and clean-machine execution remain NOT VERIFIED, direct symlink creation remains SKIP, and clean-source provenance is NOT VERIFIED. The RID harness defect is corrected; OOP is not a sandbox.

## Step 6 — Package Lifecycle win-x64 harness correction — 2026-10-19

The owner has ratified that a generic public Core capability-invocation API is not required for Artifact Fitness. Internal OOP invocation/response remains accepted at its demonstrated scope. This closes that scope decision; it does not change the Step 6 disposition.

**Root cause.** The test harness used `new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name` as the build Configuration. With output `tests/unit/bin/Release/net10.0/`, `Parent.Name` happens to be `Release`; with RID output `tests/unit/bin/Release/net10.0/win-x64/`, it is `net10.0`. Appending `net10.0` therefore produced the incorrect `tests/package-lifecycle-process-host/bin/net10.0/net10.0/...` path instead of matching the actual `bin/Release/net10.0/win-x64/` output. The same assumption affected the referenced ModuleHost path used by OOP fixtures.

**Correction.** `PackageLifecycleIntegrationTests` now derives the actual output suffix relative to `tests/unit/bin` from `AppContext.BaseDirectory` and uses that same suffix for the ProcessHost and ModuleHost project outputs. It validates the resolved path exists and fails explicitly when it does not. No fallback search, absolute development path, production code or contract was added or changed.

| Validation | Result | TRX |
|---|---:|---|
| Package Lifecycle, no RID (`FullyQualifiedName~PackageLifecycle`) | **94/94 PASS** | `tests/unit/TestResults/package-lifecycle-full-no-rid.trx` |
| Package Lifecycle, `--runtime win-x64` | **94/94 PASS** | `tests/unit/TestResults/package-lifecycle-full-win-x64.trx` |
| Runtime full | **265/265 PASS** | `tests/unit/TestResults/runtime-full-after-rid-fix.trx` |
| Core (`--runtime win-x64`) | **70 PASS / 1 SKIP / 0 FAIL** | `tests/core/TestResults/core-after-rid-fix.trx`; SKIP is the known direct symlink privilege case |
| ModuleHost-focused regression | **10/10 PASS** | `tests/unit/TestResults/modulehost-after-rid-fix.trx` |
| Publisher trust (`ModuleInfrastructureTests`) | **32/32 PASS** | `tests/unit/TestResults/module-infrastructure-after-rid-fix.trx` |
| Focused tests whose name includes Publisher | **3/3 PASS** | `tests/unit/TestResults/publisher-trust-after-rid-fix.trx` |

The directly affected `PackageLifecycleIntegrationTests` subset also passed **58/58** without RID and **58/58** with `win-x64`; the complete Package Lifecycle filter above passed 94/94 in both modes. Earlier 79 PASS / 15 FAIL RID evidence is preserved as the pre-fix result. Commands: `dotnet test tests/unit/Hispania.Runtime.Tests.csproj --configuration Release --no-restore --filter FullyQualifiedName~PackageLifecycle` and the same command with `--runtime win-x64`. No failure was filtered or ignored.

The historical Core baseline passed before this change (content ID `44B43088658F2F70FE8A0745EA5DFC3B1519CBF10B9347F3F4B88835EEDB3B12`, manifest SHA-256 `22D05298F7329BE8C2892BDEE33EBE71C6851EBC5D7A6B6BBF2A49C34F45FE87`; no additions, removals or modifications). The final comparison and TRX hashes are recorded in EV-050. Historical temporary directories were not deleted; the current `hispania-slice8-*` count is reported separately below. Step 6 remains **PARTIAL**: alternate Windows identity, clean machine and clean checkout remain NOT VERIFIED; direct symlink remains SKIP; OOP is not a security sandbox. No Step 7 or Slice 9 work started.

## Formal owner closure — Step 6 — 2026-10-19

**Step 6: PASS CON ALCANCE LIMITADO** for candidate `9459242A34B042402F79D140CEED7D16BDEB149616EC7B08573188A34D31BED0`. **Slice 8: IN PROGRESS.** This closure follows explicit owner acceptance. It does not upgrade any NOT VERIFIED criterion.

| Accepted claim | Evidence |
|---|---|
| Published artifact integrity, manifest and allowlist | Read-only validator: `passed=true`, `issues=[]`; manifest SHA-256 `8B4196461AB0EA5A8E5BF92931EC0756839E82F82208309B00539032E83185DD`. |
| Published execution outside repository; isolated data and DPAPI key persistence; OOP module lifecycle | Sanitized published smoke 17/17 twice. Fixture build used repository sources and SDK. |
| Internal OOP capability invocation and response | 2/2 integration tests. Generic public Core invocation API is outside accepted Artifact Fitness scope. |
| Regression set | Runtime 265/265; Core 70 PASS / 1 SKIP; ModuleHost 10/10; Package Lifecycle 94/94 no RID and 94/94 win-x64; Publisher Trust 32/32. TRX digests are recorded in EV-050. |
| Historical Slice 7 Core integrity | Content ID `44B43088658F2F70FE8A0745EA5DFC3B1519CBF10B9347F3F4B88835EEDB3B12`; manifest SHA `22D05298F7329BE8C2892BDEE33EBE71C6851EBC5D7A6B6BBF2A49C34F45FE87`; no added/missing/modified files. |

**NOT VERIFIED:** another Windows identity; clean machine; clean checkout reproduction; direct symlink test (retained SKIP). DPAPI is bound to the current Windows user. Fixture compilation uses repository sources/SDK. Candidate source worktree was dirty. OOP-only is not a security sandbox. No portable, clean-source reproducibility, or universal Windows-profile independence claim is made. The 114 historical/known `hispania-slice8-*` temporary directories remain; earlier failures and corrective evidence are preserved. Step 7 and Slice 9 were not started.
