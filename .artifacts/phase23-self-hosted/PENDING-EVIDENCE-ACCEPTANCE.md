# Phase 23 — Pending Evidence & CI Acceptance

## State

This report distinguishes local portable evidence from authenticated native CI.
The Phase 23 worktree started clean at `be22bbe1e459547b587be2de661fda401635f222`
on `feature/phase-23-e2e-compatibility-upgrades`; Phase 22 base
`468f1ab87e242ed241d88fa8963aabace5b4ca52` is an ancestor. The local-only
validation changes recorded here are based on that SHA and are intended for the
same branch. No workflow Run ID has been obtained yet.

| Evidencia | Estado | Sistema | Run/Artifact | SHA |
|---|---|---|---|---|
| PostgreSQL live | BLOCKED | `hispania-linux-ci` required | Validation + Production Deployment; not dispatched | Pending final CI SHA |
| Windows SCM | BLOCKED | `hispania-win-ci` required | Production Deployment; not dispatched | Pending final CI SHA |
| Linux systemd | BLOCKED | `hispania-linux-ci` required | Production Deployment; not dispatched | Pending final CI SHA |
| Published upgrade | BLOCKED | Windows/Linux self-hosted production jobs | Phase 18 current publish + archived Phase 17 predecessor gate; not dispatched | Pending final CI SHA |
| .NET coverage comparison | PASS | Local Windows | `.artifacts/phase23/full-solution-final/*.trx`; 13 projects | `be22bbe` plus this report's documented validation changes |
| Linux native | BLOCKED | `hispania-linux-ci` required | Validation; not dispatched | Pending final CI SHA |
| Mixed-version workers | UNTESTED | Historical artifact matrix | No complete authenticated historical WorkerAgent binary pairs are available in the Phase 23 artifact inventory | N/A |
| Physical power loss | UNTESTED | Physical power interruption | No dedicated safe power-control infrastructure; process crash/restart/store interruption are distinct tested cases | N/A |
| Build-only advisory | PASS | Local Windows | `.artifacts/phase23/security-final/npm-all-audit.json`, `npm-runtime-audit.json`; both 0 findings | `be22bbe` plus this report's documented validation changes |

## CI access and dispatch

The existing `Validation` and `Production Deployment` workflows previously
allowed only Phase 21/22 branches, and the shared preflight rejected Phase 23.
Their allowlists now include the exact Phase 23 branch. Validation retains its
Linux and Windows full test jobs; Production Deployment retains its real
password-auth PostgreSQL disaster/rotation gate, clean systemd and SCM installs,
the protected Phase 21 service brokers, and artifact upload. No runner labels,
service permissions, or test requirements were weakened. Both workflows still
require manual dispatch.

The GitHub Actions page for this repository returned 404 in the available
browser session and `gh` is not installed. Consequently no Run ID, URL, runner
identity, or remote result can be reported yet. No GitHub-hosted runner or local
simulation is counted as remote evidence. PostgreSQL and both native service
gates remain **BLOCKED** until their self-hosted runs complete on the final SHA.

## .NET coverage: 878 versus 532

The stored Phase 23 full-dotnet TRX set contains five projects:

| Project | Passed | Skipped |
|---|---:|---:|
| Runtime | 210 | 0 |
| Compatibility | 50 | 0 |
| Deployment | 33 | 0 |
| Inventory | 131 | 1 |
| Playback | 108 | 0 |
| **Total** | **532** | **1** |

This corresponds to `Hispania.CrossPlatform.slnf`, not the Windows
`Hispania.sln` full-solution gate. The local Windows full-solution rerun selected
13 projects and passed **878**, skipped the two authenticated PostgreSQL cases,
and failed none. Eight additional projects account for the exact difference:

| Additional project | Passed | Skipped |
|---|---:|---:|
| Media Domain | 179 | 0 |
| Library Contracts | 18 | 0 |
| Library LocalFileSystem | 11 | 0 |
| Library FFprobe | 16 | 0 |
| Library Application | 22 | 0 |
| Library Persistence SQLite | 65 | 1 |
| Metadata TMDB | 30 | 0 |
| Windows DevLauncher | 5 | 0 |
| **Additional total** | **346** | **1** |

Thus `532 + 346 = 878` passed and the additional persistence project contributes
the second expected PostgreSQL skip. The first exploratory full-solution run
omitted `HISPANIA_PHASE19_OLD_READER`, so its historical-reader test correctly
failed its fixture-presence assertion; rerunning the full solution with the
verified independent reader from `.artifacts/phase23/phase19/historical-reader/`
passed that test and all 878 cases. No test was removed, skipped, or weakened.

## Published artifact provenance and compatibility limits

No complete historical Phase 18 published binary snapshot was present locally.
The existing production workflow creates the current artifact from the exact
workflow checkout and creates its compatibility predecessor from the immutable
Phase 17 source SHA `c3a827a4b828cff37785884e889689af7cc5fce2`; the predecessor
manifest records distribution `0.7.0`, the requested RID and file checksums.
That is an authentic source-pinned predecessor, not a claimed pre-existing
Phase 18 release binary. The protected production test harness exercises
pre-upgrade data, backup, install/update, startup and recovery when run. Until
those production jobs execute, the paired published-upgrade evidence remains
**BLOCKED**. Compatibility with unrepresented complete releases and WorkerAgent
binary pairs remains **UNTESTED**; no broad historical compatibility is inferred.

Physical power loss remains **UNTESTED** and outside the safe executable scope.
Existing process termination, restart, and store interruption evidence does not
establish physical power-loss behavior.

## Security

`source-map-js@1.2.1` was affected by GHSA-68fv-2mgg-jv7q. Version `1.2.2` is a
compatible patched release within the already-declared `^1.2.1` range. The lock
file update changed only this package's version, URL and integrity value;
`package.json` and the dependency graph did not change. After `npm ci`, both
`npm audit --omit=dev` and full `npm audit` report zero findings. Web tests and
Modern/Classic builds passed. The Phase 21 security suite passed 52/52, and its
updated dependency gate now rejects every npm finding; it no longer waives this
advisory. The prior `.artifacts/phase23` audit snapshots retain the pre-fix
finding as historical evidence; the final audit is under `security-final/`.

## Local command evidence

- Full Windows command `dotnet test Hispania.sln --no-build --no-restore -m:1 --results-directory .artifacts/phase23/full-solution-final --logger trx`, with `HISPANIA_PHASE19_OLD_READER` pointing to the verified `historical-reader/Hispania.HistoricalPackage.Reader.dll`: **878 passed, 2 expected PostgreSQL skips, 0 failed**.
- `pwsh -NoProfile -NonInteractive -File tools/test-phase21-security-hardening.ps1 -AuditDependencies -ResultsDirectory .artifacts/phase23/security-final`: **52/52**, no skips; NuGet, npm runtime and full npm audits clean.
- `npm ci`: completed, zero findings.
- `npm test`: passed (95/95 existing Web tests).
- `npm run build`: Modern and Classic passed.
- Existing same-source evidence remains as recorded in `.artifacts/phase23/`; it was not relabelled as remote acceptance.

## CI run ledger

| Workflow | Run ID | URL | SHA | Runner | Gate / result | Tests / skips |
|---|---|---|---|---|---|---|
| Validation | Not dispatched | — | — | — | BLOCKED: repository Actions page unavailable in current session | — |
| Production Deployment | Not dispatched | — | — | — | BLOCKED: repository Actions page unavailable in current session | — |
