# Media Domain Design

## Status and scope

This is the conceptual design for Phase 2 — Domain and Media Model 0.2. It contains no C# types, persistence schema, public API, scanner behaviour, playback policy, or implementation decision.

ADRs 0007–0014 formalise the accepted architectural decisions of Media 0.2. ADR-0007 supersedes ADR-0005. This remains a conceptual design: no production implementation is specified here.

The model represents media independently of files, containers, paths, codecs, releases, providers, and current availability. It separates creative identity, reproducible realisation, temporal compatibility, semantic components, technical renditions, physical access, external identification, and metadata provenance.

Media 0.2 has two active scopes only: **Media Core** and **Metadata / Identification**. It is designed to function as a fully local multimedia server with ordinary local files, removable disks, offline NAS devices, and conventional remote access. Preservation, distributed media, P2P, global catalogues, global certification, global moderation, global asset registries, network normalisation, peer discovery, and distributed manifests are outside this design.

## Rejected Model: Mandatory Hierarchical Media Chain

Media 0.2 rejects the following mandatory hierarchy; ADR-0005 is superseded by ADR-0007:

```text
Work → Edition → MediaEntity → Timeline → Asset → TechnicalRepresentation → MediaSource
```

It conflates independent axes and creates artificial ownership. A recording can occur in many releases; a release can contain several entities; the same asset can cover only parts of several timelines; and a source container can expose representations of several assets. Treating those as parent-child levels either duplicates identity or makes ordinary variants impossible to state naturally.

`Edition` is especially ambiguous: it has been used to mean a cut, a commercial release, a remaster, and an encode. Those are not synonymous.

## Conceptual graph

```text
Work
 └── realizes/expresses ──► MediaEntity
                              │
                              └── has temporal reference ──► Timeline

Timeline ◄──── AssetCoverage ────► Asset
                                      │
                                      └── has rendition ────► TechnicalRepresentation
                                                                    │
                                                                    └── available through ──► MediaSource

Release ───────── contains/publishes ─────► MediaEntity
Collection ───── orders/groups ───────────► MediaEntity
ReleaseGroup ─── groups publication variants ► Release
Creator ───────── contributes (role) ──────► Work / MediaEntity / Release

ExternalIdentifierAssociation (0..N) ── attaches a Provider/Namespace/Value tuple ──► an appropriate HISPANIA entity
MetadataAssertion / ArtworkReference ── has provenance ──► an appropriate HISPANIA entity
```

Arrows are relationships, not aggregate ownership or mandatory cardinalities. A known Work can have no MediaEntity or Source. An Asset can have no known representation. A TechnicalRepresentation can be known even when no source is presently accessible. External identifiers, metadata assertions, and artwork references are cross-cutting associations; they do not alter internal identity.

## Identity concepts

### Work

A **Work** is an independent creative or conceptual identity: for example a film, composition, series, episode concept, documentary, programme, or broadcast concept. It is not a file, stream, encode, release, provider record, or availability claim.

Work remains valid for a known but unrecoverable film, a composition with many recordings, a series with or without seasons, and an emission that is documented but has no obtainable recording. Categories need not share the same internal structure: a series can organise episode works, while a composition can have recordings and music videos without pretending that either is the other.

External identifiers are evidence or mappings only. They never replace a HISPANIA Work identity.

### Creator

A **Creator** is a person or organisation that can be related to a Work, MediaEntity, or Release through a credited role. `Artist` is a role in that relationship, not a narrower replacement for Creator: the same concept also covers a composer, performer, director, writer, broadcaster, label, studio, or publisher.

This distinction is necessary for music-provider mappings without forcing a music-only `Artist` type into video and other media. Creator identity is internal and independent of a provider's person, group, or artist identifier.

### MediaEntity

A **MediaEntity** is a concrete realisation of a Work that is reproducible, or potentially reproducible, as a meaningful media unit. Candidate categories include a movie realisation, episode realisation, audio recording, music video, and broadcast realisation. It is deliberately not a generic container for albums, seasons, or arbitrary files.

Two things are the same MediaEntity when they assert the same meaningful realisation and differ only in technical rendition or availability. They are different MediaEntities when the realised content or its meaningful editorial composition differs: for example, an audio recording and its music video, or a theatrical cut and a separately authored director's cut.

A MediaEntity may have several Timelines when they are known temporal variants of that same realisation and their relationship is explicit (for example, a PAL-speed variant or a broadcast variant with a mapped recap). If a temporal change represents a distinct meaningful edit, it is safer to use a distinct MediaEntity until an explicit editorial relation says otherwise. Equal title or duration is never sufficient evidence.

### Decision rule: MediaEntity, Timeline, Asset, or TechnicalRepresentation

Classify the change at the highest layer whose identity claim changes, then preserve lower layers only where evidence supports reuse:

1. Create a **new MediaEntity** when the meaningful realised content or editorial composition changes.
2. Create a **new Timeline** when the same realisation has a distinct temporal reference or an explicit temporal transformation, without evidence of a new meaningful realisation.
3. Create a **new Asset** when the semantic component itself changes: for example a new mix, dub, commentary, subtitle translation, selectable angle, or materially different main video component.
4. Create a **new TechnicalRepresentation** when the same Asset is rendered/encoded/transcoded from technically different material, including distinct codec, technical profile, or recorded derivation evidence, without changing its semantic component.

This is not a mechanical rule based on a filename, source label, codec, resolution, duration, or provider record. Uncertainty is recorded as uncertainty; it must not be resolved by silently duplicating or collapsing entities.

| Case | MediaEntity | Timeline | Asset | TechnicalRepresentation | Reason |
|---|---|---|---|---|---|
| A. Same film and cut, different encode | Same | Same | Same | New | Encoding is a technical rendition only. |
| B. Same film and cut, new 4K master | Same | Same | Same, absent evidence of semantic change | New, with distinct provenance | A new master affects derivation and rendition; it is not automatically a new edit or semantic component. |
| C. Same film and cut, PAL speed-up | Same | New, related by temporal transformation | Same where the component is semantically equivalent; coverage records the mapping | New if a distinct encoded rendition exists | The timing changes even if editorial content does not. |
| D. Director's Cut with new scenes | New | New | New for changed main-video and affected components; unchanged components may be reused only through explicit coverage | New for each technical rendition | Added scenes constitute a meaningful editorial realisation. |
| E. Same song, remaster with no mix change | Same AudioRecording | Same | Same | New, with remaster provenance | Remastering alone does not create a new recording. |
| F. Same song, different remix | New AudioRecording | New | New principal audio Asset | New | A remix is a distinct realised recording, while both express the same musical Work. |
| G. Music video with intro and added dialogue | New MusicVideo | New | New main-video Asset; audio can be reused only where exact coverage is explicit | New for its video and any new audio rendition | The audiovisual realisation and temporal composition differ from the recording. |

### Timeline

A **Timeline** is a temporal reference: ordered positions, duration when known, and the basis on which components can be aligned. It can describe an original cut, director's cut, PAL speed-up, recap, intro/outro variation, censorship, fragment, offset, or known temporal transformation.

Timeline relationships can state compatible, incompatible, unknown, or alignable-with-transformation. The transformation itself is not designed in this phase. The model must not infer compatibility from duration, titles, filenames, resolution, or a shared Work.

### Asset

An **Asset** is a semantic media component, independent of codec, container, source, and current availability. Every Asset has exactly one closed structural **AssetMediaKind**: `Video`, `Audio`, `Text`, or `Image`. Adding a new MediaKind requires explicit domain evolution; modules and external providers cannot introduce one arbitrarily.

An Asset has zero or more unique, compatible **AssetRoles** from a HISPANIA-controlled domain vocabulary. The initial vocabulary is `main`, `dub`, `commentary`, `audio-description`, `subtitle`, and `lyrics`. `main` is compatible with Video or Audio; `dub`, `commentary`, and `audio-description` with Audio; and `subtitle` and `lyrics` with Text. More than one compatible role can describe the same Asset without requiring duplicate Assets. Future HISPANIA versions may extend the role vocabulary, but providers cannot introduce roles arbitrarily and no dynamic role registry is defined in Media 0.2.

```text
Asset
├── MediaKind: exactly 1
└── Roles: 0..N
```

Track is therefore a role-specific category of Asset, not another universal level. Language or locale, codec, resolution, title, provider, and stream index help describe a track or its exposure but are not AssetRoles and do not identify it: two Spanish dubs can differ in mix, intended region, timing, or content role. Image Assets do not initially use editorial roles because artwork editorial function is represented by `ArtworkReference`. An Asset can exist without a known TechnicalRepresentation when a local catalogue contains semantic knowledge that has not yet been technically analysed.

### AssetCoverage

**AssetCoverage** is the explicit relationship between an Asset and a Timeline. It states how, if at all, that asset maps to the temporal reference. It can express complete or partial coverage, one or more intervals, gaps, offsets, known temporal transformations, and incompatibility.

For example, a partially lost Spanish dub can cover `00:00–12:34` and `41:10–45:36` of one Timeline while having gaps elsewhere. A subtitle may cover a whole movie except an added scene. Coverage describes compatibility; it neither turns the asset into a file nor assumes a synchronisation algorithm.

### TechnicalRepresentation

A **TechnicalRepresentation** is a technically defined rendition of one Asset in that Asset's MediaKind: encoded video, audio, text, or image. It is not a filename, a container, a stream index, or a physical location. Its technical profile must have the same MediaKind as its Asset. Codec, resolution, bitrate, and format describe a rendition but do not define semantic identity or quality.

Video representations can record width, height, codec, profile, level, frame rate, bit depth, chroma, colour/HDR information, bitrate, and related technical data. Audio representations can record codec, sample rate, bit depth, channels/layout, object-audio capability, bitrate, and lossless/lossy nature. Text representations can record format, encoding, and synchronisation capabilities.

`480p`, `720p`, `1080p`, `1440p`, `2160p`, and `4K` are derived, configurable labels; they are not identity.

### MediaSource

A **MediaSource** is a physical or network availability/access record that can expose one or more TechnicalRepresentations: for example a local file, NAS object, HTTP object, optical medium, or future conventional remote transport. Sources can become temporarily inaccessible or disappear from a local catalogue without destroying the identity of an Asset or a known representation. One representation can be accessible from several sources.

A MediaSource retains a **SourceLocator** consisting of a declared `Scheme` and `OpaqueValue`. Media preserves the locator but does not interpret its transport, open its source, authenticate, or resolve credentials. The locator never contains passwords, access tokens, API keys, cookies, SMB credentials, or equivalent secrets; those concerns belong to future layers outside Media Domain. A locator scheme begins with a letter, is normalised to lowercase, and may contain letters, digits, `+`, `-`, and `.`; `ArtworkLocator` uses the same grammar while remaining a distinct value object. Container format, source-local locator, stream index, stream selection metadata, and future byte ranges belong to the representation-to-source access description. They do not turn an MKV, MP4, path, or URL into an Asset.

## Container and stream boundary

For a source file such as the following, the file is a MediaSource, and the MKV container and stream indices are access/packaging facts associated with that source's exposed representations:

```text
movie.mkv (one MediaSource; container = Matroska)
├── stream 0: video HEVC 2160p HDR
│   └── TechnicalRepresentation of MainVideoAsset
├── stream 1: English TrueHD Atmos
│   └── TechnicalRepresentation of English original-audio Asset
├── stream 2: Spanish AC-3 5.1
│   └── TechnicalRepresentation of Spanish dub Asset
├── stream 3: English commentary
│   └── TechnicalRepresentation of English commentary Asset
├── stream 4: English subtitles
│   └── TechnicalRepresentation of English subtitle Asset
├── stream 5: Spanish subtitles
│   └── TechnicalRepresentation of Spanish subtitle Asset
└── chapters
    └── temporal navigation/annotation data related to a Timeline
```

A stream is a source-specific carriage of a representation, not automatically an Asset. The same stream content in an MP4, MKV, local file, NAS copy, or HTTP object can expose the same representation through different MediaSources. Conversely, different streams with the same language can represent different Assets.

## Release, cut, master, remaster, encoding, and organisation

### Release

A **Release** is a commercial, editorial, or publication context that contains or publishes MediaEntities. Examples include a Blu-ray release, Netflix publication, album release, deluxe album release, or compilation. It does not exclusively own its members: the same recording can appear in many releases and collections.

`Edition` is not used as a universal term. It remains ambiguous unless a later, narrower decision gives it a precise meaning.

### Cut/Edit, master, remaster, and encoding

A **Cut/Edit** deserves independent identity only when it identifies a distinct meaningful realisation: a director's cut, censorship edit, or separately authored edit is normally a different MediaEntity. A small mechanically mapped timing variant can instead be a distinct Timeline of the same MediaEntity. The exact boundary needs editorial evidence, not a filename convention.

**Master** and **remaster** are optional local derivation/provenance information, or distinct Assets only when independently managed as media objects. This phase introduces no universal Master entity because that would falsely require a known, canonical, or available master for all media. Preservation workflows are outside Media 0.2.

**Encoding** is always a TechnicalRepresentation concern. A 4K remaster and an AV1 encode must never become interchangeable merely because both might be called an “edition.”

### Collection and sequence

A single **Collection** concept with an ordered membership relationship is sufficient provisionally. The membership supplies position, ordering scheme, grouping role, and any future contextual labels. A separate `Sequence` root is not yet justified.

This permits an album to order recordings, a compilation to reuse recordings from other releases, and a series to offer broadcast, DVD, chronological, or special-inclusive orderings without duplicating Episodes or MediaEntities. A season is a collection/grouping when it exists; it is not mandatory for every series.

### ReleaseGroup

A **ReleaseGroup** is an optional **Aggregate Root with its own HISPANIA ID**. It is a publication-family context that groups related Releases without claiming to be a Work, a semantic recording, or an ordered Collection. It needs independent identity because it can have metadata, external identifiers, lifecycle, and relationships to several Releases even when no provider exposes it.

The decision is not MusicBrainz-specific. MusicBrainz Release Group is one external mapping to this concept, not its definition. A ReleaseGroup is appropriate when releases are variants of one recognisable publication programme; a Collection instead orders/groups MediaEntities, while a Release publishes them.

| Publication case | ReleaseGroup decision |
|---|---|
| Original album | One ReleaseGroup containing its initial Release. |
| Remaster with the same programme | A new Release in the existing ReleaseGroup, with remaster provenance. |
| Deluxe edition with substantial additional programme | A distinct ReleaseGroup, related editorially to the original group; it is not merely a packaging variant. |
| Japanese edition | Existing group if the programme is equivalent and variation is regional/packaging; distinct related group if bonus content creates a materially different programme. |
| Compilation | Its own ReleaseGroup and Release; it can contain recordings from many other groups. |
| Box set | Its own ReleaseGroup and Release; it can relate to constituent Releases or ReleaseGroups without absorbing their identities. |
| Digital re-release with unchanged programme | A new Release in the existing ReleaseGroup. |

The group does not exclusively own its Releases, and its membership criteria must remain explicit. This is a conceptual decision only; no persistence or aggregate implementation follows from it.

## External identification, metadata, and artwork

External providers are future, independent modules. They may identify and enrich HISPANIA entities, but they do not enter the core identity model and may be replaced without changing it. Identification, metadata, artwork, ratings, and other enrichment are independent capabilities: one provider may contribute any subset of them.

### ExternalIdentifier and its association

An **ExternalIdentifier** is a value object with at least:

```text
Provider
Namespace / EntityType
Value
```

Examples:

```text
tmdb / movie / 123
tmdb / collection / 456
musicbrainz / release / …
musicbrainz / release-group / …
musicbrainz / artist / …
```

An **ExternalIdentifierAssociation** is the stateful relationship from that value to a HISPANIA target. It has immutable identifier and target references, an append-only local lifecycle, and zero or more auditable relations to other associations. Each lifecycle entry records its state, decision timestamp, `MetadataProvenance` evidence, and an optional textual reason. This is aggregate-owned state, not domain-event or event-sourcing infrastructure. `rejected` and `invalidated` entries require that reason; it is free text rather than a global reason catalogue so that the local audit retains the specific decision without designing a reason-registry subsystem. An identifiable HISPANIA entity has `0..N` such associations and remains valid with none. Identifiers from the same provider may coexist when their namespaces or roles differ; `tmdb/movie/123` and `tmdb/collection/123`, for example, are not interchangeable. The model deliberately has no provider-specific properties such as `TmdbMovieId` or `MusicBrainzReleaseId`.

`candidate`, `confirmed`, `rejected`, and `invalidated` are local matching states of this server or of the module that supplied evidence. `Candidate → Confirmed`, `Candidate → Rejected`, and `Confirmed → Invalidated` are the only local transitions. Rejected and invalidated associations are terminal: later evidence creates a new association with a new HISPANIA ID rather than reopening the prior decision. These states are not network certification, global verification, or a claim about any distributed catalogue.

`Migration`, `Successor`, and `Alias` are evidenced relations owned by an association and directed to another association ID. Migration records an explicit namespace or identifier migration; Successor records that another association replaces this one; Alias records distinct tuples evidenced as the same external identity. Media Domain rejects self-relations and duplicate `(RelatedAssociationId, Kind)` pairs, but deliberately does not resolve the referenced association or validate it through a repository.

### Correct attachment and provider mappings

External identifiers attach to the HISPANIA concept that the provider identifier actually denotes, not automatically to Work:

| Provider identity | Normal HISPANIA attachment | Qualification |
|---|---|---|
| IMDb title | Work | A title is normally a conceptual film, series, or episode work; a provider module must preserve its title type and avoid asserting a particular cut from this ID alone. |
| IMDb name | Creator | The provider person/name may represent a person or organisation used in credited roles. |
| TMDB movie | Work | It normally identifies the movie concept, not a particular encode, source, or cut. Conflicting version evidence must remain explicit. |
| TMDB TV | Work | A series Work. |
| TMDB TV episode | Work | An episode Work; a concrete broadcast or edit is a MediaEntity only when supported by evidence. |
| TMDB collection / box set | Collection | A provider grouping of works, not a technical representation or release. |
| MusicBrainz Work | Work | Musical composition/concept. |
| MusicBrainz Recording | MediaEntity | Specifically an AudioRecording realisation, not the composition Work and not a Release. |
| MusicBrainz Release | Release | Publication/release context. |
| MusicBrainz Release Group | ReleaseGroup | Publication family that groups Releases; not an ordered Collection of recordings. |
| MusicBrainz Artist | Creator | `Artist` is a contribution role on the generic Creator concept. |
| TheAudioDB artist | Creator | Enrichment evidence for a person/group; it does not replace the internal Creator ID. |
| TheAudioDB album/release-like record | Release or ReleaseGroup, only after provider-semantic mapping | A module must declare the namespace meaning; it must not map by field name alone. |

`Series` and `Episode` are Work categories; `Recording` is an AudioRecording category of MediaEntity. This is why their external identifiers cannot all attach to the same level.

### Extensible code governance

`ContributionRole`, `MetadataField`, and `ArtworkRole` are extensible domain-code value objects. Media Domain validates their representation and normalisation only. HISPANIA mappings and modules govern which semantic codes HISPANIA admits and translate provider vocabularies into those codes. Providers do not directly inject a new semantic vocabulary into Media Domain; this does not define a dynamic registry or turn those concepts into enums.

### External-identifier eligibility

ExternalIdentifiers are appropriate on **Work**, **Creator**, **MediaEntity** when a provider identifies that realisation (notably an AudioRecording), **Release**, **ReleaseGroup**, and **Collection** when the provider actually identifies that grouping. `Series` and `Episode` receive them as Work categories; `Artist` receives them as a Creator role; `Recording` receives them as a MediaEntity category.

They are not default attributes of **Timeline**, **Asset**, **TechnicalRepresentation**, **MediaSource**, **AssetCoverage**, technical profiles, stream locators, or metadata assertions. A future provider that genuinely assigns a stable identifier to one of those concepts requires an explicit namespace mapping decision; a source URL, file hash, or stream index is not by itself an ExternalIdentifier.

When a provider supplies an identifier for a particular poster, cover, or image variant, it attaches to the **ArtworkReference** if it identifies that provider-specific reference, or to an **ArtworkAsset** only when it identifies the image asset itself. It must not be attached to the Work merely because the artwork depicts it.

### ExternalIdentifier conflict and migration invariants

1. The tuple `(Provider, Namespace, Value)` is immutable identifier evidence; correcting an association does not rewrite the tuple or an internal ID.
2. A tuple can have at most one **confirmed** active HISPANIA target within its declared namespace semantics. This is an invariant of the relevant set of associations, not of one association in isolation: a future Application/Persistence layer queries that set when confirming or resolving a conflict. Media Domain owns only local lifecycle, keeps no global static state, introduces no repository, and provides no global certification.
3. Several candidate associations for the same tuple may exist only while unverified; they block automatic merge and require evidence-based resolution.
4. Re-matching rejects or invalidates the incorrect association, retaining its provenance and reason. It does not mutate or merge the affected internal entities automatically.
5. Provider namespace changes create an explicit migration, alias, or successor association. Old and new namespaces are never silently treated as equal.
6. Provider ID aliases or replacements can relate several tuples to one internal entity when their equivalence is evidenced; aliases do not make different namespaces interchangeable.
7. Provider removal, identifier retirement, or an invalid response changes association status, not HISPANIA identity.
8. An identifier association must target the concept represented by its namespace; a provider module cannot attach a convenient but semantically mismatched ID merely to improve matching.

### Artwork model

An **ArtworkReference** is an identified, provenance-bearing relationship from a target entity to artwork in a declared role, such as poster, backdrop, cover, booklet image, artist portrait, or logo. It is not the image bytes and it is not a title/genre metadata assertion. Several ArtworkReferences can coexist for the same target and role without one silently deleting another.

Artwork descriptive metadata is separate from the bytes and may itself be asserted with provenance: role, language, credited creator, provider designation, dimensions, aspect ratio, crop, and ordering are examples. It is not sufficient to identify a particular byte sequence.

When image bytes are deliberately retained or discovered, the general media model can be reused without mixing identity domains:

```text
ArtworkReference ── refers to ──► ArtworkAsset (optional semantic image Asset)
ArtworkAsset ── has rendition ──► image TechnicalRepresentation
image TechnicalRepresentation ── available through ──► MediaSource
```

`MediaSource` is generic physical/network access and can therefore represent a downloaded local file or a remote image object. Reusing it does not confuse artwork with a movie or recording, because `ArtworkReference` supplies the editorial role and the image is an Asset with an image representation. Conversely, an external artwork URL need not create an ArtworkAsset, TechnicalRepresentation, or MediaSource until HISPANIA has a reason to manage or verify its bytes. This prevents retrieval/storage policy from becoming a prerequisite for metadata.

Resolution and size are representation properties or derived labels, not artwork identity. A 500px provider thumbnail and an original-size image may be representations of the same ArtworkAsset only when their semantic equivalence is evidenced; otherwise they remain separate candidate references/representations.

### MetadataAssertion and provenance

A **MetadataAssertion** is an identified, provenance-bearing relationship, rather than a mutable field on an entity. It needs at least an entity target, field/key, asserted value, origin, observation timestamp, status, and enough evidence/context to distinguish competing claims.

Its conceptual data includes:

| Element | Purpose |
|---|---|
| Field/key | States what is claimed without freezing the future metadata schema. |
| Value | Preserves the asserted value; any provider raw form or normalisation is provenance, not a destructive overwrite. |
| Origin/provenance | Identifies manual, provider, or local-process origin; provider namespace, query/version, and evidence may be recorded when relevant. |
| Timestamp | Distinguishes source assertion time, retrieval/observation time, and future validity time when known. |
| Confidence | Optional evidence/quality signal; it does not itself make a value canonical. |
| Manual/automatic mode | States how the assertion entered HISPANIA without treating manual data as a special entity type. |
| Status | Supports active, superseded, retracted, invalid, or unverified claims without erasure. |
| Replacement/supersession | Links an updated assertion to the assertion it replaces, preserving history. |

The provider namespace belongs to provenance where relevant; it is not an implicit domain field. Metadata assertions do not require an ExternalIdentifier and manual/local values are first-class.

### Metadata conflict stress tests

| Case | Conceptual outcome |
|---|---|
| 1. TMDB and IMDb `Finding Nemo`; manual `Buscando a Nemo` | Three title MetadataAssertions on the same target, each with its own origin and status. No assertion is erased; display policy may select a locale/context-appropriate effective title later. |
| 2. Two provider release dates | Two date assertions coexist with provenance, possibly different date type/region where known. A conflict is data, not a reason to overwrite. |
| 3. One provider says 2003, another 2004 | Two year/date assertions remain distinct until evidence or policy resolves their meaning. A year alone does not mutate Work identity. |
| 4. User corrects a genre manually | A manual assertion is added, optionally superseding a prior local effective choice but not deleting provider claims. |
| 5. Provider disappears | Its assertions and identifier associations retain recorded provenance and status; they can be marked unavailable/stale without identity loss. |
| 6. Primary metadata provider changes | Resolution policy changes its preference. Assertions, IDs, artwork references, and internal identities do not change. |
| 7. Provider assertion becomes obsolete | A new assertion supersedes the older one, whose status/history remain recorded. |
| 8. Artwork from several providers | Several ArtworkReferences and their descriptive assertions coexist. Selection is a future policy, not destructive replacement. |
| 9. Provider returns an incorrect ExternalIdentifier | The identifier association is rejected/invalidated with evidence; the tuple is not silently treated as canonical. |
| 10. Two HISPANIA entities temporarily share an ExternalIdentifier due to matching error | Both associations may exist only as unverified candidates. The tuple cannot have two confirmed targets; resolution rejects or invalidates the incorrect association without merging internal entities automatically. |

### Canonical/effective value

The recommended strategy is **assertions plus configurable resolution policy**. Assertions are the durable source of truth; a policy can select an effective value by field, entity category, locale, user preference, provider trust, freshness, or manual override. A materialised effective value may later be used strictly as a cache/projection, never as the sole canonical record.

This avoids both a rigid provider priority embedded in the domain and the cost/ambiguity of recalculating every view without policy. No merge algorithm or priority order is selected here.

Manual and unidentified media remain valid:

```text
Work
├── title: manual/local assertion
├── ExternalIdentifiers: []
└── no externally available source required
```

## Concept classification and identity

| Concept | Classification | Needs a HISPANIA ID? | Notes |
|---|---|---:|---|
| Work | Aggregate Root | Yes | Canonical creative/conceptual identity |
| Creator | Aggregate Root | Yes | Person or organisation; Artist is a contribution role |
| MediaEntity | Entity | Yes | Concrete meaningful realisation |
| Timeline | Entity | Yes | Temporal reference and relationship endpoint |
| Asset | Entity | Yes | Semantic component with one closed MediaKind and zero or more compatible roles; may exist without rendition |
| TechnicalRepresentation | Entity | Yes | Technically distinguishable rendition matching its Asset MediaKind |
| MediaSource | Entity | Yes | Mutable availability/access record with a non-secret opaque locator |
| Release | Aggregate Root | Yes | Publication context; non-exclusive membership |
| ReleaseGroup | Aggregate Root | Yes | Optional publication family grouping Releases |
| Collection | Aggregate Root | Yes | Grouping/order context; non-exclusive membership |
| AssetCoverage | Relationship | Yes | Requires identity for partial/multiple mappings and lifecycle evidence |
| Timeline compatibility/alignment | Relationship | Yes | Explicit evidence rather than heuristic inference |
| Collection membership/order | Relationship | Yes | Position and ordering-scheme context |
| Creator contribution/credit | Relationship | Yes | Role-specific relation to a Work, MediaEntity, or Release |
| Metadata assertion/provenance | Relationship | Yes | Preserves competing field-level claims and their origin |
| Artwork reference/provenance | Relationship | Yes | Independent of identification and metadata retrieval |
| ExternalIdentifier | Value Object | No | Provider, namespace/entity type, and provider value |
| ExternalIdentifierAssociation | Relationship | Yes | Stateful attachment with lifecycle evidence, invalidation, and migration/successor/alias relations |
| Technical profile | Value Object | No | Video, audio, or text properties |
| Source access/container/stream locator | Value Object | No | Source-specific packaging/access facts |
| Coverage interval, gap, offset | Value Object | No | Components of AssetCoverage |
| Resolution label | Derived Value | No | Configurable classification derived from technical data |

HISPANIA uses strongly typed internal IDs with a 128-bit UUID value. Newly generated IDs use UUID v7; existing IDs rehydrate unchanged. Persistence and wire representation remain undecided. See ADR-0012.

## Core invariants

1. A Work does not depend on files, sources, providers, encodes, or releases.
2. No ExternalIdentifier is the canonical identity of HISPANIA.
3. An entity can exist without ExternalIdentifiers.
4. External providers are substitutable modules, not core-domain dependencies.
5. Identifiers from different namespaces are not interchangeable, even for the same provider and value.
6. An ExternalIdentifier must attach to the HISPANIA concept denoted by its provider namespace.
7. Metadata can originate from multiple sources, including manual/local assertions.
8. Identification, metadata, and artwork are independent capabilities.
9. Changing or removing an external provider does not change an entity's internal identity.
10. An Asset can exist without a TechnicalRepresentation.
11. A TechnicalRepresentation can be known without a currently accessible MediaSource.
12. A MediaSource does not define semantic identity.
13. Resolution and codec do not define identity or quality.
14. Language does not uniquely identify an audio, subtitle, or lyric Asset.
15. The same Timeline does not imply the same Asset.
16. The same duration does not imply the same Timeline.
17. The same Work does not imply temporal compatibility.
18. Release membership is non-exclusive.
19. A Recording can belong to multiple Releases and Collections.
20. A container or stream is not automatically an Asset.
21. Every asserted Asset-to-Timeline compatibility is expressed by AssetCoverage; unrecorded compatibility remains unknown.
22. A physical source may disappear without deleting historical knowledge of its representation.
23. Every Asset has exactly one closed MediaKind; external providers do not define MediaKinds.
24. AssetRoles are unique within an Asset, compatible with its MediaKind, and are not provider-controlled metadata.
25. A SourceLocator is opaque to Media Domain and never contains secrets.

## Stress tests 22–28

| Case | Graph outcome | Boundary verified | Naturalness (1–5) |
|---|---|---|---:|
| 22. Multi-angle | One MediaEntity and Timeline; each angle is a distinct semantic video Asset with its own representations and coverage. A source container can expose all angles. | An angle is not merely a codec property; it is selectable semantic content. | 4 |
| 23. Branching content | One Work may have multiple MediaEntities or explicitly related Timelines for authored branches. Each branch has assets and coverage; shared assets can attach to common intervals. | A tree parent cannot represent shared segments without duplication; the graph can. | 4 |
| 24. Complex MKV | `movie.mkv` is one MediaSource. Each numbered stream is a source-specific carriage of a TechnicalRepresentation; each representation belongs to its corresponding semantic video/audio/subtitle Asset. Chapters annotate/navigate the Timeline. | File/container ≠ stream ≠ Asset ≠ representation ≠ source. | 5 |
| 25. Same stream in different containers | One TechnicalRepresentation of one Asset can be exposed by multiple MediaSources, each with its own container and stream locator. | A technical rendition does not acquire a new identity because its packaging changes. | 5 |
| 26. Partially corrupt file | A MediaSource records limited/broken accessibility or verification; the representation and Asset remain known. If the readable portion is established, it can support a partial-coverage assertion; corruption alone never creates a new semantic Asset. | Availability/integrity is separate from identity. | 4 |
| 27. Local/NAS/HTTP/future transport | One representation has several MediaSources; access transport changes no upstream identity. | Source is availability only. | 5 |
| 28. Known Work without playable media | Work exists with no MediaEntity, or a known MediaEntity/Asset can exist without representation/source when local catalogue evidence supports it. | Local catalogue knowledge does not require current playback availability. | 5 |

No critical problem arises from these cases. The remaining deliberate uncertainties are listed below; none warrants restoring the rejected mandatory hierarchy.

## Future preference queries

Preference is selection policy, not domain identity. The graph retains enough information for future components to ask for, for example, a video representation matching 1080p AV1 with compatible Catalan audio, Spanish 5.1 audio, and Spanish SDH subtitles. Playback planning, ranking, transcoding, user preferences, and client capabilities are out of scope.

## Related accepted ADRs

ADRs 0007–0011 establish the graph and its original cross-cutting decisions. ADR-0012, ADR-0013, and ADR-0014 add accepted identity, Asset taxonomy, and source-locator boundaries; their full decisions remain in their ADRs.

### ADR-0007: Orthogonal media identity graph

- **Problem:** the mandatory hierarchy cannot represent independent publication, temporal, semantic, technical, and access relationships without duplication.
- **Decision:** adopt the graph of Work, MediaEntity, Timeline, Asset, TechnicalRepresentation, MediaSource, Release, Collection, ReleaseGroup, and Creator; ADR-0007 supersedes ADR-0005.
- **Rejected alternatives:** retain the mandatory chain; treat all variants as Editions; identify media by file/container/provider record.
- **Consequences:** relationships become explicit and some concepts have independent IDs; implementation needs relationship-aware persistence and cannot use a simple ownership tree.
- **Previous ADRs:** ADR-0007 supersedes ADR-0005.

### ADR-0008: AssetCoverage and temporal compatibility

- **Problem:** duration, filenames, and titles cannot safely establish subtitle, dub, angle, or fragment compatibility.
- **Decision:** use explicit coverage relationships with full/partial intervals, gaps, offsets, transformations, incompatibility, and uncertainty.
- **Rejected alternatives:** infer compatibility from duration; make every temporal variant a new Asset; embed alignment in source/container metadata.
- **Consequences:** partial or temporarily unavailable local media and transforms are representable; a later algorithm must interpret rather than redefine this evidence.
- **Previous ADRs:** refines the temporal implications of ADR-0005 without deciding its disposition.

### ADR-0009: Semantic assets, container exposure, and source access

- **Problem:** a container can carry several streams and the same rendition can occur in many containers/sources.
- **Decision:** keep Asset semantic, TechnicalRepresentation technical, and MediaSource physical/network access; record container and stream details as source-specific exposure data.
- **Rejected alternatives:** make every file or stream an Asset; identify renditions by path/URL; introduce container identity as media identity.
- **Consequences:** probing and storage modules need explicit mapping evidence; image/artwork bytes can reuse access concepts only when managed.
- **Previous ADRs:** refines the media-identity boundary in ADR-0005. It defines neither preservation nor a distributed system.

### ADR-0010: Publication and organisation contexts

- **Problem:** releases, release families, compilations, albums, seasons, and box sets have different membership and ordering semantics.
- **Decision:** use non-exclusive Release membership, ReleaseGroup as an optional publication-family Aggregate Root, and Collection as ordered/grouped MediaEntity context.
- **Rejected alternatives:** universal Edition; use Collection for ReleaseGroup; duplicate recordings/episodes for each ordering or publication.
- **Consequences:** original/re-release/remaster and regional variants can coexist; group membership criteria need explicit evidence.
- **Previous ADRs:** no direct replacement; constrains the publication terms previously implicit in ADR-0005.

### ADR-0011: External identification, metadata provenance, and artwork

- **Problem:** multiple substitute providers can disagree, disappear, or identify different concepts, while manual data and artwork must remain valid.
- **Decision:** use internal HISPANIA IDs; associate extensible `Provider / Namespace / Value` ExternalIdentifiers through stateful associations; retain field-level MetadataAssertions and ArtworkReferences with provenance; resolve effective values through configurable policy.
- **Rejected alternatives:** provider-specific ID fields; one global provider; destructive overwrite; external ID as canonical identity; couple identification, metadata, and artwork.
- **Consequences:** matching, invalidation, migration, conflicts, manual data, and provider replacement remain auditable; future modules must declare namespace-to-concept mappings.
- **Previous ADRs:** no direct replacement; establishes a boundary required by ADR-0003's contract-first modularity.

All of these ADRs are Accepted.

## Open questions before implementation

1. What editorial evidence is sufficient to distinguish a new MediaEntity from a new Timeline of an existing one?
2. Should a branching narrative be represented first as a relationship between MediaEntities, Timelines, or a later dedicated composition concept?
3. Are multi-angle video components always distinct Assets, or can a later profile describe angle variants without losing selection semantics?
4. What evidence and lifecycle states should a known-but-unavailable representation or Asset carry?
5. Does Collection need to remain one concept once non-media organisational collections and nested series/season structures are specified?
6. Which local derivation facts require first-class relationships in a conventional media-server catalogue?
7. What evidence permits two source streams to be asserted as the same TechnicalRepresentation?
8. What identifier namespaces and provider-semantic mappings are supported by the first external-provider modules?
9. Which editorial evidence and membership rules decide whether a regional or expanded publication remains in one ReleaseGroup?
10. What retention policy decides when an external artwork reference becomes a managed ArtworkAsset with local or remote bytes?
11. Which resolution-policy inputs are permitted for each metadata field, and how are user-specific choices kept separate from shared assertions?

## Out of Scope / Future Systems

The following are not Media 0.2 concepts and are not modelled here: Preservation, Distributed Media, P2P, Global Catalog, Global Certification, Global Moderation, Global Asset Registry, Network Normalization, Peer Discovery, and Distributed Manifests. If such systems are introduced, they may depend on Media; Media must never depend on them.

## Explicit non-decisions for Media 0.2

This design does not decide C# aggregate boundaries, constructors, persistence, hashing/deduplication, probing/scanning, FFmpeg integration, filesystem behaviour, codecs or languages as closed enums, playback, transcoding, user preferences, provider ownership, rights, or a synchronisation algorithm.
