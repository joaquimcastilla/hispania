# Phase 18 — Configuration, Secrets & Production Deployment

Implementation target: native Windows x64 and Linux x64. Distribution 0.7.0,
Public Stable 0, Backup Format Generation 1, Configuration Schema Generation 1,
24 independent version axes. Remote CI evidence is required before declaring
native service operation or live PostgreSQL validated on both operating systems.

## Ownership and installation layout

Host infrastructure owns strict operational configuration, private references,
logging and service lifetimes. Server composes existing owners; Platform gains no
Media/Inventory dependency. Store migrations and backup inspection remain with
their existing owners. ModuleHost remains a deterministic child, not a service.

```text
HOME/
  app/       complete verified artifact: server/, worker/, deployment/, operations/
  config/    production.json (public configuration)
  secrets/   protected plaintext references and private certificates
  state/     canonical Phase 17 managed installation root
    platform/  Identity/Authorization, Events, Jobs, Assets metadata, Audit SQLite
    inventory.sqlite, coordinator.sqlite, media.sqlite (SQLite provider)
    storage/, extensions/, transfers/, cache/, playback.json, metadata-index.json
    library/  default empty library mount; original media is not backed up
  logs/      Windows structured service logs; Linux uses journald
  backups/   verified offline bundles, including mandatory pre-upgrade backups
  run/       installation lease, interruption and last-upgrade records
  state.maintenance.lock  existing Phase 17 offline/runtime exclusion
```

Configuration and private material are outside `state/` and never captured by the
Phase 17 allowlist. Backup includes existing public portable owner settings. Keep
the public installation config with operator infrastructure records and reapply
it on a restored host; private references must be reprovisioned separately.

Production is explicit through `--home ABSOLUTE_PATH` or `HISPANIA__HOME`.
Published artifacts require that root and cannot silently enter development mode.
Development source builds retain their existing launch configuration. Paths derive
from HOME or the executable directory, never process cwd. Library mounts are
absolute; private certificates are relative to `secrets/`; capability assemblies
are relative to the worker artifact. Managed Storage/module/state paths deliberately
remain canonical for reproducible restore. No second mutable root override.

## Operator procedures

See [quick start](operations/PRODUCTION-QUICKSTART.md),
[configuration reference](operations/CONFIGURATION-REFERENCE.md),
[secret provisioning](operations/SECRET-PROVISIONING.md), and
[upgrade/rollback runbook](operations/UPGRADE-ROLLBACK.md).

Framework-dependent artifacts require the x64 .NET 10 ASP.NET Core runtime and
FFmpeg/FFprobe for the current Server Media composition. No SDK, Node, Python,
Visual Studio, Dev Launcher or repository is needed at runtime. Node/SDK/Git and
PowerShell are build-time prerequisites; the deployment CLI itself runs with .NET.
PowerShell 7 is needed only for the supplied Windows service administration script;
SCM can also be administered directly. Native Linux service administration uses
standard shell/systemd and privileged account provisioning.

Artifact manifest records SHA, RID, Distribution, build UTC, Release configuration,
SDK/runtime prerequisites and every payload checksum (including dependency files).
An external manifest checksum must be checked against a trusted delivery channel.
Manifest hashes establish integrity, not authenticity; no release signing or test
signing keys are shipped. Build UTC is metadata, not a compatibility axis. Dirty
local artifacts have separate build evidence and are not exact-commit releases.

Windows uses LocalService by default, explicit ACLs and SCM recovery (10s/30s,
then no restart; reset window one day). Custom non-admin accounts require explicit
SCM credential provisioning outside the script. Linux uses a dedicated `hispania`
user/group, bounded restart burst, NoNewPrivileges, PrivateTmp, ProtectSystem and
explicit writable paths. ProtectSystem requires precreated adjacent maintenance
lock. Original media mounts must be readable by the service identity.

`--check` validates strict public configuration, referenced secrets, permissions,
writable directories, published child/Web files and FFmpeg/FFprobe. It never
starts HTTP, opens/migrates stores or claims database connectivity/readiness.
Owner schema/connectivity validation occurs during actual startup. pg_dump and
pg_restore are required for PostgreSQL backup/restore, not normal Server startup.
Startup failures print bounded actionable diagnostics, without private exceptions.

HTTP defaults to fixed loopback 5080. Remote HTTP binding is rejected; provide a
private HTTPS certificate for remote listeners. Behind a local TLS reverse proxy,
keep the loopback listener; do not depend on unvalidated forwarded headers or
trust client-supplied identity headers. There is no ACME or automatic TLS claim.
Production clears legacy bearer/test settings, omits architecture demo modules,
test job handler, demo seeds and shutdown endpoint. Local `--bootstrap` provisions
the first operator through existing Identity/Authorization contracts. It requires
explicit username and two secret references. Remove provisioning references and
secrets after consumption. A failed attempt may leave a non-admin user; inspect
through owner operations rather than recreating identities manually in SQL.

## Upgrade, interruption and disaster recovery

Offline Server upgrade uses shared installation exclusion and Phase 17 maintenance
exclusion, verified pre-upgrade backup, `app.new` staging and entire-directory
replacement. Configuration, private material and state are preserved. `app.old`,
backups and failed state are retained for operator inspection. Windows filesystem
handle contention has a bounded retry while the same exclusive leases remain held.
An interruption marker blocks production startup until explicit recovery.

Before any new-host startup, binary recovery can reinstate the old artifact after
an interrupted staging/switch. After new-host startup, migrations may have happened:
rollback always requires backup restore plus old artifact, never just old binaries.
PostgreSQL recovery requires a separately provisioned empty target database and
matching libpq environment; original media mounts/private material are reprovisioned.
The restore-backed rollback command is for the managed Server installation. Worker
hosts do not own the complete Server database set; upgrading a standalone Worker
requires an offline binary/config preservation procedure, not a fabricated Server
backup. No hot replacement, remote admin installer or generic migration engine.

Service uninstall only unregisters the service. Binary uninstall retires app into
a retained directory; config, state, secrets and backups remain. No purge command.

## Required evidence

`tools/test-phase18-production-deployment.ps1` requires 28 configuration/secret
cases and 10 published-process cases without skips. CI additionally requires 2
actual native service cases (Server and Worker) and 1 password-auth live PostgreSQL
rotation/disaster case on Linux. Service definitions and actual operation must be
reported separately. Existing Phase 14/15/16/17, Platform-only, full solution,
Modern/Classic Web and closure/concurrency gates remain mandatory.

The upgrade predecessor is built from an archive of the exact validated Phase 17
SHA `c3a827a4b828cff37785884e889689af7cc5fce2`, with its existing explicit legacy
fixture configuration. It is compatibility evidence, not a newly invented release
or a claim that Phase 17 already had Phase 18 safety defaults. A future owner-history
fixture demonstrates old-reader rejection and restore-backed recovery without
claiming a new shipped schema. Frozen compatibility corpora are unchanged.

Limitations: local non-admin Windows cannot install SCM services; Linux execution
and live PostgreSQL require remote CI. Linux ARM64 remains experimental/unvalidated.
There is no vault, cloud KMS, HA, Kubernetes, package registry, signed public release,
mixed-version ecosystem guarantee, dependency lifecycle or Phase 19 implementation.

The production configuration remains authoritative for trust/signature authorization after importing portable backup settings. A restored owner provider that disagrees with production persistence config fails explicitly. Reapply public publisher keys/allowlists from the operator config record; a legacy backup cannot disable production signature enforcement or implicitly enable demo extensions.

Durable config/installation.json records installed Distribution/SHA/RID, initial install and update UTC timestamps, and artifact-manifest digest. Startup checks that record, current manifest and executing host payload agree before stores are composed. Mismatch rejects without silently rewriting evidence. Only verified install/upgrade/recovery updates metadata. PostgreSQL backups remain offline with respect to HISPANIA; stopping its service does not control external database clients. Coordinate external writers under the existing Phase 17 consistency limitation.

Production publication compiles out development demo composition and excludes ArchitectureTest assembly dependencies. Published hosting is compiled as production even if a manifest is missing, so an incomplete delivery cannot fall back into development. Generic ModuleHost/Worker retain their existing experimental SDK contracts without enabling sample modules. The publisher explicitly publishes RID-specific children and bypasses the development output-copy target; artifact contents do not depend on cached builds for another RID.

## Local acceptance evidence — 2026-10-06

- Full and Platform builds: zero warnings/errors. Full .NET regression: 670 passed,
  zero failed, two existing live-PostgreSQL infrastructure skips (672 total).
- Final published Windows gate: 28 configuration/secret cases and 10 real published
  process cases, all passed without skips. Includes Modern/Classic HTTP, consumed
  offline bootstrap and reprovisioned clean-root restore, pinned mTLS Worker with
  an explicitly enabled FFmpeg provider, exact archived Phase 17 A to distinct B,
  verified pre-upgrade backup, interruption recovery, failed future-schema startup,
  restore-backed rollback, metadata mismatch rejection and retained-data uninstall.
- Phase 14: 3/3; Phase 15: 16/16; Phase 16 portable: 36/36; Phase 17 portable:
  45/45. Web: 74/74 and both production builds. Closure/concurrency: 210/210
  including 20 stress repetitions. Frozen historical corpora remain unchanged.
- Both win-x64 and linux-x64 publications generated and passed manifest verification.
  Linux was cross-published on Windows, not executed locally. PowerShell definitions
  parsed and Linux installer passed `bash -n`; those are definition checks.
- Local published processes run from unrelated cwd and roots containing spaces and
  Unicode. They use .NET runtime APIs and artifact payloads; the machine still has
  its build SDK installed, so this is not a physically SDK-free VM certification.
- Actual SCM/systemd Server/Worker install/start/restart/stop/uninstall and Linux
  password-auth PostgreSQL A-to-B rotation, old-password rejection and Host B
  disaster recovery are mandatory new CI gates, **not executed locally**. CI remains
  pending because push is not authorized. Windows local portable shutdown uses a
  forced process stop; graceful Windows service shutdown is covered by the SCM gate.
- `npm audit --omit=dev` reports zero runtime dependency vulnerabilities at this
  check. An existing high-severity build dependency advisory reported by archived
  Phase 17 `npm ci` is not resolved by this phase; no blanket security certification.

Manifest SHA, Distribution, RID, UTC build time and Git SHA are authoritative in
each artifact's `artifact-manifest.json` and adjacent evidence files. Local artifacts
are marked `sourceDirty=true` because preexisting out-of-scope user documentation
changes are retained. They are local verification evidence, not signed releases or
an assertion of byte-for-byte reproducibility from a clean commit. CI publishes from
its checked-out source and checks all mandatory cases without silently accepting skips.

Phase 18 is ready for CI; supported native operation is not yet validated in CI.
