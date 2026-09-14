# Metadata

## Lectura obligatoria

- `CODEX.md`, `PROJECT_CONTEXT.md`, `docs/ARCHITECTURE.md` y `docs/ROADMAP.md`.
- `docs/MEDIA_MODEL.md` y las secciones de metadata de `docs/MEDIA_DOMAIN_DESIGN.md`.
- ADR-0003, ADR-0007, ADR-0011 y ADR-0017.

## Estado y ámbito

Metadata Providers 0.1 — Stage 1 implementa la frontera durable `IMetadataEnrichmentStore` y el roundtrip SQLite de Media 2E necesario para películas, incluido estado operativo de refresh. No implementa providers, búsqueda, matching, metadata efectiva, API/UI ni bytes de artwork. Phase 2 fija que ExternalIdentifier, ExternalIdentifierAssociation, MetadataAssertion, provenance, effective-value policy y ArtworkReference son extensiones ortogonales y opcionales de Media.

Stage 3 añade el adapter de películas TMDB (`Hispania.Metadata.Tmdb`). Se activa sólo con `Metadata:Tmdb:Enabled=true`; requiere `Metadata:Tmdb:ReadAccessToken`, permite `Language` (por defecto `es-ES`) y `Timeout` (positivo, máximo dos minutos). Las variables equivalentes usan `Metadata__Tmdb__...`. El token es un secreto de configuración y no se registra ni se serializa.

El adapter usa Bearer authentication y los endpoints `search/movie` y `movie/{id}`. Sus identificadores son `tmdb` / `movie`, con mapping version `tmdb-movie-v1`. Normaliza exclusivamente title, original-title, description y release-date; campos vacíos se omiten y una fecha presente inválida es una respuesta inválida. Poster y backdrop se conservan como locators opacos `tmdb-image` (sin URL final, descarga ni image configuration). TMDB popularity puede ser una señal de proveedor durante búsqueda, nunca confidence/provenance.

Las respuestas normalizadas llevan `ObservedAt` del procesamiento local y un fingerprint SHA-256 hexadecimal sobre una representación canónica del documento, no sobre JSON TMDB. 401/403/404 se devuelven como unsupported, 429/5xx/red como unavailable, timeout como timeout y JSON inválido como invalid response; la cancelación solicitada por el llamador se propaga. No hay retries.

Stage 4 valida el vertical completo con HTTP falso y SQLite real: búsqueda efímera, selección y confirmación explícitas, fetch/apply, refresh y rehidratación tras restart. También cubre idempotencia, supersession ante payload cambiado, delta acumulativo de artwork y ausencia de escrituras parciales ante error o cancelación.

Quedan diferidos auto-match/confirm, API/UI, artwork download/proxy, image configuration, TTL/retries, IMDb, series/personas/releases y metadata efectiva. Metadata Providers 0.1 no se considera completo por este stage.

Los proveedores externos no son identidad canónica; una entidad puede no tener identificadores externos. Identification, metadata y artwork son capacidades independientes y sustituibles.

## Límites

El ámbito futuro comprende contratos y módulos de metadata/identificación, cuando el roadmap y una tarea los autoricen. No se crean proveedores, dependencias externas, reglas de merge ni campos específicos de un proveedor antes de ese momento. No modifica el modelo Media ni otros ámbitos sin coordinación. Preservation y P2P no forman parte de este contexto.
