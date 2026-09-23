# TODO.md — HISPANIA

Statuses: ACTIVE, QUEUED, COMPLETED, BLOCKED. Normally one task is ACTIVE.

## ACTIVE

Stage B — Rich Metadata Persistence: COMPLETE.
Stage C — Metadata Settings, Locks & Manual Overrides: COMPLETE.
Stage D — Metadata Editor API: COMPLETE.
Stage E — Web Metadata Editor: COMPLETE (automated verification passed; live browser/TMDB verification pending).
People & Credits 0.1: IN PROGRESS (perfil, caché y filmografía local implementados). Live TMDB verification: PENDING.

### P0-010 — Movie Metadata Model 0.2 + Metadata Editor 0.1
Status: ACTIVE
Priority: P0
Depends: P0-005
Acceptance: provider-neutral rich metadata, persisted people/credits/studios/tags/settings/locks, manual assertions with provenance, editor API/UI, restart tests and effective-metadata tests. Live TMDB verification remains required before completion.

## QUEUED

### P0-011 — Proteger operaciones administrativas antes de exposición remota
Status: QUEUED
Priority: P0
Depends: P1-013 o un control temporal explícito de despliegue
Acceptance: endpoints de escaneo, identificación y edición requieren autorización; se prueban accesos permitidos y denegados. Hasta entonces, despliegue limitado a una red de confianza.

### P1-027 — Extraer fallback FFmpeg del host
Status: QUEUED
Priority: P1
Depends: definición del contrato de transcoding
Acceptance: contrato en `contracts`, orquestación en `application`, adaptador de proceso/caché en `infrastructure`; el host conserva sólo HTTP y composición; tests de timeout y fallo.

### P1-028 — Desacoplar ffprobe de filesystem
Status: QUEUED
Priority: P1
Acceptance: `infrastructure/ffprobe` deja de referenciar `infrastructure/library`; usa contrato de resolución de fuente y conserva controles de ruta y pruebas.

### P1-029 — Separar EFCore común de decisiones SQLite
Status: QUEUED
Priority: P1
Acceptance: conversiones y manejo de errores específicos de SQLite quedan en el adaptador SQLite; la capa EFCore común no depende de `Microsoft.Data.Sqlite`.

### P0-009 — Playback progress/resume
Status: QUEUED
Priority: P0
Depends: P0-008
Acceptance: start/progress/stop represented; resume survives restart; Continue Watching/query path; tests pass.

### P1-010 — Playback decision engine
Status: QUEUED
Priority: P1
Depends: P0-008

### P1-011 — FFmpeg remux adapter
Status: QUEUED
Priority: P1
Depends: P1-010

### P1-012 — FFmpeg transcode v1
Status: QUEUED
Priority: P1
Depends: P1-011

### P1-013 — Basic users/auth/session
Status: QUEUED
Priority: P1
Depends: P0-009

### P2-014 — Series
Status: QUEUED
Priority: P2
Depends: core MVP

### P2-015 — Music
Status: QUEUED
Priority: P2
Depends: core MVP

### P2-016 — Independent audio/subtitle/lyrics/music-video Assets
Status: QUEUED
Priority: P2
Depends: P2-015

### P3-017 — Advanced Releases / editions
Status: QUEUED
Priority: P3

### P3-018 — Advanced client capability profiles
Status: QUEUED
Priority: P3
Depends: P1-010

### P4-019 — TV/mobile clients
Status: QUEUED
Priority: P4

### P4-020 — PSP proof-of-concept
Status: QUEUED
Priority: P4
Depends: P1-012, P3-018

### P5-021 — Remote MediaSources
Status: QUEUED
Priority: P5

### P5-022 — Trusted federation
Status: QUEUED
Priority: P5
Depends: P5-021

### P5-023 — P2P asset transport
Status: QUEUED
Priority: P5
Depends: P5-022

### P6-024 — Distributed representation cache
Status: QUEUED
Priority: P6
Depends: P5-023

### P6-025 — Lost-media preservation
Status: QUEUED
Priority: P6
Depends: P5-023

### P6-026 — Advanced Timeline / AssetCoverage
Status: QUEUED
Priority: P6

## COMPLETED

### P0-005 — Metadata select/confirm/refresh end-to-end
Status: COMPLETED
Priority: P0
Depends: P0-004
Result: búsqueda, selección, confirmación, refresh y persistencia verificados con pruebas deterministas; comprobación TMDB real pendiente dentro de P0-010.

### P0-001 — Reconstruct and verify repository baseline
Status: COMPLETED
Result: repository structure, existing changes and backend/frontend verification were reviewed; backend tests pass and the frontend production build passes.

### P0-002 — Robust video discovery
Status: COMPLETED
Result: discovery accepts `.mp4`, `.mkv` and `.avi` case-insensitively; compatibility remains a probe/playback concern.

### P0-003 — Probe -> TechnicalRepresentation -> MediaSource persistence
Status: COMPLETED
Result: probe mapping and playable source persistence are implemented and covered by the current test suite.

### P0-004 — Minimal library persistence/query
Status: COMPLETED
Result: durable SQLite catalogue, stable rescans/restarts and public library query are implemented.

### P0-006 — Poster/backdrop persistence and serving
Status: COMPLETED
Result: persisted artwork references, HISPANIA artwork delivery and effective metadata projection are implemented; durable artwork caching remains deferred.

### P0-007 — Minimal movie Web UI
Status: COMPLETED
Result: movie library/detail navigation, metadata/artwork states and explicit identification flow are implemented; live/restart verification remains pending for the current TMDB slice.

### P0-008 — Direct Play v1
Status: COMPLETED
Result: allowed-source Direct Play with HTTP Range and relevant failure coverage is implemented.

## BLOCKED

Record blocker, evidence, attempts and exact required human input.
