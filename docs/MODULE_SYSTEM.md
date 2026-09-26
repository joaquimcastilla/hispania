# Module System

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
3. **A/B:** not implemented. Stable cohort assignment, metrics, fallback, rate/resource limits and recovery are prerequisites.
4. **Dynamic substitution:** not implemented. The current DI graph and module lifecycle are process scoped; selection changes require restart.

The first shadow-capable functional operation is `IPlaybackPlanner.Plan` through `ShadowPlaybackPlanner` in Playback.Application, not in the kernel. Contract tests prove that the active planner result wins and the comparison planner only receives a read request. Native implementations can still contain hidden effects, so build policy must allow this only for reviewed read-only modules. There is no effect monitor or filesystem/network sandbox in the native runtime.

FFprobe consumes the Media-owned `IMediaSourceContentAccessV1` contract and never resolves local file paths. The active local provider owns locator validation, root containment and reparse-point rejection. Future NAS/HTTP sources can implement this contract without a FFprobe project reference.

Playback still selects the transcoder through the capability resolver at startup. Its response now contains an opaque `TranscodedContentHandle` rather than a path; `ITranscodedContentStore` delivers bytes/ranges. The worker process uses `hispania.transcoding.ffmpeg.worker@1`; source bytes arrive via stdin, while a bounded JSON request/response carries the version and opaque cache key. The host applies a timeout, process-tree cancellation and one retry from a reopened source. The worker is per job, not a daemon.

Three guarantees must not be conflated. Logical isolation comes from public contracts and ownership. Process isolation runs the worker and FFmpeg outside the HTTP host. Resource controls are configured via `Playback:Worker`: Windows Job Objects set memory/CPU/time ceilings and kill-on-close; Linux `prlimit` sets address-space, CPU-time and file-size ceilings plus a .NET GC heap cap. Per-job output limits and best-effort cache pruning do not quota the aggregate volume. These controls are not a security sandbox: worker identity/environment/cache are shared, and no dedicated ACL or sandbox profile is installed. Linux runtime enforcement still needs deployment-kernel validation.

Worker V1 forwards a correlation ID from the host activity. Metrics include attempts, failures, retries, cancellations, active jobs, duration, output bytes and observable process CPU/memory. Server `/healthz` is independent of optional transcoding capability health, available at `/internal/capabilities/transcoding/health`. Recovery integration tests cover worker crash, timeout, caller cancellation and failed publication followed by retry/recovery; the real FFmpeg integration covers output and cache reuse.

`Hispania.Library.Persistence.EFCore` does not reference a concrete database provider or provider exceptions. SQLite remains default and retains its existing migrations/schema; PostgreSQL is a distinct optional adapter selected by `Persistence:Provider=postgresql`, with native UUID mapping, its own initial migration and conflict classifier. Both expose the Library/Metadata persistence contracts. CI provisions PostgreSQL 16 and requires `HISPANIA_POSTGRES_TEST_CONNECTION`; integration compares migration from empty schema, reads/writes, constraints, rollback, concurrency and projected data with SQLite. Local live validation remains pending until that CI job passes.

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

`OutOfProcessResourceLimits` configures managed heap, memory/CPU process limits where the OS adapter can attach, request duration and concurrency. Windows Job Object attachment can fail under nested desktop jobs; production policy can require OS enforcement and fail activation instead of silently proceeding. Linux `prlimit` and Windows Job Object enforcement need their respective runners to be considered operationally verified. Process separation contains crashes; it does **not** create a security sandbox. The child shares the server's OS identity and may share its files, environment secrets and network privileges. Effective hostile-code isolation requires a separately restricted OS identity, filesystem, secrets, network and handles.

The current local OOP integration test covers protocol negotiation, cancellation and timeout, crash/degraded recovery, unrelated-module continuity, explicit in-process/OOP greeting selection, rollback and publisher revocation. Local Windows testing does not verify Linux `prlimit`, Windows Job Object quota attachment, PostgreSQL live integration, or a GitHub Actions run for the uncommitted tree. See ADR-0027 and the dated validation entry in `PROGRESS-LOG.md`; configured CI is not evidence of a successful run.

#### Platform and execution guarantees (Phase 9 audit)

| Platform / mode | Failure containment | Resource controls implemented | Evidence in this working tree | Limits / pending |
|---|---|---|---|---|
| Windows / InProcess | ALC unload boundary; module exception handling only | None separate from server | Full .NET suite on Windows | Same identity/address space; not a sandbox |
| Linux / InProcess | ALC unload boundary; module exception handling only | None separate from server | Linux CI is configured, but current local changes have no run | Same identity/address space; not a sandbox |
| Windows / generic OutOfProcess | Worker process, health and bounded restart policy; Job Object kills descendants when attached | Job memory, CPU-time/rate, active process count; host request timeout/concurrency | Local Windows OOP recovery test passed with mandatory Job Object attachment enabled | Current-user identity remains shared; disk volume not quota-limited |
| Linux / generic OutOfProcess | Worker process, health and bounded restart policy; process tree termination | `prlimit` CPU-time ceiling, .NET GC heap hard cap, host request timeout/concurrency; `RLIMIT_AS` is opt-in because it prevents CoreCLR from reserving its runtime address space | Ubuntu validation run confirms PostgreSQL, FFmpeg and FFmpeg `prlimit`; generic module suite still needs a passing run after disabling default `RLIMIT_AS` | Heap cap is managed-runtime enforcement, not an OS process-memory quota; no cgroup aggregate quota, process-count or module filesystem quota; shared user identity |
| Windows / FFmpeg worker | Per-job worker; kill-on-job-close when Job Object attaches | Job memory, CPU-time/rate, active process count, host job duration/output cap/cache pruning | Six real worker recovery/media tests passed locally on Windows after process-count limits were added | Cache pruning is best effort; no disk quota for cache volume |
| Linux / FFmpeg worker | Per-job worker; process tree termination on timeout/cancellation | `prlimit` address-space/CPU/file-size, job duration, output cap, concurrency, best-effort cache pruning | Ubuntu CI explicitly runs real FFmpeg recovery/media tests; current local changes have no run | `RLIMIT_FSIZE` is a per-file cap, not aggregate disk quota; no cgroup memory/CPU or process-count quota |

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

The guarantee is registry substitution in one process. The runtime does not unload assemblies, replace files, or provide process/resource isolation for native modules. Registration of another statically composed implementation is not dynamic code loading. Hot swap of persistence, live schema changes, effectful capabilities and A/B traffic assignment are explicitly unsupported. PostgreSQL live integration and Linux resource ceilings remain pending actual infrastructure execution; a configured CI service is not treated as a passed run until its required integration test reports success.

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

## Future A/B support

El runtime debe poder evolucionar hacia múltiples implementaciones activas:

```text
recommendations:
  A: recommender-basic
  B: recommender-ml
  split: 50/50
```

Esto no es requisito para MVP, pero no debemos cerrar esa puerta.
