# Database & Persistence

## Lectura obligatoria

- `CODEX.md`, `PROJECT_CONTEXT.md`, `docs/ARCHITECTURE.md` y `docs/ROADMAP.md`.
- ADR-0003.
- La documentación del dominio que se vaya a persistir, incluida Media cuando corresponda.

## Estado y ámbito

Persistence 0.1 está en implementación. La frontera de escritura es `ILibraryCatalogStore`; `IMediaCatalogReader` conserva el contrato síncrono usado por Playback. La implementación inicial usa SQLite detrás de Infrastructure, con EF Core confinado a `infrastructure/persistence` y migrations propiedad del provider.

La configuración inicial es `Persistence:Sqlite:Path` (también `Persistence__Sqlite__Path`). Si no se proporciona, el host usa el directorio local de datos de aplicación, fuera del árbol fuente. Las migrations se aplican y el snapshot se carga antes del scan inicial; un fallo bloquea el arranque/readiness.

Las interfaces viven en contratos o dominio de aplicación y las implementaciones en módulos; el dominio no conoce SQL.

## Límites

No introduce SQLite, PostgreSQL, esquemas, migraciones, ORM ni dependencias antes de una tarea autorizada. No cambia el modelo de dominio, contratos o módulo propietario sin coordinación.
