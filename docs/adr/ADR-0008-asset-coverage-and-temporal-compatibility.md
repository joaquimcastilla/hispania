# ADR-0008: Asset Coverage and Temporal Compatibility

Status: Accepted

## Context

Titles, filenames, language, duration, resolution, codec, and a shared Work do not establish that a dub, subtitle, commentary, angle, lyric, or fragment aligns with a Timeline. Conventional local media requires a way to express partial tracks, incomplete subtitles, local cuts, and timing differences without inventing files or synchronisation algorithms.

## Decision

When accepted, represent Asset-to-Timeline compatibility through explicit `AssetCoverage` relationships. Coverage can state complete or partial intervals, multiple intervals, gaps, offsets, incompatibility, and known temporal transformations.

Compatibility may be known, unknown, incompatible, or alignable through an explicitly recorded transformation. This ADR does not define a transformation or synchronisation algorithm.

## Alternatives Rejected

- Infer compatibility from duration, filename, language, codec, resolution, or shared Work.
- Create a new Asset for every temporal variant.
- Store temporal alignment only as container or source metadata.

## Consequences

- Partial dubbing, incomplete subtitles, PAL speed-up, recaps, fragments, and distinct local cuts are representable.
- Consumers must use recorded compatibility evidence rather than heuristics.
- A later alignment engine interprets this relationship; it does not redefine semantic identity.

## Previous ADRs

Refines the temporal implications of ADR-0005. It does not itself alter ADR-0005's status.
