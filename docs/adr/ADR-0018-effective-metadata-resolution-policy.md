# ADR-0018: Effective Metadata Resolution Policy

Status: Accepted

## Context

ADR-0011 preserves metadata as non-destructive `MetadataAssertion` records and permits an effective value only as a derived projection or cache. ADR-0017 deliberately left effective title, artwork selection, presentation policy, provider priority, and effective metadata resolution out of Metadata Providers 0.1.

Application, API, and UI consumers nevertheless need a deterministic, inspectable projection of a small initial field and artwork vocabulary. That projection must not replace assertions, provenance, association lifecycle, provider data, or history. It must be reproducible from persisted assertions and artwork references, without filename-derived inference.

## Decision

Effective Metadata 0.1 is a calculated-on-demand Application projection governed by the versioned policy `effective-metadata-v0.1`.

```text
persisted MetadataAssertions + persisted ArtworkReferences
    -> effective-metadata-v0.1
    -> EffectiveMetadataProjection
```

The projection does not create, mutate, supersede, retract, or delete `MetadataAssertion`, `ArtworkReference`, provenance, identifier lifecycle, provider data, or history. Those persisted records remain the source of truth. A projection can always be recalculated from them.

### Resolution boundary

`IEffectiveMetadataResolver` is a pure resolver. It:

- receives already loaded assertions and artwork references;
- performs no I/O;
- does not access SQLite, EF, persistence implementations, or providers;
- does not refresh metadata; and
- returns a deterministic projection.

`IEffectiveMetadataProjectionReader` belongs to Application orchestration:

```text
Persistence read -> resolver -> projection
```

The read boundary must support querying enrichment by `EnrichmentTarget` and return the assertions and artwork references needed for resolution. It must not establish `LoadAsync()` of the complete catalogue as the permanent query pattern, and it remains separate from `ILibraryCatalogStore`.

### Eligible records

For a metadata field, an assertion is eligible only when it has:

- the requested `EnrichmentTarget`;
- the requested field code;
- `MetadataAssertionStatus.Active`;
- the value kind required by the field; and
- valid provenance.

`Superseded`, `Retracted`, and `Invalid` assertions are excluded. `stale` does not exclude an assertion: it is refresh operational state, not an assertion status.

For artwork, an `ArtworkReference` is eligible only when it has the requested target, exact artwork role, valid locator, and valid provenance.

An invalidated external identifier association does not automatically make a related assertion or artwork reference ineligible in 0.1. A record stops being eligible through its own status where a status exists. The interaction between identifier lifecycle and effective metadata remains an open question.

### Precedence and deterministic tie-break

The editorial resolution precedence is:

```text
Manual > LocalProcess > ExternalProvider
```

This is an editorial resolution rule for explicit correction and deterministic behaviour. It is not an assertion that manually entered data is objectively more truthful.

Within the same origin-precedence level, the resolver applies this canonical order:

```text
origin rank DESC
ObservedAt DESC
provider code ASC
related external identifier ASC
stable assertion/artwork ID ASC
```

`ObservedAt` is an observation instant, not a truth or publication measure: a newer observation does not imply a more truthful value. It is only the initial deterministic tie-break within an origin-precedence level. In particular, a newer external observation does not represent a provider ranking.

Provider code, related external identifier, and stable record ID only resolve exact ties canonically. They do not express a provider preference. Effective Metadata 0.1 has no provider ranking, no global preferred provider, and no user-configurable provider priority.

`Confidence` does not participate in ranking. It is preserved in the winner's provenance, but is not compared between providers or origins.

### Effective field vocabulary

Effective Metadata 0.1 resolves only these singular fields, each independently:

| Field | Required value kind |
| --- | --- |
| `title` | `Text` |
| `original-title` | `Text` |
| `description` | `Text` |
| `release-date` | `Date` |

A value-kind mismatch makes an assertion ineligible; the resolver performs no implicit conversion. It does not combine descriptions or values from multiple assertions.

No fallback or inference belongs to Effective Metadata. In particular, it does not derive a missing title from a filename, copy `title` into `original-title`, or infer one field from another. A future UI or other presentation policy may provide explicitly labelled fallbacks outside this projection.

### Artwork

Effective Metadata 0.1 resolves `poster`, `backdrop`, and `logo` independently by exact target and role, using the same precedence and tie-break policy. Each role may therefore select a reference from a different origin or provider.

It introduces no artwork packs, availability checks, download, HTTP retrieval, proxy, byte cache, image transformation, or image verification.

### Projection result and provenance

For each field, the minimal result is:

```text
Value?
WinnerAssertionId?
SourceProvenance?
PolicyVersion
ResolutionReason
```

For each artwork role, it is:

```text
Locator?
WinnerArtworkReferenceId?
SourceProvenance?
PolicyVersion
ResolutionReason
```

`EligibleCandidateCount` may be included when inexpensive. The result retains the provenance of the selected source record; it does not create a synthetic `MetadataAssertion` or synthetic provenance. `PolicyVersion` and `ResolutionReason` are explicit derived resolution data, not source provenance.

When no eligible field assertion exists, the result has `Value = null` and `ResolutionReason = NoEligibleAssertion`. When no eligible artwork reference exists, it has `Locator = null` and `ResolutionReason = NoEligibleArtworkReference`. No value is invented.

Raw inspection of all assertions, artwork references, and their provenance remains a separate concern. Effective Metadata 0.1 does not introduce a complex per-candidate exclusion diagnostic structure.

### Persistence and caching

Effective Metadata 0.1 is calculated on demand. It introduces no new table, migration, persisted projection, materialized winner, or cache.

A future cache may be added only as a discardable, derived projection, versioned by resolution policy and invalidated by relevant source changes. Cache infrastructure and invalidation mechanics are not designed by this ADR.

## Consequences

Positive:

- Consumers receive a minimal deterministic metadata projection without losing source facts or history.
- Explicit manual correction works without destructive overwrite.
- Provider priority, filename inference, and implicit conversion do not become accidental architecture.
- The resolver is testable without persistence, providers, network access, or time-dependent behaviour.

Costs:

- Resolution can select a newer observation that is not semantically more accurate.
- Missing locale, region, release-date-type, and source-quality semantics intentionally limit this first policy.
- Application needs a targeted enrichment read boundary before an efficient projection reader is implemented.

## Open Questions

1. Language, locale, region, and release-date-type selection policy.
2. Semantics of confidence per provider and field, including whether any future values are comparable.
3. Manual-correction authorization, authorship, and scope.
4. Whether identifier lifecycle should ever affect assertion eligibility automatically.
5. Future policy profiles, including any provider priority, and their authorization/configuration model.
6. Targeted persistence query shape, projection scale, and whether a cache becomes justified.
7. Raw inspection API and diagnostic requirements.
8. Artwork retrieval, availability, caching, proxying, and byte lifecycle.

## Related ADRs

- ADR-0003: Contract-first modularity.
- ADR-0011: External Identification, Metadata Provenance, and Artwork.
- ADR-0017: Metadata Provider Boundary, Matching, and Refresh.
