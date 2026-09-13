# Deployment & Operations

## Lectura obligatoria

- `CODEX.md`, `PROJECT_CONTEXT.md`, `docs/ARCHITECTURE.md`, `docs/ROADMAP.md` y `docs/SECURITY_MODEL.md`.
- ADR-0001, ADR-0002 y ADR-0003.

## Estado y ámbito

El baseline de Phase 10 contempla Docker deployment y backup/restore básicos. La arquitectura define observabilidad como dirección futura, pero no hay diseño operativo ni automatizaciones aprobadas en este contexto.

## Límites

No crea Docker, despliegues, secretos, publicación, monitorización externa, infraestructura cloud ni nuevas automatizaciones. No cambia CI existente ni configuración de seguridad sin una tarea autorizada.
