# ADR-0010: Publication and Organisation Contexts

Status: Accepted

## Context

Releases, publication families, albums, compilations, box sets, seasons, and alternate series orders have distinct semantics. A universal Edition either overloads publication vocabulary or duplicates recordings and episodes.

## Decision

When accepted:

- A `Release` is a commercial, editorial, or publication context that contains or publishes MediaEntities.
- A `ReleaseGroup` is an optional Aggregate Root with a HISPANIA ID that groups related Releases in a publication family.
- A `Collection` orders or groups MediaEntities without owning them exclusively.
- Release and Collection membership are non-exclusive.
- A Season is an organisational Collection when appropriate; it is not mandatory for all series.

An original album, unchanged remaster, or unchanged digital re-release can belong to the same ReleaseGroup. A substantially expanded deluxe edition, compilation, or box set can be a related but distinct ReleaseGroup. Exact membership criteria remain future editorial policy.

## Alternatives Rejected

- Use Edition as a universal container.
- Use Collection to mean both publication family and ordered media grouping.
- Duplicate recordings or episodes for each Release or ordering.

## Consequences

- A Recording can appear in multiple albums and compilations.
- A MediaEntity can appear in multiple Releases.
- Broadcast, DVD, chronological, and special-inclusive orders reuse the same episodes.
- ReleaseGroup criteria remain explicit rather than inferred from a provider.

## Previous ADRs

Constrains publication terms implicit in ADR-0005 but does not alter its status.
