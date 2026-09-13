# HISPANIA Instructions for Codex

Lee este archivo antes de modificar el proyecto.

También debes leer:

- `VISION.md`
- `PROJECT_CONTEXT.md`
- `docs/ARCHITECTURE.md`
- `docs/MODULE_SYSTEM.md`
- ADRs relevantes en `docs/adr/`

## Regla principal

No improvises arquitectura.

Implementa tickets concretos respetando contratos y límites existentes.

## Obligaciones

Antes de modificar código:

1. Identifica el módulo afectado.
2. Identifica los contratos implicados.
3. Comprueba si existe una ADR relevante.
4. Evita introducir dependencias nuevas innecesarias.
5. Evita modificar interfaces públicas salvo que la tarea lo requiera explícitamente.
6. No añadas funcionalidad no solicitada.
7. Añade tests cuando corresponda.
8. Actualiza documentación si cambia comportamiento público.
9. Si una decisión cambia arquitectura, crea o propone una ADR.

## Prohibiciones

No:

- referencies una implementación desde otro módulo cuando existe un contrato;
- accedas directamente a la base de datos desde capas ajenas al módulo propietario;
- metas lógica de negocio en controllers;
- metas lógica de negocio en React;
- acoples el core a FFmpeg;
- acoples el core a SQLite;
- acoples el core a TMDB;
- acoples el sistema a un cliente concreto;
- añadas singletons globales ad hoc;
- crees "helpers" compartidos sin propietario claro;
- dupliques modelos de dominio;
- saltes el event/contract boundary sólo por comodidad;
- introduzcas microservicios sin ADR;
- introduzcas una nueva dependencia sin justificarla.

## Estilo de implementación

Preferir:

- funciones pequeñas;
- tipos explícitos;
- cancellation tokens;
- async/await;
- immutable data donde tenga sentido;
- errores tipados;
- logging estructurado;
- dependency injection;
- tests deterministas;
- APIs idempotentes cuando sea posible;
- contracts versionables;
- feature flags para comportamiento experimental.

## Trabajo por tickets

Cada ticket debe tener:

- objetivo;
- alcance;
- fuera de alcance;
- contratos afectados;
- tests esperados;
- criterios de aceptación.

Ejemplo:

Issue: Implementar MediaAnalyzer.FFprobe

Objetivo:
Implementar `IMediaAnalyzer@v1`.

No modificar:
- contrato;
- database layer;
- playback;

Requisitos:
- proceso cancelable;
- timeout;
- parseo robusto;
- errores tipados;
- unit tests;
- integration test con fixture conocido.

## Commits

Preferir commits pequeños:

- `feat(kernel): add module registry`
- `feat(media): add media entity model`
- `feat(scanner): implement filesystem scanner`
- `feat(ffprobe): implement media analyzer`
- `test(metadata): add contract compatibility suite`
- `docs(adr): define wasm plugin runtime`

## Definition of Done

Una tarea no está terminada si:

- compila pero viola arquitectura;
- no tiene tests razonables;
- rompe contratos existentes;
- introduce warnings nuevos;
- cambia comportamiento público sin documentación;
- deja código muerto;
- requiere conocimiento oculto no documentado.
