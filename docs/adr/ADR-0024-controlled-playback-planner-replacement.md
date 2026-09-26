# ADR-0024: Supervised Modules and Controlled Playback Planner Replacement

Status: Accepted (bounded implementation; operational validations pending)

## Context

The modular runtime needs explicit lifecycle and dependency state, and the pure Playback Planner provides a safe first capability for proving replacement continuity. General native-module unloading is not available in the current static composition model. Effectful operations cannot be replayed safely, and persistence replacement requires separate consistency and migration guarantees.

## Decision

- `ModuleRuntime` owns lifecycle state, health checks, dependency validation and transition observability for statically composed modules.
- Descriptors carry module version, versioned provided/required contracts, explicit minimum-version dependencies and a per-capability dynamic-replacement declaration.
- Initially allow registry replacement only for health-checked `hispania.playback.planner@1.0` implementations.
- Start and health-check the target before switching the router. Pin every operation to a planner generation; stop admitting operations to the old generation and wait for its leases before stopping its module.
- Propagate a failed operation once. Never replay it. On a failure after activation, block new operations while restoring and health-checking the last-known implementation, then drain and retire the failed implementation.
- Protect internal listing and replacement endpoints with a configured bearer secret. Do not expose them as public APIs.
- Distinguish registry substitution from assembly unload and physical file update. None of the latter two is implemented.
- Exclude databases, live migrations, A/B traffic, and effectful capabilities until their own consistency, concurrency and rollback guarantees are demonstrated.

## Consequences and validation

The implementation is in-process and does not grant isolation from native code. Runtime state transitions are observable by event and metric; replacement and rollback have counters. Tests cover draining, operation continuity, target-health rejection, failed-operation propagation without retry, rollback, contract eligibility and endpoint authentication. PostgreSQL live execution and Linux worker resource ceilings remain pending actual CI/host execution and are not implied by the local test results.
