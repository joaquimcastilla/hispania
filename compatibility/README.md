# Compatibility evidence

Platform/tooling owns this scaffold and catalog format; each contract owner owns its fixtures and migration policy. Fixtures must identify contract ID, source release/commit, producer/consumer versions, compatibility category, expected behavior and regeneration/review instructions. Use synthetic data without secrets. Never rewrite an old golden fixture to make a new reader pass; add a new fixture and record why coexistence or migration is valid.

| Folder | Owner and intended evidence |
| --- | --- |
| api | Selected contract owners; compiled reflection snapshots and API review acknowledgement |
| modules | Extensibility; independently compiled old consumers, loading/runtime policy |
| packages | Extensibility; old signed format/manifest acceptance or explicit rejection |
| protocols | Module/Worker owners; generation negotiation, old envelopes and fencing |
| data | Each store owner; synthetic populated prior schema upgrade/read/restore |
| web | HTTP/client owners; selected old DTO/status/header/Modern/Classic behavior |

Phase 14 contains current candidate API snapshots, not historical N-1 consumers/data. `docs/compatibility/contracts.json` is reviewed metadata. The inventory scanner is intentionally scoped to source declaration syntax used here: all public type declarations in contracts/core/domain plus version-named public production declarations. HTTP, payload, persistence and behavioral families are separate explicit entries. It is not a Roslyn parser or proof that every public implementation type is an SDK.

The API mechanism uses reflection in the existing .NET test project instead of adding PublicApiAnalyzers or a separate tool package: it can select only the candidate subset, inspect compiled signatures and run under existing CI. This is a current surface baseline, not a binary compatibility checker. Record-generated members are included; compiler upgrades may require a reviewed baseline change. Attributes beyond captured nullability, serialization behavior, runtime loading and old-client semantics still need separate evidence before stable publication.

Run `dotnet test tests/unit/Hispania.Runtime.Tests.csproj --filter FullyQualifiedName~VersioningFoundationTests` to check metadata, inventory and API snapshots. To capture a deliberately reviewed candidate signature change, set `HISPANIA_CAPTURE_API_BASELINE=1` for that test invocation, inspect the diff, update api/CHANGELOG.md with owner/impact and the SHA-256 of the UTF-8/LF snapshot, and rerun with that variable absent. Capture mode is forbidden when CI=true. Reflection captures declared public signatures, generic constraints, parameter names/defaults and nullability metadata; behavioral and serialized fixtures remain separate future gates. Run `python tools/contract-inventory.py` after source or metadata changes to regenerate the inventory/table; existing reviewed entries are preserved and new defaults require review.
