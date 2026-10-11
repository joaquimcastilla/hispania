# ADR-0050: Module Platform Job contributions

Status: Accepted for experimental implementation; Step 2.1-F acceptance remains partial pending clean full-suite and platform regression gates.

## Context

Platform Jobs owns durable work, dispatch and results. ModuleExtensionCatalog owns module trust, activation, generations and operation leases. The two systems had no supported bridge for a signed module to provide a Job handler.

## Decision

Add the versioned `IPlatformJobContributionV1` capability and optional signed `ModuleManifest.JobContributions` metadata. Each declaration names a valid Job type and a capability/version mapped to the contribution contract. ModuleHost supplies module identity and generation; modules cannot choose either. The existing `PlatformJobHandlerRegistryV1` implements an additive lifecycle-aware contribution registry, atomically publishing batches while preserving static host-handler precedence.

Dispatch checks current publisher trust and the bound generation, acquires the catalog's existing lease, and invokes through the module's selected runtime. Deactivation and revocation stop new dispatches, cancel cooperative work and drain operation leases before callbacks are removed. Update validates the candidate and fences/removes the old generation before publishing the new generation, using existing package rollback behavior. Core Jobs persistence remains independent and handler registrations are ephemeral. Jobs without a handler after restart fail terminally and require explicit retry after an authorized handler is available.

Old manifests omit `JobContributions` and retain historical signing bytes; modules without this capability remain compatible. In-process modules remain trusted code and are not sandboxed. Timeouts/cancellation are cooperative for in-process handlers. The signed-package test demonstrates the ModuleHost IPC path and in-process job/drain path. A controlled differential traced its prior unload failure to generic `JsonSerializer.Deserialize<T>` over a DTO defined by the collectible test fixture; the production module instance and registration had already drained. Using `JsonDocument` in that test fixture makes the module and ALC weak-reference assertions pass (focused suite 9/9; the unload scenario passed 5/5). This identifies the test trigger, not the precise GC root; trusted module code can retain references. One full Runtime suite run had an intermittent timeout in a separate held-call unload recovery test, which passed on isolated rerun. F1 remains partial pending clean full-suite/platform regression.

## Consequences

- No second scheduler, registry, module lifecycle or Job store is introduced.
- Handler authority follows the signed package, publisher allowlist/revocation and active catalog generation.
- Missing handlers after restart do not cause automatic execution under a newly selected generation.
- The interface and manifest field are experimental public contract surface; compatibility metadata is recorded in `docs/compatibility/contracts.json`.
- Resolving the ALC retention and rerunning the required security/lifecycle matrix is required before Step 2.1 can close.
