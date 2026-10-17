# HISPANIA v1.0 — PHASE 1 SLICE 5 REPORT

## A. STATUS

**PASS — bounded to the independent Core on Windows x64.** Slice 5 adds persistent local identities, password authentication, default-deny role/permission authorization, opaque persistent sessions, explicit one-time bootstrap, and local recovery invalidation. No login or administration HTTP surface was added. This does not approve public distribution or claim that the Core is safe for Internet exposure.

## B. ROADMAP STATUS

Roadmap remains **4/14 — 28.6%**, as required by the owner. Phase 1 Slice 5/10 is complete at this report's bounded scope. Slice 6–10 remain unauthorized.

## C. PREFLIGHT AND SCOPE

- Repository/worktree: `C:\Users\joaqu\Documents\HISPANIA-v1-baseline-consolidation`.
- Branch: `codex/hispania-v1-baseline-consolidation`.
- Starting HEAD: `4a8f8e82a94a4186fd943c24abec815b18c49820` (Slice 4 closeout).
- Starting worktree: clean; no upstream was configured and no in-progress Git operation was present.
- Slice 4 implementation commits were ancestors of the starting HEAD; the full Slice 4 report, Freeze, Evidence Ledger, dependency allowlist, artifact policy, rights records, and migration implementation were reviewed.
- The preservation manifest was rechecked: 49 live source files and 49 backups (98 SHA-256 checks) matched. All 15 original worktrees were present and left untouched; the authorized worktree makes 16 in `git worktree list`.
- `LICENSE.md` SHA-256 remains `75EAE29E043C82B168894AFAAA148ADE2E9A43B4A72513FF18CFD22434F9BD3D`.
- No `apps/server`, public contract, Media/PostgreSQL provider, excluded historical asset, or license file was changed.

## D. ARCHITECTURE AND CONTRACTS

Internal contracts separate identity, password verification, authorization, sessions, bootstrap, and persistence. `ICoreIdentityStore` is the provider-neutral identity persistence boundary; `SqliteCoreIdentityStore` implements it. Identity/application services consume the interface and SQLite remains the only selected Core provider for v1.0. The boundary stays internal to the Core and exposes no SQLite types. The existing narrow `ICorePersistence` lifecycle contract still initializes and checkpoints storage before/after host operation. No speculative provider framework or generic ORM was introduced.

The Core remains independent from Media, Inventory, ERP, modules, workers, and `apps/server`. Only `/health/live` is mapped. Host startup still binds to loopback, migrates/validates storage before listening, and reports only liveness.

## E. IDENTITY AND CREDENTIALS

`core_identities` stores stable random GUID subject IDs, subject kind (`local-user` for current local accounts), username, unique normalized username, active/disabled state, and UTC creation/update timestamps. Username normalization is Unicode Form KC followed by trim, ASCII `[A-Za-z0-9._-]`, an alphanumeric first character, 3–64 characters, and invariant uppercase uniqueness. The model leaves room for non-human subject kinds without implementing them.

`core_credentials` is separate from identity data. It stores the versioned hash format, algorithm, work factor, and hash; it never stores the password. Password policy is at least 15 Unicode scalar values, at most 128 scalars, and at most 1,024 UTF-8 bytes. Invalid and absent users share generic authentication failure behavior; a dummy hash adds expensive verification work. This reduces user-enumeration signals but is not a constant-time guarantee.

## F. CRYPTOGRAPHY AND DEPENDENCIES

Credential hashes use the maintained ASP.NET Core Identity V3 `PasswordHasher<T>` implementation from the existing `Microsoft.AspNetCore.App` shared framework, with PBKDF2-HMAC-SHA512, a per-hash random salt, 220,000 iterations, and a 256-bit derived subkey. Hashes include the Identity V3 format metadata. Supported older HMAC-SHA512 hashes from 100,000 iterations are upgraded after successful authentication using a compare-and-swap update; malformed/unsupported formats and parameters fail closed. The work factor follows the current OWASP PBKDF2-HMAC-SHA512 baseline; reassess timing on supported deployment hardware and increase work factors as appropriate. See [OWASP Password Storage Cheat Sheet](https://cheatsheetseries.owasp.org/cheatsheets/Password_Storage_Cheat_Sheet.html) and [.NET PasswordHasherOptions](https://learn.microsoft.com/en-us/dotnet/api/microsoft.aspnetcore.identity.passwordhasheroptions?view=aspnetcore-10.0).

No NuGet package or project reference was added. The actual Core runtime graph remains the previously reviewed `Microsoft.Data.Sqlite` 10.0.12 and SQLitePCLRaw 2.1.12 set plus .NET shared frameworks; identity APIs are supplied by `Microsoft.AspNetCore.App`. Tests continue to use the existing xUnit/Test SDK versions. Package metadata and local validation are not redistribution clearance.

SQLite's per-user salt and slow password KDF reduce offline guessing cost after database theft; they do not encrypt the database or protect weak choices from sufficiently resourced attackers. Unix directory/file modes from Slice 4 apply; Windows inherited ACLs have not been independently audited. The database is sensitive and must be protected by the operator.

## G. EXPLICIT ADMINISTRATOR BOOTSTRAP

There are no default users or credentials. `hispania-core bootstrap-admin` (framework-dependent invocation: `dotnet Hispania.Core.dll bootstrap-admin`) initializes storage, refuses if bootstrap is no longer pending, and prompts locally for username, password, and confirmation. Interactive passwords are not echoed. For automation, the three values are read from stdin; credentials are not command-line arguments, environment variables, logs, or SQLite fields. The owner can use a protected stdin channel. Invalid inputs and failures produce sanitized output.

The first administrator, credential, `core.admin` assignment, both bootstrap completion markers, and an audit event are written in one SQLite immediate transaction. A second run is denied. Concurrent bootstrap attempts serialize, and an injected audit-write failure rolled the whole transaction back with bootstrap still pending. Pending state is corroborated by the instance marker, dedicated bootstrap row, administrator role assignment, and completion audit event; clearing one marker alone does not reopen bootstrap.

## H. ROLES, PERMISSIONS, AND AUTHORIZATION

Authorization uses explicit persisted role assignments and permission grants. A subject receives no permission by authentication alone; missing/unknown subjects and permissions, inactive accounts, unassigned roles, and nonexistent roles deny access. `core.admin` is seeded as a system role with four explicit Core permissions (identity read/manage, authorization manage, session revoke); no wildcard or implicit “authenticated means admin” rule exists. Changes to roles, permissions, grants, assignments, identity state, and bootstrap are parameterized SQL operations with immediate transactions and successful-operation audit events. The last active administrator cannot be disabled or deleted. Services accept actor subject IDs and check the relevant management permission before privileged changes.

## I. SESSIONS AND REVOCATION

Sessions use cryptographically random 256-bit opaque bearer secrets, returned only to the caller of the internal service. SQLite stores only SHA-256 token digests, plus subject, creation, expiry, and revocation timestamps. Expiry is enforced on every validation; lifetime must be positive and no more than 30 days. A subject may revoke its own session; revoking another subject's session requires `core.session.revoke`. Disabling an account revokes its sessions in the same transaction, and deleted identities cascade-delete their sessions. Session creation requires an active identity. Restart preserves session state.

`hispania-core invalidate-sessions` is a local recovery command for operators who restore an older database backup; it revokes all currently present sessions and records an audit event. It cannot detect rollback by itself and cannot undo restored password/account state. After restoring a backup, the operator must invalidate sessions and review/rotate credentials as necessary. There is no external anti-rollback trust anchor.

No cookies, JWT, HTTP login, HTTP admin, or bearer-token endpoint was introduced. Slice 6 must still decide and implement HTTPS, secure/HttpOnly/SameSite cookie policy, CSRF protection, login throttling/lockout, session transport, and endpoint authorization. The current absence of HTTP login means HTTP brute-force controls are not yet present; this Core must not be exposed as an authenticated Internet service.

## J. SQLITE SCHEMA AND MIGRATIONS

The versioned v2 migration extends Slice 4's v1 `core_instance` with a bootstrap timestamp and adds `core_identities`, `core_credentials`, `core_roles`, `core_permissions`, `core_role_permissions`, `core_subject_roles`, `core_sessions`, `core_bootstrap_state`, and `core_security_audit`, including foreign keys, unique constraints, check constraints, and an active-session lookup index. It seeds only system permission metadata and the administrator role; it creates no account. V1 is not rewritten. Migration v2 and its history entry apply transactionally. Integrity checks and future/incomplete schema fail closed while preserving data.

Evidence includes both an integration fixture reproducing the exact Slice 4 v1 schema and a process migration: the real Slice 4 published Core initialized schema v1, then the final Slice 5 published Core opened that database, migrated it to v2, and completed bootstrap. A final query confirmed schema v2, one identity, and completed bootstrap. The integration fixture separately asserts that the installation ID remains unchanged across migration.

## K. HOST AND CLI INTEGRATION

DI registers the provider-neutral store and SQLite implementation, Identity V3 password hasher, identity, authorization, session, and bootstrap services. Normal host startup still uses Slice 4's hosted lifecycle and adds no endpoint. Host tests confirm the sole route remains `/health/live`; a published-process smoke returned 200 with `{"status":"healthy"}`, 404 for `/admin` and `/login`, and checkpointed cleanly on Ctrl+C. Bootstrap and session-recovery commands initialize the same configured Core database without starting the HTTP listener.

## L. THREAT MODEL AND LIMITS

| Threat | Mitigation in Slice 5 | Residual / deferred |
|---|---|---|
| SQLite theft and offline password guessing | Per-credential salts, PBKDF2-HMAC-SHA512 at 220,000 iterations, no plaintext passwords, Slice 4 private-directory behavior | No database encryption; Windows ACL inheritance not audited; sufficiently resourced offline cracking remains possible |
| Local access by another OS account | Slice 4 reparse-point checks and Unix 0700/0600 modes | Windows ACLs depend on inherited permissions and need operator/installation hardening review |
| Future HTTP brute force / username enumeration | Generic failed-auth result and dummy expensive hash; no authentication route exists | No rate limiting/lockout yet; dummy work is not a constant-time guarantee; HTTP controls belong to Slice 6 |
| Privilege escalation | Default deny, explicit grants, actor permission checks, parameterized SQL, transactions, last-active-admin guard | No module-specific enforcement integration exists until later module slices |
| Session theft/replay/revocation | 256-bit random secret, database digest only, expiry, revoke, disable invalidation | Any bearer secret stolen from its future transport can be replayed until revoked/expired; transport hardening is Slice 6 |
| Bootstrap replay/concurrency | One-time local command, corroborating markers/audit, serialized transaction | A privileged operator can directly edit/delete the database; no hardware-backed root of trust |
| Interrupted/corrupt migration | Versioned atomic migration, migration history, integrity/FK verification, fail closed | Manual operator recovery remains necessary |
| SQL injection / secret logging | Parameterized SQL; no secret logging; published CLI stdin smoke verified no echo | Future interfaces/loggers need the same review |
| Restoring old backups | Local `invalidate-sessions` operation and documented credential review | Restored account/credential state can roll back; no automatic anti-rollback detection |

Passing these bounded tests is not an Internet security certification.

## M. CORE TESTS

Command: `dotnet test tests/core/Hispania.Core.Tests.csproj -c Release --no-restore --results-directory %TEMP%\hispania-slice5-20261017\results-core --logger "trx;LogFileName=slice5-core-final.trx" -v minimal`.

Result: **36 passed, 0 failed, 0 skipped**. TRX SHA-256: `3452A05CC38889A7CFE0B75A5F8F6B60B29C2DE3903D79877AD4884407016146`.

Coverage includes identity normalization/uniqueness and persistence, distinct salts, password success/failure/malformed hashes and rehash, migration from v1, explicit/concurrent/failed bootstrap, default deny and role escalation cases, session expiry/revoke/disable/recovery invalidation, host route boundary, and real SQLite transactions. The published bootstrap and host processes were also exercised separately.

## N. VALIDATOR AND SELECTED REGRESSION

- Artifact validator: `dotnet test tests/core-artifact-validator/Hispania.CoreArtifactValidator.Tests.csproj -c Release --no-restore --results-directory %TEMP%\hispania-slice5-20261017\results-validator --logger "trx;LogFileName=slice5-validator.trx" -v minimal` — **26/26 passed**, TRX SHA-256 `DB70F6E9E6E0F79968ACBD2BB974AF7802D73F697BA178C8FB752DD3BE9A66E0`.
- Slice 1 Inventory/Platform/storage selection — **29/29**, TRX SHA-256 `2A8F8C0F77221AF64DB341BB579CD4CC912EF14DE7276CE600B84A4E6B1F5BFC`.
- Slice 1 Deployment/Hosting — **35/35**, TRX SHA-256 `3EC463984733A1CA10C6DC5052B5DCA2932F2BE9B06A36D8CE8F57EF2192173F`.
- Slice 1 bounded Runtime/module selection — **19/19**, TRX SHA-256 `95B8CBD27279749B76DA5283B7EC1391EEAA5ACA6B9FDA2947A2F92EAB9EB4BE`.
- Selected regression total: **83/83 passed**. Full repository Runtime and all unrelated suites were not claimed.

Regression commands (from repository root; `%TEMP%` is `C:\Users\joaqu\AppData\Local\Temp` on the recorded host):

```powershell
dotnet test tests/inventory/Hispania.Inventory.Tests.csproj -c Release --no-restore --results-directory "%TEMP%\hispania-slice5-20261017\results-inventory" --logger "trx;LogFileName=slice5-inventory.trx" -p:NuGetAudit=false --filter "FullyQualifiedName~PlatformIdentityTests|FullyQualifiedName~PlatformAuthorizationTests|FullyQualifiedName~DurablePlatformEventTests|FullyQualifiedName~PlatformJobsTests|FullyQualifiedName~PlatformPersistenceTests|FullyQualifiedName~InventoryIntegrationTests|FullyQualifiedName~StorageProviderTests" -p:PlatformOnly=true -v minimal

dotnet test tests/deployment/Hispania.Deployment.Tests.csproj -c Release --no-restore --results-directory "%TEMP%\hispania-slice5-20261017\results-deployment" --logger "trx;LogFileName=slice5-deployment.trx" -p:NuGetAudit=false -v minimal

dotnet test tests/unit/Hispania.Runtime.Tests.csproj -c Release --no-restore --results-directory "%TEMP%\hispania-slice5-20261017\results-runtime" --logger "trx;LogFileName=slice5-runtime.trx" -p:NuGetAudit=false --filter "FullyQualifiedName~IndependentlyBuiltGreetingPackageInstallsLoadsUpdatesRollsBackDrainsAndUnloads|FullyQualifiedName~StartupFailureLeavesTheModuleInFailedState|FullyQualifiedName~IncompatibleExtensionIsRejectedWithoutAffectingCompatibleModules|FullyQualifiedName~RuntimeUsesExplicitDependencyToSelectAmongMultipleCapabilityProviders|FullyQualifiedName~ResolverFailsWhenModuleDoesNotProvideRequestedContract|FullyQualifiedName~CapabilityResolverSelectsOneCompatibleImplementationByContractAndId|FullyQualifiedName~RuntimeTracksTheExpectedLifecycle|FullyQualifiedName~RuntimeStartsDeclaredDependenciesBeforeConsumersAndRejectsCycles|FullyQualifiedName~ResolverFailsForAnUnknownModuleId|FullyQualifiedName~ModuleWorkerProtocolFramesAreBoundedAndDetectTruncation|FullyQualifiedName~InstallerRejectsUnavailableContractsAndCorruptPayloadWithoutInstallingFiles|FullyQualifiedName~OutOfProcessPoliciesRejectUnboundedOrInconsistentLimits|FullyQualifiedName~ActivationRejectsPostInstallPayloadAndManifestSubstitution|FullyQualifiedName~PublisherSignatureAuthenticatesCanonicalManifestAndHonorsRevocation|FullyQualifiedName~IndependentExtensionIsDiscoveredActivatedAndDeactivatedWithoutLoadingAnAssembly|FullyQualifiedName~ModuleWorkerNegotiationAuthenticatesIdentityAndSelectsCommonVersion|FullyQualifiedName~RuntimeReportsDegradedAndDrainingStatesAndRecordsTransitions|FullyQualifiedName~RegistryRejectsDuplicateModuleIds|FullyQualifiedName~PackageInstallationIsInertAndDoesNotGrantExecutionTrust" -v minimal
```

TRXs and published-process smoke records are retained outside the repository under `%TEMP%\hispania-slice5-20261017`. Real migration record SHA-256 `488DC560E3C0945C4B8F6862328773AF5688652A869F82DB355412C3CDECC486`, migration query SHA-256 `30CFD3B12ACE54921760BB3E0BE0E306AADF801555B8978ECC85072852076630`, CLI smoke SHA-256 `39DDD45D040106FD106D8C5A3FCFE53DA616FD189ED64B550E99D4BF6CFE1664`, and final-host smoke SHA-256 `2DE35A340E84DACFBD47768C390492C77322D942B8EFF8E1190930C0998AE01C`.

## O. REAL PUBLISH, GRAPH, AND EXCLUDED HASHES

Command: `dotnet publish apps/core/Hispania.Core.csproj -c Release -r win-x64 --self-contained false -p:PublishDir=%TEMP%\hispania-slice5-20261017\publish-win-x64\ -p:NuGetAudit=false -v minimal`. This was a real framework-dependent `net10.0/win-x64` publish, not a synthetic artifact. The final artifact has nine runtime files plus its generated manifest. The manifest's `sourceCommit` is implementation commit `1125be76906b8411dee6f6d54b12eaf341eee9f2`, which matches the published source. The exact publish validator `create` and `validate` commands both passed with no issues; manifest content ID `3893BB2DBED3378A6BB03C72E27C8A1A3E04C71EC6B845471C10FB383DAC8719`, manifest SHA-256 `0245E70CB444B96D7E58188AA61A1DF99FBF63D0C68DC289F92C0E07BC30E862`.

The resolved runtime package graph contains only Microsoft.Data.Sqlite 10.0.12 and SQLitePCLRaw 2.1.12 packages plus Microsoft.NETCore.App/Microsoft.AspNetCore.App shared frameworks. No `apps/server`, Inventory, Media, Library/PostgreSQL/Npgsql, FFmpeg, WorkerAgent, or Coordinator dependency appears. Identity has no extra package. All six prohibited historical artifact SHA-256 values were checked against the real published files and absent. Native SQLite artifact remains the Slice 4 `e_sqlite3.dll`; no new native payload was introduced.

## P. RIGHTS AND DISTRIBUTION

Slice 5 source/tests are owner-authorized for bounded local development and validation only. `PasswordHasher<T>` comes from the existing .NET shared framework; no new NuGet license obligations or native code were introduced. Existing SQLite package metadata and native asset review are carried forward from Slice 4. `LICENSE.md` is unchanged. No public distribution, sublicensing, release, or legal clearance is implied.

## Q. DOCUMENTATION AND EVIDENCE

Created this report; added EV-046 to the Evidence Ledger; updated the Phase 1 status note, the bounded Core identity rights record, and the Core dependency classification. Slice 4 report and public/historical contracts were not rewritten. Evidence is local Windows x64 only.

## R. INTEGRITY

`git diff --check` passed. Only authorized Core code, new Core tests, the one necessary schema-version expectation in the Core persistence test, Slice 5 documentation/evidence, and rights/dependency records changed. `apps/server`, other historical suites, public contracts, six excluded assets, original worktrees/backups, and `LICENSE.md` were left untouched. The original 15 worktrees and 98 preserved source/backup hashes were rechecked. No push, merge, tag, or release occurred; no upstream exists.

## S. COMMITS AND FINAL STATE

The code/tests and documentation/evidence are separated from unrelated history. Implementation commit: `1125be76906b8411dee6f6d54b12eaf341eee9f2` (`feat(core): add identity and authorization foundation`). The report and EV-046 were then committed and refined in documentation-only commits covering source-to-manifest binding, migration evidence precision, and exact regression commands. The final A–T closeout lists the documentation commit IDs and final HEAD; this report does not embed its own final commit hash.

## T. STOP

**SLICE 5 PASS — STOP.** Slice 6–10 were not started and require new owner authorization. HTTP login/admin, cookie/CSRF/rate-limit policy, full module permission enforcement, Linux/ARM64 execution, Windows ACL audit, anti-rollback storage, and public distribution/legal review remain future work.
