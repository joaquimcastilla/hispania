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
