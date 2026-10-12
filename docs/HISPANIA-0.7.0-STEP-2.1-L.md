# HISPANIA v0.7.0 — Step 2.1-L

**Result: PARTIAL.** The Windows native-service harness now binds each test to a fresh run ID and verifies the registered executable, installation home, role, and service account before lifecycle operations. A disposable PostgreSQL 16 runner and candidate-only ARM64 workflow are prepared. No SCM service was created, no PostgreSQL instance was available, no Raspberry Pi command ran, and no GitHub workflow was dispatched. No push, merge, tag, release, production deployment, Coordinator change, or Step 3 work occurred.

## A. Windows SCM — READY/BLOCKED

`NativeServiceTests` creates a GUID run ID and uses it in both its isolated installation directory and service name (`Hispania-Test-{RunId}-Server|Worker`). It proves that the name is absent before installation. The optional `windowsServiceName` configuration property is restricted to SCM-safe characters; its default remains the existing production service name. The service script checks role and configuration, rejects mismatched names, and verifies the SCM `PathName` and account before stop, restart, status, or deletion. The elevated CI helper also proves that the service name run ID matches the fixture directory run ID.

The native service cycle was not run because this Windows session is not elevated. The no-admin preflight regression rejected malformed and cross-run service names before querying SCM. No existing service was changed. In particular, the pre-existing GitHub Actions runner service was left intact. An administrator-authorized run is still required to claim L1 PASS.

The SCM script still provisions only the complete Windows host under `LocalService`; Linux uses its separate systemd installer. Platform-only candidate artifacts must not contain WindowsServices. The old pre-provisioned ARM64 working-tree archive is historical and may contain that assembly; the candidate job builds a fresh artifact and rejects the dependency before install. This distinction and the Windows installation constraints remain recorded in the [Step 2.1 evidence](HISPANIA-0.7.0-STEP-2-1.md) and [ARM64 operations note](operations/PLATFORM-ONLY-ARM64.md).

## B. PostgreSQL — READY/BLOCKED

No PostgreSQL server/client binaries, Docker, or Podman were present. WSL has no registered Linux distribution. The new `tools/ci/run-isolated-postgres.ps1` accepts an already-installed PostgreSQL 16 `bin` directory, creates an ACL-restricted temporary cluster with a random password and random loopback port, runs the existing library-provider and Phase 17 integration tests, then stops and removes only its uniquely named directory. It does not install PostgreSQL or connect to a remote server.

The PostgreSQL tests were not run against a live server. Supply a verified PostgreSQL 16 binary directory to the script to complete L2.

## C. Raspberry Pi — BLOCKED

Pi 4B and Pi 3B+ remain separate blocked results. Earlier diagnostics matched both SSH host keys against the existing `known_hosts` entries and found TCP/22 reachable; no local private key or running agent identity was available. An attempt to use the supplied password through the local SSH askpass path was blocked by the execution policy before SSH ran, so the password was not transmitted and neither Pi received a command. No host key, password, SSH configuration, runner, or device state was changed.

There is no native ARM64 result for this commit. Resume only after an approved local SSH identity or an allowed authentication path is available; retain strict host-key verification. Check Pi 3B+ throttling before transfer or execution.

## D. ARM64 CI — READY/BLOCKED

The existing `.github/workflows/arm64-platform-integration.yml` now has an optional candidate dispatch input and a separate candidate job. The candidate job requires the official repository, authorized event and triggering actor, exact local branch name, and input SHA equal to `github.sha`; it checks out that SHA without persisted credentials, builds the ARM64 artifact on the Pi 4B runner, verifies the clean-source manifest and file hashes, computes the archive SHA-256, and passes the artifact to the isolated integration script. It has `contents: read`, no secret references, and no PR trigger. The legacy `main` job and its pinned pre-provisioned inputs remain unchanged.

Candidate integration stages only under the dedicated CI staging tree. It validates the artifact manifest, rejects WindowsServices, Media, Inventory, and FFmpeg dependencies, provisions a temporary Platform-only installation, bootstraps a test administrator, checks readiness/authentication/SQLite/restart, verifies Coordinator service state, records metrics, and removes its run directory. Workflow cleanup removes the exact run-specific build/archive paths from runner temp.

`actionlint` passed for all workflows and Bash syntax parsing passed for the ARM64 integration script. The workflow was not dispatched because this branch remains local and push is not authorized. Once published, select this branch in the existing workflow-dispatch UI and provide its full commit SHA as `candidate_sha`.

## Validation and Git

- Deployment configuration suite: 34 passed, including safe service-name override validation.
- Full Server and Worker Agent builds: passed with zero warnings/errors.
- Playback endpoint safety regression: 3 passed. Deployment-process test project: compiled with zero warnings/errors; native SCM tests were not executed.
- `actionlint`: all workflows passed.
- PowerShell parser: all changed PowerShell scripts passed syntax parsing.
- Windows service preflight: malformed and mismatched run IDs rejected before SCM access.
- Bash parser: ARM64 integration script passed `bash -n`.
- PostgreSQL runner rejected a missing-binaries preflight before creating a temp directory; no server was started.
- Live SCM, live PostgreSQL, SSH-to-Pi, and remote Actions execution: not performed due the blockers above.

L1, L2, and L3 remain incomplete; L4 is statically prepared but not remotely executed. Step 2.1 therefore remains PARTIAL.
