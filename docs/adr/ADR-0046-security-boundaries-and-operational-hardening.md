# ADR-0046: Security boundaries and operational hardening

Status: implemented with Windows local regression; mandatory native/Linux/PostgreSQL acceptance tracked by Phase 21.

## Context

HISPANIA has identity/RBAC, immutable artifacts, upgrades, backup, production services, signed package lifecycle and declarative Web modules. Implicit safety needs explicit threat actors, assumptions, quantified resource bounds and reproducible abuse evidence. Phase 21 introduces operational hardening, not a new feature platform or a security-version axis.

## Decision

Use the durable threat model to distinguish anonymous clients, authenticated users, Platform administrators, workers, publishers, untrusted/compromised trusted packages, service accounts and OS administrators. Keep trusted InProcess code and OS administrators outside strong isolation claims. OutOfProcess is supervision/resource control under the service account, not a hostile-code sandbox.

Add bounded expiring process-local socket-peer and account/peer token buckets around authentication. Avoid a global account lockout usable as a denial-of-service weapon. Ignore untrusted forwarded headers; document reverse proxy aggregation. Bound authentication body/password input before expensive work. Maintain existing hash parameters, generic failures and backend permissions. Register permission metadata and explicit exceptional policies; reject unclassified endpoints at startup and expose the inventory only with Platform admin permission.

Keep package/archive/schema/protocol/resource bounds explicit. Use deterministic bounded mutation/property corpora with fixed seeds integrated into normal tests; no mandatory fuzzing infrastructure or nondeterministic extended CI fuzz. Every bug receives a permanent regression. Preserve safe command argument construction and existing service/resource defaults unless real tests justify tightening.

Certificate operations use explicit validity/expiry policy and pin rotation, with PKI assumptions separate from publisher revocation. No automatic CRL/OCSP, vault/HSM, encrypted backups, WAF, SIEM, HA or Phase 22 retention work is introduced. Record dependency advisories and deliberate artifact contents, separating reachable runtime from build-only impact.

## Consequences

Permissive callers can receive 413/429 or lose access to internal diagnostics. These are pre-1.0 security tightenings requiring operational/compatibility notes. Proxy/NAT aggregation and process restarts affect rate budgets. Residual risks include authorized code execution, OS compromise, plaintext backups/environment secrets, unmanaged timing/zeroization, single coordinator and disk/log capacity. Acceptance requires full existing gates and new bounded security evidence; documentation alone does not close a security invariant.
