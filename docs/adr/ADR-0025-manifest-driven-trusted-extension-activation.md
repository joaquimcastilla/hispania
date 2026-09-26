# ADR-0025: Manifest discovery with host-trusted activation

Status: Superseded by ADR-0026

## Context

Hispania needs extension discovery and controlled activation while preserving contract ownership. Loading arbitrary .NET assemblies from an extension directory would execute code with the server's process privileges. The existing runtime supervises module lifecycle but its initial inventory was fixed at construction.

## Decision

The original Phase 6 decision scanned JSON manifests as non-executable metadata and limited activation to host-registered factories. That protected the host from executing files merely because they appeared in the extension directory, but required a server build for every implementation.

ADR-0026 retains inert manifest discovery and explicit host authorization while adding local packages and collectible assembly contexts for trusted extensions. Requested permissions remain metadata and do not constrain in-process native code.

## Consequences

- Static factories remain compatible for existing built-in modules.
- Package loading and lifecycle rules are defined by ADR-0026.
