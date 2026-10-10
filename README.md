# HISPANIA

**A modular application platform for media, business software and distributed workloads.**

HISPANIA is a .NET modular application platform. Its Platform layer provides runtime mechanisms; domain suites supply business behavior. Media is a domain suite, and Inventory is the first non-media domain proving the same infrastructure can support business software. A single server host composes the suites. Remote workers are optional; a basic installation runs on one machine.

## Architecture

```text
                 HISPANIA PLATFORM
┌────────────────────────────────────────────────┐
│ Runtime · Modules · Supervisor · Extensions    │
│ Identity · Authorization · Events · Jobs       │
│ Storage · Assets · Audit · Observability        │
│ WorkerAgent · Distributed Execution · Trust     │
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
               Media UI · Inventory UI · Platform Admin

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

Implemented and demonstrated Platform mechanisms include versioned capabilities, module lifecycle and supervision, health checks, extensions, verified packages and host trust configuration, InProcess and OutOfProcess execution, generic WorkerAgent hosting, distributed workers, mTLS, leases, generations, fencing and recovery, persistent Identity/session authentication and RBAC, Durable Events, local/remote Background Jobs, opaque Assets with Storage Providers, persistent Audit, health/liveness/readiness, runtime metrics/tracing, consolidated Platform Admin, and the Playback Planner's A/B/Shadow infrastructure where applicable.

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

The Web Shell has declarative route and navigation registration for Media and Inventory, with Modern and Classic builds. The server composes the domains. Frontend module loading is not dynamic yet. Identity sessions and RBAC protect domain and Admin routes, including direct URLs and isolated grants. Session expiry removes administrative data. A bounded legacy admin bearer remains on explicitly documented compatibility routes; it is not accepted by Jobs, Events, Observability, or the consolidated Admin projections. See the [Phase 13 security and closure audit](docs/PHASE-13-CLOSURE.md).

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

The project implements package integrity checks, optional digital signatures and publisher trust, host authorization, worker node/capability authorization, mTLS, process separation and configured resource limits. These are controls with specific boundaries, not a general sandbox. InProcess modules inherit server privileges; an authorized remote worker can still be compromised. Legacy admin bearer compatibility is separate from the implemented session/RBAC system and remains limited to explicit routes. Resource limits do not isolate hostile code.

## Historical Phase 12 validation on `main`

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

## Phase 13 validation

**Phase 13 — CLOSED — validated on Linux and Windows**

Functional validated SHA: `75e10edd560e396a6e509a386d5aae5dd7928827`. [Validation #37230450104](https://github.com/joaquimcastilla/hispania/actions/runs/37230450104) completed successfully on Linux and Windows. The subsequent closure commit updates documentation only; `phase-13-complete` identifies that final checkpoint. Merge to `main` remains a separate task; Phase 14 has not started.

| Validation evidence | Result |
|---|---|
| Linux CrossPlatform .NET | 485 passed, 0 failed, 0 skipped |
| Windows full .NET | 542 passed, 0 failed, 1 existing PostgreSQL-live skip |
| PostgreSQL-live | 1/1 on Linux; Persistence 66/66 |
| Inventory/Platform regression | 53/53 on each OS |
| Closure gate | 210/210 on each OS: 4 process/architecture cases, 6 safe-error cases, 200 stress passes |
| Concurrency stress | 20 repetitions on each OS; no failures, skips or retry-to-green |
| Playback / distributed WorkerAgent controls | 99/99 / 10/10 |
| Observability | 17/17 |
| Web / focused Admin Web | 74/74 / 39/39 on each OS |
| Admin HTTP | 2/2 on each OS |
| Platform-only / Modern / Classic builds | Passed on Linux and Windows |
| FFmpeg / Planner lifecycle | 6/6 / 6/6 on each OS |
| Windows Job Object | 1/1 |
| Build warnings / errors / GitHub annotations | 0 / 0 / 0 |

Counts overlap across focused gates and full suites; they must not be added into a unique-test total. Real server graceful/abrupt restart and killed executor recovery passed alongside mTLS WorkerAgent crash/restart, leases and fencing. Full CI log review found no real secret material. The sole Windows PostgreSQL-live skip is covered by the required Linux live test.

See [closure evidence and limitations](docs/PHASE-13-CLOSURE.md), [definitive release notes](docs/PHASE-13-RELEASE-NOTES.md) and the [Phase 13 Release](https://github.com/joaquimcastilla/hispania/releases/tag/phase-13-complete).

## Milestones

- **Phase 13 — Platform Maturity: CLOSED, validated on Linux and Windows.** Immutable checkpoint: `phase-13-complete`.

- **Phase 11 — Distributed Workers:** distributed execution, mTLS, leases, fencing, remote FFmpeg and recovery. See the [Phase 11 tag](https://github.com/joaquimcastilla/hispania/tree/phase-11-complete).
- **Phase 12 — Platform Generalization:** Media-independent Platform boundaries, persistent Assets and Audit, Event Bus V1, generic WorkerAgent, Inventory, Web Shell and Platform-only build. See the [Phase 12 release](https://github.com/joaquimcastilla/hispania/releases/tag/phase-12-complete).

The `phase-12-complete` tag is an immutable historical checkpoint at `d0ca0b51a89fb0ba7fc6071f025b91404abf5e38`. `main` also contains a post-checkpoint concurrency correction validated at the SHA above; the tag was not moved to include it.

## Current limitations

- The immediate Event Bus is non-durable; the separate Durable Event Bus and Jobs are at-least-once, with no exactly-once or cross-store transaction guarantee.
- Inventory is limited and does not constitute a full ERP.
- Frontend module loading remains static. Identity/RBAC has no SSO or MFA.
- Storage initially uses a filesystem provider; no remote object storage. Metrics are runtime-only, with no TSDB/exporter. Assets Admin lookup is by ID; universal module generation is unavailable.
- Distributed coordination uses a single SQLite coordinator; no distributed PostgreSQL, multi-master, consensus or general distributed HA.
- There is no hostile-code sandbox, and A/B is not universal across domains.

## Future directions

Possible future directions include ERP core (organizations, product catalog, BOM, purchasing, manufacturing), Store, Automation, additional storage providers, SSO/MFA, external telemetry and stronger process isolation. None is introduced by Phase 13 closure.

## Repository map and further reading

- `contracts`, `core/runtime`, `core/domain`, `application`, `infrastructure`: contracts, runtime, domain logic and adapters.
- `apps/server`, `apps/worker-agent`, `apps/module-host`: composition roots and process hosts.
- `apps/web`: Modern and Classic Web Shell builds.
- `tests`: unit, integration, persistence, architecture and process-restart coverage.
- [Architecture](docs/ARCHITECTURE.md) · [Module System](docs/MODULE_SYSTEM.md) · [ADR-0030](docs/adr/ADR-0030-platform-generalization-and-domain-suites.md) · [Progress log](PROGRESS-LOG.md) · [Contributing](docs/CONTRIBUTING.md)
- [Phase 25 release readiness and acceptance matrix](docs/phase25-release-readiness.md)
