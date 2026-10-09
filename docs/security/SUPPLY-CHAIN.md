# Dependency security review

Reviewed 2026-10-10 using the locked repository graph. Local outputs are under `.artifacts/phase21/`; they are execution evidence, not authenticated provenance.

NuGet `dotnet list Hispania.CrossPlatform.slnf package --vulnerable --include-transitive --format json --no-restore` reported no vulnerable packages against `https://api.nuget.org/v3/index.json`. This is a point-in-time feed result, not absence of vulnerabilities. The dedicated security gate audits the full solution on Windows and the portable solution on Linux. Final Windows audit reported zero advisories. npm uses the checked-in lockfile and `npm ci` with the configured registry.

npm runtime-only and complete audits report zero vulnerabilities after updating the locked transitive `source-map-js` package from 1.2.1 to the patched 1.2.2, which satisfies its existing `^1.2.1` dependency range. The lockfile diff changes only that package's version, registry URL and integrity hash; the application manifest and dependency graph are unchanged. The Phase 21 gate now rejects every npm advisory, including build-only findings. Earlier `.artifacts/phase23` audit outputs record the pre-fix state and are historical evidence, not the final audit.

Native FFmpeg/FFprobe and PostgreSQL tools remain operational dependencies. Production CI already installs/probes real tools; record their versions with each artifact acceptance. Argument construction uses structured arguments and local media streams, which does not remove vulnerabilities in native decoders.

Production publication intentionally removes development extensions, demo assets and development config and verifies the artifact content inventory. PDBs follow the existing publication policy; they are legitimate diagnostics, not an authentication control. Verify test keys/attack fixtures/source are absent using production artifact tests. Artifact hashes establish consistency and exact Git identity; no release signing infrastructure is invented. Known reachable runtime Critical/High advisories block release; build-only findings need explicit graph/reachability analysis.
