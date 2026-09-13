# Module System

## Goal

Permitir que componentes relevantes puedan ser sustituidos sin modificar consumidores.

## Module Types

### Core Module

Parte esencial del sistema.

Debe ser mínimo.

### Native Module

Código de confianza ejecutado con acceso nativo.

Candidatos:

- FFmpeg integration;
- database engines;
- hardware acceleration;
- high-performance scanning.

### Sandboxed Plugin

Código de terceros ejecutado mediante WebAssembly/WASI.

Debe solicitar capacidades explícitas.

## Contract Versioning

Los plugins no deben depender principalmente de la versión global del servidor.

Preferido:

```text
provides:
  metadata-provider@2

requires:
  network@1
  configuration@1
```

En lugar de:

```text
requires server 5.3.7
```

## Manifest

Ejemplo conceptual:

```yaml
id: org.example.tmdb
name: TMDB Metadata
version: 2.1.0

provides:
  - metadata-provider@2

requires:
  - network@1
  - configuration@1

permissions:
  network:
    - api.themoviedb.org

entrypoint:
  wasm: plugin.wasm
```

## Capabilities

Posibles capacidades:

- network;
- filesystem-read;
- filesystem-write;
- media-read;
- media-write;
- gpu;
- process-exec;
- plugin-storage;
- notifications;
- secrets;
- config.

Por defecto, un sandboxed plugin no obtiene acceso que no haya solicitado.

## Runtime

Flujo:

```text
plugins/
   |
manifest discovery
   |
validation
   |
dependency resolution
   |
permission evaluation
   |
load
   |
health check
```

## Compatibility Tests

Cada contrato público debe poder disponer de una Compatibility Test Suite.

Ejemplo:

```text
Metadata Provider v2

[OK] search
[OK] external identifiers
[OK] artwork
[OK] cancellation
[OK] invalid ids
[OK] unicode
[OK] timeout
[OK] concurrency
```

## Swapping

Configuración conceptual:

```yaml
modules:
  metadata.primary: org.example.tmdb
  search.primary: core.search.sqlite
  transcoder.primary: core.transcoder.ffmpeg
```

Cambiar implementación no debe requerir recompilar consumidores.

## Future A/B support

El runtime debe poder evolucionar hacia múltiples implementaciones activas:

```text
recommendations:
  A: recommender-basic
  B: recommender-ml
  split: 50/50
```

Esto no es requisito para MVP, pero no debemos cerrar esa puerta.
