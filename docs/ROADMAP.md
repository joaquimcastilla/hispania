# Roadmap

## Current roadmap state — 2026-10-10

The active HISPANIA roadmap comprising Phases 1–25 is complete. Phase acceptance was incremental; Phase 25 final acceptance is recorded in [release readiness](phase25-release-readiness.md). This milestone closes the current roadmap, not HISPANIA development. Release Candidate preparation is tracked separately and does not add a Phase 26.

| Workstream | State |
|---|---|
| Phases 1–25 | Complete and accepted within their recorded scopes |
| 0.7.0-rc.1 preparation | In review; packages and documentation must identify the final clean preparation commit |
| Later improvements | Proposed only; not accepted roadmap commitments |

Support limits and non-validated features remain in the [Support Matrix](SUPPORT-MATRIX.md), [Phase 25 record](phase25-release-readiness.md), Phase-specific documents and [Platform 1.0 scope](PLATFORM-1.0-SCOPE.md). Future directions are proposals, not implemented work.

## Initial exploratory roadmap (historical)

The roadmap below is the early exploratory plan retained for its design history. Its dates, phase numbering and incomplete statuses describe that earlier plan and are superseded by the current 25-phase roadmap status above. Preserve this section as historical context; use the current phase documents and progress record for implementation state.

## Estado verificado — 2026-09-23

La solución compila y supera 412 pruebas .NET. El cliente web supera 29 pruebas y compila para producción. Stage B–E del editor están implementadas; sigue pendiente la comprobación manual con navegador y TMDB real. Existe un fallback experimental a FFmpeg para AVI/MP4 incompatibles; todavía no constituye Phase 7. La revisión de fronteras y riesgos está en `MODULARITY_AUDIT_2026-09-23.md`.

## Stage E verification note — 2026-09-22

Stage E — Web Metadata Editor: COMPLETE by automated verification. Backend: 389/389 tests; frontend: 20/20 tests. The editor consumes the updated read model after PUT/DELETE, preserves drafts on failed saves, distinguishes `PUT []` from `DELETE`, and is wired to the real `/media/{mediaId}/metadata/edit` route. Live browser navigation and live TMDB verification remain pending.

This roadmap is architectural first.

## Phase 0 - Project Constitution

Status: Completed

Deliverables:

- README
- Vision
- Project Context
- Architecture
- Coding rules
- Contribution guide
- Security policy
- license
- ADR framework
- CI skeleton

Goal:
make future decisions traceable.

## Phase 1 - Kernel 0.1

Status: Completed

Build:

- host;
- module registry;
- configuration;
- dependency injection;
- lifecycle;
- health checks;
- versioning;
- event infrastructure;
- structured logging;
- basic observability.

Acceptance criterion:

Two implementations of the same trivial test contract can be selected from configuration without modifying the consumer.

## Phase 2 - Domain and Media Model 0.2

Status: Completed

Define:

- Work
- MediaEntity
- Timeline
- Asset
- TechnicalRepresentation
- MediaSource
- AssetCoverage
- Release, ReleaseGroup and Collection
- Creator contribution roles
- external identification, metadata provenance and artwork references

Media Domain conceptual design and implementation: Completed. The accepted model is documented in `MEDIA_DOMAIN_DESIGN.md` and ADR-0007 through ADR-0014.

Implementation increments:

- Media 2A — Identity + Core Media: Completed.
- Media 2B — Timelines + Asset Coverage: Completed.
- Media 2C — Technical Representation + Media Source: Completed.
- Media 2D — Publication + Creator: Completed.
- Media 2E — Metadata / Identification: Completed.

No UI requirement yet.

## Phase 3 - Filesystem and Scanner 0.3

Status: Initial local-library durable playable slice completed; the full phase remains incomplete.

Completed slice:

- local filesystem discovery of `.mp4` files beneath configured roots;
- ffprobe analysis;
- mapping of MP4 with H.264 video and AAC audio;
- durable SQLite catalogue used by the public API and Direct Play.

Persistence 0.1 — COMPLETE: the durable Library playable slice provides a SQLite catalogue, stable Domain IDs for unchanged rescans and process restarts, atomic completed-scan reconciliation, import statuses, rollback on failure/cancellation, a rehydrated playable graph, startup migrations/snapshot/scan/readiness, and Direct Play with HTTP Range after restart. It does not claim PostgreSQL or full Media 0.2 graph persistence.

Future: Persistence 0.2 — Full Media Graph Persistence will add real roundtrip workflows for the remaining Media 2D/2E graph, technical profiles and coverage forms not exercised by this slice, plus a contract suite prepared for PostgreSQL.

Implement contracts:

- IFileSource
- ILibraryScanner
- IMediaAnalyzer
- ILibraryRepository

Initial modules:

- filesystem source
- ffprobe analyzer
- SQLite repository

Goal:
scan a directory and persist discovered media.

## Phase 4 - Public API 0.4

Status: Partial.

The initial public surface lists the in-memory media catalogue and streams a selected media entity for Direct Play. OpenAPI, the broader library API and the remaining endpoints are not completed.

Create:

- versioned REST API;
- OpenAPI;
- system endpoints;
- library endpoints;
- media endpoints.

Rule:
clients cannot bypass API.

## Phase 5 - First Web Client 0.5

Status: **Web UI 0.1 — First Playable Movie and Movie Detail / Library UX 0.1 completed.** The broader phase remains incomplete.

The completed vertical slice was manually verified in a real browser with:

```text
local filesystem
-> MP4 / H.264 / AAC
-> Direct Play
-> HTTP Range
-> Web UI
-> Play / Pause / Seek / Volume / Mute / Fullscreen
```

El corte original servía catálogo y reproducción nativa desde el mismo origen. Después se añadieron persistencia, metadatos, pósteres, configuración de metadatos y un fallback experimental a FFmpeg. Siguen pendientes autenticación y las demás pantallas de Phase 5.

Movie Detail / Library UX 0.1 — COMPLETE: a real local library scan, durable SQLite restart with stable `MediaEntityId`, API list, Web card/selection, detail view and native Direct Play were verified manually with the local movie. Poster/backdrop fallback was verified manually because this Work has no persisted enrichment/artwork; provider artwork delivery remains covered by deterministic E2E. The current visual design validates structure and functionality, not HISPANIA's final visual design.

Implement:

- basic home;
- library;
- media details;
- basic player;
- settings.

Goal:
scan and play a local movie through the web client.

## Phase 6 - Playback Engine 0.6

Status: Initial Direct Play slice completed; the full phase remains incomplete.

El corte Direct Play planifica y ejecuta MP4/H.264/AAC local con byte ranges. Un fallback experimental convierte algunos AVI/MP4 incompatibles a MP4 en caché. Siguen pendientes la negociación de capacidades, alternativas Direct Stream, subtítulos y streaming adaptativo.

Build:

- ClientCapabilities
- PlaybackRequest
- PlaybackPlanner
- PlaybackPlan
- PlaybackExecutor

Initial strategies:

- direct play;
- direct stream when possible.

## Phase 7 - Transcoding 0.7

Add:

- ITranscoder;
- FFmpeg implementation;
- process supervision;
- cancellation;
- timeout;
- hardware capabilities abstraction.

Future implementations:

- NVENC
- VAAPI
- QSV
- remote transcoder.

## Phase 8 - Users 0.8

Add:

- users;
- profiles;
- local authentication;
- authorization;
- sessions;
- device registration.

Contracts:

- IIdentityProvider
- IAuthenticationProvider
- IAuthorizationProvider

## Phase 9 - Metadata 0.9

Metadata Providers 0.1 — COMPLETE. This completed bounded slice is the movie metadata provider architecture, durable enrichment workflow, and the first real TMDB movie adapter. It includes movie-specific provider contracts; ephemeral search candidates; explicit Candidate selection and confirmation; provider-neutral normalized documents; Media 2E mapping with provenance and artwork references; SQLite persistence and refresh state; canonical fingerprint idempotency; supersession/delta history; restart rehydration; deterministic adapter HTTP tests; and real Application + SQLite end-to-end coverage.

Effective Metadata 0.1 — COMPLETE. ADR-0018 is implemented as policy `effective-metadata-v0.1`: a pure deterministic, on-demand projection of persisted assertions and artwork references, with targeted reads, SQLite restart coverage, metadata detail, media-list summary, HISPANIA artwork delivery URLs and Web UI consumption. It preserves provenance and does not persist or cache a materialized winner. Provider ranking, locale/region, user preferences, raw/admin metadata API, manual editing, materialized/cache projection, confidence ranking and identifier-lifecycle influence remain explicitly deferred.

TMDB Movies 0.2 — Automatic Enrichment is implementation-complete pending live TMDB verification. After the configured library scan commits, a separate Application workflow parses conservative filename signals, searches TMDB, auto-confirms only one exact title/year candidate, and reuses the existing confirm/fetch/persistence lifecycle. Confirmed successful enrichments are not searched or fetched again on startup. Ambiguous, unmatched and provider-failure cases remain catalogued with local fallback and manual identification available. Deferred: fuzzy scoring, bulk enrichment, scheduler/retry policy, TV shows, TMDB collections, cast, genres, trailers, ratings, token settings UI and durable artwork cache.

Contracts:

- IMetadataProvider
- IArtworkProvider
- IExternalIdProvider

Initial implementations:

- local metadata;
- one external provider.

## Phase 10 - 1.0 Product Baseline

Stage B update: normalized rich persistence for Work-scoped genres, tags, studios and credits, reusable provider/person identity, secondary external IDs and SQLite migration `0003_rich_metadata` is implemented and covered by restart, idempotency, changed-payload and multi-provider tests. Settings, locks, manual overrides and editor API/UI are implemented; live verification remains pending.

Movie Metadata Model 0.2 + Metadata Editor 0.1 — ACTIVE pending live verification. Stage B — Rich Metadata Persistence: COMPLETE. Stage C — Metadata Settings, Locks & Manual Overrides: COMPLETE. Stage D — Metadata Editor API: COMPLETE. Stage E — Web Metadata Editor: COMPLETE by automated verification. People & Credits: person profile endpoint, cached provider profile and local filmography are implemented; broader UI/contract verification remains in progress. Live TMDB verification: PENDING. Movie Detail remains the consumption projection and Metadata Editor is the administrative surface; Effective Metadata remains authoritative. Migration IDs: `20260922000100_initial`, `20260922000200_metadata_enrichment`, `20260922000300_rich_metadata`, `20260922000400_metadata_settings`, `20260922000500_person_metadata`.

Required:

- movies;
- series;
- music;
- users;
- metadata;
- direct play;
- transcoding;
- subtitles;
- history;
- continue watching;
- search;
- collections;
- playlists;
- basic administration;
- web client;
- Docker deployment;
- backup/restore basics;
- API stability policy.

## Phase 11 - Plugin SDK

Build:

- .NET SDK;
- Rust SDK;
- plugin templates;
- contract docs;
- compatibility tests;
- examples.

## Phase 12 - WASM Plugin Runtime

Add:

- Wasmtime;
- WASI;
- capability permissions;
- manifests;
- sandboxed storage;
- network allowlists;
- plugin health;
- resource limits.

## Phase 13 - Pipeline Engine

Represent workflows explicitly.

Initial pipeline:

Filesystem
-> Parser
-> Analyzer
-> Identifier
-> Metadata
-> Artwork
-> Repository
-> Search

Enable stage swapping via configuration.

## Phase 14 - A/B and Experimental Modules

Support:

- multiple implementations;
- routing rules;
- feature flags;
- controlled rollout;
- metrics comparison.

## Phase 15 - Full Observability

Provide per-module:

- calls;
- latency;
- errors;
- CPU;
- RAM;
- throughput;
- custom metrics.

Add benchmark harness.

## Phase 16 - Event Platform

Standardize application events.

Optional external bridge:

- NATS / JetStream.

## Phase 17 - Distributed Nodes

Optional advanced deployment:

- scanner node;
- metadata node;
- GPU transcode nodes;
- search nodes.

Home installs remain single-process by default.

## Phase 18 - Additional Clients

- Tizen
- Android
- Android TV
- Desktop
- others

## Phase 19 - Differential Features

Examples:

- audio/video toggle for tracks;
- multi-version media;
- smart playlists;
- semantic search;
- recommendation modules;
- scene detection;
- intro detection;
- credits detection;
- remote playback;
- offline clients;
- distributed transcoding.

## Phase 20 - Ecosystem

Build:

- plugin registry;
- module discovery;
- signatures;
- compatibility metadata;
- ratings/reputation;
- documentation portal.

## Phase 21 - Preservation Knowledge Graph

Independent subsystem.

Build:

- WorkID
- EditionID
- TimelineID
- AssetID
- preservation states
- evidence system
- lost-media workflows
- unknown-work submission
- coverage metrics

## Phase 22 - Distributed Content Network

Only after identity, manifests and moderation are mature.

Build:

- content addressing;
- signed manifests;
- authorized submission;
- quarantine;
- validation;
- revocation;
- peer discovery;
- pluggable transports;
- rights/distribution-state handling.

This phase must remain separate from the core media server.
