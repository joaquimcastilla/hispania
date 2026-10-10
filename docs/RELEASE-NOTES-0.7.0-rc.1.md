# HISPANIA 0.7.0 RC1 — Release Notes

**Prepared release candidate; not yet published.** This is a pre-1.0 review build, not a stable release. The candidate version is `0.7.0-rc.1`; the intended tag is `v0.7.0-rc.1`. No tag, Release, or Release assets exist yet.

## Summary

HISPANIA 0.7.0 RC1 packages the integrated result of the completed 25-phase roadmap as a self-hosted modular application platform. It combines a shared Platform runtime with Media and Inventory domains, a Modern and Classic Web Shell, an offline deployment tool and optional distributed WorkerAgents.

This release candidate makes no new domain feature claim. It brings the accepted implementation baseline into a versioned, packaged and documented candidate for review.

## Available capabilities

- Host runtime with module lifecycle, supervision, health, package verification and policy-controlled extension activation.
- Persistent local identity, session authentication, RBAC, audit and administrative projections.
- Media library/catalog, metadata provider integration, search, Direct Play and FFmpeg transcoding paths.
- Durable Platform Events and Jobs, filesystem-backed opaque Assets/Storage and runtime observability.
- Optional WorkerAgents using mTLS, explicit node/capability authorization, leases, generations and stale-result fencing.
- Inventory domain proof with items, warehouses, locations, stock and movements; it is not a complete ERP.
- Modern and Classic Web Shells, including declarative experimental Web contributions with fixed authorized operations.
- Deployment CLI and operating guides for configuration, secrets, backup/restore, upgrades and rollback.

See [Architecture](ARCHITECTURE.md), [Support Matrix](SUPPORT-MATRIX.md), [Module System](MODULE_SYSTEM.md) and [operations](operations/PRODUCTION-QUICKSTART.md) for scope and limits.

## Architecture and extensibility

HISPANIA is a modular monolith by default. One Server process is the composition root for Platform and enabled domains; standalone WorkerAgents are optional. Platform contracts define boundaries, while each domain owns its behavior and persistence. The current coordinator is a single SQLite-backed instance.

`.hispania` packages are checked against the host's compatibility, integrity and trust policies. Signature enforcement depends on operator policy. In-process extensions run with Server privileges. `AssemblyLoadContext`, process separation and resource limits do not create a hostile-code sandbox. Dynamic Web contributions are declarative; packages do not supply arbitrary executable browser code.

## Platform compatibility

| RID | Validation status | Runtime |
|---|---|---|
| `win-x64` | Validated for the recorded package and process scenarios | .NET 10 ASP.NET Core Runtime x64 |
| `linux-x64` | Native Phase 25 package acceptance passed on Ubuntu 24.04 x64 | .NET 10 ASP.NET Core Runtime x64 |
| ARM64, Raspberry Pi, macOS, other targets | Not validated; no support claim | — |

The distribution is framework-dependent, not self-contained. FFmpeg and FFprobe are needed for Media operations that use them. PostgreSQL is optional for Media persistence only; Platform owner stores and distributed coordination remain SQLite-backed.

## Installation

Before installation, obtain the package from a trusted source and verify its SHA-256 manifest checksum independently. Install the .NET 10 ASP.NET Core x64 runtime. Use the deployment CLI from the extracted package:

```sh
dotnet /delivery/deployment/Hispania.Deployment.dll verify /delivery
dotnet /delivery/deployment/Hispania.Deployment.dll install /delivery /srv/hispania server
dotnet /srv/hispania/app/server/Hispania.Server.dll --home /srv/hispania --check
```

Configure `config/production.json`, provision secret references and bootstrap the first operator as described in the [Production Quick Start](operations/PRODUCTION-QUICKSTART.md). Confirm `/livez` and `/readyz` before using the Web interface. Native service templates are provided, but this RC's Phase 25 Linux acceptance did not install a systemd unit or a Windows SCM service.

## Upgrading

No earlier stable public release is designated as a general upgrade source. HISPANIA's compatibility and upgrade evidence covers named contracts, selected historical data fixtures and predecessor package scenarios; it does not certify arbitrary snapshots from every development phase. Check the [Phase 23 compatibility matrix](compatibility/PHASE-23-COMPATIBILITY-MATRIX.md) and [Upgrade runbook](operations/UPGRADE.md) for the exact source/target path before changing stores.

Make and verify a compatible offline backup, stop writers, and follow the owner-specific migration procedure. Restores across independent stores are not globally atomic. An old executable is not a safe rollback strategy after data migration; use the documented backup/restore path. See [Upgrade and Rollback](operations/UPGRADE-ROLLBACK.md).

## Changes from the prior distribution baseline

`0.7.0` was a development/distribution baseline, not a prior stable Release. RC1 identifies the same integrated 25-phase product line as `0.7.0-rc.1`, uses the existing production publication mechanism with fresh manifests and checksums tied to its own source commit, and consolidates the installation and support documentation. It does not renumber SDK, API, package, worker, event, job, or persistence contract generations.

## Security

- Configure package trust and signature requirements for the intended installation; signatures are not universally mandatory by default.
- Protect the Server, configuration, state, backups, secrets and worker certificates with OS permissions and network controls.
- In-process modules inherit Server privileges. Out-of-process execution and mTLS do not make hostile code or a compromised worker safe.
- The coordinator is single-instance; Jobs and Events provide at-least-once semantics, not exactly-once effects.
- Public errors and admin projections are bounded, but deployment-specific network exposure and secret provisioning remain operator responsibilities.

Read [Security Operations](security/SECURITY-OPERATIONS.md), [Security Configuration](security/SECURITY-CONFIGURATION.md) and [SECURITY.md](../SECURITY.md).

## Validation evidence

Phase 25 final Linux acceptance passed on `main` SHA `8817b912b8f1748977f9145942d2dc30ad492b8e`, using the native Ubuntu 24.04 x64 runner. The workflow built a clean `linux-x64` package, verified manifest provenance, repeated publication and compared package contents, installed the package in an isolated temporary home, ran `--check`, started outside the checkout, returned HTTP 200, created `identity.sqlite` and cleaned up. See [base workflow 38064897483 and artifact 11674122162](phase25-release-readiness.md). The final preparation-branch run is listed in the [Phase 25 workflow history](https://github.com/joaquimcastilla/hispania/actions/workflows/phase25-acceptance.yml).

Windows x64 and prior phase test evidence are reused within their recorded scope. The RC1 package artifacts must identify the final clean preparation commit; Phase 25 manifests and checksums from earlier SHAs are not RC1 checksums. Repeated content comparison demonstrates package-directory repeatability on one build host. It does not, on its own, establish ZIP byte reproducibility or cross-platform build reproducibility.

## Known limitations

- Phase 24 Short/Soak campaigns used synthetic Worker/coordinator behavior in the applicable workloads. They measured Server RSS, not Server managed heap/GC, and did not include active fault injection in Short/Soak. Their results do not prove high availability, exactly-once delivery or physical power-loss durability.
- The distributed coordinator is a single SQLite instance; multi-master and general distributed HA are not implemented.
- Jobs and Events are at-least-once. Cross-store work is not one transaction, and restore/rollback is owner-specific rather than globally atomic.
- No hostile-code sandbox exists. Process limits are resource controls, not an adversarial isolation guarantee.
- Native systemd/Windows SCM install and least-privilege service behavior were not part of Phase 25 acceptance.
- Candidate-specific authenticated PostgreSQL acceptance is not recorded. PostgreSQL remains an optional Media provider, not a universal Platform backend.
- Inventory is a limited domain proof, not a complete ERP.
- ARM64, Raspberry Pi, macOS and other architectures are not validated.

## Pending issues and reporting

Known evidence gaps are listed in the [Phase 25 record](phase25-release-readiness.md) and [Support Matrix](SUPPORT-MATRIX.md). No future capability is promised by these notes. Report defects through [GitHub Issues](https://github.com/joaquimcastilla/hispania/issues); report suspected vulnerabilities through the private route in [SECURITY.md](../SECURITY.md).
