# ADR-0030 — Platform Generalization and Domain Suites

## Status

Accepted for incremental implementation in Phase 12.

## Context

Phase 11 has a generic module runtime, capability selection, extension lifecycle, experiment router, and distributed work mechanisms. The repository is still organized around Media: `core/domain` builds `Hispania.Media.Domain`; Library, Playback, and Metadata contracts are under `contracts/`; and the server composes their implementations. The platform mechanisms must be formalized without moving Media behavior into them.

## Structural audit at the Phase 11 baseline

| Area | Classification | Findings |
| --- | --- | --- |
| `core/kernel` | Platform | Minimal host/runtime primitives; no Media project reference. |
| `core/runtime` | Platform, with generic module and distributed-work mechanisms | References module-system contracts; no direct Media implementation reference found. It is still a broad runtime assembly. |
| `contracts/module-system` | Platform | Module lifecycle, manifests, capability selection, package trust, and worker protocol contracts. |
| `contracts/library`, `contracts/metadata`, `contracts/playback` | Media | Public domain contracts; several deliberately carry Media-owned types such as `MediaSource`, `MediaEntityId`, `WorkId`, and movie metadata. |
| `core/domain` | Media | Media identity, works, representations, sources, timelines, and coverage. |
| `application/library`, `application/metadata`, `application/playback` | Media | Domain use cases and policy. |
| `infrastructure/library`, `infrastructure/metadata`, `infrastructure/ffprobe`, `infrastructure/transcoding-ffmpeg`, `infrastructure/persistence` | Infrastructure / Media adapters | Concrete filesystem, provider, FFprobe, FFmpeg, and Library persistence implementations. Persistence folder currently contains a cross-cutting distributed-work adapter and Media-owned Library adapters. |
| `apps/server`, `apps/worker-agent`, `apps/ffmpeg-worker`, `apps/module-host`, `apps/cli` | Host/Application | Composition roots and process entry points. The server intentionally references concrete modules/adapters for composition. |
| `apps/web` | Host UI, currently Media-oriented | Two builds share Media UI/API surfaces; shell/domain UI separation remains future work. |
| `modules/*` | Domain modules or architecture fixtures | Playback planners and demos are modules; architecture-test modules exist for contract demonstrations. |
| experiment, distributed runtime | Cross-domain candidate | Variant routing and leased work are generic mechanisms; distributed transcoding request/handlers and result validation remain Media-specific. |

No direct dependency from `core/kernel` or `core/runtime` to Media implementation was found by project references and namespace/type scans. Media-specific references in Library, Playback and Metadata contracts are expected domain ownership, not evidence those projects belong in Platform. Do not move these types into Platform. Potential debt: broad runtime assembly mixes module lifecycle, experiment infrastructure, and distributed work; the server is a large composition root; assets are currently Media catalog concepts and need a separate generic reference contract before other domains adopt them.

## Decision

Add `Hispania.Platform.Contracts` as a Media-independent assembly. The first public contracts cover versioned events, audit records, and opaque assets. `Hispania.Runtime` owns the first local event bus adapter and remains independent of Media. Event dispatch is sequential and deterministic in-process; a handler failure is returned in the publish result and does not suppress later handlers. Caller cancellation propagates. Metrics are an injectable hook.

Assets expose opaque IDs, metadata, scopes, access-policy references, and content operations; callers never receive physical paths. Persistence and authorization remain responsibilities of registered adapters/hosts. Audit metadata is a string map and callers must omit secrets and private content.

Media remains the first domain suite and keeps its existing contracts and behavior. A second domain and application integration will be added incrementally before any repository-wide relocation.

## Phase 12 continuation: Inventory proof

Inventory is the first non-Media suite. Its public API is `Hispania.Inventory.Contracts` (`IInventoryQueryV1`, `IInventoryCommandV1`); application policy is in `application/inventory`; its SQLite owner is `infrastructure/persistence/inventory-sqlite`, with a separate configured database file and tables. The adapter uses its own schema and only intra-Inventory foreign keys. Asset IDs are opaque strings in Inventory tables; there is no FK or read/write access to Media data. The persistence interface sits in Inventory Application, so a future PostgreSQL adapter can replace SQLite without changing suite contracts.

Inventory publishes `ItemCreatedV1`, `StockChangedV1`, and `StockMovementRecordedV1` through `IPlatformEventPublisher`. Writes are committed first; a subscriber failure is isolated by the bus and does not reverse the committed Inventory operation. Audit records are emitted after successful write operations, with actor when authenticated, timestamp, domain/action/resource/result and correlation. Audit metadata stays empty unless safe details are needed. Audit now uses the persistent append-only SQLite adapter described below.

Inventory stores only `AssetReference` IDs and does not depend on the asset store. The server resolves those IDs through `IPlatformAssetStore`. The server resolves those references through the persistent Platform asset provider. The BP48 seed includes image, datasheet, and playable MP4 content.

The first Web Shell slice remains inside `apps/web/src/App.tsx`: the app shell/layout is the shared header/drawer; routing uses History API and pathname checks; there is no first-class user auth/session or domain permission model. Inventory appears as its own ERP navigation entry and a standalone page component. Inventory write APIs require the existing configured `Modules:AdminToken` bearer; reads are public, matching the present Media API posture. Inventory is composed directly by the server, not dynamically registered as a runtime module yet.

`Hispania.Platform.slnf` builds Platform contracts, kernel/runtime, module host, generic worker-agent, and Platform persistence without Media or Inventory implementations. Worker capabilities are loaded through the Runtime provider boundary.

## Consequences and limits

- Platform contracts compile without Media references, guarded by a runtime test.
- The local bus is process-local and non-durable; it does not provide retries or exactly-once delivery.
- Assets and Audit are durable in the server host; the local event bus remains process-local and non-durable.
- The minimal `/erp/inventory` view is a separate component/API consumer inside the existing Web Shell; authentication/permission shell extraction remains future work.
- Worker provider assemblies run with worker process privileges and must be configured explicitly as trusted code.
- Existing Phase 11 runtime, workers, Media persistence, and web builds are not restructured by this decision.

## Phase 12 persistence and composition update

Platform asset metadata is owned by `infrastructure/platform/persistence-sqlite`; content is owned by a separate filesystem provider that exposes generated opaque handles only. Audit uses an append-only SQLite writer and a paginated reader. Both stores are composed by `apps/server`, which also owns authorized HTTP routes. Audit filters secrets and content-like metadata keys before persistence. The demo policy grants public read for the seeded assets; uploads otherwise require the configured admin bearer token and use the configured byte ceiling.

```text
HISPANIA Platform
├── Media suite
├── Inventory bounded context
├── Platform contracts / event bus / assets / audit / workers
└── Web Shell: module navigation + Media UI + Inventory UI

Inventory item → opaque AssetRef → Platform Assets → optional Media stream capability
```

Media does not own an Asset merely because its type is Video. `apps/server` is the composition root for the Media stream capability: it checks the public asset contract and exposes byte-range playback only for video assets. Inventory contains no Playback calls, and the Platform asset store has no Media dependency. Inventory SQLite, Platform asset metadata/content, Audit SQLite, and the Media library database remain distinct data owners.

The generic WorkerAgent now loads providers from configured assembly paths through `IDistributedWorkerCapabilityProviderV1` in Runtime. The FFmpeg provider remains in the Media transcoding project; the agent executable and `Hispania.Platform.slnf` do not reference that project. Server and WorkerAgent are composition roots: they may reference implementation assemblies to construct public contracts, while domain application/contracts remain restricted to their own domain and Platform contracts.

The BP48 seed persists with image, datasheet, and a small valid MP4 sample. `ServerProcessRestartTests` launches independent server processes and verifies API-visible BP48, warehouse/location, stock/movements, assets/content/association, Audit, and Media video streaming across a graceful restart. It then commits a stock issue, kills the server process abruptly, and verifies committed state after another process restart. The web route/navigation registry composes the current Media and Inventory registrations; arbitrary dynamic module loading is future work.

The solution filter and assembly-reference tests enforce the Platform-only dependency boundary. `apps/server` and `apps/worker-agent` are explicit composition roots; the FFmpeg bridge lives in the Media transcoding adapter and implements Runtime's public provider contract. Inventory and Platform metadata/content/Audit each retain separate persistence ownership. CI now includes Platform filter builds and Inventory integration/architecture tests on Linux and Windows. This local change set has not yet been run through GitHub Actions.
