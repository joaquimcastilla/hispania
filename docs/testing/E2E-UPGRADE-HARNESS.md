# End-to-end upgrade compatibility harness

## Purpose and boundary

The Phase 23 harness composes existing owner migration, compatibility, backup,
package, Web, security and maintenance gates. It does not add a production
migration service, promote contracts to Public Stable, or claim a complete old
server installation when only immutable owner/contract artifacts exist.

## Historical corpus

The accepted source tree contains two authenticated immutable manifests:

- Phase 15 selected compatibility corpus: 27 source, binary, wire, package and
  HTTP artifacts from source `78f291fdacf9442c6e2d8e23094dc0e309828693`.
- Phase 16 populated owner-data corpus: 15 fixtures from that source, with
  owner, immutability, SHA-256 and provenance metadata.

These are real historical artifacts and populated data, but not a runnable
historical server distribution. Phase 18 package and Phase 19 Web predecessor
readers are built from their exact source SHAs and tested against deterministic
local package fixtures; they are reader compatibility tests, not complete old
installations. There are no complete authenticated runnable releases for each
Phase 13–22 identity, and there is no full historical WorkerAgent binary
matrix.

## Run locally

From the Phase 23 worktree:

```powershell
python tools/phase23-compatibility-inventory.py .artifacts/phase23/fixture-inventory.json
pwsh -NoProfile -File tools/test-phase23-e2e-compatibility-upgrades.ps1
```

The inventory command is read-only with respect to the source fixtures. The
gate resolves the accepted Phase 22 base, verifies the manifests, records its
HEAD, then runs Phase 15, 16, 17, 19, 20, 21 and 22 scripts into separate
subdirectories. Phase 18 configuration, full solution tests/build, Platform
filter build, full Web tests/builds, browser tests and dependency audits are
additional acceptance commands and must be reported separately from the
dedicated gate.

To run full .NET tests, set `HISPANIA_PHASE19_OLD_READER` to the built Phase 18
predecessor reader produced by the Phase 19 gate. Run the Phase 18
`Category=Phase18ProductionDeployment` test category independently. The live
PostgreSQL, privileged Windows SCM and Linux systemd cases require their
respective configured runners and credentials.

## Isolation, failure injection and cleanup

The source fixture validator does not mutate fixtures. Existing gates create
their test stores/packages in isolated temporary roots or under the specified
artifact directory. SQLite migration tests use real child processes and
deterministic termination boundaries, then inspect schema/data recovery.
Package and backup tests exercise their actual managers/process hosts and
validated temporary roots. The gates retain evidence instead of deleting it.

Cleanup is limited to resources owned by the tests' generated roots; never pass
a personal installation, database or media library to a cleanup helper.
Synthetic fixtures and credentials are used. Do not put secret environment
values, tokens, private keys, or production data in captured reports. Physical
power loss, controller failure and every external filesystem race are not
simulated.

## Evidence and interpretation

`.artifacts/phase23/` is ignored local output. It contains the fixture inventory,
target SHA, summary, gate logs and TRX files, full .NET/Web outputs and audit
reports. Repeated gate runs overwrite same-named gate evidence; regenerate
checksums after the final run. Distinguish:

- **Passed**: discovered and passed by the recorded command.
- **Skipped**: discovered but skipped with an explicit reason, such as the
  PostgreSQL mixed-provider restore when no authenticated server exists.
- **Not run / Blocked**: prerequisites were unavailable; this is not a pass.
- **Untested**: no artifact or executable path currently represents the claim.

No complete historical release snapshots are present. PostgreSQL live,
privileged native service installation, cross-platform execution and physical
power-loss behavior must not be inferred from portable Windows test results.
See [the compatibility matrix](../compatibility/PHASE-23-COMPATIBILITY-MATRIX.md)
and the [Phase 23 overview](../phase23-e2e-compatibility-upgrades.md).
