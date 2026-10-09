# Phase 23 compatibility matrix

This matrix records only compatibility paths backed by checked-in historical
evidence or an executable gate. A passing targeted consumer or owner-store test
does not imply that an entire historical distribution is supported.

## Historical identities and release paths

| Source identity | Evidence and source SHA | Current target | Classification | Boundary |
| --- | --- | --- | --- | --- |
| Distribution 0.7 / Phase 14 technical baseline | Immutable Phase 15 manifest, 27 selected artifacts; source `78f291fdacf9442c6e2d8e23094dc0e309828693` | Selected current contracts, consumers and owner readers | **Supported** for the named surfaces | This is not a complete runnable release and does not create a Public Stable promise. |
| Populated 0.7 owner stores | Immutable Phase 16 manifest, 15 populated artifacts; source `78f291fdacf9442c6e2d8e23094dc0e309828693` | Current owner-specific SQLite migration paths | **Supported** for the owner paths and invariants exercised by Phase 16 | There is no cross-store atomic transaction. Media migration coverage follows its recorded EF lineage. |
| Phase 18 package reader | Historical reader source `86b43bc414edd53d8f7312a0d4045dc2818a39f6`, built and exercised by Phase 19 | Current package lifecycle | **Supported** for declared package-format/lifecycle scenarios | Not a full historical server install. |
| Phase 19 Web contribution reader | Historical reader source `da57b39149b5d297f3970c17899a4da9eb366eb7`, exercised by Phase 20 | Current Web contribution host | **Supported** for declared schema-1 contribution scenarios | Modern and Classic builds/browser tests cover the current host; not every old server/client pairing. |
| Complete releases for Phase 13 through Phase 22 | No authenticated, complete historical release snapshots in the accepted Phase 22 tree | Current release | **Untested** | Current source tests and selected artifacts are not complete historical installations. |
| Phase 17/18/21/22 complete historical backup bundles | No complete authenticated bundles for each of these release identities in the available corpus | Current restore | **Untested** as per-release historical bundle paths | Existing backup gates verify actual supported bundle/restore scenarios, not every named phase. |

## Compatibility axes

| Axis | Classification | Verified evidence | Untested or unsupported boundary |
| --- | --- | --- | --- |
| Public contracts, selected HTTP semantics and frozen consumers | **Supported** for selected 0.7 baseline cases | Phase 15 corpus and HTTP gates | No blanket endpoint stability or full old client distribution claim. |
| Populated SQLite owner schemas | **Supported** for Phase 16 fixtures and owner migration tests | Phase 16 historical-data gate, checksums, invariants, repeat-open and schema safety cases | No single transaction across stores. Other unrepresented historical shapes are **Untested**. |
| PostgreSQL live upgrade/restore | **Blocked** locally when no authenticated disposable PostgreSQL connection is configured | Test path and connection requirements exist | Must run on the configured authenticated PostgreSQL CI service before claiming live support for that path. |
| Configuration and secret handling | **Supported** for current Phase 18 configuration/deployment test cases | 28 configuration tests in the Phase 18 category | A complete installation using every historical configuration release is **Untested**. |
| Package lifecycle and rollback | **Supported** for tested signed package, predecessor reader, KnownGood and restore cases | Phase 19 and Phase 22 package gates | No signature bypass; unknown or unsupported formats are rejected. No claim for all historical packages. |
| Web contributions | **Supported** for tested declarative schema-1 cases | Phase 20 reader, validation, backup, builds and Modern/Classic browser scenarios | Arbitrary script/HTML contributions and unknown schema generations are **Unsupported**. |
| Worker wire protocol | **Supported** only for frozen selected protocol/consumer cases | Phase 15 and security gates | Full historical WorkerAgent executable matrix and all mixed-version coordinator/worker pairings are **Untested**. Do not assume support. |
| Identity, events, jobs, assets, audit, coordinator, inventory and media data | **Supported** for the exact populated owner fixtures and tested migrations | 15 immutable Phase 16 fixtures plus owner assertions | Unrepresented tables/versions are **Untested**. Cross-domain foreign keys and global atomicity are not promised. |
| Application binary rollback after data migration | **Unsupported** as a universal guarantee | Runbook and owner backup/restore tests | Restore a compatible pre-upgrade backup when the old binary cannot read the new schema. |
| Package rollback to KnownGood | **Supported** for the tested package lifecycle cases | Phase 19/22 package gates | Does not roll back database schemas or unrelated owner stores. |
| Physical power loss, storage controller failure, external filesystem races | **Untested** | Process termination/fault boundaries only | No physical power-loss or hostile-filesystem guarantee. |
| Windows SCM and Linux systemd installation | **Blocked** in this local portable run | Privileged service scenarios are assigned to native self-hosted runners | Require native runner execution; do not infer from Windows unit tests or a Platform build. |
| HA, multi-master, exactly-once side effects | **Not Applicable** to this phase's supported topology | Existing single-coordinator/lease semantics | Phase 23 adds no HA or exactly-once guarantee. |

## Reproduction and evidence

From the Phase 23 worktree, run:

```powershell
pwsh -NoProfile -File tools/test-phase23-e2e-compatibility-upgrades.ps1
```

The gate verifies all 27 Phase 15 and 15 Phase 16 artifact hashes before
executing its chained predecessor gates. Its output records the tested worktree
SHA under `.artifacts/phase23/`. A `WORKTREE_HEAD` value is resolved to the exact
HEAD by the gate; it is not a historical release SHA.

See [the Phase 23 overview](../phase23-e2e-compatibility-upgrades.md),
[the upgrade runbook](../operations/UPGRADE.md),
[the rollback runbook](../operations/ROLLBACK.md), and
[the harness guide](../testing/E2E-UPGRADE-HARNESS.md).
