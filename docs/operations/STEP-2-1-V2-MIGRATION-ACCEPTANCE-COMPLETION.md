# HISPANIA v0.7.0 — Step 2.1-V2: aceptación de migración Complete RC1 → Platform-only

**Estado: PARTIAL.** El gate Windows local pasó con catálogo Media histórico poblado, y se preparó un gate repetible para Windows/Linux con evidencia descargable. CI/Linux y los fallos inyectados en todos los límites de interrupción siguen pendientes. No se declara PASS global.

## Alcance y política de datos

La ruta sigue aceptando únicamente Complete `0.7.0-rc.1` oficial (`9b5abefe4c6b631360d505587748aedcb6c522d0`, `sourceDirty=false`) hacia Platform-only SQLite del mismo RID x64. El plan debe inspeccionarse antes de ejecutar y la migración consume el SHA-256 exacto del manifiesto objetivo. PostgreSQL queda rechazado en preflight antes de marker, staging o backup; no se ha añadido soporte PostgreSQL.

La migración conserva en su ubicación los directorios `config`, `secrets`, `state`, `logs`, `backups` y las raíces externas configuradas. Los binarios previos quedan en `app.old`. El backup Phase 17 conserva las bases de datos gestionadas y permite rollback tras el primer arranque; para recuperarse antes de arrancar el host nuevo se usa `recover-before-start`. La configuración y los secretos no se incluyen en el bundle y permanecen en HOME. Media e Inventory no se montan en Platform-only: conservar sus bytes no implica exponer sus endpoints.

El fixture usa exclusivamente un HOME temporal con usuario de prueba, Inventory creado por la API, el catálogo `compatibility/data/0.7/media-library.sqlite` poblado (incluye entidades y metadata), un fichero en raíz externa, y un canario de secreto. Se fija la ruta SQLite Media en `state/media.sqlite`, que es la ruta gestionada canónica del backup Phase 17. El fixture no usa el perfil real ni datos de producción.

## Matriz de aceptación

| Gate | Estado | Evidencia / límite |
|---|---|---|
| Hashes ZIP y manifiestos oficiales Windows/Linux x64 | PASS local | Script comprueba hashes fijados antes de extraer o ejecutar. |
| Identidad Complete RC1, versión, commit y `sourceDirty=false` | PASS local | Script comprueba identidad del manifiesto embebido; `Deployment verify` se ejecuta al crear el destino. |
| Fixture temporal, usuario, Inventory poblado, Media poblada, fichero externo y canario | PASS local Windows | `phase21v2-migration.trx`; comprueba filas, SQLite integrity, hashes y login. |
| Plan, SHA manifiesto destino, target corrupto, metadata alterada, PostgreSQL bloqueado, upgrade ordinario bloqueado | PASS local Windows | Incluido en el gate completo. PostgreSQL no deja `app.new` ni cambia el manifiesto Complete. |
| Backup antes del cambio, retención `app.old`, migración y arranque Platform-only | PASS local Windows | El gate confirma bundle, distribución, readiness, login y ausencia del endpoint Inventory. |
| Conservación de usuarios e Inventory al migrar y rollback | PASS local Windows | Login y fila Inventory tras migración; rollback vuelve a Complete y el usuario puede autenticar. |
| Conservación de catálogo Media poblado al migrar y rollback | PASS local Windows | Hash exacto e integridad durante migración; tras restore verifica integridad y filas Media/metadata. |
| Fallos controlados en preflight, backup, staging, switch, metadatos y primer arranque | PENDIENTE | Hay recuperación existente para markers `staging`/`switching` y restore tras arranque; no se ha probado inyección de fallo en cada límite. No inferir atomicidad no observada. |
| Migración Linux x64 real | BLOQUEADO local; CI pendiente | Este equipo tiene WSL1 incompatible y no hay distribución Linux instalada. El workflow alojado Ubuntu está preparado; hasta que termine, Linux no cuenta como ejecutado. |
| CI Windows/Linux con evidencia retenida | PENDIENTE | Workflow `phase21v2-migration.yml`, `workflow_dispatch`, sin secretos, `contents: read`; no se ha despachado desde esta sesión. |
| PostgreSQL fuera de alcance aprobado explícitamente | PENDIENTE de decisión del usuario | El encargo recomienda Opción A, pero esta evidencia registra la recomendación como propuesta, no como aprobación expresa. El bloqueo preventivo ya está implementado. |

## Hashes oficiales

| Asset | SHA-256 | Resultado |
|---|---|---|
| RC1 Complete Windows x64 ZIP | `5a83527edc208552372f8452bfbf1037e0fa761b7e13b6c6ae9a13512e2a7eaf` | Coincide localmente |
| RC1 Windows x64 external manifest | `61b820965c39aa62e3f835d4f1137956744d576f3911a52979056d67348aa9c5` | Coincide localmente |
| RC1 Complete Linux x64 ZIP | `5ab9db0683fe5edd9eb9e5ddb274155758512ce7c0f29d01bff32984b601005d` | Coincide localmente; no ejecutado |
| RC1 Linux x64 external manifest | `65611934dc81ec38aec97ca8f99155277f98e56fcd6caa7332d6c1834a981335` | Coincide localmente; no ejecutado |

El hash del artefacto Platform-only se genera por ejecución y se conserva en `platform-publish.log` junto con la identidad de fuente. El test también registra el plan que contiene el manifiesto objetivo confirmado.

Ejecución local final en Windows x64: **1 passed, 0 failed, 0 skipped**, 34 s. Artefacto fixture `win-x64`, manifiesto SHA-256 `84d8322298c2a834fec2cd6aea67480256ebf7cbcb0b63c5228362c5f3c43184`; ZIP SHA-256 `6808fd4e496923e8000fea2152fa8a2b9efb6ce131698e66c45c3009b108c839`. Fuente de trabajo `62b5f63daed9bb6c19a886e0f7c2df7d7e94129c`, dirty local; artefacto exclusivamente temporal, no publicación. TRX registra el plan inspeccionado, SHA destino, salida de migración y salida de rollback. Evidencia bajo `%TEMP%\hispania-phase21v2-local-windows`.

## Ejecución reproducible

Windows x64, desde la raíz del repositorio:

```powershell
pwsh -NoProfile -File tools/test-phase21v2-migration.ps1 -ResultsDirectory "$env:TEMP\hispania-phase21v2-local-windows"
```

Linux x64, desde una distribución Linux con PowerShell 7 y .NET SDK 10:

```bash
pwsh -NoProfile -File tools/test-phase21v2-migration.ps1 -ResultsDirectory "$TMPDIR/hispania-phase21v2-local-linux"
```

El script descarga ambos ZIP/manifiestos de la publicación inmutable RC1, compara los cuatro SHA-256 esperados, valida el manifiesto Complete, extrae el artefacto correspondiente al host, publica un destino Platform-only limpio y ejecuta únicamente el test de aceptación de migración. El workflow manual repite el script en `windows-2025` y `ubuntu-24.04` y conserva logs, TRX, hashes y paquetes como artifacts.

## Interrupciones y recuperación

La inspección de la implementación indica que `staging`/`switching` conservan marker y directorios; `recover-before-start` restaura `app.old`, aparta el destino/staging interrumpidos, reescribe metadata y elimina el marker. El código exige rollback respaldado cuando el host nuevo puede haber arrancado, pues podría haber migrado estado. Esto describe el mecanismo disponible, no evidencia de inyección en todos los pasos. Pendiente ejecutar y registrar individualmente:

1. Rechazo de preflight sin cambios.
2. Error de creación/verificación del backup antes del switch.
3. Interrupción de copia/preparación `app.new`.
4. Interrupción durante el cambio de `app`/`app.old`.
5. Error al escribir metadata tras el cambio.
6. Primer arranque fallido seguido de rollback con bundle.

Para cada caso se debe verificar hash/manifiesto de `app`, presencia del marker, backup y estado, confirmar que la recuperación documentada funciona y adjuntar el log. No se marca esta sección como PASS por inspección estática.

## PostgreSQL: decisión solicitada

Opción recomendada por el encargo: mantener Platform-only SQLite-only en v0.7.0 y rechazar instalaciones Complete PostgreSQL antes de cualquier efecto lateral. La CLI ya aplica el rechazo preventivo. Falta que el usuario confirme expresamente que PostgreSQL queda fuera del alcance de migración de v0.7.0; hasta entonces la política está implementada como límite seguro propuesto, no como decisión aprobada.

## Seguridad del workflow

El workflow se activa solo por `workflow_dispatch`, verifica repositorio, actor, actor desencadenante y rama permitida; no escucha `pull_request`, no expone secretos y declara únicamente `contents: read`. Checkout desactiva persistencia de credenciales. Descargas RC1 están fijadas por hashes publicados y el código se ejecuta en runners GitHub-hosted Windows/Linux. La ejecución remota queda pendiente.

## Cambios y estado de publicación

El informe anterior de aceptación se conserva intacto. No se hizo commit ni push; no se modificó producción, `main`, servicios, releases, Step 2.1-W ni Step 3. La rama de trabajo sigue local.
