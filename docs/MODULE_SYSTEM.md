# Module System

> **Current validation:** Phase 12 is integrated on `main`. GitHub Actions [Validation #36625775324](https://github.com/joaquimcastilla/hispania/actions/runs/36625775324) passed on Linux and Windows at `2e16b039d0fb8a3223366f7af478353b68886af1`. Historical phase notes below retain the test state at the time they were written; later Phase 11/12 coverage supersedes their pending-validation statements. See [ADR-0030](adr/ADR-0030-platform-generalization-and-domain-suites.md) for Phase 12 scope and results. Passing configured process-limit tests does not claim hostile-code sandboxing or prove every limit-exhaustion case.

## Platform contracts and events

Cross-domain module/runtime mechanisms belong in `Hispania.Platform.Contracts` or `Hispania.ModuleSystem.Contracts`; domain-specific contracts stay with their suite. The platform assembly has no Media project reference. `LocalPlatformEventBus` provides deterministic sequential in-process dispatch over versioned event contracts. Publishers receive per-handler failures, and remaining handlers still run. This transport is not durable and does not retry; a future transport can implement `IPlatformEventPublisher` without changing event consumers.

The durable V1 contract is separate: `IDurablePlatformEventBusV1` stores explicit event type/version and bounded UTF-8 JSON with independent subscriber delivery rows. Stable subscriber IDs and registered codecs prevent CLR type names in persisted payloads from selecting runtime types. SQLite is Platform-owned and separate from domain databases. At-least-once delivery, bounded retries/dead-letter, lease recovery and replay are documented in [ADR-0033](adr/ADR-0033-platform-durable-event-bus.md). One local dispatcher per store is supported; no distributed coordination or global ordering guarantee is made.

### Domain suite ownership

Media owns Library, Metadata, Search/catalog, Playback, Streaming, and Transcoding. Inventory owns its Item/Warehouse/Location/Stock/Movement records and its SQLite schema. Inventory code references Platform contracts and its own persistence; it does not reference Media projects or tables. The server is the composition root and may reference both suites' public APIs and adapters. `Hispania.Platform.slnf` builds the Platform projects, generic WorkerAgent, and Platform persistence without Media, Playback, FFmpeg, or Inventory implementation.

Inventory is composed into the server directly through DI, not discovered as a dynamically loadable `IModule`. Inventory API reads require `inventory.read`; mutations require `inventory.write`. The legacy `Modules:AdminToken` bearer is retained only for these mutation routes: `POST /api/inventory/items`, `POST /api/inventory/warehouses`, `POST /api/inventory/warehouses/{warehouseId}/locations`, `POST /api/inventory/receive`, `POST /api/inventory/items/{itemId}/assets`, `DELETE /api/inventory/items/{itemId}/assets/{assetId}`, `POST /api/inventory/issue`, and `POST /api/inventory/transfer`. It does not authorize Inventory reads or Media endpoints. Platform Identity owns persistent users, credentials, and sessions; Platform Authorization resolves current grants on each request and drives protected Web routes and navigation. Session HTTP auth uses the explicit `Session` scheme; the temporary admin bearer is a route-specific compatibility path. WorkerAgent hosts generic capabilities without an FFmpeg project reference; the Media FFmpeg provider is loaded only when configured by the operator.

## Goal

Permitir que componentes relevantes puedan ser sustituidos sin modificar consumidores.

## Module Types

### Core Module

Parte esencial del sistema.

Debe ser mínimo.

### Native Module

Código de confianza ejecutado con acceso nativo.

Candidatos:

- FFmpeg integration;
- database engines;
- hardware acceleration;
- high-performance scanning.

### Sandboxed Plugin

Código de terceros ejecutado mediante WebAssembly/WASI.

Debe solicitar capacidades explícitas.

## Contract Versioning

Los plugins no deben depender principalmente de la versión global del servidor.

Preferido:

```text
provides:
  metadata-provider@2

requires:
  network@1
  configuration@1
```

En lugar de:

```text
requires server 5.3.7
```

## Manifest

Ejemplo conceptual:

```yaml
id: org.example.tmdb
name: TMDB Metadata
version: 2.1.0

provides:
  - metadata-provider@2

requires:
  - network@1
  - configuration@1

permissions:
  network:
    - api.themoviedb.org

entrypoint:
  wasm: plugin.wasm
```

## Capabilities

Posibles capacidades:

- network;
- filesystem-read;
- filesystem-write;
- media-read;
- media-write;
- gpu;
- process-exec;
- plugin-storage;
- notifications;
- secrets;
- config.

Por defecto, un sandboxed plugin no obtiene acceso que no haya solicitado.

## Runtime

Flujo:

```text
plugins/
   |
manifest discovery
   |
validation
   |
dependency resolution
   |
permission evaluation
   |
load
   |
health check
```

## Compatibility Tests

### Distributed capability execution (Phase 11)

The public contracts define versioned worker inventory, job envelopes, leases, heartbeats and transfer broker semantics. The coordinator uses SQLite transactions and a single-instance lock in server mode; in-memory mode remains useful for tests. Node certificate pins and per-capability allowlists are set by an administrator. Worker inventory alone never authorizes a capability. The HTTPS/mTLS worker routes are distinct from OutOfProcess local IPC and authenticated admin routes.

The generic agent executes trusted extension contracts through `ModuleExtensionCatalog` and its existing OutOfProcess host. Optional streaming capabilities plug into the agent's `IDistributedCapabilityHandlerV1` composition point. The FFmpeg handler is one such capability plugin; the agent and lease protocol remain generic. A short-lived source grant is fetched only after mTLS identity and the exact job/node/generation lease are checked. Read blocks are at most 4 MiB; one grant may serve at most 3x the declared source length. The grant token is transient and absent from persisted work JSON. Result data is streamed to a private `.partial` file, bounded by an explicit byte limit, SHA-256 validated, then atomically assigned an opaque handle. Published results expire after 24 hours; a cleanup service removes expired artifacts. No local filesystem path leaves the coordinator.

The broker rechecks the live lease after the full upload and immediately before publication. Coordinator completion is still a separate SQLite transaction: if that transaction fences the result, the endpoint removes the published file. A process crash in the small gap after atomic rename and before the transaction can leave an unreferenced result until its 24-hour cleanup; the restarted coordinator does not expose that handle. On startup the server first acquires the exclusive SQLite coordinator lock, then removes abandoned upload partials. A worker holds an exclusive lock on its private FFmpeg cache and cleans interrupted source/output files only after it owns that cache. This avoids cleanup racing another active coordinator/agent that follows the single-owner deployment rule.

Assignment requires exact capability/version match, enabled/authorized/live worker, free concurrency and a retry-safe operation with no external side effects. Generation fencing rejects completions after a lease expires, is cancelled, or is reassigned. Interrupted safe work can be redelivered; this does not provide exactly-once execution. Cancellation is sent to active agents through heartbeat responses, which cancel the local capability token and process tree.

Playback configuration is `DistributedWorkers:Transcoding:Mode`: `Local` (default), `RemotePreferred` (waits up to 10 seconds for a lease, then falls back to the local worker on remote failure) or `RemoteOnly` (return unavailable on remote failure). The remote handler invokes the existing FFmpeg worker, which uses software `libx264` or stream copy and does not assume GPU presence. It transfers source data without a shared library, verifies the uploaded SHA-256, checks MP4 container signature, stores it through the transcoder cache and returns the usual opaque Playback handle. Process integration tests exercise real FFmpeg/ffprobe, interruption, fallback and recovery. The distributed worker and mTLS paths also passed Linux/Windows CI and physical two-host validation; see ADR-0029 and the Phase 12 closure evidence. These checks do not imply exactly-once execution or hostile-code isolation.

Broker tests additionally cancel a result upload deterministically, verify partial-file cleanup, reject a result whose lease expired before publication, and delete an artifact if final coordinator completion loses the lease. The process test encodes real MPEG-4 input with FFmpeg, cancels the remote job while its result upload is blocked, and verifies cancellation/cleanup; a separate case kills the agent during source transfer. SQLite restart tests cover retry-safe work in transfer/encoding/publication states, `Uncertain` work that must not repeat, and rejection of a second coordinator. Killing the worker specifically while FFmpeg is encoding remains untested. Remote operation tolerates selected network failures through lease expiry, fencing and explicit fallback; it does not make network outcomes exactly once. OutOfProcess and FFmpeg worker isolation contain process failures and apply configured resource limits, but do not sandbox malicious code. mTLS/pinning authenticates the provisioned machines, not their integrity if compromised.

Cada contrato público debe poder disponer de una Compatibility Test Suite.

Ejemplo:

```text
Metadata Provider v2

[OK] search
[OK] external identifiers
[OK] artwork
[OK] cancellation
[OK] invalid ids
[OK] unicode
[OK] timeout
[OK] concurrency
```

## Swapping

### Runtime implementation available now

The module system advertises versioned capabilities in `ModuleDescriptor.Provides` and `Requires`. A module implements the public contract and `IModule`, then the host registers it as an `IModule`. Resolve by stable capability ID, exact contract version, and configured implementation ID:

```csharp
services.AddSingleton<IModule>(sp => sp.GetRequiredService<DirectPlayPlanner>());
services.AddSingleton<IPlaybackPlanner>(sp => sp.GetRequiredService<ICapabilityResolver>()
    .Resolve<IPlaybackPlanner>("hispania.playback.planner", new Version(1, 0), configuredModuleId));
```

Configuration currently selects the playback planner with `Modules:PlaybackPlanner`. If omitted, the server explicitly defaults to `hispania.playback.direct-planner`. If the ID is absent, advertises a different version, or does not implement the requested CLR contract, startup fails with a typed runtime exception. Multiple compatible providers without an ID are reported as ambiguous.

To add an alternative: create an independent implementation project; reference only its public contract, domain types that contract permits, and `Hispania.ModuleSystem.Contracts`; implement the contract and module lifecycle; advertise the same capability/version; register it at the host composition root; select its ID in configuration; add contract compatibility tests and dependency-graph tests. Do not reference another provider's project or schema. The host currently statically links native modules, so install/uninstall/update still requires deployment of a new host/module set and process restart.

The playback planner is the first functional demonstration. Its contracts are synchronous and pure read operations. `ShadowCapability<TInput,TResult>` invokes the active implementation first, runs a secondary implementation for comparison, returns only the active result, reports the secondary result/failure through an observer, and contains a shadow exception. It does not sandbox or suppress writes. Only use it with implementations that have no write or external side effects. Do not wrap metadata refresh, filesystem reconciliation, or transcoding in shadow mode.

The four rollout modes have these current guarantees:

1. **Manual substitution:** implemented per capability; process restart required.
2. **Shadow:** generic safe-result wrapper implemented; purity is a contract/registration responsibility, not technically enforced.
3. **A/B:** generic manual/traffic-split routing primitives and stable assignment are implemented in the runtime foundation. Manual, A/B and Shadow experiments are integrated into Playback Planner with administration, metrics and recovery; this is not a universal feature of every domain.
4. **Dynamic substitution:** not implemented. The current DI graph and module lifecycle are process scoped; selection changes require restart.

The first shadow-capable functional operation is `IPlaybackPlanner.Plan` through `ShadowPlaybackPlanner` in Playback.Application, not in the kernel. Contract tests prove that the active planner result wins and the comparison planner only receives a read request. Native implementations can still contain hidden effects, so build policy must allow this only for reviewed read-only modules. There is no effect monitor or filesystem/network sandbox in the native runtime.

FFprobe consumes the Media-owned `IMediaSourceContentAccessV1` contract and never resolves local file paths. The active local provider owns locator validation, root containment and reparse-point rejection. Future NAS/HTTP sources can implement this contract without a FFprobe project reference.

Playback still selects the transcoder through the capability resolver at startup. Its response now contains an opaque `TranscodedContentHandle` rather than a path; `ITranscodedContentStore` delivers bytes/ranges. The worker process uses `hispania.transcoding.ffmpeg.worker@1`; source bytes arrive via stdin, while a bounded JSON request/response carries the version and opaque cache key. The host applies a timeout, process-tree cancellation and one retry from a reopened source. The worker is per job, not a daemon.

Three guarantees must not be conflated. Logical isolation comes from public contracts and ownership. Process isolation runs the worker and FFmpeg outside the HTTP host. Resource controls are configured via `Playback:Worker`: Windows Job Objects set memory/CPU/time ceilings and kill-on-close; Linux `prlimit` sets address-space, CPU-time and file-size ceilings plus a .NET GC heap cap. Per-job output limits and best-effort cache pruning do not quota the aggregate volume. These controls are not a security sandbox: worker identity/environment/cache are shared, and no dedicated ACL or sandbox profile is installed. Linux runtime enforcement still needs deployment-kernel validation.

Worker V1 forwards a correlation ID from the host activity. Metrics include attempts, failures, retries, cancellations, active jobs, duration, output bytes and observable process CPU/memory. Server `/healthz` is independent of optional transcoding capability health, available at `/internal/capabilities/transcoding/health`. Recovery integration tests cover worker crash, timeout, caller cancellation and failed publication followed by retry/recovery; the real FFmpeg integration covers output and cache reuse.

`Hispania.Library.Persistence.EFCore` does not reference a concrete database provider or provider exceptions. SQLite remains default and retains its existing migrations/schema; PostgreSQL is a distinct optional adapter selected by `Persistence:Provider=postgresql`, with native UUID mapping, its own initial migration and conflict classifier. Both expose the Library/Metadata persistence contracts. CI provisions PostgreSQL 16 and requires `HISPANIA_POSTGRES_TEST_CONNECTION`; integration compares migration from empty schema, reads/writes, constraints, rollback, concurrency and projected data with SQLite. PostgreSQL-live passed 1/1 on Linux in Validation #36625775324; this is Media catalog persistence and does not imply PostgreSQL support for distributed coordination.

The server also selects the compatible transcoder through `ICompatibleMediaTranscoderV1` at `hispania.playback.compatible-transcoder@1.0`. FFmpeg implementation lives in a separate native module project. Replacement requires host registration and restart; it does not require editing the HTTP endpoint or Playback application.

## Independent web builds

The web package creates two standalone outputs:

```powershell
npm run build:modern
npm run build:classic
# or npm run build for both
```

Outputs are `apps/web/dist/modern` and `apps/web/dist/classic`; each has its own HTML entry, JS bundle and theme CSS. Choose one in server configuration with `Web:Build` (`modern` or `classic`) before process startup. Both variants share the HISPANIA application and public HTTP contracts. Selection requires a server restart and does not distribute backend traffic between variants.

These are separately built presentations over the same React screens, with separate entrypoints and theme systems. They are not two separately developed applications yet; navigation, playback UI and API client are still shared.

There is no general cross-process protocol for all modules, manifest discovery from disk, dependency resolution across optional modules, unload, hot reload, resource quota or third-party extension sandbox. Only the transcoding worker has a versioned process protocol. Native modules are trusted code with process-wide filesystem/network/process privileges. For untrusted extensions the planned WASM boundary remains future work.

## Installable trusted .NET extensions (Phase 7)

The version 1 `.hispania` package is a ZIP containing a root `module.json` and all published payload assemblies/dependencies. The manifest includes `packageFormatVersion`, `entryAssembly`, `entryType`, host compatibility bounds, versioned `implements`/`provides`/`requires`, dependencies, host-shared contract assemblies and a SHA-256 `files` map covering every payload file. Hashes detect corruption; they are not publisher signatures.

Build and package the greeting implementation independently from the server:

```powershell
dotnet publish modules/extension-demo/Hispania.ExtensionDemo.csproj -c Release -o .artifacts/greeting-v1
./tools/package-extension.ps1 -PublishDirectory .artifacts/greeting-v1 -ManifestTemplate extensions/experimental-greeting/manifest.template.json -OutputPath .artifacts/greeting-1.0.0.hispania
```

The v2 acceptance package uses `manifest-v2.template.json` and version `2.0.0`. `package-extension.ps1` refuses to overwrite an existing package and includes every publish file in the digest map. The server project references `Hispania.ExtensionDemo.Contracts` for its consumer API, but has no project reference to `Hispania.ExtensionDemo` implementation.

Install with the authenticated local admin API (raw package bytes, no URL fetch):

```powershell
curl.exe --fail-with-body -X POST `
  -H "Authorization: Bearer $env:HISPANIA_MODULE_ADMIN_TOKEN" `
  --data-binary "@.artifacts/greeting-1.0.0.hispania" `
  http://localhost:5000/internal/admin/modules/extensions/packages
```

Set `Modules:AdminToken` through protected operator configuration; source-controlled appsettings contains no secret. Package installation is inert and does not require or grant execution trust. The package installer bounds sizes and checks paths, ID/version, host compatibility, dependencies, required contract availability, exact payload list, SHA-256 digests and managed entry assembly metadata before moving files to `extensions/<id>/<version>`. It never loads package code. The operator separately authorizes IDs in `Modules:TrustedExtensionIds`; activation separately loads and executes the module. Discover with `GET /internal/admin/modules/extensions`, then `POST /internal/admin/modules/extensions/<id>/activate` and consume the sample public contract at `GET /internal/admin/modules/extensions/<id>/greeting`.

Lifecycle operations are distinct:

- **Install:** authenticated local package upload, no execution.
- **Enable/authorize:** operator adds the ID to the host allowlist; this is a host trust decision, not a self-service endpoint.
- **Activate:** load the declared entrypoint into a collectible `AssemblyLoadContext` and start it under `ModuleRuntime` supervision.
- **Deactivate:** stop accepting leased calls, wait for in-flight calls to drain, then stop the module. The assembly remains loaded.
- **Unload:** unregister a stopped module, clear runtime references, request ALC unload and report whether collection completed.
- **Update:** install a new version beside the old one, start and health-check the candidate while the active generation remains available, drain old calls, then switch. A failed candidate start/health leaves the old generation active. Use `/internal/admin/modules/extensions/<id>/update/<version>` to switch to an installed version, including rollback.
- **Uninstall:** remove one version directory only after it is unloaded.

The HTTP acceptance test installs packages produced by a separate `dotnet publish`, activates and invokes the public contract, verifies the v2 startup-failure rollback, switches successfully to v2, blocks calls after deactivation, and checks context collection. Static host factories remain supported. Public contract assemblies bind to the host default context by exact identity. `AssemblyLoadContext` provides identity separation/unloadability, not a sandbox; trusted extensions run with the server process's privileges. Effective isolation requires a separate process and versioned IPC.

### Phase 8: package trust and independent execution

Installation, publisher authorization and execution remain separate. The installer validates paths, sizes, manifests and SHA-256 payload digests and stores inert packages. SHA-256 establishes integrity relative to the manifest, not publisher identity. `ModulePackageSignature` signs canonical manifest metadata and payload digests with RSA-PSS-SHA256. Activation applies the configured publisher trust/revocation policy as well as the module ID allowlist. Trust administration endpoints currently modify in-memory policy; deployments requiring durable trust must configure protected `Modules:TrustedPublisherKeys` and `Modules:RevokedPublisherIds` settings.

Runtime is selected explicitly by the package manifest. `dotnet-inprocess-trusted` loads an allowlisted, publisher-authorized .NET extension into collectible `AssemblyLoadContext`; it shares server privileges and is not a sandbox. `dotnet-outofprocess` launches `apps/module-host` and adapts its capability to the same public interface through a proxy. Existing modules are not automatically migrated.

Module protocol V1 uses a private current-user local named pipe, a per-launch random token, identity/protocol negotiation, capability inventory, correlated request/response/error frames, cancellation, health and graceful stop. Length-prefixed JSON frames are bounded to 1 MiB by default (16 MiB hard maximum); the supervisor also bounds startup, request duration and concurrency. External process failure degrades that module only. Restart count/window/backoff are configurable and bounded. The V1 adapter supports JSON-compatible operations with at most one non-cancellation argument; streaming and richer schema evolution remain future work.

`OutOfProcessResourceLimits` configures managed heap, request duration and concurrency. Windows Job Objects enforce process/job memory, CPU, time and descendant-count ceilings when attached; a nested job may prevent attachment, and required enforcement then fails activation. Linux applies `prlimit` CPU-time limits and the .NET GC heap hard cap. `RLIMIT_AS` is opt-in because it prevents CoreCLR from reserving its address space; it is not a supported hard memory quota for generic .NET workers. A cgroup/systemd memory limit is still needed for OS-enforced Linux memory ceilings. Process separation contains crashes; it does **not** create a security sandbox. The child shares the server's OS identity and may share its files, environment secrets and network privileges. Effective hostile-code isolation requires a separately restricted OS identity, filesystem, secrets, network and handles.

GitHub Actions run [36252392250](https://github.com/joaquimcastilla/hispania/actions/runs/36252392250), SHA `e3c2103`, passed on Ubuntu and Windows. Ubuntu ran PostgreSQL 16 live migrations/constraints, real FFmpeg recovery/media integration (6 tests), Linux CPU `prlimit`, and the full cross-platform .NET test set including the OOP protocol/recovery test. Windows ran real FFmpeg integration (6 tests), OOP recovery with mandatory Job Object attachment, and DevLauncher tests. The local Windows full suite also passed; see the dated `PROGRESS-LOG.md` entry. These runs do not prove a hard Linux memory quota: generic .NET workers use the GC heap cap, while `RLIMIT_AS` is opt-in and unsuitable for CoreCLR. Neither platform provides a hostile-code sandbox.

#### Platform and execution guarantees (Phase 9 audit)

| Platform / mode | Failure containment | Resource controls implemented | Evidence in this working tree | Limits / pending |
|---|---|---|---|---|
| Windows / InProcess | ALC unload boundary; module exception handling only | None separate from server | Full .NET suite on Windows | Same identity/address space; not a sandbox |
| Linux / InProcess | ALC unload boundary; module exception handling only | None separate from server | Linux CI is configured, but current local changes have no run | Same identity/address space; not a sandbox |
| Windows / generic OutOfProcess | Worker process, health and bounded restart policy; Job Object kills descendants when attached | Job memory, CPU-time/rate, active process count; host request timeout/concurrency | Local Windows OOP recovery test passed with mandatory Job Object attachment enabled | Current-user identity remains shared; disk volume not quota-limited |
| Linux / generic OutOfProcess | Worker process, health and bounded restart policy; process tree termination | `prlimit` CPU-time ceiling, .NET GC heap hard cap, host request timeout/concurrency; `RLIMIT_AS` opt-in because it prevents CoreCLR from reserving its runtime address space | Ubuntu run `36252392250`: full cross-platform suite including OOP recovery passed; CPU `prlimit` path exercised | Heap cap is managed-runtime enforcement, not an OS process-memory quota; no cgroup aggregate quota, process-count or module filesystem quota; shared user identity |
| Windows / FFmpeg worker | Per-job worker; kill-on-job-close when Job Object attaches | Job memory, CPU-time/rate, active process count, host job duration/output cap/cache pruning | Six real worker recovery/media tests passed locally on Windows after process-count limits were added | Cache pruning is best effort; no disk quota for cache volume |
| Linux / FFmpeg worker | Per-job worker; process tree termination on timeout/cancellation | `prlimit` address-space/CPU/file-size, job duration, output cap, concurrency, best-effort cache pruning | Ubuntu run `36252392250`: real FFmpeg recovery/media 6/6 passed | `RLIMIT_FSIZE` is a per-file cap, not aggregate disk quota; no cgroup memory/CPU or process-count quota |

No configuration in this matrix makes extension code safe against a malicious publisher. On both operating systems a child shares the server account and any accessible paths, secrets and network permissions unless deployment adds OS-level separation. FFmpeg output caps and cache pruning do not reserve/enforce a hard quota on the whole backing volume.

The Windows results above prove that the Job Object accepted and attached the configured policy while the worker completed real jobs. They do not yet deliberately exceed each memory/CPU/process boundary to prove its termination behavior. Equivalent Linux enforcement has not run. Those limit-exhaustion and long-duration leak checks belong to the pending resistance suite.

The generic supervisor emits `hispania.modules.outofprocess.starts`, `.restarts`, `.failures`, `.requests` and `.request.duration` metrics tagged only with module ID/version; launch tokens, paths containing credentials and key material are never metric dimensions. The runtime lifecycle transition metric remains separate. FFmpeg exports attempt/failure/retry/cancellation, active-job, duration, working-set, CPU-time and output-size instruments. The current administrative inventory exposes the installed/active version and lifecycle state; detailed transition reason retention is not a durable audit log.

Recovery boundary: installed version directories are retained and activation re-verifies the manifest signature and payload hashes, so an operator can activate a retained package after a host restart. Extensions are not automatically reactivated on server boot; activation remains an explicit admin operation. Updating does not replay leased business operations. Interrupted install staging is ignored by discovery but is not yet reaped automatically, and interrupted uninstall may leave an invalid partial package directory for operator cleanup. Durable transaction journals, startup reconciliation and a real server-process restart acceptance test remain pending.

Test layers in this repository: protocol/signature/policy and fake-worker checks are unit/security tests; SQLite/HTTP/real FFmpeg/PostgreSQL schema exercises are integration tests; simulated crash/retry/timeout/cancellation paths are recovery tests; the planner suite now also holds 16 concurrent old-generation calls across a replacement and verifies that later calls use only the new generation. Broad fuzzing, long-running load, restart-limit exhaustion under repeated process crashes, crash injection during disk transactions and server process restart remain resistance/consistency gaps. Configured CI test names are not evidence until the current SHA completes the workflow.

#### Developing an external extension

1. Implement only a public contract from a contracts assembly; do not reference another module's implementation assembly. OOP operations must use JSON-compatible request/response values and no more than one operation parameter (a `CancellationToken` is supported separately).
2. Publish the extension independently. Create a manifest with `runtime: dotnet-outofprocess`, its entry assembly/type, exact `provides` and `contractTypes` mappings, and compatible host/protocol metadata. Do not add the contract implementation assembly to the host's shared assembly list; it must load from the package.
3. Package the complete publish directory with `tools/package-extension.ps1`, a manifest template and a publisher ID/private signing key. Protect the private key outside the repository. Add the publisher public key and module ID to operator trust/allowlist configuration.
4. Install the `.hispania` archive through the authenticated local admin endpoint. Discover it, then explicitly activate or select its version. Installation itself never executes code.
5. Verify health, cancellation, timeout, crash recovery, update and rollback in an isolated host before deployment. Set `Modules:OutOfProcessResources` and `Modules:OutOfProcessRestarts` according to deployment policy. Require successful OS quota attachment where quotas are part of the operational requirement.

## Phase 5 supervision and bounded hot swap

The integrated `ModuleRuntime` owns lifecycle state for statically registered modules: `Registered → Starting → Running`, with `Degraded`, `Draining`, `Stopping`, `Stopped` and `Failed` transitions. `ModuleDescriptor` declares implementation version, provided and required contract/version pairs, and explicit module dependencies with a minimum version. Runtime startup checks dependency cycles, missing/ambiguous capability providers and versions. `IModuleHealthCheck` is optional; absent checks are treated as healthy. Transitions increment revisions, update timestamps, emit `StateChanged` and the `hispania.modules.transitions` metric. Module health is reported separately from server liveness.

`PlaybackPlannerHotSwapService` permits replacement only when both implementations advertise `hispania.playback.planner@1.0` as dynamically replaceable and implement the public planner and health contracts. It starts and checks the target before atomically changing the router generation. A per-operation lease pins an existing call to its generation; the old generation accepts no new leases and is stopped only after active calls drain. Exceptions propagate once and are not retried. After an activated generation fails an operation, new calls wait while the service checks/restarts the last known generation, restores the route, drains and stops the failed module. Failed recovery surfaces as an explicit error and keeps calls blocked rather than silently sending them to an unhealthy implementation.

`POST /internal/admin/modules/playback-planner/{targetModuleId}` requests only this allowlisted capability replacement; `GET /internal/admin/modules` reports descriptors, dependencies, versions and lifecycle. Both require `Authorization: Bearer ...` using `Modules:AdminToken`; the endpoints return 503 when the secret is absent and 401 for invalid credentials. Keep the secret outside source-controlled settings. Replacement and rollback counters are exported through `Hispania.Server.ModuleHotSwap`.

The guarantee is registry substitution in one process. The runtime does not unload assemblies, replace files, or provide process/resource isolation for native modules. Registration of another statically composed implementation is not dynamic code loading. Hot swap of persistence and live schema changes remain unsupported; A/B traffic routing is implemented specifically for Playback Planner, not as universal domain behavior. PostgreSQL-live and configured Linux worker process paths have since passed in CI; individual resource ceilings are not all stress-tested for exhaustion.

### Contract ownership and absence

Each capability has an owner in its contract project. `ModuleDescriptor` reports provided and required versioned capability IDs; it does not transfer ownership of domain data. Consumers should resolve an optional provider at composition/startup and use a typed unavailable outcome when the feature is optional. The current resolver throws `CapabilityUnavailableException`, which the host can use for explicit fallback or omit the optional service. It does not silently substitute an arbitrary provider.

Persistence is not a message bus. Library catalog and metadata enrichment have separate application contracts and stores, even though they currently share an EFCore context and SQLite database file. SQLite migrations are owned by the SQLite adapter and have no automatic downgrade path.

Configuración conceptual:

```yaml
modules:
  metadata.primary: org.example.tmdb
  search.primary: core.search.sqlite
  transcoder.primary: core.transcoder.ffmpeg
```

Cambiar implementación no debe requerir recompilar consumidores.

### Fase 11: contratos de trabajos remotos

Los trabajos distribuidos llevan ID, versión exacta de capacidad, operación, payload JSON acotado, correlación y declaraciones explícitas de efectos/repetibilidad. El transporte de red es HTTPS con certificado cliente y pin SHA-256 configurado por el operador; la autorización de nodo y de cada capacidad vive en el coordinador. Los heartbeats renuevan leases y devuelven instrucciones de cancelación. Un lease vencido libera solo una operación segura; una operación potencialmente effectful pasa a `Uncertain`. Cada nueva asignación incrementa la generación y el coordinador descarta completados obsoletos.

El estado de coordinación se guarda transaccionalmente en SQLite con un lock de instancia única. Configura `DistributedWorkers:StatePath`; conserva también el archivo `.coordinator.lock` en un directorio protegido. El agente verifica pins SHA-256 del certificado del servidor y se integra con el catálogo genérico OutOfProcess; la API remota no reutiliza su framing IPC. La incorporación consiste actualmente en autorizar nodo, capacidades y fingerprint por la API administrativa autenticada antes de conectar el agente. La rotación sustituye el pin y obliga al agente a reconectar; la revocación bloquea la identidad. No hay emisión automática de certificados.

La prueba de proceso independiente ejecuta el módulo experimental de saludo sobre ese transporte y comprueba desconexión, expiración, reconexión y fencing. El test de Playback levanta una transcodificación real, entrega los medios por bloques, ejecuta FFmpeg en el proceso worker y valida la salida por ffprobe. También interrumpe el worker durante la transferencia y verifica que Playback cae a la ejecución local; no afirma cobertura de corte durante encoding o result upload. El contrato `IDistributedMediaTransferBrokerV1` ofrece grants efímeros por `jobId`, bloques seekable limitados a 4 MiB, token no persistido y publicación de resultados con longitud acotada y SHA-256. Los resultados se guardan temporalmente en archivos separados y se abren por handles opacos, nunca por ruta. No se promete exactly-once. La prueba ha corrido localmente en Windows; Linux runner y segundo host físico siguen pendientes.

Configuración de coordinator: `DistributedWorkers:StatePath` y `DistributedWorkers:TransferPath` en volúmenes protegidos; `DistributedWorkers:Transcoding:Mode=RemotePreferred` para habilitar el fallback remoto. Configuración del worker agent en `appsettings.json` o variables de entorno `DistributedWorker__...`: `CoordinatorUri` (HTTPS), `NodeId`, `ClientCertificatePath`, `ClientCertificatePassword`, `CoordinatorCertificateSha256Pins__0`, y `Ffmpeg__Enabled=true`, `Ffmpeg__ExecutablePath`, `Ffmpeg__CachePath`. El deploy incluye `ffmpeg-worker/Hispania.FFmpeg.Worker.dll`; Linux necesita `prlimit`. Antes de conectar, crea la identidad del nodo en administración autenticada con su fingerprint SHA-256 y los permisos `hispania.transcoding.ffmpeg.remote@1.0`. No configures el directorio de la biblioteca en el worker. Para Linux/Windows y ejemplos de enrolamiento, consulta ADR-0029.

## Future A/B support

### Phase 10 Playback Planner experiments

`Hispania.Runtime` provides `VariantExperimentRouter<T>` for generic, versioned capabilities. A variant key includes module ID, module version, capability ID, exact contract version, and generation. Configuration revisions are immutable; traffic shares use basis points summing to exactly 10,000. Calls are assigned from a caller-supplied user/session key and retain their variant while configured. The default `InMemoryPlaybackExperimentStore` is temporary. To persist an experiment, configure `PlaybackExperiments:StatePath` (environment variable `PlaybackExperiments__StatePath`) and a stable secret `PlaybackExperiments:AssignmentSalt` of at least 32 characters (`PlaybackExperiments__AssignmentSalt`), then POST with `"persistent": true`. The JSON store atomically writes one active configuration and assignments to a local file for a single server instance. It stores only salted SHA-256 assignment hashes, exact module/contract versions, generations, disabled variants, and expiration timestamps; entries expire after 30 days. Keep the file in an access-controlled data directory and the salt in a secret manager. Rotating/removing the salt blocks automatic recovery until an administrator reconfigures the experiment. Multi-instance deployments must provide a coordinated database-backed `IPlaybackExperimentStore`.

`PlaybackExperimentService` wires the router into the public `IPlaybackPlanner` HTTP path and module supervisor. Both current planners declare Manual, Shadow, and TrafficSplit support. Configure the authenticated `/internal/admin/modules/playback-experiments` endpoint with JSON such as `{ "experimentId": "playback-v1", "mode": "TrafficSplit", "variants": [{ "moduleId": "<id-A>", "basisPoints": 7000 }, { "moduleId": "<id-B>", "basisPoints": 3000 }] }`. Weights must total 10,000; variants must be running, health-checked providers of the exact `hispania.playback.planner@1.0` capability. GET the base path for status; use POST `/{id}/suspend`, `/{id}/rollback`, `/{id}/variants/{moduleId}/retire`, and `/{id}/finish`; GET `/{id}/metrics` for structured export. The existing admin bearer filter protects every route.

Assignments remain stable across weight changes while their variant stays configured. Retirement invalidates that cohort's mapping; after restart a missing variant follows the explicit policy: `RedistributeToHealthy` removes the missing variant, redistributes weights, and switches to Manual if only one compatible variant remains; `Fail` prevents resumption. A changed version/contract, failed health check, missing experiment declaration, lost publisher authorization, or changed assignment-salt fingerprint prevents the experiment from resuming. GET status reports `Ready`, `RecoveredWithFallback`, or `NotResumed`; administrators can then reconfigure with currently installed and authorized modules. A recovered disabled variant stays disabled and is not automatically reactivated. Metrics and in-flight work are process-local: restart interrupts an active HTTP operation and does not retry it. No planner side effect is replayed. Rollback history is in memory and does not survive restart; the active persistent configuration and cohorts do.

New configuration snapshots do not change active calls. Retirement routes new calls away immediately but keeps the module loaded so captured operations and any other capabilities can finish. Finish clears new routing and drains active planner calls before archiving metrics. Failure threshold is configurable (consecutive errors/timeouts); the supervisor is marked degraded, health fallback applies to new calls, and no operation is retried. Manual rollback restores the previous validated snapshot. Suspending a variant marks it unhealthy in the experiment and uses the configured fallback; rollback can reactivate it only after a health check succeeds.

Exports distinguish assigned, executed, completed, errors, timeouts, cancellations, fallback counts, shadow errors/cancellations/differences and mean latency, with module/version/generation identity and last correlation ID. Planner calls are synchronous and accept no cancellation token: timeout/cancellation metrics only reflect provider-thrown exceptions. Shadow runs only against the declared read-only `IMediaCatalogReader` boundary; it cannot invoke playback or transcoding. Its 100 ms observer timeout does not block the active response, but cannot terminate native code already running. Interface restriction is not a sandbox against hidden filesystem/network effects in trusted code. Do not opt effectful capabilities in without an enforceable isolation boundary.

New capabilities must opt in only after review. Shadow mode requires a read-only public operation and restricted read-only dependencies that can be verified; a native module declaration alone cannot constrain hidden file, network, database, or process effects. Scanners, migrations, shared persistence, and metadata writers are not eligible. Do not interpret shadow differences as defects without functional comparison rules. See ADR-0028 for the boundary and limitations.

## Platform Background Jobs (Phase 13.5)

Platform Jobs are durable commands/work, separate from Events (facts). Contracts and handlers use stable registered JobType IDs and bounded JSON; Platform persistence stores Jobs and attempt history in its own `jobs.sqlite`. The server currently registers the local `platform.test.hash` handler. A SQLite acquisition transaction and renewable lease coordinate local execution; handler code runs outside the transaction. Jobs support absolute scheduling, backoff/retry, cancellation requests, per-Job timeout, attempt history, idempotent creation keys, and stale Running recovery with at-least-once semantics. The server exposes session-RBAC admin APIs at `/api/platform/jobs` and the Web page `/admin/jobs`. WorkerAgent does not reference the Jobs implementation; a future adapter may connect through contracts. Payloads are omitted from admin list/detail and audit. Retention is not automatic. See [ADR-0034](adr/ADR-0034-platform-background-jobs.md) for failure status semantics and operational limits.

El runtime debe poder evolucionar hacia múltiples implementaciones activas:

```text
recommendations:
  A: recommender-basic
  B: recommender-ml
  split: 50/50
```

Esto no es requisito para MVP, pero no debemos cerrar esa puerta.

## Generic distributed worker providers

WorkerAgent hosts generic transport, mTLS identity, leases, health, and capability dispatch. Optional capability providers implement `IDistributedWorkerCapabilityProviderV1` in Runtime and are loaded from operator-configured assembly paths. The FFmpeg provider is implemented by the Media transcoding adapter and is absent from WorkerAgent project references. Keep provider loading explicit and trusted: assemblies run with the worker process privileges. `apps/server` and WorkerAgent are composition roots; they may join generic contracts with selected implementation providers while domain projects remain restricted to their owned contracts.

## Platform Authorization and Web Authentication (Phase 13.2)

Platform defines versioned authorization contracts independently of Media and Inventory implementations. Roles and string permission identifiers are persisted with Identity in `identity.sqlite`; grants are resolved server-side for each protected request, so active sessions reflect role changes immediately. A missing grant denies access, and disabled users have no effective permissions. For contract ownership, bootstrap, legacy bearer coexistence, and the Web `sessionStorage` decision, see [ADR-0032](adr/ADR-0032-platform-authorization-and-web-auth.md).

The ASP.NET endpoint filter centralizes `Session` validation and permission checks. Session absence/invalidity returns 401; an authenticated user without the required grant receives 403. A one-time operator-configured bootstrap secret provisions the first administrator, and the temporary legacy bearer is explicitly logged when used on compatibility-filtered routes. Phase 13.3 applies this filter to Media and Inventory at the server composition root; see the domain authorization boundary below.
## Domain authorization boundary

Phase 13.3 keeps authorization enforcement at the HTTP composition root. Inventory publishes `inventory.read` and `inventory.write`; Media publishes `media.library.read`, `media.playback.use` and `media.admin`. The server applies the reusable Platform permission filter to domain routes, while domain contracts/application projects do not reference the Authorization implementation. Platform Authorization remains independent of Media and Inventory. See [ARCHITECTURE.md](ARCHITECTURE.md#phase-133-domain-authorization-integration) and [ADR-0032](adr/ADR-0032-platform-authorization-and-web-auth.md) for the permission catalog and the explicit legacy/public-route exceptions.
