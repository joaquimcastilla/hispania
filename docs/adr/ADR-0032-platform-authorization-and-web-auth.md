# ADR-0032: Platform Authorization and Web Authentication

## Status

Implemented for Phase 13.2; local validation passed; CI pending.

## Context

Phase 13.1 provides persistent Platform users and opaque sessions. The platform needs general authorization and a Web Shell login without coupling Media or Inventory to an authorization implementation.

## Decisions

- Versioned public contracts live in `contracts/platform/authorization`; authorization policy lives in `application/authorization`; the SQLite adapter lives in Platform persistence and shares `identity.sqlite` with Identity. Role, role-permission, user-role, and one-time bootstrap state tables have explicit uniqueness and foreign-key constraints.
- Roles have stable IDs, normalized unique names, descriptions, and timestamps. Permission identifiers are validated strings rather than a closed enum. Platform publishes built-in permission name constants; other modules can use their own namespaced identifiers without adding to a global enum.
- Effective permissions are the union of permissions assigned through a user's roles. Missing grants deny by default. Disabled users receive no permissions even while a persisted session exists. The explicit `platform.admin` grant covers `platform.*` only. No role hierarchy, deny rules, ABAC, or module-domain policy is introduced.
- The initial administrator is provisioned by a one-time bootstrap endpoint. An operator supplies a high-entropy secret of at least 32 UTF-8 bytes through `Identity:Bootstrap:Token`; there is no default secret. The caller first creates a normal user through the existing administrative path, then submits that user's ID with `Authorization: Bootstrap <secret>`. A single SQLite transaction creates the reserved administrator role, grants built-in Platform permissions, assigns the role, and consumes the bootstrap state. The secret is fixed-time compared, is never returned or audited, and bootstrap cannot be repeated. Remove the configured secret after use. The operation refuses a disabled/nonexistent user.
- Protected Platform endpoints use one reusable endpoint filter: `Session` token → Identity validation → enabled user → current role grants → permission decision. Authorization is calculated per request; changing a role assignment or permission affects existing sessions immediately. Missing/invalid session returns 401; a valid session without permission returns 403. Denials are audited without session tokens.
- `Authorization: Bearer <Modules:AdminToken>` remains a temporary compatibility mechanism for endpoints deliberately protected by this filter. It is checked distinctly before session authorization, bypasses permission evaluation only on those routes, and emits a deprecation warning. It is not accepted as an Identity session and does not automatically authorize every endpoint.
- Web stores the opaque session token in `sessionStorage`, which is scoped to the browser tab and cleared on logout or failed validation. `localStorage` is not used. The token is sent only in the `Authorization: Session` header; no cookie is created, so cookie-based CSRF is not part of this design. Same-origin deployment and HTTPS in production remain important. XSS could still read a sessionStorage token; a future HttpOnly cookie deployment would need a deliberate CSRF and origin policy.
- The shared Web Shell uses one auth context for current user, session and effective permissions. Login calls Identity, fetches `/api/identity/me`, and logout revokes server-side before clearing client state. Administrative links are permission-gated and direct route access is rejected in the shell; endpoint authorization remains authoritative.
- User and role administration are deliberately limited to Platform user status/role assignment and role name/description/permission management. Deleting roles is omitted to avoid reference and recovery ambiguity. Media/Inventory API migration is deferred to Phase 13.3.

## Protected HTTP surface

- `POST /api/identity/users`: `platform.users.write` (temporary legacy bearer also accepted).
- `/api/authorization/users*`: read routes use `platform.users.read`; status and role assignment routes use `platform.users.write`.
- `/api/authorization/roles` GET: `platform.roles.read`; create/update use `platform.roles.write`; permission edits use `platform.roles.permissions.write`.
- Platform asset upload/delete use `platform.assets.write`; Platform audit uses `platform.audit.read`.
- Identity login, current-user lookup and logout retain their Phase 13.1 session semantics. The bootstrap endpoint is separately guarded by the one-time bootstrap secret.
- Existing Media and Inventory APIs are not migrated in this phase. Their existing administration paths are not evidence of fine-grained authorization.

## Consequences and limits

The design keeps persistence and policy behind Platform contracts and makes RBAC changes visible to active sessions. The legacy bearer still has broad administrative reach on explicitly filtered routes until a future removal phase. Session tokens remain exposed to same-origin script execution. There is no rate limiting, MFA, recovery flow, OAuth/OIDC, distributed session coordination, tenant authorization, or complete protection of Media/Inventory operations in this phase. See Phase 13.3 for domain API authorization.
