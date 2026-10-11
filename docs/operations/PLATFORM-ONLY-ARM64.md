# Platform-only ARM64 validation status

## Scope

HISPANIA's framework-dependent Platform-only working-tree artifact was exercised natively on Debian 13 AArch64 with ASP.NET Core Runtime 10.0.12. The Pi 3B+ ran the real manual Actions smoke in [run 38138089991](https://github.com/joaquimcastilla/hispania/actions/runs/38138089991). The Pi 4B ran an equivalent isolated staging smoke over SSH; its workflow is proposed separately and has not run in GitHub Actions.

Both smokes checked architecture/runtime, health/readiness, disposable identity/bootstrap/login, authenticated Platform endpoints, SQLite persistence across process restart and clean shutdown. The Pi 3 check also verified Media/Inventory routes were absent and no Media/Inventory/FFmpeg artifact references were present. Pi 4 staging did not access Coordinator configuration, data, logs or service controls.

## Artifact identity and known limitation

These are working-tree artifacts, not release artifacts from a clean commit. Pi 3's archive SHA-256 is `fc47a9aa2f928eb290fd40ced1b9b6a173f6ffe87e60f13ba5eac467b5a24c3d`; Pi 4's staged archive SHA-256 is `b484edf386c203539708004cba8ad424db7dd52aee8da29bbbe70817c3f077b4`. Both include `Microsoft.Extensions.Hosting.WindowsServices.dll` and its dependency metadata. Native Linux execution succeeded, but the package still fails the stricter validator requirement that excludes this dependency. Rebuild a clean artifact and revalidate before claiming that exclusion.

## Production limits

This lab evidence does not establish an ARM64 production installation lifecycle. `ProductionInstallation.Load`, production bootstrap/configuration/secrets, service provisioning, upgrade, backup and rollback remain pending for a Platform-only ARM64 host. Module install/activation was not tested because no signed fixture was provisioned. Production job execution was not tested because the Platform-only publication has no product handler registry. Worker registration, mTLS, leases and a direct Data Plane were not validated. Do not interpret endpoint inventory as proof of worker connectivity.

See [ARM64-CI.md](ARM64-CI.md) for runner hardening, hashes, metrics and operating procedures, and [ARM64-LAB.md](ARM64-LAB.md) for node/service inventory.
