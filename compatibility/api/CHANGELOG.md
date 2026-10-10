# Candidate API review acknowledgements

2026-10-05 — Initial Phase 14 baseline for selected Platform/security/assets and Media/Playback candidate surfaces at phase-13-complete. Implementation review acknowledges the current signatures only; no stable publication or historical binary promise. Fencing/HTTP Range observable behavior retains existing regression tests and explicit gates in PLATFORM-1.0-SCOPE.md.

Initial reviewed snapshot SHA-256: `971cecbd2aecb6ab8afa767ec67471ae810e1ec677db8e872a67d2a0b7f0ed8f`.

2026-10-05 — Phase 15 adopts the same selected snapshot as immutable Compatibility Baseline 0.7 at `api/0.7/selected.txt`, originating in validated Phase 14 SHA `78f291fdacf9442c6e2d8e23094dc0e309828693`. Historical consumer/package/protocol evidence is now separate from current API capture. No intentional contract break is acknowledged here.

Future deliberate break entries must name the owner, baseline artifact/type/member, old and new version axis, exact BREAKING diagnostic, reason, affected consumers, migration/coexistence and support window. Owner review must approve both the entry and the concrete compatibility transition. An entry does not bypass tests. Never overwrite/re-sign/re-hash 0.7 or change the pinned initial index to silence a failure; preserve old evidence and introduce a separately reviewed corpus/generation or an explicit support-window transition.
