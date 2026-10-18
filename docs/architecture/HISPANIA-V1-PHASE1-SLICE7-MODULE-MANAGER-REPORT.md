# HISPANIA v1.0 — Phase 1 Slice 7 Report

## A. Status

**PARTIAL — root manifest duplication is fixed and the Windows x64 artifact passes its allowlist/manifest gate.** The published-process smoke could not run in this agent environment, and temporary evidence/publish directories remain. Phase 1 remains **IN PROGRESS**; global roadmap stays exactly **4/14 — 28.6%**. Do not start Slice 8.

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

The automated smoke attempt was rejected by the execution tool before it started; no process, certificate, or smoke directory was created. This smoke has not been reported as passing. The accepted artifact and final test evidence remain available in `%TEMP%` for owner verification. Sensitive-file inventory across those directories found zero `.pfx`, `.p12`, `.pem`, `.key`, `.sqlite`, `.db`, cookie, or token files. The temporary publish/test outputs themselves have not been removed; cleanup is therefore pending.

Owner-run published Core smoke, if needed to close the process gate:

1. In PowerShell, set the artifact and a new, exclusive data directory under `%TEMP%`. Use an operator-controlled test PFX whose SAN includes `127.0.0.1`. The following prompts for the PFX password without echo and avoids a command-line argument or log:

   ```powershell
   $artifact = Join-Path $env:TEMP 'hispania-slice7-accepted-20261018-publish'
   $data = Join-Path $env:TEMP ('hispania-slice7-owner-data-' + [guid]::NewGuid().ToString('N'))
   $env:HISPANIA_CORE_DATA_DIRECTORY = $data
   $env:HISPANIA_CORE_LISTEN_ADDRESS = '127.0.0.1'
   $env:HISPANIA_CORE_PORT = '5070'
   $env:HISPANIA_CORE_HTTPS_CERTIFICATE_PATH = '<absolute path to operator test PFX>'
   $secure = Read-Host 'PFX password' -AsSecureString
   $bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
   try { $env:HISPANIA_CORE_HTTPS_CERTIFICATE_PASSWORD = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr) }
   finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr) }
   ```
2. Run `& "$artifact\Hispania.Core.exe"` in the foreground. From a second PowerShell window run `Invoke-RestMethod -SkipCertificateCheck https://127.0.0.1:5070/health/live` and confirm `status` is `healthy`; `Invoke-WebRequest http://127.0.0.1:5070/health/live` must fail. Return to the first window and press Ctrl+C; confirm the host exits cleanly.
3. Return to the first window after Ctrl+C. Clear the environment with `Remove-Item Env:HISPANIA_CORE_DATA_DIRECTORY,Env:HISPANIA_CORE_LISTEN_ADDRESS,Env:HISPANIA_CORE_PORT,Env:HISPANIA_CORE_HTTPS_CERTIFICATE_PATH,Env:HISPANIA_CORE_HTTPS_CERTIFICATE_PASSWORD`. Verify `$data` is the unique `hispania-slice7-owner-data-*` child of `$env:TEMP`, remove only that exact directory, and confirm it no longer exists. Do not delete the operator's PFX.

This checks the published Core process, HTTPS-only listener, SQLite initialization and shutdown. Fixture/ModuleHost execution is covered by the 4/4 lifecycle suite from build output; if §13 acceptance requires fixture install from the published Core itself, that remains an additional manual verification. After owner smoke, remove the retained `%TEMP%\hispania-slice7-*` scratch outputs and confirm no sensitive files remain.

## F. Rights and publication

The fixture is original project code with its origin recorded in the rights register. Runtime and ModuleHost reuse is authorized only for bounded local Slice 7 development; this report makes no ownership, relicensing, or public distribution claim. D6 remains a public distribution gate. Nothing was pushed or distributed.

## G. Git and closeout

Branch: `codex/hispania-v1-baseline-consolidation`. Commits for this task:

- `dd5e968` — `fix(core): isolate ModuleHost publish outputs`.
- `591dc17` — `fix(core): allow nested ModuleHost publish files`.
- A documentation/evidence commit records this closeout update.

No push, merge, rebase, force-push, tag, or release occurred. Global roadmap remains exactly **4/14 — 28.6%** and Phase 1 remains **IN PROGRESS**. **Final state: PARTIAL** because published-process smoke and temporary-output cleanup remain pending. No Slice 8.
