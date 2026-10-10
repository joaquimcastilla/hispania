# Phase 25 — Final Integration and 0.7.0 Release Readiness

## Decision

**Candidate status: BLOCKED pending native Linux Phase 25 acceptance.** Phase 25 is a bounded distribution and packaging closure. Windows, Web, artifact, reproducibility and Smoke evidence already accepted for the implementation commit is retained; the final Linux package must still be built and installed on the native Linux self-hosted runner. This is an evidence gap, not a claim of a known product defect. Do not publish or tag a release from this branch.

Distribution identity remains **0.7.0 pre-1.0**. No architecture other than x64 Windows and x64 Linux is included. No contract is promoted to Public Stable.

## Integration state

| Reference | Commit | Relationship and state |
| --- | --- | --- |
| Phase 23 `feature/phase-23-e2e-compatibility-upgrades` | `4faf71323cc9ec121743e85b48c354bbaa784b6c` | Phase 23 acceptance-gate commit; ancestor of Phase 24 |
| Phase 24 `feature/phase-24-operational-resilience` | `f2c5798b2c3c973deacf2cad2e7cc6d89720c247` | Builds on Phase 23; official Short/Soak evidence is linked at [Actions run 38028676728](https://github.com/joaquimcastilla/hispania/actions/runs/38028676728) |
| Local `main` at inspection | `134076f5ba2894aba216e775c4588cc3c547ee31` | Ancestor of Phase 24; Phase 24 contains 86 commits beyond this point and is not merged into `main` |
| Remote `origin/main` at inspection | `1fb323da92e6f5c6e389f36a042282430fd011c5` | Nine commits beyond the common base; contains the authorized development resilience launcher absent from Phase 23/24 |
| Phase 25 | `feature/phase-25-final-integration-release-readiness` | Based on the clean Phase 24 commit; [draft PR #11](https://github.com/joaquimcastilla/hispania/pull/11) is the third review item in the stack |

Phase 23's accepted history is included in Phase 24 by ancestry. Phase 24's existing worktree had edits to `PROGRESS-LOG.md` and `docs/phase24-operational-resilience.md`, plus untracked `docs/PHASE-24-EVIDENCE.md`; these were preserved in that worktree and are not silently included in this branch. That evidence reports four PASS campaigns (Windows/Linux × Short/Soak), with no skipped workloads. It also records the campaign boundary: synthetic data and coordinator, Server RSS but no managed heap/GC, and no declared campaign fault injection. Those limits remain in force.

The integration is performed through the authorized stacked PR sequence #9, #10, #11. Review is limited to commit ancestry, branch dependencies, conflicts and preservation of the existing default-branch launcher; historical phase suites are not rerun.

The current `origin/main` has nine commits not in Phase 23/24, while Phase 23 and Phase 24 have 83 and 86 commits not in `origin/main`, respectively. Their only tree-level difference from `origin/main` is the already-merged `.github/workflows/authorized-development-smoke.yml`, which allowlists Phase 24 Short/Soak execution on the Linux/Windows self-hosted labels. Preserve and review that workflow during integration. PR #9 is the draft Phase 23 → `main`; PR #10 is the draft Phase 24 → Phase 23. Phase 25 is prepared as the third draft PR, Phase 25 → Phase 24. PRs #4–#8 are already merged runner-launcher updates. No branch has been merged, tagged, or released.

## Acceptance matrix

PASS means reproducible evidence exists for the named scope. Historical phase tests demonstrate only their named scope; the Phase 25 exact-commit workflow remains pending. When rerun on the Phase 25 commit, retain the TRX and artifact bundle and update this table with the observed result and run URL.

| Capability | Status | Evidence boundary / next evidence |
| --- | --- | --- |
| Install from a clean distribution package | PARTIAL | Existing Windows artifact-process evidence is retained. Phase 25 builds, verifies, installs and starts the Linux package natively. |
| Start outside the development repository | PARTIAL | Existing artifact-only process evidence covers this; Linux native package launch is added in Phase 25. |
| Create/migrate SQLite state and restart without losing committed state | PARTIAL | Historical SQLite owner and process-restart evidence is retained; Phase 25 checks installed Linux initialization and durable state creation. |
| Authenticated PostgreSQL configuration | NOT TESTED | PostgreSQL is an optional Media provider; no authenticated disposable database was supplied to this campaign. Do not use a personal database as a test target. |
| Load only authorized and compatible modules; reject invalid packages | PARTIAL | Phase 8/19/20/21 package trust, compatibility, signature and fail-closed tests exist. The Phase 25 workflow does not rerun the full historical security suite. |
| Recover from controlled errors | PARTIAL | Phase 24's four sustained campaigns and accepted Phase 25 Smoke evidence are retained; they did not declare injected faults. |
| Persist durable jobs/events and storage state | PARTIAL | Durable-owner tests and Phase 24 campaign checks exist; the campaign event/job payloads are synthetic. |
| Least-privilege service identity | NOT TESTED | Native service scripts and tests exist. Phase 25 deliberately does not install into a runner's service manager; use disposable service VMs for SCM/systemd evidence. |
| Run the Modern and Classic Web Shell from the artifact | PARTIAL | Accepted Windows Web build/test and artifact-process evidence is retained; no Phase 25 Web suite is repeated. |
| Register an authorized remote WorkerAgent over mTLS; lease/fence stale results | PARTIAL | Accepted real-process mTLS WorkerAgent and coordinator fencing evidence is retained. This does not establish cross-host Windows↔Linux behavior in this run. |
| Keep administrative endpoints protected and projections bounded | PARTIAL | Phase 21/22 focused authorization and safe-projection evidence exists; exact Phase 25 suite is pending. |
| Uninstall without deleting application data | PARTIAL | Deployment process tests assert data preservation; actual native service removal is intentionally deferred to disposable service VMs. |
| Upgrade/backup/restore/rollback | PARTIAL | Phase 17/18/23 owner-specific and artifact process tests exist. No global atomic rollback across independent stores is claimed. |
| Windows x64 and Linux x64 packages with manifest and SHA-256 | PARTIAL | Clean Windows package evidence is retained. Native Linux package manifest, verifier, installation and startup remain pending. |
| Reproducible build from a clean commit | PARTIAL | Windows and cross-published Linux repeat-build evidence is retained. Repeat-build evidence on the native Linux runner remains pending. |
| ARM64/Raspberry Pi or other architectures | NOT TESTED | No such target is published or claimed. |

## Local verification record

Windows x64, 2026-10-10, on the Phase 25 working tree:

- `npm ci`: passed; current dependency audit reported 0 vulnerabilities.
- `npm run build`: Modern and Classic builds passed.
- `npm test`: 95/95 passed.
- Production `win-x64` artifact: publisher completed and `Hispania.Deployment.dll verify` passed. The first local package records `sourceDirty=true`, so it was test input only and not a release artifact.
- Focused artifact/process integration: 12/12 passed using that package and the exact archived Phase 17 predecessor. Coverage includes artifact-only startup and both Web variants, persistence/restart, bootstrap and backup/restore, upgrade/recovery, uninstall data preservation, standalone WorkerAgent mTLS and coordinator fencing.
- `Phase24ResilienceTests`: 11/11 passed.
- Phase 24 Smoke, seed `25001`: PASS; owned Server process and isolated campaign data were cleaned up. This is a 60-second wiring/recovery check, not sustained stability or injected-fault evidence.

The exact clean Phase 25 commit is `9c7872e0e3e848fd820c3b8cb6641e2e50475a8e`. On Windows x64 with .NET SDK 10.0.401, each RID was published twice from this clean commit. All 182 `win-x64` files and all 181 `linux-x64` files matched byte-for-byte across the two builds, including `artifact-manifest.json`; both have `sourceDirty=false` and pass the deployment verifier. The Linux RID is cross-published here, so this is repeatability evidence on one build host, not a native Linux execution result.

| RID | Manifest SHA-256 | Repeated build |
| --- | --- | --- |
| `win-x64` | `d4bb7e83f415f56ff632d333dd4fcbe610a9dbe8fffd89c078f8fafb99265f72` | Byte-identical, 182 files |
| `linux-x64` | `39e5cfde77ccf718e02461a570b9f52b354e5e0a6b6f391f37640d7a4a9dfe5b` | Byte-identical, 181 files |

The clean Windows artifact passed all 12 artifact/process cases against the final publisher code (TRX `phase25-final-process.trx`). The Linux artifact was cross-published and checksum-verified on Windows; it was not executed on Linux.

The TRX files are under ignored `tests/deployment-process/TestResults/` and `tests/inventory/TestResults/`; final packages and campaign evidence are under ignored `.artifacts/phase25-final/` and `.artifacts/phase25/`. The initial exploratory process-test invocation omitted required artifact/predecessor environment variables and failed at fixture setup; the configured rerun above passed all 12 selected cases. No Linux execution, authenticated PostgreSQL, or native Windows service-manager install was performed locally. The Linux runner campaign is pending integration of the workflow into main.

The historical Phase 17 predecessor's archived Web development dependencies report one high-severity npm advisory; this is retained as historical compatibility input. The current Phase 25 Web dependency audit is separate and reported zero vulnerabilities. The candidate release summary is in [Draft 0.7.0 release notes](PHASE-0.7.0-RELEASE-NOTES-DRAFT.md).

## Final acceptance procedure

After the Phase 25 workflow is available on main through the authorized stacked integration, dispatch **Phase 25 Release Acceptance** once on main and retain the uploaded Linux artifact bundle. The workflow:

1. Requires only the existing Linux self-hosted labels `[self-hosted, Linux, X64, hispania, hispania-linux]`; no GitHub-hosted runner is used.
2. Builds and verifies `linux-x64` on Linux, requires exact clean-commit provenance, republishes it and compares package bytes.
3. Installs the package under a unique runner-temporary root, runs Server `--check`, starts it from outside the repository, checks HTTP readiness and confirms durable SQLite identity state is created.
4. Uses a 45-minute timeout and removes only its uniquely owned temporary installation after the process exits. It does not install or modify systemd services and does not connect to PostgreSQL.
5. Preserves package manifests, checksum sidecars and the acceptance artifact even when a job fails.
The previous Phase 24 Short/Soak evidence remains valid for the exact Phase 24 SHA; do not repeat those long campaigns solely for Phase 25. If Phase 25 changes a resilience workload or invariant, run the corresponding focused test. A full PostgreSQL-live and native service acceptance requires explicitly provisioned disposable targets and must remain NOT TESTED until then.

## Distribution and operations

The existing production publisher is `tools/publish-production.ps1`; it targets only `win-x64` and `linux-x64`, builds both Web Shells, publishes Server/Worker/ModuleHost/FFmpeg Worker/Deployment, writes `artifact-manifest.json`, produces a SHA-256 sidecar for that manifest, and calls the deployment verifier. The target needs the .NET 10 ASP.NET Core x64 runtime and the documented FFmpeg/FFprobe tools where Media requires them. The artifact is not self-contained.

Use [Production Quick Start](operations/PRODUCTION-QUICKSTART.md) for first install and bootstrap, [Secret Provisioning](operations/SECRET-PROVISIONING.md) for credentials, [Upgrade](operations/UPGRADE.md) and [Upgrade/Rollback](operations/UPGRADE-ROLLBACK.md) for migrations and recovery, and [Rollback](operations/ROLLBACK.md) for the recovery decision. Module developers should use the [Module System guide](MODULE_SYSTEM.md) and [Web Contribution Author Guide](DYNAMIC-WEB-AUTHOR-GUIDE.md). Historical scope is listed in the [Phase 23 compatibility matrix](compatibility/PHASE-23-COMPATIBILITY-MATRIX.md), while tested targets and platform limits are in the [Support Matrix](SUPPORT-MATRIX.md). These runbooks preserve state on uninstall and require explicit operator action to remove retained data. Backups do not imply cross-store atomicity.

Known limitations include single-instance SQLite distributed coordination, no hostile-code sandbox for in-process modules, optional package signatures depending on host policy, no exactly-once claim, no cross-store global rollback, no Server managed-heap/GC evidence from Phase 24, and no active-fault campaign in the accepted Short/Soak runs. See [Phase 23 limitations](phase23-e2e-compatibility-upgrades.md), [Phase 24 scope](phase24-operational-resilience.md), [security operations](security/SECURITY-OPERATIONS.md), and the [support matrix](SUPPORT-MATRIX.md).

## Release gate

Change candidate status only after the Phase 25 Linux workflow result is reviewed and its package identifies the exact clean commit. PostgreSQL and native service checks are not part of this phase's changed capabilities; preserve their current NOT TESTED status for release claims. Do not tag or publish a Release without authorization.
