# Metadata

## Lectura obligatoria

- `CODEX.md`, `PROJECT_CONTEXT.md`, `docs/ARCHITECTURE.md` y `docs/ROADMAP.md`.
- `docs/MEDIA_MODEL.md` y las secciones de metadata de `docs/MEDIA_DOMAIN_DESIGN.md`.
- ADR-0003, ADR-0007, ADR-0011 y ADR-0017.

## Estado y ámbito

Metadata Providers 0.1 — Stage 1 implementa la frontera durable `IMetadataEnrichmentStore` y el roundtrip SQLite de Media 2E necesario para películas, incluido estado operativo de refresh. No implementa providers, búsqueda, matching, metadata efectiva, API/UI ni bytes de artwork. Phase 2 fija que ExternalIdentifier, ExternalIdentifierAssociation, MetadataAssertion, provenance, effective-value policy y ArtworkReference son extensiones ortogonales y opcionales de Media.

Los proveedores externos no son identidad canónica; una entidad puede no tener identificadores externos. Identification, metadata y artwork son capacidades independientes y sustituibles.

## Límites

El ámbito futuro comprende contratos y módulos de metadata/identificación, cuando el roadmap y una tarea los autoricen. No se crean proveedores, dependencias externas, reglas de merge ni campos específicos de un proveedor antes de ese momento. No modifica el modelo Media ni otros ámbitos sin coordinación. Preservation y P2P no forman parte de este contexto.
