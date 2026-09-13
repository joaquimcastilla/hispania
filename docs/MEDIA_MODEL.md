# Media Model

## Status

This is a concise index to the accepted Phase 2 conceptual design. Media 0.2 implementation is complete: `MEDIA_DOMAIN_DESIGN.md` defines the model; ADR-0007 through ADR-0014 are Accepted, and ADR-0007 supersedes ADR-0005. This document remains conceptual and does not prescribe implementation details.

## Core idea

A Work is not a file. Files, containers, paths, codecs, provider records, and current availability do not define canonical media identity.

The model is an orthogonal graph, not a mandatory hierarchy:

```text
Work → MediaEntity → Timeline
Timeline ← AssetCoverage → Asset → TechnicalRepresentation → MediaSource

Release → MediaEntity
ReleaseGroup → Release
Collection → MediaEntity
Creator → Work / MediaEntity / Release (role-specific contribution)
```

External identification, metadata assertions, and artwork references are provenance-bearing extensions over the appropriate entities.

## Concept summary

- **Work:** creative or conceptual identity.
- **MediaEntity:** a meaningful reproducible realisation, such as a movie realisation, episode realisation, audio recording, music video, or broadcast.
- **Timeline:** explicit temporal reference and compatibility endpoint.
- **Asset:** semantic component with exactly one closed MediaKind (`Video`, `Audio`, `Text`, or `Image`) and zero or more compatible semantic roles.
- **TechnicalRepresentation:** concrete technical rendition of an Asset.
- **MediaSource:** physical or network access to one or more representations.
- **Release:** publication context.
- **ReleaseGroup:** optional publication-family context for related Releases.
- **Collection:** non-exclusive ordered or grouped context for MediaEntities.
- **Creator:** person or organisation; artist, director, composer, and performer are contribution roles.

An Asset has exactly one closed MediaKind and zero or more unique, compatible roles:

```text
Asset
├── MediaKind: exactly 1
└── Roles: 0..N
```

In Media 0.2, `Edition` is not a universal model concept. A cut/edit, remaster, encoding, release, and collection have distinct semantics.

## Rules

1. Work, Asset, and TechnicalRepresentation remain known independently of currently available sources.
2. Temporal compatibility is explicit; equal duration, Work, language, codec, or resolution is insufficient evidence.
3. A container and its streams are source-specific exposure data, not Assets by default.
4. Release and Collection membership are non-exclusive.
5. External IDs are never canonical HISPANIA identity.
6. Metadata assertions retain individual provenance; identification, metadata, and artwork are independent capabilities.
7. Asset roles are unique, HISPANIA-controlled domain codes; providers do not introduce roles or MediaKinds arbitrarily.
8. A SourceLocator records a scheme and opaque value without transport interpretation or secrets.

## Deferred decisions

Hashing, deduplication, persistence, source probing, metadata-resolution algorithms, artwork retention, and playback policy remain undecided. See `MEDIA_DOMAIN_DESIGN.md` for open questions and ADR links.
