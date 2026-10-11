# ADR-0039: Versioning and compatibility model

Status: Accepted for Phase 14 foundation; no Public Stable publication.

## Context

Checkpoint phase-13-complete resolves to a52e7d5462c93778074b835816149c77005a42a1; functional validation was 75e10edd560e396a6e509a386d5aae5dd7928827. The local, untracked MODULE-MATURITY-AUDIT.md is input evidence, not assumed Git history. Existing System.Version values, V1/V2 names, package/worker generations, routes, schema migrations and instrument versions identify different boundaries. One product number cannot express all their consumers or compatibility rules.

## Decision

Adopt distribution SemVer baseline 0.7.0 in root version.json, with no automatic assembly/npm/package renumbering. Future independently distributed Platform/Module SDKs use explicit SDK SemVer and selected API promises. Preserve current exact capability comparisons and legacy host package bounds 1.0.0; they are not product maturity. Protocol/package format integers are technical generations; manifests currently lack a separate schema version. Schema sequences belong to persistence owners; event envelope generation differs from EventType payload version, and Jobs lacks independent payload negotiation.

Use reviewed machine metadata plus source inventory and tests, not runtime attributes/reflection enforcement in SDKs. Catalog all current contract CLR declarations and production version-named declarations, plus explicit HTTP/behavior/data families; Public visibility does not imply Public Stable. Identity query/command, RBAC service, Asset identity, Direct Play/Range and fencing are Stable Candidate with stated gates. Payload evolution, worker/module SDK/package details, provider contracts and Web/Admin DTOs remain experimental.

Compatibility declarations distinguish source, binary, wire, data and behavioral promises. Distribution patches preserve declared minor-line commitments; pre-1.0 minors may deliberately break with owner review and migration notes. Future published 1.x surfaces preserve their explicit category/window promise across minors; public breaks need major or documented coexistence. Deprecation names replacement, timeline and migration. N plus N-1 is a selected-surface target requiring evidence, not a global guarantee today.

Keep lightweight compiled reflection API snapshots for selected candidates and CI metadata gates. Golden fixtures have owner/category/provenance; historical compatibility corpus remains Phase 15+. The empty Kernel project is a placeholder (Option B); record proposed later removal without unrelated solution edits. Versioning tooling belongs to Platform and adds no domain dependencies; Platform-only independence remains enforced.

## Consequences

### 0.7.0 stabilization application

Apply this model to cross-node evolution: do not repurpose V1 worker/job fields or assume old agents understand new required semantics. Use optional/defaulted additions only where old readers remain safe; otherwise negotiate a protocol generation or introduce a separately versioned contract. Capability IDs and versions stay independent of distribution SemVer. Unknown required capabilities fail closed, and compatibility claims require old/new consumer evidence. The proposed generic workload and direct-data concepts are documented, not implemented, in [Step 1/10 audit](../HISPANIA-0.7.0-STEP-1.md).

Existing wire, schemas, APIs, signed fixtures and package acceptance stay unchanged. The root distribution source is usable by future release tooling without affecting package identity. Documentation makes maturity and compatibility limits explicit; gates catch classification gaps, duplicate IDs, source mismatches and candidate baseline changes, but do not prove historical binary/wire/data compatibility. Baselines are evidence for change review, not automatic stability. No Phase 15 implementation, push, PR, merge, tag or release is part of this phase.

See VERSIONING.md, COMPATIBILITY-POLICY.md, SUPPORT-MATRIX.md, PLATFORM-1.0-SCOPE.md, VERSION-MAP.md and compatibility/CONTRACT-CATALOG.md for operative definitions.
