# ADR-0016: Persistent Library Catalog and Import Identity

Status: Accepted

## Context

The initial Library vertical slice discovers local MP4 files, probes them, maps supported files to a Media Domain graph, and replaces an in-memory catalogue. Consequently, the catalogue is not durable and every rescan/restart regenerates Domain IDs.

Media Domain has explicit internal identities and must remain independent of paths, external identifiers, SQL, EF Core, SQLite, PostgreSQL, migrations, and infrastructure. Persistence 0.1 must make the catalogue durable without making file paths canonical media identity or coupling Playback to a database provider.

## Decision

### Boundaries and provider strategy

- Media Domain remains persistence-ignorant. It references no EF Core, SQL, database provider, `DbContext`, migrations, repositories, ASP.NET, or infrastructure assembly. No repository abstraction is introduced inside `Hispania.Media.Domain`.
- EF Core is confined to Infrastructure. EF persistence records are infrastructure records, not Media Domain objects. EF Core does not directly materialize or track `Work`, `Asset`, `Timeline`, or other Domain objects.
- Persistence rehydrates Domain objects explicitly through constructors, factories, and Domain mutation methods. It must not bypass Domain invariants through direct field mutation, reflection, proxies, or persistence-specific shortcuts.
- SQLite is the initial provider. PostgreSQL may be added later without changing Media Domain, Playback contracts, Playback application, or the catalogue read boundary consumed by Playback.
- Provider-neutral EF persistence behavior and provider-specific implementations are separated from the start:

  ```text
  H. Library Persistence EFCore
     - relational persistence records and shared mappings
     - rehydrators and scan transaction behavior

  H. Library Persistence Sqlite
     - SQLite provider configuration and DbContext activation
     - SQLite migrations

  Future: H. Library Persistence PostgreSql
     - Npgsql provider configuration
     - PostgreSQL migrations
  ```

  This separation is preferred over one combined EFCore/SQLite project because migrations are provider-owned and PostgreSQL is an explicitly planned substitution.

### Scalar representation

Persistence 0.1 preserves these logical representations independently of a provider's physical storage choice:

- Domain UUIDs use a stable, lossless, reversible representation.
- `DateTimeOffset` values preserve an unambiguous instant and their required offset semantics.
- Durations and coverage intervals use exact integral representations, never floating point.
- Decimal values preserve decimal semantics and are not automatically degraded to `double`.
- Enums and domain codes persist explicit stable values; persistence must not depend on accidental enum ordinals where that would make evolution unsafe.

The exact SQLite physical choices, such as `BLOB` versus `TEXT` for UUIDs, are schema-design decisions. They must remain consistent within a provider, preserve this logical model, and be covered by round-trip tests.

### Catalogue contracts and scan commits

- `IMediaCatalogReader` remains the existing Playback read boundary and retains its current synchronous contract in Persistence 0.1. Playback does not receive write, reconciliation, migration, provider, or database concerns.
- A persistence implementation may maintain a suitable read projection or snapshot behind that contract.
- A possible asynchronous reader evolution is deferred until implementation demonstrates that it is necessary; it is not part of Persistence 0.1.
- Library gains a separate asynchronous persistence boundary, conceptually named `ILibraryCatalogStore`. The term *store* is preferred to a generic repository because it reconciles catalogue snapshots and import identities rather than representing one Domain aggregate.
- The store resolves import identities before mapping and commits a completed scan atomically. It exposes no EF entities, SQL, sessions, or provider-specific types.

Illustrative conceptual operations are:

```text
IMediaCatalogReader
  GetAll()
  GetById(MediaEntityId)

ILibraryCatalogStore
  ResolveImportIdentitiesAsync(import keys, cancellationToken)
  CommitCompletedScanAsync(completed scan snapshot, cancellationToken)
```

A resolved import identity may include the import key, current catalogue state, persisted reusable Domain IDs, and the last observed source snapshot. A completed scan contains its scope, all observed import keys, successfully mapped entries, and observed-but-unmapped outcomes.

A completed scan is distinct from incremental upserts. It reaches normal discovery completion for its configured roots and reconciles the full observed import-key set in one transaction. Incremental upserts cannot safely establish which previous entries disappeared.

- A cancelled, failed, or transaction-aborted scan does not replace or destroy the last valid catalogue.
- A completed scan upserts successfully mapped observed entries.
- A previously valid entry remains available when its import key was observed but that scan's probe or mapping failed.
- An entry becomes disappeared only when its import key is absent from a completed scan's observed key set.

### Import identity and catalogue status

Import identity is separate from Domain identity. Persistence 0.1 uses this import key:

```text
(rootKey, normalizedRelativePath)
```

It identifies a tracked local import slot, not a Work, MediaEntity, Asset, MediaSource, or semantic work.

- Domain IDs are never derived from paths, import keys, filenames, source locators, or external IDs.
- An unchanged rescan resolving to a persisted import reuses the persisted Domain ID bundle.
- Byte length and last-modified time are observed source snapshot facts. They are not Domain identity.
- Renames and moves are not automatically reconciled in Persistence 0.1.
- Hashing, content identity, and the meaning of future hashes are out of scope.
- The mapper must accept resolved/reusable identities instead of unconditionally generating every ID.

Persistence 0.1 distinguishes these minimal catalogue/import statuses:

| Status | Meaning |
|---|---|
| `Active` | The import was observed and mapped successfully. |
| `Disappeared` | Its import key was absent from a completed scan. |
| `Unsupported` | The import was observed but its format or required capability is unsupported. |
| `ProbeFailed` | The import was observed but probing or mapping could not complete correctly. |

`Disappeared` is never used for probe or mapping errors. No additional status lifecycle is selected here. A probe/mapping error for a previously valid observed key preserves that entry's last valid catalogue representation for the current scan.

Disappeared imports are retained as catalogue history rather than physically deleted in Persistence 0.1. Their retention period remains undecided.

### Schema, transactions, migrations, and rehydration

The first schema must represent the entire frozen Media 0.2 graph, although the initial scanner writes only the playable movie slice:

- Work, MediaEntity, Timeline;
- Asset, roles, TechnicalRepresentation, technical profiles;
- MediaSource, locators, and representation/source bindings;
- AssetCoverage, intervals, and alignment;
- Release, ReleaseGroup, Collection, and memberships;
- Creator and contributions;
- ExternalIdentifierAssociation, lifecycle entries, and association relations;
- MetadataAssertion, typed value, provenance, and supersession;
- ArtworkReference and enrichment targets;
- library import records, import keys, observed source snapshots, and catalogue status.

The initial write workflow need only populate the Library slice. Its schema and rehydration design must not preclude later persistence of Media 2D and 2E.

- One database transaction commits one completed scan. Cancellation or error before commit rolls back.
- SQLite's single-writer characteristics are respected by serializing catalogue writes through an appropriate write transaction.
- Implementations may use internal staging records for large scans, provided the externally visible operation remains an atomic completed-scan commit.
- Migrations belong to Infrastructure/provider projects. SQLite and future PostgreSQL maintain provider-specific migrations for the same logical schema evolution; SQLite migration SQL is not reused as PostgreSQL migration SQL.
- The composition root runs migrations before the initial scan. Migration failure prevents scanner startup and blocks readiness.
- Rehydration loads a consistent snapshot, reconstructs roots/value objects and relationships in dependency order, and validates local invariants through Domain APIs.
- Application/Persistence owns invariants spanning records or aggregate boundaries, including uniqueness of active confirmed external-identifier associations. Media Domain holds no global state for such constraints.

## Alternatives Considered

### One combined EFCore and SQLite project

Rejected. It is faster initially but obscures provider ownership and makes the planned PostgreSQL migration/provider separation harder.

### EF annotations, mappings, or direct tracking on Domain objects

Rejected. This contaminates Media Domain and encourages persistence-driven mutation that bypasses protected Domain collections and validation.

### Keep the current `IMediaCatalog` writer as the persistence boundary

Rejected. Its synchronous `Upsert`/`Replace` operations describe the in-memory implementation, not durable reconciliation, cancellation, atomic commits, and import identity resolution.

### Name the new abstraction `ILibraryRepository`

Considered. Not selected because the persistence unit is a reconciled catalogue snapshot and imported graph, not a conventional single Domain aggregate. `ILibraryCatalogStore` is more precise.

### Convert `IMediaCatalogReader` to async now

Rejected for Persistence 0.1. Playback already depends on the current reader contract, and no implementation evidence yet requires changing it.

### Treat paths, size, or mtime as Domain identity

Rejected. Paths and source facts are not semantic media identity, and size/mtime are insufficient evidence of continuity.

### Automatically detect renames or content continuity through hashes

Rejected for now. It requires a separate decision on hashing cost, semantics, collision/privacy concerns, and reconciliation evidence.

### Physically delete disappeared imports

Rejected. Retaining them preserves import history and permits cautious future reconciliation.

## Consequences

Positive:

- Catalogue IDs survive restart and unchanged rescans.
- Playback remains independent of persistence technology.
- SQLite can be complemented by PostgreSQL behind Infrastructure.
- Migration failure cannot leave the application ready against an invalid schema.
- The full Media 0.2 graph has a durable relational representation path.
- Domain invariants remain authoritative.

Costs:

- Mapping and rehydration are explicit.
- The first schema is broader than the initial playable slice.
- SQLite write concurrency remains limited.
- Provider migrations require parity maintenance and provider-specific integration tests.
- Rename/move continuity and content identity remain deliberately unavailable.

## Open Questions

The following do not block Persistence 0.1:

1. Retention time for `Disappeared` imports.
2. Operational backup and restore policy.
3. Evidence required to preserve identity when a file changes at the same import key.
4. Hashing/content identity.
5. Rename/move reconciliation.
6. The future exact meaning of hashes.

The physical schema mapping for UUIDs, timestamps, durations, decimals, and explicit enum/code values remains a provider/schema design task, constrained by the scalar representation decision above.

## Related ADRs

- ADR-0003: Contract-first modularity.
- ADR-0007: Orthogonal media identity graph.
- ADR-0008: Asset coverage and temporal compatibility.
- ADR-0009: Semantic assets, representations, and source access.
- ADR-0011: External identification, metadata provenance, and artwork.
- ADR-0012: Internal media identity strategy.
- ADR-0014: Media source locator boundary.
- ADR-0015: Direct Play planning and source content access boundary.
