# HISPANIA v1.0 — PHASE 1 SLICE 1 REPORT

## A. STATUS

**PASS** — bounded baseline established. 83 selected tests passed (29 Platform/Inventory/storage, 35 Deployment/Hosting, 19 Runtime/module infrastructure); no selected test failed or skipped. Scope was documentation plus isolated local tests.

## B. ROADMAP STATUS

`[██████░░░░░░░░░░░░░░] 4/14 — 28.6%` remains unchanged throughout Phase 1. Phase 1 is authorized; Slice 1 is complete for this prompt. Slice 2 remains unstarted. No milestone count was advanced.

## C. PREFLIGHT

- Worktree: `C:\Users\joaqu\Documents\HISPANIA-v1-baseline-consolidation`
- Branch: `codex/hispania-v1-baseline-consolidation`
- Initial HEAD: `e5bc8a232f1e46955f0f04ab874a3ccfe04bf12a` (expected)
- Initial status: clean; Step 0.3 owner acceptance commit and Freeze, Evidence Ledger, R3 plan, R5 report, rights/license records present.
- Upstream: none configured; no upstream invented.
- Preservation root: `C:\Users\joaqu\Documents\HISPANIA-Step-0.2-Preservation-20261015`; pre-run verification found 15/15 original worktrees at recorded branch/HEAD/status and 49/49 source plus 49/49 backup hashes matching. Five pre-existing dirty worktrees were not changed.

## D. PROTECTED CONTRACTS

See [Slice 1 Compatibility Baseline](HISPANIA-V1-PHASE1-SLICE1-COMPATIBILITY-BASELINE.md) §1 for the full project/path, boundary, behavior, test, risk and compatibility matrix. Covered Runtime/Module Manager, Identity, Authorization, Events, Jobs, SQLite persistence, Hosting, Deployment, Inventory integration and Storage. No contract was modified. Main risks are V1 schema/signature drift, auth weakening, lifecycle/data compatibility, provider coupling, and accidentally changing Server while composing Empty Core.

## E. TEST DISCOVERY

- Inventory/Platform/Storage: 29 selected cases discovered: five R5 Platform groups plus one Inventory integration and six Storage Provider cases.
- Deployment/Hosting: 35 cases discovered from the full project.
- Runtime/module infrastructure: 19 individually filtered safe cases discovered. Repository-root-dependent, conventional output layout and OOP process-launch tests were excluded with reasons in the baseline.
- Discovery logs and SHA-256 values are listed in the baseline §3. Historical 219/219 Runtime, 76/76 Phase 19 lifecycle and 2/2 backup are not current-HEAD claims.

## F. TEST EXECUTION

- Source HEAD for every run: `e5bc8a232f1e46955f0f04ab874a3ccfe04bf12a`.
- OS/runtime: Windows 11 x64 build 10.0.26200; SDK 10.0.401; MSBuild 18.9.11; .NET 10.0.12.
- Run root: `C:\Users\joaqu\AppData\Local\Temp\hispania-slice1-20261017-b4f290a3`.
- Results: Inventory 29/29; Deployment 35/35; Runtime subset 19/19. Total 83/83 passed, 0 failed, 0 skipped.
- TRX paths, SHA-256, exact test commands and log hashes: baseline §3. Restore was offline from the existing package cache and empty local feed. No external services or real data.

## G. COMPATIBILITY BASELINE

Created `HISPANIA-V1-PHASE1-SLICE1-COMPATIBILITY-BASELINE.md`. It records the matrix, suite selection, current results/evidence, historical distinctions, deferred work, rights/security/persistence/module limits, rollback and rules for Slices 2–10. It expressly requires future `apps/core` to be separate and not alter `apps/server`.

## H. RIGHTS AND LICENSE BOUNDARY

R5 owner local-development attestation and exact bounded package review were reused. The broad Inventory dependency graph was restored offline; no live PostgreSQL service was used. `-p:PlatformOnly=true` prevented legacy demo asset copying. No `bp48-demo.mp4` or any of the five unknown-rights DLL hashes was found in generated artifacts. Excluded historical inputs remain in place. No distribution permission is inferred. `LICENSE.md` unchanged at SHA-256 `75EAE29E043C82B168894AFAAA148ADE2E9A43B4A72513FF18CFD22434F9BD3D`.

## I. DEFERRED VALIDATION

Full Runtime 219/219, Phase 19 76/76 and backup 2/2 were not rerun. Nine Deployment Process cases remain blocked by exact historical predecessor artifacts and/or privileged service-manager environments (see Evidence Ledger). OOP process launch, native SCM/systemd, Linux execution, ARM64/Pi, real distributed services, PostgreSQL, GPU and hardware are deferred to the corresponding authorized phases. No selected test failure is treated as historical or waived.

## J. VALIDATION

`git diff --check`: PASS. Diff paths are documentation only. No production, `apps/server`, contract, test, license or asset files changed. No secrets added. Generated outputs remain outside the repository. No unknown-rights binary was incorporated into Core. No push; no destructive Git command; no Slice 2 started.

## K. INTEGRITY

Preservation status: 15/15 original worktrees and 49/49 source plus 49/49 backup hashes intact as verified in preflight. Original dirty states remained as recorded. `LICENSE.md` hash unchanged. No preservation artifact was edited or deleted.

## L. COMMIT

A documentation-only commit was created after verifying the initial worktree was clean. See final Git summary for SHA and message. Included only the baseline, this report and Evidence Ledger update.

## M. FINAL STATE

Branch remains `codex/hispania-v1-baseline-consolidation`, no upstream configured, no push. Final worktree clean after commit. HEAD differs from initial only by the documentation commit. Phase 1 remains at 4/14; Slice 2 is not started.

## N. NEXT ACTION

**SLICE 1 PASS — AWAIT NEXT PROMPT.**
