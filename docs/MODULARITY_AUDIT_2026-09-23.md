# Revisión de modularidad y estado — 2026-09-23

## Alcance y resultado

Revisión del árbol completo de proyectos .NET, sus referencias, la composición ASP.NET, las fronteras de metadatos, persistencia, biblioteca y playback, el cliente web, tests y documentación. La modularidad es **parcialmente consistente**: dominio, contratos y aplicación no dependen de TMDB, FFmpeg ni SQLite; el host actúa como composition root para la mayoría de módulos. Hay tres dependencias concretas que contradicen la sustitución de implementaciones prevista, además de una frontera operativa de seguridad pendiente.

## Logros presentes en el árbol

- Kernel/runtime de módulos y prueba de selección; dominio multimedia separado de infraestructura.
- Descubrimiento local MP4/MKV/AVI, análisis ffprobe, catálogo SQLite durable, migraciones y rehidratación tras reinicio.
- Direct Play MP4/H.264/AAC con HTTP Range y cliente web de biblioteca/detalle/reproductor.
- Proveedor TMDB detrás de contratos normalizados, selección/confirmación/refresh, enriquecimiento automático conservador y metadatos efectivos.
- Metadatos enriquecidos, ajustes, locks, overrides manuales y editor web; perfiles de persona, caché y filmografía local en desarrollo.
- Conversión experimental de AVI/MP4 incompatibles a MP4 con FFmpeg y caché local. Esto **no** completa Transcoding 0.7.

## Hallazgos

| Prioridad | Evidencia | Consecuencia y siguiente paso |
| --- | --- | --- |
| Alta | `infrastructure/ffprobe/Hispania.Library.FFprobe.csproj` referencia `infrastructure/library`; `FfprobeMediaProbe` usa `LocalLibraryRootConfiguration` y `RootPathResolver`. | Un analizador no puede sustituir filesystem sin arrastrar su implementación. Introducir resolución de fuente por contrato y conservar validación de rutas (P1-028). |
| Alta | `ICompatibleMediaTranscoder` y `FfmpegCompatibleMediaTranscoder` están en `apps/server`; `DirectPlayEndpoints` decide el fallback y solicita el servicio opcionalmente. | El host contiene proceso, caché y parte de la política de reproducción; otro transcoder exige editar el servidor. Separar contrato, aplicación y adaptador (P1-027) mediante decisión arquitectónica explícita. |
| Media | `infrastructure/persistence/Hispania.Library.Persistence.EFCore` referencia `Microsoft.Data.Sqlite.Core`, captura `SqliteException` y usa conversiones `SqliteScalarConversions`. | La biblioteca EFCore aparentemente común no sirve aún como base independiente para PostgreSQL. Mover especificidad al adaptador SQLite (P1-029). |
| Alta antes de acceso remoto | Rutas mutables `/api/v1/admin/metadata/*` y `/api/v1/media/*/metadata/*` no exigen autorización en `apps/server`. | Restringir despliegue a una red de confianza hasta incorporar autenticación/autorización y pruebas de denegación (P0-011). |

Las rutas HTTP de TMDB nombran al proveedor de forma explícita. Son una superficie específica del adaptador y no reemplazan el contrato normalizado de aplicación; deberá definirse una API agnóstica si se quiere cambiar proveedor sin cambiar cliente.

## Verificación

`dotnet test Hispania.sln --no-restore --verbosity quiet`: 412 pruebas correctas en 10 proyectos, ninguna omitida. `npm test`: 29 pruebas correctas tras actualizar una expectativa obsoleta del mensaje de error de reproducción. `npm run build`: correcto. Estas pruebas no sustituyen la validación en navegador con TMDB real, FFmpeg instalado ni pruebas de despliegue remoto.

## En curso y pendiente

El hito Movie Metadata Model 0.2 + Metadata Editor 0.1 continúa abierto por validación manual con TMDB real. People & Credits tiene perfil y filmografía local pero necesita completar su alcance y verificación. Permanecen pendientes progreso/reanudación, negociación de reproducción, transcoding modular, usuarios/autorización, series y música. El detalle priorizado está en `TODO.md`; el roadmap distingue las fases de producto.
