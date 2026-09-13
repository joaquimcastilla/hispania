# Database & Persistence

## Lectura obligatoria

- `CODEX.md`, `PROJECT_CONTEXT.md`, `docs/ARCHITECTURE.md` y `docs/ROADMAP.md`.
- ADR-0003 and ADR-0016 (Accepted).
- La documentación del dominio que se vaya a persistir, incluida Media cuando corresponda.

## Estado y ámbito

Persistence 0.1 está completa como durable Library playable slice. Stage 1 completó los contratos de import identity y reconciliación; Stage 2 completó el catálogo SQLite durable, schema/migrations, composición de arranque y validación de restart/rehydration.

La frontera de escritura es `ILibraryCatalogStore`; `IMediaCatalogReader` conserva el contrato síncrono usado por Playback y se respalda con un snapshot en memoria cargado desde SQLite. EF Core está confinado a `infrastructure/persistence`; las entidades EF son distintas de los objetos Domain y las migrations pertenecen al provider SQLite.

La identidad de importación es `(rootKey, normalizedRelativePath)`, separada de los IDs Domain. Un rescan sin cambios y un reinicio de proceso reutilizan los IDs Domain. Los estados persistidos son `Active`, `Disappeared`, `Unsupported` y `ProbeFailed`; un completed scan hace commit atómico y un fallo o cancelación conserva el catálogo válido anterior.

El graph playable que hace roundtrip real comprende `Work`, `MediaEntity`, `Timeline`, assets video/audio y roles, representaciones y perfiles H.264/AAC del slice, `MediaSource`, bindings y `AssetCoverage`. El arranque sigue migrations → snapshot load → initial scan → readiness. Direct Play y HTTP Range se validan sobre el graph rehidratado.

La validación E2E se realizó con `Seduction and Snacks (2021).mp4` (MP4 / H.264 / AAC), sin versionar el archivo. Los dos boots produjeron `MediaEntityId` `01a09cf2-f701-7054-97e6-73144ad64f15`; el stream completo devolvió `200` y el rango `100-199` devolvió `206` con `Content-Length: 100`.

Persistence 0.2 — Full Media Graph Persistence sigue futura. Metadata Providers 0.1 — Stage 1 añade un slice limitado: roundtrip de ExternalIdentifierAssociation, lifecycle/relations, MetadataAssertion/MetadataValue/MetadataProvenance/supersession, ArtworkReference y estado operativo de refresh mediante `IMetadataEnrichmentStore`. Se difieren: workflows y rehydration completos de Media 2D restante; Release, ReleaseGroup, Collection, Creator/Contribution; perfiles text/image; coverage intervals fuera del slice; PostgreSQL; hashing/content identity; rename/move reconciliation; política de retención de `Disappeared`; backup/restore automatizado; y evolución async de `IMediaCatalogReader`.

La configuración inicial es `Persistence:Sqlite:Path` (también `Persistence__Sqlite__Path`). Si no se proporciona, el host usa el directorio local de datos de aplicación, fuera del árbol fuente. Las migrations se aplican y el snapshot se carga antes del scan inicial; un fallo no se captura, bloquea el arranque/readiness y evita que el scanner posterior se ejecute.

Las interfaces viven en contratos o dominio de aplicación y las implementaciones en módulos; el dominio no conoce SQL.

## Límites

No amplía Persistence 0.1 hacia PostgreSQL o full Media graph persistence sin una tarea autorizada de Persistence 0.2. No cambia el modelo de dominio, contratos o módulo propietario sin coordinación.
