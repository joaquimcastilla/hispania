# Transcoding

## Lectura obligatoria

- `CODEX.md`, `PROJECT_CONTEXT.md`, `docs/ARCHITECTURE.md` y `docs/ROADMAP.md`.
- ADR-0003.
- Documentación de Playback y Media cuando el trabajo establezca una dependencia concreta.

## Estado y ámbito

Fase de roadmap: Phase 7 — Transcoding 0.7. Se prevé `ITranscoder`, una implementación FFmpeg, supervisión de procesos, cancelación, timeout y abstracción de capacidades de hardware. Esas piezas no están diseñadas ni implementadas aún.

## Límites

No añade FFmpeg, procesos, aceleración de hardware, dependencias ni contratos antes de una tarea autorizada. No mezcla transcodificación con probing, demux, packaging o playback.
