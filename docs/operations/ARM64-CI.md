# HISPANIA ARM64 runner smoke workflow

This note covers the manual Platform-only smoke on the Raspberry Pi 3B+ runner and the separately proposed Pi 4B integration runner. It is infrastructure validation; it does not change production installation support or publish a release.

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

The successful SSH smoke on 2026-10-11 measured readiness at 14,304 ms and 12,001 ms after restart, 120,400 KiB RSS, 138% CPU while servicing requests, about 667 MiB host memory available, and 47.8 °C peak temperature. Five SQLite stores and login state persisted across restart. The runner remained online. `vcgencmd get_throttled` was `0x50005` before and after; no stress workload was run.

## Pi 3B+ GitHub Actions result

- PR [#17](https://github.com/joaquimcastilla/hispania/pull/17) merged to `main` on 2026-10-11 as `b2701aeb91144ea380f410ab298356677fdbd06f`. Its only files were the smoke workflow, its script and this operations note. `main` had no branch protection or ruleset at review time; maintain trusted write access and add repository rules as a separate governance action.
- Real manual run: [38138089991](https://github.com/joaquimcastilla/hispania/actions/runs/38138089991), commit `b2701aeb91144ea380f410ab298356677fdbd06f`, result **success**, duration about 1m23s. GitHub reports runner `hispania-arm64-ci`, runner version 2.338.0, machine `HispaniaWorker`; the job authenticated against the preinstalled script and artifact digests.
- The run verified artifact SHA-256 `fc47a9aa2f928eb290fd40ced1b9b6a173f6ffe87e60f13ba5eac467b5a24c3d`, AArch64 and ASP.NET Core Runtime 10. Health/readiness, platform endpoints, expected absent Media/Inventory routes, disposable authentication, five SQLite stores, login after restart and clean termination passed. Initial readiness was 18,313 ms; restart readiness 13,862 ms; RSS 121,116 KiB; active-request CPU sample 126%; temperature 33.786 °C; error-like log line count 0. These are one smoke's measurements, not benchmark baselines. The historical Pi 3 undervoltage/throttle value remains `0x50005`; it was not cleared by this run.

## Pi 4B integration runner (prepared; workflow PR not merged)

- Host `Hispania`, `192.168.1.143`, Debian 13.7, kernel `6.18.50+rpt-rpi-v8`, AArch64, four Cortex-A72 cores. Initial sample: 7.6 GiB RAM, 7.4 GiB available, 117 GiB root filesystem with 108 GiB free, 2 GiB swap, ASP.NET Core Runtime 10.0.12, `vcgencmd get_throttled=0x0`.
- Coordinator unit `hispania-platform.service` remains present, disabled and inactive. It runs as `hispania-service`, has `MemoryMax=2G`, `CPUQuota=300%`, `TasksMax=128`, reads `/etc/hispania/hispania.env`, and writes under `/var/lib/hispania` and `/var/log/hispania`. CI does not stop, start, reconfigure or use these directories.
- New runner name/labels: `hispania-arm64-integration`, `[self-hosted, Linux, ARM64, hispania-arm64-integration]`; account `hispania-integration-runner`, not a sudo/docker member. Runner software is official `actions/runner` Linux ARM64 v2.338.0, verified against SHA-256 `628b4a7258487b80c1d3c221095a7ded349f5ae0175fd3dfab708d07428f041b`. GitHub reports this runner online and idle.
- Systemd unit `actions.runner.joaquimcastilla-hispania.hispania-arm64-integration.service` runs as the dedicated account. Limits: `MemoryMax=3G`, `CPUQuota=200%`, `TasksMax=128`, one job. Journald stores service logs. Its runner credential files are mode `0600`; the short-lived registration token was not saved. The service has systemd filesystem/kernel hardening and writable paths limited to its own runner/work/staging/report directories.
- Coordinator state was checked before and after runner setup and staging smoke: `hispania-platform.service` remains disabled/inactive, `MemoryMax=2G`, `CPUQuota=300%`, `TasksMax=128`; runner cannot read `/etc/hispania/hispania.env`, `/var/lib/hispania`, or `/var/log/hispania` and cannot write the release or data paths. No Docker or sudo group membership is granted.
- Directories: runner `/opt/hispania-actions-runner`; work `/var/lib/hispania-integration-runner/_work`; immutable script/artifact `/opt/hispania-ci-integration/{scripts,artifacts}`; test reports `/opt/hispania-ci-integration/tests`; writable staging/data/results `/var/lib/hispania-ci-integration/{staging,test-data,results}`. The artifact is a root-owned read-only tar of the installed working-tree publish directory; SHA-256 `b484edf386c203539708004cba8ad424db7dd52aee8da29bbbe70817c3f077b4`. It is not a clean-commit release artifact and contains the WindowsServices assembly/dependency noted above.
- SSH staging smoke **PASS** on 2026-10-11. The script verified artifact integrity, AArch64, runtime, SQLite AArch64, health/readiness, authenticated platform routes, disposable identity, five isolated SQLite stores across restart and clean shutdown. Initial readiness 8,007 ms; restart readiness 5,356 ms; RSS 121,328 KiB; active-request CPU sample 96.9%; memory available 7.55 GiB; root disk free 108 GiB; temperature 37.972 °C; error-like log lines 0. Coordinator stayed inactive before/after. Throttle state was `0x0` in the pre-test administrator inventory; the restricted runner user cannot read `/dev/vcio_gencmd`, so its smoke report records throttling as unavailable. No stress workload ran.
- The new integration workflow is manual-only, actor/ref gated, has empty GitHub token permissions, no checkout/actions/secrets/sudo/Docker/deployment, one job, fixed input hashes and a 20-minute timeout. The separate PR is open/pending review; the workflow is not on `main` and no GitHub Actions job has run on Pi 4. SSH staging evidence is not a GitHub Actions result.
- Only Platform health/readiness/authentication/persistence/restart tests are claimed. Module lifecycle lacks a signed fixture; production job execution lacks a handler registry; worker mTLS has not been exercised; production installation remains blocked on `ProductionInstallation.Load`. Do not mark these features passing.

## Validation and status

The Pi 3 script passed `bash -n`; PR #17's workflow passed actionlint and its real Actions job passed. The Pi 4 script passed `bash -n` and the workflow passed actionlint 1.7.12 with the custom self-hosted label allowlisted; its SSH staging smoke passed. `git diff --check` is part of the publication validation. Pi 4 Actions integration remains pending until the separate PR is reviewed/merged with further authorization and dispatched successfully by the permitted actor.
