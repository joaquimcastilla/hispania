# HISPANIA ARM64 runner smoke workflow

This note covers the manual Platform-only smoke on the Raspberry Pi 3B+ runner. It is infrastructure validation; it does not change production installation support or publish a release.

## Runner and trigger

- Repository: `joaquimcastilla/hispania`; runner: `hispania-arm64-ci` on `192.168.1.135`.
- Runner service: `actions.runner.joaquimcastilla-hispania.hispania-arm64-ci.service`, account `hispania-runner`, no sudo membership. Existing systemd limits remain `MemoryMax=600M`, `CPUQuota=200%`, and `TasksMax=128`.
- The workflow is `workflow_dispatch` only. The job condition requires both actor `joaquimcastilla` and ref `refs/heads/main`; other branches/actors are skipped. It uses labels `[self-hosted, Linux, ARM64, hispania-arm64]`, `timeout-minutes: 15`, concurrency one, and `permissions: {}`.
- There is no `pull_request`/`pull_request_target` trigger, no checkout, no action dependency, no production secret, no sudo, no Docker, no deployment, and no build. The job invokes a root-owned, read-only script already installed on the Pi. Public repository write access must stay restricted to trusted maintainers, and branch protection/review on `main` should remain enabled. A workflow-level actor condition is not a substitute for repository access policy.

GitHub documents that public repository pull requests can expose self-hosted runner hosts to untrusted code and that users with repository write access can trigger manual workflows by default. Keep PR code away from this runner and keep the `main` branch and workflow execution permissions under trusted control: [runner security guidance](https://docs.github.com/en/actions/how-tos/manage-runners/self-hosted-runners/manage-access), [manual workflow execution](https://docs.github.com/en/actions/how-tos/manage-workflow-runs/manually-run-a-workflow).

## Fixed, pre-provisioned inputs

The Pi 3 archive is installed at:

```text
/opt/hispania-ci/artifacts/hispania-platform-linux-arm64-step2-working-tree-20261011.tar.gz
```

Expected SHA-256:

```text
fc47a9aa2f928eb290fd40ced1b9b6a173f6ffe87e60f13ba5eac467b5a24c3d
```

The installed file was checked directly on the Pi 3B+: size 1,732,861 bytes, owner `root:root`, mode `0444`, and SHA-256 exactly as above. The same archive was transferred from the Pi 4B publish directory; it contains 46 files / 4,007,121 extracted bytes. It is a working-tree artifact, not a release artifact tied to a clean source commit.

The installed script is `/opt/hispania-ci/scripts/arm64-platform-smoke.sh`, owner `root:root`, mode `0444`, SHA-256 `b3e3d0d74be88e22992cdd3885a5afe6d3f434b6c3d5cd3dbb709b0c2dbc35ec`. The workflow verifies that exact script digest and the script separately verifies the archive digest before extraction. Do not replace either input without reviewing the new source, regenerating the artifact, and updating both expected digests.

The package has an ARM64 `libe_sqlite3.so` (ELF AArch64, SHA-256 `707fff6b18c1f083158e7a543c8d2545d5485f547ffd96db29679a95f52878d5`) with dependencies resolving on Debian 13 ARM64. The package has no Media or Inventory assemblies and no FFmpeg file/reference in the server dependency manifest.

### Windows Services dependency in this artifact

This exact working-tree archive also contains `Microsoft.Extensions.Hosting.WindowsServices.dll` and references `Microsoft.Extensions.Hosting.WindowsServices/10.0.0` in `Hispania.Platform.Server.deps.json`. The Pi 3 Linux smoke completed with this dependency present, but this does not prove the package is free of Windows-specific host dependencies or satisfy the stricter artifact validator's WindowsServices exclusion. This CI workflow documents and tests the installed artifact as-is; it does not suppress that finding or assert the exclusion. Rebuild a clean Platform-only ARM64 package and validate its file/dependency manifests before claiming that condition is fixed. Keep this correction separate from the CI-only change unless the user authorizes Step 2 changes.

## Smoke behavior and resource bounds

The script runs as `hispania-runner`, binds a dynamically selected loopback-only port, extracts to a unique temporary folder, and uses fresh data under `/var/lib/hispania-ci/platform-test`. It checks AArch64, ASP.NET Core Runtime 10, archive SHA-256, SQLite architecture/dependencies, forbidden Media/Inventory/FFmpeg entries, readiness/health, platform routes, absent Media/Inventory routes, disposable identity bootstrap/login, SQLite files and login after restart, and clean SIGTERM shutdown. It records readiness time, RSS, CPU sample, thermal-sensor reading, logs and error-line count under `/opt/hispania-ci/tests`, then removes only its own temporary test directory. Random test tokens/passwords are process-local and are not written to the repository or report.

The script stops if available memory drops below 150,000 KiB or the readable thermal sensor exceeds 75,000 m°C. HTTP calls have a 60-second timeout to accommodate ARMv8 password hashing; the overall job has a 15-minute timeout. The smoke is deliberately light and is not a stress, soak, idle-CPU, or managed-heap test.

Reproduce from an authorized administrator session:

```sh
sudo -u hispania-runner env \
  HISPANIA_ARTIFACT_SHA256=FC47A9AA2F928EB290FD40CED1B9B6A173F6FFE87E60F13BA5EAC467B5A24C3D \
  bash /opt/hispania-ci/scripts/arm64-platform-smoke.sh
```

The successful SSH smoke on 2026-10-11 measured readiness at 14,304 ms and 12,001 ms after restart, 120,400 KiB RSS, 138% CPU while servicing requests, about 667 MiB host memory available, and 47.8 °C peak temperature. Five SQLite stores and login state persisted across restart. The runner remained online. `vcgencmd get_throttled` was `0x50005` before and after; no stress workload was run. These measurements are SSH/hardware evidence, not a GitHub Actions result.

## Validation and status

The local script was checked with `bash -n`; the manual workflow passed `actionlint`. The smoke script ran successfully as the runner user over SSH. No GitHub Actions job is claimed until the workflow has been merged to `main` and dispatched successfully by the permitted actor. The current pull request carries only the workflow, script, and this operations note.
