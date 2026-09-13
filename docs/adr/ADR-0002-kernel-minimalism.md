# ADR-0002: Keep the Kernel Minimal

Status: Accepted

## Decision

The kernel contains only runtime infrastructure.

Media-specific implementations do not belong in the kernel.

## Allowed

- module runtime;
- lifecycle;
- contracts;
- configuration;
- events;
- security;
- compatibility;
- health;
- observability hooks.

## Not allowed

- FFmpeg;
- database provider;
- metadata provider;
- movie logic;
- music logic;
- search engine;
- transcoder;
- recommendation engine.
