# Database & Persistence

## Lectura obligatoria

- `CODEX.md`, `PROJECT_CONTEXT.md`, `docs/ARCHITECTURE.md` y `docs/ROADMAP.md`.
- ADR-0003.
- La documentación del dominio que se vaya a persistir, incluida Media cuando corresponda.

## Estado y ámbito

La persistencia inicial está prevista en Phase 3 como `ILibraryRepository` con una implementación SQLite. No hay modelo de persistencia, esquema ni proveedor aprobados en este contexto.

Las interfaces viven en contratos o dominio de aplicación y las implementaciones en módulos; el dominio no conoce SQL.

## Límites

No introduce SQLite, PostgreSQL, esquemas, migraciones, ORM ni dependencias antes de una tarea autorizada. No cambia el modelo de dominio, contratos o módulo propietario sin coordinación.
