# Versioning

HISPANIA 0.7.0 is the documentation baseline for the integrated distribution, not a release publication. The authoritative distribution value is the root `version.json`. It deliberately does not set MSBuild `Version`, `AssemblyVersion`, npm versions, wire generations or manifest host bounds. Build/release tooling should consume that file when distribution artifacts are introduced. Assemblies retain their current build defaults; no independently published Platform/Module SDK release is established here.

| Axis | Current source/value | Meaning and consumers | Visibility / evolution |
| --- | --- | --- | --- |
| HISPANIA Distribution Version | `version.json`: 0.7.0 | Integrated, tested server/runtime/web distribution; operators | Public pre-1.0 SemVer; patch preserves declared commitments, minor may break with notes |
| Platform SDK Version | `contracts/platform/Hispania.Platform.Contracts.csproj`; no explicit release version | Cross-domain CLR abstractions consumed by suites | Public experimental; future independent SDK SemVer, no assembly-default maturity claim |
| Module SDK Version | `contracts/module-system/Hispania.ModuleSystem.Contracts.csproj`; no explicit release version | Module authors' CLR boundary | Public experimental; future SDK SemVer only with packaging and API baseline |
| Module Implementation Version | `ModuleDescriptor.Version`, `ModuleManifest.Version`, extension templates | Identifies implementation/package; lifecycle, dependencies and persisted experiment identity | Author-owned version; currently `System.Version`, not a complete SemVer parser (no prerelease/build labels). Future SemVer needs explicit parser/policy migration |
| Capability Contract Version | `ModuleCapability.Version`, manifest `Provides`/`Requires` ID@version | Resolver requests exact identity, such as planner@1.0 | Public experimental technical generation encoded as `System.Version`; no compatible-minor inference |
| .hispania Package Format Version | `ModuleManifest.PackageFormatVersion = 1`; installer/catalog/ModuleHost | Container, canonical signature and digest interpretation | Public experimental integer generation; incompatible format gets a new generation plus reader/rejection policy |
| Module Manifest Version | `ModuleManifest` | Metadata schema for discovery/install/activation | No separate field today; module `Version` is implementation identity, NOT schema. Evolve additive fields only after default/signature compatibility review; incompatible metadata requires a future explicit schema discriminant |
| ModuleHost IPC Protocol Version | `ModuleWorkerProtocolV1.Version = 1` | Local bounded framed JSON; supervisor and ModuleHost | Public experimental integer wire generation; negotiate supported overlap, fail closed without overlap |
| Worker Protocol Version | `DistributedWorkerAgent.ProtocolVersion`, `DistributedWorkerEndpoints.ProtocolVersion`, both 1 | Coordinator/WorkerAgent HTTP connect and execution envelopes | Public experimental generation; currently duplicated definitions guarded by tests; no distribution SemVer coupling |
| Remote Capability Version | `DistributedCapabilityV1.ContractVersion`, job request | Allowlisted capability operation/payload identity | Public experimental exact match; independent of worker transport generation |
| HTTP API Version | Server endpoint maps; worker `/internal/worker/v1`, many `/api/platform/*` unversioned | Route family selected by clients; worker routes require authenticated worker authority | Public experimental route generation; `/v1` does not declare stable 1.0 or version every DTO |
| DTO Contract Version | Server request/response records, Web `src/api.ts` and related clients | JSON field, enum, nullability and status semantics | Public experimental; mostly implicit coordinated distribution evolution; breaking DTO change needs notes/new route or negotiated coexistence |
| Event Envelope Version | `PlatformEventEnvelopeV1`, generic `EventEnvelope<TEvent>` | Durable delivery/trace metadata independent of fact data | Public experimental CLR generation; no independent serialized envelope discriminator today |
| Event Payload Version | `IPlatformEvent.Version`, codec `(eventType, version)`; `inventory.stock-changed@1` | Owner-defined serialized fact schema consumed by subscribers | Public experimental integer schema per EventType; register explicit old decoders/upcasters before retiring persisted payloads |
| Job Contract Version | `IPlatformJobServiceV1`, distributed request V1 | Durable work lifecycle and execution bridge | Public experimental CLR/wire generation; separate from payload |
| Job Payload/Handler Version | Registry `JobType`; `PlatformTestHashPayloadV1` / delay | Handler deserializes inline JSON | Experimental; JobType has no independent version field. V1 suffix does not provide payload negotiation. Breaking changes need new JobType/explicit migration; never reinterpret queued data silently |
| Storage Handle Format | `StorageObjectHandle`, filesystem parser | Opaque `storage://provider/id`; legacy filesystem hex accepted | Public experimental data format, no SemVer; preserve legacy readers or explicit migration; consumer must not parse paths |
| Persistence Schema Version | Owner SQLite migration routines, EF migration IDs/history, experiment `SchemaVersion=1` | Store readers and migration owners | Internal schema generations/ordered migrations, not product maturity; populated upgrade/rollback policy per store |
| Web Artifact Version | `apps/web/package.json` + lockfile `@hispania/web` 0.1.0, private=true | npm build identity; Modern/Classic outputs | Private artifact metadata, no evidence of independent npm publication; retain 0.1.0. Distribution shipping identity comes from version.json, HTTP DTO identity separate |
| Observability Instrument Version | `PlatformTracingV1.Source`, application `Meter`, 1.0.0 | Instrumentation scope identity for listeners/exporters | Public experimental instrumentation schema identity; names/units/meaning need compatibility review. 1.0.0 is not Platform maturity |
| Host package compatibility level | Server, WorkerAgent catalog constructor; ModuleHost bounds check: 1.0.0 | Manifest `MinimumHostVersion`/`MaximumHostVersion` acceptance | Legacy compatibility baseline, not distribution or formal negotiated generation. Retained for existing packages; changing it requires package acceptance fixtures |
| FFmpeg worker protocol | `FfmpegWorkerProtocolV1`, response ProtocolVersion=1 | Dedicated subprocess JSON adapter | Media-owned experimental wire generation, separate from generic workers; unchanged |

All values above require compatibility decisions appropriate to their consumers, including internal persisted state. Public CLR visibility alone is not a stable promise. V1/V2/Version1/Version2 declarations are enumerated in the [catalog](compatibility/CONTRACT-CATALOG.md); the machine catalog records their interpretation and source. A technical V2 is never sufficient evidence for SemVer 2.0.

```text
HISPANIA distribution 0.7.0
├── Platform / Module SDK: no independent released SemVer yet
├── package format generation 1 / module IPC generation 1
├── worker wire generation 1 / exact capability version
├── Events: envelope generation != EventType payload schema
├── Jobs: service generation != JobType payload identity
└── Persistence: schema/migration sequence per owner
```

The host's 1.0.0 was hardcoded rather than sourced from distribution metadata. Source usage establishes only package bounds semantics; there is no evidence it implements SDK binary compatibility. This phase documents it instead of renaming a public parameter or altering package acceptance. Observability's 1.0.0 likewise stays unchanged for existing listeners. Demo module/package 1.0.0/2.0.0/3.0.0 values identify historical test implementations, not product maturity.

No version derives from phase count, test count, code size, complexity or a V2 type name. See [policy](COMPATIBILITY-POLICY.md), [logical ownership map](VERSION-MAP.md) and ADR-0039.
