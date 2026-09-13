# Contextos de Codex

Los contextos de Codex son fronteras de conversación y trabajo, no necesariamente bounded contexts, módulos runtime ni proyectos compilables.

Su objetivo es permitir conversaciones especializadas sin convertir su historial en conocimiento oculto. La fuente de verdad del proyecto es Git junto con `CODEX.md`, `PROJECT_CONTEXT.md`, `docs/ARCHITECTURE.md`, `docs/ROADMAP.md`, las ADR Accepted y la documentación específica de cada dominio.

Cada archivo de este directorio es un índice: señala qué leer, cuál es su alcance y qué límites respetar. No duplica ADRs ni diseña subsistemas todavía no definidos. Si una decisión cambia, se actualiza en su ADR o documento canónico, no aquí.

Un contexto puede leer documentación de otro cuando exista una dependencia real. Esa lectura no concede autoridad para modificar el ámbito ajeno: debe declararse y coordinarse antes.

## Inicio de una conversación

```text
Estás trabajando en HISPANIA dentro del contexto <CONTEXT>.

Antes de hacer nada:
1. Lee CODEX.md.
2. Lee PROJECT_CONTEXT.md.
3. Lee docs/ARCHITECTURE.md.
4. Lee docs/ROADMAP.md.
5. Lee docs/codex-contexts/<CONTEXT>.md.
6. Lee todos los documentos y ADRs que ese contexto declare obligatorios.
7. Inspecciona el estado Git actual.

El repositorio es la fuente de verdad.
No asumas decisiones procedentes de conversaciones anteriores que no estén documentadas.
No modifiques ámbitos externos a este contexto sin indicarlo primero.
Antes de implementar, informa de tu interpretación del estado actual y del alcance.
```

## Trabajo paralelo futuro

Si varias conversaciones modifican código simultáneamente, no deben trabajar todas directamente sobre `main`. Se usarán feature branches o git worktrees según la tarea. Este documento no crea ramas ni worktrees.

## Contextos

| Contexto | Propósito |
| --- | --- |
| `architecture-kernel.md` | Kernel, runtime y reglas de modularidad. |
| `media-domain.md` | Dominio Media 0.2. |
| `metadata.md` | Identificación y metadata como extensión modular. |
| `library-scanner.md` | Biblioteca, fuentes y escaneo futuro. |
| `database-persistence.md` | Persistencia futura. |
| `playback.md` | Planificación y ejecución de reproducción futuras. |
| `streaming.md` | Streaming futuro. |
| `transcoding.md` | Transcodificación futura. |
| `search.md` | Búsqueda futura. |
| `authentication-users.md` | Usuarios y seguridad futuros. |
| `web-ui.md` | Cliente web futuro. |
| `clients.md` | Clientes adicionales futuros. |
| `deployment-operations.md` | Operación y despliegue futuros. |
| `distributed-p2p.md` | Sistema distribuido futuro, fuera del alcance actual. |
