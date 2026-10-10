# ADR-0043 — Configuration, secrets and production deployment

Status: implemented; local portable gates passed; native service and live PostgreSQL CI evidence pending.

## Decision

Production publications always select production hosting and require an absolute
`--home` argument or `HISPANIA__HOME`. Source/development builds also recognize
those explicit selectors. The existing development host remains separate. A shared host
infrastructure component owns operational configuration and secret resolution;
Server remains the composition root and domain/store owners retain migrations.

Installation layout: `app/` contains immutable published binaries, `config/`
contains public configuration, `secrets/` contains protected private material,
`state/` is the Phase 17 managed installation root, and `logs/`, `backups/`,
`run/` are explicit mutable siblings. Paths never derive from process cwd.

Configuration Schema Generation 1 is a strict typed JSON contract. Defaults are
overridden by the file, then `HISPANIA__` environment settings, then explicit
command line settings. Unknown fields and invalid ranges fail startup. Unsupported
generations fail without rewriting configuration. This introduces an independent
24th version axis; Distribution remains 0.7.0 and Public Stable remains 0.

Secret references resolve through environment or protected plaintext local files.
Permissions are a filesystem boundary, not encryption or a vault. Resolution
distinguishes missing material without printing values. PostgreSQL passwords,
bootstrap credentials and certificate passwords never belong in public config.
Private material lives outside the Phase 17 managed root and is reprovisioned
after disaster restore. Public trust configuration follows existing owner policy.

Server and WorkerAgent share hosting lifetimes for Windows SCM and systemd.
Dedicated non-administrator accounts read app/config and write mutable state;
private files grant only the account and operating-system administrators access.
Finite crash recovery and graceful bounded shutdown use the OS service manager.
ModuleHost remains a child resolved from the artifact, never an independent service.

Supported artifacts are framework-dependent win-x64 and linux-x64 publications.
Hosts require the .NET 10 ASP.NET Core runtime; publishing requires SDK and Node,
but running does not require SDK, source, Node or Python. Artifacts include both
Web builds, child binaries and checksummed dependency/source metadata. Checksums
detect corruption and do not establish publisher authenticity.

Upgrade is offline: verify stopped writers, validate/stage the new artifact,
produce and verify a Phase 17 backup, record an interrupted-operation marker,
then replace the entire app directory. Install/upgrade/restore coordination must
respect the existing exclusive maintenance boundary. No running binary replacement.
Before migration an old artifact can be reinstated. After any schema migration,
rollback requires restoration of the verified pre-upgrade backup plus old binaries;
forward-only migrations cannot be undone by replacing executables. Uninstall
preserves configuration, secrets, state and backups by default.

## Consequences and validation

Clean-host gates must use isolated published artifacts, unrelated cwd, paths with
spaces/Unicode, real readiness and process restart. Service-definition validation
and actual service execution are recorded separately. Live PostgreSQL rotation,
reprovisioning and historical recovery are mandatory Linux CI gates. Existing
Phase 14–17 compatibility and regression gates remain mandatory. ARM64, public
release signing, cloud secret stores and Phase 19 are outside this decision.
