# ADR-0031: Platform Identity Foundation

## Status

Implemented for Phase 13.1; local validation and GitHub Actions Validation run 36777214916 passed on Linux and Windows at SHA `d3b680cf9555b90107fb4ea819b2b92655695648`.

## Context

HISPANIA Platform needs a single owner for application users, password credentials, and sessions. Domain suites must consume identity through versioned Platform contracts and must not own duplicate user or session stores. Phase 13.1 establishes authentication only; it does not establish general authorization.

## Decisions

- Identity is a Platform capability. Public models and `IIdentityQueryV1` / `IIdentityCommandV1` live under `contracts/platform/identity`; policy is in `application/identity`; the SQLite adapter is in Platform persistence. Media and Inventory have no Identity dependencies.
- Usernames are trimmed and normalized to lowercase using invariant casing. The supported syntax is 3–64 ASCII letters, digits, dots, underscores, or hyphens. SQLite enforces uniqueness on the canonical username. Thus `Alice`, `ALICE`, and `alice` refer to one username. Display names preserve user casing and can be edited independently.
- Passwords are 12–1024 .NET characters. No composition rules are imposed. Credentials use .NET PBKDF2-HMAC-SHA256 with a random 16-byte salt, 600,000 iterations, and a 32-byte output, following OWASP's current PBKDF2-HMAC-SHA256 iteration recommendation ([Password Storage Cheat Sheet](https://cheatsheetseries.owasp.org/cheatsheets/Password_Storage_Cheat_Sheet.html)). The persisted algorithm identifier and iteration count allow future versions/cost increases. The runtime's standard cryptographic primitives are used; no password or credential value is logged or audited.
- Sessions use a cryptographically random 256-bit URL-safe opaque token. The raw token is returned only at login. SQLite stores a SHA-256 lookup hash of the token, never the token itself. Since tokens have 256 bits of random entropy, this hash is used as a lookup derivation rather than as a password hash.
- Session lifetime defaults to 12 hours and is configurable with `Identity:SessionLifetimeMinutes` (bounded to a positive value and at most 365 days). Expired, revoked, or disabled-user sessions fail validation. Logout atomically records revocation in SQLite.
- Identity uses its own `identity.sqlite` file under `Platform:DataPath`; an explicit override is available at `Identity:Sqlite:Path`. Users, credentials, and sessions are separate tables with primary/unique constraints and foreign keys only inside the Identity store. Schema creation is idempotent and does not drop existing data.
- Session HTTP authentication uses `Authorization: Session <token>`. The existing `Authorization: Bearer <admin-token>` remains the temporary administration mechanism for creating users and existing administrative/domain operations. A legacy admin token is never accepted as a session token; session validation is limited to Identity routes in this phase.
- Audit records cover user creation, enable/disable, display-name/password changes, login success/failure, logout, and session revocation. Login failures return one generic client error and audit no attempted username. Audit metadata contains no password, hash, or session token. Identity forwards the HTTP correlation ID to Audit.
- The service performs the same PBKDF2 work for unknown usernames using a dummy credential to avoid a trivial fast path. There is no complete rate limiter or brute-force defense in 13.1; that remains explicit follow-up work.

## HTTP surface in 13.1

- `POST /api/identity/users` — create user; protected by the legacy admin bearer.
- `POST /api/identity/login` — public username/password login; generic 401 on all failures.
- `GET /api/identity/me` — returns current user and session; requires the `Session` authorization scheme.
- `POST /api/identity/logout` — revokes the presented session.

These routes demonstrate Identity and do not replace legacy authentication on the rest of the API.

## Deferred to Phase 13.2 or later

General authorization, roles/permissions, enforcement on Media/Inventory, Web Shell auth/navigation, replacement of admin bearer auth, authenticated self-service password change, reset flows, MFA, OAuth/OIDC/SSO, rate limiting, refresh tokens, and multi-store or distributed session coordination.

## Consequences

Identity is persistent across process restarts and independently testable. SQLite uniqueness is the last defense against concurrent duplicate usernames, and session revocation is an atomic conditional update. The first implementation is a single-server SQLite store; deployments requiring coordinated multi-instance writes need a later persistence strategy.
