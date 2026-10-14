# HISPANIA v0.7.0 — Step 2.1-V: Complete RC1 to Platform-only Migration

**Status: PARTIAL.** A separate, fail-closed migration operation is implemented and passed one end-to-end Windows x64 fixture run using the official Complete RC1 publication. This is not a general migration acceptance: PostgreSQL installations are deliberately blocked, cross-platform CI has not run, and final operator startup acceptance is not performed by the migration CLI.

## Source and compatibility

The source is restricted to the official `v0.7.0-rc.1` Complete publication:

- Release: [v0.7.0-rc.1](https://github.com/joaquimcastilla/hispania/releases/tag/v0.7.0-rc.1)
- Source commit in its manifest: `9b5abefe4c6b631360d505587748aedcb6c522d0`
- Source distribution: `complete`, version `0.7.0-rc.1`, `sourceDirty=false`
- Windows x64 ZIP SHA-256: `5a83527edc208552372f8452bfbf1037e0fa761b7e13b6c6ae9a13512e2a7eaf`
- Windows x64 external manifest SHA-256: `61b820965c39aa62e3f835d4f1137956744d576f3911a52979056d67348aa9c5`
- Linux x64 ZIP SHA-256: `5ab9db0683fe5edd9eb9e5ddb274155758512ce7c0f29d01bff32984b601005d`
- Linux x64 external manifest SHA-256: `65611934dc81ec38aec97ca8f99155277f98e56fcd6caa7332d6c1834a981335`

Both release archives and manifests were downloaded from the GitHub release. The ZIP and manifest hashes matched GitHub's published asset digests for Windows and Linux. The Windows manifest source SHA and `Deployment verify` command passed locally. The Linux archive was not executed locally.

The target must be a verified Platform-only artifact for the same version and RID. This operation supports `win-x64` and `linux-x64`; Complete RC1 has no supported ARM64 migration source. Ordinary `upgrade` continues to reject distribution changes.

## Operation and data policy

The CLI separates inspection from execution. Inspect the plan first; it reports source and target identities, target manifest SHA-256, storage estimate, preserved installation paths, configured external library roots, removed runtime components, and warnings. Execution requires that exact target manifest SHA-256 as confirmation:

```text
dotnet DELIVERY/deployment/Hispania.Deployment.dll plan-migrate-complete-rc1-to-platform-only ARTIFACT HOME
dotnet DELIVERY/deployment/Hispania.Deployment.dll migrate-complete-rc1-to-platform-only ARTIFACT HOME TARGET_MANIFEST_SHA256
```

The operation acquires the installation lock, requires a stopped Complete server, checks the maintenance lock, validates the official source and target manifests, binds installed metadata to the source manifest, validates the production configuration and configured library roots, checks available space, stages and re-verifies the target manifest against the inspected SHA, creates and verifies the existing Phase 17 SQLite state backup, then switches `app` while retaining `app.old`. It writes migration details to `run/last-upgrade.json`. Repeating the same operation against that recorded target verifies and returns the original backup rather than creating another one.

The operation does not move or purge `config`, `secrets`, `state`, `logs`, `backups`, or configured external library roots. The entire `state` tree is included in the pre-migration backup. Configuration and secret files remain in place; they are not copied into the backup. Media/Inventory runtimes are removed from the target package while their files and database state remain on disk for a future compatible module. Platform-only does not mount those stores or expose the domain endpoints.

To restore after the new host has started, stop it and restore the verified bundle through the retained Complete RC1 app:

```text
dotnet DELIVERY/deployment/Hispania.Deployment.dll rollback-restore HOME BUNDLE
```

For an interruption before the new host starts, inspect the operation marker and retained directories, then use the established `recover-before-start` procedure. Do not start old binaries against state after the new host has started; use restore-backed rollback.

## Explicit blocks and limits

- **PostgreSQL:** preflight and execution reject it before creating staging, marker, or backup. Platform-only currently rejects PostgreSQL at startup, and there is no tested cross-provider migration or rollback contract. Do not migrate a PostgreSQL Complete RC1 installation with this operation. Supporting it requires a Platform-only PostgreSQL runtime and disposable PostgreSQL migration/restore evidence.
- **Service-level overrides:** the plan reads `HOME/config/production.json`; it cannot inspect every systemd unit, Windows service environment, remote worker, or operator-managed environment file. Review those references and stop related workers before use. The CLI cannot prove there are no outstanding remote/module-owned jobs.
- **Media database rows:** the end-to-end fixture proved preservation of an external media-root file and a populated Inventory SQLite record. Complete RC1 did not initialize a Media catalog database in that fixture, so a populated Media catalog migration has not been demonstrated here. The state backup retains the full state tree, but that is not a substitute for a populated Media data fixture and restore test.
- **Final acceptance:** the CLI performs an offline binary switch and returns the backup path. It does not start the service, prove readiness, authenticate with an existing account, exercise persistence through the new host, or mark operator acceptance complete. Those checks were performed by the local fixture test only. A production operator must separately check/start the service and verify a known account before accepting the migration.
- **Failure injection:** the fixture covered corrupt target rejection, active-server rejection, pre-switch PostgreSQL rejection, ordinary-upgrade rejection, idempotent repeat, and restore rollback after the new host started. It did not inject process termination at each marker/switch/metadata write boundary or test concurrent migration processes.

These limits keep the operation fail-closed, but they do not satisfy all Step 2.1-V acceptance cases. In particular, a PostgreSQL installation is refused rather than migrated, and populated Media data, Linux execution, and per-boundary recovery remain unverified.

## Local validation evidence

Environment: Windows x64, .NET SDK `10.0.401`; temporary fixture only. No user installation, Coordinator, production database, service, or runner was touched.

- Official Windows x64 RC1 archive: SHA-256 `5a83527edc208552372f8452bfbf1037e0fa761b7e13b6c6ae9a13512e2a7eaf`; extracted artifact verification passed.
- Candidate Platform-only Windows x64 artifact built from working-tree source SHA `62b5f63daed9bb6c19a886e0f7c2df7d7e94129c`, `sourceDirty=true`; manifest SHA-256 `8e3b205335012fcd7e64b3d660057b83f664ea1da69cb3b9b9d32ca582c1acd4`. It was a disposable test artifact, not a release candidate.
- `dotnet test tests/deployment-process/Hispania.Deployment.ProcessTests.csproj -c Release --filter FullyQualifiedName~OfficialCompleteRc1MigratesToPlatformOnlyAndCanRestoreTheCompleteFixture`: **1 passed, 0 failed, 0 skipped** (31 seconds).
- `apps/deployment/Hispania.Deployment.csproj` and the process-test project compiled during that run.
- No GitHub Actions workflow was added or dispatched; Linux x64 and PostgreSQL CI remain outstanding.
- `git diff --check` is required before any later commit.

## Step 2.1 decision

Step 2.1-V remains **PARTIAL**. There is a tested Windows x64 SQLite path from the official Complete RC1 binaries to a local Platform-only artifact. There is not yet an officially accepted route for PostgreSQL Complete RC1 installations, nor sufficient CI/data/recovery evidence to close the requested migration scope. No commit or push was made. Step 2.1-W and Step 3 were not started.
