# Security operations

Use authenticated backend operations and the local deployment tools already shipped. Do not put secrets in command strings, incident tickets, output captures or metric labels. Audit is append-oriented application history, not cryptographically immutable or tamper-proof evidence. Preserve relevant records under restricted access before remediation.

## Account incident

Identify the user through authorized Identity administration, inspect role assignments and Audit, and disable the account. The SQLite store atomically disables the user and revokes all existing sessions; later session creation requires enabled status. Each subsequent session validation checks disabled status; already accepted in-flight work may finish. Logout revokes one session. Re-enabling does not resurrect revoked tokens. Current V1 lacks a separate general revoke-all API, so use the supported disable-account incident control, then change its password and restore least-privilege roles only after the compromise is resolved. Protect the last enabled administrator invariant.

## Certificate expiry and rotation

Maintain an inventory of server TLS, worker client and relevant trust-anchor certificates. Review `NotBefore` and `NotAfter` in UTC at least daily; policy warns within 30 days, rejects expired/not-yet-valid material for authentication. Track public fingerprints/dates only. Current runtime expiry diagnostics coverage is listed in the phase evidence; do not assume an alert exists without that evidence.

Provision replacement B and its private file under the service identity's restricted secret directory. Authorize B's SHA-256 pin for the exact worker node using the existing certificate rotation operation. Stop the worker, replace its certificate reference/material, restart and verify reconnect plus a normal authorized job. Verify A fails authentication after pin replacement. For failed rotation, restore the last known valid file and deliberately restore the expected pin; never broaden trust or accept any certificate to recover. Server TLS rotation requires restart and endpoint/chain verification. PKI trust anchors may require host trust-store updates. Pin revocation is distinct from CRL/OCSP; HISPANIA does not operate a revocation infrastructure.

## Package incident

Record publisher/package/version/digest and dependent graph using authorized module APIs. Revoke the publisher or package authorization, disable/unload affected modules, inspect dependents and active registry. A trusted compromised InProcess module may already have compromised the server: revocation does not undo execution. Quarantine/rebuild the installation and rotate reachable secrets where warranted. Roll back only to a separately verified trusted package; re-evaluate graph and registry, then run normal/negative operation checks. Never execute an untrusted package as an investigation step.

## Backup exposure

Treat exposure as sensitive data compromise: backups contain credential hashes, session state, DB/domain content and assets. Restrict access to captured evidence, identify who received copies, invalidate exposed sessions, rotate reachable credentials/keys, and assess offline password cracking risk. Backups are unencrypted; internal SHA consistency does not authenticate provenance. Verify origin independently before restore. Restore under the offline exclusive lock into a clean target, verify semantics and append subsequent Audit records. Never overwrite valid state with an unverified bundle.

## Secret exposure

Revoke/rotate the actual secret immediately: session token → revoke session/account; bootstrap → remove/reprovision before use; worker key → replace certificate and pin; publisher key → revoke publisher and inspect dependents; DB password → rotate coordinated service/DB configuration. Search restricted logs, CI artifacts, backups and repositories for the exposed value without printing it. Redaction does not reverse an exposure. File providers store plaintext; environment variables can be read by privileged local processes; neither is a vault.

## Monitoring and audit

Monitor low-cardinality login failures/successes, authentication rate-limit rejections, verification/protocol failures, worker availability, dead letters and readiness changes. Do not use IP, username, token or arbitrary package IDs as metric labels. Repeated 429s, unexpected publisher changes, unusual privilege grants and lost worker identity are operational signals requiring investigation. Login requests rejected by the limiter do not append one audit row each. Permission-filter denial Audit has a process-wide burst of 100 and refill of one event/second. Every denial still increments `hispania.security.authorization.denied`; suppressed rows increment `hispania.security.audit.denial_suppressed`. Successful administration Audit is unaffected. This sampling loses individual denial details during floods; preserve aggregate metrics and investigate promptly. Metrics and budgets reset at restart. Restrict Audit read permission and supervisor log access. Configure journald retention/rotation or the Windows service log collector; monitor disk space. No SIEM/WAF/IDS or automatic alert platform is provided.

## Vulnerability handling

Permission-filter Audit denial rows and legacy module-admin rejection warnings share the same process-wide sampling budget; aggregate metrics count every denial. Do not interpret a missing individual row during a flood as evidence that no attempt occurred.

Reproduce privately using local test processes, classify impact, fix the affected boundary, add deterministic permanent regression, document compatibility tightening and release notes, and re-run affected plus full acceptance gates. Critical/High reachable runtime findings block release. Medium findings need a mitigation and scheduled fix; Low findings need triage; Informational observations document assumptions. Build-only advisories require reachability/supply-chain assessment rather than automatic disregard. Dependency upgrades are deliberate, pinned and tested; CI does not auto-update.
