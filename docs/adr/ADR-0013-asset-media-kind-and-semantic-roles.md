# ADR-0013: Asset Media Kind and Semantic Roles

Status: Accepted

## Context

ADR-0009 defines an Asset as a semantic media component distinct from its technical representations and physical sources. A single category such as `audio.dub` would conflate the structural modality of a component with its semantic function, creating an unnecessary combinatorial taxonomy.

Media needs to describe compatible semantic roles without treating language, codecs, provider labels, or source stream details as identity.

## Decision

- Every Asset has exactly one closed structural `AssetMediaKind`: `Video`, `Audio`, `Text`, or `Image`.
- An Asset has zero or more `AssetRole` values.
- `AssetRole` is a value object based on a HISPANIA-controlled domain code, not an arbitrary provider value or a provider extension mechanism.
- Roles are unique within an Asset.
- Every role assigned to an Asset must be compatible with that Asset's `AssetMediaKind`.
- The initial role vocabulary is `main`, `dub`, `commentary`, `audio-description`, `subtitle`, and `lyrics`.
- Initial compatibility is: `main` with Video or Audio; `dub`, `commentary`, and `audio-description` with Audio; and `subtitle` and `lyrics` with Text.
- New roles are an intentional evolution of the HISPANIA domain vocabulary; providers cannot introduce roles arbitrarily through metadata.
- Language, codec, resolution, title, provider, stream index, and other technical or source-specific properties are not Asset roles.
- Several compatible roles on one semantic component do not require duplicating that Asset.
- Image Assets do not initially use editorial roles because artwork editorial function is represented by `ArtworkReference`.

## Alternatives Considered

- A combined media-kind-and-role taxonomy such as `audio.dub`: rejected because it conflates independent dimensions and grows combinatorially.
- One optional role per Asset: rejected because one semantic component may legitimately satisfy several compatible roles.
- Arbitrary role strings supplied by metadata providers: rejected because they make domain invariants and cross-provider semantics unstable.
- Treat roles as ordinary metadata: rejected because semantic roles affect domain selection and compatibility rather than merely describing an Asset.
- Image editorial roles on Asset: rejected initially because `ArtworkReference` already carries that editorial relationship and provenance.

## Consequences

- The domain can validate modality and semantic role independently.
- Multiple compatible roles can be represented without artificial Asset duplication.
- Role vocabulary changes require deliberate domain evolution and compatibility tests.
- This ADR does not define languages, codecs, technical profiles, provider mappings, or artwork retrieval policy.

## Related ADRs

- ADR-0007 defines Asset as part of the orthogonal Media graph.
- ADR-0009 separates semantic Assets from technical renditions and source exposure.
- ADR-0011 keeps provider-supplied metadata and identification separate from internal domain identity.
