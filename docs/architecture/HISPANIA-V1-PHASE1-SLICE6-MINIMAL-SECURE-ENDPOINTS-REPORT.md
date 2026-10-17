# HISPANIA v1.0 — PHASE 1 SLICE 6 REPORT

## A. STATUS

**PASS — bounded to the independent Core on Windows x64.** Slice 6 adds HTTPS-only loopback routes for liveness, local login, session logout, and the current identity. Authentication reuses Slice 5 credentials and persistent sessions. Cookie/CSRF, origin, rate-limit, audit, restart, and published-process checks passed. This is not an Internet-exposure or public-distribution approval.

## B. ROADMAP

The owner-controlled high-level roadmap remains **4/14 — 28.6%**. Phase 1 Slices 1–6 are complete at their bounded scopes; Slices 7–10 remain unauthorized. No Module Manager, SDK, modules, Coordinator, Workers, Media, or administration API was started.

## C. PREFLIGHT

- Worktree: `C:\Users\joaqu\Documents\HISPANIA-v1-baseline-consolidation`.
- Branch: `codex/hispania-v1-baseline-consolidation`.
- Starting HEAD: `432dd6b4f554d3ff0e28b2892ae6d1adc34960c8` (Slice 5 closeout); worktree clean.
- Slice 5 report and EV-046 were read. The provider-neutral identity/session contracts, SQLite v2 store, and services were reviewed. Bootstrap already used local stdin and no HTTP route; it remains a local CLI command and does not load the HTTPS certificate or start the listener.
- The 15 original worktrees matched their inventory branch, HEAD, and pre-existing status counts; 16 worktrees were registered including this authorized worktree. All 49 preserved live-source and 49 backup SHA-256 values matched. No original worktree or backup was changed.
- `LICENSE.md` SHA-256: `75EAE29E043C82B168894AFAAA148ADE2E9A43B4A72513FF18CFD22434F9BD3D`.
- No Git operation was in progress. No upstream is configured.

## D. HTTP ARCHITECTURE

The HTTPS route composition is in `apps/core/Http/CoreAuthEndpoints.cs`; input validation, origin checks, cookie/CSRF processing, session service calls, JSON results, and correlation handling are separate from SQLite. Login throttling is ASP.NET Core middleware. HTTP audit uses a narrow service and the existing provider-neutral identity-store boundary; the SQLite adapter writes only through the existing audit table. Handlers contain no SQL or business queries.

The Core remains independent, bound to loopback, and has no dependency on `apps/server`, Media, Inventory, modules, or workers. No HTTP framework or NuGet dependency was added.

## E. ENDPOINTS AND JSON CONTRACTS

| Route | Access | Contract / result |
|---|---|---|
| `GET /health/live` | Public | `200 {"status":"healthy"}`; liveness only |
| `POST /api/v1/auth/login` | Public, same-origin HTTPS required | Request `{"username":"…","password":"…"}`; on success `200 {"subjectId":"…","username":"…","csrfToken":"…","expiresUtc":"…"}` plus session and antiforgery cookies |
| `GET /api/v1/auth/me` | Valid session cookie | `200 {"subjectId":"…","username":"…","sessionExpiresUtc":"…","permissions":[…]}`; only the current identity and its explicitly granted Core permission identifiers |
| `POST /api/v1/auth/logout` | Valid session and CSRF proof | `204`, revokes the session and expires the cookie |

Login errors use the same `401 {"error":"authentication_failed"}` for unknown, disabled, and incorrect-password accounts. Other stable error codes are `authentication_required`, `invalid_request`, and `internal_error`. Malformed JSON is rejected with 400, a body over 4 KiB with 413, invalid credentials are not reflected, and unimplemented/admin routes remain 404. Sensitive responses set `Cache-Control: no-store`; responses also set `Pragma: no-cache`, `X-Content-Type-Options: nosniff`, `Referrer-Policy: no-referrer`, and a generated `X-Correlation-ID`.

## F. AUTHENTICATION AND SESSIONS

Login delegates to Slice 5's local identity service and ASP.NET Core Identity V3 PBKDF2-HMAC-SHA512 verifier. The endpoint accepts a maximum 64-character username and 256-character password; Kestrel caps request bodies at 4 KiB. Successful login creates a random 256-bit opaque session with a 12-hour HTTP lifetime. SQLite stores only the SHA-256 digest, expiry, and revocation state. The existing service accepts at most 30-day sessions, but HTTP does not expose a renewal route.

Every `/me` and logout request validates the persistent session against expiry, revocation, and active account state. Tests covered disabled accounts after login, expiry, restart persistence, logout, and replay rejection. Login and session creation introduce no public registration, password reset, account listing, or role-management surface.

## G. HTTPS AND TRUST BOUNDARY

The Core listens only on configured loopback and now requires an explicit absolute `HISPANIA_CORE_HTTPS_CERTIFICATE_PATH` for HTTP hosting. The optional password is supplied by `HISPANIA_CORE_HTTPS_CERTIFICATE_PASSWORD`; it is not retained in the options record or logged. Startup rejects missing, relative, reparse-point, unreadable, expired, private-key-less, or non-TLS-server certificates. There is no plaintext HTTP listener. A cleartext probe against the published TLS port did not receive an HTTP response.

Slice 6's topology is a browser/client connecting directly to the local Core over HTTPS. It does not terminate TLS at a proxy. The Core does not enable forwarded-header middleware or trust `X-Forwarded-*`, `Forwarded`, `Host`-derived scheme overrides, or client-provided origin/scheme hints. A future reverse proxy must be separately specified and cannot use the current direct-origin policy without a deliberate trust-boundary design. Loopback alone is not treated as authentication; access still requires a local account and session.

HTTPS integration tests used a temporary localhost/IP-SAN certificate and pinned its exact thumbprint. Published-process smoke used curl with that certificate as an explicit test trust anchor (`--cacert`), not an insecure TLS bypass. Production must configure an operator-managed certificate trusted by its clients. No remote-browser or Internet-facing deployment is claimed.

## H. COOKIES, CSRF, ORIGIN, AND CORS

Authentication uses one mechanism: an opaque cookie named `__Secure-HispaniaSession`, with `Secure`, `HttpOnly`, `SameSite=Strict`, `Path=/api/v1/auth`, no `Domain`, and the 12-hour expiry. The cookie is not returned in JSON, and no bearer-token or localStorage transport exists.

Login requires an HTTPS `Origin` exactly matching the request origin, including host and port. Successful login returns an ASP.NET Core antiforgery request token in JSON and sets an HttpOnly, Secure, SameSite=Strict antiforgery cookie on the same restricted path. Logout requires the same-origin check and the request token in `X-Hispania-CSRF`; cross-origin and missing-token logout attempts were rejected. `me` is read-only. No CORS policy is registered, so no cross-origin access is granted.

## I. RATE LIMITING AND ENUMERATION

The published process applies a fixed-window limit of 20 login attempts per minute per socket peer address, with no queue and a `Retry-After` response. The limit is configurable within validated bounds using `HISPANIA_CORE_LOGIN_RATE_LIMIT` and `HISPANIA_CORE_LOGIN_RATE_WINDOW_SECONDS`. The Core has no trusted-proxy configuration and never reads forwarded IP headers. Behind a local reverse proxy all requests would share its loopback partition; per-client proxy attribution is unsupported until a trusted-proxy design is approved. The current limiter is process-local and resets when the process restarts.

No account is permanently locked by another identity's failed attempts. The per-IP temporal limit, generic authentication result, existing dummy-hash path, and input limits were exercised. A published-process test produced 20 generic 401 failures followed by 429 and `Retry-After: 60`; integration tests exercised concurrent rejection and recovery after the configured window. Username-specific lockout was not added because that would let a remote source deny service to a named account.

## J. AUTHORIZATION

Only liveness and login are anonymous. `me` and logout require an active, unexpired, unrevoked session. `me` is self-scoped and returns only the current account and permission identifiers that Slice 5 explicitly grants; a newly created user with no role received an empty permission list. Authentication does not grant administrative permissions. No administrative routes or development bypasses exist. Route tests assert exactly the four mapped application routes and anonymous access to `me`/logout is rejected.

## K. SQLITE AND LIFECYCLE

The existing SQLite v2 database, `ICoreIdentityStore`, and `core_security_audit` table are reused. No second session database, schema migration, or PostgreSQL Core provider was introduced. Persistence initialization still completes before listening; normal host stop ran the existing passive WAL checkpoint. Bootstrap is still a local CLI operation outside the HTTP listener. Session and audit data remain subject to the existing operator storage/backup responsibilities.

## L. AUDIT AND OPERATIONAL LOGGING

Persistent security audit event codes written to the existing audit table are `http.login.succeeded`, `http.login.rejected`, `http.login.rate_limited`, `http.session.logout`, and `http.session.rejected`. The table records event time and optional subject ID, never request bodies or credentials. Integration tests queried the database and verified login, logout, rejected-session, and rate-limit events.

Operational logs record event type and generated correlation ID, not username, password, session token, cookie, credential hash, or authentication request body. Rate-limit and failed-login logs are distinct from persisted audit rows. No distributed observability stack was added.

## M. CORE TESTS

`dotnet test tests/core/Hispania.Core.Tests.csproj -c Release --no-restore --results-directory "%TEMP%\hispania-slice6-20261017\results-core" --logger "trx;LogFileName=slice6-core.trx" -v minimal` — **44 passed, 0 failed, 0 skipped**. TRX SHA-256: `72687DDD6B80243E9E74A7FF135EAA50097DCB48298510B6EEB5ABC56FA04B82`.

Coverage includes real HTTPS login/me/logout, secure cookie attributes, CSRF and cross-origin rejection, unknown/wrong/disabled account behavior, malformed/oversized input, generic errors, no-cache/no-CORS, no bearer acceptance, default-deny permissions, disabled-account session rejection, logout replay, expiry, persistence across host restart, concurrent rate limiting and recovery, persistent audit events, route boundary, certificate requirements, existing identity/SQLite migration/lifecycle, and clean host shutdown.

## N. VALIDATOR AND REGRESSION

- Core artifact-validator suite: **26/26 passed**. TRX SHA-256 `8BC5DF56AB3330288504E797E938583D3A5858F1084E149A03AA0D9CEBC5427A`.
- Slice 1 selected Inventory/Platform: **29/29 passed**. TRX SHA-256 `FFCD3327C89AF70E4EB5A17C618000DEAAEE5AB3C81888ABD2D0E2CC53882010`.
- Deployment/Hosting: **35/35 passed**. TRX SHA-256 `7AB322401792F8D88621C2EE47595C2EC7C3832811D96827564A345DC9A750E4`.
- Bounded Runtime/module selection: **19/19 passed**. TRX SHA-256 `17EBF0797E3A64F8CCB834BAF5B1FF134638F190913704C01B2D66D618FA49EE`.
- Selected regression total: **83/83 passed**. No claim is made about unrelated or complete Runtime suites.

## O. REAL PUBLISH AND PROCESS SMOKE

Real framework-dependent publish command: `dotnet publish apps/core/Hispania.Core.csproj -c Release -r win-x64 --self-contained false -p:PublishDir=<TEMP>\hispania-slice6-20261017\publish-win-x64\ -p:NuGetAudit=false -v minimal`. Runtime: `net10.0/win-x64`, .NET SDK 10.0.401 / runtime 10.0.12. The nine runtime files passed both artifact-validator `create` and `validate` against `HISPANIA-V1-CORE-ARTIFACT-ALLOWLIST-v1.json` with zero issues. Manifest content ID: `E002BC64356B274454708C638B55E06F1054B7E75110392907AE600EB014290E`; manifest SHA-256 `74470C40B2785CE06EEBE8AE3544DD7C667D0954655C321C5D87E226844A1640`. Its `sourceCommit` is implementation commit `dd9b174ba049dbc2f01cbf8ba611ae5eccb837c2`.

Published-process smoke used the real CLI and executable: local stdin bootstrap exited 0 without echoing the password; HTTPS liveness returned 200; login/me/logout returned 200/200/204; after logout and process restarts, the revoked session still returned 401; 20 failed login attempts returned 401 and the next returned 429 with a retry header. Ctrl+C shutdown logs showed `Core host stopping`, `Core SQLite passive checkpoint completed`, and `Core host stopped`. A sanitized summary is at `%TEMP%\hispania-slice6-20261017\published-smoke-summary.json`, SHA-256 `777571FB8E066EC92D43D81A1F4B670CE10F4DD53823FDA8F5A775D7E8FEC4C9`.

## P. DEPENDENCIES AND RIGHTS

Evaluated MSBuild graph: zero `ProjectReference`; one direct `PackageReference`, `Microsoft.Data.Sqlite` 10.0.12. The actual publish graph contains the existing `Microsoft.Data.Sqlite` 10.0.12 and SQLitePCLRaw 2.1.12 packages plus `Microsoft.NETCore.App` and `Microsoft.AspNetCore.App` shared frameworks. No package was added, so the existing allowlist remains unchanged. All 6 excluded unknown-rights SHA-256 values were compared with the published file manifest; **0 matched**. No Media/PostgreSQL, `apps/server`, FFmpeg, WorkerAgent, Coordinator, or excluded historical artifact entered the Core publish. `LICENSE.md` was not modified; package review and local validation do not grant distribution rights.

## Q. DOCUMENTATION AND EVIDENCE

Created this report and EV-047 in the Evidence Ledger. All TRXs, the real publish, manifest, and sanitized published-process summary are retained outside the repository under `%TEMP%\hispania-slice6-20261017`. Evidence is Windows x64 only.

## R. INTEGRITY AND PRESERVATION

`git diff --check` passed before the implementation commit. Changes were limited to Core HTTP/configuration/audit integration, Core tests, this report, and the Evidence Ledger. No `apps/server`, public/historical contracts, Media/PostgreSQL provider, license, or original preservation root was modified. The 15 original worktrees and 49 source/backup pairs were rechecked. No push, merge, tag, or release occurred.

## S. GIT

- Implementation commit: `dd9b174ba049dbc2f01cbf8ba611ae5eccb837c2` — `feat(core): add minimal secure auth endpoints`.
- The report and EV-047 are committed separately as documentation-only evidence.
- Branch has no upstream; no upstream was invented and no push was attempted.
- Final documentation commit and HEAD are recorded in the final Slice 6 closeout. The worktree is clean.

## T. DECISION AND STOP

**SLICE 6 PASS — STOP.** The minimal local HTTPS authentication API is implemented and boundedly verified. Residual limits include Windows-only execution, no independent Windows ACL audit, process-local rate-limit state, no trusted reverse proxy, no remote browser deployment, no refresh endpoint, and no Internet-security or distribution claim. Slice 7–10 remain unauthorized; do not start Slice 7 without a new owner prompt.
