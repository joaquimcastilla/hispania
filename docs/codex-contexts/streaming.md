# Streaming

## Lectura obligatoria

- `CODEX.md`, `PROJECT_CONTEXT.md`, `docs/ARCHITECTURE.md` y `docs/ROADMAP.md`.
- ADR-0003.
- Documentación de Playback y Media sólo cuando la dependencia sea real.
- ADR-0014 y ADR-0015 cuando el trabajo trate Direct Play o byte ranges.

## Estado y ámbito

No tiene una fase autónoma: se relaciona con playback de Phase 6 y con media engine según el roadmap. ADR-0015 está Accepted para el primer Direct Play con byte ranges; no decide transcoding, remuxing, packaging ni streaming adaptativo. La arquitectura distingue demux, transcode y packaging como funciones que no deben mezclarse.

## Límites

No se definen protocolos, formatos, empaquetado ni infraestructura de streaming fuera del alcance propuesto en ADR-0015. No modifica playback, transcoding, Media o API sin autorización explícita.
