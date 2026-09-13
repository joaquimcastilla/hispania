# Media Domain

## Lectura obligatoria

- `CODEX.md`, `PROJECT_CONTEXT.md`, `docs/ARCHITECTURE.md` y `docs/ROADMAP.md`.
- `docs/MEDIA_MODEL.md` y `docs/MEDIA_DOMAIN_DESIGN.md`.
- ADR-0003; ADR-0005 como antecedente histórico superseded; ADR-0006 como frontera con sistemas futuros.
- ADR-0007, ADR-0008, ADR-0009, ADR-0010 y ADR-0011.
- ADR-0012, ADR-0013 y ADR-0014.

## Estado y ámbito

`Media 0.2 conceptual design = frozen`.

`Phase 2 implementation = complete`.

Media 2A — Identity + Core Media; Media 2B — Timelines + Asset Coverage; Media 2C — Technical Representation + Media Source; Media 2D — Publication + Creator; and Media 2E — Metadata / Identification are complete. This closure does not complete later phases or introduce provider integrations, metadata resolution, persistence, authentication, transcoding, distributed media, or Preservation.

El ámbito es el dominio Media y sus pruebas/documentación directamente relacionadas. El modelo aceptado es un grafo ortogonal de Work, MediaEntity, Timeline, Asset, AssetCoverage, TechnicalRepresentation, MediaSource, Release, ReleaseGroup, Collection, Creator y relaciones asociadas. Metadata e identificación son extensiones opcionales y modulares.

Las asociaciones de identificadores externos conservan un lifecycle local auditable con evidencia, invalidación y relaciones de migración, sucesión y alias. La unicidad de asociaciones `Confirmed` es una invariante de conjunto para una futura capa Application/Persistence: Media Domain no usa estado global estático, repositorios ni certificación global. `ContributionRole`, `MetadataField` y `ArtworkRole` validan sintaxis y normalización en el dominio; los mappings y módulos HISPANIA gobiernan el vocabulario semántico y traducen los valores de providers. `ArtworkLocator.Scheme` sigue la misma gramática declarativa de `SourceScheme` sin acoplar ambos tipos.

## Límites

Puede implementar únicamente el incremento de Phase 2 que una tarea autorice, con sus tests y documentación inmediata. No sustituye las decisiones aún abiertas documentadas en `MEDIA_DOMAIN_DESIGN.md`, ni modifica Kernel, runtime, contratos de otros subsistemas o infraestructura sin declararlo y obtener autorización.

Están fuera de Media: Preservation, Distributed Media, P2P, Global Catalog, Global Certification, Global Moderation, Global Asset Registry, Network Normalization, Peer Discovery y Distributed Manifests. Media funciona localmente y nunca depende de esos futuros sistemas.
