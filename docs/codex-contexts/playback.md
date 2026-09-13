# Playback

## Lectura obligatoria

- `CODEX.md`, `PROJECT_CONTEXT.md`, `docs/ARCHITECTURE.md` y `docs/ROADMAP.md`.
- ADR-0003.
- ADR-0009 y documentación Media si el trabajo consume fuentes o representaciones.
- ADR-0014 y ADR-0015 cuando el trabajo trate Direct Play, acceso a fuentes o byte ranges.

## Estado y ámbito

Fase de roadmap: Phase 6 — Playback Engine 0.6. El flujo previsto separa capacidades de cliente, fuentes disponibles, planificación y ejecución mediante PlaybackRequest, ClientCapabilities, PlaybackPlanner, PlaybackPlan y PlaybackExecutor.

ADR-0015 está Accepted: define la frontera del primer Direct Play sin transcoding. Su implementación sigue requiriendo un ticket autorizado.

## Límites

No existe implementación aceptada de playback. No diseña clientes, transcoding, almacenamiento ni contratos nuevos fuera del alcance decidido por ADR-0015 y una tarea autorizada. Planner y executor deben permanecer separados.
