# HISPANIA v0.7.0 — Step 2.1-V3: validación final de migración

**Estado técnico: PASS en SHA `74f442123fec1aa37f71e29abaee90d5761e5fc4`. Aceptación contractual: pendiente de aprobación PostgreSQL.** La matriz hospedada Windows/Linux y el despacho manual pasaron con RC1 Complete oficial, migración, validación funcional y rollback en fixtures temporales. Step 2.1 global no se declara cerrado por este informe.

## Contrato y política

- Fuente: Complete oficial `0.7.0-rc.1`, Git SHA `9b5abefe4c6b631360d505587748aedcb6c522d0`, `sourceDirty=false`.
- Destino: Platform-only SQLite, misma versión y mismo RID (`win-x64` o `linux-x64`). La ejecución requiere el SHA-256 de manifiesto mostrado por el plan inspeccionado.
- PostgreSQL: se rechaza en preflight antes de crear staging, marker o backup. No se convierte a SQLite ni se altera soporte PostgreSQL de Complete. **La política permanece provisional, pendiente de aprobación expresa.**
- Datos: fixture efímero bajo `%TEMP%`; usuario, Inventory y catálogo Media histórico con filas, raíz externa con archivo referenciado/conservado y secreto canario. Tras la migración se verifica SHA e integridad de SQLite; tras el backup/rollback se verifican filas e integridad porque SQLite puede reconstruir páginas con bytes diferentes.
- Nunca se utilizan HOME, bases de datos, Coordinator, servicios o datos de producción.

## Fallos y recuperación

Los checkpoints se proporcionan solo por métodos `internal` visibles a los assemblies de pruebas; no existe variable de entorno de fault injection ni argumento de producción. Las excepciones se inyectan en el mismo proceso de prueba; la terminación abrupta usa `Environment.FailFast` en un harness hijo lanzado por el test. El test espera al hijo y lo mata únicamente si excede el timeout.

| Punto | Resultado esperado y validado localmente |
|---|---|
| Durante preparación de staging (después del primer archivo copiado) | Marker `staging`; recuperación restaura Complete y aparta `app.new` parcial. |
| Antes del backup | Estado y binarios Complete sin cambios; recuperación. |
| Después de crear backup, antes de verificarlo | Datos y binarios sin conmutar; bundle retenido; recuperación. |
| Durante la verificación SQLite del bundle | Estado original intacto; recovery pre-start. |
| Después de verificar el backup | Bundle verificable y binarios Complete; recovery pre-start. |
| Antes de la conmutación | Sin cambio de aplicación; recovery pre-start. |
| Durante conmutación, tras mover Complete a `app.old` | `app.old` permite restaurar Complete; staging queda apartado. |
| Tras instalar Platform-only y antes de terminar metadata | Marker `switching`; recovery restaura Complete y preserva los directorios desplazados. |
| Excepción al escribir metadata después de conmutar | Mismo recovery pre-start; no se usa el nuevo host. |
| Terminación abrupta real del proceso hijo durante staging | Child termina con código no cero; marker y `app.new` parcial; recovery devuelve Complete y no deja proceso hijo. |
| Interrupción de rollback tras mover el state actual | Marker rollback contiene nombres relativos validados; recovery instala state respaldado, vuelve a Complete y completa metadata. |
| Reejecución posterior | Después de recuperar Complete, se repite el plan y la migración completa; migración repetida tras éxito devuelve el bundle original. |

En cada recuperación controlada el gate comprueba manifiesto Complete, inexistencia de `app.new`/`app.old`, integridad y hashes de Inventory/Media, usuario, secreto y fichero externo. Tras la migración final comprueba arranque, readiness, autenticación, persistencia, ausencia del endpoint Inventory y rollback. Los directorios desplazados `app.staged-*`, `app.interrupted-*`, `app.failed-*` y bundles no se eliminan durante recuperación: se conservan para inspección.

## CI y resultado disponible

`.github/workflows/phase21v2-migration.yml` admite `workflow_dispatch` y un `push` limitado a la rama candidata, restringe repository/actor/rama, solicita solo `contents: read`, no escucha eventos PR, no configura secretos, desactiva persistencia de credenciales y utiliza `windows-2025` y `ubuntu-24.04`. El push de bootstrap fue necesario porque GitHub no aceptaba el despacho manual mientras el workflow no existía en `main`; no se modificó `main`. Tras completar el run de push se despachó manualmente sobre el mismo SHA. Retiene ZIP/manifiestos verificados, manifiesto del destino, salida de plan/migración/rollback, logs, TRX y resumen de pruebas. El script comprueba que exactamente el gate obligatorio se descubrió y pasó, sin omitidos.

Runs vinculados al SHA publicado `74f442123fec1aa37f71e29abaee90d5761e5fc4`:

| Trigger / run | Windows `windows-2025` | Linux `ubuntu-24.04` | Resultado |
|---|---|---|---|
| [Push 38638443047](https://github.com/joaquimcastilla/hispania/actions/runs/38638443047) | PASS, gate 1/1 | PASS, gate 1/1 | PASS; duración 3m14s |
| [Manual `workflow_dispatch` 38638891561](https://github.com/joaquimcastilla/hispania/actions/runs/38638891561) | PASS, gate 1/1 | PASS, gate 1/1 | PASS; duración 3m41s |

En ambos runs: **1 passed, 0 failed, 0 skipped** en cada runner. Se descargaron los artifacts por plataforma; contienen TRX, salida de pruebas, plan/migración/rollback, evidencias del artefacto publicado y hashes.

El primer run de validación falló en bootstrap con código 5 porque el fixture común fijaba `Ffprobe__ExecutablePath=ffprobe`, una ruta relativa que RC1 rechaza. El log completo confirmó `Missing or invalid dependency: ffprobe`. Se corrigió exclusivamente la infraestructura del test: el workflow instala y verifica FFmpeg, registra las rutas absolutas y el fixture las consume. Después pasaron ambas plataformas. No se alteró el Core para forzar el resultado.

Hashes de los paquetes Complete RC1 verificados en los dos runners:

- Windows ZIP: `5a83527edc208552372f8452bfbf1037e0fa761b7e13b6c6ae9a13512e2a7eaf`; manifiesto: `61b820965c39aa62e3f835d4f1137956744d576f3911a52979056d67348aa9c5`.
- Linux ZIP: `5ab9db0683fe5edd9eb9e5ddb274155758512ce7c0f29d01bff32984b601005d`; manifiesto: `65611934dc81ec38aec97ca8f99155277f98e56fcd6caa7332d6c1834a981335`.

Artefactos Platform-only del run manual:

- Linux x64: manifiesto SHA-256 `a5bb301bb9cf2fff03caf3aeba02dcc865dbdcadac79296b980c634e17b49afb`; ZIP SHA-256 `239ab15d92b29c11f3c40d34d34f9a06cb143e2bf80e002e036a93982881945b`.
- Windows x64: manifiesto SHA-256 `211d6d06fada0d3466993eccb79969d5b552a108131707e5ad56bdf30b673c04`; ZIP SHA-256 `fefb00f6fe76091421e8c4ec5a5dc118037d9cadfc11a539589b38c86f42c7bb`.

`actionlint 1.7.12` se descargó desde el release oficial. Se verificó SHA-256 del archivo de checksums (`433028cf0ba3c42163ea1a668dedce30fcdbe84fe912b1a5e288c006eab8a4f5`) y luego el checksum oficial del ZIP Windows x64 (`6e7241b51e6817ea6a047693d8e6fed13b31819c9a0dd6c5a726e1592d22f6e9`). Resultado: `actionlint .github/workflows/phase21v2-migration.yml` terminó con código 0 y sin diagnósticos.

La matriz local Windows x64 pasó después de los ajustes: **1 passed, 0 failed, 0 skipped**, duración aproximada 80 s. `actionlint 1.7.12` terminó con código 0 y sin diagnósticos; `git diff --check` también pasó. La instalación de FFmpeg queda limitada a runners hospedados y fixture efímero; no se toca el entorno de producción.

## Limitaciones y decisión

- En este host WSL1 no es compatible y no hay Linux instalable; la aceptación Linux se ejecutó realmente en `ubuntu-24.04` hospedado.
- La política PostgreSQL está implementada fail-closed pero pendiente de aprobación expresa; no se declarará aprobada por esta intervención.
- La recuperación pre-start conserva los directorios previos para análisis, no los purga automáticamente.
- El run manual verificó ambos runners, pero Step 2.1 global y la aceptación contractual permanecen fuera del cierre V3 mientras PostgreSQL no tenga aprobación expresa.

## Publicación

Commits V3 publicados en la rama candidata: `2060d4d`, `3ffb059`, `c3e557a`, `b42af4c`, `2749bb8`, `5911e95` y `74f4421`. La rama remota se verificó en SHA `74f442123fec1aa37f71e29abaee90d5761e5fc4` durante ambos runs PASS. El informe general local sin seguimiento `docs/operations/STEP-2-1-FINAL-ACCEPTANCE.md` se conserva intacto y fuera del commit. No se realizó merge a main, force-push, tag, release, cambio al Coordinator, modificación de otro worktree ni inicio de Step 2.1-W o Step 3.
