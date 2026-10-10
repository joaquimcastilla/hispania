# HISPANIA

**HISPANIA is a self-hosted, modular application platform for media and business workloads.** Its shared runtime supplies identity, authorization, extensions, storage, events, jobs, administration and distributed workers. Media and Inventory are domain suites built on those platform contracts.

HISPANIA is a modular monolith by default. A single Server process composes the enabled domains; separate WorkerAgent processes are optional. Operators keep control of their installation, data and trust policy.

## Release candidate status

The current candidate is **0.7.0-rc.1**, a pre-1.0 release candidate prepared for review. The 25-phase roadmap is complete and Phase 25 acceptance passed incrementally. Native `linux-x64` package acceptance passed on the exact accepted `main` SHA; Windows x64 distribution and process evidence is retained from the earlier acceptance work. See the [Phase 25 evidence and release readiness record](docs/phase25-release-readiness.md).

| Target | Candidate status | Boundary |
|---|---|---|
| Windows x64 (`win-x64`) | Validated for distribution and selected process scenarios | Windows service-manager operation and least-privilege installation are not certified by Phase 25. |
| Linux x64 (`linux-x64`) | Native package accepted | Accepted on Ubuntu 24.04 x64; Phase 25 did not install a systemd service. |
| ARM64, Raspberry Pi, macOS, other targets | Not validated | No support claim is made. |

RC1 is not a stable release. No tag or GitHub Release has been created. Public contracts remain pre-1.0; no subsystem or contract has been promoted to stable.

## Architecture

```text
Server host (composition root)
├── Host and runtime: lifecycle, supervision, health and configuration
├── Platform: identity/RBAC, audit, events, jobs, assets/storage, admin and observability
├── Media suite: library, metadata, playback, streaming and FFmpeg provider
├── Inventory suite: items, warehouses, locations, stock and movements
├── Web Shell: Modern and Classic interfaces
└── Optional WorkerAgents: authorized capabilities over mTLS
```

Versioned contracts define subsystem boundaries. Platform contracts do not depend on Media or Inventory. The Server composes cross-domain behavior. `.hispania` packages are verified against host policy and may contain trusted native modules. Experimental Web contributions are declarative and use fixed authorized operations.

WorkerAgents run approved capabilities on separate processes or hosts. The coordinator uses a single SQLite instance. mTLS authenticates workers; leases and generations fence stale results. These controls do not provide multi-master high availability or a hostile-code sandbox. In-process modules inherit Server privileges, and out-of-process resource limits are not a security boundary for hostile code.

## Included capabilities

- Persistent local Identity, sessions and role-based authorization.
- Media library, metadata enrichment, search and Direct Play; FFmpeg transcoding can run locally or through an authorized worker.
- Inventory as a limited non-media domain proof, not a complete ERP.
- SQLite-backed Platform owner stores, with an optional PostgreSQL provider for Media persistence.
- Durable Events and Jobs, opaque Assets with filesystem storage, audit records and runtime observability.
- Modern and Classic Web Shells and administrative views.
- Offline installation, configuration, backup/restore and upgrade/rollback tools.

See [Architecture](docs/ARCHITECTURE.md), [Module System](docs/MODULE_SYSTEM.md), [Support Matrix](docs/SUPPORT-MATRIX.md) and the [project context](PROJECT_CONTEXT.md) for implementation boundaries and limitations.

## Install and start

The published applications target Windows x64 and Linux x64 and require the **.NET 10 ASP.NET Core x64 runtime**. They are framework-dependent, not self-contained. Media operations that analyze or transcode files need the documented FFmpeg/FFprobe tools. Verify the delivered package against its separately obtained manifest checksum before installation.

The production publisher is `tools/publish-production.ps1`. For an already prepared distribution, verify and install using the included deployment CLI:

```sh
dotnet /delivery/deployment/Hispania.Deployment.dll verify /delivery
dotnet /delivery/deployment/Hispania.Deployment.dll install /delivery /srv/hispania server
dotnet /srv/hispania/app/server/Hispania.Server.dll --home /srv/hispania --check
```

Configure `/srv/hispania/config/production.json`, provision the first operator through the documented local `--bootstrap` flow, then start the Server. The default example endpoint is `http://127.0.0.1:5080`; confirm `/livez` and `/readyz` before using the Web Shell. The full [Production Quick Start](docs/operations/PRODUCTION-QUICKSTART.md) explains configuration, secrets, bootstrap and optional native services. Windows commands and Worker installation are documented there as well.

## Documentation

- [Installation and operations](docs/operations/PRODUCTION-QUICKSTART.md)
- [Configuration reference](docs/operations/CONFIGURATION-REFERENCE.md)
- [Upgrade](docs/operations/UPGRADE.md) · [Upgrade and rollback](docs/operations/UPGRADE-ROLLBACK.md) · [Backup and restore](docs/PHASE-17-BACKUP-RESTORE.md)
- [Security model](docs/SECURITY_MODEL.md) · [Security policy](SECURITY.md)
- [Architecture](docs/ARCHITECTURE.md) · [Module development](docs/MODULE_SYSTEM.md)
- [Support matrix](docs/SUPPORT-MATRIX.md) · [Compatibility policy](docs/COMPATIBILITY-POLICY.md)
- [Release notes: 0.7.0 RC1](docs/PHASE-0.7.0-RELEASE-NOTES-DRAFT.md)
- [Progress log](PROGRESS-LOG.md) · [Roadmap](docs/ROADMAP.md)

## Develop and contribute

Use .NET SDK 10.0.401 and Node.js 24.19.0 for the repository's pinned validation toolchain. `Hispania.sln` is the full solution; `Hispania.Platform.slnf` checks the Platform dependency boundary. Web source and scripts are under `apps/web`. Read [Contributing](docs/CONTRIBUTING.md) before opening a pull request.

- Report defects or request work in [GitHub Issues](https://github.com/joaquimcastilla/hispania/issues).
- Propose changes with a [Pull Request](https://github.com/joaquimcastilla/hispania/pulls).
- Report security issues using the private contact route in [SECURITY.md](SECURITY.md), not a public issue.

The repository does not currently include a root `LICENSE` file. No open-source license is asserted by this README; review repository terms before redistributing.
