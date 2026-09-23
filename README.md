# HISPANIA

Home International Server Platform for Applications, Networking, Integration & Automation

Proyecto open source para construir una plataforma moderna de home server con arquitectura modular, contratos estables y componentes intercambiables.

El primer gran vertical del proyecto será **Media**, pero el kernel debe ser suficientemente genérico para soportar en el futuro otros dominios como fotografías, archivos, backups, automatización, IA local y monitorización.

## Objetivos principales

- Kernel pequeño y estable.
- Arquitectura modular por contratos.
- Módulos sustituibles sin reescribir el resto del sistema.
- Soporte para módulos nativos y plugins aislados.
- API pública estable para cualquier cliente.
- Observabilidad y benchmarking por módulo.
- Posibilidad de probar varias implementaciones de un mismo contrato.
- Diseño compatible con un futuro ecosistema abierto de terceros.
- Media model preparado para múltiples fuentes, versiones, idiomas, pistas y timelines.
- Separación estricta entre biblioteca local, preservación, conocimiento y distribución.

## Filosofía

El sistema no debe asumir que:

- una película es un archivo;
- una canción es únicamente audio;
- una biblioteca vive sólo en una carpeta local;
- FFmpeg es la única implementación posible;
- SQLite es la única base de datos posible;
- el frontend web es el cliente principal;
- un plugin puede acceder libremente a todo el sistema.

Todo componente importante debe depender de contratos, no de implementaciones concretas.

## Estado

Pre-alpha con vertical local funcional: descubrimiento, catálogo SQLite, metadatos TMDB, editor web y reproducción Direct Play. La compatibilidad mediante FFmpeg está en fase experimental. La revisión de límites modulares y las tareas abiertas están en `docs/MODULARITY_AUDIT_2026-09-23.md` y `TODO.md`.

Ver:

- `VISION.md`
- `PROJECT_CONTEXT.md`
- `CODEX.md`
- `docs/ARCHITECTURE.md`
- `docs/ROADMAP.md`
- `docs/MODULE_SYSTEM.md`
- `docs/MEDIA_MODEL.md`
- `docs/PRESERVATION_NETWORK.md`
