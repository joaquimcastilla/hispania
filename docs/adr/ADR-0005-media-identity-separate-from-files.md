# ADR-0005: Media Identity Is Separate From File Identity

Status: Accepted

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
