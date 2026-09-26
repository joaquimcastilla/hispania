# ADR-0027: Package provenance and process extension boundary

Status: Accepted, implemented with documented operational limits

## Context

Phase 7 introduced inert `.hispania` packages with SHA-256 payload digests and trusted in-process .NET activation. A digest detects a mismatch against the manifest but does not identify the publisher. The FFmpeg worker proves a dedicated process can serve one capability, but its protocol and lifecycle are specific to transcoding. General extensions must remain contract-driven and may not access another module's implementation.

## Decisions

- Keep `InProcess` and `OutOfProcess` as explicit manifest/runtime choices. Existing packages continue to use their declared in-process runtime; migration to a process is never implicit.
- Keep package install, publisher trust/authorization, activation and execution as separate decisions. Installing a package stores validated inert files. Activation requires both host policy approval and a supported runtime.
- Sign a canonical manifest (excluding signature fields) and all payload digests with RSA-PSS-SHA256. Trust roots are operator-managed publisher IDs and support explicit revocation. SHA-256 remains the payload-integrity check. Trust administration endpoints update process memory; durable trust must be supplied through protected host configuration.
- Define a public major-versioned request/response protocol with startup identity and protocol negotiation, capability inventory, correlation IDs, bounded frames, typed errors, cancellation, health checks and graceful shutdown. In-process and out-of-process capability adapters implement the same consumer contract.
- Use a private local IPC channel (`NamedPipeServerStream`, backed by local pipe transport on supported platforms) with current-user access and a per-launch random authentication secret supplied through the child environment. Never bind extension IPC to a public network interface.
- Make process start, timeout, cancellation, stop, exit detection, bounded restart/backoff and resource limits supervisor policy. A crash must degrade only that implementation and must not terminate the host or trigger an unbounded restart loop.
- Treat process separation as fault/resource containment only. Security sandbox claims require separately restricted OS identity, filesystem, secrets, network and handles.

## Implementation and verification boundary

Implemented: inert package installation; canonical RSA publisher signatures and trust/revocation checks; explicit `dotnet-inprocess-trusted` and `dotnet-outofprocess` selection; collectible `AssemblyLoadContext` loading; a generic module host; protocol V1 negotiation, capability discovery, correlated calls, bounded JSON frames, cancellation, health and graceful stop; process monitoring and bounded restart/backoff; update/rollback; and configurable request/concurrency plus OS-specific best-effort resource ceilings. Automated runtime tests cover signature tampering/revocation, protocol framing/negotiation, dual greeting implementations, cancellation/timeout, child crash, unrelated-module continuity, update/rollback and administrative revocation.

Phase 9 audit update: local Windows execution successfully attached a Job Object for the generic OOP greeting recovery test when mandatory enforcement was enabled, and the six FFmpeg worker/recovery/media tests passed on Windows. The OOP Windows Job Object now sets the documented job-time flag and a bounded active-process limit. Still not verified: GitHub Actions for this uncommitted tree, PostgreSQL live integration, Linux `prlimit` runtime behavior, and the proposed Windows/Linux changes on CI runners. The Ubuntu workflow will run the Linux generic worker and FFmpeg tests through their OS resource adapters; a future green run is required before claiming Linux validation. Tests not executed on a platform are not considered passed.

Security regression found and fixed during Phase 9: publisher revocation previously consulted the preferred discovered manifest rather than the manifest belonging to the loaded version. The loaded generation now retains its exact signed manifest; reactivation validates that generation again and revocation deactivates it even when discovery prefers a newer version. Tests cover post-install DLL mutation, signed manifest mutation, unknown-editor update rejection, and revocation after a newer package has become the discovery preference. This does not bind global trusted publisher IDs to specific module namespaces; operators must only trust publisher keys authorized to publish within the host's extension policy.

Trust endpoints are in-memory for the current server lifetime; configure trusted keys/revocations in protected operator settings for durable policy. Process isolation is not a security sandbox: child and server share the OS identity and potentially filesystem, environment, secrets, and network permissions. `AssemblyLoadContext` is likewise not a sandbox. V1 dispatch supports simple JSON-compatible operations with at most one non-cancellation parameter; richer payload schemas and streaming are future work.

## Consequences

- The host consumes stable capability contracts independent of deployment mode.
- Third-party package identity can be evaluated independently from package integrity and host execution permission.
- Each IPC protocol major version remains independently negotiable; incompatible extensions fail activation with a typed compatibility error.
- The initial protocol implementation must impose message and time limits before accepting extension traffic.
- Existing packages are not silently migrated to OOP. Runtime choice remains explicit in each manifest.
- Install, trust/authorization, and activation/execution remain independent decisions.
