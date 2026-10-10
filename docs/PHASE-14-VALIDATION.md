# Phase 14 — Versioning and compatibility foundation

Branch: `feature/phase-14-versioning-compatibility`, created exactly from `a52e7d5462c93778074b835816149c77005a42a1` after fetch and verification of the peeled annotated `phase-13-complete` tag. Functional Phase 13 validation remains `75e10edd560e396a6e509a386d5aae5dd7928827`. Date: 2026-10-05 (Europe/Madrid).

Initial tracked working tree was clean; `docs/MODULE-MATURITY-AUDIT.md` was the sole untracked input and is not part of the checkpoint/history. A separate `docs/ROADMAP.md` modification appeared during work and is excluded from Phase 14 commits. Neither file was authored or revised by this phase. Main and existing tags remain unchanged; no push, PR, merge, new tag or release is performed. Phase 15 is not started.

## Decisions and deliverables

- 22 axes documented: the requested 20 plus legacy host package compatibility level and dedicated FFmpeg subprocess protocol. Distribution, SDK, implementation, capability, package/manifest, transport, HTTP/DTO, envelope/payload, JobType/handler, handle, persistence, Web and instrumentation meanings are explicit.
- Root `version.json` is the single distribution documentation source at 0.7.0; no MSBuild/npm/module/protocol/schema versions are synchronized. Patches preserve declared minor-line commitments; pre-1.0 minors may deliberately break with owner review and migration notes. Future published 1.x promises preserve selected categories across minors; breaks need major or documented coexistence. No subsystem becomes 1.0.
- Machine catalog: 411 entries, comprising 398 CLR declarations and 13 non-CLR families; 110 V1/V2-named declarations. Classifications: 351 Public Experimental, 38 Stable Candidate (36 CLR + Range/fencing behavior families), 20 Internal, 2 Experimental, 0 Public Stable, 0 Deprecated. Counts include supporting candidate DTOs, not 38 separately mature subsystems. `IndependentGreetingModuleV2` is a demo implementation behind V1, not SDK 2.0. No Version1/Version2 declaration was found.
- Candidates: Identity query/command and supporting value/results, Authorization service/decision, AssetId/AssetReference, selected Direct Play/Range types and HTTP behavior, stale-generation fencing invariant. Remaining gates are explicit API/transitive DTO review, old clients/binaries/peers, populated upgrades, backup/restore and abuse/deployment policies as appropriate.
- Experimental: module SDK/runtime/package lifecycle details, worker transport DTOs, event payload evolution, Jobs JSON/handler representation, Storage provider API and Web/Admin HTTP DTOs. No accidental freeze of all CLR-public types.
- Host 1.0.0 is the hardcoded legacy acceptance value compared to manifest min/max host bounds; it does not establish SDK binary compatibility or product maturity. Values and package acceptance stay unchanged. ActivitySource/Meter 1.0.0 is instrumentation scope identity. Private `@hispania/web` 0.1.0 remains build artifact metadata, with no independent npm release evidence.
- Kernel Option B: empty csproj, no source and no production references; propose later removal, retain solution entries now. Minimal factual wording correction in ADR-0030; local maturity audit unchanged.
- Support: recorded Phase 13 Windows/Linux x64, SQLite, filesystem, InProcess/OutOfProcess, WorkerAgent, Modern/Classic execution; PostgreSQL partially validated for Library, not a universal Platform provider; Linux ARM64 theoretically supported only. Current Phase 14 local evidence is Windows x64, distinct from inherited Linux evidence.
- 1.0 target: selected existing Platform runtime/SDK/package, Identity/RBAC, Assets/filesystem/SQLite, Events/Jobs, single coordinator/fencing, safe Audit/Observability/Admin and static Web boundaries. Dynamic Web, HA/consensus, general object storage, complete ERP/Media, SSO, advanced MFA, multi-tenancy and autoscaling are explicitly excluded. Unselected shipped details remain experimental.
- Scaffold: api/modules/packages/protocols/data/web with owner/provenance/golden-fixture rules. Current candidate compiled reflection snapshot has 36 types and 459 lines; no historical N-1 corpus. Use existing .NET reflection tooling instead of adding an analyzer dependency or production attributes.
- Three directed tests enforce catalog parsing/unique IDs/required metadata/stability/source inventory/SDK internal exclusions, distribution/document/npm/host/protocol consistency, and reviewed candidate API signatures with SHA-256 acknowledgement. CI Linux and Windows require all three discovered/passed without skips and forbid capture mode. Existing recursive Platform graph/assembly architecture tests remain active; no production dependency is added.

## Local validation

| Check | Result |
| --- | --- |
| Platform-only build | Passed, zero warnings/errors |
| Full Windows solution build | Passed, zero warnings/errors |
| Full .NET regression | 545 passed, 0 failed, 1 pre-existing PostgreSQL-live skip |
| New foundation gates | 3/3 passed, no skips; final focused rerun after metadata review |
| Inventory/Platform | 53/53, including Identity 5, RBAC 2, Events 4, Jobs 9, Storage 6, Observability 16 and dependency architecture 4 |
| Playback / generic distributed workers | 99/99, including mTLS WorkerAgent process, coordinator restart/fencing and transfer tests |
| Dedicated FFprobe regression | 16/16 |
| Windows generic OOP Job Object | 1/1 with HISPANIA_TEST_WINDOWS_JOB_OBJECT=1 |
| Web / Modern / Classic | Both builds passed; 74/74 Web tests, including Admin/auth parity |
| Inherited closure/concurrency gate | 210/210 passed: 4 process/architecture, 6 safe-error, 200 stress checks across 20 repetitions; no skips |
| git diff --check | Passed for staged Phase 14 changes and remaining external diff |

Counts overlap between full suites and focused gates; do not sum them as unique tests. Logs/TRX are under ignored `.artifacts/phase14/` and existing TestResults directories.

The first full run exposed an existing FFprobe test availability helper deadlock: redirected stderr was not drained and ExitCode was read after an unchecked timeout. The test-only correction drains stderr, kills/waits on timeout and explicitly fails instead of silently skipping. The focused 16-test suite and subsequent full 545-pass run verify the fix. No Media/ERP production code changed. The shared tests/unit folder also required excluding the Runtime-only versioning test from the Domain test project, preserving its existing references.

## Documentation and limits

VERSIONING.md, COMPATIBILITY-POLICY.md, SUPPORT-MATRIX.md, PLATFORM-1.0-SCOPE.md, VERSION-MAP.md, the generated catalog, compatibility scaffold and ADR-0039 are delivered. ARCHITECTURE.md, MODULE_SYSTEM.md and PROGRESS-LOG.md link the new foundation; ADR-0027 terminology distinguishes protocol generations from stable contracts.

The scanner is a bounded source inventory, not a complete C# parser. The reflection baseline records current selected signatures/nullability/defaults/constraints, not old-binary loading, serialization, behavior or every custom attribute. Compiler-generated record signatures may require review on SDK upgrades. Protocol/package-format literals remain in existing code with current generation consistency checks; there is no new range negotiation or schema migration.

Linux execution and live PostgreSQL are not repeated locally: no configured PostgreSQL test connection/server was available. The pre-existing live-test skip is retained, with the required Linux CI live gate unchanged. ARM64 remains unvalidated. N plus N-1 is a selected-surface target, not a global support guarantee. Phase 14 is ready for CI: all available local gates passed. Only future CI can validate this branch on Linux/live PostgreSQL.
