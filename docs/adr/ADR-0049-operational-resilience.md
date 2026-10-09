# ADR-0049: Operational Resilience Harness

- Status: Accepted for Phase 24 local implementation
- Date: 2026-10-10

## Context

The platform already has health/readiness, durable SQLite Jobs and Events, filesystem Storage, and a fenced distributed-work coordinator. Operational evidence needs repeatable synthetic load and controlled recovery without binding test tooling to production resources or adding a second coordinator.

## Decision

Add a test-only campaign runner with a versioned manifest, reproducible seed, explicit bounded profiles/limits, a campaign-owned Server process, isolated stores, deterministic synthetic workloads, selected fault actions, cleanup, invariants, and checksummed reports. Reuse existing service adapters and contracts. Keep PostgreSQL execution for authenticated CI and define long campaigns without running them automatically.

## Consequences

Smoke can validate local wiring and cleanup; it cannot establish long-duration stability. The harness uses one coordinator and makes no distributed HA, exactly-once, global rollback, or production SLA claims. Process and metric evidence is host/runtime dependent and unavailable values are explicit. Future CI gates can build on this boundary without changing product APIs.
