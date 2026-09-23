# Metadata

## Manual library metadata scans

The Dashboard exposes a full refresh (`POST /api/v1/admin/metadata/scan`) and an incremental metadata lookup (`POST /api/v1/admin/metadata/scan-new`). Both first rescan the configured library so newly added video files are catalogued. Full refresh updates metadata for every movie with a confirmed provider identifier and searches movies without one; incremental lookup searches only movies lacking a confirmed identifier for the currently configured provider. SQLite remains the authoritative index. The Dev Launcher exports a readable `.hispania-metadata-index.json` in the Movies root containing active paths, Work IDs and all confirmed provider/namespace/value identifiers; it is regenerated at startup and after enrichment runs.

The Dev Launcher reads the TMDB read-access token from `tools/dev-launcher/tmdb-token.txt` under the selected repository. The file is local-only and ignored by Git. The Launcher UI displays its path and can open it for editing, but never displays the token itself.

## Localized matching and artwork diagnosis — 2026-09-22

The library convention `Título (AAAA).ext` is parsed into independent local search signals; the extension remains a playback/catalog capability and is never used as identity. Automatic matching remains conservative: normalized exact title or provider-supplied original title, exact year and exactly one candidate. `The Weather Man (2005).avi`, `Seduction and Snacks (2021).mp4` and `Un Ratoncito Duro De Roer (1997).mkv` are covered by parser tests; `Mouse Hunt (1997)` is covered through the provider original-title path. No local live record proves why the two reported movies lack artwork; live TMDB credentials, candidate/confirmation/refresh state and artwork delivery must be inspected before claiming a fix.

## Stage E verification — 2026-09-22

Stage E Web Metadata Editor is complete by automated verification. PUT/DELETE endpoints return the updated provider-neutral read model; `PUT []` is an empty manual override while DELETE removes the override. Failed saves preserve drafts and do not show success. Automated coverage includes locks, multi-provider sources, people/credits and the editor route. Live TMDB verification remains pending.

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

TMDB Movies 0.1 — Block A es la base live del provider: mantiene el adapter aislado, no decide matching y no persiste por sí mismo.

TMDB Movies 0.2 — Automatic Enrichment añade un workflow Application separado del scanner: parsea `displayName` como señal local, ejecuta búsqueda TMDB y auto-confirma sólo un candidato con título normalizado exactamente equivalente y año exacto. La normalización es trim, case-insensitive, determinista Unicode y equivalencia trivial `&`/`and`; no usa fuzzy, popularidad ni primera posición. Reutiliza select/confirm/fetch existentes para persistir assertions, artwork y refresh state. Confirmed + refresh succeeded evita búsquedas/fetch repetidos en startup. Ambiguous, no-match y provider failure conservan la película catalogada y dejan disponible el flujo manual. Implementación pendiente de verificación live con token.

El flujo público completo es `MediaEntity -> Work -> TMDB search -> Candidate -> Confirmed -> refresh -> assertions/artwork -> Effective Metadata -> Artwork Delivery -> Web UI`. Search no escribe en SQLite; select escribe sólo Candidate; confirm es siempre explícito; refresh es siempre explícito. El mismo payload no añade duplicados y un payload cambiado conserva historia mediante assertions superseded y artwork acumulativo. Quedan diferidos auto-match, fuzzy scoring, bulk enrichment, background refresh, TV shows, collections TMDB, cast, genres, trailers, ratings, settings UI de token y durable artwork cache. Identification & Enrichment no se marcará COMPLETE hasta la comprobación live y restart con SQLite durable.

Stage 3 añade el adapter de películas TMDB (`Hispania.Metadata.Tmdb`). Se activa sólo con `Metadata:Tmdb:Enabled=true`; requiere `Metadata:Tmdb:ReadAccessToken`, permite `Language` (por defecto `es-ES`), `Region` (por defecto `ES`) y `TimeoutSeconds` (15 por defecto; máximo 120). Las variables equivalentes son `Metadata__Tmdb__Enabled`, `Metadata__Tmdb__ReadAccessToken`, `Metadata__Tmdb__Language`, `Metadata__Tmdb__Region` y `Metadata__Tmdb__TimeoutSeconds`. El token es un secreto de configuración y no se registra ni se serializa.

El adapter usa la API v3 (`https://api.themoviedb.org/3/`) y Bearer Read Access Token con los endpoints `search/movie` y `movie/{id}`. Search envía `query`, `language`, `region`, `include_adult=false`, `page=1` y `primary_release_year` si existe pista de año. Sus identificadores son `tmdb` / `movie`, con mapping version `tmdb-movie-v1`. Normaliza exclusivamente title, original-title, description y release-date; campos vacíos se omiten y una fecha presente inválida es una respuesta inválida. Poster y backdrop se conservan como locators opacos `tmdb-image` (sin URL final, descarga ni image configuration). TMDB popularity puede ser una señal de proveedor durante búsqueda, nunca confidence/provenance.

Las respuestas normalizadas llevan `ObservedAt` del procesamiento local y un fingerprint SHA-256 hexadecimal sobre una representación canónica del documento, no sobre JSON TMDB. 401/403/404 se devuelven como unsupported, 429/5xx/red como unavailable, timeout como timeout y JSON inválido como invalid response; la cancelación solicitada por el llamador se propaga. No hay retries.

Stage 4 valida el vertical completo con HTTP falso y SQLite real: búsqueda efímera, selección y confirmación explícitas, fetch/apply, refresh y rehidratación tras restart. También cubre idempotencia, supersession ante payload cambiado, delta acumulativo de artwork y ausencia de escrituras parciales ante error o cancelación.

Effective Metadata 0.1 — COMPLETE: `effective-metadata-v0.1` resuelve en memoria y bajo demanda assertions y artwork references persistidos mediante lectura dirigida, sin filename, I/O de provider, proyección persistida ni cache. La política es determinista: `Manual > LocalProcess > ExternalProvider`, después `ObservedAt`, código de provider, identificador relacionado e ID estable; confidence no participa. SQLite restart, metadata detail, summary de media list, URLs HISPANIA de artwork y consumo Web UI están cubiertos. API y UI consumen la proyección, no assertions raw. Quedan diferidos provider ranking configurable, locale/region, preferencias de usuario, API raw/admin, edición manual, proyección materializada/cache, confidence ranking e influencia del lifecycle de identifiers.

Artwork Delivery 0.1 — Stage 1 COMPLETE: `IMetadataEnrichmentStore` permite lookup directo de `ArtworkReference` por su ID, sin cargar enrichment completo. `IArtworkContentResolver` es un boundary provider-neutral; el adapter TMDB resuelve únicamente locators `tmdb-image`, con `PosterCard → w500` y `BackdropDetail → w1280`. `GET /api/v1/artwork/{id}` recibe sólo el ID HISPANIA y una variant cerrada, entrega streaming validado de JPEG/PNG/WebP y expone cache de navegador `public, max-age=86400`; no hay cache durable. Las APIs de media proyectan URLs relativas HISPANIA desde el winner ID (`posterUrl` y `deliveryUrl`), nunca desde locator o URL remota. Artwork Delivery 0.1 permanece abierto: Stage 2 no está iniciado. La atribución TMDB requerida queda pendiente de una futura UI About/Credits.

Quedan explícitamente diferidos: stages posteriores de effective metadata resolution; provider ranking; auto-match y auto-confirm; metadata HTTP API; presentación de metadata en Web UI; download/cache/proxy de artwork e image configuration; retry/backoff; scheduler; freshness TTL; IMDb; providers de series, personas y música; y API/autorización para confirmación manual. La comprobación live es manual: `dotnet run --project apps/cli -- tmdb-smoke` busca `Seduction and Snacks` (2021) con `es-ES`/`ES`; tras revisar un candidato, `dotnet run --project apps/cli -- tmdb-smoke --id <tmdb-id>` muestra sólo su documento normalizado. Requiere `Metadata__Tmdb__ReadAccessToken` en el entorno, nunca imprime el secreto, no persiste nada y no bloquea CI, cuyos tests usan HTTP falso determinista.

La verificación live de Block A se completó con un token local de runtime no versionado. Para `Seduction and Snacks` (2021), la selección manual fue TMDB ID `889824`: título y título original `Seduction & Snacks`, fecha `2021-10-28`, poster `tmdb-image:/pr4cedrtyz9udITWiqFqcjm6dS3.jpg`, backdrop `tmdb-image:/6anyOQ23avWVIkglXaEfyaridjF.jpg`, mapping `tmdb-movie-v1` y fingerprint `ea484f0e1816599d3afdc9496fb872bac5616195d6a49f7a1b8ca81a0321dd3c`. El documento normalizado no contenía description; se conserva como dato ausente válido del provider y locale actuales, sin fallback ni error. TMDB Movies 0.1 — Block A: COMPLETE.

Los proveedores externos no son identidad canónica; una entidad puede no tener identificadores externos. Identification, metadata y artwork son capacidades independientes y sustituibles.

El Panel de control permite volver a ejecutar manualmente el enrichment sobre el catálogo de películas mediante `POST /api/v1/admin/metadata/scan`. Reutiliza el proveedor configurado (TMDB en esta configuración), el matcher y las reglas de conflicto actuales; no vuelve a escanear archivos, no confirma conflictos históricos y devuelve conflicto si ya hay un enrichment ejecutándose. Requiere TMDB habilitado. Las demás secciones del panel son sólo navegación visual por ahora.

## Límites

El ámbito futuro comprende contratos y módulos de metadata/identificación, cuando el roadmap y una tarea los autoricen. No se crean proveedores, dependencias externas, reglas de merge ni campos específicos de un proveedor antes de ese momento. No modifica el modelo Media ni otros ámbitos sin coordinación. Preservation y P2P no forman parte de este contexto.
Movie Metadata Model 0.2 + Metadata Editor 0.1 se desarrolla por stages. Movie Detail es una proyección de consumo y el editor una superficie administrativa; Effective Metadata sigue siendo la autoridad. TMDB ya normaliza idioma original, tagline, community rating explícito, géneros, compañías, keywords, IMDb y colección con campos/identificadores provider-neutral. People/credits, settings/locks, API/UI del editor y live verification siguen pendientes.
Stage B — Rich Metadata Persistence: COMPLETE. Stage C — Metadata Settings, Locks & Manual Overrides: COMPLETE. Stage D — Metadata Editor API: COMPLETE. Stage E — Web Metadata Editor: PENDING. People & Credits 0.1: FUTURE. Live TMDB verification: PENDING. SQLite persiste de forma normalizada géneros, tags, estudios, personas y créditos para `Work`, con ownership por provider, identidad de persona `(ProviderCode, ProviderId)`, provenance, idempotencia, restart y aislamiento multi-provider testeados. Migration IDs: `20260922000100_initial`, `20260922000200_metadata_enrichment`, `20260922000300_rich_metadata`, `20260922000400_metadata_settings`; mapping legacy→standard: `0001_initial`→`20260922000100_initial`, `0002_metadata_enrichment`→`20260922000200_metadata_enrichment`, `0003_rich_metadata`→`20260922000300_rich_metadata`, `0004_metadata_settings`→`20260922000400_metadata_settings`. Locked rich collections preserve only the projection owned by the provider being refreshed. Identical metadata fingerprint is not reapplied.
### Decisión arquitectónica: Person es una entidad de primer nivel

`Person` no es un detalle embebido de `Credit`: tiene un `PersonId` interno estable y puede reutilizarse entre múltiples `Work`. Las identidades externas se modelan por namespace `(provider, externalId)` para reconciliación, sin confundirlas con el identificador interno ni hacer fuzzy merging por nombre. `Credit` relaciona `WorkId` con `PersonId` y conserva cada rol, trabajo, personaje y ordinal, permitiendo varios créditos de la misma persona en un mismo `Work` y funcionando también para series, episodios y futuros tipos audiovisuales.

La lectura provider-neutral `IRichMetadataStore.GetCreditsForPersonAsync` deja preparada la filmografía local a partir de nuestros propios créditos, sin reconstruirla desde un proveedor externo. La metadata futura de persona (biografía, fechas, lugar, artwork e identificadores externos) deberá seguir el sistema de provenance existente (`Manual`, `LocalProcess`, `ExternalProvider`), sin campos específicos de TMDB. Este milestone no implementa endpoint, UI, búsqueda, filmografía externa ni recomendaciones.
