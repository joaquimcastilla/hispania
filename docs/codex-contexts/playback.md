# Playback

## Lectura obligatoria

- `CODEX.md`, `PROJECT_CONTEXT.md`, `docs/ARCHITECTURE.md` y `docs/ROADMAP.md`.
- ADR-0003.
- ADR-0009 y documentación Media si el trabajo consume fuentes o representaciones.

## Estado y ámbito

Fase de roadmap: Phase 6 — Playback Engine 0.6. El flujo previsto separa capacidades de cliente, fuentes disponibles, planificación y ejecución mediante PlaybackRequest, ClientCapabilities, PlaybackPlanner, PlaybackPlan y PlaybackExecutor.

## Límites

No existe diseño detallado ni implementación aceptada de playback. No diseña clientes, streaming, transcodificación, almacenamiento o contratos nuevos sin una tarea y las decisiones necesarias. Planner y executor deben permanecer separados.
