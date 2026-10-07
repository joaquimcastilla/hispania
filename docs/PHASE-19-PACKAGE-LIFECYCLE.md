# Phase 19 — Package Lifecycle & Dependency Management

Local implementation is ready for CI on `feature/phase-19-package-lifecycle-dependencies`, from exact validated Phase 18 SHA `86b43bc414edd53d8f7312a0d4045dc2818a39f6`. Implementation commit: `03b80bbc017ece1b3ee9464e84bbbc52829f8693`. CI must run both OS gates before closure. No push, PR, merge, tag, release or Phase 20 is authorized. Preexisting ROADMAP and MODULE-MATURITY-AUDIT edits are excluded.

## Package model and historical safety

The complete predecessor manifest audit and architectural decisions are in [ADR-0044](adr/ADR-0044-package-dependencies-and-lifecycle.md). Format 1 retains its CLR module `Version`, ID, signature canonicalization and frozen Phase 15 artifacts. Format 2 adds nullable `packageVersion`, `packageDependencies` and `requiredModuleHostProtocol`; all are authenticated metadata. Format-1 declarations of these new fields are rejected. Null fields are omitted from signing serialization so old signatures remain valid. Package identity is `id`, not an archive filename; package SemVer is independent of CLR descriptor identity. Format 2 requires a package version and dependency array, even when empty.

New IDs use lowercase ASCII letters/digits/dot/hyphen/underscore, at most 128 characters, ordinal equality, an alphanumeric first character and no trailing dot or Windows device name. They never select arbitrary extraction paths. Historical IDs are not renamed. New SemVer strings are strict and at most 128 characters; legacy CLR versions are not silently rewritten. A four-part historical version with nonzero revision requires explicit migration before graph operations.

The current reader accepts formats 1 and 2. The independent test driver references Runtime/contracts compiled from the exact Phase 18 source commit, runs in another process and proves its installer rejects format 2 before writes or execution. This is historical-source consumer evidence, not a claim that the current implementation represents the old reader. Phase 15's immutable package additionally runs through the managed set and restart path. Its corpus is never regenerated.

Distribution remains 0.7.0, the version-axis count remains 24, Backup Format remains 1, Configuration Generation remains 1 and Public Stable remains zero. Package format changes an existing axis. Required ModuleHost protocol uses its existing generation; host bounds retain their legacy interpretation and do not become distribution dependencies.

## Local resolution and planning

Runtime uses pinned NuGet.Versioning 7.9.0 for strict SemVer, comparison and interval parsing, not package fetching. See the [NuGet version/range specification](https://learn.microsoft.com/en-us/nuget/concepts/package-versioning). Accepted forms are exact `[1.2.3]`, bounded `[1.2.3,2.0.0)`, lower `[1.2.3,)`, upper `(,2.0.0)`. Bounds must themselves be strict SemVer; wildcards, caret, tilde, npm comparator expressions and whitespace are rejected. Build metadata does not change precedence. Prereleases require an explicitly prerelease bound.

Only the current authoritative set and explicitly supplied local archives participate. Unchanged installed roots are pinned. An explicit target selects the highest compatible candidate with deterministic ordinal tie breaks; unused files appearing in a directory do not trigger upgrade or enable. One selected/active package version per ID is supported; inactive bytes may be retained for rollback. Same ID/version with different archive hashes is ambiguous and rejected. Installed disabled dependents still block removal. There is no cascade uninstall.

Planning verifies archive integrity without staging, installing, executing, granting trust/authorization or writing a catalog. `PackageOperationPlan` exposes current/selected candidates, install/update/downgrade/unchanged/uninstall changes, dependency-first activation and reverse deactivation orders, and findings. Missing/conflicting dependencies, self/duplicate edges and cycles fail before mutation. Limits are 32 dependencies per package, 128 selected packages, depth 32, 512 candidates and 8,192 search steps. Exhaustion fails explicitly; this is not an unbounded optimal solver.

Package dependencies and exact public capability requirements remain separate. The existing ModuleRuntime, CapabilityResolver and IPC boundary remain responsible for contracts; the package manager hands no private implementation object to a dependency consumer. Platform gains no Media or Inventory implementation dependency.

## Lifecycle, recovery and security

`ApplyPackageSetAsync` rechecks the plan against current state, stages and verifies every new archive before stopping the previous set, drains existing leases under the configured request-duration bound, unloads old ALCs/stops old ModuleHosts, publishes the selected catalog, activates dependencies first, and finalizes only after health gates. Coordinated major updates stop the old graph before starting the new graph. Admitted leased calls may finish; universal zero interruption is not promised. Disposed leases release their contract reference. Consumers must not retain raw contract objects or completed module tasks beyond the lease if they require collectible unload.

Installed, publisher-trusted, host-authorized, enabled and runtime-healthy are separate decisions, exposed independently by package diagnostics. An inert install may remain unauthorized/disabled; permissive signature policy can retain unknown/unsigned publishers but cannot execute format-2 packages without a trusted signature. Enabling A requires B to be enabled, individually authorized/trusted, active and healthy. No implicit authorization occurs. Revoking B stops its dependent graph first under the existing revocation policy; new calls validate dependency health/trust. Cross-publisher dependencies never inherit grants. Publisher-ID takeover during update is rejected; this phase has no ownership-transfer policy. Existing explicit publisher-key administration is retained.

The protected `.package-state` directory owns generation-1 catalog, journal, previous set and content-addressed original archives. Catalog entries include ID/version, manifest, artifact digest, resolved pinned graph, enabled state and an authorization snapshot. Snapshots never override current host policy and contain no private keys/secrets. Operation states are Planned, Staged, Draining, Committed, Activating, Validated, Completed, RollingBack and Failed. Atomic file replacement plus flushed journal writes supports process-crash recovery; filesystem/catalog/process changes are not one ACID transaction and no power-loss filesystem guarantee is claimed.

Interrupted operations conservatively restore the previous graph. Current and old signatures, revocations, authorization, payload integrity and health are checked again. An unsafe rollback leaves an explicit incomplete/blocked journal, with mutation refused until recovery. Corrupt/future catalog or journal is not reset or deleted. There is an in-process mutation gate and an exclusive filesystem package-operation lock; diagnostics remain readable. Stale plans cannot overwrite a newer operation.

Rollback returns to a previously operational set and retained bytes. Deliberate downgrade additionally requires explicit consent and module-owned data compatibility; this manager performs no database/schema downgrade. Operators must use verified Phase 17/18 installation backup/restore when owner migrations require it. `CleanupPackageHistoryAsync` is an explicit cleanup retaining current and previous sets, refusing active-generation removal and leaving unrelated drop-in files alone. No arbitrary history manager is introduced.

ZIP compressed/expanded/entry limits, payload hashes, path/link rejection and bounded manifests remain enforced. There are no install scripts or shell callbacks. InProcess is trusted native code and OOP is fault/resource isolation; neither is a hostile-code sandbox. Metrics have no ID/version labels: operation success/failure, resolution failure, rollback count and duration. The optional existing Platform audit writer records bounded install/update/uninstall/enable/disable/rollback/failure outcomes, without archives, publisher keys or exception bodies.

## Operator and developer guide

There is no new package CLI or HTTP mutation surface. These are actual application-service APIs, usable by explicit host/operator composition; they are not fictitious command names:

```csharp
var plan = await catalog.PlanPackageSetAsync(
    ["/operator-supplied/base.hispania", "/operator-supplied/consumer.hispania"],
    new Dictionary<string, string> { ["test.package.consumer"] = "[1.0.0]" });
// Inspect plan.Findings, Changes, Selected and ActivationOrder before applying.
await catalog.ApplyPackageSetAsync(plan); // inert install
var enable = await catalog.PlanPackageSetAsync([], new Dictionary<string, string>());
await catalog.ApplyPackageSetAsync(enable, new HashSet<string>
    { "test.package.base", "test.package.consumer" });
await catalog.RollbackPackageSetAsync(); // current trust/authorization still apply
await catalog.RecoverPackageSetAsync(); // conservative interrupted-operation recovery
await catalog.CleanupPackageHistoryAsync();
```

Before enabling, the operator separately configures each `trustedExtensionIds` entry and appropriate `trustedPublisherKeys` in protected Phase 18 configuration. Effective legacy keys are `Modules:TrustedExtensionIds`, `Modules:TrustedPublisherKeys`, `Modules:RevokedPublisherIds`; revocations remain authoritative. No dependency operation edits these grants. Existing live trust administration remains in memory; durable operator decisions must also be persisted in protected configuration before restart/backup. Format 2 execution always requires a trusted publisher signature, including when the permissive historical format-1 policy is configured. To disable, apply a plan with the desired enabled subset (empty disables all). To update, supply local archives plus explicit ranges for changed roots; incompatible isolated changes fail, compatible coordinated changes use one plan. To uninstall, pass a `remove` set to planning; all installed dependents must be removed explicitly in the same plan first. Logical uninstall removes membership; retained rollback bytes are cleaned only by the explicit retention operation. Legacy single-module mutations are refused for managed IDs. Adopting an existing legacy installation requires its original archive and explicit unloading of its legacy runtime generation first.

`GET /api/platform/admin/modules/packages/graph` reuses Session-only `platform.admin` protection and exposes package versions, dependencies, dependents and bounded status without paths, keys or payloads. Existing Modern/Classic pages remain unchanged. Diagnostics distinguish Resolved, MissingDependency/VersionConflict findings, UnauthorizedDependency, UntrustedDependency, DependencyUnhealthy, RecoveryRequired and HostIncompatible/ProtocolIncompatible. During draining, cutover or incomplete recovery, new managed contract leases are refused; already admitted leases retain the existing bounded drain policy. Optional incompatible packages remain installed and blocked; the server's hosted recovery logs a sanitized warning instead of granting them execution.

Developer format-2 example (TEST ONLY; omitted entry/digest/signature fields must be supplied by packaging):

```json
{
  "id": "test.package.consumer",
  "version": "1.0.0",
  "packageVersion": "2.0.0",
  "packageFormatVersion": 2,
  "packageDependencies": [
    { "packageId": "test.package.base", "versionRange": "[2.0.0,3.0.0)" }
  ],
  "requiredModuleHostProtocol": 1
}
```

The previous Runtime CLR constructor signature is retained through a forwarding overload; selected Stable Candidate API baselines remain unchanged. The neutral signed test corpus is generated with ephemeral TEST ONLY keys: base 1.0/1.1/1.4/2.0, consumer 1.0/2.0, an unhealthy consumer and negative publisher/protocol variants. No publishing private key is committed. Capabilities still declare their exact public contract identities in `provides`/`requires` and CLR contract maps; package edges do not create implementation access.

## Integration and validation

Phase 17 generation-1 backups capture installed/previous archive bytes, pinned graph, journal, enabled state and protected public trust/revocation settings. Verification rejects missing dependency bytes, graph/manifest disagreement and unresolved/incomplete journals before restore/activation. Relative catalog paths allow clean-root restore. Package operations are distinct from distribution upgrades and reuse the existing installation maintenance boundary; no cluster/worker auto-deployment is added.

Mandatory `tools/test-phase19-package-lifecycle.ps1` runs strict resolver tests, signed graph lifecycle, current trust/ownership tests, real ALC drain/unload, real OOP update/rollback, fault injection and process termination at journal boundaries, plus the independently compiled historical reader. Validation workflow invokes it on Windows and Linux before full regressions. The additional Inventory/host test exercises real offline installation backup/destroy/restore and corrupt dependency rejection. The existing production deployment gate also requires two Phase 19 artifact-only distribution-switch cases: a signed historical-format package remains selected after offline backup and switch, and a controlled host-bound incompatibility keeps the optional package blocked while the published server serves requests. The compatible case now runs the exact archived Phase 17 predecessor c3a827a4b828cff37785884e889689af7cc5fce2 with the signed format-1 package, executes its public greeting contract, upgrades to the current publication, verifies package health in the real published server and recovers the same pinned graph. The incompatible case uses controlled host bounds without inventing a distribution version. Phase 18's separate populated predecessor upgrade/rollback test remains required; distribution stays 0.7.0. Phase 14–18, full/Platform .NET, Web builds/tests, closure/concurrency and live Linux PostgreSQL remain required gates.

Local acceptance on 2026-10-07:

| Gate | Result |
| --- | --- |
| Phase 19 resolver/lifecycle/historical reader/real crash | 71/71 passed, no skips |
| Phase 19 real installation backup/restore | 2/2 passed, no skips |
| Phase 19 published distribution switch/blocked optional package | 2/2 passed, no skips |
| Complete .NET suite | 743 passed, zero failed, two existing PostgreSQL infrastructure skips |
| Full solution and Platform builds | Zero warnings/errors |
| Phase 14/15/16 portable/17 portable regression cases | 3/16/36/45 passed within the complete suite |
| Phase 18 configuration and published processes | 28/28 and 10/10 passed, no skips |
| Web and Modern/Classic builds | 74/74 tests passed; both builds succeeded |
| Closure and 20 concurrency repetitions | 210/210 passed |
| Frozen corpus | 27 fixtures; both historical hashes unchanged; Python negatives 10/10 |

The bounded resolver measurement was 67.328 ms for 81 packages/80 edges on this machine; it is not a performance guarantee. Two additional negative ZIP cases confirm .NET 10 bounds a forged entry length and rejects its truncated manifest in both inspection and installation before any destination is created. Local logs/TRX are retained under ignored `.artifacts/phase19-*`; publications and the detailed local report are under `.artifacts/phase19/`. Local publications preserve the actual `sourceDirty` flag; preexisting user documentation means they cannot be claimed as reproducible clean release artifacts.

Production `npm audit --omit=dev` reports zero vulnerabilities. Full build audit still reports one high development advisory in source-map-js 1.2.1 in the current build, not only the archived predecessor. No lockfile or frozen artifact was rewritten in this phase.

Linux execution, native SCM/systemd and live PostgreSQL are CI requirements, not locally claimed passes. Registry/marketplace/download discovery, optional/peer/dev dependencies, dynamic Web, arbitrary scripts, hostile-code sandboxing, HA package coordination and automatic Worker deployment are outside scope. This is readiness for CI, not Phase 19 closure.
