# ADR-0044: Package dependencies and lifecycle

Status: Accepted for experimental implementation; local acceptance passes, with both OS CI gates required before phase closure.

## Existing boundary audit

At Phase 18 SHA `86b43bc414edd53d8f7312a0d4045dc2818a39f6`, `ModuleManifest.Id` is stable identity, `Version` is a CLR `System.Version`, and format generation is 1. `Implements`, `Provides`, `Requires`, `ContractTypes` describe public capabilities; `Dependencies`, `DependencyMinimumVersions`, `OptionalDependencies` describe module runtime dependencies. EntryAssembly/EntryType, SharedAssemblies, Runtime and RestartRequiredForUpdate describe loading. MinimumHostVersion/MaximumHostVersion retain the legacy host acceptance value, independent of distribution 0.7.0. Files maps relative paths to SHA-256; PublisherId/SignatureAlgorithm/Signature authenticate canonical metadata with RSA-PSS. Permissions are declarations, not grants. Installation validates bounded ZIP contents and stores inert version directories; catalog activation separately checks publisher policy and the host allowlist. Multiple inert versions exist; one runtime generation per module ID is supported. Discovery currently prefers the highest CLR version, without a durable resolved package graph.

## Decisions

Introduce format 2 with a separate strict SemVer 2 `PackageVersion` and required `PackageDependencies` (PackageId/VersionRange). Preserve `Version` as CLR entry descriptor identity. Format 1 cannot declare these fields. Null new fields are omitted so historical canonical signing bytes remain unchanged. Historical readers already reject any package format other than 1; no old consumer can silently accept format-2 dependencies. Keep the existing package-format axis and experimental status.

Use pinned NuGet.Versioning in Runtime for SemVer parsing/comparison and NuGet interval ranges, with strict SemVer bounds. This general parsing library has no domain or registry dependency; runtime resolution performs no network requests. Exact `[1.2.3]`, bounded `[1.2.3,2.0.0)`, lower `[1.2.3,)`, upper `(,2.0.0)` are the only accepted forms. Stable requests exclude prereleases unless a bound explicitly names one. IDs use bounded lowercase ASCII letters/digits/dot/hyphen/underscore and ordinal equality, rejecting platform-reserved path names.

One selected package version per ID forms an authoritative graph; retained inactive bytes are candidates only. Planning is side-effect free, resolves explicit roots and installed packages, rejects ambiguity and cycles, and uses deterministic descending version choices within bounded search. Package dependencies grant neither publisher trust nor execution authorization. Capability resolution remains owned by the existing runtime resolver.

Package set operations stage every archive before publishing a catalog. A durable journal and retained previous set support conservative rollback; filesystem/catalog/process changes are not ACID. Runtime activation must recheck current trust, host authorization and dependency health. Rollback is subject to current revocations and is distinct from deliberate downgrade. Module-owned schema reversibility remains the module owner's responsibility; generic package operations cannot downgrade databases. Existing leases and bounded draining are reused. InProcess remains trusted code; OOP remains fault/resource isolation, not a hostile-code sandbox.

No registry, downloads, scripts, dynamic Web, cluster package coordinator, worker deployment, arbitrary history retention or Public Stable promotion is introduced.
