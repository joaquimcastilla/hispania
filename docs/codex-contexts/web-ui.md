# Web UI

## Lectura obligatoria

- `CODEX.md`, `PROJECT_CONTEXT.md`, `docs/ARCHITECTURE.md` y `docs/ROADMAP.md`.
- La documentación de la API pública cuando exista.

## Estado y ámbito

Fase de roadmap: Phase 5 — First Web Client 0.5. Web UI 0.1 implementa un único flujo: listar el catálogo, seleccionar una película y reproducirla mediante el reproductor nativo del navegador. No incluye navegación, metadatos enriquecidos, usuarios ni controles de reproducción propios.

La API pública disponible para este flujo es:

- `GET /api/v1/media`, que devuelve elementos con `id` y `displayName`;
- `GET /api/v1/media/{id}/stream`, que sirve Direct Play MP4 y HTTP Range.

El cliente usa rutas relativas para compartir origen con el host ASP.NET en producción. El proxy de Vite sólo existe para desarrollo y reenvía `/api` al host local; no se abre CORS global.

Los clientes sólo interactúan con la API pública y no contienen lógica de negocio crítica.

## Límites

No crea endpoints ni contratos de API sin una tarea autorizada. No accede a módulos, base de datos o dominio saltando la API. React sólo conoce los DTO públicos `id` y `displayName` y no contiene lógica de negocio.
