# Phase 16 — Schema Evolution & Historical Data Upgrades

Estado: implementación local lista para CI; cierre condicionado al gate PostgreSQL-live de Linux. Rama `feature/phase-16-historical-data-upgrades`, creada tras fetch desde exactamente `5e2d7a526f6a21e392367e47341ef94ce35fab09`. Distribución 0.7.0, baseline 0.7; 22 ejes independientes, 0 Public Stable, 5 familias/38 entradas Stable Candidate. No se inicia Phase 17.

## Auditoría de stores y ownership

| Store / owner del ledger | Estado histórico → actual | Mecanismo / escritor |
|---|---|---|
| Platform.Identity | sin ledger → 1 | SQLite; plan propio, archivo compartido con RBAC |
| Platform.Authorization | sin ledger → 1 | SQLite; plan propio; valida también el owner Identity antes de escribir |
| Platform.Events | sin ledger → 1 | SQLite; escritor transaccional; recuperación de Processing según reglas existentes |
| Platform.Jobs | sin ledger → 1 | SQLite; columnas heredadas dentro de la migración; leases/idempotency existentes |
| Platform.Assets | sin ledger → 1 | SQLite metadata; bytes pertenecen al provider Filesystem |
| Platform.Audit | sin ledger → 1 | SQLite; append semántico |
| Inventory.Persistence | sin ledger → 1 | SQLite; movimientos/stock/AssetRefs del dominio Inventory |
| Platform.Coordinator | sin ledger → 1 | SQLite y lock exclusivo del coordinador; single coordinator |
| Media.Persistence.SQLite | EF 1 → 5 y EF 5 → 5 | `__EFMigrationsHistory`, lineage EF existente y aliases legacy reconocidos |
| Media.Persistence.PostgreSQL | EF InitialPostgreSql → mismo lineage | `__EFMigrationsHistory`, guard previo, locking propio de EF/PostgreSQL |
| Media.Playback.Experiments | JSON SchemaVersion 1 → 1 | reemplazo atómico del archivo; estado corrupto/futuro bloquea también Save/Remove |
| Platform.Storage.Filesystem | layout opaque + legacy → ambos legibles | archivos de bytes, sin ledger independiente; su metadata evoluciona con Platform.Assets |

El índice JSON de biblioteca es una exportación derivada del catálogo EF, no el catálogo autoritativo. Outputs de transcoding y registros/parciales de transferencia distribuida son temporales recuperables/limpiables por el lifecycle existente; no constituyen un segundo schema de instalación. No se inventan migraciones para ellos. El host conserva su restricción de escritor único para estado de playback y coordinador; este trabajo no añade HA.

## SQLite explícito y forward-only

La mecánica interna source-linked `SqliteSchemaUpgrade` no es un SDK público ni una migración global: cada constructor conserva su DDL y su owner. `hispania_schema_history(owner,version,migration_id)` registra `0001_baseline07_adoption`. CurrentSchemaVersion=1; MinimumSupportedSchema=0 significa únicamente el shape legacy conocido, no cualquier archivo no versionado. AppliedMigrations contiene los IDs ordenados.

La migración 1 adopta el DDL histórico sin reescribir IDs ni filas; las columnas ad hoc conocidas quedan registradas dentro de ese paso. Lectura del historial antes del lock, `BEGIN IMMEDIATE`, nueva comprobación, DDL + ledger + commit en una transacción. Repetir startup es idempotente. SQLite coordina los procesos concurrentes. WAL se habilita después de validar/adoptar; un schema futuro se rechaza antes de cambiar journal mode. Se validan columnas de los shapes soportados; no se declara compatibilidad con modificaciones arbitrarias de tipos/constraints hechas por terceros.

Versiones futuras, IDs desconocidos, duplicados, huecos y ledger malformado abortan. Identity/RBAC comprueban ambos owners porque comparten archivo. Archivo ausente crea schema nuevo; archivo corrupto o incompatible requiere recuperación explícita y se preserva. No hay reset, borrado, downgrade ni intento de reparar silenciosamente. Las transacciones interrumpidas se dejan al recovery nativo de SQLite.

Media no recibe el ledger de Platform. Guard EF acepta únicamente un prefijo conocido (tras normalizar aliases permitidos), rechaza futuro/unknown/gaps antes de MigrateAsync y mantiene exactamente las cinco migrations SQLite y la migration PostgreSQL existentes. Los fixtures prueban el salto real SQLite 1 → 2 → 3 → 4 → 5 y las lecturas de metadata del esquema final. PostgreSQL usa el SQL generado por el IMigrator histórico, filas pobladas y el runner actual en un schema aislado.

Un binario histórico 0.7 no recibe retroactivamente estos guards: rollback a ese ejecutable sobre archivos adoptados no se considera seguro. Los binarios posteriores deben participar en la política de rechazo de schema futuro. Volver a un binario antiguo requiere un procedimiento operativo compatible y datos previos adecuados; backup/restore integral queda para Phase 17.

## Provenance y corpus inmutable

Source exacto `78f291fdacf9442c6e2d8e23094dc0e309828693`. Productor fuera de solution/CI en `tools/historical-data-capture`; sus ProjectReferences apuntan exclusivamente a un `git archive` de ese SHA mediante HistoricalRoot. Se compararon 172 inputs históricos de producción/build (core, infrastructure, persistence JSON y props) contra el archivo Git, sin diferencias tras normalizar CRLF. Los constructors antiguos crean los SQLite; el IMigrator antiguo genera los schemas EF/SQL PostgreSQL; la población determinista de TEST ONLY agrega filas sobre esos schemas. El JSON procede de Save/SaveAssignment antiguos. Lectores antiguos verificaron PBKDF2, ambos content handles, DTO del coordinador y Work/metadata manual.

La instrumentación inicial usó UUID BLOB para Media; el lector antiguo también la rechazó. Se corrigió antes de congelar el corpus: el provider EF histórico usa GUID TEXT canónico aunque el DDL declare BLOB. Este ajuste de captura no es una reparación de la migración actual. No se ha regenerado un fixture para resolver un fallo del código actual.

15 artefactos: nueve SQLite, dos blobs de cinco bytes, dos SQL PostgreSQL, un JSON playback y un snapshot semántico. Los dos owners de seguridad comparten un SQLite real. Credenciales, salts, tokens y payloads son exclusivamente sintéticos y están etiquetados TEST ONLY. Los expiries 2099 sirven para probar estados sin depender del reloj; no cambian la política de emisión de sesiones.

Manifest independiente `compatibility/data-baseline-0.7.json`, SHA256 `68a28dafbf9709284b361dacf8113219ac44d130d0ca77a7646e6df29c7bc898`. Los hashes de cada artefacto aparecen abajo. En las entradas blob y snapshot, expectedCurrentSchema describe la adopción de la instalación asociada, no un ledger incrustado en esos archivos.

Phase 15 conserva sus 27 artefactos, 36 API types, manifest SHA256 `4cb34019df3824596917297e2c81a992f881f45bb2cc3a534752b6b970446da5` y freeze index `de8431a7f42e4a36979cc372a574cb62094f37399e9ee88a167d17f3a2bb005b`. Su membership checker delega exclusivamente `data/` al nuevo checker; cualquier alta o cambio en el corpus contractual sigue fallando. El checker Phase 16 rechaza datos no registrados y sustituciones incluso con rehash del manifest. No hay captura/regeneración en CI.

| Artefacto | SHA256 |
|---|---|
| `data/0.7/BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB.blob` | `486ea46224d1bb4fb680f34f7c9ad96a8f24ec88be73ea8e5a6c65260e9cb8a7` |
| `data/0.7/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa.blob` | `2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824` |
| `data/0.7/assets.sqlite` | `1cc04c613dedf5319eb4eaa70b706fb9a2963ee69bf2860e3d4cfaa4794d39fc` |
| `data/0.7/audit.sqlite` | `ddca58d5214b4091e953a20966af3b987fd943f0f4ebc9e0ddc605e70ba7eab9` |
| `data/0.7/coordinator.sqlite` | `a11ca4e255d989cf309e838629f309837183a55ae860dec3b7031a61ae4037fa` |
| `data/0.7/events.sqlite` | `975e77c84e791a9fcfd968912802ba994ed3d5eabaffe1f0e40ddfdf702418d4` |
| `data/0.7/identity-authorization.sqlite` | `ab3e3c9faaa24112261e24d05f2cd7ce595d1c13f94234b554237cb1a41a27c2` |
| `data/0.7/inventory.sqlite` | `98053d3014a3e30b65c66d6dd22d3ceaad20b2170ea69cab4f9a7bfc404f8064` |
| `data/0.7/jobs.sqlite` | `326aaea208a27000e64b81384043516643d7ab73ddc1cf7152aa00273e013113` |
| `data/0.7/media-initial.sqlite` | `4bd2e45142a469ca4d3e14970fe1ed124453f824848130a7f4e17e99c8fe6660` |
| `data/0.7/media-library.sqlite` | `4d2c7f4d5521a986ebc9acedd2dcda0086e0ae11f7767ad3a7ff232a283b7ecf` |
| `data/0.7/media-postgresql-data.sql` | `a780fd5db75e3b53d21f630f343227d0febd4c445e083743c658567689a3ce67` |
| `data/0.7/media-postgresql-schema.sql` | `62d50a3e2ee7ce2dd13659effa20462a6cf4e51b7925f0fa55deab0dbbd488ab` |
| `data/0.7/playback-experiment.json` | `15bfd2c96eec5c0d9f97498d59adabc9978986e26fe813ca22bb079fd56b1b4a` |
| `data/0.7/semantic-snapshots.json` | `8f9e89f9a6c43844319f4426929965fe1addd5a22dc3802581c4b14b3eae508a` |

## Invariantes y evidencia

- Identity: cuatro users, hash/salt PBKDF2 válido, username normalizado, sesión activa/revocada/expirada y disabled user; autenticación actual y contenido histórico conservados.
- RBAC: permisos efectivos 2/1/0, default-deny y bootstrap consumido, sin ampliar grants.
- Events: cinco eventos, seis deliveries, dos subscribers, attempts y estados Pending/Processing/retry/Delivered/DeadLetter; recuperación conserva ownership y no promete exactly-once.
- Jobs: siete jobs y attempts, Pending/future/Running/Succeeded/Failed/Cancelled/DeadLettered; idempotency conserva JobId y lease recovery añade el intento esperado.
- Assets: metadata/provider/scopes y opaque/legacy bytes; writer actual crea handles opacos. Audit lee y añade registros sin alterar los anteriores.
- Inventory: stock 7, dos movimientos, AssetRef; UNIQUE/FK/CHECK siguen rechazando operaciones inválidas.
- Coordinator: generaciones 7/8/9 y high-water preservados; nueva ejecución incrementa generación y completion/failure con generación anterior se rechazan.
- Media: IDs nativos legibles, assertions manuales, artwork, settings/locks, people/credits; snapshots comparan todas las filas/columnas históricas, integrity_check y foreign_key_check.
- Safety: nueve casos de schema futuro usan también el ejecutable hijo actual y hash no-write; cuatro historiales inválidos, shape incompatible, corrupto frente a ausente, multi-hop con rollback y gap negativo, dos procesos concurrentes repetidos tres veces.
- Interrupción: muerte real de un proceso después de DDL + UPDATE dentro de la transacción, en journal DELETE y WAL. Reapertura comprueba rollback de filas, tabla y ledger, después completa el upgrade.

Los tests sólo mutan copias temporales y comparan datos contra el snapshot histórico. Un test EF preexistente fabricaba un hueco (4 eliminada, 5 retenida); su preparación ahora usa el Down de EF exclusivamente en la DB temporal del test para producir un prefijo real 1–3 antes de renombrar aliases. El runner de producción sigue siendo forward-only; esto no es un fixture histórico 0.7 ni un cambio del corpus.

## Startup y observabilidad

La inicialización de persistencia resuelve los stores críticos antes de completar StartAsync del host. Un upgrade fallido aborta el inicio; no publica readiness ni permite requests sobre estado parcialmente disponible. Cada archivo es su propia frontera transaccional: no existe atomicidad global entre stores. No hay live migration mientras se atienden requests. Startup es suficiente para esta fase; no se añade una CLI de migración. El estado interno Read responde versión/mínimo/IDs y los checkers read-only validan el corpus, sin reparar archivos.

Trace/DiagnosticListener registra store, from/to, migration ID y success/failure, sin datos, rutas ni SQL. Meter `Hispania.SchemaMigration` expone duration/failures con tag store de cardinalidad fija. No se usan IDs de usuario, job, token o archivo como etiquetas. Las excepciones externas dan instrucciones de preservar el archivo/usar ejecutable compatible; el inner exception queda para diagnóstico interno.

## Validación y CI

`tools/test-phase16-historical-upgrades.ps1` exige 36/36 casos portables sin skips, dos análisis deterministas y seis negativos aislados. Linux CI añade `-RequirePostgreSql`, exige conexión y exactamente un test live passed; no acepta el skip condicional. Windows conserva el único skip PostgreSQL preexistente cuando no dispone del provider live.

Validación local final: Phase 16 36/36 y Python 6/6; Phase 15 16/16 y Python 10/10; Phase 14 3/3; .NET Windows 597 passed, 0 failed, 1 skip PostgreSQL preexistente (598 total); closure/concurrency 210/210 con 20 repeticiones; Web 74/74 y builds Modern/Classic. Full y Platform builds: 0 warnings/errors. Ver también PROGRESS-LOG.md. CI está configurado para ambos OS; este trabajo no se ha subido. La evidencia PostgreSQL-live y la confirmación de ambos jobs en el SHA de Phase 16 quedan pendientes. No se declara Phase 16 cerrada.

No se implementan backup/restore, snapshots coordinados, HA, deployment ni frontend modular. La compatibilidad demostrada se limita al baseline poblado elegido y sus invariantes verificadas; no demuestra recuperación tras pérdida del disco.
