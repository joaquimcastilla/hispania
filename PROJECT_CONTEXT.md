# HISPANIA Project Context

Este documento resume las decisiones tomadas hasta ahora y debe leerse antes de realizar cambios arquitectónicos.

## 1. Naturaleza del proyecto

Se construirá un servidor multimedia completamente nuevo.

No se reutilizará:

- código de Jellyfin;
- backend de Jellyfin;
- API de Jellyfin;
- base de datos de Jellyfin;
- frontend de Jellyfin;
- compatibilidad obligatoria con Jellyfin.

Sí pueden utilizarse dependencias generales y estándares del ecosistema, por ejemplo:

- .NET / ASP.NET Core;
- React / TypeScript;
- FFmpeg / ffprobe;
- SQLite / PostgreSQL;
- OpenTelemetry;
- Wasmtime;
- Docker / OCI;
- NATS;
- OpenAPI.

## 2. Arquitectura fundamental

Se utilizará inicialmente un **modular monolith**.

La modularidad no implica microservicios.

El proceso principal podrá cargar múltiples módulos, pero éstos deberán interactuar mediante contratos explícitos.

El kernel debe permanecer pequeño.

El core no debe conocer implementaciones concretas.

## 3. Kernel

El kernel debe contener sólo responsabilidades estructurales:

- runtime;
- module registry;
- lifecycle;
- configuration;
- event infrastructure;
- security;
- capability/permission model;
- contract resolution;
- health reporting;
- version compatibility.

No deben pertenecer al kernel:

- películas;
- series;
- música;
- FFmpeg;
- TMDB;
- SQLite;
- búsqueda;
- transcoding;
- recomendaciones;
- subtítulos;
- clientes.

## 4. Contratos

Las fronteras entre subsistemas estarán definidas por contratos versionados.

Ejemplos:

- `IFileSource`
- `ILibraryScanner`
- `IMediaAnalyzer`
- `ILibraryRepository`
- `IMetadataProvider`
- `IArtworkProvider`
- `ISearchProvider`
- `IPlaybackPlanner`
- `ITranscoder`
- `IIdentityProvider`
- `IAuthenticationProvider`
- `IContentResolver`
- `IContentTransport`

Un módulo debe depender del contrato, nunca de otra implementación.

## 5. Tipos de extensibilidad

Se contemplan tres niveles:

### Core

Código esencial del runtime.

### Native Modules

Módulos oficiales o de confianza con máximo rendimiento.

Tecnologías candidatas:

- C#
- Rust

### Sandboxed Plugins

Plugins de terceros ejecutados con aislamiento.

Tecnología preferida:

- WebAssembly Component Model
- WASI
- Wasmtime

Los plugins deben utilizar permisos/capabilities explícitos.

## 6. Stack inicial preferido

Backend:
- C#
- .NET LTS
- ASP.NET Core

Frontend:
- TypeScript
- React
- Vite

High-performance optional components:
- Rust

Media:
- FFmpeg
- ffprobe

Data:
- SQLite inicialmente
- PostgreSQL como implementación futura

Observability:
- OpenTelemetry

Tests:
- xUnit
- Vitest
- Playwright

Containerization:
- Docker / OCI

CI:
- GitHub Actions

## 7. Clientes

Ningún cliente debe contener lógica de negocio crítica.

Todos los clientes deben utilizar una API pública.

Clientes previstos:

- web;
- Tizen;
- Android;
- Android TV;
- desktop;
- futuros clientes de terceros.

El frontend web no debe tener privilegios arquitectónicos frente a otros clientes.

## 8. Modelo multimedia

Una obra no equivale a un archivo.

Debe existir separación entre:

- Work
- Edition
- Timeline
- MediaEntity
- MediaSource
- Track / Asset

Una entidad puede tener múltiples representaciones.

Ejemplo de canción:

- audio FLAC;
- audio AAC;
- videoclip 1080p;
- videoclip 4K;
- lyrics;
- timed lyrics;
- traducciones;
- subtítulos del videoclip.

Ejemplo de película:

- múltiples calidades;
- varias pistas de audio;
- subtítulos independientes;
- diferentes ediciones;
- diferentes timelines.

## 9. Playback

La reproducción debe separarse en:

- capacidades del cliente;
- capacidades de las fuentes;
- selección de fuente;
- planificación;
- ejecución.

El `PlaybackPlanner` debe ser sustituible.

El transcoder debe ser sustituible.

## 10. Pipelines

Los flujos complejos deben poder convertirse en pipelines configurables.

Ejemplo de ingest:

Filesystem
-> Parser
-> Analyzer
-> Identifier
-> Metadata
-> Artwork
-> Repository
-> Search Index

Cada etapa debe poder implementarse mediante distintos módulos.

## 11. Observabilidad

Cada módulo debe exponer:

- versión;
- latencia;
- errores;
- timeouts;
- CPU;
- RAM;
- throughput;
- métricas propias.

El sistema debe permitir benchmarking y comparación de implementaciones.

## 12. Experimentación

A largo plazo se desea poder probar:

- Module A vs Module B;
- algoritmo A vs B;
- scanner A vs B;
- recommendation engine A vs B.

La arquitectura debe evitar impedir este escenario.

## 13. Red de preservación / conocimiento multimedia

Este sistema debe estar separado del kernel y del media server básico.

Conceptos separados:

1. Knowledge:
   qué obras sabemos que existen.

2. Preservation:
   qué assets han sobrevivido.

3. Storage:
   dónde existen copias verificadas.

4. Distribution:
   quién puede obtenerlas y bajo qué condiciones.

Estados sugeridos:

- AVAILABLE
- PARTIALLY_PRESERVED
- MISSING
- LOST
- AT_RISK
- PRIVATE_ARCHIVE
- RIGHTS_RESTRICTED
- UNKNOWN

La red no debe asumir que preservación equivale a distribución.

## 14. Red distribuida

A futuro puede existir un módulo global de contenidos.

El kernel no debe saber que existe P2P.

Se usarán contratos genéricos como:

- `IContentResolver`
- `IContentProvider`
- `IContentPublisher`
- `IContentTransport`

Posibles transportes:

- local;
- HTTP;
- S3;
- LAN;
- P2P;
- otros.

## 15. Ingest global controlado

Los nodos normales no deben poder publicar blobs arbitrarios a un catálogo global.

La publicación global debe pasar por:

Submission
-> Quarantine
-> Automated Validation
-> Review
-> Signed Manifest
-> Approved Catalog

Sólo cuentas autorizadas pueden proponer nuevos assets.

La biblioteca local del usuario nunca se publica automáticamente.

## 16. Lost media

La red de conocimiento debe permitir registrar obras aunque el archivo no exista.

Debe poder almacenarse evidencia de existencia:

- publicaciones;
- revistas;
- programación;
- fotografías;
- documentación;
- referencias de archivo.

Una obra puede estar:

- conocida;
- no preservada;
- preservada;
- restringida;
- perdida.

Debe existir una vía específica para trabajos no presentes en grandes bases de metadata.

## 17. Regla crítica para Codex

Codex nunca debe sacrificar una frontera arquitectónica por resolver un ticket rápidamente.

Si una tarea exige romper un contrato o introducir una dependencia transversal, debe detenerse y proponer una ADR antes de implementarla.
