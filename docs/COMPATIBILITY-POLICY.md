# Compatibility policy

The integrated documentation baseline is HISPANIA 0.7.0. Distribution SemVer is independent of SDK releases, module implementation identities, route/wire generations, schema migrations and instrumentation identities. No complete subsystem or catalog entry is Public Stable in Phase 14.

## Meaning of a commitment

Every compatibility declaration must name its owner, consumers, stability, category, supported versions and evidence. A CLR `public` modifier permits access; it does not promise stability. Internal means implementation/tooling boundary, Experimental means an uncommitted evolving surface, Public Experimental means an externally consumed uncommitted surface, Stable Candidate means a specifically selected boundary with remaining gates, Public Stable means an explicitly published commitment, and Deprecated means retained with a retirement plan. Stability and exposure are separate catalog fields.

| Category | Promise to test |
| --- | --- |
| Source | Previously valid consumer source still compiles with supported SDK |
| Binary | Previously compiled assembly/plugin still loads and runs against supported host; source recompilation is insufficient evidence |
| Wire | Supported peers exchange messages, negotiate supported generations and reject incompatible peers safely |
| Data | Previously persisted data can be opened or explicitly migrated without silent reinterpretation/loss |
| Behavioral | Documented outcomes, security, error/status, ordering and side-effect semantics remain true |

Do not say only “compatible”. Module compatibility is the intersection of package/manifest reader, host bounds, capability identity, shared CLR assembly/API, runtime policy and IPC where applicable. Host bounds passing alone proves none of binary, wire or behavioral compatibility. Authentication/trust/allowlists remain mandatory regardless of version.

## Distribution 0.x

A patch (0.7.0 → 0.7.1) permits bug fixes, hardening and internal changes with no intentional break of declared compatible surfaces within that minor line. Security fixes may correct previously unintended behavior; document observable impacts and migrations, never quietly call an intentional contract break a patch. A minor (0.7.x → 0.8.0) may deliberately break experimental or candidate surfaces after owner review, release notes and migration/compatibility notes where consumers or persisted data are affected. Pre-1.0 is not permission to break without notice. SDKs that later ship independently use their own SemVer and declarations, not automatic distribution numbering.

## Future published 1.x surfaces

Publication of a specific surface at 1.0 requires a written category/window promise and passing gates. 1.1/1.2 preserve that promise; breaking public contracts requires 2.0 unless an explicitly documented coexistence/negotiation mechanism preserves the promised older surface. Additive members can break interface implementers, enum switches, JSON validation or binary loading; review actual consumers before calling a change additive. None is published stable here.

## Protocols, DTOs and data

Integer generations and V1 suffixes are technical identities. IPC currently negotiates generation overlap; capability resolution uses exact equality. HTTP `/v1` is routing, not SDK SemVer. Review JSON names/casing, omitted/null/default fields, enums, limits, errors and status codes alongside signatures. Envelope evolution and payload evolution are separate: event `(EventType, Version)` requires explicit decoder support; Jobs currently has only JobType and JSON, so incompatible handlers need a new identity or migration. No automatic upcasting or N-1 negotiation is claimed. Persistence migrations belong to each store owner; read compatibility, upgrade, backup and rollback are different claims. Storage URI handles remain opaque and legacy hex readers must be retained until a reviewed migration replaces them.

## Window

Current evidence establishes same-checkpoint interoperability plus particular legacy data cases (filesystem handles, migration-history bridges), not a global previous-minor promise. Patches in the 0.7 line must preserve stated behaviors and pass current regressions. The target, not current guarantee, is current minor N plus previous minor N-1 for selected published SDK APIs, package/manifest readers, IPC/worker generations, selected HTTP DTOs and populated stores. Owner declarations and actual old fixtures must establish each category separately before advertising it. Phase 15 may start verifying that target; this phase does not implement its historical corpus. ARM64 compilation or running latest tests does not validate older peers.

## Deprecation and change review

For future Public Stable surfaces: mark deprecated, name replacement and category impact, publish migration notes and a removal release; retain through at least the next minor and a minimum six months, and never less than the published support window. Remove only in an appropriate major unless continued coexistence keeps the old promise. For 0.x: publish replacement/impact and intended next-minor removal before retirement; emergency security changes require explicit reasons and operator instructions. Persisted formats cannot be discarded merely because an API is deprecated. Internal changes may be coordinated, but still must preserve required data/security invariants.

The catalog is reviewed metadata, not runtime reflection annotations. New or changed entries require owner and breaking policy. Candidate API changes must update the generated baseline with a review acknowledgement in `compatibility/api/CHANGELOG.md`. Publication as Public Stable requires a separate explicit decision and enforcement expansion; Phase 14 gates reject it. Fixtures must contain no credentials, personal data or physical deployment paths.

## Phase 15 historical evidence

Phase 15 implements immutable selected historical evidence in [Compatibility Baseline 0.7](PHASE-15-COMPATIBILITY-BASELINES.md). The manifest/index gate protects hashes and membership; an owner-approved breaking-change acknowledgement never authorizes rewriting old fixtures or automatically waives CI. Preserve evidence and explicitly review coexistence, migration or supported-window changes.


## Phase 16 — historical persisted data

Owner-local SQLite schemas now track version 1 (`0001_baseline07_adoption`) and minimum supported known legacy shape 0. Media keeps its EF SQLite five-step and PostgreSQL one-step lineages; playback JSON remains schema 1. Unknown/future histories fail before writes. Upgrades are forward-only and per-store transactional; startup blocks readiness on failure. Old executables without guards are not safe rollback targets. Data schema versions remain independent of distribution/API/wire versions; no contract gains Public Stable status.

The independent 15-artifact populated corpus and mandatory Linux PostgreSQL gate are documented in [Phase 16](PHASE-16-HISTORICAL-DATA-UPGRADES.md) and [ADR-0041](adr/ADR-0041-schema-evolution-and-historical-upgrades.md). Phase 15's 27 artifacts and hashes remain frozen. Local implementation is prepared for CI; Phase 16 closure awaits live PostgreSQL and both OS jobs. Backup/restore remains outside this phase.

## Phase 17 — restore compatibility

Generation-1 bundles reject unsupported formats, future distribution/schema metadata, duplicate paths and integrity failure before switching an application root. Restore may materialize known legacy owner schemas and subsequently run Phase 16 migrations. SHA-256 is integrity evidence, not backup authentication. Clean-root offline restore is experimental; distribution remains 0.7.0. See [Phase 17](PHASE-17-BACKUP-RESTORE.md).

## Phase 18 production deployment

Distribution 0.7.0 and Configuration Schema Generation 1 are independent. Strict production JSON rejects unknown fields/generations; omitted optional fields use typed defaults. No implicit configuration migration. Private material is reprovisioned after Phase 17 restore. After new-host startup, rollback requires verified backup plus old binaries because owner migrations remain forward-only. See [Phase 18](PHASE-18-PRODUCTION-DEPLOYMENT.md) and [ADR-0043](adr/ADR-0043-configuration-secrets-and-production-deployment.md).
