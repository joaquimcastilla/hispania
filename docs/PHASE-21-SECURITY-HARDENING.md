# Phase 21 — Security Operations Hardening

Status: **Phase 21 — ready for CI** after Windows local regression. **Phase acceptance remains pending mandatory Linux/systemd, real SCM and authenticated PostgreSQL CI.** No push, PR, merge, tag, Release or Phase 22 is authorized by this work.

## Git and version state

Branch `feature/phase-21-security-operations-hardening`, exact base `742914b097aa8407bbd93a0ddcd0b40bfe7f46d9`. Fetch and commit existence verification preceded branch creation. Worktree: `phase21-security-hardening`. Existing changes to `docs/ROADMAP.md` and untracked `docs/MODULE-MATURITY-AUDIT.md` remain in the original Phase 20 checkout and are not included here.

Distribution 0.7.0, Package Format 2, Web Contribution Schema 1, 25 version axes and Public Stable 0 remain. No serialized version axis is added. Compatibility tightenings are recorded in `COMPATIBILITY-POLICY.md`.

## Durable model and operations

* [Threat model](security/THREAT-MODEL.md): nine actors, assets, STRIDE surface inventory, nine trust boundaries, assumptions, invariants and residual risks.
* [Operations](security/SECURITY-OPERATIONS.md): account, package, certificate, backup and secret incidents; severity and vulnerability handling; Audit limitations.
* [Configuration](security/SECURITY-CONFIGURATION.md): socket/proxy semantics, authentication/request bounds, TLS, private files, processes and trust.
* [Supply chain](security/SUPPLY-CHAIN.md): audited graph, exact build advisory, runtime distinction and artifact policy.
* [ADR-0046](adr/ADR-0046-security-boundaries-and-operational-hardening.md) and repository `SECURITY.md`.

## Implemented controls

| Area | Result |
| --- | --- |
| Authentication | Process-local bounded token buckets: socket burst 20/20 per minute; account/socket burst 5/6 per minute; 4096 partitions, 10 minute inactivity expiry, saturation fail-closed. Generic 429 and Retry-After. Bootstrap shares socket control. No permanent account lockout. |
| Enumeration/passwords | Missing/wrong/disabled failure equality; existing dummy PBKDF2 and fixed-time compare retained. Login >1024 characters rejected before UTF-8/PBKDF2; creation/change 12–1024. 600k SHA256 iterations, 16 byte salt unchanged. |
| Sessions | Fresh 256-bit random tokens, hash-at-rest, expiry and single-session logout revalidated. SQLite disable atomically revokes all current sessions; insertion requires enabled status; re-enable cannot resurrect old tokens. No general revoke-all SDK/API is added. |
| Authorization | Existing backend RBAC retained, with explicit permission metadata linked to the actual filters. Startup rejects unclassified registered endpoints. Admin-only machine-readable `/api/platform/admin/security/endpoints`. Reviewed route/method allowlist and handler policies cover health, login/bootstrap, shell, artwork, public-read assets, signed PNG, sessions, Web operations, development/test handlers and mTLS. Permission-filter denial Audit and legacy admin rejection warnings share a burst 100/one-per-second disk budget; registered aggregate denial/suppression metrics count all attempts. |
| Diagnostics | Detailed module/transcoding health requires observability permission; metadata enrichment requires Media admin. Public readiness/liveness remains aggregate. Error boundary adds stable `request_failed` and validated/generated correlation ID while suppressing exception material. |
| Network | Forwarded headers ignored; socket peer cannot be spoofed through XFF. Kestrel headers/count/line/timeouts explicit; auth body 8 KiB including chunked handling. No credentialed wildcard CORS or auth cookies. HSTS not inferred from forwarded headers. |
| Headers/Web | Existing CSP retained; no arbitrary contribution JS/HTML/URLs. nosniff, DENY, no-referrer, sensitive no-store. Both browser builds assert headers plus existing CSP/XSS/permission negatives. |
| Packages | Null/oversized manifest collections rejected; actual manifest decompression bounded to 1 MiB before JSON parsing; duplicate fields rejected consistently in install and inspection. Existing archive, signatures, graph, publisher trust/revocation and activation controls retained. |
| ModuleHost | Frames reject invalid lengths, malformed JSON, duplicate fields, null envelopes and unknown opcodes; null/oversized launch tokens fail closed. Stdout/stderr drain with fixed buffers and capture at most 16 KiB; startup errors do not serialize child stderr. Existing timeout/OS resource/restart policy retained. |
| Worker | Null/oversized inventory rejected without dereference; results capped at 1 MiB; heartbeat IDs/count/lease/failure codes bounded. Malformed authenticated worker input returns safe 400. Existing capability allowlist and fencing retained. |
| Backup/filesystem | Verify before modifying restore target; cap declared bundle payload 1 TiB, 65536 files, depth 64; no ZIP decompression surface in directory bundles. Windows staging/restore root ACL limited to current identity, SYSTEM, Administrators; Linux private root 0700. Existing traversal/link/temp cleanup checks retained. |
| Secrets | Existing private-file ownership/mode/ACL and all-ancestor reparse checks run on every resolve. ModuleHost/FFmpeg/FFprobe remove production `HISPANIA_SECRET_*` inheritance while preserving required runtime/protocol environment. Explicit seven-class fake canary production logger test plus gate-output scan. Login intentionally issues its session token in the no-store response; that authorized delivery is not diagnostic disclosure. |
| Certificates | Startup/check evaluates configured TLS/worker certificate validity and safe dates; expired/future material rejected; explicit 30 day warning. Real ephemeral TLS test covers missing/wrong/expired/future identity, exact pin A→B rotation, A rejection, B reconnect and revocation. No CRL/OCSP infrastructure. |
| Deployment/process commands | Existing structured FFmpeg/FFprobe/pg/ModuleHost arguments and local media streams reviewed. Windows service paths reject controls/quotes; systemd paths reject controls and service account cannot start with an option prefix. Real script regression rejects unsafe input before mutation. Existing service hardening unchanged. |

## Reproducible security gate

`./tools/test-phase21-security-hardening.ps1 -AuditDependencies` runs separately from normal regression. Expected minimum suites: runtime 32, HTTP/Identity/backup permissions 7, bootstrap/permission races 2, certificate dates 4, endpoint/service policy 3, real mTLS abuse 1, HTTP ranges/opaque IDs 2, seven-class secret canaries 1: **52 tests, no skips**. Missing/failed/skipped tests fail the gate. Fixed seeds 2101/2102/2103 (384 random malformed JSON payloads with valid bounded frame lengths, invalid-length corpus and depth-65 rejection), 2111/2112 (256 valid-text/invalid-route pairs), 2121 (64 bounded malformed archives), 2131/2132 (128 valid ranges paired with malformed ranges and IDs), 2141/2142 (256 valid dependency ranges/canonical IDs paired with unsafe mutations), explicit traversal/case-collision/null/oversize/envelope regressions. Inputs contain no personal or real secret data.

Finite child probes exercise the existing real resource policy: CPU execution must end before an eight-second fallback, memory allocation is capped at 128 MiB even if the configured 64 MiB ceiling fails, and Windows attempts exactly one child under a one-process Job Object. Parent has a 20-second deadline and tree cleanup. Linux exercises prlimit CPU and the managed heap cap; no Linux child-count containment is claimed. Stdout/stderr newline-free input is separately drained and bounded to 16 KiB capture.

The real server exports 133 development/test endpoints with no unclassified policies; permission-protected GET routes also reject anonymous requests. Production excludes the development/test handlers. Machine-readable `endpoint-inventory.json` is preserved by the dedicated gate. Normal diagnostic baselines are recorded in TRX for successful PBKDF2 login, registry snapshots, package validation and protocol roundtrips; these are rough smoke measurements, not comparative benchmarks or SLAs.

CI provisions seven fake secret classes in the production logger test, captures production-gate stdout/stderr, then scans local text evidence and expanded production artifacts with `test-security-canaries.ps1`. This does not claim to fetch or scan remote GitHub console logs. Existing production artifact tests verify runtime-only content; the final clean-SHA publication is recorded below.

Validation CI runs the dedicated gate on Linux and Windows and preserves `.artifacts/phase21-security`. Existing historical, PostgreSQL, closure, browser, real SCM/systemd and production workflows remain required. Runtime npm findings or new unaudited build advisories fail. NuGet audit failure/unavailability fails rather than pretending a clean result.

## Evidence ledger

Local Windows execution outputs live in `.artifacts/phase21/` and dedicated gate directories. Only the final accepted result for the final implementation should be used; earlier discovery failures are retained for diagnosis, not counted as successes.

| Gate | Current evidence |
| --- | --- |
| Dedicated security | Final `security-final8`: 52/52 passed, zero skips; actual HTTP metric increments, route/method policy, secret inheritance and JSON depth corpus included. |
| Web unit/build | 89/89, Modern and Classic builds passed. |
| Phase 20 | 55 runtime, 2 backup and both real Chromium builds passed; no retries/skips. |
| Phase 19 | Final rerun: 75 lifecycle and 2 backup passed; exact historical reader SHA `86b43bc414edd53d8f7312a0d4045dc2818a39f6`. |
| Phase 15 | 16/16 immutable compatibility passed. |
| Phase 16 | 36/36 portable historical data upgrade passed; live PostgreSQL reserved for Linux CI. |
| Phase 17 | 45/45 portable backup/process/interruption passed; final full regression also rechecked the new actual Windows ACL case. |
| Phase 14/versioning | Included in full solution and explicit CI gate; no maturity/version promotion. |
| Closure/concurrency | 210 successful executions across 62 TRX reports, including all 20 repetitions; zero failures/skips. |
| Full .NET | Final `final4`: **854 passed, zero failures, two expected PostgreSQL skips on Windows**. Initial discovery/teardown issues were corrected, not excluded. |
| Production | 42 local configuration/published process/package/Web upgrade cases passed. Clean-source publication and its follow-up evidence use `.artifacts/phase21/production-clean` and `production-clean-evidence`; these are generated after local commits. Native SCM, Linux/systemd and authenticated PostgreSQL require their real CI hosts. |
| NuGet/npm | Final full Windows solution audit: zero NuGet advisories; runtime npm clean. One assessed High build-only source-map-js advisory; exact waiver rejects additional advisories, sources or audit failures. |

Final browser evidence: Modern 17.3s, Classic 17.2s, two passed in 36.3s with zero failures/skips/retries. Local canary scan covered 473 output/artifact files and passed; a separate synthetic leak probe was correctly rejected without printing its value. Expanded final clean artifacts are scanned again after publication.

Rough diagnostic samples in final TRX: normal PBKDF2 login 302ms, ten package inspections 42ms, one hundred empty registry snapshots 23ms, one thousand normal protocol roundtrips 22ms. Concurrent login/disable completed in 824ms. These are machine/load-dependent samples, not SLAs or proof of a comparative performance improvement. Native tools recorded locally: .NET SDK 10.0.401, npm 12.0.2, FFmpeg/FFprobe 9.0.1; authenticated native PostgreSQL remains a CI prerequisite.

## Remaining acceptance work and limits

This phase must not be closed by documentation or a small passing subset. Local final regression, final clean-SHA publication and mandatory real service/PostgreSQL/Linux gates must be reconciled with the Definition of Done below. No Linux distribution or PostgreSQL service/tooling is installed here, and this Windows process is not elevated for SCM. Those gates remain required on their configured CI hosts. No push or remote CI dispatch is performed under the user's local-only instruction.

## Definition of Done traceability

| Item | Control and executable evidence | Acceptance |
| --- | --- | --- |
| 1–2 Threat model/boundaries | THREAT-MODEL actors, assets, STRIDE, assumptions and nine boundaries | Documented |
| 3 Invariants | Dedicated 52-test gate plus identity, authorization, package, Web, backup and fencing regressions | Windows local green |
| 4–5 Authentication/enumeration | Socket/account buckets, generic failures, dummy PBKDF2; real HTTP spoof/recovery and Identity tests | Local |
| 6 Sessions/revocation | Persistent logout, disable/re-enable and eight concurrent login attempts | Local |
| 7–8 Endpoint inventory/allowlist | Startup rejects unknown routes, real 133-route inventory, admin/anonymous negatives | Local |
| 9 Health/errors | Aggregate public health; protected details; safe exception/correlation tests | Local |
| 10 Proxy semantics | No forwarded-header processing; real XFF/proto spoof negatives | Local |
| 11 Request limits | Kestrel/body policy, 413, password/archive/backup/protocol/worker ceilings | Local |
| 12 Package fuzz | Seeds 2121/2141/2142; null/duplicate/path/case/graph/signature/revocation regressions | Local |
| 13 Protocol malformed input | Seeds 2101–2103, opcodes/duplicate/null/length, inventory/results/fencing | Local |
| 14 Filesystem | Actual backup/restore ACL test; Phase17 traversal/link/private-secret/reparse regressions | Windows local; Linux CI |
| 15 Commands | Structured FFmpeg/FFprobe/pg/ModuleHost arguments; actual control-path script rejection | Portable/local; native pg CI |
| 16 Child limits | Real finite CPU/memory probes and Windows one-child denial; output cap | Windows local; Linux CI |
| 17 Secrets | Seven-class logger canaries, no exception formatting, gate scans and production artifact scan | Local outputs; CI repeated |
| 18 Certificates | UTC validity/warning policy; published worker TLS tests and actual rotation/revocation | Local processes; services CI |
| 19 Worker mTLS | Real TLS missing/wrong/expired/future/node/pin/rotation/revocation test | Local |
| 20–21 Headers/Web negatives | Modern/Classic real Chromium CSP/headers/XSS/RBAC and schema tests | Local |
| 22 Supply chain | Full/portable NuGet audit, runtime/full npm scope and exact dev advisory waiver | Point-in-time audit |
| 23–24 Operations/risks | Incident runbooks, sampling/metrics, backup/key/trust risks | Documented |
| 25 Phase14–20 | Exact compatibility/historical/backup/production/package/Web/full regression ledger | Windows local green; native/Linux gates pending |
| 26 Services/PostgreSQL | Existing required real SCM/systemd and authenticated PostgreSQL jobs remain intact | **Pending CI; not locally verified** |

OutOfProcess shares OS identity and is not a hostile-code sandbox. Local/root compromise, stolen unrevoked tokens, plaintext file/environment secrets, unencrypted backups and unauthenticated backup provenance remain explicit risks. Audit is append-oriented, not tamper-proof. Single coordinator, no HA, no automatic PKI revocation, supervisor log/capacity dependencies and managed-string zeroization limits remain. These limitations are deliberate; outstanding acceptance evidence is separate and cannot be relabelled as an accepted residual risk.

Readiness: **Phase 21 — ready for CI**, with local Windows evidence above. Closure is **not** claimed: Definition of Done item 26 and actual Linux execution require the existing mandatory CI hosts. No remote CI run is asserted. Do not begin Phase 22.
