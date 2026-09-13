# Contributing

## Before coding

Read:

- `VISION.md`
- `PROJECT_CONTEXT.md`
- `CODEX.md`
- `docs/ARCHITECTURE.md`
- relevant ADRs.

## Pull requests

A PR should state:

- problem;
- approach;
- affected modules;
- affected contracts;
- tests;
- migration impact;
- compatibility impact.

## Architectural changes

Create an ADR before merging major architectural changes.

Examples:

- changing plugin runtime;
- changing API paradigm;
- introducing microservices;
- changing identity model;
- replacing storage abstraction;
- changing event system.

## New modules

A module should document:

- id;
- owner;
- provided contracts;
- required contracts;
- permissions;
- configuration;
- health behavior;
- metrics;
- failure modes.

## Third-party modules

Third-party plugins should pass the compatibility suite for the contracts they claim to implement.
