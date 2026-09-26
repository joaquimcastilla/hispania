# ADR-0020: Media Source Content Access Contract

Status: Accepted

## Context

FFprobe was coupled to `Hispania.Library.LocalFileSystem`: it resolved a logical library root/path and read local paths directly. Playback already required source access, and future NAS, HTTP and P2P sources must own locator interpretation and authorization.

## Decision

Media Domain owns the versioned `IMediaSourceContentAccessV1` capability (`hispania.media.source-content-access@1.0`). It accepts a `MediaSource`, cancellation, and returns read-only content, observed length/time, or an explicit unavailable/unsupported/inaccessible outcome. It exposes no physical path. Source providers own locator resolution and access controls.

FFprobe consumes the contract and pipes the stream to the ffprobe process. The local filesystem provider preserves root containment checks and rejects reparse points. Playback uses an adapter over the same source owner while retaining its playback-specific result shape.

## Consequences

FFprobe can be built without the local provider and does not know roots or paths. The initial contract returns a .NET stream, so it is an in-process/native contract and not yet a remote process wire protocol. HTTP/NAS adapters must define seek, range, timeout, and credential behavior through provider policy; those are not inferred from local filesystem behavior.
