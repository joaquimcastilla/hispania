# ADR-0003: Contract-first Modularity

Status: Accepted

## Decision

Cross-module communication uses explicit contracts.

Consumers reference interfaces/capabilities, not concrete implementations.

## Example

Preferred:

Library -> IMetadataProvider

Not allowed:

Library -> TmdbMetadataProvider
