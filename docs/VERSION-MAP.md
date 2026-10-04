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
