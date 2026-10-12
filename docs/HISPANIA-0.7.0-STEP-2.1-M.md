# HISPANIA v0.7.0 — Step 2.1-M

**Result: PARTIAL.** Local Windows validation and the available Linux cross-publishes passed after fixing a stale Phase 18 gate count. Native SSH, Windows SCM, PostgreSQL, Linux execution, ARM64 hardware, and GitHub Actions remain unverified because their authorized credentials or execution environments were unavailable. No production service or data was changed; the branch was not pushed, merged, or released, and Step 3 was not started.

## Infrastructure access

Strict, key-only SSH attempts to `hispania@192.168.1.143`, `hispaniaworker@192.168.1.135`, and the recorded Ubuntu account `adminbejomedia@192.168.1.138` reached each known host key and returned `Permission denied (publickey,password)` (exit 255). The available `.ssh` directory contains only `known_hosts` files; there is no SSH config, private identity, or running agent. No password or alternate authentication path was attempted. Consequently, current OS/architecture/permissions, runner-to-host mapping, Pi 3B+ electrical state, and remote PostgreSQL availability could not be checked.

The local Windows process is not elevated. PostgreSQL 16 binaries and Docker/Podman are not installed, and WSL reports no registered distribution. No remote job was dispatched.

## Fix made

`tools/test-phase18-production-deployment.ps1` expected 28 configuration cases, but the current Phase 18 category contains 29, including the service-name override regression. The gate now requires 29 and records the same count. Before the correction, all 29 tests passed but the script incorrectly aborted on its stale expected count. After correction, the complete available script passed all configured gates.

## Local validation

| Gate | Result |
| --- | --- |
| Runtime Release | 219 passed, 0 skipped |
| Inventory / Jobs / Phase 17 SQLite and backup coverage | 144 passed, 1 PostgreSQL-only test skipped |
| Library persistence | 65 passed, 1 PostgreSQL-only test skipped |
| Deployment | 34 passed |
| Phase 18 configuration and secrets | 29 passed |
| Published complete Windows process scenarios | 10 passed |
| Phase 19 package distribution upgrade | 2 passed |
| Platform-only Windows x64 runtime smoke | PASS: health/readiness, bootstrap/login, Jobs, route inventory, SQLite persistence, graceful shutdown; excluded domain routes returned 404 |
| Platform-only Linux ARM64 cross-publish | PASS: manifest and ELF AArch64 SQLite checks; no native execution |
| Platform-only Linux x64 cross-publish | PASS: manifest/package verification; source marked dirty because the gate-count fix was present |
| Complete Linux x64 cross-publish | PASS: package verification; source marked dirty because the gate-count fix was present |
| Windows service name preflight | PASS: malformed and cross-run IDs rejected before SCM access |
| `actionlint` 1.7.12, ARM64 Bash syntax, changed PowerShell parsing, `git diff --check` | PASS |

The Windows Platform-only smoke measured 1,454 ms to readiness and 83,640,320 bytes RSS. Managed heap and CPU were not sampled. PostgreSQL-only integration tests were skipped by their explicit connection-string guard; no SQLite or mock result is being reported as PostgreSQL validation.

The complete Windows artifact used by the Phase 18/19 process gates was built in a fresh TEMP directory from clean commit `829878dd2e9447e1f71afefa6f19bccd36d3a582`; its ZIP SHA-256 was `d7422872aede3c03847399b290c05bc3644eafd977473019cd276b1cf7ef20cc`. Phase 17 predecessor manifest SHA was checked against `c3a827a4b828cff37785884e889689af7cc5fce2` before the compatibility gate.

Two earlier process-test invocations were not valid acceptance results: one ran without its mandatory publication/history fixtures, and another supplied a Platform-only artifact to tests that require a complete `Hispania.Server.dll` distribution. Those attempts exposed missing fixture requirements and artifact-type mismatch; the supported complete-artifact gate was then run and passed 29 + 10 + 2 tests. No product tests were disabled or altered.

## Workflow review

The candidate ARM64 workflow is dispatch-only, pins the official repository, actor, triggering actor, exact branch, and candidate SHA, checks the runner identity/architecture, checks out `github.sha` with persisted credentials disabled, grants only `contents: read`, and references no production secrets. It has no external pull-request execution path. Candidate staging and cleanup are run-specific; the integration script targets its isolated CI staging tree and checks the Coordinator service state without changing it. The candidate Platform-only manifest rejects WindowsServices and domain product components. `actionlint` passed locally, but the workflow was not dispatched.

## Remaining gates

- Real SCM install/start/restart/stop/uninstall: unavailable without an authorized elevated test runner; the existing Actions runner service was left untouched.
- PostgreSQL 16 live migrations, persistence, transaction/rollback, restart, backup/restore: unavailable without a disposable server.
- Pi 4B ARM64 execution and Pi 3B+ electrical preflight/smoke: unavailable because SSH authentication failed. No package was transferred and neither device received a command.
- Linux x64 native runtime and GitHub runner matrix: unavailable; cross-publish is not native execution or remote CI.

Step 2.1 remains **PARTIAL**. Publishing this branch for CI is a separate authorized action; the branch was not published here.
