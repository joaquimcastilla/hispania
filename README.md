# HISPANIA

**Home International Server Platform for Applications, Networking, Integration & Automation** es una plataforma modular en .NET para alojar suites y dominios independientes. Platform ofrece runtime, módulos, workers, eventos, Assets y Audit; Media es la primera suite y Inventory demuestra un segundo dominio. El sistema permite sustituir implementaciones mediante contratos versionados, instalar extensiones y ejecutar capacidades autorizadas en otros procesos o equipos.

HISPANIA funciona en una sola máquina. El coordinador y los workers remotos son opcionales: una instalación básica no necesita varios servidores ni una infraestructura de microservicios.

## Estado actual

La rama `feature/phase-12-platform-generalization` extiende la arquitectura con Platform Assets/Audit persistentes e Inventory. «Implementado» identifica funcionalidad presente; «validado» indica el alcance observado en pruebas o ejecución física. No implica soporte multi-coordinator ni aislamiento de código hostil.

| Área | Estado | Alcance y límites actuales |
|---|---|---|
| Kernel modular y contratos de módulo | Implementado y probado | Registro, resolución explícita de capacidades, dependencias, lifecycle y salud. |
| Extensiones `.hispania` | Implementado y probado | Paquetes ZIP inertes, manifiesto, SHA-256, firma de editor opcional y autorización de host separada. |
| Ejecución InProcess y OutOfProcess | Implementado y probado | La extensión InProcess debe ser de confianza; OutOfProcess usa Module Host y protocolo local versionado. Ninguno es un sandbox. |
| Lifecycle y hot swap | Implementado para el Playback Planner | La sustitución controlada, draining y rollback automático se limitan a `hispania.playback.planner@1.0`. |
| Experimentos Manual, A/B y Shadow | Implementado para Playback Planner | Pesos, cohortes estables, métricas, fallback y ciclo administrativo. Shadow solo admite la frontera de lectura declarada. |
| Catálogo multimedia | Implementado | Adaptadores SQLite y PostgreSQL seleccionables al iniciar; migraciones propias por proveedor. PostgreSQL live validado en Linux. |
| FFmpeg local y Playback | Implementado y validado | Worker FFmpeg por trabajo, salida MP4 opaca y entrega mediante Playback. |
| Worker remoto y mTLS | Implementado y validado físicamente | Windows 11 como coordinador y Ubuntu 24.04.5 como worker; autorización explícita por nodo/capacidad. |
| Leases, generaciones y fencing | Implementado y validado físicamente | Expiración, reasignación, rechazo de finalización obsoleta y recuperación transaccional SQLite. No ofrece exactly-once. |
| Coordinación distribuida | Un coordinador | El estado distribuido persistente usa SQLite con bloqueo exclusivo. PostgreSQL distribuido y multi-master están pendientes. |
| Cliente web Modern y Classic | Implementado | Dos builds web que consumen la API pública. |
| Windows y Linux | Validado en los alcances indicados abajo | CI en Linux; pruebas Windows locales y ejecución física Windows 11 ↔ Ubuntu para Phase 11. |
| Workers GPU, selección por carga y múltiples coordinadores | Pendiente | Inventario de aceleradores no equivale a scheduling GPU ni a soporte de estas funciones. |

## Arquitectura general

```text
HISPANIA PLATFORM
│
├── Platform Services
│   ├── Runtime
│   ├── Module System
│   ├── Supervisor
│   ├── Extensions
│   ├── Workers
│   ├── Events
│   ├── Assets
│   ├── Audit
│   └── Persistence abstractions
│
├── Media Suite
│
├── ERP Foundation
│   └── Inventory
│
└── Web Shell
    ├── Media UI
    └── Inventory UI
```

**HISPANIA Platform proporciona mecanismos. Los módulos de dominio proporcionan lógica de negocio.** Media es una suite de dominio sobre Platform. Inventory demuestra que un dominio no multimedia utiliza la misma infraestructura de runtime, contratos, eventos, Assets, Audit, persistencia y Web Shell. Inventory es una base ERP acotada, no un ERP completo.

La dirección de dependencias es:

```text
Platform → no depende de dominios

Media → Platform
Inventory → Platform
Future ERP → Platform
Store → Platform
Automation → Platform
```

Las implementaciones internas de dominios no dependen directamente unas de otras. El host de composición puede conectar contratos públicos cuando una función lo requiere; por ejemplo, la integración Asset→Media se compone en el host y no crea una dependencia de Platform o Inventory hacia Media.

Hito técnico de Phase 12: Platform compila sin Media y sin Inventory; WorkerAgent es genérico y FFmpeg es un proveedor Media; Inventory posee persistencia propia; Assets son referencias opacas persistentes y Audit es transversal y persistente; Asset→Media se compone en el host; Media conserva su comportamiento de Phase 11; y Web Shell presenta más de un dominio mediante registro compartido. La carga dinámica de módulos de frontend todavía no está implementada.

El servidor es un monolito modular: el host compone servicios y adaptadores; las aplicaciones dependen de contratos y dominio; las implementaciones pueden permanecer en el proceso, ejecutarse en un Module Host local o atender una tarea en un worker remoto.

```text
┌──────────────────────────── HISPANIA ────────────────────────────┐
│                                                                  │
│  Clientes                                                        │
│  ├── Web Modern / Classic                                        │
│  └── otros clientes                                              │
│          │ API pública                                           │
│          ▼                                                       │
│  ┌────────────────────────────────────────────────────────────┐  │
│  │ Server / composition root                                  │  │
│  │ Kernel runtime · contratos · health · lifecycle            │  │
│  │ Library · Metadata · Playback · administración             │  │
│  └──────────────────────────┬─────────────────────────────────┘  │
│                             │                                    │
│         ┌───────────────────┼─────────────────────┐              │
│         ▼                   ▼                     ▼              │
│   InProcess             OutOfProcess       Coordinador único     │
│   confiable             Module Host         SQLite + leases      │
│                              │                     │              │
│                      IPC local versionado          │ HTTPS + mTLS │
└────────────────────────────────────────────────────┼─────────────┘
                                                     ▼
                                        Worker Agent autorizado
                                                     │
                                           Capability / FFmpeg
```

El Kernel no contiene el dominio multimedia ni ejecuta SQL o FFmpeg. Mantiene el catálogo de módulos, resuelve contratos, valida dependencias y supervisa lifecycle y health. Las aplicaciones multimedia y los adaptadores de persistencia implementan capacidades fuera del Kernel.

## Kernel, módulos y lifecycle

El runtime separa tres responsabilidades: `IModuleRegistry` registra y describe módulos; `ICapabilityResolver` elige un proveedor para un contrato y una versión exacta; `IModuleRuntime` inicia, detiene y observa salud. El host es el composition root que ensambla esas piezas con las aplicaciones y adaptadores.

- Los descriptores declaran identidad, versión, contratos proporcionados y requeridos, dependencias y compatibilidad.
- La resolución compara exactamente `(contractId, contractVersion)`. Si hay varios proveedores, el consumidor o la configuración debe identificar uno; no se escoge silenciosamente por orden de registro.
- El runtime ordena el inicio según dependencias, valida que los proveedores requeridos estén activos y consulta health checks.
- Los módulos no deben referenciar directamente clases o assemblies de otros módulos. Se comunican mediante contratos públicos versionados.
- El Kernel no es un bus remoto, un motor de negocio multimedia, un gestor de bases de datos ni una frontera de seguridad para código nativo confiable.

Estados observables del módulo:

```text
Registered ──► Starting ──► Running ──► Draining ──► Stopping ──► Stopped
                   │            │             │            │
                   └────────────┴─────────────┴────────────┴──► Failed
                                │
                                └──► Degraded ──► Running (si recupera salud)
```

`Failed` puede resultar de un inicio, parada o recuperación fallidos. `Draining` marca una implementación en retirada; el router de Playback Planner deja de enviarle solicitudes nuevas y espera a que terminen las operaciones que ya capturaron esa generación.

## Contratos y capacidades

Una capacidad es una interfaz pública identificada por contrato y versión. El consumidor conoce ese contrato, no la clase concreta ni dónde se ejecuta:

```text
Consumer
   │ contrato público versionado
   ▼
Capability Resolver
   ├── Implementation A
   ├── Implementation B
   └── Remote implementation (cuando se habilita)
```

El proveedor puede ser un módulo, una extensión o un proxy hacia trabajo distribuido. El consumidor no necesita conocer su clase, assembly, proceso, servidor o sistema operativo. En ejecución remota, el adaptador de Playback conserva su contrato y el coordinador traduce la operación elegible al protocolo de trabajo distribuido.

## Modelos de ejecución

### InProcess

```text
HISPANIA Server
      │ extensión autorizada y confiable
      ▼
Collectible AssemblyLoadContext
      │ AssemblyDependencyResolver
      ▼
Extensión .NET + contratos compartidos del host
```

`AssemblyLoadContext` separa la resolución de dependencias de la extensión y comparte las assemblies de contrato con el host. El contexto es collectible y puede descargarse después de detener la extensión y liberar sus referencias. Esto facilita reemplazo y aislamiento de dependencias, pero **no** limita permisos: código InProcess hereda los privilegios del servidor y no está aislado de forma segura.

### OutOfProcess local

```text
HISPANIA Server ── IPC local versionado ──► Module Host
                                               │
                                               ▼
                                       Extensión/worker local
```

El proceso separado contiene algunos fallos y permite terminar o reiniciar el host. El protocolo de módulo usa framing local (Named Pipes en .NET); no se expone como listener de red. Los límites de recursos aplican `Job Objects` en Windows y `prlimit` en Linux donde se configura el worker. Son límites operativos, no un sandbox. Las políticas de recuperación limitan los reintentos y no vuelven a ejecutar automáticamente toda operación.

### Worker remoto

```text
Windows 11 / HISPANIA Server                 Ubuntu / Worker
┌─────────────────────────┐                 ┌────────────────────────┐
│ Playback + coordinador  │── HTTPS + mTLS ─►│ Worker Agent           │
│ leases + broker de media│                 │ capacidad autorizada   │
└─────────────────────────┘                 │ Module/FFmpeg Worker   │
                                            └───────────┬────────────┘
                                                        ▼
                                                     FFmpeg
```

El Worker Agent es genérico y puede ejecutarse en otro ordenador físico. Anuncia inventario y capacidades, reclama tareas autorizadas, renueva leases y ejecuta el handler configurado. La validación física de Phase 11 usó Windows 11 como coordinador y Ubuntu 24.04.5 como worker. La conexión remota es opcional; sin worker, el modo `Local` sigue siendo la opción predeterminada.

## Extensiones instalables

Un archivo `.hispania` es un ZIP de formato versionado con `module.json` en la raíz y los archivos publicados del paquete. El manifiesto registra identidad y versión, contratos, runtime, assembly de entrada, dependencias, mapa de SHA-256 y, cuando se firma, editor, algoritmo y firma.

```text
extension.hispania (ZIP)
├── module.json                  # manifiesto, mapa de hashes y firma opcional
├── Hispania.Extension.dll       # assembly de entrada/publicación
├── *.dll                        # contratos y dependencias del paquete
└── ...                          # restantes archivos publicados
```

El instalador valida límites, rutas, compatibilidad, declaración de contratos y hashes; guarda bytes inertes y no ejecuta el paquete. El firmador usa RSA-PSS con SHA-256. La identidad de publisher debe ser confiable para considerar confiable la firma. La autorización de ID de módulo por el host es otra decisión:

```text
Installed ≠ Trusted ≠ Active
```

Instalar no implica confiar ni activar. Activar requiere autorización del host y contratos compatibles. Desactivar detiene el módulo; una actualización se instala en una carpeta de versión aparte y la sustitución deja activa la versión anterior hasta que la candidata inicia y supera health. Rollback y retirada dependen del ciclo soportado por el módulo; no existe una consola universal para todas las extensiones.

## Hot swap y generaciones

El hot swap supervisado implementado se limita al Playback Planner reemplazable. La generación nueva se inicia y valida antes de recibir tráfico; las peticiones ya iniciadas conservan la generación que capturaron.

```text
Generation A recibe solicitudes nuevas
             │
             ├── Iniciar y verificar Generation B
             │
Nuevas solicitudes ─────────────────────────► B
Solicitudes en vuelo ───────────────────────► A
             │
A entra en Draining ──► espera 0 solicitudes ──► Stop / retire
```

Si la candidata falla antes de activarse, A permanece activa. Si la activación o el draining no pueden completarse, el servicio restaura A cuando es posible. Un fallo de operación de la generación activa también intenta el rollback a la generación saludable anterior. Este mecanismo específico no equivale a hot swap universal de cualquier servicio o dependencia.

## Experimentos A/B y Shadow

El router de variantes es reutilizable, pero hoy lo emplea Playback Planner y solo acepta capacidades que declaran expresamente compatibilidad con el modo elegido. Admite selección manual, `TrafficSplit` y `Shadow`:

```text
                    ┌── Variant A ──► peso configurado
Request ─► Router ──┤
                    └── Variant B ──► peso configurado
```

Las asignaciones pueden mantenerse estables mediante una clave seudonimizada y una sal administrada; las ponderaciones usan puntos básicos y deben sumar 10 000. Estado y métricas permiten observar resultados, suspender o retirar variantes y aplicar fallback o rollback. La persistencia del experimento es optativa y de un único host; por defecto el estado está en memoria.

```text
Clave de usuario/sesión ─► asignación estable ─► Router ─► misma variante
```

En Shadow, la variante secundaria solo compara su planificación con la respuesta activa; el cliente recibe siempre el resultado activo. La capacidad debe declarar la frontera read-only `IMediaCatalogReader`. No se ejecutan sombras de transcodificación ni operaciones de escritura compartida: declarar una interfaz de lectura restringe las dependencias inyectadas, pero no sandboxea código nativo confiable.

## Phase 11 — trabajo distribuido

El coordinador único mantiene autorización de nodos, estado de trabajos, leases, generaciones y publicación de resultados. Los workers no se inscriben por sí solos: un operador autoriza el ID, el fingerprint SHA-256 del certificado cliente y la lista exacta de capacidades permitidas.

```text
┌──────────────────────── HISPANIA Main ────────────────────────┐
│ Playback │ Coordinador único │ Scheduler │ Lease Manager      │
│          │ SQLite transaccional │ Media Transfer Broker       │
└───────────────────────────┬───────────────────────────────────┘
                            │ HTTPS + mTLS
                            ▼
┌──────────────────── Worker autorizado ───────────────────────┐
│ Worker Agent │ Capability handler │ FFmpeg Worker │ FFmpeg    │
│ Concurrencia y límites de recursos configurados               │
└───────────────────────────────────────────────────────────────┘
```

El transporte remoto HTTPS/mTLS es distinto del protocolo IPC local. El coordinador valida certificado y autorización, y cada trabajo se asigna a un nodo que anuncia la versión exacta de la capacidad. El inventario puede incluir CPU, arquitectura o etiquetas de acelerador, pero las etiquetas de hardware son informativas: el scheduler actual selecciona por autorización, capacidad exacta, salud/heartbeat, concurrencia y reglas deterministas; no ofrece planificación por carga ni compatibilidad GPU automática.

### Transcoding remoto desde Playback

```text
Cliente
  │
  ▼
Playback ─► ICompatibleMediaTranscoderV1 ─► Scheduler
                                             ├── Local ─► FFmpeg Worker
                                             └── Remote
                                                   │ lease activa
                                                   ▼
                                             Worker autorizado
                                             ├── solicita bloques de fuente
                                             ├── FFmpeg / libx264
                                             └── sube resultado limitado
                                                   │ hash verificado
                                                   ▼
                                      handle opaco ─► Playback ─► Cliente
```

Los modos configurables son:

| Modo | Comportamiento |
|---|---|
| `Local` | Predeterminado; ejecuta transcoding local sin asignar trabajo remoto. |
| `RemotePreferred` | Intenta usar una capacidad remota elegible; si no hay resultado remoto, cae al transcoding local. |
| `RemoteOnly` | No ejecuta FFmpeg local; si no consigue resultado remoto, Playback informa que no está disponible. |

El worker no necesita montar ni indexar la biblioteca multimedia. No recibe una ruta general al catálogo: el broker emite una concesión vinculada a job/lease, limita el acceso al medio autorizado y sirve bloques de bytes. El resultado se publica con longitud y SHA-256 validados y se devuelve como handle opaco, no como ruta del worker.

### Leases, generaciones y fencing

Cada asignación tiene propietario, caducidad y generación monotónica. El heartbeat renueva la lease vigente. Una reasignación incrementa la generación, de modo que el trabajador anterior no puede completar ni publicar un resultado válido:

```text
Job #42 · Generation 7 · Worker A
                 │ Worker A deja de renovar
                 ▼
             Lease expira
                 │ reasignación
                 ▼
Job #42 · Generation 8 · Worker B
                 │
Worker A vuelve e intenta publicar generación 7
                 ▼
              RECHAZADO
```

El coordinador valida propietario y generación al actualizar y publicar el resultado. Tras reinicio, invalida las leases activas: reintenta únicamente trabajo declarado retry-safe y sin efectos externos; el trabajo de seguridad incierta pasa a `Uncertain`. Esto impide aceptar finalizaciones obsoletas, pero no garantiza ejecución exactly-once. Un crash en la estrecha ventana entre publicar el archivo y persistir la finalización puede dejar un artefacto sin referencia hasta su limpieza; Playback no entrega ese artefacto como resultado.

## Persistencia

Hay dos ámbitos distintos:

| Ámbito | Implementación actual | Recuperación y límites |
|---|---|---|
| Catálogo y datos de Media | Adaptadores SQLite y PostgreSQL seleccionables al iniciar | Migraciones por proveedor; PostgreSQL live probado en CI y con una prueba dedicada en Linux. |
| Coordinación de trabajo remoto | SQLite transaccional | Snapshot y lock exclusivo `.coordinator.lock`; solo un coordinador puede poseer ese estado. |
| Experimentos Playback | Memoria por defecto; archivo JSON local opcional | La persistencia opcional es de un host y requiere una sal estable protegida. |

SQLite coordinador soporta recuperación y fencing de un único proceso servidor. PostgreSQL para el catálogo **no** significa que el estado distribuido soporte PostgreSQL ni varios coordinadores. PostgreSQL distribuido, consenso y multi-master permanecen pendientes.

## Seguridad y límites

### Controles implementados

- SHA-256 por archivo de paquete; verificación opcional de firma RSA-PSS/SHA-256 y confianza/revocación de editor configurada por el host.
- Allowlist de módulos y autorización explícita de capacidades por worker.
- HTTPS con certificados cliente, validación de identidad/fingerprint y negociación de protocolo.
- Grants de transferencia vinculados a la lease activa, bloques acotados, límites de tamaño, hash del resultado y handles opacos.
- Fencing por generación, límites de proceso configurados y cleanup de artefactos parciales/expirados.
- `Job Objects` en Windows y `prlimit` en Linux para los límites de proceso que admite la política del worker.

### Lo que estos controles no son

**Proceso separado ≠ sandbox. TLS/mTLS ≠ sandbox. `AssemblyLoadContext` ≠ sandbox.** OutOfProcess contiene fallos y aplica algunos techos operativos; el proceso aún hereda usuario, entorno y acceso de la máquina. mTLS autentica extremos autorizados, no protege frente al compromiso de un worker legítimo. No se promete protección total frente a extensiones maliciosas, aislamiento de kernel, cuotas globales de disco ni exactly-once. Ejecuta solo workers y extensiones de confianza, con cuentas y permisos del sistema limitados por el operador.

## Compatibilidad validada

| Componente | Windows | Linux |
|---|---|---|
| HISPANIA Server / runtime .NET 10 | Compilación y suites locales validadas | CI y suite completa validadas en Ubuntu 24.04.5 |
| Worker Agent | Pruebas de protocolo/proceso; coordinador físico de Phase 11 | Worker físico Ubuntu validado con coordinator Windows |
| FFmpeg / FFprobe | Ejecución local validada | FFmpeg remoto real y FFprobe validados en Ubuntu |
| OutOfProcess | Pruebas Windows | Pruebas Linux y ejecución física de módulos/worker |
| Catálogo SQLite | Validado | Validado |
| Catálogo PostgreSQL | Adaptador .NET disponible; live verificado en workflow Linux | PostgreSQL 16 live probado en CI y en prueba dedicada |
| Estado distribuido PostgreSQL / multi-coordinator | No implementado | No implementado |
| Job Objects | Aplicados por la política de Windows | No aplica |
| `prlimit` | No aplica | Aplicado por la política Linux; requiere que esté instalado |

La evidencia de Phase 11 incluye suite .NET completa en Windows y Ubuntu; la suite Linux completa registró 472 aprobadas, 0 fallidas y 1 omitida por no configurar PostgreSQL en esa ejecución. Después, la prueba PostgreSQL omitida se ejecutó explícitamente contra PostgreSQL 16.15 y pasó 1/1, 0 fallos, 0 omisiones. El workflow GitHub `Validation` ejecutó CI Linux con PostgreSQL 16 en [el commit `85b35b2`](https://github.com/joaquimcastilla/hispania/actions/runs/36357738829). La ejecución física Windows 11 ↔ Ubuntu 24.04.5 incluyó registro/autorización mTLS, transcode FFmpeg remoto consumido por Playback, fallo del worker, recuperación, expiración y reasignación de lease, fencing de generación obsoleta y rechazo HTTP 409. Los commits de cierre documentado son `8137963` y `652692c`.

## Evolución futura

Las siguientes capacidades son trabajo futuro; no forman parte de la Phase 12:

- Incorporar varios workers con políticas de scheduling que consideren carga real y prioridades.
- Seleccionar capacidades por CPU/GPU y compatibilidad efectiva, no solo por etiquetas informativas.
- Añadir colas, afinidad y redistribución explícita de trabajos compatible con las políticas de retry y fencing.
- Validar workers heterogéneos y estrategias de recuperación con más de un nodo.

La coordinación multi-master, P2P público y federación entre instalaciones son trabajo futuro; no forman parte de las capacidades actuales.

## Mapa del repositorio

| Ruta | Contenido |
|---|---|
| `apps/server` | Composition root, API, administración, Playback, coordinador y transferencia. |
| `core/runtime` | Registro, resolver, lifecycle, extensiones, router de experimentos y protocolo de worker genérico. |
| `contracts` | Contratos públicos versionados de módulos, Media, Playback y workers. |
| `application`, `core/domain` | Casos de uso y modelo de dominio independientes de adaptadores. |
| `infrastructure` | FFmpeg/FFprobe, proveedores de metadatos y persistencia SQLite/PostgreSQL. |
| `apps/module-host`, `apps/worker-agent`, `apps/ffmpeg-worker` | Hosts de proceso local/remoto y worker de transcoding. |
| `apps/web` | Clientes web Modern y Classic. |
| `tests` | Unitarias, integración de procesos, Playback, persistencia y workers. |

## Documentación relacionada

- [Arquitectura](docs/ARCHITECTURE.md)
- [Sistema de módulos](docs/MODULE_SYSTEM.md)
- [ADR-0029: trabajo distribuido con coordinador único](docs/adr/ADR-0029-single-coordinator-distributed-work.md)
- [Registro de progreso](PROGRESS-LOG.md)
- [Guía de contribución](docs/CONTRIBUTING.md)
- [Visión](VISION.md) · [Contexto del proyecto](PROJECT_CONTEXT.md) · [Auditoría de modularidad](docs/MODULARITY_AUDIT_2026-09-23.md)
