# Coding Standards

## Backend

Preferred:

- C#
- nullable enabled;
- warnings treated seriously;
- async for IO;
- CancellationToken propagated;
- records/immutable structures when appropriate;
- dependency injection;
- structured logging;
- explicit DTOs;
- explicit domain models.

Avoid:

- static global state;
- service locator pattern;
- giant manager classes;
- arbitrary helper classes;
- controllers with business logic;
- catching `Exception` without reason;
- silent failure;
- database access from unrelated modules.

## Frontend

Preferred:

- TypeScript strict mode;
- React;
- typed API client;
- feature-based organization;
- state kept near ownership;
- accessibility;
- keyboard/remote-navigation compatibility.

Avoid:

- implicit `any`;
- duplicated server business logic;
- direct DB assumptions;
- hardcoded server implementation details.

## Contracts

Contracts should:

- be minimal;
- express capabilities;
- avoid implementation-specific types;
- support cancellation where relevant;
- use stable value objects;
- document error behavior;
- be versionable.

## Testing

Use:

- unit tests for pure behavior;
- integration tests for boundaries;
- contract compatibility tests for plugin interfaces;
- end-to-end tests for critical user flows.

Do not mock everything.

Prefer testing real module boundaries where feasible.
