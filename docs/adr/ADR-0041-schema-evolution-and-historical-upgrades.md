# ADR-0041 — Schema evolution and historical populated upgrades

Date: 2026-10-05
Status: Accepted locally; live PostgreSQL evidence pending CI

## Context

Phase 15 freezes contracts, but opening empty databases cannot prove preservation of installed state. Platform/Inventory use owner-local CREATE/ALTER while Media already has EF lineages. A future binary must not silently write an unknown schema.

## Decision

Keep independent owner plans. Source-link internal SQLite transaction mechanics, not domain migrations or a public API. Introduce owner/version/migration_id history and an atomic adoption step for the supported unversioned 0.7 shape. Validate before writer lock and after SQLite writer exclusion. Record DDL and ledger together; preserve existing rows/IDs and constraints. Missing files initialize; malformed/future histories and corrupt files fail without reset. Identity and Authorization validate their co-owned history before writing.

Keep Media's EF migrations and legacy aliases. Validate the applied prefix before normalization/migration; do not replace EF history. JSON playback retains schema 1 and atomic replace; failed reads block later writes. Fail startup before readiness if critical stores cannot initialize. Log fixed metadata and use store-only metric dimensions.

Freeze populated artifacts produced by source 78f291fdacf9442c6e2d8e23094dc0e309828693 separately from the 27 Phase 15 contract artifacts. Verify semantic rows, public readers, constraints, restart/idempotency, real process death, concurrency and no-write future rejection. Linux CI must execute populated PostgreSQL evidence, never skip it. Capture tools cannot overwrite reviewed fixtures and are outside CI.

## Consequences

Forward upgrades are supported for explicit historical shapes. Downgrade is unsupported; an old binary without these guards cannot be made safe retrospectively. Atomicity is per store, not a global installation transaction. The shared internal helper does not couple schema ownership. Schema 1 adoption is a bookkeeping foundation; it does not invent a new domain transformation. Existing EF steps supply the demonstrated real multi-hop transformation. Operational recovery and backup/restore remain Phase 17.

See [Phase 16 evidence](../PHASE-16-HISTORICAL-DATA-UPGRADES.md) for owners, fixture hashes, validation scope and pending CI.
