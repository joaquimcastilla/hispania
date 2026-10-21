# HISPANIA v1.0 — Phase 1 Slice 7 Report

## A. Status

**DONE — ACCEPTED BY OWNER**, limited to the validated Windows x64 scope. The owner explicitly accepted Slice 7 on 2026-10-18 after the complete published-Core fixture smoke passed 14/14. The acceptance does not claim Linux, Raspberry Pi, other architectures, hostile-native-code sandboxing, or capabilities from later phases. Phase 1 remains **IN PROGRESS**; global roadmap stays exactly **4/14 — 28.6%**. Slice 8 remains **NOT STARTED**.

## B. Root cause and correction

The source of the duplicate files was proved in `hispania-slice7-publish-diag.log` (SDK 10.0.401): the Core `ProjectReference` to ModuleHost had `ReferenceOutputAssembly="false"` but retained the default `Private` value. The SDK's `GetCopyToPublishDirectoryItems` target therefore imported ModuleHost's publish items into Core's publish root. Those items included `Hispania.ModuleHost.deps.json`, runtimeconfig, and apphost. Separately, Core's existing `CopyModuleHostForCorePublish` target correctly copied ModuleHost's DLL, `.deps.json`, runtimeconfig and required contracts into `module-host/`. This produced one unwanted root `.deps.json` in addition to the Core root `.deps.json` and the intended nested ModuleHost `.deps.json`.

Correction: the ModuleHost project reference now sets `Private="false"`. This retains it as a build dependency while preventing the SDK from transferring the child's publish content to Core's root. The existing explicit target still copies every file required by the `dotnet module-host/Hispania.ModuleHost.dll` launcher into the architecture-approved `module-host/` directory. No artifact file was deleted after publish and no validator rule was weakened. The policy now permits only the five exact required paths under `module-host/`; tests assert those exact entries.

Architecture Freeze §13, the Core manifest contract and the allowlist require a clean Core artifact, one root Core dependency manifest, and the ModuleHost process isolated under `module-host/`. The nested process uses the existing Runtime/contracts; it does not imply a sandbox or hostile native-code isolation.

## C. Automated tests

Run on Windows x64 after the correction and policy update. Every suite completed with zero failures and zero skips:

- Core Release: **50/50**.
- Artifact validator: **26/26**.
- Slice 7 fixture lifecycle: **4/4**.
- Selected regression: Inventory/Platform/Storage **29/29**; Deployment/Hosting **35/35**; Runtime module infrastructure **32/32**; total **96/96**.

Security/auth coverage in Core and lifecycle suites includes unknown and revoked publishers, invalid signature, altered payload hash, corrupt archive, incompatible host, trusted signed fixture install, disable/enable, restart recovery, fixture failure isolation, and admin authentication/permission/Origin/CSRF checks. The ModuleHost integration tests run the actual out-of-process host from build output; they are not a smoke of the published Core process.

After the owner's published-Core smoke report, the agent reran `dotnet test tests/core/Hispania.Core.Tests.csproj --no-restore --filter FullyQualifiedName~ModuleAdministrationReusesSessionAuthorizationOriginAndAntiforgery --verbosity minimal`: **1/1 passed**. This confirms the current test's authorization/Origin/antiforgery behavior; its invalid-package request does not prove successful signed-fixture installation through published Core.

Final TRX files are in `%TEMP%\hispania-slice7-final-evidence-20261018`:

| Suite | TRX SHA-256 |
|---|---|
| Core | `E31DE75FB7812369FDD8F5CEFAC2FA946AC7630716B0BF72A1710341E87F6265` |
| Artifact validator | `658D5FB26CF8339E25BEA6F277BAE5667E253222DA8795DEF3E698B0585DAFED` |
| Fixture lifecycle | `15AE0545B36B1225137C9EE88DE8605F80B012C4AC5DA7257D3F8F73A3E57AA3` |
| Inventory/Platform/Storage | `9ADE2F31A14F1736A6E2B3606034177E445BA8D466646A3BBA2DC7A9B8DBB45E` |
| Deployment/Hosting | `7906311F5A1E7F824ABDA67CC5219BFAB7640F27D1FD3D985E71E71208D380E5` |
| Runtime module infrastructure | `5E875F048FE0DAAC2D70B13DDF1C3062CCEB1793719794773443112807EFBA76` |

## D. Windows x64 artifact

Publish command:

```powershell
dotnet publish apps/core/Hispania.Core.csproj -c Release -r win-x64 --self-contained false --no-restore -p:DebugSymbols=false -p:DebugType=None -o "$env:TEMP\hispania-slice7-accepted-20261018-publish"
```

The artifact was generated from source commit `591dc17db24f0afd3ad066c96b0b1db1494d0150`. The validator's `create` and subsequent `validate` both passed with no issues, policy `hispania-core-v1.0.0`, and **19 files**. Structure checks: exactly one root `.deps.json`; five expected ModuleHost files in `module-host/`; zero PDBs; zero forbidden business/dependency filename matches; zero of the six excluded historical fingerprints.

Manifest `contentId`: `44B43088658F2F70FE8A0745EA5DFC3B1519CBF10B9347F3F4B88835EEDB3B12`

Manifest SHA-256: `22D05298F7329BE8C2892BDEE33EBE71C6851EBC5D7A6B6BBF2A49C34F45FE87`

| Artifact file | SHA-256 |
|---|---|
| `Hispania.Core.dll` | `2B9AC69B5C4718F3F1590CEBC7AD927CFD8428EAE3B82545531B44693B13812E` |
| `Hispania.Core.deps.json` | `C0411ADAA85FDB2E5F9A7414698C1332312D198B92FE343547BC750BE5A28D07` |
| `Hispania.Core.exe` | `C0AC7D9FD3CEE15473D2CBF084667469BC023597704C55550AB703DBE0905F43` |
| `module-host/Hispania.ModuleHost.dll` | `1A6BEFD8ADBA5BF4EE056DB21C368D148AFB5AFB7ADEC2360E564F24D32C5349` |
| `module-host/Hispania.ModuleHost.deps.json` | `819BD7AAF94E558F0AABF82BF8D2218F815E196C43125AB3E88B94705A6FABBF` |
| `module-host/Hispania.ModuleHost.runtimeconfig.json` | `C230A317A54DD960BCBEB5F347F52E18DC665A26F7EFDA2159FCED9A5AC7E097` |

## E. Published-process smoke and temporary files

### Owner-reported complete published Core smoke — 2026-10-18

The owner reports executing `tools/test-slice7-published-fixture-lifecycle.ps1` against the accepted `net10.0/win-x64` publish and receiving **PASS — 14/14**. Reported fields: `overall=PASS`; content ID `44B43088658F2F70FE8A0745EA5DFC3B1519CBF10B9347F3F4B88835EEDB3B12`; `coreExitCode=0`; `secretsPrinted=false`; and `failureStage`, `errorCode`, and `failureDetailCode` all null.

The owner reports successful trusted-publisher configuration, fresh SQLite/bootstrap and HTTPS, admin authentication/permissions, signed-fixture installation with HTTP 201, trusted/authorized disabled listing, activation with HTTP 200 and healthy state, persistence after Core restart, disable and removal through administrative endpoints, controlled fixture failure without Core failure, clean shutdown, no residual processes, unchanged published artifact, and temporary cleanup. This is explicitly **operator-reported evidence**; Codex did not execute the full 14-check run. The agent separately ran the `-RestartOnly` probe against the same published artifact; it passed installation, activation, stop with exit code zero, restart, persisted enabled/healthy state, artifact integrity and cleanup.

The owner supplied the original sanitized JSON at the designated intake path. It was copied byte-for-byte from the owner's Downloads directory; source and evidence SHA-256 match. Structural validation passed: exactly 14 expected checks, all PASS; accepted Core content ID and Core/ModuleHost hashes match the accepted manifest; owner-attested fixture hashes match; `coreExitCode=0`; all failure fields null; `secretsPrinted=false`; no sensitive property names, secret-pattern values or user-profile paths detected. Evidence file SHA-256: `FDE46346CD3B88402D7F0E725A12F4B00DA806F930F4B28C810F988BC6FC6F56` (2,505 bytes). The content remains **owner-reported execution evidence**, not a full smoke run executed by Codex. No credentials, cookies, tokens or private keys are recorded.

### Formal owner acceptance — 2026-10-18

The owner stated: “Como propietario de HISPANIA, acepto formalmente Slice 7 — Module Manager & Trust, limitado al alcance Windows x64 validado.” This explicit acceptance authorizes documentary closure and marking Slice 7 DONE. It does not authorize commits, push, releases, public distribution, or Slice 8.

### Acceptance audit against R3 Slice 7

| Criterion | State | Evidence and scope |
|---|---|---|
| Accepted Windows x64 publish is allowlist/manifest compliant and unchanged | PASS | Agent rechecked all 18 manifest file entries against size and SHA-256; no mismatches. Validator passed with no issues. Content ID matches the report. Owner reports artifact unchanged during full smoke. |
| Explicit test publisher trust and signed fixture install; trust separate from permissions | PASS | Owner reports publisher trust configuration and HTTP 201 installation; disabled listing was trusted/authorized. Core and lifecycle automated suites cover trust, signature, integrity and authorization behavior. |
| Unknown/revoked signer, tampered hash/signature, corrupt archive and incompatible host fail closed before execution | PASS | Automated Core and fixture lifecycle coverage recorded in §C (Core 50/50, lifecycle 4/4); these cases are not claimed as manually repeated in the full smoke. |
| Activate only the explicit fixture; healthy status; signing grants no permissions | PASS | Owner reports activation and healthy state. Automated authorization/permission tests recorded in §C, including the targeted 1/1 rerun. |
| Enabled module survives published Core restart and restores healthy | PASS | Owner reports full smoke PASS; agent's `-RestartOnly` published-artifact probe independently verified the state. Same isolated data root, catalog and fixture directory were asserted by the probe. |
| Disable and uninstall through authenticated administrative endpoints | PASS | Owner reports both operations passed in the full smoke; endpoint/authentication/Origin/antiforgery contracts are documented in §C and the inspected Core route implementation. |
| Controlled module failure remains isolated from Core | PASS | Owner reports controlled failure without Core loss; automated lifecycle tests cover failure isolation. No sandbox or hostile-native-code guarantee follows. |
| Shutdown/checkpoint, process cleanup and temporary-secret cleanup | PASS | Owner reports exit code 0, checkpoint/clean shutdown, no residual processes and temporary cleanup; the agent's restart probe also passed clean shutdown and cleanup. |
| Rights-cleared, harmless fixture and bounded local use | PASS | Fixture provenance/right scope is recorded in the rights register and §F; no distribution or relicensing claim. |
| Full-run evidence retained with sanitized checks and package hashes | PASS — OWNER-REPORTED EXECUTION | Original JSON retained at `docs/architecture/evidence/slice7/published-core-fixture-smoke-20261018.json`; SHA-256 `FDE46346CD3B88402D7F0E725A12F4B00DA806F930F4B28C810F988BC6FC6F56`. Validation confirmed the 14 expected PASS entries, required result fields, manifest identity/hashes and sanitized status. |

### Artifact verification — read-only

Accepted artifact: `%TEMP%\hispania-slice7-accepted-20261018-publish`; manifest source commit `591dc17db24f0afd3ad066c96b0b1db1494d0150`; manifest SHA-256 `22D05298F7329BE8C2892BDEE33EBE71C6851EBC5D7A6B6BBF2A49C34F45FE87`; content ID `44B43088658F2F70FE8A0745EA5DFC3B1519CBF10B9347F3F4B88835EEDB3B12`. The agent independently checked all 18 listed files, sizes and hashes and reran the artifact validator: `passed=true`, `issues=[]`, policy `hispania-core-v1.0.0`. The publish directory contains those 18 files plus the manifest (19 files total). Core and ModuleHost hashes match the manifest values already recorded in §D.

### Temporary outputs — read-only inventory

The owner-reported exclusive full-smoke directory and test secrets were cleaned. No temporary files were deleted by Codex. The following prior publish/evidence/scratch entries remain in `%TEMP%`: `hispania-slice7-20261018-regression`, `hispania-slice7-accepted-20261018-publish`, `hispania-slice7-final-20261018-publish`, `hispania-slice7-final-evidence-20261018`, `hispania-slice7-rerun-20261018`, `hispania-slice7-rootcause-20261018-publish`, `hispania-slice7-accepted-20261018-descriptor.json`, `hispania-slice7-final-20261018-descriptor.json`, and `hispania-slice7-publish-diag.log`. Preserve the accepted publish, final TRX evidence, and root-cause log. The remaining build/publish outputs are optional scratch cleanup candidates, but their contents have not been reviewed in this audit and they were not deleted. Their retention is not a functional Slice 7 blocker; do not delete them without owner authorization.

## F. Rights and publication

The fixture is original project code with its origin recorded in the rights register. Runtime and ModuleHost reuse is authorized only for bounded local Slice 7 development; this report makes no ownership, relicensing, or public distribution claim. D6 remains a public distribution gate. Nothing was pushed or distributed.

## G. Git and closeout

Branch: `codex/hispania-v1-baseline-consolidation`. Commits for this task:

- `dd5e968` — `fix(core): isolate ModuleHost publish outputs`.
- `591dc17` — `fix(core): allow nested ModuleHost publish files`.
- This review's report/ledger updates are documentation-only and remain uncommitted at HEAD `ef7365bb5e7d7551cb1093519743cc1885757b5b`.

No push, merge, rebase, force-push, tag, or release occurred. Global roadmap remains exactly **4/14 — 28.6%** and Phase 1 remains **IN PROGRESS**. **Final state: DONE — ACCEPTED BY OWNER**, limited to the validated Windows x64 scope. All technical gates and durable evidence checks are satisfied within that scope. Temporary scratch-output disposition is optional and separate from technical acceptance. At the time of this Slice 7 closeout, Slice 8 had not started; its separate Step 0 is now recorded in EV-050.
