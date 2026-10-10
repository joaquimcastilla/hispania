# ADR-0048: End-to-end compatibility and upgrade validation strategy

Status: Accepted for Phase 23 validation implementation

## Context

HISPANIA already has owner-specific schema upgrades, historical compatibility
baselines, backup/restore, production configuration, package lifecycle, Web
contribution validation, security hardening and maintenance gates. Duplicating
those mechanisms as a new general-purpose migration/versioning subsystem would
create competing sources of truth. At the same time, a process-start test alone
does not demonstrate preservation of populated state or recovery behavior.

The accepted historical Phase 22 source contains selected immutable contract,
binary, protocol, package and populated owner-store fixtures, but no complete
runnable release snapshot for each Phase 13–22 phase. Historical claims must be
bounded by the actual artifacts available.

## Decision

Phase 23 composes existing phase gates and adds a test-only upgrade matrix,
read-only provenance/hash validation for historical manifests, and a dedicated
local runner that records target SHA, logs, test results and limitations. It
reuses owner migrations and existing backup, package, configuration, HTTP, Web,
worker/security and maintenance tests. Destructive test operations remain in
isolated fixture/temp roots.

The compatibility matrix classifies each path as Supported, Unsupported,
Untested, Blocked or Not Applicable and names its evidence boundary. Selected
historical readers built from recorded predecessor SHAs are identified as
reader tests, not as complete historical application installations. No
Public Stable contract is created, no business feature is added, and the
distribution version remains unchanged.

Rollback reporting separates before-start binary recovery, package KnownGood
rollback and compatible backup/data restore. We do not claim a universal old
binary rollback after schema migration, global atomicity across independent
stores, exactly-once processing, or physical power-loss safety.

## Consequences

- Existing owner and subsystem tests remain authoritative for their specific
  guarantees; the Phase 23 gate adds reproducible composition and evidence.
- Missing complete historical releases and unavailable PostgreSQL/native
  service prerequisites stay explicitly Untested or Blocked.
- Local green evidence prepares acceptance CI but cannot substitute for
  authenticated PostgreSQL, Windows SCM, Linux systemd or cross-platform runs.
- Evidence is local and ignored by Git unless a later, separate acceptance
  workflow publishes sanitized artifacts.
- Any future expansion of compatibility claims requires authentic source
  artifacts and a test that exercises the stated consumer/upgrade path.

## Related decisions

- ADR-0040: Historical compatibility baselines
- ADR-0041: Schema evolution and historical upgrades
- ADR-0042: Backup, restore and disaster recovery
- ADR-0043: Configuration, secrets and production deployment
- ADR-0044: Package dependencies and lifecycle
- ADR-0045: Dynamic Web contributions
- ADR-0046: Security boundaries and operational hardening
- ADR-0047: Retention, reconciliation and capacity observation
