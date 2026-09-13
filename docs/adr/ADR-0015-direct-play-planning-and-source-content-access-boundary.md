# ADR-0015: Direct Play Planning and Source Content Access Boundary

Status: Accepted

## Context

Library discovers files, probes them, constructs the Media graph, and maintains its catalogue. Playback is a distinct responsibility: it must query that catalogue through a read contract, select a directly reproducible combination, and execute it without learning filesystem details or HTTP semantics.

ADR-0003 requires a contract-first boundary between these responsibilities. ADR-0009 and ADR-0014 keep Media identity and `SourceLocator` interpretation separate from physical source access. The first playback vertical slice is limited to a directly playable MP4 containing H.264 video and AAC audio from one source. It does not include transcoding, remuxing, subtitles, adaptive streaming, persistence, authentication, or remote source implementations.

## Decision

- Playback has its own application layer and does not depend on `Hispania.Library.Application` or any other Library implementation.
- Playback queries the Library catalogue exclusively through a read-only catalogue contract. That contract permits lookup and enumeration; it does not permit scans, replacement, or upsert operations.
- Playback separates planning, physical source access, execution, and HTTP adaptation:
  - a planner selects a directly reproducible combination and returns a minimal `DirectPlayPlan`;
  - a source-content accessor resolves and opens a `MediaSource` through infrastructure;
  - an executor revalidates the selected source, resolves a neutral byte-range request, and returns a neutral execution result;
  - HTTP parses protocol input and translates the execution result to HTTP without selecting media or accessing a source.
- `DirectPlayPlan` contains only `MediaEntityId`, `MediaSourceId`, `VideoTechnicalRepresentationId`, and `AudioTechnicalRepresentationId`. It contains no physical path, `SourceLocator`, root key, relative path, stream, MIME type, or HTTP type.
- Direct Play serves the original source bytes without transcoding or remuxing. There is no transcoding fallback in this slice.
- A source-content contract exposes a read-only, asynchronously disposable content handle with a `Stream`, known current length, seek capability, and current source facts. It does not expose a filesystem path or filesystem-specific stream type. This slice requires known length and seek support.
- `MediaSource` and its `SourceLocator` are resolved only by infrastructure. The local Library accessor reuses the existing centralized Library locator convention and its secure root-resolution policy; Media Domain and HTTP do not interpret the opaque locator value.
- The scan snapshot remains outside Media Domain. The catalogue/import entry retains observed byte length and last-modified facts independently of `LibraryFileReference`.
- If both required scan facts are available and either current length or current last-modified value differs, execution returns `SourceChanged` and does not serve bytes. If either required snapshot fact is unavailable, this conservative slice returns a typed result that requires a fresh scan rather than inventing equality.
- Byte ranges use a neutral single-range model: `Full`, `From(start)`, `Closed(start, end)`, and `Suffix(length)`. Values use non-negative `long`; `Suffix(0)` is invalid; multipart ranges are unsupported.
- Execution results are neutral and include `FullContent`, `PartialContent`, `RangeNotSatisfiable`, `RangeNotSupported`, `SourceUnavailable`, `SourceChanged`, and `PlanNoLongerValid`. Application returns no HTTP status codes.
- The HTTP adapter owns parsing of `Range` and the MP4-to-`video/mp4` mapping. It maps the neutral outcomes to HTTP responses.

## Consequences

- `Hispania.Playback.Application` can be introduced without a dependency on Library implementation projects.
- The existing in-memory Library catalogue must implement the new read-only catalogue contract while retaining its mutable operations as Library-internal responsibilities.
- `Hispania.Library.LocalFileSystem` may later implement the source-content accessor contract, but Playback remains independent of that implementation.
- Reusing the existing locator convention prevents a second path syntax and avoids duplicated containment logic.
- A file changed since scan is unavailable for Direct Play until a later scan refreshes the catalogue. This is intentionally conservative and does not add hashing or automatic reprobe.
- The initial HTTP surface can remain limited to listing media and streaming by `MediaEntityId`; no client-provided path, locator, source ID, or stream index is accepted.

## Alternatives Considered

- Implement Playback inside `Hispania.Library.Application`: rejected because discovery/catalogue ownership and playback selection/execution are separate responsibilities.
- Let Playback reference the mutable Library catalogue: rejected because Playback requires only read access.
- Let the HTTP controller open sources or resolve locators: rejected because it mixes protocol adaptation with application and infrastructure responsibilities.
- Have the planner open source content: rejected because planning must remain deterministic and side-effect free.
- Return absolute paths or `FileStream` from the content contract: rejected because they couple consumers to filesystem infrastructure and bypass source-access policy.
- Serve current bytes when scan facts are missing or changed: rejected for this slice because the technical graph may be stale.
- Support multiple byte ranges or multipart responses: deferred to a later decision.

## Related ADRs

- ADR-0003 defines contract-first modularity.
- ADR-0009 separates Assets, TechnicalRepresentations, and MediaSources.
- ADR-0014 keeps SourceLocator interpretation and physical source access outside Media Domain.
