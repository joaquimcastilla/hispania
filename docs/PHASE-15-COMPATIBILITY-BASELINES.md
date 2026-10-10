# Phase 15 — Contract Freeze & Compatibility Baselines

Compatibility Baseline **0.7** is technical evidence from Phase 14 validated SHA `78f291fdacf9442c6e2d8e23094dc0e309828693`, not a public 0.7 release. Distribution remains **0.7.0**. No package, protocol, event-envelope or schema generation changes.

## Provenance and frozen artifacts

`compatibility/baseline-0.7.json` inventories 27 artifacts with owner, origin SHA, version axis, SHA-256, expectation and regeneration policy. The separately pinned `manifests/0.7/frozen-index.json` records initial membership/metadata. It is integrity metadata, excluded from the 27 artifact count. `-text` attributes preserve byte hashes across Windows/Linux checkouts. Some captured API/JSON text retains the historical Windows producer CRLF bytes; API comparisons normalize newlines and JSON comparisons are semantic. The frozen paths declare `cr-at-eol` for Git whitespace review, without changing old bytes.

The source was extracted with `git archive` into ignored `.artifacts/phase15/origin`. Capture code in `tools/historical-capture` references that archived tree through `HistoricalRoot`; none of its references target current contracts. Historical contracts/module assemblies were built with `DebugType=none` and normalized `PathMap`, removing personal PDB paths. `Capture.csproj` is deliberately outside the solution and CI. Output must be a new staging directory; the capture refuses an existing directory. The initial package construction test found a manifest Name inconsistent with the historical descriptor; capture was corrected before the first reviewed baseline commit. Production contracts were unchanged.

The API text is the 36-type, 459-line selected snapshot already captured in Phase 14. The binary consumer's source was written as test instrumentation against those archived contracts, compiled once, and preserved alongside its project and DLL. The SDK binaries demonstrate provenance and must never be substituted for current runtime assemblies in compatibility gates.

Artifacts comprise selected API text; three historical contract DLLs; Identity/RBAC consumer source/project/binary; historical greeting DLL; signed package/manifest/public key; five DTO/permission JSON fixtures; eight small HTTP cases in one fixture; five binary ModuleHost frames; five Worker JSON frames. No historical database or credential store is committed.

## Freeze and change review

Historical bytes, hashes, metadata and membership are immutable after the initial baseline commit. Tests verify the pinned index hash, compare every pinned entry with the live manifest, verify every artifact hash, and detect unlisted corpus files. Removing a fixture, removing its manifest entry, changing it and rehashing it, or silently regenerating it fails. New reviewed artifacts may be appended without touching old entries/index.

When appended artifacts themselves become historical baselines, add a separately reviewed supplemental index/hash pin for them. The initial 0.7 index is never extended or replaced. This task freezes the initial 27 artifacts; it does not silently assign historical status to arbitrary future additions.

`python tools/compatibility-manifest.py` is a deterministic read-only analysis. `--initial-freeze` refuses existing manifests/indexes. Neither CI nor normal tests regenerate evidence. `HISPANIA_CAPTURE_API_BASELINE` is blocked once the historical baseline exists. Review future breaks in `compatibility/api/CHANGELOG.md`; a changelog entry never automatically bypasses compatibility gates. Preserve old evidence and add a new corpus/generation or explicitly revise the supported window and tests with owner review.

The public API diff qualifies lines with their declaring type: missing type/member, visibility loss, changed return/signature/nullability and enum value changes appear as BREAKING; new members appear as ADDITIVE. Removed types have all their historical entries reported. This does not capture every attribute, protected member, assembly ABI rule or behavioral change. The Phase 14 gate remains an additional exact selected-snapshot review.

Fixture immutability and reproducible builds differ. Analysis, hashes, API ordering and diff must repeat identically. Historical DLL/package bytes are preserved, not rebuilt during gates. Ephemeral RSA signing intentionally produces a new signature on a new staging capture; equivalence of rebuilds is not a 0.7 byte-reproduction promise.

## Compatibility evidence and candidate reevaluation

“Yes” below means the bounded scenario was executed; “no” means no empirical coverage of that category, not proof of incompatibility. A DTO parser accepting a value is not an authorization or existence guarantee.

| Family | Status | Source | Binary | Serialization/wire | Behavior | Remaining blockers |
|---|---|---|---|---|---|---|
| Identity query/command and selected DTOs | Stable Candidate | Yes, frozen consumer | Yes, old DLL/current contracts | Yes, user/session default JSON | Yes, query, duplicates, login failure, revoke, disabled user | Actual later-version assemblies, broader consumers, public JSON serializer publication, evolution/security review |
| Authorization/RBAC service | Stable Candidate | Yes, same consumer | Yes, same old DLL | Yes, role JSON and permission IDs; no HTTP route freeze | Yes, default deny, assignment/removal, effective grants, one-time bootstrap | Administration contract remains experimental, permission catalog evolution, policy/security review |
| AssetId/AssetReference | Stable Candidate | No dedicated old source consumer; selected API diff only | No dedicated binary caller | Yes, historical default JSON and writer semantic shape | Yes, equality/hash and opaque GUID | Explicit serializer publication and input validation; no storage/existence promise |
| Direct Play / Range | Stable Candidate | No old Playback source consumer; selected API diff only | No old Playback binary caller | Yes, eight bounded real HTTP cases | Yes, full/closed/suffix/open/416/malformed/multiple/source-changed; disposal | Protected members/full ABI, broader HTTP evolution, no codecs/transcoding/HLS/device promise |
| Stale-generation fencing | Stable Candidate | N/A, invariant | N/A, invariant | Selected Worker completion JSON; no entire mixed-release protocol promise | Yes, stale rejection before/after new completion and stale failure | Durable Jobs attempt/store historical migrations and full mixed-release matrix remain future work |

There are **38 candidate catalog entries**, not 38 independent families. The other entries are supporting Identity/value/result and Playback nested/result types within these five families; “other 33” must not be interpreted as 33 additional families. All 38 retain their existing candidate classification after review, with the coverage limits above. None is promoted to Public Stable and none is demoted solely because a broader test matrix remains unfinished. Authorization role/administration/helper contracts, Module SDK, packages and protocol DTOs remain experimental even where fixtures now cover them.

## JSON, enums and error policy

Captured JSON uses the archived default `System.Text.Json` options (PascalCase, numeric enums), not a claim that all HTTP serialization uses those options. HTTP worker requests separately exercise the current ASP.NET Web parser against historical PascalCase input.

AssetReference serializes as `{ "Id": { "Value": "GUID" } }`. Property order and unknown fields are tolerated by the current reader; the writer comparison uses semantic JSON, not bytes. Missing Id produces the empty GUID under 0.7 parser behavior and is not declared a valid stored asset. An AssetReference is neither a filesystem path nor a guarantee of existence. Identity user/session and role fixtures preserve semantic writer shapes. Missing optional session lifetime keeps the 12-hour default; missing optional worker trace metadata remains null. Required-field validation is not implied by successful JSON deserialization.

Selected candidate enums are Identity `UserStatus` and `IdentityFailure`; Playback range/result variants are CLR types, not numeric wire enums. Numeric values are API-snapshotted. Default JSON accepts unknown enum integers but rejects unknown strings: clients must fail closed, not map a future integer to success/enabled. Identity rejects undefined status commands; unknown Identity/Authorization failure values do not succeed. AuthorizationFailure is experimental but audited by the old consumer fixture strategy. Worker state enums are experimental, with no open-value compatibility promise. Future enum additions must retain old numeric values and undergo owner review rather than assuming tolerance.

Errors freeze typed Identity failure outcomes, generic unsuccessful login and selected HTTP status (200/206/400/409/416). Module fixture error code `TEST_FAILURE` is synthetic transport data; it is not a new production error-code guarantee. Exception text, stack traces, internal database/hash details and diagnostic wording are excluded.

## Consumers, package and protocols

The old Identity/RBAC DLL loads without compilation. Its method parameter types are asserted to be current Platform types, then the real current Identity/Authorization implementations and temporary fresh SQLite adapter execute its queries and commands. This demonstrates binary compatibility for one bounded caller, not arbitrary ABI compatibility. The separate source test compiles the frozen source against the current Platform DLL and executes it, using a distinct assembly name. Temporary databases are fresh execution state, not historical upgrades.

The greeting implementation is compiled from archived module source and packaged using archived signing/container contracts. Current host requires the explicit TEST ONLY publisher public key and module allowlist, verifies signature and file digests, installs inertly, loads, executes, stops and unloads. The test runs in a child process; the original package is never rebuilt. Its ephemeral private key is discarded; only public material is preserved. A tampered manifest must fail signature validation.

ModuleHost fixtures are actual little-endian length-prefixed frames: hello, accepted generation-1 negotiation, success, error and cancel. Tests decode old bytes, negotiate current version and reject unsupported version, truncated frame and malformed JSON. Unknown JSON fields are tolerated. Worker fixtures cover connect with advertised capability, negotiation, leased assignment, result and stale completion. Tests decode old JSON and assert current public coordinator fencing. A real HTTP parser gate checks historical connect, 99-only version conflict, malformed/truncated/incompatible input and unknown fields. This is selected protocol evidence, not a complete old/new WorkerAgent process matrix.

All fixture passwords/tokens and identifiers are visibly TEST ONLY and never grant access outside temporary test instances. Certificate identities inside gates are synthetic or generated ephemerally; no machine certificate, private key, connection string, real session token or personal path belongs in the corpus.

## Validation and limitations

Run `./tools/test-phase15-compatibility.ps1`: 13 corpus/API/consumer/serialization/fencing/package/protocol tests plus three Playback HTTP/disposal/Worker tests, all required with no skips. Both Linux and Windows Validation jobs invoke this gate. The new test project is in the full solution and CrossPlatform filter; Platform-only production boundaries remain unchanged.

Local Windows validation (2026-10-05):

| Check | Result |
|---|---|
| Platform-only build | Passed, zero warnings/errors |
| Complete solution build | Passed, zero warnings/errors |
| Complete .NET regression, serial project execution | 561 passed, 0 failed, 1 existing PostgreSQL-live skip (562 total) |
| Phase 14 metadata/API gates | 3/3 |
| Phase 15 reviewed dedicated gate | 16/16, no skips |
| Inventory / Identity / RBAC / Events / Jobs / Storage / Observability | Inventory-owned suite 53/53 |
| Runtime / module infrastructure / Worker coordinator | 47/47 |
| Playback / distributed process / mTLS / FFmpeg / selected HTTP | 102/102 |
| FFprobe | 16/16 |
| Library persistence | 65 passed, 1 existing PostgreSQL-live skip |
| Closure and 20 concurrency repetitions | 210/210 across 62 TRX files, no skips |
| Web / Modern / Classic | 74/74; both builds passed |
| Manifest analysis repeated | Identical output and hashes; zero integrity errors |
| Capture inputs compared with validated Git SHA | 23 production files match after newline normalization |
| Security review of corpus and package members | No personal paths, private keys, real credentials or connection strings |
| Git whitespace check | Passed |

The first concurrent full regression hit the existing five-second FFmpeg availability timeout while Web/test projects were starting. FFprobe passed 16/16 independently, then the entire solution passed with `dotnet test Hispania.sln --no-build -m:1`. The Phase 14 stderr-draining/timeout fix is unchanged; no assertions were relaxed. Compiler/build warnings are zero. Reused/shared TRX filenames can emit test-runner overwrite notices; these are output-file notices, not compiler warnings or skips.

Negative evidence includes isolated API member removal/signature/enum mutation/additive reporting; missing/changed/rehashed/omitted corpus rows; malformed manifest; incompatible JSON shape; unsupported protocol; truncated/malformed frames; tampered signature; and explicitly rejected historical API regeneration with unchanged old bytes. No production code was deliberately corrupted.

Current manifest SHA-256: `4cb34019df3824596917297e2c81a992f881f45bb2cc3a534752b6b970446da5`.
Pinned initial index SHA-256: `de8431a7f42e4a36979cc372a574cb62094f37399e9ee88a167d17f3a2bb005b`.
Selected API byte hash: `3930dc60626443317aad7a47268723a24214a5f329ff28673bb40d3f693a299c` (the normalized LF snapshot keeps Phase 14 hash `971cecbd2aecb6ab8afa767ec67471ae810e1ec677db8e872a67d2a0b7f0ed8f`).
 Linux Phase 15 CI execution remains pending because this task explicitly excludes push. Existing Phase 14 Linux validation does not validate the new Phase 15 tests. ARM64 remains unvalidated. No Public Stable promotion, distribution bump, schemas, full database upgrades, backup/restore, deployment, complete N-1 matrix or Phase 16 implementation is included.
