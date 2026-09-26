# ADR-0021: Modular Playback Transcoding and Selectable Web Builds

Status: Accepted

## Context

The HTTP server contained the FFmpeg process, cache policy, and fallback selection. The UI was a single Vite output. Alternative transcoders should implement a public Playback contract, and deployments need a manual web build choice independent from multimedia modules.

## Decision

- Playback Contracts owns `ICompatibleMediaTranscoderV1` (`hispania.playback.compatible-transcoder@1.0`) and `ITranscodedContentStore`. Results contain an opaque generated-content handle, media type and length; host-local paths are not part of the contract.
- The host registers and selects a module at startup. HTTP only maps the neutral available/unavailable result. Replacement requires host deployment/restart.
- Vite produces independent `modern` and `classic` outputs with distinct entrypoints and theme styles. `Web:Build` selects one output directory at process startup. Both currently share the same React screens, navigation logic, and HTTP client.

## Consequences

This enables contract-compatible transcoder replacement without changing the endpoint and lets an operator choose one built UI per server instance. FFmpeg is executed by a separate process-per-job worker over redirected stdin/stdout pipes using the versioned `hispania.transcoding.ffmpeg.worker@1` JSON envelope; binary source bytes travel on stdin, and only a cache key returns. The host supervises each job with a four-hour timeout, tree cancellation, atomic result publication and one bounded retry. The worker and host share a configured cache volume. This is process isolation only: both processes run as the same OS identity, inherit the worker configuration, and have no CPU/memory quotas or sandbox policy. There is no persistent worker pool, health heartbeat, hot swap, backend A/B, simultaneous UI rollout, or two independently evolving product clients.
