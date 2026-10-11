# Security Model

## Goals

- least privilege;
- explicit capabilities;
- isolation of third-party code;
- auditable operations;
- secure defaults.

## Trust Levels

### Core

Trusted.

### Native modules

Trusted or explicitly approved.

### Sandboxed plugins

Untrusted by default.

## Plugin permissions

Possible permissions:

- outbound network;
- filesystem read;
- filesystem write;
- media read;
- media write;
- GPU;
- process execution;
- secret access;
- user data;
- plugin-owned storage.

Permissions should be visible to administrators before installation.

## Global catalogue safety

For any future shared network:

- normal peers cannot introduce arbitrary global assets;
- submissions are quarantined;
- manifests are signed;
- assets are content-addressed;
- revocation must exist;
- provenance is retained;
- administrative actions are audited.

## Local vs Global

Local media is private unless an explicit publication workflow occurs.

Never make "scan local library" equivalent to "publish to global network".

## Production installation and CI runners

- Production artifacts are integrity-checked against a per-file manifest and an externally conveyed package SHA-256. A manifest is not a signature; the digest must arrive over an independently trusted channel.
- Platform-only supports only its server role and SQLite in the current installation profile. Bootstrap secrets belong in protected `secrets/` files or the service environment and should be removed from configuration and storage after bootstrap.
- Staged upgrade holds the installation lock, verifies a Phase 17 backup before switching binaries and keeps the prior application. If a new host may have migrated state, recovery is restore-backed; binary-only rollback is not asserted to reverse schemas.
- Self-hosted ARM64 runners must not execute pull request code. The current workflows are manual-only and actor/ref-gated, use empty GitHub permissions, fixed root-owned inputs and no production secrets. Repository write access and `main` review policy remain part of that trust boundary; a workflow condition alone is insufficient.
- Platform Job module contributions are opt-in signed manifest declarations bound to the catalog's module ID and active generation. Publisher trust is rechecked at dispatch; deactivation/revocation drains and cancels active work, and Core persists Jobs separately from ephemeral handler registrations. The in-process module runtime is trusted code, not a sandbox. Focused unload coverage passes after avoiding generic reflection deserialization of a fixture-owned DTO, but arbitrary trusted module code can still retain references and the complete suite has had one intermittent unload timeout; this is not yet a closed lifecycle gate.
- `Modules:AdminToken` remains a compatibility bearer, separate from Session/RBAC. Production configuration may contain only a validated `env:NAME` or protected `file:NAME` reference; raw tokens are resolved into process configuration and are not written to manifests or logs. Tests generate a fresh token and expose it only to their isolated child process. Missing configuration remains HTTP 503; missing/invalid credentials remain HTTP 401.
