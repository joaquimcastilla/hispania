# HISPANIA 0.7.0 — Draft Release Notes

**Draft only. This is not an official Release.** Phase 25's clean-commit Windows/Linux runner acceptance is pending; see the [readiness matrix](phase25-release-readiness.md).

## Distribution

- Version: `0.7.0` (pre-1.0).
- Candidate architectures: Windows x64 (`win-x64`) and Linux x64 (`linux-x64`). ARM64, Raspberry Pi and other architectures are not claimed.
- Runtime: .NET 10 ASP.NET Core x64 runtime. The published applications are framework-dependent, not self-contained.
- Media installations may require FFmpeg and FFprobe. Worker installations need only the runtime and their configured capability providers.
- Build and verify packages with `tools/publish-production.ps1`; each package includes a per-file SHA-256 manifest and a SHA-256 sidecar for the manifest.

## Included platform capabilities

- Modular Server and standalone WorkerAgent hosts with versioned contracts and package lifecycle.
- Persistent identity/session authentication, authorization and administrative operations.
- SQLite-backed owner stores, durable Events and Jobs, Assets/Storage, Audit and observability.
- Optional Media PostgreSQL provider; Platform stores and distributed coordinator remain SQLite-backed.
- Generic WorkerAgent mTLS transport, explicit node/capability authorization, leases, generation fencing and recovery.
- Modern and Classic Web Shells. Experimental package Web contributions are declarative and use fixed authorized operations.
- Offline installation, configuration and secret-reference provisioning, backups, upgrades, restore/rollback runbooks, and Windows/Linux service templates.

## Security and operational limits

- InProcess modules inherit Server privileges. `AssemblyLoadContext` is not a sandbox; process limits do not make untrusted code safe.
- Package signatures are optional unless the operator requires them; host authorization and publisher trust must be configured for the intended deployment.
- The distributed coordinator is a single instance using SQLite. There is no multi-master/high-availability claim and no exactly-once job or event guarantee.
- Restore and rollback are owner-specific. Restoring independent stores is not globally atomic; an old binary is not a safe rollback after an unverified schema migration.
- PostgreSQL is a Media persistence option, not a universal Platform backend. Authenticated PostgreSQL acceptance for this candidate remains pending.
- Phase 24's Short/Soak evidence uses synthetic loads and an in-process coordinator. It does not establish Server managed-heap/GC trends or active fault-injection recovery.
- Native service-manager installation and least-privilege operation require disposable Windows/Linux acceptance targets; they are not part of local Phase 25 evidence.
- Inventory is a limited domain proof, not a complete ERP.

See [Production Quick Start](operations/PRODUCTION-QUICKSTART.md), [Support Matrix](SUPPORT-MATRIX.md), [Security Operations](security/SECURITY-OPERATIONS.md), and [Upgrade/Rollback](operations/UPGRADE-ROLLBACK.md) before deployment.
