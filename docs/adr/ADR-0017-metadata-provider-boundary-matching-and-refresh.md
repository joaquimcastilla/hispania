# ADR-0017: Metadata Provider Boundary, Matching, and Refresh

Status: Accepted

## Context

Media 2E models external identifiers, metadata assertions, provenance, and artwork references without coupling Media Domain to providers, HTTP, UI, or persistence. Persistence 0.1 persists only the playable Library slice; its Media 2E schema is preparatory and does not provide the required write and rehydration workflows.

The first movie metadata provider must not make its API, DTOs, field vocabulary, matching behaviour, or refresh mechanism an accidental global architecture.

## Decision

Metadata enrichment is a separate Application workflow:

```text
Library / Application search signals
-> metadata provider
-> provider-specific DTO
-> normalized movie metadata document
-> matching decision
-> Media 2E mapping
-> metadata persistence
```

A provider never writes Media Domain objects or persistence records directly. Application owns matching decisions, Domain-object construction, idempotency, and persistence orchestration.

## Provider Boundary

- HTTP, authentication, rate limits, response parsing, and provider-specific DTOs remain inside the provider adapter or module.
- Provider-specific DTOs do not enter Application or shared contracts.
- Media Domain never references TMDB, IMDb, MusicBrainz, HTTP, or provider DTOs.
- Application receives only normalized metadata contracts.
- Persistence stores mapped Domain records and operational refresh state; it does not decide matching.
- Providers do not write directly to Media Domain or Persistence.

TMDB may be a future movie adapter, but neither its API nor its terminology is a HISPANIA-wide contract.

## Provider Contracts

The first vertical uses a movie-specific contract equivalent to `IMovieMetadataProvider`. It supports:

- a stable provider descriptor;
- provider identity and supported identifier namespaces;
- movie search from normalized signals;
- retrieval of a normalized movie document by external identifier;
- typed errors, including unavailable provider, timeout, malformed response, unsupported operation, and cancellation;
- mapping and normalization version information.

A minimal common descriptor or capability contract is permitted only for module discovery, diagnostics, and capability inspection. It must not become a universal data-operation interface. HISPANIA does not adopt a generic `IMetadataProvider` that claims a universal search or document shape across movies, music, releases, people, and artwork.

The normalized contract layer contains provider-neutral values for external identifier references, movie search requests, movie candidates, normalized movie documents, normalized fields and values, normalized artwork references, typed results/errors, and mapping version. These are Application/provider-boundary contracts, not Domain entities.

## Search and Matching

A `MovieSearchRequest` contains only search signals, including a filename-derived title candidate, optional extracted year, optional observed duration, and other justified non-identifying observations. It excludes paths as semantic identity, `WorkId` as an external query term, nonexistent hashes, and any assertion that a local file is the same thing as a provider record.

A normalized search candidate contains an external identifier, title, optional original title, optional release date or year, optional provider score, optional provider-specific confidence, and available explanation data. Provider score and confidence are evidence only; they never confirm a match by themselves.

Search results are ephemeral by default. Application does not persist every search result as a candidate association. A `Candidate` `ExternalIdentifierAssociation` is created only when Application explicitly selects a result for tracking, review, or confirmation. A future policy may elect to persist search candidates, but that is outside Metadata Providers 0.1.

The default lifecycle is therefore:

```text
selected search result -> Candidate ExternalIdentifierAssociation
```

Transition to `Confirmed` requires an explicit Application decision or a future separately accepted deterministic matching policy. Filename/title similarity alone never auto-confirms an association.

## Target Semantics

For the movie vertical, an external movie record maps by default to `Work`.

It does not map to `MediaSource`, `Asset`, or a local file because those represent availability, semantic components, or technical access rather than the creative work represented by an external movie catalogue record. It does not map by default to `MediaEntity`: one external movie record normally identifies the film concept, while HISPANIA may represent several meaningful realisations, cuts, or temporal variants. Mapping to a `MediaEntity` requires separate evidence and a future explicit policy.

## Normalized Movie Document

A normalized movie document contains:

- a primary external identifier;
- aliases and secondary identifiers;
- normalized metadata fields with typed values;
- normalized artwork references;
- provider observation timestamp;
- mapping version.

It contains no provider DTO types, endpoints, API-specific response shapes, or provider-specific field names. Provider mapping translates provider fields and artwork classifications into HISPANIA-governed codes before Application consumes the document.

## Mapping to Media 2E

- `ExternalIdentifierAssociation` maps `provider / namespace / value` to `EnrichmentTarget.For(workId)` and begins as `Candidate` when Application selects it.
- Lifecycle entries retain explicit evidence, timestamp, and reason where applicable.
- Association relations represent migration, successor, or alias only when authoritative provider evidence explicitly supplies that relationship; they are never inferred from similar strings.
- `MetadataAssertion` is created once per normalized field. Initial mappings may include `title`, `original-title`, `description`, and `release-date`; this ADR does not close the `MetadataField` vocabulary.
- `ArtworkReference` maps normalized poster, backdrop, and logo classifications to HISPANIA artwork roles. It stores references only and does not download bytes.

Provider mapping must translate provider vocabulary through a HISPANIA-controlled mapping profile. Providers cannot inject arbitrary `MetadataField` or `ArtworkRole` codes into the core.

## Provenance

Each lifecycle entry, association relation, metadata assertion, and artwork reference retains independent provenance:

- `ExternalProvider` origin;
- provider identity;
- related external identifier;
- observation instant;
- confidence only when the provider supplies a meaningful confidence.

Provenance is never inherited implicitly from a Work, MediaEntity, or another assertion.

## Refresh, Staleness, and Fingerprints

Metadata refresh is separate from library scanning. A completed scan may create or enqueue an enrichment need, but scans do not remotely refresh every movie on every execution.

Persistence maintains durable operational refresh state outside Media Domain, at minimum for target, provider, external identifier when known, last attempt, last success, mapping version, normalized payload fingerprint, and result/status.

`stale` is operational state, not a `MetadataAssertion` status. It means that enrichment has no successful observation, no longer satisfies freshness policy, depends on an invalidated identifier, or was produced by an obsolete mapping version. It does not mean that a stored assertion is automatically false.

The normalized payload fingerprint is exclusively an operational idempotency and refresh key. It is not Work identity, MediaEntity identity, file identity, content identity, or a multimedia hash. A changed fingerprint means that the normalized document changed under a particular mapping version; it does not establish that the movie's semantic identity changed.

## Idempotency and History

The logical identity of an enrichment application is:

```text
target + provider + external identifier + mapping version + normalized payload fingerprint
```

For the same logical payload, refresh does not create new Domain IDs, duplicate assertions, or duplicate artwork references; it updates only operational refresh state as needed. For a changed payload, Application writes the meaningful delta and retains history. It may supersede an earlier assertion only where the field has explicitly singular semantics and both assertions refer to the same source record. Values from multiple providers coexist and are never destructively overwritten.

The fingerprint algorithm is deferred. It must be deterministic for semantically equal normalized documents and versioned with normalization and mapping behaviour.

## Conflicts

Effective metadata is not implemented by this decision.

- Assertions from multiple providers, local processes, and manual input coexist.
- Conflicts are preserved rather than hidden.
- No provider is globally preferred.
- No global ranking is introduced.
- No destructive replacement occurs.

## Artwork Locators

Provider mapping should prefer a stable provider resource key or stable locator and avoid signed, expiring URLs when a durable provider reference exists. Metadata Providers 0.1 stores `ArtworkReference` only. Downloading, caching, proxying, byte verification, and conversion into `ArtworkAsset -> TechnicalRepresentation -> MediaSource` are out of scope.

## Persistence Requirements

Metadata Providers 0.1 requires a bounded Persistence 0.2 slice:

- round-trip `ExternalIdentifierAssociation`;
- lifecycle entries and association relations, including evidence and reasons;
- `MetadataAssertion`, all `MetadataValue` variants, provenance, and supersession;
- `ArtworkReference` with provenance;
- durable operational refresh state;
- atomic idempotent writes and rollback;
- uniqueness enforcement for active confirmed identifier associations.

This requires a new migration and real persistence records, mappings, writers, and rehydrators. It does not require full persistence of Release, ReleaseGroup, Collection, Creator, or Contribution before the movie/Work vertical uses those targets. Metadata persistence uses a write boundary separate from `ILibraryCatalogStore`, which remains responsible for import reconciliation and the playable catalogue.

## Concurrency

Refresh serialization is a logical guarantee scoped to:

```text
target + provider + external identifier
```

ADR-0017 does not decide a technical mechanism for that guarantee. The first implementation may use suitable local transactions and constraints, process-local serialization, or job serialization without making any of those mechanisms global architecture. Distributed locking is not designed here.

Library scan, metadata refresh, and manual confirmation are separate operations. Persistence must apply idempotency checks and resulting mutations atomically. Manual confirmation rechecks current association state in its transaction, and confirmed-association uniqueness is enforced by Persistence. A refresh completing after manual confirmation may add evidence or assertions, but must not reverse a manual lifecycle decision or silently confirm another candidate.

## API and UI

Effective title, poster selection, and presentation policy are out of scope. A future raw API may expose associations, assertions, artwork references, and provenance for inspection, but it is not a requirement for the first provider adapter. This ADR introduces no UI work.

## Consequences

Positive:

- Provider implementations remain replaceable.
- Provider DTO churn is isolated.
- Matching is cautious and auditable.
- Metadata preserves source-level provenance, history, and conflicts.
- Persistence can advance as a bounded Media 2E slice.
- Refresh avoids unnecessary remote work and duplicate records.

Costs:

- Explicit normalized contracts and mappings are required.
- Application owns orchestration and matching decisions.
- Metadata remains unresolved or incomplete until future policy work.
- Changed local imports may need re-identification; metadata is never transferred merely because a path or filename looks similar.

## Alternatives Rejected

### Universal `IMetadataProvider`

Rejected because movies, music, releases, people, and artwork do not share one meaningful search or document model.

### Provider writes directly to Domain or Persistence

Rejected because it bypasses matching, vocabulary governance, idempotency, and transaction ownership.

### Filename auto-confirmation

Rejected because filenames are weak, non-semantic evidence.

### Persist every search result as a candidate

Rejected because provider search results are ephemeral by default and repeated searches must not create unbounded candidate-association history.

### Destructive overwrite

Rejected because it loses provenance, conflicts, and correction history.

### Refresh on every scan

Rejected because scanning is local discovery, not remote synchronization scheduling.

### Provider DTO leakage into Application

Rejected because it makes a provider API HISPANIA's accidental metadata contract.

### Provider-specific Domain fields

Rejected by ADR-0011 and this ADR because they couple stable media identity to a provider.

## Open Questions

1. Initial versioned HISPANIA movie-field and artwork-role mapping profile.
2. Freshness policy defaults and refresh scheduling mechanism.
3. Exact payload fingerprint canonicalization.
4. Manual confirmation API and authorization model.
5. Provider retry, backoff, quota, and circuit-breaker policy.
6. Automatic matching policy, if ever introduced.
7. Effective metadata resolution policy.
8. Artwork download, cache, and proxy lifecycle.

## Related ADRs

- ADR-0003: Contract-first modularity.
- ADR-0011: External identification, metadata provenance, and artwork.
- ADR-0016: Persistent library catalog and import identity.

## Further ADRs

No additional ADR is required before the first provider adapter or the bounded Metadata Persistence Slice, provided they conform to this decision.

A further ADR is required before automatic matching or auto-confirmation, effective metadata resolution or provider priority, artwork byte download/caching/proxying, distributed refresh execution, or provider credential/security architecture beyond existing configuration policy.
