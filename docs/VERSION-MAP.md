# Logical version map

These are ownership/release planning units, not new NuGet packages or physical version changes. Current distribution baseline is 0.7.0; only `version.json` records it. Provisional readiness labels from the maturity audit are not independently published release versions. Actual release numbers must be chosen when a unit ships and its promise is known.

| Logical unit / owner | Existing implementation boundary | Planning baseline | Decision |
| --- | --- | --- | --- |
| Integrated Platform distribution / Platform | Server composition, runtime, Platform adapters and Web | 0.7.0 | Actual documentation identity, root version.json |
| Platform SDK / Platform | contracts/platform | 0.x, unissued | One coherent SDK release unit; no package per abstraction |
| Module SDK + Runtime / extensibility | contracts/module-system, core/runtime, ModuleHost, tooling | provisional 0.8.0 | Experimental readiness target, not assembly/API generation |
| Worker stack / distributed runtime | Generic WorkerAgent, coordinator, owned SQLite state, contracts | provisional 0.8.0 | Wire generation separate; fencing candidate only |
| Identity + Authorization / security | Platform contracts, identity/auth applications, SQLite | provisional 0.9.0 candidate | Selected service boundaries are candidates; no subsystem 1.0 |
| Events / Platform delivery | Platform Events contracts, runtime dispatcher, SQLite | provisional 0.8.0 | Envelope and per-owner payload versions independent |
| Jobs / Platform work | Jobs application/contracts/store/remote bridge | provisional 0.8.0 | JobType/payload evolution unresolved; test handlers not stable |
| Assets + Storage / Platform content | Asset metadata, storage registry/filesystem/SQLite | provisional 0.8.0 | Asset identity candidate, provider contract experimental |
| Audit + Observability / Platform diagnostics | Audit adapters/store, metrics/health/tracing | provisional 0.8.0 | Scope instrument version 1.0.0 retained, independent of release |
| Web / client shell | apps/web Modern/Classic + server HTTP projections | provisional 0.7.0 | Private npm 0.1.0 retained; shared distribution shipping, not DTO version |
| Media / Media owners | domain/library/metadata/playback and adapters | no aggregate release selected | Direct Play candidate does not promote whole suite |
| Inventory / ERP owner | contracts/application/SQLite | experimental, no release selected | Domain proof, not ERP product 1.0 |

SDKs may become independent SemVer units only if actually distributed to consumers. Until then build defaults are implementation metadata; synchronizing every csproj would manufacture promises. Signed package implementation identities and host compatibility ranges must not be regenerated from distribution SemVer. The empty Kernel is not a useful release unit. See [axis inventory](VERSIONING.md) and [publication scope](PLATFORM-1.0-SCOPE.md).

## Phase 15 historical evidence

[Phase 15](PHASE-15-COMPATIBILITY-BASELINES.md) adds technical Compatibility Baseline 0.7 from Phase 14 SHA 78f291fdacf9442c6e2d8e23094dc0e309828693: selected API, old binary/source consumer, signed package, serialization and generation-1 protocol evidence. Distribution remains 0.7.0; no generation is renumbered.


## Phase 16 — historical persisted data

Owner-local SQLite schemas now track version 1 (`0001_baseline07_adoption`) and minimum supported known legacy shape 0. Media keeps its EF SQLite five-step and PostgreSQL one-step lineages; playback JSON remains schema 1. Unknown/future histories fail before writes. Upgrades are forward-only and per-store transactional; startup blocks readiness on failure. Old executables without guards are not safe rollback targets. Data schema versions remain independent of distribution/API/wire versions; no contract gains Public Stable status.

The independent 15-artifact populated corpus and mandatory Linux PostgreSQL gate are documented in [Phase 16](PHASE-16-HISTORICAL-DATA-UPGRADES.md) and [ADR-0041](adr/ADR-0041-schema-evolution-and-historical-upgrades.md). Phase 15's 27 artifacts and hashes remain frozen. Local implementation is prepared for CI; Phase 16 closure awaits live PostgreSQL and both OS jobs. Backup/restore remains outside this phase.

| Independent persistence axis | Owner | Current schema / supported predecessor |
|---|---|---|
| Identity | Platform.Identity | 1 / known unversioned 0.7 shape |
| Authorization | Platform.Authorization | 1 / known unversioned 0.7 shape |
| Events | Platform.Events | 1 / known unversioned 0.7 shape |
| Jobs | Platform.Jobs | 1 / known unversioned 0.7 shape |
| Asset metadata | Platform.Assets | 1 / known unversioned 0.7 shape |
| Audit | Platform.Audit | 1 / known unversioned 0.7 shape |
| Inventory | Inventory.Persistence | 1 / known unversioned 0.7 shape |
| Coordinator | Platform.Coordinator | 1 / known unversioned 0.7 shape |
| Media SQLite | Media.Persistence.SQLite | EF 20260923000500_person_metadata / contiguous known prefix |
| Media PostgreSQL | Media.Persistence.PostgreSQL | EF 20260926104649_InitialPostgreSql / empty or same lineage |
| Playback experiments | Media.Playback.Experiments | JSON 1 / schema 1 |
| Storage bytes | Platform.Storage.Filesystem | opaque and legacy layouts; no database ledger |

## Phase 17 — backup format axis

Backup Format Generation 1 belongs to offline host operations and is independent of distribution 0.7.0 and component schemas. There are now 23 version axes and zero Public Stable declarations. See [Phase 17](PHASE-17-BACKUP-RESTORE.md) for managed-layout support and pending acceptance.

## Phase 18 production deployment

Configuration Schema Generation 1 is the independent 24th axis. Distribution remains 0.7.0, Backup Format Generation remains 1 and Public Stable remains 0. Frozen compatibility corpus identities are unchanged; configuration.schema is Experimental. See [Phase 18](PHASE-18-PRODUCTION-DEPLOYMENT.md) and [ADR-0043](adr/ADR-0043-configuration-secrets-and-production-deployment.md).
