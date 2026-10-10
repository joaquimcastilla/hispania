# Phase 20 — Dynamic Web Modules

Status: Experimental. Acceptance evidence and local Git checkpoint are recorded in `.artifacts/phase20/INFORME-PHASE20.md`. Distribution remains 0.7.0; private npm remains 0.1.0; Public Stable contracts remain zero. Web Schema Generation 1 is compatibility axis 25. Historical axis counts and corpus bytes remain historical facts.

## Scope and ownership

Trusted Phase 19 packages may contribute declarative pages to both Modern and Classic without recompiling their host bundles. Runtime validates and projects package metadata; Server authenticates and authorizes the projection and fixed operations; the existing Shell registry/router composes the page. Runtime and generic Worker remain free of Media/ERP domain references. No marketplace, remote registry, package JavaScript, raw HTML/CSS, iframe, service worker, arbitrary URL, browser sandbox, or Node runtime is introduced. Package CLR code remains trusted in-process code under Phase 19 policy.

Initial extension points are `PrimaryNavigation`, `AdminNavigation`, and exact pages beneath `/extensions/<package-id>/<contribution-id>`. Primary links appear in desktop navigation and the mobile drawer. Admin slots require an accepted Platform permission. Contribution IDs are stable within a package; the composite identity is PackageId + ContributionId. Built-in routes win and cannot be shadowed. Pages share the existing `routeForPath` composition, authentication and host components; they do not introduce a second router.

## Descriptor, trust and compatibility

`web/contributions.json` is ordinary Package Format 2 payload. Every descriptor/asset path must be in the existing signed `Files` SHA-256 inventory. Changing either bytes or the digest map invalidates integrity or publisher verification. No new manifest field alters Phase 15 canonical signatures. Ephemeral TEST ONLY RSA keys are generated during fixture runs, never published.

Required UI declares `hispania.web.contributions@1.0` in both manifest `Requires` and CLR `ModuleDescriptor.Requires`. New hosts require their statically composed `WebContributionHostModule`; a package cannot pretend to provide host support. Current preflight rejects unavailable/unknown required generations before install. The exact archived Phase 19 reader rejects the missing capability before `StartAsync`, rolls back, and publishes no healthy/enabled package. Its older loader may construct the trusted module before negotiation: this evidence is not a claim that old hosts execute no constructor code. Optional UI is ignored by older hosts; future/invalid optional descriptors leave backend activation available but UI blocked. Required invalid UI fails preflight/activation. Package Format remains 2.

Schema 1 rejects unknown fields, duplicate JSON properties, unknown schema generations, routes, components, operations and slots. It does not silently reinterpret a future generation. Incompatible evolution requires a new schema generation, explicit negotiation and a separate corpus. This remains Experimental; no cross-minor stable promise is implied.

## Bounds and rendering

| Item | Accepted bound |
| --- | --- |
| Descriptor | UTF-8 JSON, 64 KiB, depth 12 |
| Contributions per package | 1–32 |
| Components per page | 1–32 |
| Title / component text | 128 / 2048 characters, plain React text |
| Navigation | package/status/table icons; two named sections; order −1000…1000 |
| Assets | flat `web/assets/<ASCII-name>.png`; 1 MiB each, 4 MiB declared aggregate; dimensions ≤2048×2048 |
| API rows | at most 100 bounded name/value pairs |

Components are heading, text, status, table and button. Text is escaped by React, including literal `<script>` strings. Data components can request only GET `platform.health`; a button can request only POST `platform.health.refresh`. The latter executes composed Platform health checks, not an arbitrary application command. Schema 1 includes no form, user-supplied action arguments, job handler injection or business mutation. A later operation catalog requires explicit backend composition, permission checks and schema review.

PNG files require signature/header/chunk bounds/end validation and no trailing content. They are served only from eligible packages, under artifact-hash URLs, with `image/png`, `nosniff` and no-store. Encoded traversal, absolute paths, executable suffixes and unsigned assets fail. Existing package path validation rejects filesystem links/reparse points. Assets are public nonsensitive navigation decoration; packages must not put private data in them.

## Security and lifecycle

Registry DTOs require the existing Session authentication, expose only current-user permitted contributions and contain no private filesystem/trust metadata. Every operation independently revalidates the session, page permission, declared operation, package policy/health and `platform.observability.read`. Navigation visibility is convenience; backend RBAC remains authority. Package permissions are declarations, never role grants. An operator explicitly assigns the namespaced permission or an existing allowed Platform permission. Empty/expired sessions return 401; denied operations 403; inactive or unlisted operations 404; incorrect methods 405. Host error bodies are not displayed as executable or trusted detail.

Projection snapshots are immutable and computed under the existing package activation gate. Install is inert; enable publishes; disable/uninstall/revocation/dependency failure remove contribution eligibility; update replaces content and hash; failed activation rolls back backend and UI together. The same package graph propagates transitive dependency health. No UI-specific graph/catalog/journal is persisted. Parsed documents are cached by authenticated descriptor hash; eligibility and file integrity are checked again on access, and obsolete entries are evicted. No ZIP is parsed per HTTP request. Existing open pages obtain a changed navigation set on reload; their data/actions already reject stale eligibility immediately. This is an explicit refresh model, not a push-notification promise.

Portable backup/restore uses the existing Phase 17 store inventory and package state. On restart, required descriptors/assets are revalidated and the registry rebuilt. Future optional UI remains blocked while its backend recovers. No absolute URL/path, session token or compiled UI registry is written into package state.

## CSP and diagnostics

Server sets `script-src 'self'`, no inline scripts and no unsafe-eval; object/frame are none, base/form self. Existing host inline styles require `style-src 'self' 'unsafe-inline'`. Existing host images retain self, TMDB, blob and data; media retain self/blob. Packages cannot choose these sources and get no HTML/style field. This is host defense in depth, not a hostile-code sandbox. The actual Chromium tests load a controlled TEST ONLY origin script to prove blocked eval and injected inline script under this policy.

Admin module diagnostics list package/version, contribution ID, route, state, reason and count with bounded safe identifiers. Eligibility/validation states distinguish Active, Blocked and Invalid. Existing package audit captures lifecycle mutations. The `Hispania.Runtime.Web` meter records active-count samples and validation failures without package/session/user labels. No per-request high-cardinality package metric is emitted.

## Verification and limits

The mandatory `tools/test-phase20-dynamic-web.ps1` gate compiles the exact Phase 19 reader, proves required-feature rejection, checks signed/current/future corpus and lifecycle tests, performs actual backup tests, builds both bundles, and runs real Chromium against a Server process. Modern and Classic each exercise UI login, returned navigation/direct route, literal malicious text, real health rows/action, denied user, invalid session, install/enable/disable/update/rollback/uninstall/revoke and backup destruction/restore. Zero retries/skips are permitted. Unit renderer tests additionally cover 401/403/404/500 and hostile route strings.

Production tests use actual verified published artifacts: exact Phase 19 process, offline distribution upgrade, current Server readiness/authenticated registry, optional future isolation and unchanged package state. Publication excludes demo implementations, fixture packages and signing keys. Both OS CI jobs retain Phase 14–19/closure/media/process gates; production jobs retain live password-auth PostgreSQL, real systemd/SCM Server and Worker tests. Local Windows cannot stand in for Linux/native privileged evidence. CI is pending until the branch is intentionally pushed by the operator. See the evidence report for exact counts, hashes, timings and limitations.

The 32-page fixture measures 20 coherent registry reads without inventing a latency SLA. Bundle size is measured against the accepted Phase 19 artifact. These are local measurements, not compatibility guarantees.

See [ADR 0045](adr/ADR-0045-dynamic-web-contributions.md), [author guide](DYNAMIC-WEB-AUTHOR-GUIDE.md), [operator guide](DYNAMIC-WEB-OPERATOR-GUIDE.md) and [Schema 1 corpus](compatibility/web/schema1/README.md).
