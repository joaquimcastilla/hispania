# Preservation and Global Media Knowledge Network

## Purpose

Diseñar una futura capa separada del servidor multimedia local capaz de registrar:

- qué obras existen;
- qué versiones existen;
- qué materiales están preservados;
- qué materiales están perdidos;
- qué assets son verificables;
- qué distribución está permitida.

El objetivo arquitectónico es preservación y conocimiento estructurado.

## Critical separation

No mezclar:

```text
KNOWN
PRESERVED
STORED
DISTRIBUTABLE
```

Una obra puede estar preservada pero no distribuible.

## Four Layers

### 1. Knowledge

Qué obras o assets sabemos que existen o existieron.

### 2. Preservation

Qué materiales han sobrevivido.

### 3. Storage

Dónde existen copias verificadas.

### 4. Distribution

Quién puede obtenerlas y bajo qué condiciones.

## Preservation States

- AVAILABLE
- PARTIALLY_PRESERVED
- MISSING
- LOST
- AT_RISK
- PRIVATE_ARCHIVE
- RIGHTS_RESTRICTED
- UNKNOWN

## Lost Media

La base de conocimiento debe permitir registrar obras sin archivo.

Evidence may include:

- broadcast schedules;
- magazines;
- catalogues;
- photographs;
- production records;
- archive references;
- newspapers;
- trusted testimony.

Possible lifecycle:

```text
KNOWN
 -> MISSING
 -> FOUND
 -> PRESERVED
```

## Unknown Works

No todo contenido relevante existe en TMDB, MusicBrainz u otras bases.

Debe existir una ruta de submission para:

- regional television;
- local broadcasts;
- radio;
- obscure documentaries;
- historical recordings;
- short films;
- pilots;
- advertising;
- alternative dubs;
- local productions;
- journalistic material.

## Global catalogue ingestion

Los nodos normales no pueden publicar directamente al catálogo global.

Suggested flow:

```text
Submission
  |
Quarantine
  |
Automated Validation
  |
Review
  |
Signed Manifest
  |
Approved Catalogue
```

Only authorized accounts may propose new global assets.

## Local isolation

El contenido local de un usuario:

- no se publica automáticamente;
- no se registra como distribuible automáticamente;
- no entra en la red sólo por existir en su servidor.

## Manifest

Conceptually:

```text
WorkID
EditionID
TimelineID
AssetID
MediaType
Language
Codec
Duration
Fingerprint
ContentHash
Provenance
RightsStatus
ModeratorSignatures
```

## Distributed content model

El servidor local no debe saber si la fuente es P2P.

Use generic contracts:

- `IContentResolver`
- `IContentProvider`
- `IContentPublisher`
- `IContentTransport`

Potential implementations:

- LocalResolver
- HttpResolver
- LanResolver
- S3Resolver
- DistributedResolver

## Moderation and safety

A global catalogue must not accept arbitrary blobs.

Need:

- allowlisted submitters or authorized roles;
- quarantined ingest;
- automated validation;
- human review;
- cryptographically signed manifests;
- revocation lists;
- provenance;
- audit trail.

## Coverage metrics

The knowledge graph can calculate preservation coverage.

Examples:

```text
known works
preserved works
fully preserved works
works with lossless audio
works with original subtitles
works with translations
works with verified timelines
```

For series:

```text
Series X

S01E01 preserved
S01E02 preserved
S01E03 partial
S01E04 missing
```

## Legal architecture principle

The system must model rights and distribution state separately from preservation state.

Do not encode the assumption:

"preserved" == "publicly downloadable".
