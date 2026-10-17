# HISPANIA v1.0 — PHASE 1 SLICE 3 REPORT

## A. STATUS

**PASS — BOUNDED TO WINDOWS X64 HOST AND SELECTED REGRESSION SUITES.** The first independent Empty Core builds, starts without modules, exposes only loopback liveness, stops cleanly, and its real framework-dependent publish passes the Slice 2 artifact policy. This does not certify Linux/ARM64 execution, release readiness, or deferred product capabilities.

## B. ROADMAP STATUS

Roadmap remains **4/14 — 28.6%** until Phase 1 is accepted in full. Phase 1 now has 3/10 completed slices; Slice 4 and later slices remain pending.

## C. PREFLIGHT

Authorized worktree: `C:\Users\joaqu\Documents\HISPANIA-v1-baseline-consolidation`; branch `codex/hispania-v1-baseline-consolidation`; initial HEAD `82c5af6132a719a6072e8d577e4ac7e36c9b070f`. The worktree was clean and no Git operation was in progress. Phase 0 Freeze, Slice 1 baseline, Slice 2 allowlist/validator, Evidence Ledger and rights register were reviewed. The worktree inventory now reconciles 15 preserved originals plus this authorized worktree: 14 saved detailed records match their original branch/HEAD/status counts and `phase20-production-validation` is the additional clean detached worktree. The 49 source/backup pairs matched their preservation manifest at preflight; final integrity is recorded in §O.

## D. CORE HOST STRUCTURE

Created `apps/core/Hispania.Core.csproj` and a small composition root: `Program.cs`, `Configuration/CoreHostOptions.cs`, and `Hosting/CoreHost.cs`. Added the isolated `tests/core/Hispania.Core.Tests.csproj` and `CoreHostTests.cs`. The project targets .NET 10 and uses the ASP.NET Core shared framework. It contains no module loading, domain services, database, Identity, scheduler, or worker execution.

## E. DEPENDENCY GRAPH

MSBuild evaluated graph: `ProjectReference=[]`, `PackageReference=[]`; framework references are `Microsoft.NETCore.App` and `Microsoft.AspNetCore.App`. The runtime dependency graph contains only those shared frameworks. No references to `apps/server`, Inventory, Media/Library, FFmpeg, WorkerAgent, PostgreSQL/Npgsql or domain projects were found. The policy rationale for the Core artifact-root conditional entry was clarified without weakening its fail-closed behavior; the real publish and policy tests were rerun afterward.

## F. BOOT & HEALTH

The published Windows x64 process starts with no modules and serves only `GET /health/live`. The default bind is `127.0.0.1:5070`; the response body is `{"status":"healthy"}`. A real process returned 200 for GET, 405 for POST, 404 for `/admin` and `/`, and no `Server` header. No sensitive state is returned. There is no CORS policy or other anonymous endpoint.

## G. CONFIGURATION & SECURITY

Defaults are loopback address, port 5070, 10-second shutdown timeout, and `Information` logging. Port and shutdown timeout are range-validated; non-loopback binding is rejected. `HISPANIA_CORE_LOG_LEVEL` is the only configurable logging override and invalid/`None` values fail closed before host start with exit code 2 and a safe error. Environment is read from the standard `ASPNETCORE_ENVIRONMENT`; version is included in structured lifecycle logs. No credentials, tokens, secrets, administrative route, or module permission mechanism were added. The liveness route is the explicitly permitted public exception.

## H. LIFECYCLE

The host uses the generic .NET host lifecycle with a bounded shutdown timeout and structured start/stop logging. In-process tests verified start, stop, cancellation, occupied-port startup failure and empty-module operation. A real published process was started and answered health requests, rejected invalid logging configuration with exit 2, then received Ctrl+C and shut down; port 5070 was verified closed afterward. SIGTERM was not independently exercised on Windows.

## I. REAL PUBLISH VALIDATION

Built and published `net10.0` / `win-x64`, framework-dependent (`--self-contained false`). The four published files are `Hispania.Core.dll`, `.deps.json`, `.exe`, and `.runtimeconfig.json`. The evaluated graph and a descriptor tied to implementation source `8d810fdd3ba23604ee113ee02767a05c2789b9ff` were supplied to the Slice 2 validator. Real manifest create, validate, and repeated create/validate all passed with stable content ID `3A491EC7C047AF8E4F30E6A68A4D5E45D85C6C85C3E32E754B387096441C7840`; generated time may differ. The six excluded hashes were scanned against the actual published files with zero hits. Validator policy suite passed 25/25 after the bounded policy rationale clarification. No simulated publish is counted as evidence.

Evidence SHA-256: evaluated graph `E18E7AFD7B0F2882E5098851B3EE3D52230CB297884223264FA01F79A3F2D29B`; publish log `72E128CC6D001415ED5940C45F69F9A05392947BAAEFDD33DEAC8AB6A8154931`; final manifest `ABE60B7626323A8AB0B7E6B256D1B5140D1E766419BB71E93686A8E2EEA0E0AF`. Manifest create/validate output logs each have SHA-256 `516BDA89803E77A05FC7ADFDE20BFB54D3733FA5537479A46995C22B0D53FDCA`.

## J. TEST EXECUTION

Environment: Windows 11 x64, .NET SDK 10.0.401, runtime 10.0.12. Build completed with 0 warnings and 0 errors. Core tests: 17/17 passed, 0 failed/skipped; TRX SHA-256 `F8FFD33A0F849FB4641833D7DD6676D01C5643535DF63B39BBFFD00E4D303BFB`. Slice 2 validator tests: 25/25 passed, 0 failed/skipped; TRX SHA-256 `A7788E4248F456090DDA869324E0D3B3E0875B5EDBCF899046EA0A68CEFC0B3F`.

Core build log SHA-256 `D415F658277F72FD25295AFF6F88D9EA9AC5FFF73A3A233EDF5E295E47189410`; Core test log `7607553F34EBC9A19551CDDD0C8DBC825D67ABC620FCF749071FEC46129B4B37`; validator test log `7500B2E5970CA46146E9B2536D2FA67A398112BFA4067EF67373C9F2B860A048`.

Primary commands (all outputs/results were directed under `%TEMP%\hispania-slice3-20261017`):

```powershell
dotnet test tests/core/Hispania.Core.Tests.csproj --no-restore --artifacts-path "$run\artifacts" --results-directory "$run\results-final2" --logger 'trx;LogFileName=core-tests-final2.trx' -p:NuGetAudit=false
dotnet publish apps/core/Hispania.Core.csproj -c Release -r win-x64 --self-contained false --no-restore --artifacts-path "$run\artifacts" --output "$run\publish\win-x64-final" -p:NuGetAudit=false
dotnet test tests/core-artifact-validator/Hispania.CoreArtifactValidator.Tests.csproj --no-restore --artifacts-path "$run\artifacts" --results-directory "$run\results-validator-final" --logger 'trx;LogFileName=validator-final.trx' -p:NuGetAudit=false
```

The real-process smoke log SHA-256 is `5FB23589E8AFCEBAEEEC3B27ED5956E264F4B656A1CC17E191CB586F321B45FC`; invalid-configuration log `2EC884474744441F0AEB7C60F00A3D91AF47D4A26D71B9FEB63976D5FBEC3FA6`. Temporary root `%TEMP%\hispania-slice3-20261017` is not committed.

## K. REGRESSION

Reran the selected Slice 1 suites at the Slice 3 source HEAD using external temporary artifacts: Inventory/Platform/storage 29/29, Deployment/Hosting 35/35, and bounded Runtime/module 19/19. Total **83/83**, 0 failed/skipped, matching Slice 1's 83-test selected baseline. TRX SHA-256: Inventory `83048F14222AE21E8EDDB6A356B3E4CEC39C9354B46B2BCDA62BAFFC692AE182`; Deployment `7D33AABD35CF08FB7442176B446D835F4419F5D23CCC369BB46E767F56A97EB0`; Runtime `4AD2CFBC727EC0EB6EA38FA15A573D381D22A4397CE004DDD8A706FF553B9DA4`. Corresponding test-log SHA-256: Inventory `DF64C43726893C5FDDFA2B3E699FF84923FDC5D79896405051C90CAA3F8D39CD`; Deployment `B351E69CA748E0DF19704C4E3582104FC25749968F394AC7614048694A2386EF`; Runtime `683F96819B8EDEB0C3643F8099A8BECEDEA7B826B413DACA75764F2EE785589E`. This is the same bounded selection, not the entire repository test suite.

## L. RIGHTS & LICENSES

No historical fixture or unknown-rights asset was used. The publish contained none of the six excluded hashes. `LICENSE.md` remained byte-identical at SHA-256 `75EAE29E043C82B168894AFAAA148ADE2E9A43B4A72513FF18CFD22434F9BD3D`. This work authorizes local development and validation only; no third-party distribution, relicensing or release clearance is implied.

## M. DOCUMENTATION

This report records Slice 3; EV-044 was added to the Evidence Ledger. The Slice 2 report links to the reconciled original-worktree inventory. The dependency classification records the realized Core project graph, contributor rights register has a bounded Slice 3 row, and the versioned artifact allowlist contains only the tested rationale clarification. No historical reports were rewritten.

## N. DEFERRED VALIDATION

Core SQLite, Identity/authorization, Module Manager/trust, Coordinator, Workers and Data Plane remain unimplemented. SQLite native dependency/publish validation belongs to Slice 4. Linux x64 and Linux ARM64 publishing/execution, including native ARM64, remain untested. SIGTERM was not separately exercised. No release, distribution, or all-platform support claim is made.

## O. INTEGRITY

Final closeout confirmed all 15 original worktree identities/branches/HEADs and pre-existing status counts were preserved; `phase20-production-validation` remains clean and detached. All 49 live source hashes and 49 backups match the preservation manifest. `LICENSE.md` hash is unchanged. Secret scanning found no credential-like additions in the staged documentation and Core source. Only `apps/core`, `tests/core`, and Slice 3 architecture/evidence documentation changed across the three thematic commits; `apps/server`, existing public contracts, historical tests and license were untouched. `git diff --check` passed. No push was made; no upstream is configured.

## P. COMMITS

- `af1356a7743f5a46d19955be1b3eded18175354d` — `feat(core): add independent minimal host` (Core host, initial test project).
- `8d810fdd3ba23604ee113ee02767a05c2789b9ff` — `feat(core): validate core logging configuration` (safe logging configuration and tests).
- Documentation/evidence closeout commit and exact paths are recorded in the final task response.

## Q. FINAL STATE

Branch: `codex/hispania-v1-baseline-consolidation`; final HEAD and clean worktree are recorded in the final task response. No push, merge, tag, release, or Slice 4 work occurred.

## R. NEXT ACTION

**SLICE 3 PASS — AWAIT NEXT PROMPT.** Stop here. Slice 4 requires a separate owner prompt.
