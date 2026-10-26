# Published Integration Gate — PI-01–PI-36 owner-accepted status record

Accepted: 2026-10-26. Step 7.4 is PASS within the owner-ratified scope; this status record does not change frozen acceptance definitions. Scope: Windows 11 x64, current tested user/context except where PI-35 required elevation. PI-31, PI-32 and PI-34 remain NOT VERIFIED; PI-36 remains NOT APPLICABLE.

| ID | Contract criterion | Contract severity | Ratified classification | Current status | Evidence source / scope |
|---|---|---|---|---|---|
| PI-01 | Candidate manifest, allowlist and content ID agree | BLOCKING | Blocking | PASS | Candidate 700E: fresh read-only validator passed, issues=[]; 18 manifest payload entries + manifest reconciled (see candidate inventory). Supersedes older PI-01 FAIL snapshot for candidate 9459. |
| PI-02 | Candidate file inventory has no missing, extra or modified entries | BLOCKING | Blocking | PASS | Candidate 700E: 18/18 manifest payload path/size/SHA matched; 19 files including manifest; no extra/missing/mismatch. |
| PI-03 | Historical Slice 7 publish remains unchanged | BLOCKING | Blocking | PASS | Pinned historical baseline JSON matched all 19 on-disk files including manifest; content ID and manifest SHA match; no mismatch. |
| PI-04 | R3 forbidden business content and six rights-uncleared artifacts are absent | BLOCKING | Blocking | PASS | Step 7.2 static scan: inventory evidence JSON; validator `passed=true`; all six R2 files present/hash-matched in repository but path/hash absent from candidate/historical; recursive graph and 13-DLL PE reference/resource scan; nine Core route templates enumerated; forbidden ASCII/UTF-16LE token scan has no forbidden hits |
| PI-05 | Core and ModuleHost required files are present in the publish | BLOCKING | Blocking | PASS | Candidate inventory; published smoke evidence |
| PI-06 | Core starts and shuts down from the publish directory | BLOCKING | Blocking | PASS | Sanitized published smoke DP-isolated, 17/17; owner-accepted Step 6 scope |
| PI-07 | ModuleHost launches from its published location | BLOCKING | Blocking | PASS | Published smoke and ModuleHost tests |
| PI-08 | Application files are not resolved from repository paths at runtime | BLOCKING | Blocking | PASS | Step 6 copied-publish smoke ran Core/ModuleHost from copied publish; fixture build still uses repo SDK/source |
| PI-09 | Candidate shared-runtime requirements are explicit | BLOCKING | Blocking | PASS | Step 6 manifest/runtime configuration identifies .NET 10 and ASP.NET Core 10 shared frameworks; clean machine without SDK was not tested |
| PI-10 | No unintended absolute development paths or test/secret residue ships | BLOCKING | Blocking | PASS | Step 6 structural scan and validator, at their recorded scan scope |
| PI-11 | Explicit isolated Core data directory is honored | BLOCKING | Blocking | PASS | Published smoke; Core persistence/storage tests |
| PI-12 | Explicit Data Protection key directory is honored | BLOCKING | Blocking | PASS | CoreDataProtection tests 8/8; isolated published smoke 17/17; scoped to current Windows identity |
| PI-13 | Invalid or inaccessible configured roots fail closed | BLOCKING | Blocking | PASS | Core storage and Data Protection test records, automated configuration cases |
| PI-14 | Data and key state survives Core restart in the isolated run | BLOCKING | Blocking | PASS | Published smoke; Data Protection restart test; current identity only |
| PI-15 | HTTPS and liveness are validated on the published process | BLOCKING | Blocking | PASS | Step 6 published smoke evidence |
| PI-16 | Authentication rejects invalid credentials/origin and admin routes require authorization | BLOCKING | Blocking | PASS | Slice 6 report; Step 6 published smoke auth checks; synthetic credentials only |
| PI-17 | Administrative mutations retain CSRF/origin protections | BLOCKING | Blocking | PASS | Slice 6 security report and Core tests; automated/manual evidence |
| PI-18 | Trusted signed fixture installs through the published Core | BLOCKING | Blocking | PASS | Owner-reported published lifecycle smoke; Slice 7 evidence |
| PI-19 | Unknown/revoked/tampered signer or package is rejected | BLOCKING | Blocking | PASS | Publisher Trust 32/32; archive matrix; Slice 7 smoke evidence; automated negative cases at recorded scope |
| PI-20 | In-process module runtime is rejected by Core | BLOCKING | Blocking | PASS | Step 5 policy matrix; Step 6 published smoke |
| PI-21 | OOP fixture activates, responds internally, disables and uninstalls | BLOCKING | Blocking | PASS | Published smoke; internal OOP invocation 2/2; Slice 7 accepted evidence; public generic HTTP invocation is excluded by owner decision |
| PI-22 | Restart restores persisted module state consistently | BLOCKING | Blocking | PASS | Published smoke 17/17; owner accepted scope |
| PI-23 | Controlled fixture/ModuleHost failure leaves Core healthy and recoverable | BLOCKING | Blocking | PASS | Published smoke; OOP tests; OOP is not a malicious-code sandbox |
| PI-24 | No owned Core or ModuleHost process remains after shutdown | BLOCKING | Blocking | PASS | Step 6 published smoke 17/17 and owner-accepted evidence |
| PI-25 | ZIP extraction rejects unsafe names, collisions, link entries and corrupt content before publication | BLOCKING | Blocking | PASS | Step 2 adversarial extraction matrix: 22 PASS; 128 MiB threshold only code-reviewed |
| PI-26 | Storage paths and cleanup reject junction/reparse redirects and preserve external canaries | BLOCKING | Blocking | PASS | Step 1: 5 junction PASS; historical direct-symlink test was SKIP in the earlier run; PI-35 was subsequently verified as PASS in a separate elevated Windows execution (1 passed, 0 failed, 0 skipped). The historical SKIP remains recorded. |
| PI-27 | Package transactions recover from interruption and malformed journal | BLOCKING | Blocking | PASS | Step 3: 18 dedicated PASS; simulated checkpoints, not arbitrary power loss |
| PI-28 | Uninstall/retention only removes verified owned files | BLOCKING | Blocking | PASS | Step 4 safe deletion matrix: 18 scenarios PASS; no TOCTOU guarantee |
| PI-29 | Invalid configuration/module startup and unexpected worker exit have controlled outcomes | BLOCKING | Blocking | PASS | Step 6 published smoke; Runtime 265/265; bounded tested failure cases |
| PI-30 | Secret material is absent from logs and retained evidence | BLOCKING | Blocking | PASS | Smoke JSON `secretsPrinted=false`; Data Protection log test |
| PI-31 | Execution under another Windows identity is demonstrated | MAJOR proposal | Non-blocking under ratified limited scope | NOT VERIFIED | No identity switch/user was created |
| PI-32 | Clean Windows machine and runtime-only installation are demonstrated | MAJOR proposal | Non-blocking under ratified limited scope | NOT VERIFIED | No VM/machine available; candidate framework-dependent |
| PI-33 | Clean-checkout source-to-artifact provenance is demonstrated | MAJOR proposal | Blocking | PASS | Owner-ratified clean-source candidate; clean worktree HEAD/tree verified and manifest sourceCommit matches de790c…; bounded provenance only, no binary reproducibility claim. |
| PI-34 | Byte-identical rebuild is demonstrated | OPTIONAL proposal | Non-blocking under ratified limited scope | NOT VERIFIED | Same environment repeat recorded for earlier candidate; no clean checkout proof |
| PI-35 | Direct symbolic-link rejection is demonstrated independently from junction behavior | MAJOR proposal | Blocking | PASS | PI-35 owner-accepted run: summary + TRX; 1 PASS / 0 FAIL / 0 SKIP, direct SymbolicLink path cases; elevated=true. Scope limitations are explicit. |
| PI-36 | Public generic HTTP capability invocation exists | OUT OF GATE | Not applicable by owner decision | NOT APPLICABLE | Owner decision in Step 6 closure; capability audit documents absence |

Status roll-up: 32 PASS, 3 NOT VERIFIED (PI-31, PI-32, PI-34), 1 NOT APPLICABLE (PI-36). PI-35 PASS is limited to the elevated Windows x64 execution described in the evidence; it does not demonstrate the test contract phrase “without elevated privileges.”

PI-04 bounded static audit: PASS per the existing R2/R3 per-exclusion register in the frozen gate. This accepted status record does not convert legal rights clearance, arbitrary-code analysis, or legacy Server runtime behavior into verified claims.

Prior status rows in the frozen gate are retained as dated historical records. Current reconciliation above supersedes their status values only for candidate 700E; it does not rewrite their criteria or acceptance policy.
