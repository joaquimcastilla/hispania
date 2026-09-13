# Media Model

## Core idea

Una obra no es un archivo.

Un archivo es una posible representación física de una parte de una obra o edición.

## Main Entities

### Work

Identidad conceptual.

Ejemplos:

- una película;
- una serie;
- una canción;
- un documental;
- una emisión;
- una obra audiovisual.

### Edition

Versión concreta de una obra.

Ejemplos:

- theatrical cut;
- director's cut;
- censored TV cut;
- remaster;
- regional version.

### Timeline

Referencia temporal compatible entre assets.

Dos assets sólo deben considerarse intercambiables si son compatibles con la misma timeline o existe una transformación explícita entre timelines.

### MediaEntity

Entidad reproducible o navegable.

Tipos previstos:

- Movie
- Series
- Season
- Episode
- Track
- Album
- Artist
- MusicVideo
- Photo
- Playlist
- Collection

### MediaSource / Asset

Representación concreta.

Ejemplos:

- video 2160p;
- video 1080p;
- audio español 5.1;
- audio inglés Atmos;
- subtitle es-ES;
- lyrics;
- artwork.

## Music example

```text
Track
Queen - Bohemian Rhapsody
|
+-- audio FLAC
+-- audio AAC
+-- music video 1080p
+-- music video 4K
+-- lyrics original
+-- timed lyrics
+-- translation ES
+-- translation CA
+-- video subtitles
```

Audio y videoclip pertenecen a la misma entidad semántica cuando corresponda.

## Movie example

```text
Work: Movie
|
+-- Edition: Theatrical
    |
    +-- Timeline A
        |
        +-- Video 2160p
        +-- Video 1080p
        +-- Audio es-ES 5.1
        +-- Audio en-US 5.1
        +-- Subtitles es-ES
        +-- Subtitles ca-ES
```

## Hashes

Assets inmutables pueden identificarse por contenido.

Candidato recomendado:

- BLAKE3 para hashing rápido;
- SHA-256 cuando interoperabilidad lo requiera.

No decidir definitivamente sin ADR.

## Rules

1. Work != File.
2. Edition != Work.
3. Timeline compatibility must be explicit.
4. Audio tracks are first-class assets.
5. Subtitle tracks are first-class assets.
6. Lyrics are not identical to video subtitles.
7. Metadata providers do not own canonical identity.
8. Local file path is an implementation detail.
9. Remote assets must fit the same model.
10. A media entity may exist without any available playable asset.
