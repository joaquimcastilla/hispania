# Upgrade runbook

This runbook describes the safe operational sequence for a supported HISPANIA
deployment upgrade. The exact deployment CLI and recovery commands are recorded
in [UPGRADE-ROLLBACK.md](UPGRADE-ROLLBACK.md). Phase 23's evidence is targeted;
it does not make every historical distribution or every cross-store sequence a
supported upgrade path.

## Preflight

1. Identify the installed distribution, artifact version, RID and architecture.
   Verify the delivery manifest and SHA-256 through the trusted delivery path.
2. Check the source and target pair against the
   [Phase 23 compatibility matrix](../compatibility/PHASE-23-COMPATIBILITY-MATRIX.md).
   Stop if the path is marked Unsupported, Untested or Blocked for the topology
   you intend to use. Resolve configuration/schema errors before touching data.
3. Verify disk capacity, ownership, ACLs/file modes, service identity, required
   runtimes and package trust configuration. Do not put secrets in command
   arguments or diagnostic artifacts.
4. Ensure only the intended installation is selected. Stop the service and
   confirm the process has exited and all writers are drained.
5. Create a Phase 17 compatible pre-upgrade backup using the deployment's
   documented provider procedure. Verify its manifest and file hashes, and
   record the bundle identifier and location outside the installation root.
   PostgreSQL backup requires authenticated credentials and the matching
   `pg_dump`/`pg_restore` tools; never use trust authentication as evidence.
6. Preserve public configuration, private secret references/material, package
   trust settings, and the old application artifact. Do not copy private values
   into the upgrade report.

## Upgrade and validation

1. Use the deployment upgrade command and staging layout documented in
   [UPGRADE-ROLLBACK.md](UPGRADE-ROLLBACK.md). Do not replace binaries while the
   service is writing. Do not hand-edit schema ledgers or migration history.
2. Let the application perform only its supported owner migrations. A failed
   preflight should leave stores untouched. An interrupted or partially applied
   migration must be treated as an incomplete upgrade until the documented
   recovery procedure proves otherwise.
3. Start the service and wait for readiness and health checks. Check startup
   logs for migration completion, configuration warnings, package activation
   state, and owner store errors. Logs must not contain secret values.
4. Verify representative authenticated APIs and the enabled package/Web
   contributions. Verify preserved identities, relationships, package
   KnownGood state and other domain data using owner-specific checks, not merely
   process start or HTTP 200.
5. Restart once through the normal service procedure and verify migration
   idempotence and readiness again. Record source/target SHAs, bundle ID,
   results and artifact references.

## Failure response

- Before the new host has started: inspect the operation marker and staged
  directories, then use `recover-before-start` only where the detailed runbook
  says that state is eligible.
- Once the new host may have started or migrated data: assume the schema may
  have changed. Stop every writer. Do not try an old binary against the changed
  stores as a rollback strategy.
- Restore a verified compatible pre-upgrade backup using the documented
  provider-specific procedure. Preserve failed state for diagnosis. Reapply
  external media mounts and private references, then validate readiness and
  data invariants before resuming service.
- If the backup is missing, invalid, from an unsupported format, or cannot be
  restored safely, keep the service stopped and escalate through the site's
  recovery process. Do not reset or recreate the database to make startup pass.

Binary recovery before startup, package KnownGood rollback, and data restore
have distinct effects. See [ROLLBACK.md](ROLLBACK.md); none is a universal
cross-store undo migration.
