# Auditoría de biblioteca e imágenes — 2026-09-26

## Evidencia local

El Dev Launcher apunta a `movies/` y a `%LOCALAPPDATA%/Hispania/visual-test.sqlite`. La biblioteca tiene ocho carpetas con un vídeo cada una. Sólo `Seduction and Snacks (2021)` contiene dos SRT (`2_English.srt` y `3_English.srt`). El escáner recorre carpetas y admite `.mp4`, `.mkv` y `.avi`; ignora SRT y otros auxiliares. Un vídeo suelto en la raíz sigue siendo candidato legacy. La clave de importación es `(rootKey, relativePath)`, por ejemplo `movies` y `The Weather Man (2005)/The Weather Man (2005).avi`.

SQLite contiene 11 importaciones, Work, MediaEntity y MediaSource: ocho `active` y tres `disappeared`. Los tres registros desaparecidos son las rutas sueltas anteriores de Seduction, The Weather Man y Un Ratoncito. Cada carpeta actual tiene una importación activa, un Work, una MediaEntity y una fuente. La proyección HTTP devuelve ocho películas, sin duplicados visibles. No hay evidencia de cambio de `rootKey`: los 11 registros usan `movies`. Los nuevos Work de esas tres películas proceden del cambio de ruta relativa al colocar los vídeos en carpetas; la política vigente no reconcilia movimientos. Las rutas y títulos sustentan que son importaciones históricas de la misma película física, aunque no hay hash anterior que demuestre igualdad de bytes.

## Identificación e imágenes

| Película activa | Parsing | TMDB confirmado | Título TMDB localizado / original | Estado local |
|---|---|---|---|---|
| Seduction and Snacks | `Seduction and Snacks`, 2021 | `889824` | `Seduction & Snacks` / `Seduction & Snacks` | Refresh correcto; poster y backdrop persistidos |
| The Weather Man | `The Weather Man`, 2005 | `6963` | `El hombre del tiempo` / `The Weather Man` | Refresh correcto; poster y backdrop persistidos |
| Un Ratoncito Duro De Roer | `Un Ratoncito Duro De Roer`, 1997 | `6283` | `Un ratoncito duro de roer` / `MouseHunt` | Refresh correcto; poster y backdrop persistidos |

Con el token local del Dev Launcher, búsquedas TMDB reales devolvieron un candidato exacto por título y año para las tres películas, y el detalle de cada ID devolvió poster y backdrop. El token no se imprimió. En una copia de SQLite, el servidor devolvió URL efectiva de poster y detalle para las tres; los seis endpoints de poster/backdrop respondieron `200 image/jpeg` con firma JPEG válida. También se comprobó `200` en los dos posters directamente desde el CDN. Por ello, en el estado actual la ausencia de imágenes reportada no se reproduce en base de datos, proyección ni entrega HTTP. No se dispone de una captura o fallo de red del navegador del momento original para atribuir aquella observación a cache, sesión o interfaz.

## Conflicto persistido

Hay un conflicto histórico `tmdb/movie/889824` entre el Work de `Seduction and Snacks (2021).mp4` desaparecido y el Work de `Seduction and Snacks (2021)/Seduction and Snacks (2021).mp4` activo. La asociación del Work antiguo está ahora invalidada y la del nuevo, confirmada. No se observan conflictos persistidos para las otras dos películas. El conflicto es coherente con dos importaciones de la misma película tras cambiar su ruta; no hay indicio de dos películas distintas ni de un defecto por el que un escaneo de una carpeta genere dos Work. No se fusionaron ni borraron registros históricos.

## Subtítulos y reproducción

Los dos SRT externos de Seduction existen en disco, pero el descubrimiento sólo emite vídeos. No hay importación, asset de texto, asociación persistida al vídeo ni ruta de reproducción para estos sidecars. El modelo dispone de perfil técnico de texto preparatorio, pero la escritura del catálogo actual persiste sólo los assets de vídeo y audio extraídos del vídeo. Catalogación y Direct Play usan la fuente del vídeo; el reproductor no recibe pistas SRT externas.

## Incidencia adicional y validación

Con TMDB deshabilitado, el parámetro nullable `IPersonMetadataProvider?` del endpoint de personas se infería como body de un GET y la inicialización de rutas causaba `500` también en `/api/v1/media`. Se cambió a resolución opcional desde servicios. La misma instancia de prueba, sin token y sobre copia de SQLite, devolvió `200` y ocho películas tras el cambio. Esto explica un fallo posible de catálogo completo cuando falta el token, pero no la observación específica de sólo dos posters en la configuración actual.

Verificación: 412/412 pruebas .NET, 29/29 pruebas web, build .NET y web correctos, `git diff --check` sin errores. La suite SQLite incluye carpeta con vídeo y dos SRT, dos carpetas diferentes, rescaneo, reinicio, recuentos de entidades y fuentes, ausencia de duplicados visibles y conservación de una asociación TMDB existente.

## Seguimiento: seis películas MKV sin reproducción

Las seis películas reportadas (`17 otra vez`, `A través de mi ventana`, `Adicción perfecta`, `American Beauty`, `Aterriza Como Puedas` y `Un Ratoncito Duro De Roer`) son MKV. La instancia del Dev Launcher devolvía `415` para `GET /api/v1/media/{id}/stream` en las seis. El planificador permite Direct Play únicamente para MP4 H.264/AAC y el fallback FFmpeg aceptaba AVI/MP4, pero excluía MKV antes de abrirlo. Ésta es la causa demostrada del fallo HTTP.

FFprobe detectó H.264 y audio AC3/EAC3/MP3 en cinco títulos; `Aterriza Como Puedas` usa HEVC y AC3. El fallback ahora acepta MKV. Copia el vídeo H.264 al MP4 compatible y convierte el audio a AAC; para HEVC convierte vídeo a H.264 y audio a AAC. La conversión produce una derivada en la caché de reproducción y no modifica la fuente. Una prueba de integración genera MKV pequeños H.264/AC3 y HEVC/AC3, verifica los códecs del MP4 resultante y obtiene `206 video/mp4` por la ruta HTTP con rango. La suite .NET completa pasó con 414 pruebas.

La instancia ya abierta del Dev Launcher seguía ejecutando el binario anterior durante esta auditoría. Es necesario reiniciar el Launcher para cargar el cambio. El primer acceso a un MKV debe crear la derivada completa antes de responder; para los archivos de varios GB puede tardar, especialmente el HEVC. No se realizó la conversión completa de las seis películas reales ni se midió su tiempo de espera en navegador.
