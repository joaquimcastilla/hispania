# HISPANIA v1.0 — Phase 1 Slice 7 Report

## A. Decision

**PARTIAL — implementation and automated lifecycle/security coverage are present; publish-manifest acceptance is blocked.** Do not start Slice 8. No public distribution, push, or release was performed.

## B. Scope and implementation

Local implementation commit: `ca4f9219d334cdd74a2934faf98802141acf0695` (`feat(core): add trusted Slice 7 module lifecycle`). It adds a minimal Core Module Manager over the existing Runtime package installer/catalog and ModuleHost, signed fixture source and rights provenance, explicit publisher trust/revocation configuration, strict session/permission/CSRF-protected administration endpoints, SQLite migration for Core module permissions, lifecycle and failure-isolation tests, and a narrowly extended artifact policy/validator for the nested ModuleHost process.

The fixture signer is `hispania.slice7.test`; private test keys are generated in memory and are not included in the repository or publish. Signing, package integrity, publisher trust, permissions, and process fault isolation remain separate checks. Out-of-process hosting is not a sandbox and does not protect Core from malicious native code. The fixture has no declared capabilities and receives no permissions by virtue of its signature.

## C. Automated evidence

Directly executed on Windows x64 at source commit `ca4f9219d334cdd74a2934faf98802141acf0695`:

- Core Release suite: 50 passed, 0 failed.
- Core artifact validator suite: 26 passed, 0 failed.
- Slice 7 lifecycle-focused tests: 4 passed, 0 failed.
- Selected baseline regression: Inventory/Platform/Storage 29 passed; Deployment/Hosting 35 passed; Runtime/module infrastructure 19 passed; total 83 passed, 0 failed.

Regression TRX SHA-256:

| Evidence | SHA-256 |
|---|---|
| `slice7-core.trx` | `8DBD2F3DA691EA931F423BF532FAE99BDF7C9657DEEB3C62E67F4B811D314536` |
| `slice7-inventory.trx` | `8A7793D260EF1583B0B98B4BD5D47BEAF21AEC0B3CEEE2B72914947B6F974212` |
| `slice7-deployment.trx` | `84389A159735F930A8B5ABF0DA207C36F4FFF1D3DFB0D404667CF03EBF5AB38D` |
| `slice7-runtime.trx` | `4833AD60B77C56D44B8000811B2B989172944F7C10C63127CD006D2D9422016E` |

The focused and full test runs cover unknown/revoked publisher, bad signature, changed payload hash, corrupt archive, incompatible host, signed fixture install disabled by default, enable/disable/uninstall, restart recovery, ModuleHost execution and fixture failure isolation. Admin route tests cover authentication, role permissions, origin and antiforgery behavior. These are automated test results, not a published-process smoke.

## D. Windows x64 publish and manifest

`dotnet publish apps/core/Hispania.Core.csproj -c Release -r win-x64 --self-contained false --no-restore -o <temporary publish-win-x64>` completed and produced a 19-file artifact. Its file inventory exposed duplicate root ModuleHost launcher/dependency files alongside the intended `module-host/` copy. The artifact validator was run with the Slice 7 allowlist and rejected the artifact:

```text
passed: false
Exactly one root Core .deps.json file is required.
```

Therefore no valid generated manifest, accepted content ID, or publish hash set exists. A corrective publish attempt was rejected by the execution tool before running; it was not retried. The attempted PowerShell block would remove the temporary publish directory and run `dotnet publish` again. No operation from that rejected command executed. This remains the acceptance blocker; regenerate into a fresh temporary directory, correct the duplicate root outputs in publish composition, then rerun the validator and prohibited-content checks.

The existing temporary publish directory is outside the repository at `%TEMP%\hispania-slice7-20261018-regression\publish-win-x64`; it contains ordinary build outputs, not test secrets. The rejected command did not delete or alter it. Test TRX files are also in that temporary evidence directory. This report does not claim cleanup of those non-secret outputs.

## E. Rights, publication, and remaining verification

The fixture is original project code with origin recorded in the rights register. Existing Runtime and ModuleHost reuse is authorized only for bounded local Slice 7 development; this report does not claim ownership, relicensing, or public distribution rights. The repository remote is public and D6 remains a distribution gate. Nothing was pushed or distributed.

Outstanding before Slice 7 can be closed:

1. Produce a clean Windows x64 publish with only the Core dependency manifest at the root and the approved ModuleHost manifest under `module-host/`.
2. Pass manifest/allowlist validation and verify forbidden business modules, six prohibited fingerprints, PDB/debug files, and secrets are absent.
3. Record accepted artifact hashes and manifest content ID.
4. Verify temporary-artifact cleanup. If a published-process lifecycle smoke is required by the final §13 acceptance review, run it against that accepted artifact or provide owner-run instructions and evidence.
5. Recheck a clean worktree after evidence is recorded.

## F. Git

Branch: `codex/hispania-v1-baseline-consolidation`. Starting HEAD for authorized implementation was `904587fd1640c3a0d7dfe068b1a8f41bf028f645`; implementation HEAD is `ca4f9219d334cdd74a2934faf98802141acf0695`. No push, merge, rebase, force-push, tag, or release occurred. A documentation-only closeout commit records this partial result. Worktree state is given in the final response.

**Closeout: PARTIAL. Slice 7 is not COMPLETED. Stop here; Slice 8 requires separate authorization.**
