# Phase 25 — Final Integration and 0.7.0 Release Readiness

## Decision

**Phase 25 final acceptance: PASS.** The accepted candidate source is `main` at `8817b912b8f1748977f9145942d2dc30ad492b8e`. The native Linux package workflow completed successfully. This closes the 25-phase roadmap and supplies the release evidence baseline for preparing `0.7.0-rc.1`.

The RC1 branch adds version metadata and release documentation after the accepted Phase 25 SHA. Therefore, the accepted artifact below is evidence for Phase 25 only. RC1 packages must be built from the final clean commit of the preparation branch; this page's historical Phase 25 manifests are not reusable RC1 checksums. There is no RC1 tag or published GitHub Release.

Distribution targets in this evidence are x64 Windows and x64 Linux. No ARM64, Raspberry Pi, macOS, or other target is claimed. No contract is Public Stable.

## Accepted Phase 25 Linux run

| Item | Result |
|---|---|
| Branch / SHA | `main` / `8817b912b8f1748977f9145942d2dc30ad492b8e` |
| Workflow | [Phase 25 Release Acceptance #38064897483](https://github.com/joaquimcastilla/hispania/actions/runs/38064897483) — SUCCESS |
| Runner | `hispania-linux-ci`, Ubuntu 24.04 x64 |
| Artifact | [phase25-linux-native-package, artifact 11674122162](https://github.com/joaquimcastilla/hispania/actions/runs/38064897483/artifacts/11674122162) |
| Package | `linux-x64`, `sourceDirty=false`, manifest source SHA equals accepted SHA |
| Toolchain | .NET SDK 10.0.401; ASP.NET Core Runtime 10.0 x64; Node 24.19.0 |
| Repeated publication | Manifest and package directory contents compared byte-for-byte on the same Linux build host |
| Native package check | Extracted/installed to an isolated temporary home; `--check` passed; process started outside checkout; HTTP 200; `identity.sqlite` created; cleanup completed |

The Linux artifact manifest and checksum are inside the accepted GitHub Actions artifact. They describe that exact Phase 25 commit and version `0.7.0`, not the later RC1 candidate.

## Phase 25 CI corrections

The first three workflow attempts exposed runner/environment integration issues; the final run above passed after these corrections:

| Run | Initial failure | Correction carried into the successful workflow |
|---|---|---|
| [#38055000115](https://github.com/joaquimcastilla/hispania/actions/runs/38055000115) | `setup-dotnet` tried to write `/usr/share/dotnet`, which the non-root self-hosted runner could not create. | Detect and reuse the pinned runner SDK/runtime when available; otherwise install the pinned SDK under the runner temporary directory and set a private `DOTNET_ROOT`. |
| [#38055691375](https://github.com/joaquimcastilla/hispania/actions/runs/38055691375) | The Linux package step ran the Web build without installing locked npm dependencies (`tsc: not found`). | Set up the pinned Node toolchain and run `npm ci` from the checked-in lockfile before publication. |
| [#38059068368](https://github.com/joaquimcastilla/hispania/actions/runs/38059068368) | The PowerShell install step assigned to `$home`, a built-in read-only variable, and stopped before installation. | Rename the local variable to `$installHome`; the native install/check/start/HTTP/state/cleanup sequence then completed. |

These were runner/workflow integration failures, not evidence of product behavior failing. They are retained rather than rewritten as successful attempts.

## Reused Windows and historical evidence

The Phase 25 implementation record retained the following Windows x64 local evidence for its recorded clean implementation SHA `9c7872e0e3e848fd820c3b8cb6641e2e50475a8e`, using .NET SDK 10.0.401:

- Two repeat publications produced matching package directory contents for `win-x64` (182 files) and cross-published `linux-x64` (181 files), with clean provenance at that SHA and the deployment verifier passing.
- Focused published-artifact/process integration passed 12/12 cases for artifact-only startup and both Web variants, persistence/restart, bootstrap and backup/restore, upgrade/recovery, uninstall data preservation, WorkerAgent mTLS and coordinator fencing.
- Existing Phase 24 Smoke seed `25001` passed as a 60-second wiring/recovery check.
- Earlier phase acceptance records provide selected evidence for compatibility, migrations, package trust/lifecycle, security, backup/restore, Web parity, worker fencing and domain behavior. Their scope stays tied to their own recorded commits and tests; Phase 25 did not rerun those suites.

The repeated Linux publication from Windows was cross-publication evidence only. Native Linux execution is provided by the successful Phase 25 run above. Historical package manifest hashes from the implementation record are intentionally omitted here as current RC checksums.

## Acceptance boundaries and open limits

| Area | Status at Phase 25 | Boundary |
|---|---|---|
| Clean Linux x64 package, provenance, manifest, checksum | PASS | Native Ubuntu 24.04 runner and exact accepted SHA. |
| Install, `--check`, run outside checkout, HTTP readiness, SQLite identity state, cleanup | PASS | Isolated runner temp home; no system service was installed. |
| Windows x64 distribution | Validated within recorded package/process scenarios | Does not certify Windows SCM service installation or long-term operation. |
| Media PostgreSQL | Optional provider; candidate-specific authenticated acceptance not recorded | Platform stores and coordinator remain SQLite-backed. |
| Phase 24 resilience campaigns | Four accepted Windows/Linux × Short/Soak campaigns on their recorded Phase 24 SHA | Synthetic Worker/coordinator behavior in the campaigns; Server RSS sampled, managed heap/GC not measured; Short/Soak had no active fault injection. No HA, exactly-once or physical power-loss guarantee follows. |
| WorkerAgent | Existing mTLS, authorization, lease and fencing evidence is reused | Does not validate all historical peer combinations or arbitrary hostile workers. |
| ARM64, Raspberry Pi, macOS, other architectures | NOT TESTED | No release support claim. |
| Native systemd/SCM operation and least-privilege service operation | NOT TESTED by Phase 25 acceptance | Templates and focused scripts exist; use disposable hosts for that evidence. |

Other product limits include a single SQLite coordinator, at-least-once Jobs/Events, no global atomic transaction/rollback across independent stores, no general hostile-code sandbox, Inventory not being a complete ERP, and no general promise that an old binary can open stores after migration. See [Phase 23](phase23-e2e-compatibility-upgrades.md), [Phase 24](phase24-operational-resilience.md), [security operations](security/SECURITY-OPERATIONS.md) and the [support matrix](SUPPORT-MATRIX.md).

## Distribution mechanism

`tools/publish-production.ps1` is the existing production publisher. It builds Modern and Classic Web assets, publishes Server, WorkerAgent, ModuleHost, FFmpeg Worker and Deployment for `win-x64` or `linux-x64`, copies the OS service templates, writes a per-file `artifact-manifest.json`, writes a SHA-256 sidecar for the manifest and invokes `Hispania.Deployment.dll verify`. The package is framework-dependent and needs the .NET 10 ASP.NET Core x64 runtime. FFmpeg and FFprobe are needed for applicable Media operations.

The manifest records distribution version, source SHA, dirty-tree state, RID, build configuration, SDK, runtime prerequisite and SHA-256 per published file. A manifest proves content inventory for its exact source and files. Repeated directory comparison on one host does not prove ZIP byte reproducibility or cross-platform build reproducibility. RC1 ZIPs, if prepared, must be checked against their manifests after extraction; their final archive hashes are separate checksums.

## Release gate and recordkeeping

RC1 preparation updates the distribution version source, builds both verified targets from one clean final preparation commit, verifies extraction and manifests, records new checksums/provenance, reviews the documentation and creates a PR against `main`. The existing manual Phase 25 acceptance workflow is allowlisted on `release/0.7.0-rc.1-preparation` to run that same bounded native Linux package check on the exact candidate commit. Its RC run result and artifact belong below once completed. Reuse Phase 25 and earlier phase evidence only within the boundaries above. Do not repeat Phase 1–24 suites or Phase 24 Soak solely for the documentation/version preparation. Do not create the `v0.7.0-rc.1` tag, publish a GitHub Release or upload Release assets without later explicit authorization.
