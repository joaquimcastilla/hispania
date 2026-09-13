# Authentication & Users

## Lectura obligatoria

- `CODEX.md`, `PROJECT_CONTEXT.md`, `docs/ARCHITECTURE.md`, `docs/ROADMAP.md` y `docs/SECURITY_MODEL.md`.
- ADR-0003.

## Estado y ámbito

Fase de roadmap: Phase 8 — Users 0.8. Están previstos usuarios, perfiles, autenticación local, autorización, sesiones y registro de dispositivos, junto con `IIdentityProvider`, `IAuthenticationProvider` e `IAuthorizationProvider`. No hay diseño implementado en este contexto.

## Límites

No introduce identidad, autorización, credenciales, proveedores ni dependencias de seguridad antes de una tarea autorizada. No modifica API pública, kernel o datos de otros dominios sin coordinación.
