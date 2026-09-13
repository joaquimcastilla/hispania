# Library & Scanner

## Lectura obligatoria

- `CODEX.md`, `PROJECT_CONTEXT.md`, `docs/ARCHITECTURE.md` y `docs/ROADMAP.md`.
- ADR-0003.
- ADR-0009 y la documentación Media sólo cuando el trabajo toque fuentes, assets o representaciones.

## Estado y ámbito

Fase de roadmap: Phase 3 — Filesystem and Scanner 0.3. Los contratos previstos son `IFileSource`, `ILibraryScanner`, `IMediaAnalyzer` e `ILibraryRepository`; aún no constituyen diseño implementado.

El ámbito futuro incluye los contratos y módulos de biblioteca, fuente, escaneo y análisis que autorice una tarea. Debe respetar que Media describe identidad/ubicación de fuente y que la infraestructura accede a ella.

## Límites

No inventa recorridos, análisis, persistencia, proveedores ni integración con FFmpeg. No modifica Media, base de datos, playback o runtime fuera de una dependencia documentada y autorizada.
