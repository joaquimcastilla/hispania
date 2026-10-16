# HISPANIA v1.0 — Dependency and License Audit (Step 0.3-R1)

**Baseline:** `9771b063fefe569a9268fb80cb6789dbc4c043b1`

**Environment:** Windows 11 x64, .NET SDK 10.0.401 / runtime 10.0.12, Node 24.19.0, npm 12.0.2.

**Scope:** manifest and package metadata inspection only; no installation scripts were run, no services were installed, and no artifact was published. This is an engineering inventory, not legal advice.

## Reproducible inventory

The detailed deduplicated inventory is [HISPANIA-V1-DEPENDENCY-INVENTORY.csv](HISPANIA-V1-DEPENDENCY-INVENTORY.csv). It records 101 resolved NuGet package/version pairs across 59 solution projects and 156 locked npm package entries. The NuGet package-license fields came from cached `.nuspec` metadata; npm license fields came from `apps/web/package-lock.json`. All 257 entries had license metadata, but their state is **REPORTED BY METADATA**, not independently verified or a redistribution clearance. Lock files are absent for .NET projects; the NuGet result reflects the restored graph at audit time and may vary if versions float or feeds change.

The historical static scan in Freeze §10 records 153 npm entries for its then-reviewed source baseline. R1 counted 156 versioned package entries in the committed lock at `9771b063...` (157 lock object keys including the root project). This is a source-snapshot/counting difference, not a change to any approved architecture decision; the historical finding is retained as historical evidence.

Reproduce without writing package outputs into the checkout (from the repository root; pick a fresh external directory):

```powershell
$audit = Join-Path $env:TEMP 'hispania-dependency-audit'
New-Item -ItemType Directory -Force $audit | Out-Null
dotnet list Hispania.sln package --include-transitive --format json > (Join-Path $audit 'nuget.json')
dotnet --info > (Join-Path $audit 'dotnet-info.txt')
node --version
npm --version
Get-Content apps/web/package-lock.json
```

For package metadata, inspect each resolved NuGet package's `.nuspec` in the configured global package cache and each npm package's lockfile `license` value. Rebuild the CSV after any dependency or lock change; this inventory was generated using .NET JSON output, the local NuGet cache and the committed npm lock. The repository has no `Directory.Packages.props`, `.NET packages.lock.json`, or checked-in `dotnet-tools.json` at this baseline. `apps/web` is marked `private`; the frontend lock records registry integrity and versions.

## Package set and distribution boundary

The 14 unique direct NuGet references are Microsoft.AspNetCore.TestHost 10.0.1; Microsoft.Data.Sqlite 10.0.12; Microsoft.EntityFrameworkCore, `.Design`, `.Relational`, `.Sqlite` 10.0.12; Microsoft.Extensions.Hosting.Systemd and `.WindowsServices` 10.0.0; Microsoft.Extensions.Logging.Abstractions 10.0.0; Microsoft.NET.Test.Sdk 17.11.1; Npgsql.EntityFrameworkCore.PostgreSQL 10.0.0; NuGet.Versioning 7.9.0; xunit 2.9.2; and xunit.runner.visualstudio 2.8.2. Other test projects resolve additional versions (captured in CSV), including Microsoft.NET.Test.Sdk 17.14.1 and xUnit 2.9.3.

The npm direct runtime dependencies are React and React DOM; Playwright, Testing Library, TypeScript, Vite, Vitest, jsdom and their dependencies are build/test tooling. The package-lock includes optional platform-specific Rolldown and TypeScript packages; their lock metadata is included. No frontend package publication is authorized by this audit.

Tracked binary/media inventory is limited to five historical compatibility DLLs and `apps/server/demo-assets/bp48-demo.mp4`. These DLLs are compatibility fixtures, not dependencies to copy into a new Core artifact. The MP4 is included by the legacy server project when `PlatformOnly` is not true. Its source, underlying media rights and redistribution terms are **BLOCKED — UNKNOWN RIGHTS**. No tracked font/icon/image files were found. SQLite uses `Microsoft.Data.Sqlite`/`SQLitePCLRaw` and a bundled `e_sqlite3` native package in relevant resolved graphs; the exact RID publish graph and native notice/source obligations must be regenerated and reviewed for each artifact. No FFmpeg executable is tracked: the adapter/worker projects are source code and invoke an external tool; do not infer that a runtime binary is bundled. GPU/driver dependencies are optional worker/environment requirements, not a Core dependency. PostgreSQL is present only in the Library persistence project and is excluded from the approved Core provider policy.

## License review

| Area | Metadata/evidence | Current classification | Development vs distribution |
|---|---|---|---|
| HISPANIA source/docs/assets | `LICENSE.md` grants personal, local, non-commercial use; owner direction 9.11 targets AGPLv3 Core and prefers Apache-2.0 SDK | **CONFLICT — OWNER DECISION REQUIRED** on authority/applicability and file-level scope; do not change the license in R1 | Internal study/testing is not public redistribution. Any public artifact requires established rights and approved license application first. |
| NuGet packages | 101 resolved package/version records in CSV; metadata reports mostly MIT/Apache-2.0, two `PostgreSQL` licenses and one xUnit license URL | **REVIEW REQUIRED**; metadata was not checked against upstream license texts, notices, patent terms or exact redistribution graph | Development use can proceed for internal validation subject to repository rights. Distribution needs notices/source obligations evaluated per shipped artifact. |
| npm packages | 156 lock entries; metadata includes MIT, Apache-2.0, MPL-2.0, ISC, BSD, CC0, BlueOak and others | **REVIEW REQUIRED**; package-lock declarations are not legal verification; frontend artifact inclusion not proven | Private web build is development use. A public web bundle requires per-package notice and license review, including whether source changes/offer obligations apply. |
| Native SQLite | SQLitePCLRaw package graph contains native SQLite assets | **REVIEW REQUIRED** | Establish target RID files, exact package notices and redistribution contents at publish time. |
| FFmpeg/ffprobe | Source adapters and process invocation exist; no tracked executable identified | **NOT APPLICABLE** to an artifact that does not distribute the tool; otherwise **REVIEW REQUIRED** | Verify runtime configuration and exact binary/license/build provenance if bundled or redistributed. |
| Historical DLLs and demo video | Tracked under `compatibility/` and legacy `apps/server/demo-assets/`; no per-file right evidence found in this audit | **BLOCKED — UNKNOWN RIGHTS** | Keep out of any new artifact until provenance and redistribution authority are documented. |
| OS/runtime/system libraries | .NET 10 framework/runtime and platform services; exact installed runtime is environment evidence, not a bundled payload manifest | **REPORTED BY METADATA** / artifact-specific review required | Framework-dependent vs self-contained publication changes what is redistributed; decide per target. |

No AGPL/Apache compatibility conclusion is offered for the combined work. SPDX/package declarations alone do not establish that a package is included, compatible, or authorized. The compatibility of MPL-2.0 and other npm licenses must be evaluated against the actual bundle/linking and distribution model, with legal review before public release.

## Gate conclusions

- **Internal Phase 1 development:** package metadata review alone is not a reason to block isolated internal coding; Freeze §16 gates for reproducible dependency/license inventory and Core distribution boundaries still need explicit acceptance. R1 did not start Phase 1.
- **Phase 1 artifact acceptance:** do not distribute publicly. Build a Core-only graph, verify each package/native file actually included, and retain generated SBOM/notices. The current personal/non-commercial project license conflicts with the approved open-source direction and must not be silently bypassed.
- **Public v1.0 release:** **LEGAL REVIEW REQUIRED** for ownership, contributor authority, license application, dependency notices/source, assets, FFmpeg/native binaries and combined-work boundaries.
- **Owner/legal actions:** establish authority for existing source and media assets; confirm the Core/SDK license boundary; decide how owner-approved license 9.11 applies to contributions and assets; approve a compliant per-artifact redistribution pack. No automatic decision was taken.
