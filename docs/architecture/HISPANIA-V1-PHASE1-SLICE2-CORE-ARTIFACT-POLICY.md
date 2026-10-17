# HISPANIA v1.0 — Phase 1 Slice 2 Core Artifact Policy

**Status:** Implemented and validated over synthetic fixtures only. **No Empty Core host or Core publish exists yet.** This policy is version 1 and deliberately fails closed.

## Dependency boundary

The candidate graph is recorded in [HISPANIA-V1-CORE-DEPENDENCY-CLASSIFICATION.csv](HISPANIA-V1-CORE-DEPENDENCY-CLASSIFICATION.csv). Project references were inspected from the current `.csproj` files at Slice 2's starting HEAD; target framework is `net10.0` from the shared build props. The inventory separates:

- **Allowed:** ModuleSystem.Contracts, Platform.Contracts, and .NET 10 shared frameworks `Microsoft.NETCore.App` and `Microsoft.AspNetCore.App`.
- **Conditional:** Runtime (its assembly contains web contribution, experiment and distributed-worker features beyond the minimal lifecycle API); Identity and Authorization implementations (Slice 5); all Core SQLite packages/provider projects (Slice 4); and Core host (Slice 3). Conditional entries are documentation candidates and are rejected by the validator until explicitly promoted to `allowed`.
- **Forbidden:** existing Platform SQLite provider because its graph pulls Identity, Authorization, Jobs and Maintenance; `apps/server`; Inventory, Media, Library, Playback, FFmpeg/FFprobe, PostgreSQL, WorkerAgent and domain integrations.
- **Deferred:** ModuleHost for Slice 7, Jobs/Maintenance if a later frozen Core profile requires them, and domain-owned persistence under its module's phase.

The existing Runtime project has direct references only to ModuleSystem.Contracts and Platform.Contracts, `NuGet.Versioning` 7.9.0 and `Microsoft.AspNetCore.App`. Its full assembly surface has not yet been accepted for Core, so both Runtime and its NuGet package remain conditional. Identity and Authorization each reference Platform.Contracts only. The existing Platform SQLite provider directly references Platform.Contracts, Identity, Authorization, Jobs and Maintenance applications, plus `Microsoft.Data.Sqlite` 10.0.12 and `Microsoft.AspNetCore.App`; it is not reused as Core persistence.

The `apps/server` graph is not copied or transformed. Media's Library SQLite/PostgreSQL adapters, Inventory persistence, distributed work and Workers remain outside Core. PostgreSQL/Npgsql is explicitly rejected as a Core dependency. Contracts and module-owned persistence remain separate.

## Versioned allowlist and phases

The machine-readable [allowlist v1](HISPANIA-V1-CORE-ARTIFACT-ALLOWLIST-v1.json) identifies project, framework, package and native runtime asset dependencies by exact identity and version or an explicit `10.0.*` framework version range. Each row declares permitted phases (`build`, `runtime`, `publish`), status and rationale. Only `status: allowed` is admitted; conditional, forbidden, deferred, unknown IDs, unknown versions, and unknown phases fail closed. Every resolved dependency is checked, including transitive packages read from the selected `.deps.json` target.

The initial publish profile is **framework-dependent** for `win-x64`, `linux-x64` and `linux-arm64`. Self-contained publishing is not admitted in policy v1: its runtime pack file graph is not yet inventoried. Framework-dependent output must include the expected Core assembly, `.deps.json` and `.runtimeconfig.json`; the latter must identify `net10.0` and the shared framework versions. Allowed output files are exact names/paths, not broad extension globs. Conditional file names (Runtime, identity/auth, SQLite and package assemblies) are explicitly listed separately and rejected until their dependency rows are promoted. Runtime pack files may be admitted later only as explicitly identified, RID-specific content.

SQLite is the approved Core persistence direction but is not implemented or admitted yet. Candidate versions are `Microsoft.Data.Sqlite`/`Microsoft.Data.Sqlite.Core` 10.0.12 and SQLitePCLRaw bundle/core/native/provider packages 2.1.12. R5 records package metadata as MIT and Apache-2.0, respectively; those records are not artifact-specific distribution clearance. Expected native assets are listed by RID in policy v1 as conditional. Their exact publish paths, hashes, license notices and native payload must be confirmed by a real Core publish in Slice 4/9. No EF Core or PostgreSQL package is allowed.

## Manifest v1

The formal schema is [HISPANIA-V1-CORE-ARTIFACT-MANIFEST.schema.json](HISPANIA-V1-CORE-ARTIFACT-MANIFEST.schema.json). The validator implements the same v1 fields and strict JSON property handling. It records schema version, artifact ID, source commit, TFM, RID, publish mode, generated UTC timestamp, canonical content ID, sorted file entries (normalized relative path, size, SHA-256, file type, optional origin), resolved dependencies by kind/version/phase/directness, explicit exceptions, and policy result.

`contentId` is SHA-256 over the canonical ordered artifact metadata, file entries, dependency entries and exceptions. It excludes `generatedAtUtc` and the validator's policy-result text. Therefore identical content produces the same identity even when generated at different times. The timestamp is evidence metadata, never an integrity substitute. Absolute developer paths, traversal and non-NFC paths are rejected; file origins cannot contain absolute or traversal paths. The manifest is written at the reserved relative artifact-root path `core-artifact-manifest.json` and is excluded from its own file list to avoid a self-hash cycle.

The schema carries an exception record shape (`id`, justification, scope, evidence, approval, review-by date); **allowlist v1 authorizes zero exceptions**. A non-empty exception list currently fails closed. No exception may override any of the six unknown-rights hashes in the current policy.

## Validator and invocation

Tool: `tools/core-artifact-validator/Hispania.CoreArtifactValidator.csproj` (BCL only; no project/domain/NuGet runtime dependency). Tests: `tests/core-artifact-validator/Hispania.CoreArtifactValidator.Tests.csproj` (uses the repository's already reviewed xUnit/Test SDK test-only versions). Run from the repository root:

```powershell
dotnet run --project tools/core-artifact-validator/Hispania.CoreArtifactValidator.csproj -- create --root <publish-dir> --descriptor <artifact-descriptor.json> --policy docs/architecture/HISPANIA-V1-CORE-ARTIFACT-ALLOWLIST-v1.json
dotnet run --project tools/core-artifact-validator/Hispania.CoreArtifactValidator.csproj -- validate --root <publish-dir> --policy docs/architecture/HISPANIA-V1-CORE-ARTIFACT-ALLOWLIST-v1.json
```

`create` inventories files and reads the selected `.deps.json` plus `.runtimeconfig.json`; the descriptor supplies the evaluated build-phase project/package graph, artifact identity and provenance. The validator then independently re-reads the publish graph and rejects omitted or extra runtime/publish dependencies. It reports JSON with human-readable issue strings and exits 0 only on pass; policy violations exit 1, malformed invocation/input exits non-zero. The output JSON is stable except generation time and policy-result issues.

The validator rejects unknown files, missing required files, hashes/sizes that differ, prohibited or unlisted direct/transitive dependencies, schema/TFM/RID/publish mismatches, unknown file types, duplicate paths, case-insensitive prefix collisions (including directory segments), absolute paths, traversal, non-portable Windows path names, non-NFC paths and all symlinks/reparse points. It does not follow directory links. The artifact staging directory must remain immutable while validation runs; the tool is a build gate, not an online hostile-filesystem race monitor.

The build-phase dependency list must be generated from the evaluated MSBuild project graph and restore assets, not manually curated from package names. Publish-phase package/project closure is parsed from the artifact `.deps.json`; framework references come from `.runtimeconfig.json`; native runtime assets are parsed from the selected RID target and must also appear in the file manifest. This source-graph adapter has no real Core graph to exercise in Slice 2 and must be wired and proven when the host exists.

## Rights boundary and exclusions

The six SHA-256 values in the allowlist match the five historical unknown-rights DLLs and the legacy demo MP4 listed in `HISPANIA-V1-UNKNOWN-RIGHTS-COMPONENTS.csv`. Validator tests use synthetic files and hash strings; they do not open or execute the unknown DLLs or process the MP4. Historical files remain untouched. A test run or passing artifact policy grants no distribution rights. `LICENSE.md` remains unchanged.

## Publish integration gates

- **Slice 3:** create the first real independent host and evaluate the MSBuild project graph; validate its framework-dependent RID publish. Do not change `apps/server`.
- **Slice 4:** add the dedicated SQLite provider, resolve actual package/native assets and verify each supported RID.
- **Slice 7:** add Module Manager/ModuleHost only if required; review signer/runtime artifacts and keep OOP/ALC labeled as non-sandbox.
- **Slice 8:** validate the complete isolated artifact, including unexpected copied files and rights hashes.
- **Slice 9:** run and retain per-RID publish manifests on available Windows/Linux targets; cross-publication alone is not native execution evidence.
- **Slice 10:** acceptance uses the exact final artifact manifests and evidence; Slice 2's synthetic fixture is never represented as a Core artifact.

No Core host, Core SQLite provider, endpoints, new module manager, release or publish was created in Slice 2.
