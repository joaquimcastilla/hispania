# HISPANIA 0.7.0 — Step 1/10: audit and architectural contracts

**Assessment: PARTIAL.** This document consolidates the contracts and gaps visible in the RC1 source. PR #16 is reviewed but remains open and unmerged. No runtime contract was changed in this step because new generic Data Plane DTOs would duplicate or overstate the existing, narrower contracts.

## Evidence baseline

- `main` and tag `v0.7.0-rc.1` resolve to `9b5abefe4c6b631360d505587748aedcb6c522d0`.
- PR #16 (`audit/modularity-0.7.0`) is open against `main`, one commit ahead, and GitHub reports it mergeable. The head is `b9ea7f3c9efd16290d4ecd82854d3059bb9db709`; no status checks are attached to the PR. Its base is the RC1 commit, so its documentation and test changes have no merge-base conflict with the current base. This is a source comparison, not a completed PR check or merge.
- PR #16 adds a modularity audit, module development guide, documentation updates, a focused extension HTTP integration test and a probe script. Its own conclusion is PARTIAL: self-hosted CI on its head is outstanding and domain-only Server distribution is unsupported. It does not establish standalone Media/Inventory installation or ARM64 support.
- RC1 remains pre-1.0. No stable promotion or product-version change is part of this step.

## Existing architecture and implemented contracts

`Hispania.Platform.Contracts` and `Hispania.Runtime` are distinct from Media and Inventory implementations. `Hispania.Platform.slnf` is the existing no-Media/no-Inventory implementation build boundary. The Server remains the composition root and directly references Media and Inventory, so this does not demonstrate independently distributable domain packages. Inventory owns its persistence and has no implementation dependency on Media.

The repository already has several relevant contracts; the following are the actual names and limits at this baseline:

| Concern | Existing owner and contract | Assessment / gap |
|---|---|---|
| Node identity and authentication | `DistributedWorkerSnapshotV1.NodeId`, certificate thumbprint, coordinator authentication; Platform Identity separately models users and sessions | Worker identity is certificate-pinned and operator-authorized. It is not a portable enrollment identity/lifecycle API. Do not conflate worker identity with user identity. |
| Capabilities | `DistributedCapabilityV1`; `DistributedWorkerInventoryV1` | Exact contract ID/version plus OS, architecture, CPU, advisory memory, accelerator labels and concurrency. Inventory is a hint; authorization is a separate coordinator allowlist. |
| Registration | `IDistributedWorkCoordinatorV1.RegisterWorker`, connect request/response and heartbeat records | Implemented registration/connection protocol; do not add a second `WorkerRegistration` abstraction without a compatibility design. |
| Work descriptor and requirements | `DistributedJobRequestV1` and `PlatformJobV1` | Generic capability/job type and operation, bounded JSON payload, retry/side-effect flags and timeout. No general typed resource requirement or portable input/output contract. |
| Selection | Coordinator assignment/lease APIs and `IPlatformRemoteJobExecutorV1` | Current scheduler uses exact capability/version, authorization, health/connection, capacity and deterministic preference. Hardware labels do not prove application-level suitability. |
| Lease and fencing | `DistributedJobLeaseV1`; job generation and lease metadata | Expiry, generation fencing and stale completion rejection exist. SQLite supports a single coordinator; execution is at-least-once, not exactly-once. |
| Status and cancellation | `DistributedJobStateV1`, worker heartbeat cancellation IDs, coordinator cancel/fail/complete | Implemented for current worker protocol. Cancellation is cooperative; generation fences late results but do not prove immediate process termination. |
| Results and data | `DistributedWorkerCompletionV1`, bounded result metadata, `IDistributedMediaTransferBrokerV1`, Platform Storage opaque handles | Current generic Jobs V1 carries bounded inline JSON and small metadata. The broker transfers bytes for a Media path, despite a neutral host boundary. No generic authorized source/destination descriptor or client-to-origin direct delivery contract exists. |
| Telemetry and failure policy | heartbeat, job state/failure code, job attempts and observability contracts | Operational state and bounded failure summaries exist. No general resource-usage telemetry contract or portable failure-policy descriptor. |
| Modules and lifecycle | `IModule`, `ModuleDescriptor`, package manifests and Runtime lifecycle/catalog | Trusted .NET extensions support installation, activation, update, deactivation, rollback and uninstall under host policy. They run with server privileges; this is not a sandbox. Domain suites are not all installable runtime modules. |

### Proposed contract evolution (design only)

Do not add parallel interfaces in Step 1. In later steps, evolve the existing contracts additively and with explicit protocol negotiation: (1) add a versioned workload requirements envelope beside the existing job/capability identity; (2) introduce generic, opaque-handle source/destination descriptors and short-lived, audience-bound transfer authorization beside Platform Storage and the current media broker; (3) extend execution reporting with bounded resource telemetry and structured retry/failure classification; and (4) keep enrollment identity, user identity, publisher trust, node authorization and capability authorization as separate security decisions.

Existing V1 wire payloads and meanings must remain readable. New fields must be optional/defaulted for old consumers, or use a negotiated protocol generation/new versioned contract. Unknown required capabilities fail closed; optional metadata can be ignored. IDs, byte counts, timestamps, content hashes, expiry, audience, operation and maximum transfer limits must be validated. Credentials are short-lived, scoped, revocable and never logged or persisted in job payloads. Errors expose bounded stable codes, not secrets or raw exception bodies. Extension points are named and versioned; arbitrary CLR types, commands, paths and executable payloads are forbidden.

| Future contract concept | Owner | Boundary |
|---|---|---|
| Node identity/capability and registration | Module System / Platform worker contracts | Coordinator authenticates node; inventory never grants authority. |
| Workload descriptor/requirements/selection | Platform Jobs + Module System | App owns business meaning; scheduler evaluates generic declared requirements. |
| Execution lease/status/cancellation/failure | Module System coordinator and Platform Jobs adapter | Coordinator owns lease/fencing; Jobs owns durable user-visible job/retry state. |
| Source/destination/transfer authorization | Platform Storage/transfer contracts | Source and destination owners retain bytes; coordinator issues scoped authority and state only. |
| Execution telemetry | Platform Observability | Worker reports bounded facts; coordinator associates them with authenticated node and lease. |
| Client operation status | Platform Jobs/API | Client sees operation state and authorized delivery information without cluster topology. |

## Core, application, coordinator, worker and client boundaries

- **Core / Platform:** owns host startup/configuration, module registry/lifecycle, generic identity and authorization mechanisms, coordinator/scheduler, worker supervision, public APIs and observability. Platform contracts/runtime do not reference Media or Inventory. Platform code must not acquire application-specific behavior, require GPU resources or assume heavyweight host resources. The lightweight ARM64 resource goal is unverified.
- **Application/module:** owns business rules, configuration namespace, data schema and provider, declared capability/job handlers and its update/deactivation policy. It may request generic work through owned contracts. Physical independent installation is only available for the extension/package mechanism within its current trust/runtime constraints; it is not established for all domain suites.
- **Coordinator:** authenticates and authorizes nodes/capabilities, observes inventory, assigns supported jobs, owns leases/generation fencing, records status/results/failures and emits supervision data. It remains single-instance with SQLite-backed distributed coordination.
- **Worker:** advertises bounded inventory and exact capabilities, authenticates to the coordinator, executes only allowlisted handlers under a valid lease, receives cooperative cancellation and reports bounded completion/failure. Existing workers do not implement a generic data-source/destination client protocol.
- **Client:** requests services and can query supported operation status. A topology-transparent direct-origin data authorization/delivery contract is not present; do not claim that the client can currently obtain all data directly from the selected node.

## Control, execution and data planes

Adopt the architectural rule: **the coordinator controls operations, while bytes flow directly between authorized participants when connectivity and policy permit.** Control Plane covers identity, authorization, discovery, scheduling, leases, state and telemetry. Execution Plane covers the worker process performing an authorized handler. Data Plane covers source reads, transforms/streams and result writes.

This is a target contract, not a description of every current flow. The repository has scoped Media worker source/result transfer; generic Platform Jobs V1 currently sends bounded inline JSON and result metadata, and some result brokering is coordinator-hosted. Direct Play and distributed transcode examples in the stabilization request are architectural examples, not new flows implemented by this step.

Preferred future flows: Direct Play is authorized by the coordinator and served from source to client; distributed processing is authorized by the coordinator, with source-to-worker and worker-to-destination transfer; generic app processing follows the same source/worker/destination pattern. The coordinator observes and records state but does not relay bulk bytes by default.

An explicit relay may be selected when direct connectivity is unavailable, a client or protocol requires it, a security boundary requires inspection, or a route/node failure leaves relay as the authorized recovery. Relay must be opt-in/policy-authorized, size/rate bounded, observable, and clearly represented in status. If no safe route exists, fail with a bounded reason; never widen authorization silently. New data-plane flows are deferred.

## Dependency and implementation findings

- Prohibited dependency direction: Platform contracts/runtime/application must not reference Media or Inventory implementations. The RC1 Platform solution filter is the existing build guard; existing architecture tests cover selected assembly boundaries.
- Intended dependency cycle: none. Current domain dependency test/build evidence exists, but this step did not run the full architecture suite. No duplicate worker/job contract family was found in the files inspected; there is a Media-scoped transfer contract and a generic jobs/worker contract, which are distinct scopes rather than duplicates.
- Composition coupling: `apps/server/Hispania.Server.csproj` directly references Media domain/application/providers and Inventory application/contracts/persistence. This is expected for today's composition root but blocks a domain-free server distribution. Assign to Step 2/3 after a design of host composition.
- Host coupling: generic WorkerAgent remains a distinct app, while server composition references media/FFmpeg integrations and a platform Jobs adapter. The Platform-only filter excludes Media/Inventory implementation. Keep new generic contracts in Platform/Module System rather than server or media assemblies.
- Security: mTLS/certificate pinning, explicit node/capability authorization, bounded payloads, leases and fencing are implemented. They do not provide hostile-worker isolation, exactly-once side effects, multi-master availability, or generic end-to-end transfer authorization.
- Persistence/recovery: Media/domain stores are separately owned; Platform Jobs, Events, Identity and distributed work use their own SQLite stores. Existing recovery and rollback are subsystem-specific, not a global cross-store transaction.
- CI: `.github/workflows/validation.yml` and phase-specific workflows cover builds/tests across configured platforms. ARM64 is not in the support matrix. PR #16 has no reported check runs; its recorded local evidence is not a remote CI result.

## Architecture decision record consolidation

No duplicate ADR is added. Existing decisions are the source of truth for current mechanisms: ADR-0002/0003 for minimal kernel and explicit contracts; ADR-0029 for single-coordinator work, workers and fencing; ADR-0030 for platform/domain boundaries; ADR-0031/0032 for user identity and authorization; ADR-0033/0034/0035 for durable events, Jobs and remote job execution; ADR-0036/0037 for storage and observability; ADR-0039/0040/0048 for compatibility; ADR-0044 for package/lifecycle; ADR-0046 for security boundaries. This step adds the following accepted architectural direction to the relevant documents while preserving those narrower implementation scopes:

1. Platform/Core remains application-independent; composition roots may compose concrete suites.
2. Control, Execution and Data planes have separate responsibilities and trust checks.
3. Direct transfer is preferred; relay is explicit, bounded and authorized.
4. Scheduling remains generic and capability/version based, with advisory resource hints.
5. Node OS/architecture are inventory facts, not assumptions in contract semantics; support still requires platform validation.
6. Node identity, user identity, publisher trust and operation/data authorization remain distinct.
7. Package lifecycle operations are distinct and subsystem-owned; there is no universal atomic application upgrade/rollback.
8. Contract evolution is versioned, negotiated where required, additive within compatible generations, and evidence-backed under the compatibility policy.

Alternative considered: immediately add a new family of portable node/workload/data DTOs. Rejected for this step because equivalent worker/Job/lease concepts already exist and a second family would create ownership and migration ambiguity. Tradeoff: the target contract is documented, while direct data transfer and general requirements remain deferred pending compatibility design.

## Validation and completion gate

This step changed documentation only. Source-level inspections and PR metadata review are recorded above; no build or test result is claimed here. The PR #16 self-hosted CI status is outstanding. Full configured RC1 Linux/Windows jobs and ARM64 resource/support evidence remain distinct gates. Step 1 therefore remains PARTIAL until the PR's checks are recorded and the owner decides its integration; there was no merge or remote publication.

For Step 2, first establish an isolated Platform/Core composition path without Media/Inventory implementation references, review startup/provider ownership, and measure a release build and representative workload on a real Linux ARM64 target. Preserve existing V1 worker, package, schema and distribution versions. Do not declare Raspberry Pi support from a successful cross-build alone.
