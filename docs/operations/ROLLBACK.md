# Rollback and recovery runbook

HISPANIA has three separate rollback mechanisms. Choose based on what has
changed and preserve evidence before acting. The detailed deployment commands
and provider-specific restore requirements are in
[UPGRADE-ROLLBACK.md](UPGRADE-ROLLBACK.md).

## Application binary rollback

Binary rollback returns to an earlier executable. It does not reverse schema
migrations. It is eligible only when the new host has not started and the
durable operation marker and retained directories prove that no new writer
acquired the runtime lease. Follow the documented `recover-before-start` path.

If the new host started, assume it may have changed one or more stores even if
it never became Ready. An old binary may not understand the new schema. Do not
claim or attempt a universal binary-only rollback in that state.

## Package rollback

Package rollback selects the package lifecycle's retained KnownGood generation
for the affected package graph. Use the package manager's validated rollback
operation and inspect the resulting activation and health state. Package
rollback does not restore application data, configuration, other package
graphs, or owner databases.

## Data restore

Data rollback means restoring a verified pre-upgrade backup whose format and
provider set are supported by the target restore procedure. For a managed
SQLite installation use the documented `rollback-restore` command only after
the new process is stopped. For PostgreSQL, provision an empty owner database
and restore with authenticated credentials; never overwrite the failed database
or enable trust authentication to make restore succeed.

Before restoring:

1. Stop all application and worker writers.
2. Preserve failed binaries, stores and operation markers for diagnosis.
3. Verify backup manifest, checksums, file completeness, format compatibility
   and restore root.
4. Confirm the restore target is isolated and is not a personal/production
   media root or unrelated database.
5. Restore to a fresh target using the documented owner/provider procedures.
6. Restore public configuration and secret references separately; do not
   extract secrets to logs or evidence.
7. Verify owner invariants, permissions, package state, fencing/idempotency
   state and service readiness before reopening traffic.

Restoring multiple independent stores is not globally atomic. If recovery is
interrupted, stop and inspect retained markers and failed state; do not rerun
blindly. Phase 23 process-kill tests do not establish physical power-loss or
hostile-filesystem safety.

## Decision table

| Observed state | Action | Not a valid substitute |
| --- | --- | --- |
| New host has not started; operation marker proves this | Documented before-start binary recovery | Deleting markers or manually swapping directories |
| Package activation failed and package KnownGood is intact | Package lifecycle rollback, then health verification | Restoring unrelated databases |
| New host started or data/schema may have changed | Stop writers and restore compatible pre-upgrade data | Starting old binaries against possibly migrated data |
| Backup validation fails or source format is unsupported | Keep service stopped and escalate | Resetting stores or treating an existing directory as a valid backup |
