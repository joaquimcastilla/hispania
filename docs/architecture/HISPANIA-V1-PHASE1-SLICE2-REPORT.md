# HISPANIA v1.0 — PHASE 1 SLICE 2 REPORT

## A. STATUS

**PASS — BOUNDED TO SYNTHETIC FIXTURES.** The policy, schema, and validator are implemented and the validator suite passes 25/25. No `apps/core` project or real Core publish exists, so this is not evidence for a real Core artifact.

## B. ROADMAP STATUS

```text
HISPANIA v1.0 — ROADMAP STATUS

[██████░░░░░░░░░░░░░░] 4/14 — 28.6%

[✓] Phase 0 — Architecture Freeze
[✓] 0.1 — Baseline Reconciliation Audit
[✓] 0.2 — Baseline Consolidation
    [✓] Preservation — 49/49 SHA-256
    [✓] Owner Freeze integrated
    [✓] WIP Semantic Reconciliation — R1
    [✓] Gate I — Identity and Authorization
    [✓] Gate II — Platform Events and Persistence
    [✓] Gate III — Inventory Integration
    [✓] Gate IV — Platform-only and Hosting
    [✓] Gate V — Operational Evidence
    [✓] Owner Acceptance
[✓] 0.3 — Phase 1 Readiness
    [✓] R1–R5
    [✓] Final Owner Acceptance
[~] 1 — Empty Core
    [✓] Slice 1 — Baseline Regression & Compatibility
    [✓] Slice 2 — Core Allowlist & Manifest
    [ ] Slice 3 — Independent Core Host
    [ ] Slice 4 — SQLite & Lifecycle
    [ ] Slice 5 — Identity & Authorization
    [ ] Slice 6 — Minimal Endpoints
    [ ] Slice 7 — Module Manager & Trust
    [ ] Slice 8 — Artifact Isolation
    [ ] Slice 9 — Cross-platform Publishing
    [ ] Slice 10 — Final Acceptance
[ ] 2 — Module System & SDK
[ ] 3 — Inventory Module
[ ] 4 — Coordinator & Distributed Workers
[ ] 5 — Direct Data Plane & Gateway
[ ] 6 — Media Module
[ ] 7 — Identity, Permissions & Clients
[ ] 8 — Administration & Recovery
[ ] 9 — Cross-platform Validation
[ ] 10 — Security & Release v1.0
```

The overall roadmap stays at 4/14 until Phase 1 acceptance.

## C. PREFLIGHT

- Worktree: `C:\Users\joaqu\Documents\HISPANIA-v1-baseline-consolidation`.
- Branch: `codex/hispania-v1-baseline-consolidation`.
- Starting HEAD: `af03bca20d9c21d95e847dc47d027b0965d98131`.
- No incomplete Git operation was present. Initial worktree was clean before Slice 2 edits.
- Freeze, final Step 0.3 acceptance, Slice 1 report/baseline, Evidence Ledger, provisional allowlist, R5 dependency/license inventory were present.
- The original worktree inventory is reconciled in [HISPANIA-V1-WORKTREE-PRESERVATION-INVENTORY.md](HISPANIA-V1-WORKTREE-PRESERVATION-INVENTORY.md): 15 original roots plus this authorized integration worktree make 16 current entries. The archive snapshot details 14; its missing record is the clean detached `phase20-production-validation` root at HEAD `2cfb8e6f8756b6ee934143e5393bea6e1adf1cc3`. All 14 archived records match their saved HEAD, branch and status count.
- The 5 preserved source worktrees have 49 source files and 49 backups; every live and backup SHA-256 matches the preservation manifest (49/49 pairs).

## D. DEPENDENCY CLASSIFICATION

The detailed inventory is [HISPANIA-V1-CORE-DEPENDENCY-CLASSIFICATION.csv](HISPANIA-V1-CORE-DEPENDENCY-CLASSIFICATION.csv).

- **Allowed:** `Hispania.ModuleSystem.Contracts`, `Hispania.Platform.Contracts`, .NET 10 `Microsoft.NETCore.App` and `Microsoft.AspNetCore.App` shared frameworks.
- **Conditional:** Runtime and `NuGet.Versioning` 7.9.0; Identity/Authorization applications; planned Core host and provider-neutral persistence contract; planned Core SQLite provider; `Microsoft.Data.Sqlite` 10.0.12 and SQLitePCLRaw 2.1.12; Hosting and native SQLite assets pending later slices and real graph review.
- **Forbidden:** `apps/server`, Inventory, Media, Library, Playback, Transcoding, FFmpeg/FFprobe, WorkerAgent/distributed domain services, Npgsql/PostgreSQL, EF Core, and the existing Platform SQLite provider whose graph includes Identity, Authorization, Jobs and Maintenance.
- **Deferred:** ModuleHost and later module/domain-owned persistence or capabilities, according to the planned slices.
- Existing project metadata and dependency evidence were inventoried; no real Core build graph was available.

## E. CORE ALLOWLIST

Versioned policy `hispania-core-v1.0.0` is in [HISPANIA-V1-CORE-ARTIFACT-ALLOWLIST-v1.json](HISPANIA-V1-CORE-ARTIFACT-ALLOWLIST-v1.json). It records dependency kind/identity/version, applicable build/runtime/publish phases, status, rationale, and assembly identity where relevant. Only `allowed` rows pass; conditional, forbidden, deferred, unknown IDs/versions/phases fail closed. Dependency extraction includes every package in the selected `.deps.json` target, so transitive runtime packages are checked. Project build-graph dependencies are supplied through the descriptor; a future Core MSBuild graph must be evaluated and complete before publish validation.

Policy supports `win-x64`, `linux-x64`, `linux-arm64`, `net10.0`, and framework-dependent publish only. Self-contained publish remains rejected until runtime packs are inventoried. Exact allowed and conditional file paths are used; unknown runtime files are not blanket-denied by extension. Six excluded hashes are checked independently of names and match all six rows in the unknown-rights inventory.

## F. MANIFEST SCHEMA

[HISPANIA-V1-CORE-ARTIFACT-MANIFEST.schema.json](HISPANIA-V1-CORE-ARTIFACT-MANIFEST.schema.json) defines schema v1: artifact ID, source commit, TFM, RID, publish mode, UTC generation time, content ID, files (normalized relative path, size, SHA-256, type, optional origin), resolved dependencies (kind, ID, version, phase, directness), explicit exception fields, and policy result. Unknown JSON properties are rejected. Content ID hashes canonical sorted artifact metadata, files, dependencies and exceptions; it excludes generation timestamp and policy-result text. It does not claim publisher authenticity. Exception records are schema-shaped, but policy v1 rejects nonempty exceptions.

## G. VALIDATOR IMPLEMENTATION

- BCL-only console tool: `tools/core-artifact-validator/`.
- New synthetic xUnit suite: `tests/core-artifact-validator/`.
- CLI supports `create` and `validate`; the output manifest is reserved at the artifact root and excluded from its own file inventory.
- Validates required/extra files, dependency identities and versions, TFM/RID/mode, schema, deterministic content ID, file hashes/sizes, exclusions and paths.
- Rejects root/artifact symlinks or reparse points, absolute/traversal/backslash/non-NFC/Windows-reserved paths, case-insensitive path collisions, forbidden patterns and unknown-rights hashes. Directory scanning does not follow reparse points. The documented operating assumption is an immutable staging tree during validation to avoid concurrent filesystem replacement races.
- `dotnet msbuild ... -getItem:PackageReference -getItem:ProjectReference` returned empty arrays for the tool project; TargetFramework is `net10.0`.

## H. SECURITY TESTS

25/25 tests passed, 0 failed, 0 skipped. Coverage includes valid synthetic manifest and CLI round trip; extra and missing files; forbidden direct/transitive packages and versions; TFM/RID and path/mode checks; wrong hash/size; absolute and traversal paths; duplicates and case collisions; unknown schema and malformed JSON; excluded hash; external directory junction; deterministic identity under order/timestamp variation; forbidden root-level rights-sensitive files; conditional file/package rejection under checked-in policy; and nonzero exit on policy violation. The junction test ran on Windows and was not skipped. All fixture payloads were synthetic; no historical DLL was executed and the MP4 was not opened or processed.

## I. SQLITE STRATEGY

Core SQLite is an approved v1 provider direction, but remains conditional for implementation and artifact admission. Candidate graph: `Microsoft.Data.Sqlite`/`Microsoft.Data.Sqlite.Core` 10.0.12 and SQLitePCLRaw bundle/core/native/provider packages 2.1.12. R5 package metadata identifies MIT and Apache-2.0, respectively. Native assets are candidates for `win-x64`, `linux-x64`, and `linux-arm64`; exact output paths, hashes, selected native payload and notices require a real Slice 4 publish and later RID review. The existing Platform SQLite provider is not reused because its graph includes Identity, Authorization, Jobs and Maintenance. No Core SQLite provider was implemented.

## J. RIGHTS AND LICENSES

The tool uses .NET BCL APIs only. The test project uses xUnit 2.9.2, xUnit runner 2.8.2 and Microsoft.NET.Test.Sdk 17.11.1 already reviewed in the R5 package inventory. No new runtime package was added. The six unknown-rights SHA-256 values are in the versioned policy and match `HISPANIA-V1-UNKNOWN-RIGHTS-COMPONENTS.csv`. `LICENSE.md` remains byte-identical (SHA-256 `75EAE29E043C82B168894AFAAA148ADE2E9A43B4A72513FF18CFD22434F9BD3D`). No distribution or relicensing clearance is implied.

## K. TEST EXECUTION

Environment: Windows 11 x64, .NET SDK 10.0.401, .NET runtime 10.0.12. Restore used an empty local feed and the existing package cache; all outputs and evidence are under `%TEMP%\hispania-slice2-20261017`.

Commands:

```powershell
dotnet restore tests/core-artifact-validator/Hispania.CoreArtifactValidator.Tests.csproj --source C:\Users\joaqu\AppData\Local\Temp\hispania-slice2-20261017\empty-feed --artifacts-path C:\Users\joaqu\AppData\Local\Temp\hispania-slice2-20261017\artifacts -p:NuGetAudit=false
dotnet build tools/core-artifact-validator/Hispania.CoreArtifactValidator.csproj --no-restore --artifacts-path C:\Users\joaqu\AppData\Local\Temp\hispania-slice2-20261017\artifacts -p:NuGetAudit=false
dotnet test tests/core-artifact-validator/Hispania.CoreArtifactValidator.Tests.csproj --no-restore --artifacts-path C:\Users\joaqu\AppData\Local\Temp\hispania-slice2-20261017\artifacts --results-directory C:\Users\joaqu\AppData\Local\Temp\hispania-slice2-20261017\results-final6 --logger "trx;LogFileName=slice2-validator.trx" -p:NuGetAudit=false
dotnet msbuild tools/core-artifact-validator/Hispania.CoreArtifactValidator.csproj -getItem:PackageReference -getItem:ProjectReference -getProperty:TargetFramework
```

Build: 0 warnings, 0 errors. TRX: 25 total, 25 passed, 0 failed, 0 not executed. Temporary evidence:

- `results-final6/slice2-validator.trx` SHA-256 `FE2D2DC501FA4BEA2A40FAC589F584667901A5EBB9B82DD9F189BDCB0162D1EA`.
- `logs-final6/build.txt` SHA-256 `DE3EA214432F7C17E9710D716E7BD3DDD3B5B2BAB164D5EADCFA0BE876F105CC`.
- `logs-final6/test.txt` SHA-256 `40606D2B240C3BAC46187259F5A89233999B28C3C9D39F9A0D8BAE6518A9F183`.
- `logs-final6/evaluated-refs.json` SHA-256 `2577A469EFD20DD6C291611D115A3B27AC7DB0760E1EB25A0E78D55B490FAAE8`.

## L. REGRESSION

Slice 1's 83/83 baseline evidence remains documented in EV-042. It was not rerun because Slice 2 adds an isolated tool and tests and changes no existing project, shared contract, application, or baseline test. The new tool has no project references. This bounded change presented no shared-code regression surface.

## M. DEFERRED VALIDATION

No `apps/core` exists, so no actual Core graph or publish is certified. Slice 3 must validate the first real host and complete evaluated project graph. Slice 4 must exercise SQLite dependencies and native outputs. Slice 7 must validate Module Manager dependencies. Slice 8 must demonstrate artifact isolation. Slice 9 must validate per-RID publishing on available platforms. Slice 10 owns final acceptance. `.deps.json` covers resolved runtime dependencies; future integration must also feed the complete evaluated build graph and published native/runtime assets. Bit-for-bit reproducible .NET binaries were not tested or claimed.

## N. DOCUMENTATION

Created the policy report, versioned JSON allowlist, manifest JSON Schema, and project/dependency classification CSV. Updated the provisional allowlist and contributor rights register. Updated the Evidence Ledger with EV-043. `LICENSE.md`, application code, existing public contracts and historical tests were not changed.

## O. INTEGRITY

- The [reconciled worktree inventory](HISPANIA-V1-WORKTREE-PRESERVATION-INVENTORY.md) lists all 15 original worktrees; 14 have archived detail rows and the additional detached `phase20-production-validation` root is clean. The authorized worktree is the 16th entry.
- All 49/49 preserved source/backup pairs match the manifest hashes.
- Unknown-rights hashes match the allowlist; the license hash is unchanged.
- Only new tool/test files and Slice 2 architecture documentation/rights/evidence records are in scope. No `apps/server`, existing contract, historical test, or license file changed.
- `git diff --check` passed before commit; no push was performed.

## P. COMMITS

Implementation commit: `e5653a34151fac236a69abdb70ddc6abc6e4d35e` — `build: add core artifact allowlist validator` (tool, tests, versioned allowlist and schema). Documentation/evidence commit: created after this report; its SHA and exact message are recorded in the final task response and Git history.

## Q. FINAL STATE

The final documentation commit, resulting HEAD, and clean worktree are recorded in the final task response. No push, merge, release, tag, or Slice 3 work occurred.

## R. NEXT ACTION

**SLICE 2 PASS — AWAIT NEXT PROMPT.** Stop here. Slice 3 requires the owner's next prompt.
