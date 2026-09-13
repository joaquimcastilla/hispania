# ADR-0009: Semantic Assets, Technical Representations, and Source Access

Status: Accepted

## Context

A semantic component, its encoding, and its current location are different concerns. A single container can expose several streams for different components, while one technical rendition can be available through multiple files, containers, paths, or transports.

## Decision

When accepted, use:

```text
Asset → TechnicalRepresentation → MediaSource
```

An Asset is the semantic component: principal video, original audio, dub, commentary, audio description, subtitle, or lyric. It can remain known while a local disk is disconnected, a NAS is offline, a path has disappeared, or a local catalogue has not yet recovered source access.

A TechnicalRepresentation is a concrete technical rendition of an Asset, such as HEVC 2160p HDR10, AV1 1080p, FLAC 24/96, AC-3 5.1, ASS, or SRT. Codec, resolution, and bitrate do not identify the Asset.

A MediaSource is physical or remote access to one or more representations, such as a local file, NAS object, HTTP object, optical medium, or future transport. Container format, stream index, and future byte ranges are source-specific exposure data.

For example, `movie.mkv` is a MediaSource. Its video, English-audio, Spanish-audio, and Spanish-subtitle streams expose representations of their corresponding Assets; the container itself is not automatically an Asset.

## Alternatives Rejected

- Treat every file or stream as an Asset.
- Identify representations by path or URL.
- Introduce container identity as multimedia identity.

## Consequences

- The same representation can be exposed by several sources without duplication.
- Probing and source modules require explicit mapping evidence.
- Image/artwork bytes may reuse TechnicalRepresentation and MediaSource only when managed or locally verified.

## Previous ADRs

Refines the media-identity boundary in ADR-0005. It defines neither Preservation nor a distributed system.
