# HISPANIA v1.0 — Phase 1 Slice 1 Compatibility Baseline

**Status:** PASS — bounded current-HEAD regression baseline established, 2026-10-17.
**Source HEAD:** `e5bc8a232f1e46955f0f04ab874a3ccfe04bf12a`
**Platform:** Windows 11 x64 (10.0.26200), .NET SDK 10.0.401 / MSBuild 18.9.11, .NET runtime 10.0.12.
**Scope:** documentation and isolated local verification only. No product, test, or contract files changed. Phase 1 remains at roadmap progress 4/14.

## 1. Protected contract inventory

| Area / contract | Source project and path | Current behavior and public/internal boundary | Existing tests and current evidence | Regression risk / compatibility requirement |
|---|---|---|---|---|
| Runtime and module manager | `core/runtime/Hispania.Runtime.csproj`; `IModuleRuntime.cs`, `IModuleRegistry.cs`, `IModuleResolver.cs`; module-system interfaces in `contracts/module-system` | Runtime lifecycle (start/stop/drain/resume/health/degraded), registry, resolver and capability selection. These interfaces are consumed contracts; concrete runtime/catalog implementations and internal lifecycle machinery are implementation details. | `tests/unit/Hispania.Runtime.Tests.csproj`; current bounded selection 19/19 PASS. Full Runtime Debug 219/219 is historical and not rerun here. | Preserve lifecycle results, dependency ordering, failure state, resolution semantics, and existing consumer behavior. New `apps/core` must not change `apps/server` behavior. OOP/ALC remain failure isolation/loading mechanisms, not security sandboxes. |
| Identity | `contracts/platform/identity/PlatformIdentity.cs`; `application/identity` | Public V1 query/command interfaces and stable result/value types cover users, sessions, login/logout and account changes. Application implementation and session storage are internal provider details. | `PlatformIdentityTests` in Inventory test project; included in current 29/29 selection. | Preserve V1 signatures and tested login/session, disabled-user, password and audit behavior; do not weaken authentication defaults. |
| Authorization | `contracts/platform/authorization/PlatformAuthorization.cs`; `application/authorization` | Public V1 authorization/admin contracts, permission identifiers, role and assignment values. Enforcement/composition is implementation detail. | `PlatformAuthorizationTests`; current selected run. | Preserve default-deny decisions, effective permissions, role administration and bootstrap constraints. No implicit permissions for modules. |
| Events | `contracts/platform/events/PlatformEvents.cs`, `DurablePlatformEventsV1.cs`; runtime and SQLite infrastructure | Public event envelope/publisher/subscriber and durable event bus V1 schemas/statuses; dispatch/store implementations are internal. | `DurablePlatformEventTests`; current selected run. | Preserve versioned event identity, delivery/retry/dead-letter/replay and bounded payload behavior. Do not break V1 envelope/schema compatibility. |
| Jobs | `contracts/platform/jobs/PlatformJobsV1.cs`; `application/platform-jobs`; SQLite provider | Public job/attempt/status/options and store/service boundary; scheduler/executor/provider implementation is internal. | `PlatformJobsTests`; current selected run. | Preserve job lifecycle, idempotency, lease, retry, cancellation, timeout and remote-only ownership semantics tested by the suite. |
| SQLite Platform persistence | `contracts/platform` storage/persistence contracts; `infrastructure/platform/persistence-sqlite` | Current SQLite project implements existing Platform stores (identity, authorization, events, jobs, etc.); it is not itself a provider-neutral Core contract. Core's v1.0 official provider is SQLite, but Core must depend on a narrow provider-neutral persistence contract, not SQLite APIs. | `PlatformPersistenceTests`; current selected run. | Preserve current Platform data behavior; do not turn existing provider internals into public Core contract. Core PostgreSQL is out of scope. Library/Media PostgreSQL is module-owned and must not be touched in Slice 1. |
| Hosting | `infrastructure/hosting/Hispania.Hosting.csproj`; production configuration/host composition; `apps/server` remains existing host | Hosting APIs/configuration and artifact verification compose/deploy an app; host-specific configuration and wiring are internal unless exposed through explicit contract. | Deployment/Hosting test project, current full 35/35 PASS. | Keep server startup/configuration and existing production artifact behavior intact. Empty Core is a separate composition root beside `apps/server`; future `apps/core` must not alter or replace `apps/server`. |
| Deployment | `infrastructure/hosting`; `tests/deployment` | Published artifact metadata, payload verification, protected configuration, secrets and service policy are deployment boundaries; test harness helpers are internal. | Full `Hispania.Deployment.Tests.csproj`, 35/35 PASS at this HEAD. | Keep integrity checks, fail-closed configuration, secret redaction and production trust constraints. This test suite is not native install/service-manager certification. |
| Inventory integration | `contracts/inventory`, `application/inventory`, `infrastructure/inventory/persistence-sqlite`, `tests/inventory/InventoryIntegrationTests.cs` | Module-owned Inventory contracts/services and current integration with platform events/audit. Inventory owns its data/provider choice; no Core ownership implied. | `InventoryIntegrationTests`, current selected run. | Preserve event/audit integration and module data ownership. Do not move Inventory schema/store into Core. |
| Storage provider / assets | `contracts/platform/storage/StorageContractsV1.cs`, `infrastructure/platform/storage-filesystem`, `tests/inventory/StorageProviderTests.cs` | Public storage-provider/asset references abstract content location; filesystem provider handles content and platform store metadata. | Six `StorageProviderTests`, current selected run. | Preserve provider resolution, opaque handles, path traversal rejection, migration compatibility and compensation behavior. Do not leak local paths or assume one module's backend is Core-wide. |

The public boundary in this matrix is the explicit contract/API and versioned data shape. SQL schema details, project composition, concrete provider types and test helpers are internal unless explicitly surfaced through those contracts. No new abstraction is proposed here.

## 2. Test selection and discovery

All discovery and execution happened against source HEAD above. Restore was offline from the existing NuGet cache and an empty local package feed. No external service or real user data was used.

| Suite | Selected classes/methods | Discovered | Reason and exclusions |
|---|---|---:|---|
| Inventory Platform + integration/storage | `PlatformIdentityTests`, `PlatformAuthorizationTests`, `DurablePlatformEventTests`, `PlatformJobsTests`, `PlatformPersistenceTests`, `InventoryIntegrationTests`, `StorageProviderTests` | 29 | R5's five Platform groups (22 tests) plus relevant Inventory integration (1) and storage providers (6). `-p:PlatformOnly=true` prevents copying legacy demo assets. No PostgreSQL service was contacted. |
| Deployment/Hosting | Entire `Hispania.Deployment.Tests.csproj` | 35 | Bounded unit/config/artifact tests use temporary/local fixtures; full suite had no service dependency. Native SCM/systemd install tests are not in this suite. |
| Runtime/Module infrastructure | 19 individually filtered methods: publisher signature/revocation; worker protocol bounds/negotiation and OOP limits; independently built package lifecycle/install/substitution; extension discovery/compatibility; registry/resolver; lifecycle/failure/dependency/capability resolution | 19 | Relevant in-memory/temp-input subset. Excluded repository-root-dependent tests, conventional-output alignment tests, and the actual OOP process-launch greeting test; the latter requires conventional `module-host` output and is deferred to a controlled later harness. This selection does not establish that hostile code is sandboxed. |

Historical evidence was kept distinct: Runtime Debug 219/219, Phase 19 lifecycle 76/76, backup 2/2, and prior Deployment/Hosting 35/35 were not attributed to this HEAD. Deployment's prior 35/35 was independently rerun here. Nine deployment-process cases remain blocked on exact predecessor artifacts or privileged/service-manager environments as catalogued in the Evidence Ledger. No selected test failed or skipped; no failure was waived.

## 3. Current results and evidence

Temporary run root: `C:\Users\joaqu\AppData\Local\Temp\hispania-slice1-20261017-b4f290a3`. TRX and logs are preserved there, outside the repository.

| Suite | Command (from repository root) | Result | TRX SHA-256 |
|---|---|---:|---|
| Platform + Inventory/storage | `dotnet test tests/inventory/Hispania.Inventory.Tests.csproj --no-restore --artifacts-path <run>\artifacts-inventory --results-directory <run>\results-inventory --logger "trx;LogFileName=slice1-inventory.trx" -p:NuGetAudit=false --filter "FullyQualifiedName~PlatformIdentityTests|FullyQualifiedName~PlatformAuthorizationTests|FullyQualifiedName~DurablePlatformEventTests|FullyQualifiedName~PlatformJobsTests|FullyQualifiedName~PlatformPersistenceTests|FullyQualifiedName~InventoryIntegrationTests|FullyQualifiedName~StorageProviderTests" -p:PlatformOnly=true` | 29 passed, 0 failed, 0 skipped; 6 s | `BC7EF5C0BB0000EA99ECF8FA123F1CE79837D9390E08A4F556FBAA754549E64E` |
| Deployment/Hosting | `dotnet test tests/deployment/Hispania.Deployment.Tests.csproj --no-restore --artifacts-path <run>\artifacts-deployment --results-directory <run>\results-deployment --logger "trx;LogFileName=slice1-deployment.trx" -p:NuGetAudit=false` | 35 passed, 0 failed, 0 skipped; 1 s | `B46805A9BA6181D533813360AEFEB1FC54D6EB62CD5C6AD0F5112DCF8C5F3F26` |
| Runtime/module subset | `dotnet test tests/unit/Hispania.Runtime.Tests.csproj --no-restore --artifacts-path <run>\artifacts-runtime --results-directory <run>\results-runtime --logger "trx;LogFileName=slice1-runtime.trx" -p:NuGetAudit=false --filter "<19 FullyQualifiedName~ method clauses joined by |; exact command in test-runtime.log>"` | 19 passed, 0 failed, 0 skipped; 1 s | `54B0182E186EB35CC35A4A209A2BA5A67FF32977E03EF1F7C9786150A1F2A39A` |

Exact full command lines and stdout/exit codes: `logs/test-inventory.log` SHA-256 `E9B8B0B1DCA80E646F024613F8692F2BEB81E5BC26AC2B2C8588C809558498B7`; `logs/test-deployment.log` `0F725AD27D6FE42F0C6B86BEFEEA21F52C066411A638727184236852309FEE6A`; `logs/test-runtime.log` `9C5A4406C8A15766FFF73AC277BB7A3E445C77E3A204D596D9079676D6D24A60`. Discovery logs: inventory 29 names, deployment 35 names, runtime 19 names; hashes respectively `6D3B3FC1689A25E18E477CC4D0073641992B5F7A2BD93928D77E5B5E0572A627`, `102D3588CEE9492C8AF7E4390CE9BDF6E3913A58245DF14B79E8F4829B95D0F7`, `FCBE2CB5873B49D63602F96483B2D4D75D17E062D28EE4BD373AC73FE4740C97`.

The exact Runtime filter is the OR of the 19 method names listed in §2 and is preserved verbatim in `logs/test-runtime.log`. TRX timestamps and counters are authoritative; displayed suite duration is rounded by `dotnet test`.

## 4. Failures and deferred validation

- **Current selected baseline:** no failures, skips, or unexplained regressions (83/83 pass).
- **Full Runtime Debug 219/219:** historical only; full suite not rerun in this isolated run because repository-root/process-output-dependent tests require a separate controlled harness. Current 19-method subset is not a substitute for full suite evidence.
- **Phase 19 lifecycle 76/76 and backup 2/2:** historical only; not rerun. The old historical reader/artifacts are outside the authorized inputs and remain excluded pending exact rights/fixture requirements.
- **Deployment Process:** nine cases blocked on exact immutable predecessor artifacts, privileged service-manager/SCM environments or corresponding host setup; existing Evidence Ledger names them. Do not fabricate fixture artifacts or count them as passed.
- **Native Windows service, Linux systemd, Linux execution, ARM64/Raspberry Pi, real Coordinator/Worker/Data Gateway, PostgreSQL and hardware/GPU:** deferred to their authorized slices/phases. This run is Windows x64, local, synthetic, and does not certify these targets.
- **Out-of-process worker launch:** deferred; protocol/limits are tested but process supervision is not shown to be a security sandbox.

## 5. Dependencies, rights and license boundary

The selected test projects resolve broad graphs (Inventory includes Server, Media, PostgreSQL, WorkerAgent and process-harness projects). Exact effective NuGet package inventories were reviewed in R5; restores here used the existing package cache offline, with no live PostgreSQL connection. This is local testing evidence only and no redistribution rights follow from it. `LICENSE.md` remains unchanged.

Five unknown-rights historical DLLs and `apps/server/demo-assets/bp48-demo.mp4` remain excluded from Empty Core. Inventory test build used `-p:PlatformOnly=true`; no `bp48-demo.mp4` was found under the isolated artifacts. The six listed unknown component SHA-256 values in `HISPANIA-V1-UNKNOWN-RIGHTS-COMPONENTS.csv` did not match any file in the generated build artifacts. No excluded repository artifact was deleted or relocated. Test execution is not distribution authorization.

## 6. Security, persistence and module constraints

- Authentication remains mandatory by default; bootstrap has no default credentials; authorization is default-deny. Public endpoints must remain explicit.
- Module install requires signature/integrity/compatibility/publisher trust checks. Signed is not safe; publisher trust is separate from permissions; permissions are declared and enforced where supported. Untrusted/adversarial native modules are unsupported in v1.0. OOP and collectible ALC are not sandboxes. No WASM/sandbox work in Slice 1.
- Core uses SQLite as the only officially supported v1.0 Core provider, behind a narrow provider-neutral persistence contract. Public Core contracts must not expose SQLite implementation types. PostgreSQL Core support is out of scope. Media's PostgreSQL provider is module-owned and untouched.
- Module data ownership and persistence policy stay with each module. Do not impose Core's SQLite choice on Inventory, Media, ERP or future modules.

## 7. Compatibility rules for Slices 2–10

1. Preserve existing `apps/server` sources, project graph, routes, configuration, persistence and deployment behavior. New `apps/core` is a separate composition root; never rewrite or transform Server as part of Core work.
2. Do not change existing public contracts or V1 event/job/storage schemas without a separately authorized compatibility decision and tests.
3. Reuse Kernel/Runtime selectively; do not turn internal implementation details into Core public contracts or add speculative generic abstractions.
4. Core persistence implementation must use a provider-neutral contract; SQLite is its authorized provider. Keep module-owned stores/providers independent. Do not touch Media PostgreSQL in Phase 1.
5. Keep auth/default-deny and explicit endpoint policies. A new Core endpoint must not become public implicitly.
6. Maintain package-signature, publisher trust, compatibility and integrity checks. Do not claim OOP/ALC is a sandbox or accept arbitrary untrusted native modules.
7. Keep build/intermediate/test/publish outputs external or in explicitly isolated artifact roots; never copy unknown-rights artifacts into Core.
8. No distribution, release, license change, push, or modification of the 15 preserved original worktrees is authorized by this baseline.
9. At each later slice, rerun the relevant current-HEAD regression groups, preserve exact commands/TRX/hashes, and record failures/deferred evidence separately.
10. Stop at this Slice 1 boundary; Slice 2 requires the owner's next prompt.

## 8. Rollback and final validation

This slice changed documentation only. All test/build outputs are in the task-owned `%TEMP%` run root above and retained as audit evidence. Rollback is limited to reverting the documentation-only commit (if created); no database, production file, contract, test or application code changed. No destructive cleanup was run.

`git diff --check`: PASS. Documentation-only paths: this baseline, the Slice 1 report, and the Evidence Ledger. `apps/server`, code, contracts, tests, licenses and assets are unchanged. `LICENSE.md` SHA-256 remains `75EAE29E043C82B168894AFAAA148ADE2E9A43B4A72513FF18CFD22434F9BD3D`. Preservation verification remains 15/15 original worktrees and 49/49 source plus 49/49 backup hashes unchanged. No push; no Slice 2 started.
