# HISPANIA v1.0 — Step 0.3-R2 Owner Decision Packet

**Baseline reviewed:** `118840f934df0783de13bebcd7ea0ac0e66dc9fe`
**Status:** REQUEST FOR OWNER INPUT — none of these recommendations is approved by this packet.
**Phase 1:** NOT READY; NOT AUTHORIZED.

## 1. Phase 1 internal development and rights boundary

**Question:** May Phase 1 internal-only implementation begin before all source ownership/authority and license conflicts are resolved, if no affected source, package, SDK or binary is redistributed?

- **Recommendation — NOT APPROVED:** permit internal-only Phase 1 work only after the owner confirms authority to develop/reuse the selected source; keep all builds local/private and do not distribute artifacts. Resolve rights before any public or third-party distribution.
- **Conservative alternative:** do not begin Phase 1 implementation until authority and license scope for every reused file/contribution are evidenced and reviewed.
- **Trade-off:** the first option may preserve development progress but does not cure unknown rights and depends on adequate authority to make even internal copies/builds. The conservative option reduces uncertainty but delays implementation.
- **Owner response requested:** select one, define exclusions/conditions, and identify the evidence or review required.

## 2. §12 and §16 timing interpretation

**Question:** Which §12 items must be closed before the first Phase 1 code change, and which may be delivered as implementation/acceptance outputs under §§13 and 16?

- **Recommendation — NOT APPROVED:** before code, require baseline/worktree inventory, the rights-safe scope, minimum work breakdown, proposed dependency boundary, and the admin mutation security decision. Let the new composition root, final package manifest, fixture trust tests and empty-boot process acceptance be implementation deliverables, with explicit hold points before affected routes/artifacts are enabled or distributed.
- **Conservative alternative:** treat every §12 “BLOCKER for Fase 1” bullet as a pre-code gate, including a complete root, allowlist, artifact manifest and empty boot acceptance evidence.
- **Trade-off:** the recommended interpretation follows §13's implementation language but must reconcile the §12/§16 entry-gate wording; the conservative interpretation preserves the strictest reading but makes some “Phase 1” deliverables prerequisites to starting Phase 1.
- **Owner response requested:** define the exact pre-code checklist and acceptable evidence. This packet does not amend the Freeze.

## 3. Proposed Core dependency allowlist

**Question:** Approve or amend [the proposed allowlist](HISPANIA-V1-CORE-DEPENDENCY-ALLOWLIST.md)?

- **Recommendation — NOT APPROVED:** start from the narrow new Core host, reviewed Runtime/ModuleSystem/Platform contracts, minimal local identity/authorization, ModuleHost, and a new provider-neutral Core persistence contract with a narrow SQLite provider. Exclude server, business modules, old Platform SQLite provider until its broad graph is justified, workers, compatibility artifacts and demo assets.
- **Conservative alternative:** approve no existing application/runtime project for reuse until a component-level reference review is complete; design a minimal new composition and provider graph first.
- **Trade-off:** reuse reduces duplicate code but carries transitive responsibilities; a narrower graph costs more initial design/implementation and may avoid importing unrelated platform behavior.
- **Owner response requested:** approve, reject or amend the project-level list and name any mandatory exclusions.

## 4. Exclude unknown-rights artifacts from Core

**Question:** Confirm the proposed Core exclusion of five historical DLLs and the legacy MP4 listed in [the artifact inventory](HISPANIA-V1-UNKNOWN-RIGHTS-COMPONENTS.csv).

- **Recommendation — NOT APPROVED:** yes, exclude all six from Core and do not use the historical greeting DLL as the Phase 1 fixture; select a newly built rights-cleared fixture if/when Phase 1 is authorized. Separately hold legacy server redistribution of the MP4 pending rights evidence.
- **Conservative alternative:** quarantine the six artifacts from all builds/tests and remove them only through a separately authorized preservation/legal decision after historical compatibility requirements are addressed.
- **Trade-off:** exclusion keeps Core small and avoids importing unclear rights; quarantining from all uses may break the historical compatibility corpus. Neither is a legal rights determination.
- **Owner response requested:** select Core disposition and separately decide whether to retain/execute the historical compatibility test after rights review.

## 5. Contributor authority and provenance evidence

**Question:** What evidence can the owner provide that establishes authority over each file/contribution intended for future licensing?

- **Recommendation — NOT APPROVED:** prepare a file/contribution-level origin register identifying author/contributor, employee/contractor status, assignment/permission, third-party or copied/derived content, generated/AI-assisted inputs where known, prior grants, and asset creator/performer/source/consents. Record evidence without inferring authorship from Git identity.
- **Conservative alternative:** treat every file with incomplete origin evidence as unavailable for reuse/relicensing until specific written permission or other authority evidence is obtained.
- **Trade-off:** targeted provenance can focus on Core/SDK candidates first but leaves other repository content uncleared; repository-wide review costs more and may uncover broader legacy uncertainty.
- **Owner response requested:** identify contributors and evidence available, choose the initial audit scope, and determine whether employment/contractor assignments or external permissions need review.

## 6. Legal review and intended distribution

**Question:** What legal review is required before public source, binaries, `.hispania` packages or SDK/contract publication?

- **Recommendation — NOT APPROVED:** obtain qualified review of authority and license application per file/component, combined distribution/link/load/IPC boundaries, third-party notices/native payloads, package signing versus rights, public API/SDK terms, trademarks and contributor policy before each intended public distribution class.
- **Conservative alternative:** no public source, binary, package, SDK or external contributor onboarding until a complete repository-wide rights and licensing review is complete.
- **Trade-off:** artifact-scoped review can unblock a narrow release earlier but does not clear unrelated components; repository-wide clearance is slower and more comprehensive.
- **Owner response requested:** choose the review scope and gate; identify counsel/reviewer and the intended first distribution class.

## Evidence for owner review

- §12 labels seven items “BLOCKER for Fase 1”; §13 calls itself a specification for the next phase and says it is not implementation authorization; §16 calls its list entry gates to complete in the later preparation/implementation task. Exact pre-code timing is ambiguous, so readiness is not passed here.
- The candidate allowlist is static graph analysis, not an artifact-specific published dependency graph.
- Rights are not resolved by hashes, a valid signature, repository presence or a Git author field. `Signed != Safe`; publisher trust and permissions remain separate under Owner Decision 9.2.
- No owner option in this packet is approved. No progression to another decision, Phase 1 task or implementation follows from this review.
