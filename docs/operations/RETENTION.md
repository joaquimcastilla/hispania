# Retention operations

Phase 22 maintenance is an explicit administrator operation. It does not run on a timer or in response to low free space. Every owner evaluates its own records. The Platform coordinator stores plans, run state and batch checkpoints in `maintenance.sqlite`; it does not query another owner's tables.

## Workflow

1. An administrator with `platform.maintenance.read` requests an owner plan. The plan reports candidates, protected records, estimated bytes where meaningful, and warnings. A plan expires after ten minutes.
2. An administrator with `platform.maintenance.execute` confirms the plan ID. The server revalidates eligibility under the owner's lock before each bounded batch. The caller limits a run to at most 100 batches. A crash can leave a run interrupted; replan and retry. Owner mutations are idempotent for the implemented stores.
3. Inspect the run history and owner state. SQLite row deletion is logical reclamation: it does not promise that the database file or filesystem free-space value will shrink. Do not run VACUUM automatically under load.

All retention policies default to disabled. `MinimumAge` is an additional floor; zero age requires an explicit enabled plan and execute request. Plan and execute use UTC timestamps. Unknown or future state is protected.

| Owner | Candidate | Always protected | Default age when unspecified |
| --- | --- | --- | --- |
| `platform.events` | Old events whose every delivery is delivered and aged | Pending, processing, dead-letter and replayable deliveries | Explicit minimum age required for execution |
| `platform.jobs` | Terminal jobs; failures and dead letters require recorded terminal audit | Pending, scheduled, running, retryable, unaudited failures, and idempotency tombstones | Explicit minimum age required; tombstones retain the configured idempotency horizon, 365 days by default |
| `platform.sessions` | Expired or revoked session hashes | Active/future sessions, users, roles and permissions | Explicit minimum age required for execution |
| `platform.packages` | Superseded managed artifact bytes and abandoned marked staging | Current, active and previous KnownGood graphs; incomplete operation journal | 30 days |
| `platform.backups` | Verified, unpinned archive bundles outside the newest retained set | Last verified copy, newest two by default, latest pre-upgrade copy, pinned bundles, corrupt/unknown bundles, bundles in restore use | 30 days |
| `platform.backup-staging` | Abandoned backup-create staging with the owner marker | Active creation, unmarked/unknown directories, restore staging and restore markers | 1 day |
| `platform.maintenance-history` | Old successful run records beyond the recent per-owner count | Active, failed, interrupted, cancelled and last successful runs | Explicit minimum age required; newest 20 per owner retained by default |

Audit has no deletion owner. Coordinator fencing generations and high-water state have no deletion owner. Domain files outside owned Platform storage, including user media, are never retention candidates.

## Backup archive and pins

Set `Backup:ArchiveRoot` to an archive directory outside `Backup:ManagedRoot`. Only immediate child directories that pass full Phase 17 bundle verification can be considered. Without this setting, backup retention execution is unavailable. Add an adjacent `<bundle-name>.pin` file to protect a bundle; remove it only by an explicit operator action. A pin is checked again before deletion. Bundle names are restricted to short alphanumeric, period, underscore and dash names. The backup owner preserves at least one verified copy even if `MaximumRetainedCount` is zero. The latest verified pre-upgrade bundle remains protected.

Backup plans list the first bounded batch of candidate names and backup IDs plus per-bundle timestamp, size, verification state, disposition and reason. They also show up to 20 protected/invalid bundle details and total protected count. Each backup execution consumes one batch from that plan. Replan to remove further candidates. An invalid newer backup never counts as a verified replacement. Restore and retention use the same per-bundle file lock. Backup creation and staging cleanup share the archive lock; staging cleanup requires the adjacent `.owned` marker. It does not clear restore staging or an incomplete restore marker.

## Recovery and boundaries

Job idempotency keys survive pruning in a separate tombstone table for the configured horizon. A duplicate returns the original job identity with `HistoryPruned` when its full history is gone. Event pruning deletes attempts and delivery rows with the event in one transaction only after all deliveries are completed. Package staging cleanup requires an ownership marker created by the package operation; it never clears an incomplete restore marker or unknown staging directory. The maintenance journal only records control state and never acts as a cross-domain transaction.

Expired unreferenced plan records are removed in the same small maintenance-history batch. Failed and interrupted runs remain available for diagnosis.
