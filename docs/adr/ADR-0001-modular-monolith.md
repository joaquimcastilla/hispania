# ADR-0001: Start as a Modular Monolith

Status: Accepted

## Context

The project requires strong modularity but is initially developed by a small team.

Microservices add network, deployment, consistency and debugging complexity.

## Decision

Start as a modular monolith.

Modules communicate through contracts even when running in the same process.

## Consequences

Positive:

- simpler development;
- simpler deployment;
- low latency;
- easier debugging;
- preserves modular boundaries.

Negative:

- runtime isolation between native modules is limited;
- extraction to separate processes may require adapters later.
