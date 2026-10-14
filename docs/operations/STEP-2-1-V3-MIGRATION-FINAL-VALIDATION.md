# HISPANIA v0.7.0 — Step 2.1-V3: validación final de migración

**Estado de esta entrega: PARTIAL hasta CI real Windows/Linux.** La matriz local Windows x64 pasó con RC1 Complete oficial, destino Platform-only generado en el mismo árbol, fallos controlados por checkpoint y terminación abrupta de un proceso hijo. El workflow es válido según actionlint. Falta publicar estos cambios para ejecutar el workflow hospedado, obtener evidencia Linux x64 y vincular resultados al SHA remoto. La aceptación PostgreSQL sigue pendiente del propietario.

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

`.github/workflows/phase21v2-migration.yml` admite `workflow_dispatch` y un `push` limitado a la rama candidata, restringe repository/actor/rama, solicita solo `contents: read`, no escucha eventos PR, no configura secretos, desactiva persistencia de credenciales y utiliza `windows-2025` y `ubuntu-24.04`. El push de bootstrap es necesario porque GitHub solo acepta `workflow_dispatch` cuando el workflow ya existe en la rama por defecto; cambiar `main` está prohibido en esta intervención. Tras ese primer run en la rama, se intentará el despacho manual sobre el mismo SHA. Retiene ZIP/manifiestos verificados, manifiesto del destino, salida de plan/migración/rollback, logs, TRX y resumen de pruebas. El script comprueba que exactamente el gate obligatorio se descubrió y pasó, sin omitidos.

`actionlint 1.7.12` se descargó desde el release oficial. Se verificó SHA-256 del archivo de checksums (`433028cf0ba3c42163ea1a668dedce30fcdbe84fe912b1a5e288c006eab8a4f5`) y luego el checksum oficial del ZIP Windows x64 (`6e7241b51e6817ea6a047693d8e6fed13b31819c9a0dd6c5a726e1592d22f6e9`). Resultado: `actionlint .github/workflows/phase21v2-migration.yml` terminó con código 0 y sin diagnósticos.

La matriz completa Windows x64 ejecutada al preparar V3 pasó: **1 passed, 0 failed, 0 skipped**, duración 84 s; el resumen del script confirmó `count=1; skipped=0`. El run confirmó hashes RC1 Windows/Linux y generó el artefacto Platform-only local, pero todavía no es CI ni cuenta como Linux ejecutado. Manifiesto destino SHA-256 `1d27964bdc1dbec43c8ba74393de6291a8368ba45834066ad098a1e31afcf6d5`; ZIP destino SHA-256 `1e715ed4f0052cada9a325df1eb375b0ffcf630b4b02ee41cd9c029655822d03`. El TRX, resumen, salida de consola y hashes están bajo `%TEMP%\hispania-phase21v3-local-windows`.

## Limitaciones y decisión

- En este host WSL1 no es compatible y no hay Linux instalable; Linux x64 real requiere el job `ubuntu-24.04` hospedado.
- El primer intento manual devolvió HTTP 404 porque el workflow todavía no está en `main`. La actualización añade bootstrap `push` limitado a la rama candidata; al completar ese run se hará el despacho manual. Hasta que ambos se completen no hay conclusión final Windows/Linux de CI.
- La política PostgreSQL está implementada fail-closed pero pendiente de aprobación expresa; no se declarará aprobada por esta intervención.
- La recuperación pre-start conserva los directorios previos para análisis, no los purga automáticamente.
- Hasta que ambas matrices hospedadas pasen, V3 queda PARTIAL y Step 2.1-V no se cierra como aceptación final.

## Publicación

V3 autoriza publicar a la rama candidata cuando las comprobaciones locales y actionlint estén correctos. No se permite merge a main, force-push, tag, release, cambios al Coordinator, otros worktrees, Step 2.1-W ni Step 3.
