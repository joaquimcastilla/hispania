# Security Model

## Implemented extension boundary (0.7.0)

The permission list below is a design vocabulary, not a per-plugin enforcement mechanism. Current `.hispania` package signatures authenticate a publisher against operator-configured keys, and the extension-ID allowlist separately authorizes activation. Signature requirement depends on host policy for historical Format 1; Format 2 requires trusted publisher signatures. Manifest `permissions` are declarations, not OS-enforced grants. In-process modules share Server privileges. ModuleHost gives process/failure boundaries and configured resource limits but shares the operating-system identity; it is not a hostile-code sandbox. See [the modularity audit](MODULARITY-AUDIT-0.7.0.md) and [security operations](security/SECURITY-OPERATIONS.md).

## Goals

- least privilege;
- explicit capabilities;
- isolation of third-party code;
- auditable operations;
- secure defaults.

## Trust Levels

The categories below describe desired trust boundaries; only Core and operator-trusted native extension paths exist today. HISPANIA 0.7.0 does not ship a sandboxed plugin runtime.

### Core

Trusted.

### Native modules

Trusted or explicitly approved.

### Sandboxed plugins (future)

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
