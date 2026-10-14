# HISPANIA v0.7.0 — Step 2.1 Final Acceptance

**Audit date: 2026-10-15 (Europe/Madrid). Overall status: PARTIAL.** The validated Platform-only SQLite scope is technically well supported across Windows/Linux x64 and native ARM64 smoke. Contractual acceptance remains pending the owner's express PostgreSQL policy decision. This status is not general production approval.

## Executive decision

- **Technical readiness:** PASS for the tested Platform-only SQLite artifact and migration scope, with the per-platform limits and evidence age recorded below. The supported Complete Windows/Linux production deployment and real Windows SCM paths also have passing gates. Pi 3B+ telemetry and sustained-use readiness remain partial.
- **Contractual acceptance:** **PENDING; Step 2.1 remains PARTIAL.** The proposed v0.7.0 policy (Platform-only SQLite; Complete keeps PostgreSQL; RC1-to-Platform-only migration refuses PostgreSQL before writes) has fail-closed implementation and test evidence, but the user has not expressly approved the policy. Step 2.1-X says that approval is mandatory for global PASS.
- **Production deployment authorization:** **NOT GRANTED by this audit.** A technical PASS does not authorize touching a production installation, Coordinator, database, service, or release. No such system was changed here.
- **Step 3:** not started and not authorized.

No exception to the original acceptance criteria is inferred. V3 completed the previously missing SQLite RC1 migration coverage; W completed ZIP reproducibility for the stated RIDs. PostgreSQL scope is still a pending owner decision, not an approved exception.

## Candidate and traceability

- Repository: `joaquimcastilla/hispania`.
- Branch: `codex/hispania-0.7.0-step2-1-production-readiness`.
- Audited source HEAD: `e9303e6ec18251b708200a6f3cc9073bc04c9f4d` (W implementation was tested at `4f8ae676d7ad1203ec90014d356feb2530352160`; `e9303e6` adds W documentation only).
- Base: `origin/main` `05ad5c19c851d8651e57dba61992080193219742`.
- The candidate branch is 65 commits ahead of `origin/main`; this is accumulated Step 2/2.1 work, not the content of this acceptance document alone.
- At audit start, the only worktree change was this untracked report. No product/workflow source or other worktree was changed for this audit. Only this report is intended for the acceptance commit.

The original local report's historical findings are retained here as dated evidence, reconciled against the later V3, W, and X runs. The earlier V/V2/V3/W notes remain unchanged and provide implementation detail. Statements in older snapshots such as “migration unsupported” or “ZIP bytes differ” describe the state at their stated date and are superseded by the later results linked below.

### Evidence and source SHA index

| Area | Run and source SHA | OS / architecture and observed outcome | Current validity |
|---|---|---|---|
| General validation, full .NET suite, PostgreSQL 16, security and platform gates | [Validation 38322711067](https://github.com/joaquimcastilla/hispania/actions/runs/38322711067), `fefbf7cee7746940ce2117c431ff7888fe939c19` | GitHub-hosted Windows x64; 754 passed, 0 failed, 0 skipped. Dedicated PostgreSQL job also passed Phase 17/16 and live PostgreSQL migration/constraint integration against disposable PostgreSQL 16. | Reusable for unchanged contracts. It predates the dedicated migration implementation and W packager changes; those areas are covered by later targeted runs, not this full-suite count. |
| Linux and Windows production deployment / upgrade / rollback | [Production Deployment 38322731395](https://github.com/joaquimcastilla/hispania/actions/runs/38322731395), `fefbf7cee7746940ce2117c431ff7888fe939c19` | Linux x64 real systemd Server/Worker and Windows x64 self-hosted production gate passed. Install, lifecycle, upgrade and recovery scenarios passed. | Reusable for deployment logic not changed by V/W. W's current ZIP install/`--check` evidence is separately tested below. This run's `tar.gz` is not evidence of tarball reproducibility. |
| Native Pi 4B and Pi 3B+ | [ARM64 integration 38322713642](https://github.com/joaquimcastilla/hispania/actions/runs/38322713642), `fefbf7cee7746940ce2117c431ff7888fe939c19` | Pi 4B `aarch64`: native .NET 10 Platform-only smoke passed hash/manifest, readiness, authentication, APIs, SQLite integrity/restart, shutdown and cleanup. Pi 3B+: native lightweight smoke passed; throttling telemetry unavailable in that run. Cross-publish job is distinct from native jobs. | Reusable for unchanged ARM64 runtime path. Not evidence for Complete ARM64 runtime. Pi 3B+ remains partial for sustained use because power/throttling is unverified and prior flags were recorded. |
| Real Windows SCM | [Hosted SCM baseline 38615197576](https://github.com/joaquimcastilla/hispania/actions/runs/38615197576), `62b5f63daed9bb6c19a886e0f7c2df7d7e94129c`; [current-SHA repeat 38645517882](https://github.com/joaquimcastilla/hispania/actions/runs/38645517882), `e9303e6ec18251b708200a6f3cc9073bc04c9f4d` | Current-SHA GitHub-hosted Windows Server 2025 x64; real Server and Worker SCM lifecycle/recovery **2/2 passed**, cleanup passed; job 4m40s. The two self-hosted production jobs were skipped. | Direct on audited HEAD. The TRX artifact reports total 2, passed 2, failed 0, not executed 0. |
| RC1 → Platform-only migration (previous accepted matrix) | [V3 push 38638443047](https://github.com/joaquimcastilla/hispania/actions/runs/38638443047), [manual 38638891561](https://github.com/joaquimcastilla/hispania/actions/runs/38638891561), source `74f442123fec1aa37f71e29abaee90d5761e5fc4` | `windows-2025` and `ubuntu-24.04`; each required migration gate 1/1 passed, with Media/Inventory/external-root preservation, rollback and recovery evidence. | Direct for V3 implementation; packager subsequently changed in W, so the new current-SHA repeat below is the relevant updated-package evidence. |
| RC1 → Platform-only migration using W package generation | [X repeat 38644966170](https://github.com/joaquimcastilla/hispania/actions/runs/38644966170), `e9303e6ec18251b708200a6f3cc9073bc04c9f4d` | `windows-2025` 3m29s and `ubuntu-24.04` 2m23s; each run artifact's `required-test-summary.txt` says `Passed; count=1; skipped=0`. Official RC1 ZIP and manifest digests were checked before extraction. | Direct on audited HEAD; validates migration against the current publisher/package output. Tests use temporary fixture homes, not production. |
| Deterministic ZIP packaging | [Phase 25 W 38643530263](https://github.com/joaquimcastilla/hispania/actions/runs/38643530263), `4f8ae676d7ad1203ec90014d356feb2530352160` | Three hosted jobs passed on `windows-2025` (`win-x64`), `ubuntu-24.04` (`linux-x64`), and `ubuntu-24.04` (`linux-arm64` cross-publish). Independent builds matched at tree, manifest and ZIP-byte levels. | Direct for W implementation; audited HEAD adds documentation only after the tested W implementation. ARM64 row is cross-publish/package validation, not native execution. |
| Current-SHA hosted SCM rerun | [X SCM repeat 38645517882](https://github.com/joaquimcastilla/hispania/actions/runs/38645517882), `e9303e6ec18251b708200a6f3cc9073bc04c9f4d` | GitHub-hosted Windows SCM job passed; 2/2 tests, cleanup passed. Self-hosted production jobs skipped. | Direct on audited HEAD. |

### Changes after older evidence

The common general-validation source `fefbf7c` predates V migration code, Windows hosted SCM helpers, and W deterministic packaging. Evidence is not promoted wholesale to exact-HEAD evidence. Current targeted coverage closes those changed areas: V3/X runs exercise migration at current publisher output; the hosted SCM rerun covers current Windows package publication; W checks independent output trees and ZIPs on all three RIDs. W changed the deterministic ZIP tool, both publishers, reproducibility verifier, Phase 25 workflow, and package tests. Since accepted W implementation SHA `4f8ae67`, only W documentation changed through `e9303e6`.

No full 754-test suite was repeated after W. The changed areas were covered by the focused 13-test ZIP suite on each hosted OS, the three-RID publication/install matrix, the current-SHA two-platform migration gate, and the current-SHA hosted SCM gate. The remaining unchanged full-suite contracts are reused from `fefbf7c`; this is not a claim that 754 tests ran at `e9303e6`.

## Acceptance matrix

| Area / criterion | Status | Evidence and limit | Effect |
|---|---|---|---|
| Core independence from Media and Inventory | **PASS** | Platform-only dependency-boundary gate in Validation 38322711067; current product contracts are unchanged by V/W. Platform-only artifact rejects domain dependencies and routes. | Meets tested Core/Platform boundary. |
| Platform-only publication and startup | **PASS** | Pi native ARM64 smoke 38322713642; current ZIP extracted-install/manifest/`--check` in W 38643530263 for Linux/Windows x64; X migration repeat 38644966170 publishes and executes current Platform-only target in fixtures. | Supported SQLite scope demonstrated. |
| Linux x64 build/install/runtime | **PASS** | Deployment 38322731395 and W 38643530263. W's extracted Platform-only ZIP install and Server `--check` pass; Linux systemd lifecycle and rollback passed on the earlier unaffected deployment implementation. | No native Linux host process gate was repeated after W, but current ZIP install was. |
| Windows x64 build/install/runtime | **PASS** | Deployment 38322731395, W 38643530263, current-SHA hosted Windows SCM 38645517882. | Real Server/Worker SCM acceptance and cleanup passed on exact audited HEAD. |
| Linux ARM64 artifact | **PASS for cross-publish; PASS for Platform-only native smoke** | W 38643530263 validates deterministic `linux-arm64` package/manifest using host-native Deployment verifier; Pi 4 run 38322713642 executes Platform-only artifact natively. | These are distinct claims. No Complete ARM64 runtime acceptance. |
| Raspberry Pi 4B | **PASS for brief smoke** | Native `aarch64`, .NET 10, manifest/hash, readiness, authenticated API, SQLite persistence/restart and clean shutdown passed in 38322713642. Coordinator inactive before/after. | Brief smoke and resource sample, not soak/capacity certification. |
| Raspberry Pi 3B+ | **PARTIAL** | Native lightweight smoke passed in 38322713642; earlier measured `get_throttled=0x50005`. Latest Actions telemetry could not read throttling state. Historical sample: 45.084 °C, about 588 MiB available, 14.288 s first readiness, 14.930 s restart readiness. | Do not claim sustained-use power/thermal readiness until current telemetry is available and healthy. |
| SQLite | **PASS for tested paths** | Pi SQLite integrity and persisted authentication/restart; migration fixture verifies user, Inventory, populated Media catalog, metadata, state backup and restored rows/integrity on Windows/Linux. | Does not establish all operator/database backup procedures. |
| PostgreSQL | **PARTIAL — contract decision pending** | Complete PostgreSQL 16 disposable migration/constraint and backup/restore gates passed in 38322711067. Platform-only is SQLite-only; Complete RC1 migration refuses PostgreSQL in preflight, before staging/marker/backup; V3/X exercises the fail-closed path. | Owner has not expressly approved the proposed v0.7.0 exclusion policy. Mandatory contractual blocker; no global PASS. |
| Deployment and update | **PASS for exercised paths** | Linux systemd and Windows production deployment/update/rollback run 38322731395; Windows SCM real lifecycle separately. W current ZIP extracted install and `--check` for x64. | Not blanket authorization to deploy an installation. |
| Rollback / disaster recovery | **PASS for exercised fixtures** | Deployment 38322731395 tests same-RID recovery; V3/X migration tests backup-backed rollback, pre-start recovery and injected failure checkpoints. | Migration scope is official Complete RC1 SQLite x64 to Platform-only x64, same RID; no PostgreSQL conversion or ARM64 RC1 migration. |
| Windows SCM | **PASS** | 38645517882: Windows Server 2025, exact audited SHA; actual service install/start/readiness/recovery/restart/stop/uninstall, 2/2; cleanup passed. Both self-hosted deployment jobs skipped. | Current-SHA evidence. |
| RC1 migration | **PASS for supported SQLite scope** | Current-SHA run 38644966170 across Windows/Linux, 1/1 each; official source Complete RC1 `9b5abefe4c6b631360d505587748aedcb6c522d0`, verified ZIP and manifest SHA, temporary HOME, current Platform-only target, persisted data and restore. | PostgreSQL intentionally refuses; final operator service startup/acceptance is outside migration CLI. |
| Media/Inventory preservation | **PASS for populated fixture** | V2/V3 and current X migration fixture verifies populated Inventory SQLite and Media catalog rows/metadata, external library file, user/auth, secrets canary and rollback restoration. | Media/Inventory remain stored but are not mounted/exposed by Platform-only. No populated production data used. |
| ZIP byte reproducibility | **PASS for tested ZIP RIDs** | W run 38643530263: `linux-x64` 186 files, ZIP `bab00e7630adf76aef358968270f79ccf5d170bec8dc7fbcd635bd2cc0f33411`, manifest `db9eedd924a71ef4e9f323bae28b6e69af58f3a7854dac6b1d024895340c60e0`; `win-x64` 187 files, ZIP `e833a3b75180dd6ebe8560cd30ff08887c3c9212407900194320702d7cd77686`, manifest `b7e5545cb0db5b0616179c8d96f84aa84f7943c7d0d2fe98df512f496d6c6c3b`; `linux-arm64` 186 files, ZIP `1b10801757ce2a3f229c50b405b607c0380c12957bd30308fdda706bcad39bc6`, manifest `55e9c9b0eb3bd79e87b162652780f45e7789efe9c89c6d270673f2c91295f8ff`. Each A/B pair matched byte-for-byte. | Same hosted OS per RID; no cross-host-OS equality claim. `tar.gz` reproducibility remains untested. |
| Manifests / integrity | **PASS** | W validates manifest A/B equality, sidecar checksums, extracted tree equality and artifact verification; current X migration revalidates official RC1 hashes and generated target manifest. | Hashes establish integrity/identity, not publisher trust by themselves. |
| Security | **PASS with residual hardening items** | Workflows use `contents: read`, trusted repo/actor/trigger/ref/SHA guards, exact SHA checkout, `persist-credentials: false`, no PR execution route in privileged gates; dedicated migration workflow has no secrets; tests use temporary fixtures; manifests and package paths are verified. Validation scans 165 captured outputs for canaries. | See residual risks; remote console logs were not included in that 165-file scan, and actions are tag-referenced rather than immutable SHA-pinned. |
| Modules / Platform jobs | **PASS for available contracts and tested lifecycle** | `IPlatformJobContributionV1`, manifest declarations, catalog lifecycle and dispatch tests exist; Validation covers Platform boundary and targeted suites passed in recorded runs. Production starts with an empty handler registry until an authorized module contributes a handler. | Trusted/authorized module code and module-provided production jobs are not enabled by default; OOP is not a hostile-code sandbox. |
| CI | **PASS for linked gates** | Current-SHA migration run; W three-RID deterministic matrix; current-SHA hosted SCM run. Older full suite and deployment jobs link above. Candidate-only workflows have actor/ref guards and limited permissions. | No claim that all runs tested identical SHAs; per-gate source validity is explicit. |
| Documentation | **PASS as consolidated evidence index** | This report supersedes stale status summaries while preserving their linked run and design records. V/V2/V3/W docs retain details and dated decisions. | Older phase reports are historical snapshots, not current status. |

## Security and operational review

### Controls observed

- Privileged or candidate-only GitHub workflows are dispatch/candidate scoped, verify official repository, authorized actor and triggering actor, branch and exact `github.sha`, and grant only `contents: read`. They check out the requested SHA with credential persistence disabled. No external `pull_request` path runs the privileged SCM/migration fixtures.
- The current migration workflow downloads the official RC1 archive and external manifests, verifies fixed SHA-256 values before extraction/execution, uses same-RID x64 fixtures, and retains logs/TRX/hashes. It never invokes a production host. The test script's destructive cleanup is constrained to its explicit unique result directory and named generated children.
- W's ZIP packer sorts paths, normalizes metadata, includes empty directories, and rejects unsafe paths, case collisions, links/reparse points and output paths inside the source. Sidecar checksums are written after archive finalization. The Windows hosted SCM helper confines install/cleanup to a unique run fixture and rejects unexpected/mismatched service identity before acting.
- Test isolation corrections from J/K explicitly root SQLite stores, caches, transfer roots, metadata and library roots under unique temporary fixtures and verify containment. Prior monitored tests compared `%LOCALAPPDATA%\Hispania` metadata before/after; no production file contents were read or removed.
- Phase 17 backup verification and rollback preserve config/secrets separately and state in a verified bundle. Migration leaves `config`, `secrets`, `state`, `logs`, `backups`, and configured external roots in place; it retains old binaries and does not purge those stores.
- No production secrets are mapped to the migration or hosted SCM jobs. The general Validation PostgreSQL integration uses disposable test credentials; they are not production credentials. No Coordinator, real installation, persistent runner, release or production service was operated during this audit.

### Residual risks and limits

| Severity | Residual |
|---|---|
| **High — contractual** | Owner approval of the fail-closed PostgreSQL policy is outstanding. Until recorded expressly, Step 2.1 cannot be globally accepted. |
| **Medium — operations** | Pi 3B+ has historical undervoltage/throttling flags (`0x50005`) and current runner telemetry is unavailable. A short smoke does not clear sustained-use risk. |
| **Low — supply chain** | GitHub Actions references use moving version tags, not immutable commit SHAs. The resolved action SHAs are present in run logs, but pinning would reduce tag movement risk. |
| **Low — evidence scope** | The 165-file canary scan did not include remote Actions console logs. Same-OS reproducibility is proved per RID, not across different host operating systems. Linux `.tar.gz` reproducibility and long-duration soak/capacity evidence are absent. |
| **Compatibility boundary** | Complete Windows retains `Microsoft.Extensions.Hosting.WindowsServices` 10.0.0. Platform-only suppresses it and has no Windows SCM installer. Installing Windows services requires the complete Windows host and elevated operator context. Complete ARM64 cross-publish does not prove WindowsServices or Complete ARM64 runtime support. |
| **Product scope** | Platform-only does not mount Media/Inventory, so data preservation does not imply their APIs or functionality are available. PostgreSQL-to-SQLite conversion is not provided. Module authorization and production mTLS provisioning remain operator/configuration contracts and were not established by ZIP packaging. |

The former blocking items “RC1 migration unsupported” and “ZIP hashes differ” are resolved only within their stated scopes by V3/X and W. The earlier local report's Windows SCM gap is superseded by current-SHA hosted evidence. Pi 3 telemetry and PostgreSQL contractual approval remain open.

## Contract, decision and conditions

The proposed PostgreSQL policy remains **pending express owner approval**. No approval or exception was found in the trusted history reviewed for X. The implementation remains fail-closed and rejects PostgreSQL migration before any mutation. Do not change this behavior to obtain a green acceptance.

**Overall Step 2.1: PARTIAL.** Functional and platform evidence is strong for the tested Platform-only SQLite scope, but the mandatory contract decision is unresolved and Pi 3B+ sustained-use telemetry remains incomplete. No critical product/security defect was demonstrated by the audited evidence. The accepted runs do not confer permission for a production deployment or stable release.

Before claiming overall PASS:

1. Record the owner's explicit approval or rejection of the proposed PostgreSQL policy. If approved, keep the existing fail-closed policy for PostgreSQL migrations; if rejected, obtain a separately scoped contract/implementation decision.
2. Before sustained Pi 3B+ deployment, obtain current reliable power/throttling telemetry and resolve any undervoltage flags.
3. If release acceptance requires cross-OS ZIP equality, tarball reproducibility, or a soak/capacity target, define and run those explicit gates; present evidence does not imply them.

Production deployment authorization is separate and must remain explicit. Step 3 remains outside scope.

## Audit actions and Git record

- Current-SHA migration regression dispatched at `e9303e6` to `windows-2025` and `ubuntu-24.04`; both jobs passed and uploaded artifacts. No source or production system was changed by the run.
- Current-SHA Windows hosted SCM-only workflow passed at `e9303e6` (4m40s, 2/2 tests, cleanup passed); the persistent `linux-production` and `windows-production` jobs were skipped.
- No merge, force-push, tag, release, Coordinator action, production data/service change, or Step 3 work was performed.
- This consolidated report replaces the previous untracked acceptance snapshot as the current evidence index. The V/V2/V3/W reports and older phase reports remain intact as dated supporting records.
- Final commit and remote candidate SHA are recorded in the audit response after validation and normal push. No merge to `main` is authorized.
