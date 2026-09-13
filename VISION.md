# HISPANIA Vision

## Qué queremos construir

Una plataforma open source de home server moderna, modular y extensible.

El primer objetivo funcional es crear un servidor multimedia completo desde cero, inspirado conceptualmente por productos como Jellyfin pero sin reutilizar código, APIs, base de datos ni arquitectura interna de Jellyfin.

La plataforma debe evolucionar hacia un sistema donde las funcionalidades sean módulos intercambiables que se conectan mediante contratos explícitos.

## Visión a largo plazo

Queremos que un usuario pueda instalar un único núcleo y después añadir o sustituir capacidades como piezas de un puzzle:

- almacenamiento;
- bibliotecas;
- metadata;
- búsqueda;
- reproducción;
- transcoding;
- autenticación;
- subtítulos;
- recomendaciones;
- red local;
- red distribuida;
- fotografías;
- backups;
- IA local;
- automatización;
- monitorización.

## Diferenciador principal

La modularidad no debe ser un mecanismo de extensiones añadido posteriormente. Debe ser la propiedad central del sistema desde el primer commit.

El objetivo no es tener "plugins que añaden cosas" a un monolito, sino poder sustituir implementaciones enteras detrás de contratos estables.

Ejemplos:

- SQLite -> PostgreSQL
- TMDB -> otro proveedor de metadata
- FFmpeg -> otro media engine
- búsqueda simple -> Meilisearch
- PlaybackPlanner v1 -> PlaybackPlanner v2
- Auth local -> OIDC
- scanner clásico -> scanner alternativo
- recommendation engine A -> B

## Principios de producto

1. Open source.
2. Self-hosted primero.
3. Control del usuario sobre sus datos.
4. Clientes desacoplados.
5. API pública estable.
6. Módulos sustituibles.
7. Kernel pequeño.
8. Observabilidad integrada.
9. Seguridad por capacidades.
10. Compatibilidad y migraciones como elementos de primera clase.
11. Nada de dependencias conceptuales innecesarias con proveedores concretos.
12. La biblioteca representa entidades multimedia, no sólo archivos.

## Ambición

A largo plazo, el proyecto podría convertirse en una plataforma general de home server comparable conceptualmente a un "runtime de aplicaciones domésticas", con Media como primer vertical destacado.
