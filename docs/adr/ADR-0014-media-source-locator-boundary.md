# ADR-0014: Media Source Locator Boundary

Status: Accepted

## Context

ADR-0009 defines `MediaSource` as a physical or network access record while keeping media identity independent of paths, containers, and transport. Media must retain a declared source location without becoming responsible for interpreting or accessing filesystem, HTTP, SMB, or future transport details.

The source location must also not become a storage mechanism for credentials or other secrets.

## Decision

- A `MediaSource` has a `SourceLocator` described by `Scheme` and `OpaqueValue`.
- Media describes and preserves that declared location; infrastructure resolves and accesses it.
- `Scheme` is a declared namespace or locator class, not a Media implementation of a transport.
- `OpaqueValue` is not interpreted by Media Domain.
- `SourceLocator` contains no passwords, access tokens, API keys, cookies, SMB credentials, or equivalent secrets.
- Authentication, authorization, credential resolution, and source access belong to future layers outside Media Domain.
- This decision does not design storage adapters, transport implementations, credential-resolution mechanisms, P2P, or distributed transports.

## Alternatives Considered

- A fully opaque locator without a scheme: rejected because it cannot distinguish declared locator namespaces without external conventions.
- Transport-specific locator types in Media Domain: rejected because they would couple Media to filesystem, HTTP, SMB, or other infrastructure behavior.
- URI parsing and interpretation in Media Domain: rejected because parsing, transport semantics, and access policy belong to infrastructure.
- Embed credentials in a source locator: rejected because secrets are neither media identity nor source location and require separate security controls.

## Consequences

- Media can retain source locations for local files, removable media, NAS, and conventional remote access without resolving them.
- Infrastructure can add resolver implementations without making their transport semantics domain behavior.
- Locator schemes and opaque values need future boundary validation and resolver contracts, but no adapter is introduced by this ADR.
- Source access must remain possible to model independently from credentials and authorization state.

## Related ADRs

- ADR-0003 requires contract-first boundaries between modules.
- ADR-0007 keeps Media local and independent of distributed systems.
- ADR-0009 defines MediaSource as access information distinct from semantic and technical identity.
