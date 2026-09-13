# Architecture

## 1. Architectural Style

Inicialmente se utilizará un **modular monolith**.

El sistema corre principalmente como un único proceso, pero cada subsistema está aislado por contratos.

Microservicios no son el punto de partida.

La arquitectura debe permitir extraer algunos módulos a procesos o nodos separados en el futuro sin reescribir el dominio.

## 2. High-level view

```text
                    Clients
          Web / TV / Android / Others
                       |
                  Public API
                       |
                  Application Host
                       |
                    Kernel
                       |
       +---------------+----------------+
       |               |                |
     Library         Playback          Users
       |               |                |
     modules         modules          modules
       |               |
  Metadata/Search   Media Engine
                       |
                 FFmpeg module
```

## 3. Kernel Responsibilities

El kernel gestiona:

- bootstrapping;
- module discovery;
- dependency resolution;
- module lifecycle;
- configuration;
- events;
- permissions;
- health;
- contracts;
- compatibility;
- observability hooks.

El kernel no implementa funcionalidades de dominio multimedia.

### Phase 1 module infrastructure

The initial module infrastructure separates the module catalogue, operational lifecycle and contract resolution:

- `IModuleRegistry` registers modules and exposes the catalogue;
- `IModuleRuntime` owns lifecycle state and module health;
- `IModuleResolver` resolves a selected module through a requested contract.

The application host is the composition root and may reference static module implementations only to register them. Consumers depend on contracts, never on concrete module classes. Host health remains distinct from temporary module diagnostics.

## 4. Dependency Rule

Las dependencias deben apuntar hacia contratos y dominio estable.

Correcto:

```text
Library
  |
  v
IMetadataProvider
  |
  +--> TMDB module
  +--> Local metadata module
```

Incorrecto:

```text
Library
  |
  v
TmdbMetadataProvider
```

## 5. API Boundary

Los clientes sólo pueden interactuar mediante la API pública.

Controllers deben:

- validar entrada;
- traducir DTOs;
- llamar application services;
- traducir respuesta.

Controllers no deben contener negocio.

## 6. Domain Boundary

El dominio multimedia debe ser independiente de:

- HTTP;
- UI;
- SQL;
- FFmpeg;
- proveedores externos;
- almacenamiento físico.

## 7. Data

Las interfaces de persistencia deben vivir en contratos o dominio de aplicación.

Las implementaciones concretas viven en módulos.

Ejemplos:

- SQLiteRepository
- PostgreSqlRepository

El dominio no conoce SQL.

## 8. Media Engine

Funciones diferentes:

- probing/análisis;
- demux;
- transcode;
- packaging;
- streaming.

No deben mezclarse en una clase gigantesca.

## 9. Playback

Flujo:

```text
PlaybackRequest
       |
ClientCapabilities
       |
AvailableMediaSources
       |
PlaybackPlanner
       |
PlaybackPlan
       |
PlaybackExecutor
```

El planner y el executor son piezas separadas.

## 10. Event Architecture

Eventos de dominio/aplicación sugeridos:

- MediaAdded
- MediaRemoved
- MediaUpdated
- LibraryScanStarted
- LibraryScanCompleted
- MetadataUpdated
- PlaybackStarted
- PlaybackStopped
- UserCreated
- ModuleLoaded
- ModuleFailed
- PluginInstalled

Inicialmente pueden ser in-process.

En el futuro puede existir un bridge hacia NATS u otro bus externo.

## 11. Distributed Evolution

La arquitectura debe permitir:

```text
Main Server
   |
 Event/Command transport
   |
   +-- scanner node
   +-- metadata node
   +-- transcode GPU node
   +-- search node
```

pero nunca se debe diseñar el MVP como sistema distribuido.

## 12. Observability

Todos los módulos deben tener:

- logs estructurados;
- metrics;
- traces;
- health;
- version;
- module id.

OpenTelemetry será la abstracción preferida.

## 13. Error Policy

Errores deben ser explícitos y clasificables:

- validation;
- unavailable dependency;
- timeout;
- permission denied;
- unsupported media;
- malformed media;
- provider failure;
- conflict;
- cancellation.

No usar excepciones genéricas para control de flujo normal.
