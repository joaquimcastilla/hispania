# ADR-0040: Historical compatibility baselines

Status: Accepted for Phase 15; selected compatibility evidence, no Public Stable publication.

## Context

Phase 14 SHA `78f291fdacf9442c6e2d8e23094dc0e309828693` passed Validation #37265443105 on Linux and Windows. Current-to-current serialization cannot establish historical compatibility. Distribution 0.7.0, SDK assembly identities, package format 1 and protocol generations 1 remain separate axes.

## Decision

Create technical Compatibility Baseline 0.7 from an isolated `git archive` of that exact SHA. Synthetic DTOs and module/worker frames are serialized with assemblies compiled from the archived contracts. Preserve a test consumer compiled against archived Platform contracts and a real greeting implementation compiled from archived source, inside a signed format-1 package. Tests load the consumer against current assemblies without recompilation; a separate test compiles the same frozen source against current contracts. Historical SDK DLLs are provenance evidence and are never copied into current runtime resolution paths.

Freeze byte hashes, provenance, owner, axis, expectations and regeneration policy in a machine manifest. A separately pinned immutable index protects initial membership and metadata even if a developer edits both the fixture and manifest. All later artifacts must be listed, but new entries can be appended without changing old rows or the index. Git checkout preserves corpus bytes with `-text`. The initial reviewed commit establishes the freeze; construction experiments and failed capture packages are not published baselines.

Compare selected compiled APIs with the immutable 0.7 reflection snapshot. Report removed/changed lines as BREAKING and additions as ADDITIVE, qualified by declaring type. Include public enum numeric values and nullability. This is selected source API evidence, not exhaustive CLR ABI proof. Binary consumer execution provides separate evidence.

Keep signed fixture private keys ephemeral: retain only package signature and TEST ONLY public key. Host trust remains explicit. Exercise package activation in a child process so collectible loading and native Windows file locks cannot contaminate the test runner; assertions still require activation, execution, stop and unload.

HTTP fixtures capture only method, route template, Range input, selected status/headers and a five-byte body. Fencing tests use public coordinator operations and historical completion payloads, asserting obsolete results fail before and after a valid newer result; never snapshot coordinator internals. Full database upgrades, complete mixed-release matrices, deployment and backup are outside this decision.

Future breaks require an owner-approved entry in `compatibility/api/CHANGELOG.md` identifying the exact old surface/artifact, old/new axis, diagnostic, reason, coexistence/migration and supported window. Acknowledgement alone does not waive tests or permit regeneration. Preserve 0.7 and introduce a separately reviewed generation/corpus or explicit support-window transition in the same change.

## Consequences

The first corpus is independent evidence for future changes, while SDKs and protocols remain experimental and the five selected families remain Stable Candidate. Windows local validation and portable Linux CI configuration are distinct from a completed Linux Phase 15 run. Build reproducibility is not fixture immutability: signing uses an ephemeral key, so recapture cannot reproduce the package signature and is not a gate requirement.
