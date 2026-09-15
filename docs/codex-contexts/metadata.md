# Metadata

## Lectura obligatoria

- `CODEX.md`, `PROJECT_CONTEXT.md`, `docs/ARCHITECTURE.md` y `docs/ROADMAP.md`.
- `docs/MEDIA_MODEL.md` y las secciones de metadata de `docs/MEDIA_DOMAIN_DESIGN.md`.
- ADR-0003, ADR-0007, ADR-0011, ADR-0017 y ADR-0018.

## Estado y ámbito

Metadata Providers 0.1 — COMPLETE. Su alcance cerrado es: arquitectura de provider de metadata de películas, workflow durable de enrichment y el primer adapter real de películas TMDB. No significa que todas las features de metadata ni la UI completa de metadata estén terminadas.

- Stage 1 — COMPLETE: `IMetadataEnrichmentStore`, roundtrip SQLite de Media 2E y estado operativo de refresh.
- Stage 2 — COMPLETE: contratos específicos de película, señales/candidates de búsqueda y workflow Application de selección y confirmación explícitas.
- Stage 3 — COMPLETE: adapter `Hispania.Metadata.Tmdb`, normalización y HTTP determinista.
- Stage 4 — COMPLETE: vertical adapter → Application → SQLite → restart validado con HTTP falso determinista.

Media 2E fija que ExternalIdentifier, ExternalIdentifierAssociation, MetadataAssertion, provenance, effective-value policy y ArtworkReference son extensiones ortogonales y opcionales de Media.

Stage 3 añade el adapter de películas TMDB (`Hispania.Metadata.Tmdb`). Se activa sólo con `Metadata:Tmdb:Enabled=true`; requiere `Metadata:Tmdb:ReadAccessToken`, permite `Language` (por defecto `es-ES`) y `Timeout` (positivo, máximo dos minutos). Las variables equivalentes usan `Metadata__Tmdb__...`. El token es un secreto de configuración y no se registra ni se serializa.

El adapter usa Bearer authentication y los endpoints `search/movie` y `movie/{id}`. Sus identificadores son `tmdb` / `movie`, con mapping version `tmdb-movie-v1`. Normaliza exclusivamente title, original-title, description y release-date; campos vacíos se omiten y una fecha presente inválida es una respuesta inválida. Poster y backdrop se conservan como locators opacos `tmdb-image` (sin URL final, descarga ni image configuration). TMDB popularity puede ser una señal de proveedor durante búsqueda, nunca confidence/provenance.

Las respuestas normalizadas llevan `ObservedAt` del procesamiento local y un fingerprint SHA-256 hexadecimal sobre una representación canónica del documento, no sobre JSON TMDB. 401/403/404 se devuelven como unsupported, 429/5xx/red como unavailable, timeout como timeout y JSON inválido como invalid response; la cancelación solicitada por el llamador se propaga. No hay retries.

Stage 4 valida el vertical completo con HTTP falso y SQLite real: búsqueda efímera, selección y confirmación explícitas, fetch/apply, refresh y rehidratación tras restart. También cubre idempotencia, supersession ante payload cambiado, delta acumulativo de artwork y ausencia de escrituras parciales ante error o cancelación.

Effective Metadata 0.1 — Stage 3 COMPLETE: `GET /api/v1/media` incorpora summary efectiva por lote (`title`, `releaseDate`, locator de poster) y Web UI consume esa proyección sin resolver metadata. La UI aplica sólo fallback de presentación `effective title → displayName`, carga el detalle desde `GET /api/v1/media/{id}/metadata` al seleccionar y conserva Direct Play. Los locators de artwork siguen siendo metadata opaca: se muestra placeholder, nunca URL directa, descarga o proxy. No incluye API raw, ranking, cache ni UI de edición.

Artwork Delivery 0.1 — Stage 1 COMPLETE: `IMetadataEnrichmentStore` permite lookup directo de `ArtworkReference` por su ID, sin cargar enrichment completo. `IArtworkContentResolver` es un boundary provider-neutral; el adapter TMDB resuelve únicamente locators `tmdb-image`, con `PosterCard → w500` y `BackdropDetail → w1280`. `GET /api/v1/artwork/{id}` recibe sólo el ID HISPANIA y una variant cerrada, entrega streaming validado de JPEG/PNG/WebP y expone cache de navegador `public, max-age=86400`; no hay cache durable. Las APIs de media proyectan URLs relativas HISPANIA desde el winner ID (`posterUrl` y `deliveryUrl`), nunca desde locator o URL remota. Artwork Delivery 0.1 permanece abierto: Stage 2 no está iniciado. La atribución TMDB requerida queda pendiente de una futura UI About/Credits.

Quedan explícitamente diferidos: stages posteriores de effective metadata resolution; provider ranking; auto-match y auto-confirm; metadata HTTP API; presentación de metadata en Web UI; download/cache/proxy de artwork e image configuration; retry/backoff; scheduler; freshness TTL; IMDb; providers de series, personas y música; y API/autorización para confirmación manual. Una búsqueda live de TMDB es una comprobación operativa opcional pendiente de secret local: no bloquea 0.1 porque adapter, E2E y CI son deterministas y no usan internet.

Los proveedores externos no son identidad canónica; una entidad puede no tener identificadores externos. Identification, metadata y artwork son capacidades independientes y sustituibles.

## Límites

El ámbito futuro comprende contratos y módulos de metadata/identificación, cuando el roadmap y una tarea los autoricen. No se crean proveedores, dependencias externas, reglas de merge ni campos específicos de un proveedor antes de ese momento. No modifica el modelo Media ni otros ámbitos sin coordinación. Preservation y P2P no forman parte de este contexto.
