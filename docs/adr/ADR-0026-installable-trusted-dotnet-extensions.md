# ADR-0026: Installable trusted .NET extensions

Status: Accepted

## Context

Manifest-only discovery plus static host factories could not load independently compiled implementations. HISPANIA needs local package installation and a runtime loading boundary while keeping public contracts shared and ensuring directory contents never execute by discovery alone.

## Decision

- A `.hispania` package is a ZIP with a root `module.json`, `packageFormatVersion: 1`, entry assembly/type, compatibility bounds, versioned contract declarations, module dependencies, shared public contract assembly names, and SHA-256 hashes for every payload file.
- Package installation is local and inert. The installer bounds compressed size, entry count and expanded size; rejects unsafe paths, links, duplicates, missing/extra files, malformed identities, incompatible host/contract dependencies and hash mismatches; verifies the entry DLL metadata; and atomically moves a staging directory to `extensions/<module-id>/<version>` without loading code. Existing versions are never overwritten.
- Discovery parses metadata only. Activation requires a module ID in `Modules:TrustedExtensionIds` or the legacy host factory registry. A collectible `AssemblyLoadContext` plus `AssemblyDependencyResolver` loads the declared entrypoint only after authorization. Module-system and public `*.Contracts` assemblies resolve from the host default context and require exact assembly identity.
- The runtime registry has explicit removal for stopped modules. Extension calls use a lease that blocks new calls during drain and pins the instance until an in-flight operation completes. Deactivation stops the module; unload separately unregisters it and requests collectible-context unload; uninstall separately removes one inactive version directory.
- Update candidates are loaded, identity/contract-checked, started and health-checked while the old generation continues serving. The runtime then blocks new leases, drains old work, stops/unregisters the prior module and registers the candidate. On candidate startup or health failure, the old generation remains active. Prior package files remain installed for explicit rollback to that version.
- The admin API keeps bearer-token authentication. It accepts a local package body and does not fetch code from Internet sources. Package hashes detect accidental or in-transfer corruption; they are not signatures or proof of publisher identity. The allowlist and operator-controlled local package source define which code is trusted.
- In-process extension code has the server process's privileges. `AssemblyLoadContext` provides assembly identity separation and unloadability, not a sandbox. Extensions requiring security/resource isolation must use a separate worker process and a versioned IPC contract.

## Consequences

- The server does not reference or rebuild a concrete extension implementation. It does reference the public contract needed by a consumer.
- Built-in static module registration remains supported.
- Operations performed through the extension lease cannot overlap a generation cutover or be replayed by the updater. Module lifecycle callbacks themselves are extension code and must be designed with idempotent start/stop behavior.
- File replacement is versioned and rollback uses already-installed files. The updater does not automatically download code and does not claim process isolation.
- Package signatures, untrusted extensions, WASM and generic process worker hosting remain future work.
