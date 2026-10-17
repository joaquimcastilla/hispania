# HISPANIA v1.0 — Step 0.3-R5 Rights and Validation Report

**Date:** 2026-10-17
**Starting branch:** `codex/hispania-v1-baseline-consolidation`
**Starting HEAD:** `c1360708c4f16e511d78377c57d2be361307ba08`
**Scope:** owner local-development attestation, exact selected Slice 1 rights/package review, and five bounded Platform test groups. Documentation and temporary build/test outputs only.
**Phase 1:** NOT AUTHORIZED; no implementation started.

## A. R5 disposition

**PASS — R5 bounded scope.** The owner supplied the requested local-development attestation. The exact five selected test groups were discovered and executed from the R5 starting HEAD: 22 passed, 0 failed, 0 skipped. The resolved 29-package graph was reviewed for local testing and recorded in the package-level CSV. This closes the bounded R4 validation/right-to-test prerequisite for this selected slice. It does not establish general title, rights in every repository file, redistribution authority, or test evidence for other suites.

**Step 0.3 technical readiness: READY FOR OWNER ACCEPTANCE.** Final Step 0.3 owner acceptance is still required. Phase 1 requires a separate express authorization after that acceptance.

## B. Owner attestation and interpretation

The owner stated: “Soy el autor o tengo autorización para usar y desarrollar localmente el código principal de HISPANIA.” The scope recorded here is internal local use/development of the principal HISPANIA code, including the exact selected source/test compilation and execution evidenced below. This is an owner statement, not independent proof of title, a digital signature, legal opinion, or blanket assertion that the owner authored or controls every file.

Third-party package terms were separately reviewed for this bounded local test use. The statement does not clear the six unknown-rights artifacts, unrelated dependencies/assets, public or third-party distribution, relicensing, product release, or future inputs not covered by the evidence. `LICENSE.md` was not changed.

## C. Exact test selection and result

Test project: `tests/inventory/Hispania.Inventory.Tests.csproj`.

| Group | Test class |
|---|---|
| Identity | `PlatformIdentityTests` |
| Authorization | `PlatformAuthorizationTests` |
| Durable events | `DurablePlatformEventTests` |
| Jobs | `PlatformJobsTests` |
| SQLite Platform persistence | `PlatformPersistenceTests` |

Filter used for both discovery and execution:

```text
FullyQualifiedName~PlatformIdentityTests|FullyQualifiedName~PlatformAuthorizationTests|FullyQualifiedName~DurablePlatformEventTests|FullyQualifiedName~PlatformJobsTests|FullyQualifiedName~PlatformPersistenceTests
```

The isolated discovery and execution used `-p:PlatformOnly=true`, `--no-restore`, and `--artifacts-path` under `%TEMP%`. Restore was performed first using an empty local source, the existing global package cache, and `NuGetAudit=false`; no package download was necessary. No tests were added or modified. The five class groups discovered 22 test cases and execution reported **22 passed, 0 failed, 0 skipped**. TRX: `%TEMP%\hispania-r5-clean-4614fe1f083a4b30a881f25257805001\results\platform-slice1.trx`, SHA-256 `440444796034EECB2839ABC192BB739E245E3D0D426ED874E654D23AE3625604`.

Environment: Windows 11 x64, .NET SDK 10.0.401, runtime 10.0.12. The effective project graph contained 31 HISPANIA project libraries and 29 NuGet package libraries. The project graph is materially broader than the selected test filter and compiles existing project references; no service, external process, or live PostgreSQL instance was started by these selected test cases. `PlatformOnly=true` was used to keep the unknown demo MP4 out of build outputs.

## D. Package and third-party review

All 29 resolved NuGet packages are listed with exact versions, direct/transitive scope, declared/upstream license, evidence note, and source link in [HISPANIA-V1-R5-SLICE1-PACKAGE-LICENSES.csv](HISPANIA-V1-R5-SLICE1-PACKAGE-LICENSES.csv). Exact cached nuspecs/license metadata were inspected. The licenses found are MIT, Apache-2.0, and PostgreSQL License. SQLitePCLRaw's managed/native wrapper packages declare Apache-2.0; SQLite itself states its core source code is public domain. Npgsql and its EF provider are present transitively through a project reference, but no live PostgreSQL service was used and their presence does not imply PostgreSQL Core support.

`xunit.abstractions` 2.0.3 has no `<license>` expression in its package nuspec. NuGet's package License Info links to the xUnit source license, which is Apache-2.0. This evidence is sufficient for this private local test scope; retain the metadata caveat and verify the exact package/license notice again before any distribution. This review is not legal advice or a redistribution compliance certification.

## E. Unknown-rights artifacts

The six artifacts in [HISPANIA-V1-UNKNOWN-RIGHTS-COMPONENTS.csv](HISPANIA-V1-UNKNOWN-RIGHTS-COMPONENTS.csv) remain excluded from Empty Core under D4. The selected classes do not invoke the historical compatibility suite. The final clean build used `PlatformOnly=true`; the MP4 was absent from the final temporary build output. The earlier exploratory no-property run copied the MP4 into a temporary server output, but did not load or execute it; that incidental copy was not included in the clean final run. The R5 report does not clear any of the six artifacts for other uses.

## F. Historical test claim

R5 independently verifies the exact five named classes at the identified current starting HEAD. It does not recover the original historical 22/22 command, source SHA, or TRX. EV-032's historical provenance therefore remains unknown; its numeric total now has a separate, reproducible current-HEAD test result in EV-040. The R4 report remains accurate as a record of R4, when discovery/execution had not been performed.

## G. Rust future option

The owner's mention of Rust is recorded only as a possible future option. It does not change the frozen v1.0 C#/.NET architecture, authorize a rewrite, alter public contracts, or begin implementation. Any later language change needs a separate owner decision and compatibility/migration/maintenance evidence. Carried-forward code retains its rights/terms review obligations; exact new Rust crates need their own provenance and license review.

## H. Readiness gates

| Gate | R5 result |
|---|---|
| Owner local-development attestation | **RECORDED — BOUNDED** |
| Exact selected Slice 1 package license review | **PASS FOR LOCAL TESTING; distribution unapproved** |
| Five selected test groups | **PASS — 22/22** |
| Technical Step 0.3 readiness | **READY FOR OWNER ACCEPTANCE** |
| Final Step 0.3 owner acceptance | **PENDING** |
| Separate Phase 1 authorization | **NOT GRANTED** |
| Public distribution/relicensing/release | **NOT AUTHORIZED** |

No owner decision 9.x or 0.3-D1–D6 is changed by this validation result. No other test suites, hardware support, release gates, or legal conclusions are inferred.

## I. Exact repository changes

Documentation only: this R5 report; R5 package license CSV; R5 addenda to the Phase 0 Architecture Freeze, Readiness Blockers, and Evidence Ledger; and the bounded R5 updates to the Contributor Rights Register. No production code, test source, contracts, runtime behavior, lockfiles, package manifests, license files, assets, binaries, or historical compatibility artifacts were modified.

## J. Reproducibility and temporary artifacts

Run root: `%TEMP%\hispania-r5-clean-4614fe1f083a4b30a881f25257805001`. Restore, discovery, build outputs, test results, and logs were directed there. Restore used the existing NuGet global package cache and an empty local feed. The result TRX is identified in §C. The first exploratory run without `PlatformOnly=true` is not the final clean run and its incidental MP4 copy was limited to temporary build output.

## K. Integrity checks

Initial worktree was clean at the starting HEAD. The initial `LICENSE.md` SHA-256 recorded by the preflight was `75EAE29E043C82B168894AFAAA148ADE2E9A43B4A72513FF18CFD22434F9BD3D`. R5 is restricted to documentation; final `git diff --check`, changed-path review, license hash/status, preservation inventory, and post-commit cleanliness are recorded in the completion summary.

## L. Git and preservation

Initial branch was `codex/hispania-v1-baseline-consolidation`; initial HEAD was `c1360708c4f16e511d78377c57d2be361307ba08`; no upstream was configured. Do not push or invent an upstream. The preflight listed 15 other/original worktrees and 49 preserved source/backup files; final preservation verification is a required closeout check. A thematic documentation-only commit may be created only if its staged paths are exactly the R5 documentation files.

## M. Final source state

Record the final HEAD and commit in the task completion response after documentation checks. No merge, force-push, tag, release, or Phase 1 work is authorized or performed. `LICENSE.md` remains unchanged.

## N. Next action

Present this bounded evidence for the owner's **final Step 0.3 acceptance**. Do not start Phase 1 until that acceptance and a separate express Phase 1 authorization are received. Public distribution remains separately gated under D6.
