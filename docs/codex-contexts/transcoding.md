# Transcoding

## Lectura obligatoria

- `CODEX.md`, `PROJECT_CONTEXT.md`, `docs/ARCHITECTURE.md` y `docs/ROADMAP.md`.
- ADR-0003.
- Documentación de Playback y Media cuando el trabajo establezca una dependencia concreta.

## Estado y ámbito

Fase de roadmap: Phase 7 — Transcoding 0.7. Ya existe un fallback experimental `FfmpegCompatibleMediaTranscoder` para AVI/MP4 incompatibles, con salida MP4 en caché y cancelación. Su interfaz e implementación residen en `apps/server`, por lo que aún no satisfacen la frontera modular prevista. Faltan un contrato independiente, adaptador FFmpeg, política de timeout y capacidades de hardware. Ver `docs/MODULARITY_AUDIT_2026-09-23.md`.

## Límites

No añade FFmpeg, procesos, aceleración de hardware, dependencias ni contratos antes de una tarea autorizada. No mezcla transcodificación con probing, demux, packaging o playback.
