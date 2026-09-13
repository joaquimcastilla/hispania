# ADR-0011: External Identification, Metadata Provenance, and Artwork

Status: Accepted

## Context

External providers identify different semantic concepts, can disagree, disappear, or be replaced, and must coexist with manual and local metadata. Identification, metadata, and artwork may originate from different modules and providers.

## Decision

When accepted:

- Every HISPANIA entity retains internal identity independent of external providers.
- `ExternalIdentifier` is the extensible value `Provider / Namespace / Value` and is attached through a stateful association carrying candidate, confirmed, rejected, evidence, invalidation, migration, and alias information. These are local matching states of this server or its module, never network certification or global verification.
- A confirmed active tuple identifies at most one semantically compatible target. Shared candidate matches never trigger automatic entity merge.
- Metadata is retained as non-destructive `MetadataAssertion` records with target, field, value, provenance, timestamps, optional confidence, manual/automatic origin, status, and supersession.
- Effective metadata uses assertions plus configurable resolution policy. A future materialised value is only a cache/projection.
- Identification, metadata, and artwork are independent capabilities.
- `ArtworkReference` preserves editorial role and provenance. If image bytes are managed or locally verified later, they may be modelled as `ArtworkAsset → TechnicalRepresentation → MediaSource`.

Provider namespaces must map to the correct HISPANIA concept. For example, MusicBrainz Work maps to Work, Recording to MediaEntity, Release to Release, Release Group to ReleaseGroup, and Artist to Creator.

## Alternatives Rejected

- Provider-specific domain fields such as `TmdbMovieId`.
- A single mandatory external provider.
- Treat external IDs as canonical identity.
- Destructive metadata overwrite or one globally fixed provider priority.
- Couple identification, metadata, and artwork.

## Consequences

- Conflicts, rematching, provider retirement, aliases, and manual corrections remain auditable.
- Future modules must declare provider namespace-to-concept mappings.
- Artwork retrieval is not a prerequisite for identification or metadata.
- No metadata merge algorithm or personal-preference model is selected.

## Previous ADRs

Establishes a provider boundary consistent with ADR-0003. It does not modify an existing ADR.
