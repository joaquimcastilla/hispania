# Web UI

## Lectura obligatoria

- `CODEX.md`, `PROJECT_CONTEXT.md`, `docs/ARCHITECTURE.md` y `docs/ROADMAP.md`.
- La documentación de la API pública cuando exista.

## Estado y ámbito

Fase de roadmap: Phase 5 — First Web Client 0.5. **Web UI 0.1 — First Playable Movie está completado y verificado manualmente en navegador real** para el vertical slice MP4/H.264/AAC, Direct Play y HTTP Range. Implementa un único flujo: listar el catálogo, seleccionar una película y reproducirla mediante el reproductor nativo del navegador. Play, pause, seek, volume, mute y fullscreen fueron validados con controles nativos.

La API pública disponible para este flujo es:

- `GET /api/v1/media`, que devuelve elementos con `id` y `displayName`;
- `GET /api/v1/media/{id}/stream`, que sirve Direct Play MP4 y HTTP Range.

Movie Detail / Library UX 0.1 — COMPLETE: la biblioteca local real fue escaneada, rehidratada desde SQLite tras restart con el mismo `MediaEntityId`, mostrada y reproducida mediante Direct Play en navegador. En esa verificación no había enrichment/artwork persistido para el Work local, por lo que se verificaron los fallbacks de poster/backdrop; el delivery de artwork provider está cubierto por E2E determinista. El diseño actual valida estructura y funcionalidad, no el diseño visual final de HISPANIA.

Artwork Delivery 0.1 — Stage 1 añade `effective.posterUrl` en la lista y `artwork.poster.deliveryUrl` / `artwork.backdrop.deliveryUrl` en metadata detail. Son rutas relativas HISPANIA hacia `GET /api/v1/artwork/{id}` y la UI las consume directamente mediante `<img loading="lazy">`; no conoce `tmdb-image`, locators ni hosts TMDB. Cada tarjeta y el detalle mantienen su propio placeholder ante URL ausente o error de imagen. La vista Movie Detail / Library UX 0.1 muestra backdrop, poster, título, título original opcional, fecha, descripción y un Play explícito que revela el reproductor asociado. El player y Direct Play no participan en la entrega de artwork. Sólo se aplica cache de navegador desde el endpoint; no existe cache durable ni fetch/Blob/base64 en JavaScript. El footer muestra la atribución mínima de TMDB sin afirmar endorsement o certification. Artwork Delivery 0.1 Stage 2 no está iniciado.

El cliente usa rutas relativas para compartir origen con el host ASP.NET en producción. `dotnet run` sirve el build existente de `apps/web/dist` para la prueba local; la publicación conserva el `wwwroot` publicado. El proxy de Vite sólo existe para desarrollo y reenvía `/api` al host local; no se abre CORS global.

Los clientes sólo interactúan con la API pública y no contienen lógica de negocio crítica.

## Límites

No crea endpoints ni contratos de API sin una tarea autorizada. No accede a módulos, base de datos o dominio saltando la API. React sólo conoce los DTO públicos `id` y `displayName` y no contiene lógica de negocio.
