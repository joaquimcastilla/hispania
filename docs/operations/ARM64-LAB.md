# HISPANIA ARM64 lab inventory and operations

**Last audited:** 2026-10-11. These Raspberry Pi checks use a pre-provisioned working-tree artifact and do not certify the ARM64 production installer or stable release.

## Nodes and responsibilities

| Node | System | Runner and role |
|---|---|---|
| Pi 3B+ `HispaniaWorker`, `192.168.1.135` | Debian 13.7, AArch64, 1 GB class, ASP.NET Core Runtime 10.0.12 | `hispania-arm64-ci`, runner v2.338.0; light compatibility and smoke jobs |
| Pi 4B `Hispania`, `192.168.1.143` | Debian 13.7, kernel `6.18.50+rpt-rpi-v8`, AArch64, four Cortex-A72 cores, 7.6 GiB RAM, ASP.NET Core Runtime 10.0.12 | `hispania-arm64-integration`, runner v2.338.0; staging integration jobs |

Pi 4 initial inventory: root filesystem 117 GiB with 108 GiB free; 2 GiB swap; `vcgencmd get_throttled=0x0`. Pi 3 previously and during its Actions smoke reported `0x50005`; this is a power/undervoltage warning and was not treated as an application failure. Both workloads were bounded; neither Pi ran a build or stress test.

## Pi 4 Coordinator and runner isolation

The existing `hispania-platform.service` remains disabled and inactive after its lab validation. It runs as `hispania-service`, with `MemoryMax=2G`, `CPUQuota=300%`, and `TasksMax=128`; its configuration, data, logs and release are separate from CI. The integration runner runs as `hispania-integration-runner`, has no sudo/docker membership, and cannot read Coordinator configuration/data/logs or write its release/data. Its systemd service is `actions.runner.joaquimcastilla-hispania.hispania-arm64-integration.service`, with `MemoryMax=3G`, `CPUQuota=200%`, `TasksMax=128`, and one job maximum. Runner credentials are mode `0600` and logs use journald.

Coordinator paths: `/opt/hispania/current`, `/opt/hispania/releases`, `/etc/hispania`, `/var/lib/hispania`, `/var/log/hispania`. Integration paths: `/opt/hispania-actions-runner`, `/var/lib/hispania-integration-runner/_work`, `/opt/hispania-ci-integration/{artifacts,scripts,tests}`, `/var/lib/hispania-ci-integration/{staging,test-data,results}`. Only integration-owned staging, test data, work and report directories are writable by the runner.

## Evidence and limits

The Pi 3 Actions run [38138089991](https://github.com/joaquimcastilla/hispania/actions/runs/38138089991) passed Platform health/readiness, authentication, SQLite persistence across restart and clean shutdown. Its report and metrics are in [ARM64-CI.md](ARM64-CI.md).

The Pi 4 SSH smoke passed the same categories on isolated staging and preserved Coordinator state. It measured first readiness 8,007 ms, restart readiness 5,356 ms, RSS 121,328 KiB, CPU sample 96.9% during active requests, 7.55 GiB available RAM, 108 GiB disk free and 37.972 °C. Five SQLite files and login persisted across restart; zero error-like server-log lines were counted. The staging user could not access the Pi's throttling device, so the run-level throttling value is unavailable; the administrator inventory before the run read `0x0`.

The tested working-tree artifact includes `Microsoft.Extensions.Hosting.WindowsServices.dll` and its dependency entry. Linux execution passing does not resolve the stricter WindowsServices exclusion. Module lifecycle, executable production job handlers, worker mTLS, installation/upgrade/rollback and production bootstrap are not validated. No Data Plane or production deployment claim is made.
