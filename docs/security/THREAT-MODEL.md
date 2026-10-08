# HISPANIA threat model

Scope: Distribution 0.7.0, Package Format 2, Web Contribution Schema 1, single coordinator. This model describes security boundaries, not a guarantee of absence of vulnerabilities. Phase 21 evidence is tracked in `../PHASE-21-SECURITY-HARDENING.md`.

## Assets and actors

Protect identity credentials and sessions, role assignments, signing and mTLS keys, configuration secrets, database/domain state, asset contents, authorized package graph, execution generations, availability, and operational evidence.

| Actor | Authority and threat |
| --- | --- |
| Anonymous remote client | Reaches exposed HTTP; can spray passwords, submit malformed requests, and consume resources. |
| Authenticated ordinary user | Valid session, limited permissions; may invoke hidden APIs directly. |
| Privileged HISPANIA administrator | Platform permissions; can authorize executable packages and workers; is not necessarily an OS administrator. |
| Worker | mTLS private key and registered SHA-256 certificate pin; may send false inventory, malformed results or stale generations. |
| Package publisher | Signing key whose public key is trusted by the operator; integrity does not establish harmlessness. |
| Malicious untrusted package | Archive/manifests can be inspected without executing code; trust and activation authorization are separate gates. |
| Compromised authorized package | Previously trusted executable code; InProcess can compromise the entire server. OutOfProcess shares service identity unless separately provisioned. |
| Local service account | Reads runtime secrets and modifies state; compromise exposes those assets. |
| OS administrator/root | Can replace code, access keys/state, change ACLs and clocks; outside HISPANIA's strong boundary. |

## Assumptions

TLS termination and routing are correctly configured; OS permissions and process controls work; publisher and CA/mTLS private keys remain private; the clock is reasonably correct; package trust is deliberately administered; production artifacts are verified against expected build provenance by the operator. Hash checksums alone do not authenticate the publisher. Local secret files hold plaintext, not hardware-backed vault material. Managed .NET strings do not provide guaranteed secure zeroization.

## Trust boundaries

| Boundary | Authentication / authorization | Integrity / confidentiality | Limits / failure policy |
| --- | --- | --- | --- |
| Browser → HTTP Server | Explicit route policy; Session token plus current backend permissions; reviewed anonymous exceptions | Production remote endpoint requires HTTPS; same-origin CSP; no auth cookies | Kestrel headers/timeouts; 8 KiB login/bootstrap; token buckets; generic login rejection; unknown policy stops startup |
| Server → PostgreSQL | Operator-provisioned credentials and DB grants | Parameterized queries; password references resolved locally; transport security follows DB deployment | Driver pool and transaction policies; unavailable DB degrades health; no connection string returned |
| Server → filesystem | Service OS identity; asset identifiers are opaque handles | Root/path and link checks, platform ACLs | Streaming bounded writes; reject traversal/reparse; fail operations rather than report partial success |
| Server → ModuleHost | Per-launch random token, package identity/version and protocol negotiation | Current-user local pipe; fixed-time secret comparisons | Frame length, concurrency, duration and OS process limits; fail/restart under bounded policy |
| Coordinator → WorkerAgent | TLS validation plus registered SHA-256 peer certificate pin associated with node ID; capability allowlist | mTLS and transfer bearer grants scoped to job | Bounded metadata/work/result sizes; generation fencing; reject expired/future certificates and stale results |
| Package archive → verifier | Publisher signature and separate operator activation authorization | Signed payload inventory/hash checks; inspection never loads package code | 128 MiB compressed, 256 MiB expanded, 512 entries, bounded dependency search; reject malformed/unknown formats |
| Web manifest → renderer | Active authorized package; backend contribution and operation permissions | Signed Schema 1 declarative data; React text escaping; no raw JS contract | 64 KiB descriptor, 32 contributions/components, bounded text/PNG/rows; unsupported data rejected |
| Operator → backup/restore/deployment | Local operator OS authority and exclusive installation lock | Internal consistency hashes, path/link checks; backups contain sensitive plaintext state | Bounded metadata; clean restore target; failures preserve valid state; provenance is not authenticated |
| Operator → configuration/secrets | Local OS permissions and strict production configuration | `file:` ownership/ACL checked on every resolve; `env:` exposed to privileged local processes | 64 KiB secrets, bounded config; reject unknown/unsafe production settings; no known/demo credentials |

## Systematic attack-surface review (STRIDE)

S = spoofing, T = tampering, R = repudiation, I = disclosure, D = denial of service, E = privilege escalation.

| Surface | Threats | Primary control and residual |
| --- | --- | --- |
| Identity login/session/logout/bootstrap | S/I/D/E | Generic failures; PBKDF2/fixed-time comparison; fresh random hash-at-rest sessions; expiry/logout/disabled checks; HTTP abuse limiter; one-time transactional bootstrap. Stolen valid token remains usable until expiry/revocation. |
| Admin APIs/RBAC | S/T/R/I/E | Current backend permissions and explicit inventory; UI hiding does not authorize; legacy module bearer is separate administrative authority. |
| Events/Jobs | T/D/R | Payload/result/attempt bounds, retry delay, dead letters, leases and audit. Disk capacity/retention remains an operational dependency. |
| Assets/Storage/Direct Play | T/I/D/E | Opaque IDs, per-asset public-read policy or permissions, rooted handles, streaming and range parsing. Public-read assets are intentionally public. |
| Packages/install/preflight | S/T/D/E | Grammar, graph/resource bounds, integrity, publisher trust/revocation and activation authorization. Trusted executable code is not sandboxed. |
| ModuleHost | S/T/D/E | Authenticated local protocol and supervision. Same service account does not imply privilege isolation. |
| Worker/coordinator | S/T/D/E | mTLS chain and pin/node binding, allowed capabilities, bounded protocol metadata and fencing. Inventory resource claims are advisory. Single coordinator is an availability risk. |
| Web registry/assets/actions | T/I/D/E | Schema 1 validation, no arbitrary URL/script, signed active PNG, CSP, current backend contribution/operation RBAC. PNG URLs can be public and are not a confidentiality mechanism. |
| Backup/restore | T/I/D/E | Exclusive lock, safe clean target, internal hashes and schema checks. Unencrypted backup exposure compromises hashes/session state/domain data. |
| Configuration/secret references | T/I/E | Strict settings, private-file checks, size limits; OS compromise and environment inspection remain outside boundary. |
| Diagnostics/health/errors | I/D/R | Safe aggregate readiness; module/diagnostic details require permissions; sanitized production error boundary/correlation IDs. |
| PostgreSQL | T/I/D | Parameterized data, bounded driver pool, authenticated production CI; DB administrator is trusted; DB TLS must be provisioned consistently. |
| FFmpeg/FFprobe/pg tools/service tooling | T/D/E | `UseShellExecute=false`, `ArgumentList`, local streams for media, bounded child policy and safely generated service files. Native parsers remain an attack surface. |

## Testable invariants

1. Revoked sessions never authorize after restart; disabled users fail each new validation.
2. Frontend permissions never replace backend RBAC.
3. Missing trust or activation authorization prevents executable package activation; revoked publisher cannot activate.
4. Stale execution generation cannot publish completion.
5. Unsupported future schema/format is never written as current state.
6. Invalid backup cannot replace valid target state.
7. Secret values are excluded from APIs, metrics labels and operational output.
8. Route, archive and storage IDs cannot escape controlled roots.
9. Missing/wrong-password/disabled authentication returns the same failure shape.
10. Authentication limits cannot be bypassed by an untrusted forwarded header; memory is bounded and limits recover.
11. Every registered HTTP endpoint has a reviewed explicit policy; unclassified routes stop startup.

## Residual risk register

| Risk | Handling |
| --- | --- |
| Trusted InProcess/authorized executable package compromise | Grant trust as code execution authority; isolate deployments at OS level where necessary. No hostile-code sandbox claim. |
| OS admin/service identity compromise | Restrict accounts and ACLs; rebuild/reprovision after compromise. |
| Unencrypted and unauthenticated backup provenance | Restrict access, transport/storage externally; verify expected origin through operator process. |
| Environment secrets and managed-string lifetime | Minimize exposure/lifetime; rotate after compromise; no vault/zeroization claim. |
| Single coordinator/no HA | Plan outage/recovery; no automatic availability guarantee. |
| No automatic CRL/OCSP infrastructure | Explicit pin rotation/revocation and PKI operations; do not equate pin state with X.509 revocation. |
| Distributed password spray and NAT contention | Process-local IP/account-pair buckets; network edge/operator monitoring. No Internet-scale DDoS protection. |
| Filesystem validation/use races | Private roots and restricted owners are assumptions; no immunity against an OS administrator swapping links. |
| Disk/log retention | Supervisor rotation and capacity monitoring; complete retention management deferred beyond Phase 21. |

No automatic promotion of Stable Candidate contracts. Public Stable remains 0; version axes remain 25.
