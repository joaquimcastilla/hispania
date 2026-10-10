# Offline upgrade and recovery runbook

1. Obtain/verify the new artifact and trusted external checksum; verify its RID.
2. Stop the native service; wait for stopped status, not just the stop request.
3. Confirm installation/Phase 17 writer exclusion. Do not replace live binaries.
4. Run the deployment CLI from an external delivery directory:

```sh
dotnet DELIVERY/deployment/Hispania.Deployment.dll upgrade DELIVERY HOME
```

The command verifies both publications, stages `app.new`, creates/verifies a fresh
pre-upgrade bundle, records switching intent, retires old binaries into `app.old`
and switches the whole directory. It preserves config/secrets/state and previous
backups. A retained `app.old` is an explicit conflict for another upgrade; archive
it after confirming the previous upgrade before running another one. No automatic
deletion of recovery evidence. PostgreSQL backup requires matching PGHOST, PGPORT,
PGDATABASE, PGUSER, PGPASSWORD/service or pgpass provisioning and pg_dump/pg_restore.
Never place the password on the command line.

5. Run new Server `--check`, start its native service, wait for livez/readyz and Web.
6. Verify owner operations; record the artifact manifest and backup identifier.

If staging/switching was interrupted before any new startup, inspect
`run/upgrade-in-progress.json`, retained directories and backup. Then:

```sh
dotnet DELIVERY/deployment/Hispania.Deployment.dll recover-before-start HOME
```

This accepts staging/switching markers, or a completed switch whose last-upgrade record proves the new host never acquired its runtime lease. Preflight dependency failures occur before that lease and can use this recovery. Startup intent is durably recorded before any store composition; afterward recovery requires restore. Rejected/staged artifacts are preserved.
It is never an undo-migration command. If a new host was started, assume migration
could have happened even when it failed to reach Ready. Stop all writers and use
the verified pre-upgrade backup plus old binaries. For managed SQLite installations:

```sh
dotnet DELIVERY/deployment/Hispania.Deployment.dll rollback-restore HOME BUNDLE
```

Restore validates the bundle and writes a fresh target, then retains failed state
and failed app directories before reinstating restored state and `app.old`. Remount
original media and reprovision private references before starting the predecessor.
The exact Phase 17 predecessor uses its documented legacy explicit host configuration,
not the newer `--home` configuration reader.

For PostgreSQL, first provision an empty owner database and point libpq restore
environment at it. Update public postgreSql.database and private provider material
for that database; never restore over a nonempty failed database. Retain failed
database for inspection. A rollback interruption is fail-closed and requires manual
inspection/verified restore; `recover-before-start` rejects rollback-phase markers.

For standalone Worker (no complete Server database set), stop the worker and
preserve public config, protected material and extension state; verify/stage a new
worker artifact and switch offline. Do not invoke the Server backup upgrade CLI on
a worker-only state root. No new module dependency/lifecycle behavior is introduced.

Uninstall unregisters service first. `uninstall-binaries HOME` retires app and
preserves config, secrets, state, logs and backups; remove retained artifacts only
under an explicit operator retention decision. No purge command is implemented.

The production configuration remains authoritative for trust/signature authorization after importing portable backup settings. A restored owner provider that disagrees with production persistence config fails explicitly. Reapply public publisher keys/allowlists from the operator config record; a legacy backup cannot disable production signature enforcement or implicitly enable demo extensions.
