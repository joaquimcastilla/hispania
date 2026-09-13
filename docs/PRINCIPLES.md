# Engineering Principles

1. Kernel stays small.
2. Depend on contracts, not implementations.
3. Architecture beats short-term convenience.
4. Public APIs are explicit and versioned.
5. Modules own their data.
6. UI does not own business logic.
7. Media identity is not file identity.
8. Local paths are implementation details.
9. External providers are replaceable.
10. FFmpeg is an implementation, not the architecture.
11. SQLite is an implementation, not the architecture.
12. No arbitrary shared utility dumping ground.
13. Every cross-module dependency must be intentional.
14. Every architectural change requires an ADR.
15. Experimental behavior should use feature flags.
16. Cancellation and timeout must be first-class.
17. Logs must be structured.
18. Metrics must be attributable to modules.
19. Security uses least privilege.
20. Sandboxed plugins receive explicit capabilities only.
21. Backward compatibility matters after public stabilization.
22. Before 1.0, bad contracts may be replaced rather than preserved forever.
23. Tests should validate behavior and contracts.
24. Compatibility suites should exist for third-party extension points.
25. Preservation, storage and distribution are separate concepts.
