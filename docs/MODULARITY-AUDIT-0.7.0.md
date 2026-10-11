# HISPANIA modularity audit — 0.7.0

Audit baseline: public RC1 source `9b5abefe4c6b631360d505587748aedcb6c522d0`; audit work is kept on `audit/modularity-0.7.0`. RC1 remains unchanged. This is a source and targeted-test audit, not a stable release declaration.

## Result

**Overall: PARTIAL. Stable release gate: NOT PASSED.** The signed external-package probe passes through the supported extension API in an isolated ASP.NET TestServer. The shipped Server still composes Media and Inventory directly and has no domain-only distribution, which fails the stated domain-independence requirement. No stable tag or release should be created from this audit result.

The project name is **HISPANIA — Home International Server Platform for Applications, Networking, Integration & Automation**. The repository ships Platform mechanisms, Media, a bounded Inventory proof, selected trusted extensions, Web shells and workers. ERP, industrial/home automation and AI are possible domains, not currently shipped applications.

## Matrix

| Category | Result | Evidence and limits |
|---|---|---|
| Core independence | PARTIAL | `Hispania.Platform.slnf` is intentionally buildable without Media/Inventory implementations; Platform contracts and Runtime contain no direct references to those implementations. `apps/server/Hispania.Server.csproj` is the composition root and directly references current application suites. No general cycle is evident in the dependency graph, but the packaged Server is not a domain-neutral core distribution. |
| Public contracts | PASS | Versioned contracts live under `contracts/`; capability selection checks exact contract IDs/versions. `docs/compatibility/contracts.json` and compatibility policy list version axes. These remain pre-1.0/experimental, not a stable SDK. |
| External modules | PASS | `tools/test-modularity-probe.ps1` independently publishes the greeting module, packages signed v1/v2 archives and passes them to the extension admin-route integration test. The test uses an isolated TestServer/catalog, not a running published Server executable. |
| Packaging | PASS | The probe runs `dotnet publish` separately from Server and uses `tools/package-extension.ps1` to add payload digests and RSA-PSS signatures. Output package hashes are emitted; each run uses an ephemeral private key. Format 2 adds SemVer package identity and dependencies. |
| Installation | PASS | `ExtensionPackageInstaller` bounds ZIP contents, checks paths, hashes, host/contracts and stores bytes inertly. The admin upload accepts request bytes, not network URLs. `ExtensionAdminHttpIntegrationTests` covers authenticated upload/rejection. |
| Activation/deactivation | PASS | `ModuleExtensionCatalog` checks module allowlist and publisher policy, starts under runtime supervision, drains leased calls, supports unload. HTTP test invokes greeting, deactivates, observes conflict, reactivates and requests unload. |
| Update | PASS | HTTP integration exercises a v2 candidate whose startup fails (v1 continues responding), then a compatible v2 switch. Lifecycle tests validate drain and staged graph changes. |
| Rollback | PASS | HTTP test uses explicit switch back to installed version in the legacy lifecycle; Phase 19 tests exercise package-set rollback after update/faults while rechecking current trust. Data/schema rollback remains module-owned and is not automatic. |
| Revocation | PASS | Signed-package tests cover unknown, trusted, invalid and revoked publisher behavior; package graph tests verify revocation stops dependent execution. Operator settings are the durable trust source; live API changes alone are in-memory. |
| Dependencies | PASS | Format 2 uses bounded local SemVer resolution, deterministic graph planning, cycle/missing/version conflict checks, explicit activation ordering and dependency-first removal rules. No registry, download resolution, optional/peer/dev graph or worker auto-deployment. |
| Web extensibility | PARTIAL | Phase 20 provides signed declarative schema and fixed authorized operations shared by Modern/Classic. It is Experimental, has no executable JS/HTML/CSS and does not sandbox malicious package content. |
| Isolation | PARTIAL | Contract/data ownership is logical; collectible ALC supports unload; ModuleHost/WorkerAgent provide process/failure boundaries and selected resource controls. In-process native code inherits Server privileges; out-of-process shares OS identity. Neither is a hostile-code sandbox. |
| Security | PARTIAL | Admin mutation routes require a configured bearer; package trust, signatures, revocation, allowlists, bounded archive validation and worker mTLS exist. The probe sets `requirePublisherSignature: true`, configures its generated public key and the module allowlist; unsigned content installs inertly but activation is rejected. Native extension code still has broad process privileges. |
| Domain independence | FAIL | Inventory owns its store and does not reference Media, and Platform filter builds without either. But the production Server composition/package contains Media and Inventory; Inventory is directly composed, not an optional installable module. There is no verified ERP-only/automation-only distribution. |
| Worker integration | PASS | Generic agents advertise authorized capability, authenticate via mTLS and use lease/generation fencing; the coordinator selects allowed matching workers. Semantics are at-least-once, single coordinator/SQLite, no HA or generic AI distribution claim. |
| Developer documentation | PARTIAL | Module System documents public contracts and workflow; this audit adds a concise author guide. Compatibility and lifecycle generations remain experimental/pre-1.0, with no guarantee of broad third-party SDK stability. |

## Practical probe and test evidence

The strict probe builds the greeting extension separately, packages v1 and compatible v2 through the official tool, configures an ephemeral public key plus module allowlist and `requirePublisherSignature: true`, then exercises authenticated module admin routes. It proves unsigned content remains inert and cannot activate, a signed extension can execute its public greeting contract, a failed v2 start leaves v1 serving, successful v2 activation works, explicit version rollback works, deactivation blocks calls, reactivation works, unload succeeds, both package versions are removed and the API still responds with no discovered extension. The probe is a disposable TestServer using the production endpoint mapping and catalog, not the full published Server composition.

Targeted checks run locally on Windows with .NET SDK 10.0.401:

| Check | Result | Notes |
|---|---|---|
| `./tools/test-modularity-probe.ps1` | PASS, 1/1 | Builds independent module v1/v2, uses official packager and ephemeral RSA key, then exercises signature policy, authenticated install, activation, greeting, failed candidate, update, rollback, deactivate, reactivate, unload, uninstall and post-removal API response through TestServer. Archive SHA-256 values are printed; private key is deleted. |
| `./tools/test-phase19-package-lifecycle.ps1 -ResultsDirectory .artifacts/modularity-audit-phase19` | PASS, 76/76 lifecycle + 2/2 backup | Covers graph, signed packages, revocation, rollback, crash/recovery and historical reader SHA `86b43bc414edd53d8f7312a0d4045dc2818a39f6`; no network package resolution. Local SDK only. |
| `dotnet test tests/unit/Hispania.Runtime.Tests.csproj --no-build --filter FullyQualifiedName~ModuleInfrastructureTests` | PASS, 32/32 | Module lifecycle, external catalog trust, unload, OOP worker crash recovery and capability contracts. |
| `dotnet test tests/inventory/Hispania.Inventory.Tests.csproj --filter FullyQualifiedName~PlatformDependencyTests` | PASS, 4/4 | Verifies Platform project reference closure excludes domain implementations and checks assembly references; also documents Server references to Inventory/Media. |
| `dotnet build Hispania.Platform.slnf -c Release` | PASS, 0 warnings/errors | Built Platform, ModuleHost and generic WorkerAgent without Media or Inventory implementations. |
| Local Markdown link check + `git diff --check` | PASS | All local links in changed Markdown files resolve; no whitespace errors. |
| Full published Server executable after physical uninstall | NOT TESTED | TestServer stays alive and its authenticated extensions API responds after both packages are removed; no separate production-bundle process was used for this extension probe. |
| Self-hosted CI on this audit SHA | NOT TESTED | No GitHub-hosted runner was used. |

The signed module probe and package lifecycle tests pass. They do not upgrade the overall result to PASS because the separate requirement that applications be installable without unrelated domains fails in the current Server distribution.

## Architecture findings

- Platform contracts, generic runtime, storage/events/jobs/auth foundations are separate from domain implementations. The `.slnf` is a build boundary, not evidence that each domain can be installed independently.
- The production Server is a modular monolith and composition root. It currently composes Media and Inventory in one application; the distribution does not offer an Inventory-free/Media-free core profile.
- The `.hispania` extension path is real: inert package install, metadata/discovery, operator authorization and publisher trust, runtime activation, leases, update, rollback, uninstall and dependency graph are distinct operations. Format 2 package management remains experimental.
- Module permissions in manifests are declarations. Host allowlist and publisher signatures establish operator authorization/provenance, not per-extension OS capability restriction.
- Web package content is declarative and fixed-operation; it cannot supply backend modules in JavaScript.
- WorkerAgents execute allowlisted capability contracts with mTLS and stale-generation fencing. They do not make all modules distributed or provide exactly-once, coordinator HA or an AI execution network.

## Required work before stable 0.7.0

1. Resolve the domain-independence failure by providing evidence and packaging that permits domain-optional operation, or narrow the product claim/acceptance criteria explicitly before a future audit. No architectural shortcut is proposed here.
2. Run the relevant audit branch gates on the self-hosted runner for the final source SHA; do not use GitHub-hosted runners.

No version bump, stable tag, stable GitHub Release or RC1 mutation is made by this audit.
