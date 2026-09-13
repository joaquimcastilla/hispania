# ADR-0007: Orthogonal Media Identity Graph

Status: Accepted

## Context

ADR-0005 correctly separates media identity from filesystem paths, but its mandatory chain conflates independent axes: `Work -> Edition -> Timeline -> Assets/Sources`. `Edition` has been used ambiguously for a cut, a publication, a remaster, and an encode. Albums, compilations, alternate episode orderings, double episodes, and reusable components require a graph rather than exclusive parent-child ownership.

## Decision

When accepted, use the following conceptual graph:

```text
Work ─realizes/expresses→ MediaEntity → Timeline
Timeline ←AssetCoverage→ Asset → TechnicalRepresentation → MediaSource

Release ─contains/publishes→ MediaEntity
ReleaseGroup ─groups→ Release
Collection ─orders/groups→ MediaEntity
Creator ─contributes(role)→ Work / MediaEntity / Release
```

`ExternalIdentifierAssociation`, `MetadataAssertion`, and `ArtworkReference` are orthogonal extensions over appropriate entities. `Creator` represents a person or organisation; artist, director, actor, composer, performer, and similar terms are contribution roles, not fundamental entity types. `Edition` is not a universal container.

Files, containers, paths, provider records, codecs, and availability never define canonical multimedia identity.

This graph describes local Media only. It does not model Preservation, P2P, distributed media, global catalogues, global certification, global moderation, global asset registries, network normalisation, peer discovery, or distributed manifests. Future systems in those areas may depend on Media; Media does not depend on them or presume any external authority.

## Alternatives Rejected

- Retain the mandatory hierarchy from ADR-0005.
- Treat all distinctions as editions.
- Model media identity by file, container, path, or provider record.

## Consequences

- Publication, realisation, timeline, semantic component, technical rendition, and access relationships are explicit.
- Entities may participate in multiple Releases and Collections without duplication.
- Future persistence must model relationships rather than a single ownership tree.
- This ADR does not choose C# aggregate implementation, persistence, identifiers, or algorithms.

## Supersession

Supersedes ADR-0005.
