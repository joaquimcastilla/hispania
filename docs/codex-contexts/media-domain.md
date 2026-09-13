# Media Domain

## Lectura obligatoria

- `CODEX.md`, `PROJECT_CONTEXT.md`, `docs/ARCHITECTURE.md` y `docs/ROADMAP.md`.
- `docs/MEDIA_MODEL.md` y `docs/MEDIA_DOMAIN_DESIGN.md`.
- ADR-0003; ADR-0005 como antecedente histórico superseded; ADR-0006 como frontera con sistemas futuros.
- ADR-0007, ADR-0008, ADR-0009, ADR-0010 y ADR-0011.

## Estado y ámbito

`Media 0.2 conceptual design = frozen`.

`Phase 2 implementation = in progress`.

El ámbito es el dominio Media y sus pruebas/documentación directamente relacionadas. El modelo aceptado es un grafo ortogonal de Work, MediaEntity, Timeline, Asset, AssetCoverage, TechnicalRepresentation, MediaSource, Release, ReleaseGroup, Collection, Creator y relaciones asociadas. Metadata e identificación son extensiones opcionales y modulares.

## Límites

Puede implementar únicamente el incremento de Phase 2 que una tarea autorice, con sus tests y documentación inmediata. No sustituye las decisiones aún abiertas documentadas en `MEDIA_DOMAIN_DESIGN.md`, ni modifica Kernel, runtime, contratos de otros subsistemas o infraestructura sin declararlo y obtener autorización.

Están fuera de Media: Preservation, Distributed Media, P2P, Global Catalog, Global Certification, Global Moderation, Global Asset Registry, Network Normalization, Peer Discovery y Distributed Manifests. Media funciona localmente y nunca depende de esos futuros sistemas.
