# HISPANIA

**A modular application platform for media, business software and distributed workloads.**

HISPANIA is a .NET modular application platform. Its Platform layer provides runtime mechanisms; domain suites supply business behavior. Media is a domain suite, and Inventory is the first non-media domain proving the same infrastructure can support business software. A single server host composes the suites. Remote workers are optional; a basic installation runs on one machine.

## Architecture

```text
                 HISPANIA PLATFORM
┌────────────────────────────────────────────────┐
│ Runtime · Modules · Supervisor · Extensions    │
│ Workers · Events · Assets · Audit · Persistence │
│ Security / Trust · Health / Metrics             │
└───────────────────────┬────────────────────────┘
                        │ public, versioned contracts
               ┌────────┴────────┐
               │                 │
        ┌──────▼──────┐   ┌──────▼──────────┐
        │ MEDIA SUITE │   │ ERP FOUNDATION │
        │ Library     │   │ Inventory      │
        │ Metadata    │   │ Item           │
        │ Search      │   │ Warehouse      │
        │ Playback    │   │ Location       │
        │ Streaming   │   │ Stock          │
        │ Transcoding │   │ StockMovement  │
        └──────┬──────┘   └──────┬──────────┘
               └────────┬────────┘
                        ▼
                     WEB SHELL
               Media UI · Inventory UI

        Server host = composition root for suites
```

**HISPANIA Platform provides mechanisms. Domain modules provide business logic.** Platform does not depend on Media or Inventory. Media and Inventory consume Platform contracts, and their internal implementations do not depend directly on each other. The server host composes public contracts where cross-domain behavior is needed; the Web Shell presents the registered domains.

```text
Platform → no domain dependencies
Media → Platform     Inventory → Platform
Future ERP → Platform     Store → Platform     Automation → Platform
```

Future ERP, Store, Automation and Analytics are possible directions, not implemented suites.

## Platform

Implemented and demonstrated Platform mechanisms include versioned capabilities, module lifecycle and supervision, health checks, extensions, verified packages and host trust configuration, InProcess and OutOfProcess execution, generic WorkerAgent hosting, distributed workers, mTLS, leases, generations, fencing and recovery, Event Bus V1, opaque Assets, persistent Audit, persistence abstractions, metrics/hooks, and the Playback Planner's A/B/Shadow infrastructure where applicable.

The `Hispania.Platform.slnf` filter builds Platform contracts, runtime, generic WorkerAgent and Platform persistence without Media or Inventory implementations. This is a dependency-boundary build, not a claim that every domain service is independently deployable.

### Workers and distributed execution

```text
Platform WorkerAgent
    ├── capability hosting
    ├── transport and mTLS authentication
    ├── authorized nodes and capabilities
    ├── leases, health and generation fencing
    └── retry and recovery

Media provider
    └── FFmpeg
```

WorkerAgent is generic; FFmpeg is a Media provider loaded through the provider contract. Execution can be local or remote. The current coordinator is single-instance, using SQLite coordination state. Windows Job Objects and Linux `prlimit` apply configured process limits where supported. These controls do not create a sandbox. `AssemblyLoadContext` is not a sandbox; OutOfProcess is not hostile-code isolation; mTLS authenticates peers but cannot make a compromised worker safe. InProcess code inherits server privileges. Package signatures can be optional depending on host configuration.

## Media Suite

Media is a domain suite over Platform, containing library/catalog, metadata, search, playback, streaming and FFmpeg transcoding. Transcoding supports local and remote worker execution. The Playback Planner currently contains the Manual, A/B and Shadow experiments; A/B is not a universal domain capability. Platform has no dependency on Media.

## ERP Foundation: Inventory

Inventory is the first non-Media domain. It includes Item, Warehouse, Location, Stock and StockMovement, its own SQLite persistence, API and Web Shell view. Inventory stores opaque Asset references. The server composition root can connect an Asset to Media playback through public contracts; Inventory itself does not depend on Media.

**Inventory is not yet a complete ERP.** Purchasing, Sales, Manufacturing, Accounting and CRM are not implemented.

## Web Shell

The Web Shell has declarative route and navigation registration for Media and Inventory, with Modern and Classic builds. The server composes the domains. Frontend module loading is not dynamic yet. There is no general user/session and per-domain permission system; current administrative writes use a configured bearer token, while some read routes are public.

## Persistence and ownership

```text
Platform
├── Asset metadata             → SQLite
├── Asset content              → filesystem
├── Audit                      → append-only SQLite
└── Distributed coordination  → SQLite (single coordinator)

Inventory
└── Inventory data             → dedicated SQLite store

Media
├── SQLite
└── PostgreSQL (optional; live CI validated)
```

Each domain owns its data. Cross-store foreign keys are not used; domains exchange opaque references and public contracts. PostgreSQL support for Media does not mean distributed coordination supports PostgreSQL or multi-master operation. Assets and Audit currently have initial Platform providers that may later gain alternatives.

## Security and trust boundaries

The project implements package integrity checks, optional digital signatures and publisher trust, host authorization, worker node/capability authorization, mTLS, process separation and configured resource limits. These are controls with specific boundaries, not a general sandbox. InProcess modules inherit server privileges; an authorized remote worker can still be compromised. The current admin bearer token is not a complete Identity/RBAC system. Resource limits do not isolate hostile code.

## Validation on `main`

Validated SHA: [`2e16b039d0fb8a3223366f7af478353b68886af1`](https://github.com/joaquimcastilla/hispania/commit/2e16b039d0fb8a3223366f7af478353b68886af1) · GitHub Actions [Validation #36625775324](https://github.com/joaquimcastilla/hispania/actions/runs/36625775324).

| Area | Result |
|---|---|
| Linux .NET suite | 476 passed, 0 failed, 0 skipped |
| Windows .NET suite | 486 passed, 0 failed, 1 skipped (PostgreSQL-live, run on Linux) |
| PostgreSQL-live | 1/1 passed on Linux |
| Platform-only build | Passed on Linux and Windows |
| Persistent Platform Assets and Audit | Passed persistence and restart coverage |
| Inventory, persistence, architecture and real server restart/recovery | 6/6 |
| Distributed WorkerAgent / mTLS | 10/10 |
| Playback regression | 92/92 |
| Web tests | 31/31 |
| Modern / Classic builds | Passed |
| Compilation warnings / GitHub annotations | 0 / 0 |

The restart test launches real server processes, verifies persisted state after graceful restart, terminates a server abruptly, and checks committed state after recovery. The CI workflow also validates the complete build and suite on Linux and Windows.

## Milestones

- **Phase 11 — Distributed Workers:** distributed execution, mTLS, leases, fencing, remote FFmpeg and recovery. See the [Phase 11 tag](https://github.com/joaquimcastilla/hispania/tree/phase-11-complete).
- **Phase 12 — Platform Generalization:** Media-independent Platform boundaries, persistent Assets and Audit, Event Bus V1, generic WorkerAgent, Inventory, Web Shell and Platform-only build. See the [Phase 12 release](https://github.com/joaquimcastilla/hispania/releases/tag/phase-12-complete).

The `phase-12-complete` tag is an immutable historical checkpoint at `d0ca0b51a89fb0ba7fc6071f025b91404abf5e38`. `main` also contains a post-checkpoint concurrency correction validated at the SHA above; the tag was not moved to include it.

## Current limitations

- Event Bus V1 is local to the process and non-durable.
- Inventory is limited and does not constitute a full ERP.
- Frontend module loading is not dynamic; general Identity/RBAC is not implemented.
- Assets and Audit have initial providers; additional providers are future work.
- Distributed coordination uses a single SQLite coordinator; no distributed PostgreSQL or multi-master.
- There is no hostile-code sandbox, and A/B is not universal across domains.

## Future directions

These are roadmap possibilities, not current functionality or an indication that Phase 13 has started: Identity/users/permissions; ERP core (organizations, product catalog, BOM, purchasing, manufacturing); Store/Web; Automation; a durable Event Bus; additional storage providers; and stronger process-isolation options.

## Repository map and further reading

- `contracts`, `core/runtime`, `core/domain`, `application`, `infrastructure`: contracts, runtime, domain logic and adapters.
- `apps/server`, `apps/worker-agent`, `apps/module-host`: composition roots and process hosts.
- `apps/web`: Modern and Classic Web Shell builds.
- `tests`: unit, integration, persistence, architecture and process-restart coverage.
- [Architecture](docs/ARCHITECTURE.md) · [Module System](docs/MODULE_SYSTEM.md) · [ADR-0030](docs/adr/ADR-0030-platform-generalization-and-domain-suites.md) · [Progress log](PROGRESS-LOG.md) · [Contributing](docs/CONTRIBUTING.md)
