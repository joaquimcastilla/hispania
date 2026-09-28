# ADR-0023: PostgreSQL Library Persistence Provider

Status: Accepted (opt-in; live execution required in CI)

## Context

EF Core store logic should be provider-neutral, while SQLite owns its existing BLOB UUID schema and migration history. A second provider is needed to test database portability without rewriting or migrating an installed SQLite catalog.

## Decision

- `Hispania.Library.Persistence.EFCore` owns provider-neutral store logic and the shared relational model. Provider selection and schema lifecycle belong to a persistence adapter.
- Keep `Hispania.Library.Persistence.Sqlite` and its five migrations unchanged. SQLite remains the default provider.
- Add `Hispania.Library.Persistence.PostgreSql` using Npgsql, native PostgreSQL `uuid` values, a provider-owned initial migration, a PostgreSQL-specific unique-violation classifier, and a migration initializer.
- Select PostgreSQL only with `Persistence:Provider=postgresql` and an explicit `Persistence:PostgreSql:ConnectionString`. Migrations are additive and run only against the selected provider's configured database.
- Use exact public storage contracts (`ILibraryCatalogStore`, metadata store contracts and `IMediaCatalogReader`) for ownership; no consumer should access another provider's context or tables.

## Validation and consequences

Automated provider model and generated-migration tests run without a database server. The opt-in integration test uses `HISPANIA_POSTGRES_TEST_CONNECTION`, creates a unique temporary schema, checks failed-connection recovery, applies migrations from an empty schema, and compares reads/writes, uniqueness, transactions, concurrent imports and projections against a disposable SQLite database. CI provisions PostgreSQL 16 and requires the connection variable, so the integration Fact cannot be skipped there. This local environment has no PostgreSQL server/container runtime; live behavior remains pending a green CI execution. PostgreSQL remains opt-in and does not read, copy, convert or modify a SQLite database.
