# Streaming

## Lectura obligatoria

- `CODEX.md`, `PROJECT_CONTEXT.md`, `docs/ARCHITECTURE.md` y `docs/ROADMAP.md`.
- ADR-0003.
- Documentación de Playback y Media sólo cuando la dependencia sea real.

## Estado y ámbito

No tiene una fase autónoma ni diseño aceptado: se relaciona con playback de Phase 6 y con media engine según el roadmap. La arquitectura sólo distingue demux, transcode y packaging como funciones que no deben mezclarse.

## Límites

No se definen protocolos, formatos, endpoints, empaquetado ni infraestructura de streaming en este índice. No modifica playback, transcoding, Media o API sin autorización explícita.
