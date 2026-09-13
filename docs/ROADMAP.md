# Roadmap

This roadmap is architectural first.

## Phase 0 - Project Constitution

Deliverables:

- README
- Vision
- Project Context
- Architecture
- Coding rules
- Contribution guide
- Security policy
- license
- ADR framework
- CI skeleton

Goal:
make future decisions traceable.

## Phase 1 - Kernel 0.1

Build:

- host;
- module registry;
- configuration;
- dependency injection;
- lifecycle;
- health checks;
- versioning;
- event infrastructure;
- structured logging;
- basic observability.

Acceptance criterion:

Two implementations of the same trivial test contract can be selected from configuration without modifying the consumer.

## Phase 2 - Domain and Media Model 0.2

Define:

- Work
- Edition
- Timeline
- MediaEntity
- MediaSource
- Asset
- language tracks
- subtitle tracks
- metadata identity

No UI requirement yet.

## Phase 3 - Filesystem and Scanner 0.3

Implement contracts:

- IFileSource
- ILibraryScanner
- IMediaAnalyzer
- ILibraryRepository

Initial modules:

- filesystem source
- ffprobe analyzer
- SQLite repository

Goal:
scan a directory and persist discovered media.

## Phase 4 - Public API 0.4

Create:

- versioned REST API;
- OpenAPI;
- system endpoints;
- library endpoints;
- media endpoints.

Rule:
clients cannot bypass API.

## Phase 5 - First Web Client 0.5

Implement:

- basic home;
- library;
- media details;
- basic player;
- settings.

Goal:
scan and play a local movie through the web client.

## Phase 6 - Playback Engine 0.6

Build:

- ClientCapabilities
- PlaybackRequest
- PlaybackPlanner
- PlaybackPlan
- PlaybackExecutor

Initial strategies:

- direct play;
- direct stream when possible.

## Phase 7 - Transcoding 0.7

Add:

- ITranscoder;
- FFmpeg implementation;
- process supervision;
- cancellation;
- timeout;
- hardware capabilities abstraction.

Future implementations:

- NVENC
- VAAPI
- QSV
- remote transcoder.

## Phase 8 - Users 0.8

Add:

- users;
- profiles;
- local authentication;
- authorization;
- sessions;
- device registration.

Contracts:

- IIdentityProvider
- IAuthenticationProvider
- IAuthorizationProvider

## Phase 9 - Metadata 0.9

Contracts:

- IMetadataProvider
- IArtworkProvider
- IExternalIdProvider

Initial implementations:

- local metadata;
- one external provider.

## Phase 10 - 1.0 Product Baseline

Required:

- movies;
- series;
- music;
- users;
- metadata;
- direct play;
- transcoding;
- subtitles;
- history;
- continue watching;
- search;
- collections;
- playlists;
- basic administration;
- web client;
- Docker deployment;
- backup/restore basics;
- API stability policy.

## Phase 11 - Plugin SDK

Build:

- .NET SDK;
- Rust SDK;
- plugin templates;
- contract docs;
- compatibility tests;
- examples.

## Phase 12 - WASM Plugin Runtime

Add:

- Wasmtime;
- WASI;
- capability permissions;
- manifests;
- sandboxed storage;
- network allowlists;
- plugin health;
- resource limits.

## Phase 13 - Pipeline Engine

Represent workflows explicitly.

Initial pipeline:

Filesystem
-> Parser
-> Analyzer
-> Identifier
-> Metadata
-> Artwork
-> Repository
-> Search

Enable stage swapping via configuration.

## Phase 14 - A/B and Experimental Modules

Support:

- multiple implementations;
- routing rules;
- feature flags;
- controlled rollout;
- metrics comparison.

## Phase 15 - Full Observability

Provide per-module:

- calls;
- latency;
- errors;
- CPU;
- RAM;
- throughput;
- custom metrics.

Add benchmark harness.

## Phase 16 - Event Platform

Standardize application events.

Optional external bridge:

- NATS / JetStream.

## Phase 17 - Distributed Nodes

Optional advanced deployment:

- scanner node;
- metadata node;
- GPU transcode nodes;
- search nodes.

Home installs remain single-process by default.

## Phase 18 - Additional Clients

- Tizen
- Android
- Android TV
- Desktop
- others

## Phase 19 - Differential Features

Examples:

- audio/video toggle for tracks;
- multi-version media;
- smart playlists;
- semantic search;
- recommendation modules;
- scene detection;
- intro detection;
- credits detection;
- remote playback;
- offline clients;
- distributed transcoding.

## Phase 20 - Ecosystem

Build:

- plugin registry;
- module discovery;
- signatures;
- compatibility metadata;
- ratings/reputation;
- documentation portal.

## Phase 21 - Preservation Knowledge Graph

Independent subsystem.

Build:

- WorkID
- EditionID
- TimelineID
- AssetID
- preservation states
- evidence system
- lost-media workflows
- unknown-work submission
- coverage metrics

## Phase 22 - Distributed Content Network

Only after identity, manifests and moderation are mature.

Build:

- content addressing;
- signed manifests;
- authorized submission;
- quarantine;
- validation;
- revocation;
- peer discovery;
- pluggable transports;
- rights/distribution-state handling.

This phase must remain separate from the core media server.
