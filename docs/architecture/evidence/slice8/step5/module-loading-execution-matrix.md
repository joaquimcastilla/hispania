# EV-050 — Slice 8 Step 5: Module loading and execution

Date: 2026-10-18<br>
Repository branch: `codex/hispania-v1-baseline-consolidation`<br>
Source HEAD: `ef7365bb5e7d7551cb1093519743cc1885757b5b`<br>
Acceptance platform: Windows x64, .NET 10

## Runtime policy finding — pre-decision baseline

**Pre-decision baseline:** `ModuleManifest.Runtime` continues to define both `dotnet-inprocess-trusted` and `dotnet-outofprocess` for compatibility. The generic runtime catalog and its low-level tests retain both implementations. The owner has since approved an OOP-only policy for installable modules administered by Core; see the resolution section below.

The pre-decision behavior was consistent with the manifest's explicit runtime field and compatibility note that packages can be in-process. A native in-process module can execute with Core process privileges, access resources available to Core, stall or terminate Core, and retain process state. Collectible `AssemblyLoadContext` limits assembly resolution and supports unload attempts; it is not a security boundary. The approved policy below constrains the Core-managed installable-module boundary without changing the manifest contract or the low-level compatibility mechanism.

## Dependency and unload matrix

The new test fixtures are purpose-built, test-only assemblies. Two packages use the same private assembly simple name, `Hispania.PrivateFixture.Dependency`, at assembly versions 1.0.0.0 and 2.0.0.0. They are signed during each test using an ephemeral RSA key and trusted only by that test's catalog. No fixture dependency project is referenced into the Core or accepted publish.

| Case | Runtime | Expected result | Result |
|---|---|---|---|
| Two simultaneously active packages resolve their own private dependency version and output | `dotnet-inprocess-trusted` | v1 returns `private-dependency-v1`; v2 returns `private-dependency-v2`; dependencies appear in separate collectible ALCs and not in `AssemblyLoadContext.Default` | PASS |
| Two simultaneously active packages resolve their own private dependency version and output | `dotnet-outofprocess` | Each ModuleHost returns its package-specific marker; private dependency is absent from the Core/test process default context | PASS |
| Private dependency omitted from signed package | Both runtimes | Startup fails; module is not registered or acquirable | PASS |
| API-incompatible dependency substituted with same simple name but a different version | Both runtimes | Startup fails; module is not registered or acquirable | PASS |
| Active operation lease held during unload | In-process | Catalog waits for the lease; externally retained ALC makes `UnloadAsync` report `false`; after releasing that reference it becomes collectible | PASS |
| ModuleHost process unload | OOP | Deactivation/unload completes and the process is stopped | PASS |

The v2 dependency deliberately has a different method signature. A different assembly version alone is not necessarily an incompatible binding: an earlier experiment with the same API showed .NET could load the higher version. The final test checks an actual missing API and confirms `MissingMethodException`, rather than equating version inequality with incompatibility.

## Execution and failure matrix

| Requirement | Existing verification | Result |
|---|---|---|
| Trusted module install, activation, public contract invocation and deactivation | `ModuleHostPlatformJobContributionTests.SignedModuleHostContributionActivatesExecutesDrainsAndRevokes`; `SignedInProcessModuleContributionIsCompatibleAndDrains` | PASS |
| OOP worker failure, degraded state, rejected acquisition and explicit reactivation recovery; another module stays running | `ModuleInfrastructureTests.OutOfProcessGreetingRunsThroughPublicContractAndRecoversAfterWorkerCrash` | PASS in final full Runtime run |
| OOP startup failure and controlled in-process startup failure | New missing/incompatible private dependency cases; `ModuleInfrastructureTests.StartupFailureLeavesTheModuleInFailedState`; Core `Slice7ModuleLifecycleTests.FixtureFailureIsRolledBackWithoutStoppingCore` | PASS |
| Persistence and Core restart behavior | Core `Slice7ModuleLifecycleTests.TrustedFixtureInstallsDisabledRestartsAndUninstallsWithoutPersistingPrivateKey`; prior owner-reported published fixture lifecycle smoke | Automated 4/4 PASS; owner smoke remains separately attributed in Slice 7 evidence |
| OOP process/job resource controls | `Phase21SecurityHardeningTests.FiniteChildProbesAreContainedByRealResourcePolicy` exercises CPU and memory limits; Windows memory case also exercises process-count limit with required OS limits | PASS in full Runtime run |
| No orphan ModuleHost process after suites | Process inventory searched command lines containing `Hispania.ModuleHost.dll` | 0 residual processes |

Windows OOP defaults are a 2 GiB memory cap, 120 seconds cumulative CPU, 75% CPU hard cap, 16 processes in the Windows Job Object, one concurrent request and a 30-second request duration. The resource test uses a smaller disposable policy and confirms OS enforcement. These are operational resource controls, not a sandbox; they do not protect Core from malicious in-process code or OOP code using resources the operating system grants outside those limits.

## Regression results

TRX results are retained under `.artifacts/slice8-step5-20261018/` (ignored test output, not added to the repository):

| Suite | Result | TRX SHA-256 |
|---|---:|---|
| Private dependency/runtime matrix | 6/6 PASS | `4B3E950AABC8D7D14BDB53CE363003F92A4BB58D2CB59F021EFABB12A7041863` |
| ModuleHost focused | 9/9 PASS | `5C27E592D5464041C6DD9C35E3A1F1A05899440B8E77AC2AC06EE29DCCF201C1` |
| Core | 59 PASS / 1 SKIP / 0 FAIL | `53204D2F257C3F307464628B7B01CEDB9900C4906B445D5BCE59ABA5A21F8CB9` |
| Slice 7 fixture/Core lifecycle | 4/4 PASS | `5519FA93D39CCE79FBA42D755BDB5E8F9595B765C35A4121D7F7401FD249083E` |
| Package lifecycle with historical reader | 94/94 PASS | `378B0B9F57DD5665E34E85E9A9131950CD1386CB2A87827E73D899596B6EF99C` |
| Package backup/restore | 2/2 PASS | `6058F7811131ABB73250CA36847DDA282BA11A5D9C0F85B3439E5B4DC0E4DFEA` |
| Full Runtime, including all runtime-matrix cases | 265/265 PASS | `C4D27A14E7F9CA50FE8F8A79EC76A151A3BF59B30EC69D201BFADE59D1A8BE55` |

The Core SKIP is the existing direct symbolic-link creation test; Step 1's separate junction checks remain the applicable Windows reparse-point evidence. During this initial pre-decision test-only phase, no Core/product code, contracts, signed package format, publisher trust, permission policy or accepted artifact was modified.

One full Runtime attempt before the final passing run reported 261/262: the existing `OutOfProcessGreetingRunsThroughPublicContractAndRecoversAfterWorkerCrash` expected a request timeout but observed `IOException` because the worker had exited after the preceding cancellation. The test passed in three isolated consecutive runs and in subsequent full Runtime runs (262/262, 262/262, then 265/265). No production change was made to mask the transient. Retain this as an intermittent scheduling/supervision-test risk, not a proven production defect.

## Accepted artifact integrity

`artifact-before.json` and `artifact-after.json` are byte-identical; each SHA-256 is `64A73CACB74F0B05BD679B72A90365FA5164EFF376B6AF9EDE65500FE79CD5FE`. Both compare successfully with `added=[]`, `missing=[]`, `modified=[]`, content ID `44B43088658F2F70FE8A0745EA5DFC3B1519CBF10B9347F3F4B88835EEDB3B12`, and manifest SHA-256 `22D05298F7329BE8C2892BDEE33EBE71C6851EBC5D7A6B6BBF2A49C34F45FE87`. The artifact validator passed before and after with `issues=[]` and policy `hispania-core-v1.0.0`; both output files have SHA-256 `08DD9A585910E064CEFAC1EBACE64B80CDB1BF9A26B34EA330D486A4C841EC4F`. The accepted publish was not rebuilt or modified.

## Temporary output and limits

The final test implementation removes each newly created test root and the last focused run added no new roots. Eighteen scratch package directories from earlier authoring/rerun attempts remain under `%TEMP%` as `hispania-slice8-private-deps-<32-hex>`; inspection found the expected synthetic package trees and no reparse points. A cleanup command targeting only those verified test-owned roots was rejected by the execution tool policy before running, so none was removed. These directories contain synthetic fixture packages, not private signing keys or credentials. Manual cleanup may remove only those exact prefixed roots after verifying their contents.

## Outcome before owner policy resolution

Technical loading/execution tests passed at the bounded Windows x64 scope; Step 5 was PARTIAL pending the owner's runtime decision. This historical result is superseded by the resolution below. Slice 8 remains IN PROGRESS; Step 6 and Slice 9 have not started. Global roadmap remains 4/14 — 28.6%.

## Owner decision and implementation — OOP-only Core policy (2026-10-18)

The owner approved `dotnet-outofprocess` as the only runtime allowed for installable modules administered by Core. This policy does not constrain internal HISPANIA components or technical in-process implementations that do not use Core's managed extension system. `ModuleManifest.Runtime` values and signed package bytes remain unchanged.

Core configures `ModuleExtensionCatalog` with an explicit allowed-runtime set. The catalog's shared `ValidateManagedModuleRuntime` rejects any other manifest runtime with `ModuleRuntimePolicyException`. Core calls this check after archive inspection and before package planning/staging; package-set application checks every selected module before writing transaction state; direct activation checks before loading; package-set recovery checks every enabled persisted entry before starting the first module; and `CreateLoadedModule` checks again before creating an OOP worker or loading an assembly. Publisher signature and trust validation remain in their existing package-planning and activation checks. Built-in trusted factories and internal Core modules do not use the installable-package loader. Install and enable HTTP routes return controlled `module_runtime_not_allowed` responses. The generic catalog retains its existing compatibility behavior when no host-specific allowlist is supplied.

Policy implementation files: `apps/core/Hosting/CoreHost.cs`, `apps/core/Modules/CoreModuleManager.cs`, `apps/core/Http/CoreModuleEndpoints.cs`, `core/runtime/ModuleExtensionCatalog.cs`, `core/runtime/PackageSetLifecycle.cs`, and `core/runtime/ModuleExceptions.cs`. New rejection/recovery coverage is in `tests/core/Slice7ModuleLifecycleTests.cs`. No frozen manifest or signed-package contract file changed.

| Requirement | Evidence | Result |
|---|---|---|
| Reject signed unauthorized runtime before Core installation | `CoreRejectsSignedInProcessPackageBeforeInstallation` | PASS |
| Reject direct Core catalog activation of legacy in-process package | `CoreCatalogRejectsLegacyInProcessActivationBeforeModuleLoad` | PASS |
| Keep previously persisted enabled in-process package inactive during Core recovery | `CoreRecoveryLeavesPreviouslyPersistedInProcessPackageInactive` | PASS |
| Preserve in-process ALC mechanism for technical compatibility | Existing runtime in-process dependency, load and unload cases | PASS |
| Preserve OOP operation, recovery and Core health | Core lifecycle and full Runtime suites | PASS |
| Preserve accepted publish | Before/after baseline and allowlist validator | PASS |

### Policy-resolution regression results

Results are retained under `.artifacts/slice8-step5-policy-20261018/`:

| Suite | Result | TRX SHA-256 |
|---|---:|---|
| Focused OOP-policy Core tests | 3/3 PASS | `84936344FB6D52357ACD4BC116F54BA90AC340958B571A84BE06DA0286EBD50D` |
| Full Core | 62 PASS / 1 SKIP / 0 FAIL | `3D016CE32081B75564F1B9F78D7C59C654AA355E2CE4C25B81B4458D3A41646B` |
| ModuleHost selection | 10/10 PASS | `C4B1FDF7A76149634DA68D2A01F2C91359211684886EE365A951C8CBC434275C` |
| Package Lifecycle with historical reader | 94/94 PASS | `3662BAC59C1803C51BD5E1EE7ECE5DA0AA9BF0A6524B5B79CFC2334BC92DD69C` |
| Package backup/restore | 2/2 PASS | `478E610158E182D00E455D0D20DCD37FE4B49CFBE07634875A54B8D2C26AB19F` |
| Full Runtime | 265/265 PASS | `2F8984102590B6EBE7BE158F492B7801BC5C84CC37F37D7C38D8068BF134B377` |

The Core SKIP remains the known direct symbolic-link privilege case; five real junction boundary tests from Step 1 pass. An earlier full Runtime run recorded an intermittent OOP timeout-versus-worker-exit assertion failure; it passed isolated reruns and subsequent full runs, including this 265/265 run. This remains a test scheduling/supervision risk, not a reproduced product defect. No ModuleHost process remained after the suites.

The accepted publish was compared before and after at `%TEMP%\hispania-slice7-accepted-20261018-publish`. Both comparer results pass, have no added, missing, or modified files, and are byte-identical with SHA-256 `64A73CACB74F0B05BD679B72A90365FA5164EFF376B6AF9EDE65500FE79CD5FE`. Content ID remains `44B43088658F2F70FE8A0745EA5DFC3B1519CBF10B9347F3F4B88835EEDB3B12`; manifest SHA-256 remains `22D05298F7329BE8C2892BDEE33EBE71C6851EBC5D7A6B6BBF2A49C34F45FE87`. The allowlist validator passed with `issues=[]`, policy `hispania-core-v1.0.0`; captured `validator-after.json` SHA-256 is `08DD9A585910E064CEFAC1EBACE64B80CDB1BF9A26B34EA330D486A4C841EC4F`. The accepted artifact was not rebuilt or modified.

**Step 5 status: PASS at bounded Windows x64 scope.** Slice 8 remains IN PROGRESS; Step 6 and Slice 9 have not started. Global roadmap remains 4/14 — 28.6%. Eighteen earlier synthetic `hispania-slice8-private-deps-*` directories remain untouched as instructed. No commit or push was made; pre-existing worktree changes remain preserved.
