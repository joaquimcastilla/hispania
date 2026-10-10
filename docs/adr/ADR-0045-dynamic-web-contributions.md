# ADR-0045: Dynamic Web contributions

Status: Accepted for Experimental implementation; acceptance evidence is recorded in the Phase 20 local report.

## Decision

Keep Package Format 2. A conventional `web/contributions.json` payload declares Web Contribution Schema Generation 1. The existing signed `Files` digest map authenticates every descriptor byte and asset; no new manifest field changes historical canonical signing. Optional descriptors can be ignored by older hosts. Required UI uses the existing `Requires` capability negotiation (`hispania.web.contributions@1.0`), also present in the module's CLR descriptor. The server registers a host capability provider; predecessor hosts cannot activate that requirement. A provider supplied by another package does not qualify as host support in the new preflight.

Runtime owns bounded validation and derives immutable registry snapshots under the existing package operation gate. Package catalog, live authorization, publisher trust, dependency health and journal state remain authoritative. No additional durable UI state or dependency graph is introduced. Parsed descriptors are cached by authenticated content digest; eligibility is checked on every registry or operation request.

Modern and Classic share the existing Shell routing composition and one declarative renderer. Extensions own `/extensions/<package-id>/<page-id>` only. Restricted text, headings, status, tables and named buttons use host components. Data/actions use an explicitly composed Platform operation catalog with fixed methods and backend permission checks. There are no arbitrary URLs, endpoint builder injection, executable package JavaScript, HTML, CSS, iframe or service workers. Authentication uses the existing explicit Session header, not ambient cookies.

Required invalid descriptors fail package preflight/activation. Optional invalid descriptors block UI only, with bounded diagnostics; other packages and the Shell survive. Navigation and routes are one snapshot; lifecycle update, rollback, restore and startup rebuild derive from the selected package set. Existing clients refresh to obtain a changed set; every data/action request rechecks current eligibility.

The schema is a separate experimental compatibility axis, independent of package container and npm bundle versions. Literal display text is experimental. CSP restricts script execution to the host origin without inline scripts or eval. These restrictions do not provide a hostile-code browser sandbox.
