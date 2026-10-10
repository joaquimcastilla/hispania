# Configuration Schema Generation 1

UTF-8 JSON at `HOME/config/production.json`; exact camelCase names, unknown fields
reject. Missing optional fields retain typed defaults. No schema migration; an
unsupported generation rejects before store startup. Precedence: typed defaults
< file < `HISPANIA__` environment < `--set=FIELD=VALUE`. Explicit `--home` overrides
`HISPANIA__HOME`; duplicate explicit roots reject. Other production CLI keys reject.

Examples: `HISPANIA__ENDPOINT=http://127.0.0.1:5081`,
`HISPANIA__WORKER__MAXIMUMCONCURRENTJOBS=4`,
`--set=webBuild=classic`. Boolean/numeric overrides use JSON scalars; arrays and
maps use whole JSON values. The independent secret environment namespace is
`HISPANIA_SECRET_NAME`; it is not a public configuration override.

| Field | Default / validation |
| --- | --- |
| generation | 1; other generations reject |
| role | server; must equal executable server/worker |
| endpoint | http://127.0.0.1:5080; explicit fixed-port origin, remote HTTP rejected |
| webBuild | modern or classic |
| logLevel | Information; Warning/Error/Critical also allowed; no verbose production profile |
| bootstrapSecret | null; env:NAME or file:NAME |
| bootstrapUsername | null; explicit local provisioning identity |
| bootstrapPasswordSecret | null; explicit initial password reference |
| ffmpeg / ffprobe | PATH commands; absolute paths also accepted, cwd-relative paths reject |
| persistence | sqlite or postgresql |
| postgreSql.host / port / database / username | localhost / 5432 / hispania / hispania; port 1..65535 |
| postgreSql.passwordSecret | required for PostgreSQL; reference, never connection string/plaintext |
| tls.certificateFile / passwordSecret | secrets-relative PFX and optional password reference; HTTPS requires PFX |
| libraryRoots | []; canonical empty state/library default; configured mounts must be absolute/available |
| trustedExtensionIds / revokedPublisherIds | []; explicit package authorization/revocation |
| trustedPublisherKeys | {}; public keys only; signatures always required in production |
| worker.coordinatorUri / nodeId | required for worker; HTTPS and bounded identity |
| worker.certificateFile / passwordSecret | private secrets-relative PFX and optional password reference |
| worker.coordinatorCertificateSha256Pins | required worker array of explicit 64-hex pins |
| worker.ffmpegEnabled | false; explicit opt-in Media remote provider, requires FFmpeg |
| worker.contractAssemblies | []; explicit assembly names |
| worker.capabilityProviderAssemblies | []; explicit artifact-root-relative DLL paths |
| worker.maximumConcurrentJobs | 2; 1..64 |
| worker.leaseSeconds | 30; 5..300 |
| worker.pollMilliseconds | 500; 50..60000 |
| worker.heartbeatSeconds | 5; 1..300 and below lease duration |
| capacity.reserveBytes | 1073741824; nonnegative volume safety reserve |
| capacity.warningFreeFraction / criticalFreeFraction | 0.15 / 0.05; free-space fractions with 0 < critical < warning < 1 |
| maintenance.jobIdempotencyHorizonDays | 365; 1..3650 days for pruned Job key tombstones |
| maintenance.orphanGraceHours | 24; 1..720 hours before owned content enters logical quarantine |
| maintenance.backupArchiveRoot | null; optional absolute managed bundle archive outside the installation root; required for backup retention |

Mutable roots are fixed relative to HOME for Phase 17 managed recovery. Media
SQLite uses state/media.sqlite; PostgreSQL remains a separately provisioned owner
database. Logging goes to journald or Windows logs/server.jsonl / worker.jsonl;
operator retention/rotation remains OS-managed. Do not put databases in config/.

Exit codes: 0 success; 2 invalid configuration; 3 missing/insecure secret; 4 startup,
data/store or backup recovery failure; 5 missing dependency/publication/permissions;
6 running installation/conflict/interrupted operation. `--check` does not promise
connectivity, migration compatibility or readiness; actual startup validates them.

For Media workers, explicitly select worker.capabilityProviderAssemblies = ["server/Hispania.Transcoding.FFmpeg.dll"] and worker.ffmpegEnabled = true. The complete delivered artifact supplies that provider and its dependencies; its child resolves to app/server/ffmpeg-worker and its mutable cache to state/cache. The generic WorkerAgent project still has no Media implementation reference. No test capability provider is enabled automatically.
