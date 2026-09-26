# Contributing

## Before coding

Read:

- `VISION.md`
- `PROJECT_CONTEXT.md`
- `CODEX.md`
- `docs/ARCHITECTURE.md`
- relevant ADRs.

## Validation

The Linux CI job restores, builds and tests `Hispania.CrossPlatform.slnf`. This filter includes every cross-platform project and excludes the Windows-only development launcher. The Windows CI job runs `tools/dev-launcher-tests/Hispania.DevLauncher.Tests.csproj` with the Windows Desktop runtime. On Windows, `dotnet test Hispania.sln` remains the complete local check.

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
