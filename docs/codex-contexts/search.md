# Search

## Lectura obligatoria

- `CODEX.md`, `PROJECT_CONTEXT.md`, `docs/ARCHITECTURE.md` y `docs/ROADMAP.md`.
- ADR-0003.
- Documentación de los dominios indexados cuando exista una tarea concreta.

## Estado y ámbito

No existe una fase autónoma de búsqueda: aparece como parte del pipeline futuro y del baseline de Phase 10. `ISearchProvider` es un contrato previsto, no diseñado ni implementado.

## Límites

No se decide índice, motor, esquema, consultas, ranking ni dependencia externa. No modifica dominio Media, persistencia, API o módulos de metadata sin autorización.
