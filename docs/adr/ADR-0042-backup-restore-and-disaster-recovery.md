# ADR-0042 — Backup, restore and disaster recovery

Date: 2026-10-05
Status: Accepted for local implementation; mandatory live Linux disaster evidence pending

## Decision

Use an explicit offline installation barrier for the first full-backup implementation. The composition root opts into a managed durable root, takes an OS-released exclusive installation lock before opening stores, and preserves existing configured source paths through a machine-local layout descriptor, while bundles and clean restore use canonical destination paths. Machine source paths never enter the bundle. Backup requires the host to stop; it acquires the same lock. No writer, including dispatchers, coordinator callbacks or trusted extension processes, may remain active. External writers are unsupported. Reads are unavailable during this initial offline window. Timeout rejects the operation without changing source state.

The consistency point starts after shutdown has completed and the exclusive lock has been acquired. No new operations are admitted. Committed durable Running/Processing records are preserved without falsifying completion; restart uses existing lease recovery and at-least-once semantics. Coordinator generation high-water is retained. The host can resume after capture releases the lock. This is application quiescence, **not global ACID**.

Capture SQLite through its online backup API into standalone files, including committed WAL pages, and run integrity checks. PostgreSQL uses compatible `pg_dump`/`pg_restore` logical tooling with credentials reprovisioned externally. Filesystem bytes retain opaque handles; owner-local validation detects missing Asset bytes, records orphan bytes, and retains package bytes without repackaging.

Backup format generation 1 is independent of distribution 0.7.0 and database schemas. A bounded manifest inventories components, schemas, SHA-256 hashes and sizes. Stream data into a sibling staging directory; publish only a completely verified bundle by directory rename. Checksums detect accidental corruption; they do not authenticate a malicious replacement of both manifest and payload.

Restore supports only a clean managed root. Validate all input before changing the target, prepare a complete sibling root and rename it into place. PostgreSQL restore has no distributed commit with the local root: a durable marker outside the root blocks startup until the complete restore succeeds. Interrupted external restore requires an operator to recreate the clean database and retry; do not claim global rollback. Machine paths are supplied at the destination. Current owner migrations run on subsequent startup.

Portable configuration is an explicit allowlist, including public publisher keys and revocation policy. Exclude DB passwords, bootstrap tokens, API keys, signing private keys, worker certificates/private credentials and assignment salts. Identity password/session hashes are necessary durable security state and make the bundle sensitive. Protect its access; this phase does not add encryption or secret escrow.

## Scope and acceptance

Single-host, manual full backups only. No retention scheduler, cloud, incremental backup, HA, partial/merge restore, installer or Phase 18 configuration platform. Existing Phase 15 artifacts remain immutable. This ADR is not accepted closure evidence until real clean-host, mixed-provider, interruption and regression gates pass.
