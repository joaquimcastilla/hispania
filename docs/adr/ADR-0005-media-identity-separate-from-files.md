# ADR-0005: Media Identity Is Separate From File Identity

Status: Superseded by ADR-0007

## Supersession

ADR-0007 replaces the mandatory hierarchical model with an orthogonal local media-identity graph. This ADR is retained as historical context.

## Decision

Do not model a movie, song or episode as a filesystem path.

Use:

Work -> Edition -> Timeline -> Assets/Sources

## Consequences

Supports:

- alternate qualities;
- multiple audio tracks;
- independent subtitles;
- remote sources;
- future distributed storage;
- audio/video representations of the same track;
- preservation state without playable files.
