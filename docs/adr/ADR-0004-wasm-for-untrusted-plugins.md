# ADR-0004: WebAssembly for Untrusted Third-party Plugins

Status: Proposed

## Context

Loading arbitrary native or .NET code gives plugins broad process-level access.

## Decision

Use WebAssembly Component Model + WASI + Wasmtime as the preferred future sandbox for third-party plugins.

Trusted high-performance modules may remain native.

## Requirements

- capability permissions;
- resource limits;
- network allowlists;
- plugin-owned storage;
- versioned contracts.
