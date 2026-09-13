# Metadata

## Lectura obligatoria

- `CODEX.md`, `PROJECT_CONTEXT.md`, `docs/ARCHITECTURE.md` y `docs/ROADMAP.md`.
- `docs/MEDIA_MODEL.md` y las secciones de metadata de `docs/MEDIA_DOMAIN_DESIGN.md`.
- ADR-0003, ADR-0007 y ADR-0011.

## Estado y ámbito

Phase 9 define los contratos y primeros proveedores. Phase 2 ya fija conceptualmente que ExternalIdentifier, ExternalIdentifierAssociation, MetadataAssertion, provenance, effective-value policy y ArtworkReference son extensiones ortogonales y opcionales de Media.

Los proveedores externos no son identidad canónica; una entidad puede no tener identificadores externos. Identification, metadata y artwork son capacidades independientes y sustituibles.

## Límites

El ámbito futuro comprende contratos y módulos de metadata/identificación, cuando el roadmap y una tarea los autoricen. No se crean proveedores, dependencias externas, reglas de merge ni campos específicos de un proveedor antes de ese momento. No modifica el modelo Media ni otros ámbitos sin coordinación. Preservation y P2P no forman parte de este contexto.
