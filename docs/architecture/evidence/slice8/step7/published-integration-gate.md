# HISPANIA v1.0 — Slice 8, Step 7.1: Published Integration Gate

Date: 2026-10-19<br>
Scope: acceptance contract and evidence mapping only. Step 7.1 does not execute Steps 7.2–7.5, add product code/tests/endpoints, publish a package, or close Slice 8.

## Purpose and claim boundaries

The Published Integration Gate determines whether a **specific, identified Windows x64 distribution candidate** can be accepted for the product scope defined by Architecture Freeze §13 and the approved R3 plan. It does not turn a successful component test into proof that the published product works, and it does not equate artifact integrity, execution, host independence, or reproducibility.

Keep these claims separate:

| Claim | What it establishes | What it does not establish |
|---|---|---|
| A. Internal component function | A component or contract behaves under its focused automated tests. | That the packaged files are complete or the published application can use that component. |
| B. Published integration | The exact candidate starts and its packaged Core, ModuleHost, storage and module lifecycle work together in an isolated run. | A clean-machine, other-user, or clean-source build claim. |
| C. Repository/SDK independence | Processes can run from a copied publish without resolving application files from the repository; build-tool use is recorded separately. | Independence from installed shared runtimes, SDK presence, user profile, or machine configuration. |
| D. Path/configuration independence | Explicit data and Data Protection paths are honored and fail closed as configured. | Encryption or portability across Windows identities. DPAPI is user-context bound. |
| E. Security/failure behavior | Tested trust, authorization, path, archive and recovery boundaries reject the named cases and preserve state. | A sandbox, TOCTOU-proof filesystem boundary, or protection from hostile native code. |
| F. Reproducibility/provenance | Source state, build inputs, artifact manifest and hashes are traceable; repeated builds can be compared at the stated scope. | Clean-checkout or byte-reproducibility unless those exact procedures were run and recorded. |

### Gate status vocabulary

Every criterion uses exactly one status: `PASS`, `FAIL`, `NOT VERIFIED`, or `NOT APPLICABLE`. `PASS` is tied to the stated platform, artifact identity, procedure and evidence. Missing or indirect evidence stays `NOT VERIFIED`. `NOT APPLICABLE` requires an explicit scope/owner decision. No result is promoted based on inference.

For overall gate status, report `PASS` only when every blocking criterion passes; `PARTIAL` when a blocker is unverified or failed but the candidate remains available for further work; and `FAIL` when a blocking criterion demonstrates an unsafe, corrupt, or unusable candidate. Optional/non-blocking findings remain visible. The owner-ratified classification for this gate is recorded below; that ratification does not accept residual PI-04/PI-35 gaps or authorize later steps.

### Scope carried from the approved plan

The gate retains the R3/Architecture Freeze §13 product boundary: the Core artifact must exclude Media, Inventory, provider/demo business modules and all six R2 artifacts whose rights were unresolved; evidence must check files, assemblies/references, routes, migrations and content and retain the file/hash inventory. The new gate adds integration and distribution evidence without broadening that product scope. Windows x64 is the current acceptance target. No public distribution or release is authorized by this document.

## Original exclusion register and PI-04 audit

### R2 — six exact unknown-rights artifacts

Source: `docs/architecture/HISPANIA-V1-UNKNOWN-RIGHTS-COMPONENTS.csv`. The CSV has no identifier column; `R2-1` through `R2-6` below are row references assigned solely for this audit, not original source IDs. Its six paths and SHA-256 values are reproduced below exactly. “PASS” is limited to the artifact-manifest path and hash comparison run read-only on both the latest candidate and historical Slice 7 Core; it does not clear rights or permit use elsewhere.

| R2 ID | Original artifact and SHA-256 | Original rationale / affected component | Evidence and check | Result | Risk if not verified |
|---|---|---|---|---|---|
| R2-1 | `compatibility/clients/0.7/identity/Hispania.Compatibility.Consumer07.dll` — `FCB341C4F3504B8FA85C75E32F386A3251B5CC31A55385B0B6B45FB4A9AF540E` | Historical v0.7 compatibility consumer; used by a compatibility test, not needed by Core | Repository file exists at CSV hash; exact path/SHA absent from candidate and historical manifests; digest is excluded by policy | PASS — source/inventory/hash check | Could ship an uncleared historical binary or compatibility surface |
| R2-2 | `compatibility/contracts/0.7/Hispania.ExtensionDemo.Contracts.dll` — `BD059255E0CD308ED1E4643287DE911BF9A96524FEC29B69BD9074D1F0BFC243` | Historical extension-demo contract; frozen compatibility corpus, no Core need | Repository file exists at CSV hash; exact path/SHA absent from both manifests; digest is excluded by policy | PASS — source/inventory/hash check | Could ship uncleared demo contract or couple Core to the old contract |
| R2-3 | `compatibility/contracts/0.7/Hispania.ModuleSystem.Contracts.dll` — `AE16C3C87A374A59889D7F13AB5CB26E38A96641470045BF80E9D5BF45910CEB` | Historical ModuleSystem contract binary; preserve only in historical corpus subject to rights | Repository file exists at CSV hash; exact path/SHA absent from both manifests; digest is excluded by policy | PASS — source/inventory/hash check | Could ship an uncleared old contract and create version ambiguity |
| R2-4 | `compatibility/contracts/0.7/Hispania.Platform.Contracts.dll` — `D21D7B5AC7CACF15503FA711F32DEF540FC4C8AAA0A86D6AFF735EB76F262951` | Historical Platform contract binary; preserve only in historical corpus subject to rights | Repository file exists at CSV hash; exact path/SHA absent from both manifests; digest is excluded by policy | PASS — source/inventory/hash check | Could ship an uncleared old platform contract |
| R2-5 | `compatibility/modules/0.7/greeting/Hispania.ExtensionDemo.dll` — `AFE8836CF7988DF26ECCC091C4C222E896AA9930BF203C8CDFA4CCB719315710` | Historical greeting module; R2 says no Core need and do not use as Slice 1 fixture absent rights clearance | Repository file exists at CSV hash; exact path/SHA absent from both manifests; digest is excluded by policy | PASS — source/inventory/hash check | Could ship or execute a historical module with unresolved rights |
| R2-6 | `apps/server/demo-assets/bp48-demo.mp4` — `BDB067DD7FD2A9D1C75185BBE3F30839B53BF30FF5C45B276D2E7DD5B51E2434` | Legacy Server demo video route/content; not included by Core; no redistribution without rights clearance | Repository file exists at CSV hash; exact path/SHA absent from both manifests; digest is excluded by policy | PASS — source/inventory/hash check | Could ship unlicensed media or legacy server demo content |

Read-only procedure compared each CSV path and digest against every manifest file in `%TEMP%\hispania-slice8-step6-candidate-9e471e99e46a48739e72d507afa7baa3-publish` and `%TEMP%\hispania-slice7-accepted-20261018-publish`. All six rows returned `pathPresent=false` and `excludedHashPresent=false` in both artifacts. This establishes the per-artifact file/hash result. It is not a substitute for checking assembly references, routes, migrations or embedded content.

### R3 and Freeze exclusions / scope requirements — Step 7.2 static audit (2026-10-20)

Sources: R3 owner plan §“Slice 8 — Artifact isolation and forbidden-content proof”; the allowlist's “Explicit exclusions”; Architecture Freeze §§12–13 and §13 validation criteria. The R3 plan itself does not assign IDs to individual exclusions; `R3-1`…`R3-9` below are audit row references added for traceability, not original requirement identifiers. The original requirement is to scan the package file list, assembly/reference graph, routes, migrations and content, and assert absence of all six R2 paths/hashes. The exact named exclusions are taken from the allowlist and Freeze text; no exclusion is inferred from a short name alone.

| R3 ID | Original exclusion or unchanged-scope requirement | Affected component | Existing evidence / method | Result | Risk if not verified |
|---|---|---|---|---|---|
| R3-1 | “no Media/Inventory/provider/demo components” in the clean Core artifact | Core project/runtime and publish | Exact payload/dependency allowlist, recursive project graph, 13 DLL PE reference scans and byte-string scans found no Media/Inventory/provider/demo dependency or component. Candidate route templates and manifest resources were also enumerated below | PASS — the named package, dependency, assembly and route exclusions were checked | Does not claim analysis of arbitrary machine-code behavior beyond named exclusions |
| R3-2 | `apps/server`, its `Hispania.Hosting`/service packaging, demo routes and demo content excluded | Legacy server and Core composition | No Server file or project/assembly edge. Nine route templates extracted from Core DLL and matched to Core route registrations; none is a Media/Inventory/Library/demo route. No `demo-assets`, `bp48-demo` or known legacy route/content string hit | PASS — files, graph and route scan; the legacy Server itself was not launched | Runtime behavior of the separate legacy Server was outside this static task |
| R3-3 | Media/Library/Playback/TMDB, Inventory, ERP, AI, FFmpeg/ffprobe, frontend assets and demo business modules excluded | Business modules, adapters, UI/content | Manifest/dependency graph, PE references/resources, route templates, and ASCII/UTF-16LE scans across all candidate files found no named business module, frontend bundle, media/demo asset or corresponding route | PASS — all named exclusions at package/dependency/assembly/route/content scan scope | A general semantic code audit is not claimed |
| R3-4 | PostgreSQL/Npgsql and Library/Media provider, schema and migrations excluded; existing provider belongs to Media and is not Core support | Core persistence graph vs Media-owned persistence | No Npgsql/PostgreSQL dependency, file, PE reference, resource or content token. Embedded resources are limited to `Microsoft.Data.Sqlite.Properties.Resources.resources`, `NuGet.Versioning.Resources.resources`, and `ILLink.Descriptors.xml`; Core has no embedded resources. The `Migration` strings and `core_schema_migrations` code belong to Core SQLite persistence; no Media schema/migration item was found | PASS — provider, resources, migration names/ownership and content scan | Does not clear Media provider rights or authorize its distribution |
| R3-5 | `infrastructure/platform/persistence-sqlite` is not an assumed drop-in; its Identity, Authorization, Jobs and Maintenance graph is not admitted without necessity evidence | Core persistence adapter | Recursive project graph contains only Core, Runtime, ModuleHost and ModuleSystem/Platform contracts; no `persistence-sqlite` edge. Deps/PE references contain no Identity/Authorization application or Jobs/Maintenance assembly | PASS — exact source graph, artifact dependency and assembly-reference checks | Candidate provenance is still dirty-worktree; PI-33 is separate |
| R3-6 | `infrastructure/platform/worker-agent-jobs`, distributed-work persistence, remote workers and schedulers excluded absent a boot-critical approved contract | Core vs worker infrastructure | Project graph, manifest deps, PE references, resource names and candidate byte-string scan contain no worker-agent, distributed-work, remote-worker or scheduler dependency/content identifiers | PASS — named dependency/reference/content scan | No claim about future module-owned workers |
| R3-7 | CLI is not a host dependency; tests, TestHost/xUnit/Test SDK, historical compatibility DLLs, MP4, module-owned stores/migrations, native/media binaries and npm packages excluded from Core distribution | Publish file set and project graph | Exact inventory extensions are `.dll` (13), `.exe` (1), `.json` (5); no test/testhost/xUnit/PDB/TRX/PFX/PEM/DB/npm package. Six historical R2 files exist in the repository at matching hashes but each path/hash is absent from candidate and historical artifacts. Only native payload is approved SQLite `e_sqlite3.dll`; no module-owned migration or media binary found | PASS — listed files/dependencies/content; manifest classification mismatch is tracked separately under PI-01 | Native SQLite is required; fileType label must be corrected in a future authorized candidate |
| R3-8 | `core/kernel/Hispania.Kernel.csproj` excluded by default unless reviewed Core functionality needs it | Core project graph | Candidate declared dependencies, recursive project graph, PE reference scan and binary token scan contain no Kernel project/assembly reference | PASS — graph and artifact reference/content checks | Does not establish clean-checkout provenance |
| R3-9 | “No Media/Inventory routes, data, schema, tests, or behavior moved/changed as part of Core creation”; keep `apps/server` unchanged and runnable | Source tree / legacy product composition | Git status shows no current local modifications under `apps/server`, Media, Inventory, Library or Playback; Core graph and candidate route/reference scan show no transferred business surface | PASS — no local transfer/change and package-exclusion scope. Legacy Server runtime launch was not part of this static audit | Historical legacy Server runnability is not asserted here |

**PI-04 aggregate result: PASS for the requested static exclusion audit of this exact candidate.** Each named R2 and R3 exclusion is individually checked at the specified artifact/file, dependency, assembly-reference, route, migration/resource or content dimension; R2 paths/hashes are absent from both retained manifests and the six source files are separately confirmed present at matching hashes in the repository. This is not legal rights clearance, runtime proof of the legacy Server, or a guarantee against arbitrary malicious code. PI-04's owner-set close blocker is satisfied at this bounded static scope; keep the stated limits visible. The independent native PE/manifest fileType discrepancy remains a separate **PI-01 FAIL**, and PI-33/PI-35 remain unresolved close blockers.

The package route scan extracted exactly: `/health/live`, `/api/v1/auth/login`, `/api/v1/auth/logout`, `/api/v1/auth/me`, `/api/v1/auth/modules`, `/api/v1/auth/modules/install`, `/api/v1/auth/modules/{id}`, `/api/v1/auth/modules/{id}/enable`, and `/api/v1/auth/modules/{id}/disable`. These matched Core source route registrations and contained no Media, Inventory, Library, or legacy Server route. The 13 DLL scan found zero forbidden assembly-reference matches; Core itself has no embedded resource. Other embedded resources are `Microsoft.Data.Sqlite.Properties.Resources.resources`, `NuGet.Versioning.Resources.resources`, and `ILLink.Descriptors.xml`. The dependency graph contains only the approved Core/Runtime/ModuleHost/contracts projects, the .NET shared frameworks, NuGet.Versioning and SQLite provider packages. A scan of candidate files as ASCII and UTF-16LE found no forbidden product, historic binary, media, npm, test-host or demo markers; migration markers map to Core-owned SQLite schema code.

## Acceptance matrix

“Current status” reflects retained historical evidence plus the read-only Step 7.2 checks recorded below. No Core, ModuleHost, or smoke execution was performed in Step 7.2.

| ID | Description | Rationale | Component | Procedure | Expected result | Evidence | Dependencies | Severity proposal | Current status |
|---|---|---|---|---|---|---|---|---|---|
| PI-01 | Candidate manifest, allowlist and content ID agree | Bind acceptance to exact bytes and metadata | Publish candidate | Run Core artifact validator and independently verify manifest metadata against payload types | Before validator correction: `passed=true`; after correction: `passed=false` for native `Hispania.Core.exe` declared `content` and native `e_sqlite3.dll` declared `managedAssembly` | Candidate manifest SHA remains `8B4196461AB0EA5A8E5BF92931EC0756839E82F82208309B00539032E83185DD`; content ID remains `9459242A34B042402F79D140CEED7D16BDEB149616EC7B08573188A34D31BED0`; read-only PE validation 2026-10-20 | Retained candidate unchanged; source fix needs a future authorized candidate | BLOCKING | FAIL — old manifest types mismatch actual PE contents; hashes and content identity remain intact |
| PI-02 | Candidate file inventory has no missing, extra or modified entries | Detect package drift | Publish candidate | Compare full relative-path/size/SHA inventory with manifest | No missing, unexpected or changed files | Step 6 candidate inventory and validator | PI-01 | BLOCKING | PASS |
| PI-03 | Historical Slice 7 publish remains unchanged | Protect accepted reference | Slice 7 artifact | Run read-only baseline comparator | Original ID and manifest digest; empty added/missing/modified/selfTest | Baseline JSON; comparator re-run 2026-10-19 | Pinned baseline file | BLOCKING | PASS |
| PI-04 | R3 forbidden business content and six rights-uncleared artifacts are absent | Preserve frozen product boundary | Candidate files, assembly/reference graph, routes, migrations, content | Scan exact inventory, deps and project graphs, PE references/resources, routes and contents; compare all R2 paths/hashes | All named exclusions absent; inventory retained | Step 7.2 static scan: inventory evidence JSON; validator `passed=true`; all six R2 files present/hash-matched in repository but path/hash absent from candidate/historical; recursive graph and 13-DLL PE reference/resource scan; nine Core route templates enumerated; forbidden ASCII/UTF-16LE token scan has no forbidden hits | Freeze §13; R2 exclusion list | BLOCKING | PASS — bounded named exclusion scan complete; limits above |
| PI-05 | Core and ModuleHost required files are present in the publish | Ensure packaged process pair is complete | Candidate | Inspect manifest and run published lifecycle | Required Core, ModuleHost, contracts and runtime files present | Candidate inventory; published smoke evidence | PI-01 | BLOCKING | PASS |
| PI-06 | Core starts and shuts down from the publish directory | Validate actual distribution entry point | Published Core | Launch candidate executable, health check, orderly shutdown | Healthy response; exit 0; no owned process remains | Sanitized published smoke DP-isolated, 17/17; owner-accepted Step 6 scope | Candidate, .NET 10 and ASP.NET Core 10 shared frameworks | BLOCKING | PASS |
| PI-07 | ModuleHost launches from its published location | Prevent accidental repo resolution | Core + ModuleHost | Activate signed OOP fixture from published run and observe owned process lifecycle | ModuleHost starts, serves fixture, exits; no orphan | Published smoke and ModuleHost tests | PI-05, trusted fixture | BLOCKING | PASS |
| PI-08 | Application files are not resolved from repository paths at runtime | Separate published execution from source-tree execution | Core + ModuleHost | Run a copy outside repository; record executable, working/content root and loaded paths | Runtime application paths belong to copied publish or isolated data roots | Step 6 copied-publish smoke ran Core/ModuleHost from copied publish; fixture build still uses repo SDK/source | Isolated copy | BLOCKING | PASS |
| PI-09 | Candidate shared-runtime requirements are explicit | Prevent false self-contained claim | Distribution | Inspect runtime configuration and execute with documented framework requirements | Framework-dependent `net10.0`, win-x64 requirement stated | Step 6 manifest/runtime configuration identifies .NET 10 and ASP.NET Core 10 shared frameworks; clean machine without SDK was not tested | Windows x64 | BLOCKING | PASS |
| PI-10 | No unintended absolute development paths or test/secret residue ships | Avoid developer-machine coupling and leakage | Candidate | Scan manifest and text/configuration; search forbidden suffixes/content | No disallowed residue or development path | Step 6 structural scan and validator, at their recorded scan scope | Candidate inventory | BLOCKING | PASS |
| PI-11 | Explicit isolated Core data directory is honored | Isolate test/runtime state | Core configuration + SQLite | Start candidate with unique data directory and inspect resulting paths/schema | Writes remain within configured data root; schema initializes | Published smoke; Core persistence/storage tests | Supported config contract | BLOCKING | PASS |
| PI-12 | Explicit Data Protection key directory is honored | Separate key ring from default profile location | Core Data Protection | Run with configured directory and inspect only path metadata/key persistence | Keys persist under explicit root; default profile ring is not fallback | CoreDataProtection tests 8/8; isolated published smoke 17/17; scoped to current Windows identity | Windows DPAPI current-user context | BLOCKING | PASS |
| PI-13 | Invalid or inaccessible configured roots fail closed | Prevent silent writes to another location | Core config/storage | Supply invalid/unwritable explicit paths in isolated tests | Startup/configuration reports controlled failure; no fallback | Core storage and Data Protection test records, automated configuration cases | Test-owned roots | BLOCKING | PASS |
| PI-14 | Data and key state survives Core restart in the isolated run | Verify persistence across process boundary | SQLite + Data Protection | Restart Core against same isolated paths and query expected state | State remains available; no profile path substitution | Published smoke; Data Protection restart test; current identity only | PI-11, PI-12 | BLOCKING | PASS |
| PI-15 | HTTPS and liveness are validated on the published process | Confirm secure listener baseline | Published Core | Request health over TLS; verify plain HTTP is rejected where configured | HTTPS healthy; HTTP not accepted | Step 6 published smoke evidence | Test certificate/isolated run config | BLOCKING | PASS |
| PI-16 | Authentication rejects invalid credentials/origin and admin routes require authorization | Preserve Slice 5/6 boundary in integration | Core HTTP API | Exercise anonymous, valid and invalid sessions; allowed and denied admin operations | Correct status/permission outcome; no secret material in evidence | Slice 6 report; Step 6 published smoke auth checks; synthetic credentials only | Synthetic identities only | BLOCKING | PASS |
| PI-17 | Administrative mutations retain CSRF/origin protections | Prevent cross-site state changes | Core module admin endpoints | Send valid and invalid same-origin/antiforgery requests | Invalid mutation rejected; authorized protected mutation succeeds | Slice 6 security report and Core tests; automated/manual evidence | PI-16 | BLOCKING | PASS |
| PI-18 | Trusted signed fixture installs through the published Core | Verify trust-to-install integration | Publisher trust + package installer + Core API | Configure explicit test publisher and install signed fixture | Trusted valid package accepted; install state is inert/disabled until activation | Owner-reported published lifecycle smoke; Slice 7 evidence | Private test key protected; owner trust config | BLOCKING | PASS |
| PI-19 | Unknown/revoked/tampered signer or package is rejected | Fail closed at trust/integrity boundary | Package inspector + publisher trust | Submit controlled invalid-signature, unknown/revoked signer and tampered content cases | Rejected before module code runs; no active/partial package | Publisher Trust 32/32; archive matrix; Slice 7 smoke evidence; automated negative cases at recorded scope | Synthetic packages only | BLOCKING | PASS |
| PI-20 | In-process module runtime is rejected by Core | Enforce OOP-only managed-module policy | CoreModuleManager | Install/activate/recover signed in-process declaration | Controlled `module_runtime_not_allowed`; no module load/launch | Step 5 policy matrix; Step 6 published smoke | Frozen manifest remains readable | BLOCKING | PASS |
| PI-21 | OOP fixture activates, responds internally, disables and uninstalls | Verify end-to-end module lifecycle | Core + ModuleHost + fixture | Published lifecycle operation sequence and internal typed request/response test | Healthy response; state transitions persist; removal succeeds | Published smoke; internal OOP invocation 2/2; Slice 7 accepted evidence; public generic HTTP invocation is excluded by owner decision | PI-18, PI-20 | BLOCKING | PASS |
| PI-22 | Restart restores persisted module state consistently | Detect split catalog/filesystem state | Core + SQLite + package store | Restart after activation and query module state/health | Installed/enabled/trust state matches committed generation | Published smoke 17/17; owner accepted scope | PI-11, PI-18 | BLOCKING | PASS |
| PI-23 | Controlled fixture/ModuleHost failure leaves Core healthy and recoverable | Demonstrate process fault isolation | Core supervisor + ModuleHost | Cause controlled startup/runtime failure; query Core and perform recovery | Core stays available; error is controlled; recovery/cleanup follows policy | Published smoke; OOP tests; OOP is not a malicious-code sandbox | Test-owned child processes | BLOCKING | PASS |
| PI-24 | No owned Core or ModuleHost process remains after shutdown | Prevent test residue | Published processes | Enumerate exact test-owned process IDs and check after stop | All owned process IDs exited; exit status recorded | Step 6 published smoke 17/17 and owner-accepted evidence | Process ownership tracking | BLOCKING | PASS |
| PI-25 | ZIP extraction rejects unsafe names, collisions, link entries and corrupt content before publication | Ensure untrusted bytes cannot escape staging | Archive inspector/installer | Run synthetic adversarial matrix and verify outside canaries/final store | Rejected cases do not publish or activate; prior graph stays authoritative | Step 2 adversarial extraction matrix: 22 PASS; 128 MiB threshold only code-reviewed | Synthetic small archives | BLOCKING | PASS |
| PI-26 | Storage paths and cleanup reject junction/reparse redirects and preserve external canaries | Protect authorized roots and deletion targets | StoragePathGuard + package lifecycle | Exercise junction at data/staging/store/delete roots and hash external target | Reject before write/delete; canary hashes unchanged | Step 1: 5 junction PASS; direct symlink test is separately SKIP and PI-35 remains NOT VERIFIED | Windows junction support | BLOCKING | PASS |
| PI-27 | Package transactions recover from interruption and malformed journal | Keep one authoritative package generation | PackageSetLifecycle | Inject test-owned journal checkpoints, malformed state and concurrent plans | Recover prior/committed complete graph; fail closed on corrupt journal | Step 3: 18 dedicated PASS; simulated checkpoints, not arbitrary power loss | Test process harness | BLOCKING | PASS |
| PI-28 | Uninstall/retention only removes verified owned files | Prevent deletion of foreign files | PackageSetLifecycle | Add unknown canary, malformed ownership marker or active package; request cleanup | Reject/preserve foreign data; remove only verified obsolete owned bytes | Step 4 safe deletion matrix: 18 scenarios PASS; no TOCTOU guarantee | Test-owned roots | BLOCKING | PASS |
| PI-29 | Invalid configuration/module startup and unexpected worker exit have controlled outcomes | Verify operational recovery paths | Core host + ModuleHost supervisor | Exercise recorded failure cases in isolated published/test runs | No silent fallback; state is consistent; Core remains healthy where specified | Step 6 published smoke; Runtime 265/265; bounded tested failure cases | Exact failure injection and logs | BLOCKING | PASS |
| PI-30 | Secret material is absent from logs and retained evidence | Prevent credential leakage | Smoke harness + logs/evidence | Scan sanitized outputs and assert secret sentinel values absent | Sanitized evidence only; no private keys, passwords, cookies or tokens | Smoke JSON `secretsPrinted=false`; Data Protection log test | Synthetic credentials | BLOCKING | PASS |
| PI-31 | Execution under another Windows identity is demonstrated | Measure user-context coupling | Distribution/runtime | Run under a separate ordinary identity with isolated paths | Successful or specifically diagnosed behavior; no access to original profile | No identity switch/user was created | Separate owner-provided environment | MAJOR proposal | NOT VERIFIED |
| PI-32 | Clean Windows machine and runtime-only installation are demonstrated | Measure machine dependency | Distribution/runtime | Run on clean host with only required shared runtimes, no SDK/repo | Startup and lifecycle pass; prerequisites match documentation | No VM/machine available; candidate framework-dependent | Clean Windows host/runner | MAJOR proposal | NOT VERIFIED |
| PI-33 | Clean-checkout source-to-artifact provenance is demonstrated | Separate dirty-worktree build from release provenance | Build/reproducibility | Build candidate from clean checkout; record commit, dirty state, SDK, command, inventory, content ID | Source state and output mapping are explicit; reproducibility claim limited to measured result | Existing candidate source state says dirty worktree; same-environment comparison only | Clean checkout and pinned SDK | MAJOR proposal | NOT VERIFIED |
| PI-34 | Byte-identical rebuild is demonstrated | Make strong reproducibility claim only if tested | Build pipeline | Repeat build from declared clean input/environment and compare full hashes | All declared reproducibility targets match, or variance explained | Same environment repeat recorded for earlier candidate; no clean checkout proof | Pinned toolchain/clean source | OPTIONAL proposal | NOT VERIFIED |
| PI-35 | Direct symbolic-link rejection is demonstrated independently from junction behavior | Complete Windows link-type coverage | StoragePathGuard | Create a test-owned symlink without elevated privileges and run path/deletion cases | Correct pre-write/pre-delete rejection and canary preservation | Existing direct API test remains skipped for token privilege; junction tests pass separately | Windows symlink-capable identity | MAJOR proposal | NOT VERIFIED |
| PI-36 | Public generic HTTP capability invocation exists | Client-facing arbitrary module call route | Core public API | Not run; owner accepted this as outside Artifact Fitness and no route is in the frozen contract | No requirement for this gate; do not add a route here | Owner decision in Step 6 closure; capability audit documents absence | Future product/API decision if desired | OUT OF GATE | NOT APPLICABLE |

## Owner-ratified acceptance classification for all 36 criteria (2026-10-20)

The owner ratified this policy for this gate. “Block start” means the precondition for 7.2; “block close” means the criterion must pass before Step 7 closes. PI-01–PI-03 were sufficient to authorize beginning 7.2 based on the checks then available; a newly discovered failure is recorded below and must be resolved for acceptance. This policy does not authorize later steps.

| PI | Proposed classification | Reason / timing |
|---|---|---|
| PI-01 | Blocker for final candidate acceptance and closing Step 7 | Pin candidate identity and ensure manifest metadata describes the actual payload. The source publisher/validator correction is implemented and tested, but the retained candidate still has two stale PE classifications; a future authorized candidate is required. |
| PI-02 | Blocker for starting 7.2 | Preserve an exact inventory baseline for the selected candidate; current validator compares manifest file set/hashes. |
| PI-03 | Blocker for starting 7.2 | Confirm historical reference before any new candidate activity; current read-only comparison passes. |
| PI-04 | Blocker for closing Step 7 | Execute all original R3 exclusion scans; they are now complete for the named static artifact exclusions and PI-04 currently passes at that bounded scope. |
| PI-05 | Blocker for closing Step 7 | Required Core/ModuleHost package composition must be present in the final candidate. |
| PI-06 | Blocker for closing Step 7 | The final candidate must start and stop as a published process. |
| PI-07 | Blocker for closing Step 7 | Published Core must launch the packaged ModuleHost. |
| PI-08 | Blocker for closing Step 7 | Runtime source resolution must be shown to use the publish copy; fixture build caveat remains disclosed. |
| PI-09 | Blocker for closing Step 7 | Runtime prerequisites must be identified and stated accurately. |
| PI-10 | Blocker for closing Step 7 | Candidate must not contain development paths, test residue or secrets. |
| PI-11 | Blocker for closing Step 7 | Isolated Core data path must be honored. |
| PI-12 | Blocker for closing Step 7 | Explicit Data Protection key path must be honored without profile fallback. |
| PI-13 | Blocker for closing Step 7 | Invalid/unavailable configured roots must fail closed. |
| PI-14 | Blocker for closing Step 7 | Persisted data/key behavior must survive restart in the accepted scope. |
| PI-15 | Blocker for closing Step 7 | Published service must retain HTTPS/liveness boundary. |
| PI-16 | Blocker for closing Step 7 | Authentication and admin authorization are minimum secure integration criteria. |
| PI-17 | Blocker for closing Step 7 | Admin mutations must retain CSRF/Origin defenses. |
| PI-18 | Blocker for closing Step 7 | A trusted signed fixture must install through the published Core. |
| PI-19 | Blocker for closing Step 7 | Trust and integrity failures must reject before execution. |
| PI-20 | Blocker for closing Step 7 | OOP-only Core-managed runtime policy must be enforced. |
| PI-21 | Blocker for closing Step 7 | Published OOP lifecycle and the accepted internal invocation contract must work. |
| PI-22 | Blocker for closing Step 7 | Module state must restore consistently after restart. |
| PI-23 | Blocker for closing Step 7 | Controlled ModuleHost failure must not terminate Core; this is fault isolation, not sandboxing. |
| PI-24 | Blocker for closing Step 7 | Owned processes must terminate cleanly. |
| PI-25 | Blocker for closing Step 7 | Adversarial archive rejection and non-publication must remain demonstrated. |
| PI-26 | Blocker for closing Step 7 | Required storage-junction and deletion boundaries must pass; direct symlink is tracked separately in PI-35. |
| PI-27 | Blocker for closing Step 7 | Package transaction recovery and malformed-journal fail-closed behavior are required. |
| PI-28 | Blocker for closing Step 7 | Deletion must remain ownership-checked and preserve foreign files. |
| PI-29 | Blocker for closing Step 7 | Published failure/recovery cases must produce controlled, consistent outcomes. |
| PI-30 | Blocker for closing Step 7 | Evidence must not expose secrets. |
| PI-31 | Non-blocking for tested Windows x64 user/environment; limitation mandatory | Another Windows identity remains NOT VERIFIED. Do not claim support for other identities. It becomes a blocker if multi-identity support is claimed. |
| PI-32 | Non-blocking for tested Windows x64 user/environment; limitation mandatory | A clean Windows machine remains NOT VERIFIED. Framework-dependent prerequisites are explicit; do not claim clean-machine support. |
| PI-33 | Blocker for closing Step 7 | A final accepted candidate should map to a clean, identified source state; current candidate was built from a dirty worktree. |
| PI-34 | Non-blocking with limitation documented | Byte-identical rebuild is not a promise in the approved scope; do not call the artifact reproducible. Reclassify if such a promise is made. |
| PI-35 | Blocker for closing Step 7 | Direct symlink remains NOT VERIFIED; the SKIP cannot be accepted as PASS and Freeze §13 is unchanged. No elevation/system changes under this task. |
| PI-36 | Not applicable | Owner accepted that a generic public HTTP capability invocation API is outside Artifact Fitness; no endpoint is to be added for this gate. |

### Ratified start and close policy

**Blockers for starting 7.2**: PI-01–PI-03 established the candidate identity, its original validator/inventory result and unchanged historical baseline needed to begin this static inspection. Owner authorization and candidate/source selection were also provided. The newly found PI-01 metadata mismatch is a close blocker; it does not retroactively invalidate the authorization to inspect.

**Blockers for closing Step 7**: all criteria classified as blocking in the 36-row table, including PI-01–PI-30, PI-33 and PI-35. PI-04's named static exclusion scans are now PASS; PI-35 remains blocking and its SKIP is not PASS; Freeze §13 is unchanged.

**Non-blocking with explicit limitation**: PI-31, PI-32 and PI-34 under the owner-approved bounded Windows x64/current-user acceptance and no byte-reproducibility promise. PI-31/32 cannot support broader identity or clean-machine claims. PI-34 cannot support byte-identical reproducibility claims. PI-36 is NOT APPLICABLE by the owner's accepted Step 6 scope decision.

This classification is owner-ratified. It does not accept PI-04 or PI-35 gaps and does not waive PI-33. No public release follows from this gate; D6's separate distribution restrictions remain in force.

## Reusable evidence package

For every run retain a run identifier, sanitized command transcript, UTC timestamps, OS/build and PowerShell/.NET version, artifact ID/content ID, source commit and dirty-tree indicator, manifest SHA-256, full relative-path/size/SHA-256 inventory, validator output, test/TRX paths and SHA-256, sanitized Core/ModuleHost logs, HTTP outcome summary, process IDs/exit codes, configured data/key root identities (paths may be redacted outside local evidence), before/after artifact comparison, and cleanup result. Never retain private keys, passwords, cookies, bearer tokens, live session state or SQLite files containing test sessions. Hash the sanitized evidence package and add its digest to EV-050.

Evidence provenance labels:

- `AGENT-RUN`: command executed directly by Codex, with command and artifact recorded.
- `OWNER-REPORTED`: test run by the owner; include the owner's supplied sanitized output and its digest, without rewriting it as an agent run.
- `REPOSITORY-RECORDED`: historical evidence already checked into the working tree; cite path and recorded digest.
- `NOT VERIFIED`: no specific supporting evidence.

## Planned execution after separate authorization

These steps are a proposal only. None is started by this Step 7.1 document task.

| Step | Preconditions | Work and tests | Main risks | Exit evidence |
|---|---|---|---|---|
| 7.2 — Prepare deployable package | Owner-ratified policy; candidate identity and inspection scope selected | Static inspection of retained candidate; validate manifest/allowlist/inventory; audit original exclusions and dependency graph; document deployment prerequisites and clean-source provenance procedure. No new candidate is generated in this authorized substep. | Dirty source provenance, semantic manifest errors, excluded content or secrets | Per-exclusion results, exact inventory, runtime/configuration notes, identified gaps; historical baseline unchanged |
| 7.3 — Published end-to-end integration | 7.2 candidate passes structure; test signer/fixture rights and trust material are available; synthetic config is ready | Run Core/ModuleHost from candidate against exclusive data/key roots; auth/admin controls; OOP fixture install/activation/restart/failure/disable/uninstall; before/after artifact comparer | Accidental repo/profile data use, secret leakage, orphaned processes, invalid fixture trust | Sanitized E2E summary, test/TRX hashes, process cleanup and candidate before/after comparison |
| 7.4 — Independence and security boundaries | 7.3 passes; owner supplies approved alternate-user/clean-host environment if those gates are required | Test independent run directory, no-repo execution, runtime-only prerequisites, alternate identity/clean host where available; run approved path/archive/trust/failure negatives | DPAPI user binding, unavailable clean host, TOCTOU/platform-specific assumptions | Per-environment results with PASS/FAIL/NOT VERIFIED; negative-case canary hashes; no broader portability claim |
| 7.5 — Evidence and closure review | 7.2–7.4 results complete; all failures and skips retained; owner ratifies residual limitations | Reconcile matrix, artifacts and evidence ledger; check every blocking criterion; `git diff --check`; report candidate disposition and remaining risk to owner | Promotion of an unverified item, stale hashes, accidental scope/status drift | Final evidence index and explicit owner acceptance request; Slice 8 remains IN PROGRESS until owner acceptance |

## Current 7.1 verification snapshot

- Latest candidate: `%TEMP%\hispania-slice8-step6-candidate-9e471e99e46a48739e72d507afa7baa3-publish`; content ID `9459242A34B042402F79D140CEED7D16BDEB149616EC7B08573188A34D31BED0`; validator `passed=true`, `issues=[]`; manifest SHA-256 `8B4196461AB0EA5A8E5BF92931EC0756839E82F82208309B00539032E83185DD`.
- Historical Slice 7 artifact: `%TEMP%\hispania-slice7-accepted-20261018-publish`; content ID `44B43088658F2F70FE8A0745EA5DFC3B1519CBF10B9347F3F4B88835EEDB3B12`; manifest SHA-256 `22D05298F7329BE8C2892BDEE33EBE71C6851EBC5D7A6B6BBF2A49C34F45FE87`; comparator `passed=true`, `added=[]`, `missing=[]`, `modified=[]`, `selfTest=[]`.
- TEMP contained 114 directories matching `hispania-slice8-*` at inspection. All were preserved; this task created no temporary roots.
- Repository branch `codex/hispania-v1-baseline-consolidation`, HEAD `ef7365bb5e7d7551cb1093519743cc1885757b5b`; pre-existing worktree changes remain. This task edits documentation only and does not stage, commit or push.

**Step 7.1 result at that historical snapshot: PASS — contract and evidence map prepared.** It was not a pass of Steps 7.2–7.5 and did not close Slice 8. The owner later ratified the policy and authorized Step 7.2; the inspection results are recorded below. The authorization does not extend to 7.3–7.5.

## Step 7.2 — Static deployable-package inspection (2026-10-20)

### Candidate identity and inventory

Inspected read-only: `%TEMP%\hispania-slice8-step6-candidate-9e471e99e46a48739e72d507afa7baa3-publish`.

| Property | Observed value |
|---|---|
| Artifact | `hispania.core` |
| Content ID | `9459242A34B042402F79D140CEED7D16BDEB149616EC7B08573188A34D31BED0` |
| Manifest SHA-256 | `8B4196461AB0EA5A8E5BF92931EC0756839E82F82208309B00539032E83185DD` |
| Manifest sourceCommit | `ef7365bb5e7d7551cb1093519743cc1885757b5b` |
| Target | `net10.0`, `win-x64`, framework-dependent |
| Manifest payload | 18 files |
| Actual root | 19 files including `core-artifact-manifest.json` |
| Inventory comparison | `missing=[]`, `unexpected=[]`, `modified=[]`; all 18 payload hashes and sizes match |
| Inventory evidence | [Candidate inventory JSON](candidate-inventory-9459242a34b042402f79d140ceed7d16bdeb149616ec7b08573188a34d31bed0.json), SHA-256 `6EBE9633D2F38FD5B8D4A1A67B29AA16B74489E84EADD665DA7F8A6600369AB5` |

The 18 payload entries are the Core executable/DLL, Core runtime/dependency manifests, ModuleHost executable payload and runtime/dependency manifests, ModuleSystem and Platform contracts, Runtime, Microsoft.Data.Sqlite, NuGet.Versioning, SQLitePCLRaw managed libraries/provider, and `e_sqlite3.dll`. The machine-readable inventory records every relative path, size and SHA-256. No candidate file was changed.

The Core artifact validator was rerun against the pinned allowlist: `passed=true`, `issues=[]`, content ID unchanged. An independent PE metadata check found `e_sqlite3.dll` is a native PE with no CLI metadata, while the candidate manifest classifies it as `managedAssembly`. Thus byte inventory/content identity are verified, but PI-01 fails the semantic file-type agreement check. The validator did not detect this mismatch because its accepted enum/path classification does not establish PE metadata type. This is a concrete manifest defect; this task does not repair or republish it.

The historical Slice 7 comparator was rerun read-only: `passed=true`, `added=[]`, `missing=[]`, `modified=[]`, `selfTest=[]`; content ID `44B43088658F2F70FE8A0745EA5DFC3B1519CBF10B9347F3F4B88835EEDB3B12`; manifest SHA-256 `22D05298F7329BE8C2892BDEE33EBE71C6851EBC5D7A6B6BBF2A49C34F45FE87`.

### PI-04 — R2 and R3 static results

The six R2 rows in the table above each pass the exact path and SHA-256 absence check against both artifact manifests. A separate source-tree check confirms each listed file exists at the CSV SHA-256; this proves those bytes remain in the repository but are absent from the two retained publishes. It is not a rights determination.

For R3, the audit examined the complete candidate file set, allowlist and manifest dependency graph, recursive current project-reference graph, PE assembly references and resources, runtime configurations, route templates and candidate bytes as ASCII and UTF-16LE. The source graph is `apps/core` → Runtime, ModuleHost (build/copy target), ModuleSystem contracts and Platform contracts; Runtime and ModuleHost reference only the two contracts. It contains no Server, Media, Inventory, platform SQLite, Identity/Authorization application, Kernel, Jobs, worker-agent or distributed-work project edge. The manifest's unique dependencies are the .NET 10 and ASP.NET Core 10 shared frameworks; direct packages Microsoft.Data.Sqlite 10.0.12 and NuGet.Versioning 7.9.0; transitive Microsoft.Data.Sqlite.Core 10.0.12 and SQLitePCLRaw bundle/core/lib/provider 2.1.12; and the three allowed project dependencies Runtime and the two contracts. The validator accepted this graph with no policy issues.

Thirteen DLLs were inspected; zero forbidden PE assembly references were found. Nine route literals were extracted from Core and matched to source registrations: `/health/live`, the three auth routes `/api/v1/auth/login`, `/api/v1/auth/logout`, `/api/v1/auth/me`, and module routes `/api/v1/auth/modules`, `/api/v1/auth/modules/install`, `/api/v1/auth/modules/{id}`, `/api/v1/auth/modules/{id}/enable`, `/api/v1/auth/modules/{id}/disable`. No Media, Inventory, Library or legacy Server route was found. Core has no embedded manifest resources. The enumerated resources in other DLLs are SQLite/NuGet resource bundles and `ILLink.Descriptors.xml`. Migration strings in Core map to its SQLite `core_schema_migrations`; no Media-owned provider/schema/migration artifact was found. Candidate content scans found no named excluded binaries, demo/media assets, npm bundles, test-host content or forbidden business/provider markers. The only native payload is the allowlisted SQLite `e_sqlite3.dll`.

**PI-04 aggregate: PASS for the named static exclusion requirements of this exact candidate.** Every R2/R3 category in the source plan and allowlist has an individual scoped result in the table above. This does not clear rights, attest to separate legacy Server runtime operation, or claim a general malicious-code semantic audit. PI-04's owner-set blocker is satisfied at this static scope.

The discrepancy that the manifest calls native `e_sqlite3.dll` a `managedAssembly` is tracked separately as PI-01 FAIL. It is a manifest semantic classification issue, not the presence of a forbidden dependency.

#### PI-01 semantic-classification correction — 2026-10-20

The publisher and validator now classify PE content from PE/CLI metadata. The frozen manifest schema already permits `nativeRuntime`; no schema or content-identity algorithm changed. Focused tests pass 34/34. The corrected validator identifies two stale labels in the retained Slice 8 candidate: native `Hispania.Core.exe` is declared `content` (actual `applicationHost`), and native `e_sqlite3.dll` is declared `managedAssembly` (actual `nativeRuntime`). The same two discrepancies appear in the historical Slice 7 manifest. Both retained artifacts still match their recorded byte inventories and hashes; neither manifest or payload was rewritten. This does not retroactively alter their historical cryptographic evidence. PI-01 remains FAIL for the current candidate and requires a newly published, validated candidate before acceptance.

### Runtime and deployment prerequisites (static, not a deployment run)

- Windows x64 and installed .NET 10 shared frameworks are required. The candidate is framework-dependent: Core requires `Microsoft.NETCore.App` and `Microsoft.AspNetCore.App` 10.0.0; ModuleHost requires `Microsoft.NETCore.App` 10.0.0. `dotnet` must be discoverable to launch ModuleHost. The SDK is not required for routine launch, but was used in development/fixture construction; no clean-machine test was done.
- Launch Core from the package directory with `Hispania.Core.exe`. The packaged ModuleHost default is `module-host\Hispania.ModuleHost.dll`; leave `HISPANIA_CORE_MODULES__MODULEHOSTPATH` unset to use it. ModuleHost is launched by Core through `dotnet`.
- Core serves HTTPS only, bound to loopback (`127.0.0.1` by default, port `5070`); configured listen address must remain loopback. `GET https://127.0.0.1:5070/health/live` is the health check. There is no readiness endpoint in the inspected contract. Plain HTTP is not the service endpoint.
- HTTPS startup requires `HISPANIA_CORE_HTTPS_CERTIFICATE_PATH` to identify an absolute, accessible certificate file with a private key and server-auth usage. Optional `HISPANIA_CORE_HTTPS_CERTIFICATE_PASSWORD` is sensitive configuration; provision it without command-line arguments or logs. The host identity needs access to the certificate private key.
- `HISPANIA_CORE_DATA_DIRECTORY` optionally sets an absolute data root outside the publish; it must be writable for SQLite and runtime state. Without it, Core uses `%LOCALAPPDATA%\Hispania\Core`.
- `HISPANIA_CORE_DATA_PROTECTION_KEYS_DIRECTORY`, when set, must be an absolute writable directory outside the publish. On Windows its keys are protected with DPAPI for the current user and are not portable to another user by this evidence. If absent, ASP.NET Core's default profile-backed key repository is used. Directory isolation is not a separate at-rest encryption guarantee; DPAPI behavior is user-context bound.
- `HISPANIA_CORE_LISTEN_ADDRESS`, `HISPANIA_CORE_PORT`, `HISPANIA_CORE_SHUTDOWN_TIMEOUT_SECONDS`, `HISPANIA_CORE_LOG_LEVEL`, `HISPANIA_CORE_LOGIN_RATE_LIMIT`, and `HISPANIA_CORE_LOGIN_RATE_WINDOW_SECONDS` are optional host settings. Only loopback listen addresses and validated ranges are accepted. `HISPANIA_CORE_MODULES__TRUSTEDPUBLISHERKEYS__<publisherId>` supplies a trusted publisher public key; `HISPANIA_CORE_MODULES__REVOKEDPUBLISHERS__<index>` identifies revocations. Public trust keys do not replace permissions. Do not include the signing private key in the Core package.
- `Hispania.Core.exe bootstrap-admin` performs interactive local bootstrap with username/password read from the console (password is not supplied as an argument); it initializes the configured Core store. Use synthetic credentials and the isolated data directory. For normal operation, start the executable without that command. Stop with Ctrl+C and verify the process exited; do not install a Windows service as part of this static substep.
- Package directory requires read/execute; data and key directories require only the access needed for database/key persistence; the certificate private key must be readable by the same Windows identity. No ACL changes or permission elevation were performed.
- Scope proven remains Windows x64/current tested user only. No support claim is made for another identity, a clean host, Linux, ARM64, portability of DPAPI keys, or hostile native modules. OOP is not a sandbox.

### Clean-source provenance proposal (not executed)

The current candidate records source commit `ef7365…`, but its source worktree was dirty; the manifest commit field is therefore not proof that the bytes came only from that commit. Preserve the current dirty checkout untouched. When separately authorized: (1) inventory and protect all current staged, unstaged and untracked changes; (2) identify the exact Slice 8 source/documentation changes intended for the candidate and keep unrelated changes out; (3) obtain authorization to create a local commit containing only that reviewed change set; (4) create a separate clean worktree at the resulting commit, verify `git status --porcelain` is empty, record SDK/tool versions and exact publish command; (5) publish to a new unique output directory, never the retained candidate root; (6) validate manifest/allowlist, full inventory and content ID; (7) compare the historical Slice 7 artifact read-only. Do not use reset, clean, stash, or rewrite the existing branch as part of this proposal. PI-33 remains NOT VERIFIED until such a clean-source candidate is produced and mapped.

### Step 7.2 disposition

Static inspection is **PARTIAL**: PI-04's named exclusions pass, but PI-01 has a manifest file-type mismatch, PI-33 is NOT VERIFIED because the source worktree is dirty, and PI-35 remains NOT VERIFIED. No new publish, build, product/test change, smoke, installation, or deployment was performed. Steps 7.3–7.5 and Slice 9 remain unstarted. See EV-050 Step 7.2 for repository and worktree state.

## PI-33 — Local source worktree audit and clean-provenance plan — 2026-10-20

### Read-only Git state

- Repository: `.`.
- Branch: `codex/hispania-v1-baseline-consolidation`.
- HEAD: `ef7365bb5e7d7551cb1093519743cc1885757b5b`.
- Upstream: none configured.
- Status: **24 tracked paths modified, 53 untracked paths, 0 staged, 0 deleted**. All tracked changes are unstaged. No merge, rebase, cherry-pick, revert or sequencer marker exists.
- `git diff` is against the above HEAD. The prior PI-01 change passed `git diff --check`; this audit adds no production/test changes.
- The tree contains Slice 8 implementation plus prior Slice 7 acceptance documentation/evidence. It is unsafe to treat every local path as one candidate commit.

Classification: **A** = candidate runtime/tool source required for the current Slice 8 candidate; **B** = tests or test harness needed to validate that source; **C** = Slice 8 policy, report or evidence; **D** = Slice 7/historical material outside the Slice 8 candidate build. Each row records the consequence of excluding and including the path.

### File-by-file inventory

| Path | Status | Class | Purpose/dependencies; exclusion and inclusion risks |
|---|---:|:---:|---|
| `apps/core/Configuration/CoreHostOptions.cs` | M | A | Reads DP options; depends on new `CoreDataProtectionOptions.cs`. Exclude: Core build/configuration gap. Include: changes host settings. |
| `apps/core/Configuration/CoreStorageOptions.cs` | M | A | Calls `StoragePathGuard`. Exclude: DATA_DIRECTORY boundary is incomplete. Include: changes accepted storage paths. |
| `apps/core/Hosting/CoreHost.cs` | M | A | Registers DPAPI key storage and OOP-only module policy. Exclude: approved Step 5/6 runtime behavior absent. Include: changes host startup/runtime policy. |
| `apps/core/Http/CoreModuleEndpoints.cs` | M | A | Returns controlled administrative errors for disallowed module runtime. Exclude: Core API policy result incomplete. Include: changes module endpoint responses. |
| `apps/core/Modules/CoreModuleManager.cs` | M | A | Guards staging and validates runtime at install/activation. Exclude: storage/runtime activation controls absent. Include: changes installation/activation behavior. |
| `apps/core/Persistence/SqliteCorePersistence.cs` | M | A | Guards Core data-root initialization. Exclude: persistence path boundary incomplete. Include: changes startup validation. |
| `apps/core/Configuration/CoreDataProtectionOptions.cs` | ?? | A | New optional key-directory parser/validator; required by CoreHostOptions. Exclude: dependent code does not build and isolated DP path is absent. Include: introduces a host configuration option. |
| `core/runtime/StoragePathGuard.cs` | ?? | A | Shared storage/reparse-point checks used by Core and Runtime. Exclude: dependent A files fail to build and storage safeguards disappear. Include: affects storage path and cleanup operations. |
| `core/runtime/ExtensionPackageInstaller.cs` | M | A | Reuses archive inspector and path guard. Exclude: guarded extraction/install absent. Include: changes package acceptance and cleanup. |
| `core/runtime/ModuleExceptions.cs` | M | A | Defines controlled disallowed-runtime exception consumed by Core/API. Exclude: explicit policy error contract missing. Include: adds a public Runtime exception. |
| `core/runtime/ModuleExtensionCatalog.cs` | M | A | Package lifecycle, storage ownership and OOP-only activation policy. Exclude: Steps 3–5 behavior absent. Include: changes module activation and lifecycle. |
| `core/runtime/PackageArchiveInspector.cs` | M | A | Shared ZIP entry/path validation. Exclude: adversarial extraction gate incomplete. Include: changes packages eligible for install. |
| `core/runtime/PackageRetentionOwnerV1.cs` | M | A | Validates ownership before retention cleanup. Exclude: safe cleanup ownership incomplete. Include: changes cleanup eligibility. |
| `core/runtime/PackageSetLifecycle.cs` | M | A | Transaction, recovery, concurrency and guarded cleanup. Exclude: Step 3/4 package behavior absent. Include: changes persistent package-set operations. |
| `tools/core-artifact-validator/Program.cs` | M | A | Publisher and validator; PE/CLI classification and semantic manifest checks. Exclude: PI-01 validator gap returns. Include: changes manifest creation/validation; the future manifest content ID will be recalculated under the unchanged algorithm. |
| `docs/architecture/HISPANIA-V1-CORE-DEPENDENCY-ALLOWLIST.md` | M | C | Updates Slice 7 acceptance narrative; policy JSON is unchanged. Exclude: owner-accepted scope narrative is stale. Include: documentation only. |
| `docs/architecture/HISPANIA-V1-EVIDENCE-LEDGER.md` | M | C | Phase status and EV-050 history. Exclude: project evidence/roadmap traceability is stale. Include: documentation only; review for prior unrelated ledger changes before committing. |
| `docs/architecture/HISPANIA-V1-PHASE1-SLICE7-MODULE-MANAGER-REPORT.md` | M | D | Records prior Slice 7 owner acceptance. Exclude: historical evidence narrative incomplete. Include: prior-slice material in a Slice 8 source commit without affecting runtime. |
| `tests/core-artifact-validator/ArtifactValidatorTests.cs` | M | B | Publisher/validator and PE mismatch tests; native fixture uses installed Windows `coreclr.dll`. Exclude: PI-01 regression untested. Include: test-only; must not enter publish. |
| `tests/core/CorePersistenceTests.cs` | M | B | DATA_DIRECTORY and DP options tests. Exclude: Core path/DP regressions untested. Include: test-only changes. |
| `tests/core/Slice7ModuleLifecycleTests.cs` | M | B | Module endpoint/lifecycle compatibility tests. Exclude: prior-slice integration regression less verified. Include: test-only. |
| `tests/package-lifecycle-process-host/Program.cs` | M | B | Test-owned process checkpoint/concurrency helper. Exclude: process recovery tests cannot run. Include: test helper; keep out of Core publish. |
| `tests/unit/Hispania.Runtime.Tests.csproj` | M | B | Adds references/copy rules for private module fixture projects. Exclude: fixture isolation tests lack inputs. Include: expands test build graph only. |
| `tests/unit/ModuleHostPlatformJobContributionTests.cs` | M | B | Updates expectations for current module trust/policy behavior. Exclude: stale regression expectation remains. Include: changes test semantics; review alongside Runtime policy. |
| `tests/unit/ModuleInfrastructureTests.cs` | M | B | Runtime policy/activation compatibility cases. Exclude: OOP-only policy lacks coverage. Include: test-only. |
| `tests/unit/PackageLifecycleIntegrationTests.cs` | M | B | Process recovery, concurrency, ownership and cleanup tests. Depends on process host. Exclude: Step 3/4 regression evidence absent. Include: test-only processes and temporary files. |
| `tests/core/CoreDataProtectionTests.cs` | ?? | B | New DP default/custom-directory tests. Depends on new Core options and host setup. Exclude: key-ring isolation behavior untested. Include: test-only. |
| `tests/private-module-fixtures/dependency-v1/Hispania.PrivateFixture.Dependency.csproj` | ?? | B | Builds dependency v1. Required by module-v1 test. Exclude: first dependency version unavailable. Include: test build graph only. |
| `tests/private-module-fixtures/dependency-v1/PrivateDependency.cs` | ?? | B | Source for dependency v1. Required by its project. Exclude: first version fixture absent. Include: test-only code. |
| `tests/private-module-fixtures/dependency-v2/Hispania.PrivateFixture.Dependency.csproj` | ?? | B | Builds dependency v2. Required by module-v2 test. Exclude: version comparison unavailable. Include: test build graph only. |
| `tests/private-module-fixtures/dependency-v2/PrivateDependency.cs` | ?? | B | Source for dependency v2. Required by its project. Exclude: second version fixture absent. Include: test-only code. |
| `tests/private-module-fixtures/module-v1/Hispania.PrivateFixture.ModuleV1.csproj` | ?? | B | Builds module fixture against dependency v1. Exclude: first module isolation case unavailable. Include: test build graph only. |
| `tests/private-module-fixtures/module-v1/PrivateModule.cs` | ?? | B | Source for module fixture v1. Exclude: first module behavior unavailable. Include: test-only code. |
| `tests/private-module-fixtures/module-v2/Hispania.PrivateFixture.ModuleV2.csproj` | ?? | B | Builds module fixture against dependency v2. Exclude: second module isolation case unavailable. Include: test build graph only. |
| `tests/private-module-fixtures/module-v2/PrivateModule.cs` | ?? | B | Source for module fixture v2. Exclude: second module behavior unavailable. Include: test-only code. |
| `tests/unit/PackageArchiveAdversarialTests.cs` | ?? | B | ZIP/path/entry/size adversarial tests; depends on inspector/installer. Exclude: Step 2 cases untested. Include: test-only archives under owned temp roots. |
| `tests/unit/PrivateModuleDependencyIsolationTests.cs` | ?? | B | ALC and OOP dependency tests; depends on four fixture projects and Runtime. Exclude: isolation regression untested. Include: test-only; results must map to exact source commit. |
| `tools/test-slice7-published-fixture-lifecycle.ps1` | ?? | B | Prior accepted Slice 7 fixture smoke harness. Exclude: repeat compatibility smoke unavailable. Include: test harness only; it builds fixture source and is not distribution content. |
| `tools/test-slice8-published-artifact-fitness.ps1` | ?? | B | Published candidate fitness harness. Exclude: published lifecycle verification unavailable. Include: test tooling only; requires isolated runtime inputs and must not be packaged. |
| `tools/verify-slice8-artifact-baseline.ps1` | ?? | B | Read-only manifest inventory/baseline comparison. Exclude: retained artifact integrity cannot be rechecked. Include: verification tool only. |
| `docs/architecture/HISPANIA-V1-PHASE1-SLICE8-ARTIFACT-ISOLATION-REPORT.md` | ?? | C | Slice 8 report/status/limitations. Exclude: formal evidence incomplete. Include: documentation only; align candidate identity before final acceptance. |
| `docs/architecture/evidence/slice7/README.md` | ?? | D | Index for prior Slice 7 evidence. Exclude: historical evidence navigation suffers. Include: prior-slice documentation only. |
| `docs/architecture/evidence/slice7/published-core-fixture-smoke-20261018.json` | ?? | D | Owner-supplied sanitized Slice 7 smoke record. Exclude: owner acceptance evidence not co-located. Include: historical only; retain owner-reported provenance and do not describe as agent execution. |
| `docs/architecture/evidence/slice8/core-artifact-baseline-win-x64.json` | ?? | C | Pinned historical accepted artifact baseline. Exclude: integrity comparison has no reference. Include: records historic identity, not current candidate. |
| `docs/architecture/evidence/slice8/step2-adversarial-extraction-matrix.md` | ?? | C | Step 2 adversarial matrix. Exclude: extraction evidence incomplete. Include: documentation/evidence only. |
| `docs/architecture/evidence/slice8/step3-transactions-concurrency.md` | ?? | C | Step 3 transaction/concurrency results. Exclude: acceptance rationale missing. Include: evidence only. |
| `docs/architecture/evidence/slice8/step3/artifact-after.json` | ?? | C | Step 3 post-operation artifact comparison. Exclude: integrity chain incomplete. Include: evidence only; not a new candidate inventory. |
| `docs/architecture/evidence/slice8/step3/artifact-comparator-selftest.json` | ?? | C | Step 3 comparator self-test result. Exclude: detector evidence missing. Include: evidence only. |
| `docs/architecture/evidence/slice8/step4-safe-deletion-matrix.md` | ?? | C | Step 4 ownership/deletion cases. Exclude: deletion evidence incomplete. Include: evidence only. |
| `docs/architecture/evidence/slice8/step4/artifact-after.json` | ?? | C | Step 4 post-operation artifact comparison. Exclude: integrity chain incomplete. Include: evidence only. |
| `docs/architecture/evidence/slice8/step4/artifact-before.json` | ?? | C | Step 4 pre-operation artifact comparison. Exclude: before/after proof incomplete. Include: evidence only. |
| `docs/architecture/evidence/slice8/step4/artifact-comparator-selftest.json` | ?? | C | Step 4 comparator self-test. Exclude: detector evidence missing. Include: evidence only. |
| `docs/architecture/evidence/slice8/step5/artifact-after.json` | ?? | C | Step 5 post-operation artifact comparison. Exclude: published-byte integrity evidence incomplete. Include: evidence only. |
| `docs/architecture/evidence/slice8/step5/artifact-before.json` | ?? | C | Step 5 pre-operation artifact comparison. Exclude: before/after proof incomplete. Include: evidence only. |
| `docs/architecture/evidence/slice8/step5/artifact-validator-after.txt` | ?? | C | Step 5 post-operation validator output. Exclude: validator evidence missing. Include: evidence only. |
| `docs/architecture/evidence/slice8/step5/artifact-validator-before.txt` | ?? | C | Step 5 pre-operation validator output. Exclude: comparison record incomplete. Include: evidence only. |
| `docs/architecture/evidence/slice8/step5/module-loading-execution-matrix.md` | ?? | C | Step 5 runtime/load matrix. Exclude: runtime policy evidence incomplete. Include: evidence only. |
| `docs/architecture/evidence/slice8/step6/alc-correction-results-20261019.json` | ?? | C | ALC repeat results. Exclude: regression closure evidence incomplete. Include: prior run data; check machine paths before external sharing. |
| `docs/architecture/evidence/slice8/step6/alc-reference-investigation-20261019.json` | ?? | C | ALC reference investigation. Exclude: diagnostic evidence missing. Include: evidence only; may include local paths. |
| `docs/architecture/evidence/slice8/step6/alc-unload-diagnosis.md` | ?? | C | ALC decision and limitation. Exclude: accepted lifecycle behavior undocumented. Include: evidence only. |
| `docs/architecture/evidence/slice8/step6/artifact-fitness-matrix.md` | ?? | C | Artifact Fitness criterion matrix. Exclude: Step 6 status not traceable. Include: evidence only; keep NOT VERIFIED labels. |
| `docs/architecture/evidence/slice8/step6/candidate-core-artifact-manifest.json` | ?? | C | Earlier candidate manifest. Exclude: earlier candidate identity lost. Include: historical only; not latest candidate. |
| `docs/architecture/evidence/slice8/step6/candidate-dp-isolated-manifest-20261019.json` | ?? | C | Earlier DP-isolated candidate manifest. Exclude: that run identity lost. Include: historical candidate only. |
| `docs/architecture/evidence/slice8/step6/candidate-dp-isolated-summary-20261019.json` | ?? | C | DP-isolated smoke summary. Exclude: run result lost. Include: evidence only; confirm sanitization before external disclosure. |
| `docs/architecture/evidence/slice8/step6/candidate-inventory.json` | ?? | C | Earlier candidate inventory. Exclude: earlier inventory untraceable. Include: historical only; risk of confusing it with the latest candidate. |
| `docs/architecture/evidence/slice8/step6/candidate-validator-report.json` | ?? | C | Earlier candidate validation result. Exclude: old validator result lost. Include: historical only, superseded by later evidence. |
| `docs/architecture/evidence/slice8/step6/capability-invocation-audit-20261019.json` | ?? | C | Internal OOP invocation audit. Exclude: Step 6 internal response evidence lost. Include: evidence only; not a public HTTP capability API claim. |
| `docs/architecture/evidence/slice8/step6/evidence-file-hashes.json` | ?? | C | Hash index for prior evidence files. Exclude: integrity map lost. Include: hashes become stale if referenced files are altered. |
| `docs/architecture/evidence/slice8/step6/historical-baseline-after.json` | ?? | C | Historical baseline result after Step 6. Exclude: post-check absent. Include: read-only evidence. |
| `docs/architecture/evidence/slice8/step6/historical-baseline-before.json` | ?? | C | Historical baseline result before Step 6. Exclude: before/after chain incomplete. Include: read-only evidence. |
| `docs/architecture/evidence/slice8/step6/published-candidate-smoke-dp-isolated-20261019.json` | ?? | C | DP-isolated published smoke output. Exclude: run evidence missing. Include: evidence only; inspect local paths before external sharing. |
| `docs/architecture/evidence/slice8/step6/published-candidate-smoke-sanitized.json` | ?? | C | Sanitized published smoke output. Exclude: smoke evidence missing. Include: evidence only; preserve its provenance label. |
| `docs/architecture/evidence/slice8/step6/runtime-stabilization-20261019.json` | ?? | C | Runtime stabilization results. Exclude: test history incomplete. Include: retain failed-run history as well as passes. |
| `docs/architecture/evidence/slice8/step6/same-environment-reproducibility.json` | ?? | C | Same-environment comparison. Exclude: measured limitation undocumented. Include: not proof of clean-checkout reproducibility. |
| `docs/architecture/evidence/slice8/step6/source-worktree-state.json` | ?? | C | Source state for a prior candidate. Exclude: provenance limitation unrecorded. Include: date it clearly to avoid implying it is current. |
| `docs/architecture/evidence/slice8/step7/candidate-inventory-9459242a34b042402f79d140ceed7d16bdeb149616ec7b08573188a34d31bed0.json` | ?? | C | Retained candidate inventory. Exclude: exact hash map missing. Include: describes the old semantic labels; do not use as evidence for a corrected candidate. |
| `docs/architecture/evidence/slice8/step7/published-integration-gate.md` | ?? | C | PI gate and audit record. Exclude: acceptance criteria/provenance incomplete. Include: evidence only; preserve owner-ratified classifications. |

**Lectura por archivo:** la columna `Purpose/dependencies; exclusion and inclusion risks` da motivo, dependencia y riesgo individual. El escaneo de secretos cubrió cada una de las 77 rutas: cero coincidencias de marcadores; no se reportó ningún contenido coincidente. Relación con Slice 8: clases A y B son fuente/herramientas y validación directa; clase C es el contrato/evidencia de esta ejecución; clase D preserva trazabilidad histórica Slice 7, sólo auditoría. La revisión de la lista de estado no detectó archivos cuyo propósito o dependencia quedaran sin clasificar.
### Files ignored by Git

`git status --ignored` reported 14,123 ignored paths, primarily generated `bin/`, `obj/`, TestResults, frontend/build outputs and `.artifacts/`. The ignored `.artifacts/` tree contains 25 directories and about 2,399 Git-ignored paths, including retained Phase 19 and Slice 8 outputs. They are not candidate source and must not be added wholesale; some historical build/test outputs are evidence and are to be preserved. Builds in a clean worktree must restore/build from tracked source, not copy these outputs. `tools/dev-launcher/tmdb-token.txt` is ignored by policy but absent. No ignored `.pfx`, `.pem`, `.key`, `.p12`, `.db` or `.sqlite` path was identified in the tracked-change/untracked-name scan. A filename/literal-marker scan printed no secret values and found no matching private-key or bearer-token literals. The package candidates live under `%TEMP%`, outside this repository status.

### Dependency map and candidate selection

The Core build requires the A source under `apps/core/`, `core/runtime/`, `CoreDataProtectionOptions.cs`, and `StoragePathGuard.cs`; Core project references pull Runtime and contracts. OOP module execution uses the existing ModuleHost project/artifact and the new Runtime policy, with no ModuleHost source change in this worktree. The publisher/validator requires `tools/core-artifact-validator/Program.cs` and the unchanged manifest schema/policy JSON. DP isolation requires both Core option/registration files and Windows DPAPI support; it does not make DPAPI keys portable across Windows identities. Publisher trust continues to use the existing signature/trust contracts; OOP-only enforcement is in the Runtime catalog and Core activation path. Package lifecycle source changes are in the Runtime files above.

Regression validation requires the modified Core/Runtime tests, process host, Runtime test project, four fixture projects and their four source files, adversarial ZIP tests, ALC isolation tests, DP tests and artifact validator tests. The three `tools/test-*`/baseline scripts are validation inputs only; none is part of the product package. No new production dependency was found in the reviewed source changes; package verification should still restore from declared project files and validate the generated `.deps.json` in a future authorized candidate.

No file is conclusively classified as an unrelated source-code change from the path/diff review; the Slice 7 report and evidence are clearly outside the Slice 8 runtime candidate but are relevant historical acceptance records (D). The 24 modified and 53 untracked paths all need explicit owner-reviewed selection before a commit. In particular, do not stage `-A` and do not infer that an untracked evidence file is source input.

### Proposed clean provenance sequence — not executed

1. Preserve this checkout and its complete status/diffs as the source of record; do not reset, clean, stash, switch branches or rewrite history.
2. Owner reviews the A/B/C/D inventory and chooses the exact commit set. Resolve any disputed/uncertain path explicitly; exclude D and all ignored outputs from the candidate source commit unless the owner separately directs otherwise.
3. With separate authorization, stage only enumerated paths (never `git add -A`) and create a local commit with an auditable message. Verify staged diff and commit tree; leave unselected local changes untouched.
4. With separate authorization, create a separate worktree at that exact commit. Verify its HEAD and empty porcelain status; record SDK/runtime, OS, RID, `dotnet --info`, restore inputs and exact publish command. Do not copy `bin/obj` or `.artifacts` into it.
5. Publish to a new unique output directory. Record source commit/tree, SDK, dependency graph, full relative file inventory, manifest hash, per-file SHA-256 and content ID. Do not overwrite the retained candidate or Slice 7 historical artifact.
6. Run the allowlist and corrected semantic validator. PI-01 must pass for every manifest entry. Re-run named R2/R3 checks for PI-04 against that exact candidate and compare the historical Slice 7 artifact read-only.
7. Record evidence and candidate identity in a later documentation-only change, so documenting the candidate does not alter the source commit from which it was produced. Do not claim binary reproducibility unless separately measured.

The clean commit, worktree creation, publication, validator run against a new candidate and any later documentation commit all require separate authorization under the current scope. PI-33 remains NOT VERIFIED until those authorized steps produce a clean source-to-candidate mapping. Current readiness result: **PARTIAL**.

## PI-33 — Selección explícita para commit local de procedencia (2026-10-20)

### Estado contrastado

El inventario de rutas estándar coincide: 24 modificaciones rastreadas y 53 rutas no rastreadas, cero staged, cero eliminaciones (77 únicas). Rama `codex/hispania-v1-baseline-consolidation`; HEAD `ef7365bb5e7d7551cb1093519743cc1885757b5b`; no upstream configurado. No se alteró el índice ni se creó commit. No hay rutas sin clasificar; la propuesta incluye explícitamente cada ruta actual.

### INCLUIR — conjunto exacto propuesto (77 rutas)

Se propone incluir todos los cambios actuales para que código de Slice 8, pruebas/harnesses y evidencia de procedencia formen un estado local identificable. El grupo D es evidencia histórica de auditoría, no código/payload del Core. ModuleHost no tiene fuentes modificadas; el proyecto existente sigue en el grafo de publicación.

### A — fuente necesaria para producto/herramientas de publicación (15)

- `apps/core/Configuration/CoreDataProtectionOptions.cs` — `??`
- `apps/core/Configuration/CoreHostOptions.cs` — `M`
- `apps/core/Configuration/CoreStorageOptions.cs` — `M`
- `apps/core/Hosting/CoreHost.cs` — `M`
- `apps/core/Http/CoreModuleEndpoints.cs` — `M`
- `apps/core/Modules/CoreModuleManager.cs` — `M`
- `apps/core/Persistence/SqliteCorePersistence.cs` — `M`
- `core/runtime/ExtensionPackageInstaller.cs` — `M`
- `core/runtime/ModuleExceptions.cs` — `M`
- `core/runtime/ModuleExtensionCatalog.cs` — `M`
- `core/runtime/PackageArchiveInspector.cs` — `M`
- `core/runtime/PackageRetentionOwnerV1.cs` — `M`
- `core/runtime/PackageSetLifecycle.cs` — `M`
- `core/runtime/StoragePathGuard.cs` — `??`
- `tools/core-artifact-validator/Program.cs` — `M`

### B — pruebas, fixtures y harnesses requeridos (22)

- `tests/core-artifact-validator/ArtifactValidatorTests.cs` — `M`
- `tests/core/CoreDataProtectionTests.cs` — `??`
- `tests/core/CorePersistenceTests.cs` — `M`
- `tests/core/Slice7ModuleLifecycleTests.cs` — `M`
- `tests/package-lifecycle-process-host/Program.cs` — `M`
- `tests/private-module-fixtures/dependency-v1/Hispania.PrivateFixture.Dependency.csproj` — `??`
- `tests/private-module-fixtures/dependency-v1/PrivateDependency.cs` — `??`
- `tests/private-module-fixtures/dependency-v2/Hispania.PrivateFixture.Dependency.csproj` — `??`
- `tests/private-module-fixtures/dependency-v2/PrivateDependency.cs` — `??`
- `tests/private-module-fixtures/module-v1/Hispania.PrivateFixture.ModuleV1.csproj` — `??`
- `tests/private-module-fixtures/module-v1/PrivateModule.cs` — `??`
- `tests/private-module-fixtures/module-v2/Hispania.PrivateFixture.ModuleV2.csproj` — `??`
- `tests/private-module-fixtures/module-v2/PrivateModule.cs` — `??`
- `tests/unit/Hispania.Runtime.Tests.csproj` — `M`
- `tests/unit/ModuleHostPlatformJobContributionTests.cs` — `M`
- `tests/unit/ModuleInfrastructureTests.cs` — `M`
- `tests/unit/PackageArchiveAdversarialTests.cs` — `??`
- `tests/unit/PackageLifecycleIntegrationTests.cs` — `M`
- `tests/unit/PrivateModuleDependencyIsolationTests.cs` — `??`
- `tools/test-slice7-published-fixture-lifecycle.ps1` — `??`
- `tools/test-slice8-published-artifact-fitness.ps1` — `??`
- `tools/verify-slice8-artifact-baseline.ps1` — `??`

### C — contratos de publicación, documentación y evidencias de Slice 8 (37)

- `docs/architecture/evidence/slice8/core-artifact-baseline-win-x64.json` — `??`
- `docs/architecture/evidence/slice8/step2-adversarial-extraction-matrix.md` — `??`
- `docs/architecture/evidence/slice8/step3-transactions-concurrency.md` — `??`
- `docs/architecture/evidence/slice8/step3/artifact-after.json` — `??`
- `docs/architecture/evidence/slice8/step3/artifact-comparator-selftest.json` — `??`
- `docs/architecture/evidence/slice8/step4-safe-deletion-matrix.md` — `??`
- `docs/architecture/evidence/slice8/step4/artifact-after.json` — `??`
- `docs/architecture/evidence/slice8/step4/artifact-before.json` — `??`
- `docs/architecture/evidence/slice8/step4/artifact-comparator-selftest.json` — `??`
- `docs/architecture/evidence/slice8/step5/artifact-after.json` — `??`
- `docs/architecture/evidence/slice8/step5/artifact-before.json` — `??`
- `docs/architecture/evidence/slice8/step5/artifact-validator-after.txt` — `??`
- `docs/architecture/evidence/slice8/step5/artifact-validator-before.txt` — `??`
- `docs/architecture/evidence/slice8/step5/module-loading-execution-matrix.md` — `??`
- `docs/architecture/evidence/slice8/step6/alc-correction-results-20261019.json` — `??`
- `docs/architecture/evidence/slice8/step6/alc-reference-investigation-20261019.json` — `??`
- `docs/architecture/evidence/slice8/step6/alc-unload-diagnosis.md` — `??`
- `docs/architecture/evidence/slice8/step6/artifact-fitness-matrix.md` — `??`
- `docs/architecture/evidence/slice8/step6/candidate-core-artifact-manifest.json` — `??`
- `docs/architecture/evidence/slice8/step6/candidate-dp-isolated-manifest-20261019.json` — `??`
- `docs/architecture/evidence/slice8/step6/candidate-dp-isolated-summary-20261019.json` — `??`
- `docs/architecture/evidence/slice8/step6/candidate-inventory.json` — `??`
- `docs/architecture/evidence/slice8/step6/candidate-validator-report.json` — `??`
- `docs/architecture/evidence/slice8/step6/capability-invocation-audit-20261019.json` — `??`
- `docs/architecture/evidence/slice8/step6/evidence-file-hashes.json` — `??`
- `docs/architecture/evidence/slice8/step6/historical-baseline-after.json` — `??`
- `docs/architecture/evidence/slice8/step6/historical-baseline-before.json` — `??`
- `docs/architecture/evidence/slice8/step6/published-candidate-smoke-dp-isolated-20261019.json` — `??`
- `docs/architecture/evidence/slice8/step6/published-candidate-smoke-sanitized.json` — `??`
- `docs/architecture/evidence/slice8/step6/runtime-stabilization-20261019.json` — `??`
- `docs/architecture/evidence/slice8/step6/same-environment-reproducibility.json` — `??`
- `docs/architecture/evidence/slice8/step6/source-worktree-state.json` — `??`
- `docs/architecture/evidence/slice8/step7/candidate-inventory-9459242a34b042402f79d140ceed7d16bdeB149616ec7b08573188a34d31bed0.json` — `??`
- `docs/architecture/evidence/slice8/step7/published-integration-gate.md` — `??`
- `docs/architecture/HISPANIA-V1-CORE-DEPENDENCY-ALLOWLIST.md` — `M`
- `docs/architecture/HISPANIA-V1-EVIDENCE-LEDGER.md` — `M`
- `docs/architecture/HISPANIA-V1-PHASE1-SLICE8-ARTIFACT-ISOLATION-REPORT.md` — `??`

### D — evidencia histórica de Slice 7 (auditoría; no se compila) (3)

- `docs/architecture/evidence/slice7/published-core-fixture-smoke-20261018.json` — `??`
- `docs/architecture/evidence/slice7/README.md` — `??`
- `docs/architecture/HISPANIA-V1-PHASE1-SLICE7-MODULE-MANAGER-REPORT.md` — `M`


### EXCLUIR del commit de procedencia

Ninguna de las 77 rutas estándar se excluye de esta propuesta. Sí quedan expresamente fuera del commit y del payload:

- `bin/`, `obj/`, `TestResults/`, `.artifacts/`, `node_modules/`, `dist/` y otros outputs ignorados.
- Los 114 directorios `%TEMP%\hispania-slice8-*` y otros temporales locales.
- Secretos y configuración local, bases SQLite, claves, certificados y tokens; ninguno figura en las 77 rutas.
- La candidata publicada retenida y el Core histórico (artefactos externos al árbol Git; no copiarlos al commit ni reconstruirlos).
- Cambios ajenos: no se detectaron rutas estándar ajenas a Slice 8 y la evidencia relacionada de Slice 7.

Hay 14,123 rutas ignoradas en el repositorio, principalmente `bin/`, `obj/`, TestResults, frontend generado y `.artifacts`; no se modificaron ni eliminaron. Los TRX/logs ignorados no sustituyen las evidencias sanitizadas versionadas de la selección.

### Riesgos de la selección

- Escaneo de marcadores de clave privada PEM, Bearer largo y valores no vacíos en campos de secreto: 0 coincidencias en 77 rutas; no se imprimieron valores.
- Dos documentos incluyen una ruta absoluta bajo un perfil Windows: `docs/architecture/HISPANIA-V1-PHASE1-SLICE8-ARTIFACT-ISOLATION-REPORT.md` y `docs/architecture/evidence/slice8/step7/published-integration-gate.md`. No es una coincidencia de secreto, pero puede revelar el nombre de usuario/máquina en una publicación externa. Revisar/redactar antes de cualquier publicación externa. Esta autorización es local y no autoriza push.
- Los inventarios/manifests incluidos describen candidatas históricas. PI-01 sigue FAIL para los dos manifests retenidos; no deben presentarse como candidata semánticamente corregida. PI-33 sólo se demuestra mediante una publicación posterior desde checkout limpio.
- La documentación/evidencia en el commit no pasa al payload; el publisher sigue limitado por allowlist y manifest.

### Plan Git propuesto (NO EJECUTADO)

Sólo después de autorización separada para modificar el índice y crear commit local:

```powershell
$ErrorActionPreference = 'Stop'
$ExpectedHead = 'ef7365bb5e7d7551cb1093519743cc1885757b5b'
if ((git rev-parse HEAD).Trim() -ne $ExpectedHead) { throw 'HEAD changed; re-audit first.' }
$Include = @(
    'apps/core/Configuration/CoreDataProtectionOptions.cs',
    'apps/core/Configuration/CoreHostOptions.cs',
    'apps/core/Configuration/CoreStorageOptions.cs',
    'apps/core/Hosting/CoreHost.cs',
    'apps/core/Http/CoreModuleEndpoints.cs',
    'apps/core/Modules/CoreModuleManager.cs',
    'apps/core/Persistence/SqliteCorePersistence.cs',
    'core/runtime/ExtensionPackageInstaller.cs',
    'core/runtime/ModuleExceptions.cs',
    'core/runtime/ModuleExtensionCatalog.cs',
    'core/runtime/PackageArchiveInspector.cs',
    'core/runtime/PackageRetentionOwnerV1.cs',
    'core/runtime/PackageSetLifecycle.cs',
    'core/runtime/StoragePathGuard.cs',
    'docs/architecture/evidence/slice7/published-core-fixture-smoke-20261018.json',
    'docs/architecture/evidence/slice7/README.md',
    'docs/architecture/evidence/slice8/core-artifact-baseline-win-x64.json',
    'docs/architecture/evidence/slice8/step2-adversarial-extraction-matrix.md',
    'docs/architecture/evidence/slice8/step3-transactions-concurrency.md',
    'docs/architecture/evidence/slice8/step3/artifact-after.json',
    'docs/architecture/evidence/slice8/step3/artifact-comparator-selftest.json',
    'docs/architecture/evidence/slice8/step4-safe-deletion-matrix.md',
    'docs/architecture/evidence/slice8/step4/artifact-after.json',
    'docs/architecture/evidence/slice8/step4/artifact-before.json',
    'docs/architecture/evidence/slice8/step4/artifact-comparator-selftest.json',
    'docs/architecture/evidence/slice8/step5/artifact-after.json',
    'docs/architecture/evidence/slice8/step5/artifact-before.json',
    'docs/architecture/evidence/slice8/step5/artifact-validator-after.txt',
    'docs/architecture/evidence/slice8/step5/artifact-validator-before.txt',
    'docs/architecture/evidence/slice8/step5/module-loading-execution-matrix.md',
    'docs/architecture/evidence/slice8/step6/alc-correction-results-20261019.json',
    'docs/architecture/evidence/slice8/step6/alc-reference-investigation-20261019.json',
    'docs/architecture/evidence/slice8/step6/alc-unload-diagnosis.md',
    'docs/architecture/evidence/slice8/step6/artifact-fitness-matrix.md',
    'docs/architecture/evidence/slice8/step6/candidate-core-artifact-manifest.json',
    'docs/architecture/evidence/slice8/step6/candidate-dp-isolated-manifest-20261019.json',
    'docs/architecture/evidence/slice8/step6/candidate-dp-isolated-summary-20261019.json',
    'docs/architecture/evidence/slice8/step6/candidate-inventory.json',
    'docs/architecture/evidence/slice8/step6/candidate-validator-report.json',
    'docs/architecture/evidence/slice8/step6/capability-invocation-audit-20261019.json',
    'docs/architecture/evidence/slice8/step6/evidence-file-hashes.json',
    'docs/architecture/evidence/slice8/step6/historical-baseline-after.json',
    'docs/architecture/evidence/slice8/step6/historical-baseline-before.json',
    'docs/architecture/evidence/slice8/step6/published-candidate-smoke-dp-isolated-20261019.json',
    'docs/architecture/evidence/slice8/step6/published-candidate-smoke-sanitized.json',
    'docs/architecture/evidence/slice8/step6/runtime-stabilization-20261019.json',
    'docs/architecture/evidence/slice8/step6/same-environment-reproducibility.json',
    'docs/architecture/evidence/slice8/step6/source-worktree-state.json',
    'docs/architecture/evidence/slice8/step7/candidate-inventory-9459242a34b042402f79d140ceed7d16bdeB149616ec7b08573188a34d31bed0.json',
    'docs/architecture/evidence/slice8/step7/published-integration-gate.md',
    'docs/architecture/HISPANIA-V1-CORE-DEPENDENCY-ALLOWLIST.md',
    'docs/architecture/HISPANIA-V1-EVIDENCE-LEDGER.md',
    'docs/architecture/HISPANIA-V1-PHASE1-SLICE7-MODULE-MANAGER-REPORT.md',
    'docs/architecture/HISPANIA-V1-PHASE1-SLICE8-ARTIFACT-ISOLATION-REPORT.md',
    'tests/core-artifact-validator/ArtifactValidatorTests.cs',
    'tests/core/CoreDataProtectionTests.cs',
    'tests/core/CorePersistenceTests.cs',
    'tests/core/Slice7ModuleLifecycleTests.cs',
    'tests/package-lifecycle-process-host/Program.cs',
    'tests/private-module-fixtures/dependency-v1/Hispania.PrivateFixture.Dependency.csproj',
    'tests/private-module-fixtures/dependency-v1/PrivateDependency.cs',
    'tests/private-module-fixtures/dependency-v2/Hispania.PrivateFixture.Dependency.csproj',
    'tests/private-module-fixtures/dependency-v2/PrivateDependency.cs',
    'tests/private-module-fixtures/module-v1/Hispania.PrivateFixture.ModuleV1.csproj',
    'tests/private-module-fixtures/module-v1/PrivateModule.cs',
    'tests/private-module-fixtures/module-v2/Hispania.PrivateFixture.ModuleV2.csproj',
    'tests/private-module-fixtures/module-v2/PrivateModule.cs',
    'tests/unit/Hispania.Runtime.Tests.csproj',
    'tests/unit/ModuleHostPlatformJobContributionTests.cs',
    'tests/unit/ModuleInfrastructureTests.cs',
    'tests/unit/PackageArchiveAdversarialTests.cs',
    'tests/unit/PackageLifecycleIntegrationTests.cs',
    'tests/unit/PrivateModuleDependencyIsolationTests.cs',
    'tools/core-artifact-validator/Program.cs',
    'tools/test-slice7-published-fixture-lifecycle.ps1',
    'tools/test-slice8-published-artifact-fitness.ps1',
    'tools/verify-slice8-artifact-baseline.ps1'
)
# Precondiciones antes de tocar el índice.
$AlreadyStaged = @(git diff --cached --name-only)
if ($AlreadyStaged.Count -ne 0) { throw 'Index is not empty; stop and re-audit.' }
$Actual = @((@(git diff --name-only) + @(git ls-files --others --exclude-standard)) | Sort-Object -Unique)
$Expected = @($Include | Sort-Object -Unique)
$Difference = @(Compare-Object -ReferenceObject $Expected -DifferenceObject $Actual)
if ($Difference.Count -ne 0 -or $Actual.Count -ne 77) { throw 'Worktree path inventory differs from reviewed selection.' }
git add -- $Include
$Staged = @(git diff --cached --name-only)
$Unexpected = @($Staged | Where-Object { $_ -notin $Include })
$Missing = @($Include | Where-Object { $_ -notin $Staged })
if ($Unexpected.Count -or $Missing.Count -or $Staged.Count -ne 77) { throw 'Staged set differs from the reviewed allowlist.' }
$Forbidden = @($Staged | Where-Object { $_ -match '(^|/)(bin|obj|TestResults|\.artifacts|node_modules|dist)(/|$)|\.(pfx|p12|pem|key|sqlite|db)$' })
if ($Forbidden.Count) { throw 'Generated/local/secret-like path staged.' }
git diff --cached --check
# Inspeccionar nombres y el diff completo antes de commit.
git diff --cached --stat
git diff --cached --
# Escaneo read-only; informa sólo rutas, nunca líneas/valores coincidentes.
$SecretPattern = '(?im)-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----|Bearer\s+[A-Za-z0-9._~+/-]{24,}|"(?:password|client_secret|private_key|access_token|refresh_token)"\s*:\s*"[^"]{8,}"'
$SecretFiles = @($Include | Where-Object { (Test-Path -LiteralPath $_ -PathType Leaf) -and [regex]::IsMatch((Get-Content -LiteralPath $_ -Raw), $SecretPattern) })
if ($SecretFiles.Count) { throw ('Potential secret markers in: ' + ($SecretFiles -join ', ')) }
# Sólo tras revisar el diff y una autorización separada:
git commit -m 'HISPANIA v1.0: capture Slice 8 artifact source and evidence'
$CommitSha = (git rev-parse HEAD).Trim()
$TreeSha = (git rev-parse 'HEAD^{tree}').Trim()
"commit=$CommitSha`ntree=$TreeSha"
```

`git add` y `git commit` son propuestas, no ejecutadas; no se propone push. Antes de ejecutarlas se debe repetir la comprobación de que las 77 rutas/status no cambiaron. Si HEAD o el conjunto cambia, volver a auditar.

### Pruebas posteriores al futuro commit y previas a publicación

Primero, pruebas focalizadas:

```powershell
dotnet test tests/core-artifact-validator/Hispania.CoreArtifactValidator.Tests.csproj -c Release
dotnet test tests/core/Hispania.Core.Tests.csproj -c Release
dotnet test tests/core/Hispania.Core.Tests.csproj -c Release --filter FullyQualifiedName~CoreDataProtectionTests
dotnet build apps/module-host/Hispania.ModuleHost.csproj -c Release
dotnet test tests/unit/Hispania.Runtime.Tests.csproj -c Release --filter 'FullyQualifiedName~PackageArchiveAdversarialTests|FullyQualifiedName~PrivateModuleDependencyIsolationTests|FullyQualifiedName~ModuleHostPlatformJobContributionTests|FullyQualifiedName~PackageLifecycleIntegrationTests|FullyQualifiedName~Publisher'
```

Después, regresiones/harnesses:

```powershell
dotnet test tests/unit/Hispania.Runtime.Tests.csproj -c Release
pwsh -NoProfile -File tools/test-phase19-package-lifecycle.ps1
dotnet test tests/core/Hispania.Core.Tests.csproj -c Release
dotnet test tests/unit/Hispania.Runtime.Tests.csproj -c Release --runtime win-x64 --filter FullyQualifiedName~PackageLifecycle
pwsh -NoProfile -File tools/verify-slice8-artifact-baseline.ps1 -ArtifactRoot (Join-Path $env:TEMP 'hispania-slice7-accepted-20261018-publish') -BaselinePath docs/architecture/evidence/slice8/core-artifact-baseline-win-x64.json
```

El harness Phase 19 construye el lector histórico y ejecuta Package Lifecycle + backup/restore; el script fue inspeccionado y la prueba no requiere resolución de paquetes de red. Un checkout nuevo sí puede necesitar `dotnet restore`/feed antes de testear. ModuleHost, Publisher Trust y OOP-only se ejercitan en Runtime; Data Protection se prueba en Core. No hay proyecto de pruebas ModuleHost independiente entre las rutas cambiadas.

Condiciones: .NET SDK 10 y restore NuGet disponible/cache; Windows x64 para Core, ModuleHost, DPAPI y procesos. Las suites usan procesos/SQLite/directorios temporales propios y no requieren servidor externo conocido. Las junctions de las pruebas existentes no requieren elevación; la prueba directa de symlink conserva su SKIP conocido por privilegio/Developer Mode, sin cambiar configuración ni elevar. El smoke y validador sobre una candidata nueva se ejecutan sólo después de commit autorizado y publicación desde checkout limpio.

**Preparación:** PARTIAL para PI-33. La selección está revisada y los artefactos retenidos verificados, pero todavía no hay commit, checkout limpio ni candidata derivada de tal checkout. No se inició 7.3–7.5 ni Slice 9.