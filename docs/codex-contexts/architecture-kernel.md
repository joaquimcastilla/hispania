# Architecture & Kernel

## Lectura obligatoria

- `CODEX.md`, `PROJECT_CONTEXT.md`, `docs/ARCHITECTURE.md` y `docs/ROADMAP.md`.
- ADR-0001, ADR-0002 y ADR-0003.
- ADR-0004 sólo si el trabajo trata plugins no confiables; sigue en estado Proposed.

## Estado y ámbito

Fase de roadmap: Phase 1 — Completed. El ámbito abarca la infraestructura existente de `core/kernel`, `core/runtime`, `contracts/module-system`, `apps/server`, `modules/architecture-test` y sus tests de runtime. El servidor sólo es composition root para registrar implementaciones disponibles.

Las decisiones aceptadas son modular monolith, kernel mínimo y modularidad contract-first. Registry, runtime y resolver tienen responsabilidades separadas; los consumidores dependen de contratos, no de implementaciones concretas.

## Límites

Puede modificar ese ámbito y su documentación cuando una tarea lo autorice. No incorpora dominio Media, proveedores, almacenamiento, UI ni nuevas dependencias. No cambia contratos públicos, límites de módulos o arquitectura sin autorización y ADR cuando corresponda. Respeta que el kernel no conoce funcionalidades multimedia ni implementaciones de módulos.

Las decisiones de extensiones futuras permanecen pendientes; este contexto no las anticipa.
