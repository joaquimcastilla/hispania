# Library & Scanner

## Lectura obligatoria

- `CODEX.md`, `PROJECT_CONTEXT.md`, `docs/ARCHITECTURE.md` y `docs/ROADMAP.md`.
- ADR-0003.
- ADR-0009 y la documentación Media sólo cuando el trabajo toque fuentes, assets o representaciones.

## Estado y ámbito

Fase de roadmap: Phase 3 — Filesystem and Scanner 0.3. Está implementado el slice local que descubre `.mp4` bajo roots configurados, usa ffprobe y mapea MP4 con vídeo H.264 y audio AAC a un catálogo en memoria. El primer flujo se verificó manualmente de extremo a extremo con una película local.

La fase completa no está cerrada: no hay persistencia, SQLite, soporte general de formatos ni un `ILibraryRepository` implementado. Los contratos previstos siguen siendo `IFileSource`, `ILibraryScanner`, `IMediaAnalyzer` e `ILibraryRepository` según el roadmap.

El ámbito futuro incluye los contratos y módulos de biblioteca, fuente, escaneo y análisis que autorice una tarea. Debe respetar que Media describe identidad/ubicación de fuente y que la infraestructura accede a ella.

## Límites

No amplía recorridos, análisis, persistencia, proveedores ni integración con FFmpeg fuera del slice autorizado. No modifica Media, base de datos, playback o runtime fuera de una dependencia documentada y autorizada.
