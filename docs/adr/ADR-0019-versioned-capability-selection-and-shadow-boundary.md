# ADR-0019: Versioned Capability Selection and Shadow Boundary

Status: Accepted

## Context

`IModuleResolver` selected an implementation only by module ID and had no declared capability/version metadata. Functional substitution was demonstrated only with architecture-test modules. HISPANIA needs an explicit, consumer-independent way to select implementations without turning the kernel into a media service locator.

Shadow comparisons must not change effective state. Native in-process code cannot be prevented from performing side effects by a generic wrapper.

## Decision

- A module descriptor may declare provided and required `(contractId, Version)` capability values.
- The runtime resolves a requested CLR contract against an exact declared capability version. Hosts may specify an implementation ID. No registration-order/default winner is permitted when several providers match.
- The application/host composition root owns the concrete implementation selection. Functional consumers continue to receive their contract through DI.
- Shadow execution returns only the active result, records the secondary result or error, and contains the secondary exception.
- Shadow-capable operations must be pure/read-only or receive a genuinely read-only capability. This restriction is documented but cannot be enforced for trusted native assemblies.
- In-process implementation selection occurs at startup. Hot unload, resource isolation, A/B assignment, and process IPC are not claimed.

## Consequences

Positive: manual substitution applies to functional contracts; missing/ambiguous providers fail explicitly; alternatives can be compared without changing the active result.

Costs: the host statically registers native implementations; exact contract versions require a new selection when consumers move versions; shadow safety depends on implementation discipline; changing selection requires restart.

## Initial functional adoption

`IPlaybackPlanner` v1 is registered as `hispania.playback.planner@1.0`. The server selects `Modules:PlaybackPlanner` and defaults explicitly to the existing direct planner. A second planner implementation is used in runtime tests to prove exchange. This ADR does not introduce production A/B testing.

## Follow-up implementation boundaries

- FFprobe consumes a read-only media source stream through `IMediaSourceContentAccessV1` owned by Media Domain (`hispania.media.source-content-access@1.0`). It no longer resolves library keys or depends on the local filesystem provider. The local adapter continues to enforce canonical root containment and rejects reparse-point files.
- `ICompatibleMediaTranscoderV1` belongs to Playback Contracts. FFmpeg runs in `Hispania.Transcoding.FFmpeg` as a native module; the host registers/selects it and the HTTP endpoint sees only a neutral result. The cache is keyed by source identity and observed snapshot; source bytes are never overwritten.
- `IUniqueConstraintViolationClassifier` keeps EF metadata orchestration independent of SQLite exception types. SQLite owns its implementation. EFCore still contains schema-specific scalar mapping for canonical UUID BLOBs and timestamp/decimal columns; it is not yet a provider-neutral persistence adapter and no PostgreSQL compatibility is claimed.
- The web frontend builds independently to `dist/modern` and `dist/classic`; `Web:Build` chooses one at process startup. Both builds share application screens and HTTP contracts but have separate entrypoints and theme sheets. This is manual build selection, not backend A/B assignment.

Shadow comparison is applied to the read-only playback-planning contract in `ShadowPlaybackPlanner`. Tests verify both planners run and only the selected planner result is returned. Because trusted native code can still have hidden side effects, this does not constitute a sandbox or enforceable resource boundary.
