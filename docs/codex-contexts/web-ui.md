# Web UI

## Inicio y carruseles — 2026-09-22

Inicio organiza filas independientes en este orden: `Mis contenidos`, `Seguir viendo` y `Reciente en Películas`. `ContentCarousel` admite `library`, `landscape` y `poster`: tarjetas 2:1 (180–250 px según breakpoint), 16:9 (250–330 px) y 2:3 (112–145 px), respectivamente. Series y música sólo se mostrarán como filas de recientes cuando exista catálogo real. Películas recientes usa entradas reales del catálogo y el click abre Movie Detail; sin poster se muestra el placeholder local HISPANIA. El catálogo no entrega fecha de incorporación ni progreso de reproducción: el orden reciente conserva el orden del API y `Seguir viendo` muestra un vacío discreto hasta que exista progreso persistido.

Cada fila controla su propio viewport, usa scroll-snap y desplazamiento horizontal nativo para touch/trackpad, flechas por ~85% del ancho visible y teclado con flechas. Los controles sólo aparecen con overflow, se desactivan en los límites y se respeta `prefers-reduced-motion`. Las tarjetas de bibliotecas tienen degradados locales y fondos de color HISPANIA, botones de superficie completa y estado claro “Próximamente” para bibliotecas aún no implementadas. CSS limita el ancho de página, conserva proporciones en móvil y no usa cuadrículas en Inicio.

## Global navigation update — 2026-09-22

The HISPANIA shell now has one reusable accessible drawer: menu button, brand, real media sections, explicit administration placeholders, active state, Escape and outside-click close, keyboard focus outlines, scrollable drawer and mobile sizing. Home/Favorites/Search/Settings are not presented as functional routes unless implemented. Movie Detail still opens the per-movie Metadata Editor and the editor retains its return path.

## Stage E verification — 2026-09-22

Stage E Web Metadata Editor is complete by automated verification (20/20 frontend tests). The real route `/media/{mediaId}/metadata/edit` supports direct entry, Movie Detail entry and return; the router hook ordering is stable. Loading, visible focus, accessible labels, responsive layout and loading/error/success states are covered by the implementation. Live browser navigation remains pending.

## Lectura obligatoria

- `CODEX.md`, `PROJECT_CONTEXT.md`, `docs/ARCHITECTURE.md` y `docs/ROADMAP.md`.
- La documentación de la API pública cuando exista.

## Estado y ámbito

Fase de roadmap: Phase 5 — First Web Client 0.5. **Web UI 0.1 — First Playable Movie está completado y verificado manualmente en navegador real** para el vertical slice MP4/H.264/AAC, Direct Play y HTTP Range. Implementa un único flujo: listar el catálogo, seleccionar una película y reproducirla mediante el reproductor nativo del navegador. Play, pause, seek, volume, mute y fullscreen fueron validados con controles nativos.

La API pública disponible para este flujo es:

- `GET /api/v1/media`, que devuelve elementos con `id` y `displayName`;
- `GET /api/v1/media/{id}/stream`, que sirve Direct Play MP4 y HTTP Range.

Movie Detail / Library UX 0.1 — COMPLETE: la biblioteca local real fue escaneada, rehidratada desde SQLite tras restart con el mismo `MediaEntityId`, mostrada y reproducida mediante Direct Play en navegador. En esa verificación no había enrichment/artwork persistido para el Work local, por lo que se verificaron los fallbacks de poster/backdrop; el delivery de artwork provider está cubierto por E2E determinista.

Web Library Visual Shell 0.2: el cliente presenta una navegación persistente con Películas, Series, Colecciones y Música. Películas consume el catálogo real, metadata efectiva, las URLs HISPANIA de poster/backdrop y Direct Play. Series, Colecciones y Música son estados visuales honestos de “Aún no disponible”: no inventan catálogo ni crean endpoints. El detalle de película compone un hero con backdrop, poster, campos efectivos presentes y Play, sin conocer locators ni hosts TMDB. La UI aplica una estética oscura propia inspirada en patrones de streaming, todavía provisional, con tarjetas responsivas y estados de carga/error/vacío accesibles; React mantiene solamente estado de presentación y llamadas a la API pública existente. La validación manual completa de Películas con API activa, catálogo real y artwork delivery sigue pendiente.

TMDB Movies 0.2 mantiene la identificación manual para corregir o cambiar asociaciones, pero la metadata normal no depende de abrir el detalle: el startup workflow puede auto-identificar y enriquecer antes de que la UI liste la película. La UI sigue consumiendo sólo Effective Metadata y delivery URLs HISPANIA; no recibe tokens, DTOs TMDB, locators ni URLs CDN. Ambiguous/no-match conserva `displayName` y el botón de identificación manual. La implementación automática está pendiente de verificación live y restart con SQLite durable.

Artwork Delivery 0.1 — Stage 1 añade `effective.posterUrl` en la lista y `artwork.poster.deliveryUrl` / `artwork.backdrop.deliveryUrl` en metadata detail. Son rutas relativas HISPANIA hacia `GET /api/v1/artwork/{id}` y la UI las consume directamente mediante `<img loading="lazy">`; no conoce `tmdb-image`, locators ni hosts TMDB. Cada tarjeta y el detalle mantienen su propio placeholder ante URL ausente o error de imagen. La vista Movie Detail / Library UX 0.1 muestra backdrop, poster, título, título original opcional, fecha, descripción y un Play explícito que revela el reproductor asociado. El player y Direct Play no participan en la entrega de artwork. Sólo se aplica cache de navegador desde el endpoint; no existe cache durable ni fetch/Blob/base64 en JavaScript. El footer muestra la atribución mínima de TMDB sin afirmar endorsement o certification. Artwork Delivery 0.1 Stage 2 no está iniciado.

El cliente usa rutas relativas para compartir origen con el host ASP.NET en producción. `dotnet run` sirve el build existente de `apps/web/dist` para la prueba local; la publicación conserva el `wwwroot` publicado. El proxy de Vite sólo existe para desarrollo y reenvía `/api` al host local; no se abre CORS global.

Los clientes sólo interactúan con la API pública y no contienen lógica de negocio crítica.

## Límites

No crea endpoints ni contratos de API sin una tarea autorizada. No accede a módulos, base de datos o dominio saltando la API. React sólo conoce los DTO públicos `id` y `displayName` y no contiene lógica de negocio.
La separación prevista para Movie Metadata Model 0.2 + Metadata Editor 0.1 es explícita: Movie Detail es consumo; Metadata Editor es administración. El editor no será fuente directa del detalle: los cambios manuales se conservarán como assertions con provenance y Effective Metadata resolverá el valor visible. La pantalla de edición, settings/locks y su API aún no están implementados.
La persistencia rica provider-neutral de Stage B ya está implementada y testeada; el Web Metadata Editor sigue siendo el siguiente Stage y no forma parte de Movie Detail.
## Metadata milestone status

Stage B — Rich Metadata Persistence: COMPLETE. Stage C — Metadata Settings, Locks & Manual Overrides: COMPLETE. Stage D — Metadata Editor API: COMPLETE. Stage E — Web Metadata Editor: PENDING. People & Credits 0.1: FUTURE. Live TMDB verification: PENDING. The global `Movie Metadata Model 0.2 + Metadata Editor 0.1` milestone remains ACTIVE and is not marked COMPLETE.
