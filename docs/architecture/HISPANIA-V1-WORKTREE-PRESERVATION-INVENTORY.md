# HISPANIA v1.0 — Original Worktree Preservation Inventory

**Captured:** 2026-10-17 at Phase 1 Slice 3 preflight. **Authorized integration worktree:** `HISPANIA-v1-baseline-consolidation` is intentionally excluded from this list. The 15 rows below are the original worktrees to preserve.

The earlier preservation snapshot's metadata declared 15 roots but its detailed array contained only 14. The missing detailed entry is `phase20-production-validation`. This inventory reconciles the live set: 14 entries checked against the saved snapshot, plus that additional original root. No original worktree was modified during this capture.

| Original worktree | Branch / state | HEAD | Pre-existing status entries |
|---|---|---|---:|
| `home_server_project_docs` | `feature/phase-13-platform-maturity` | `5e5f302c6cbda5c4523e64eddbe60d64840b86f0` | 14 |
| `arm64-ci-publication` | `codex/hispania-arm64-integration` | `d3a16e8bd89c5f431faa1b1c17324220c0591a10` | 0 |
| `phase-13-4-durable-event-bus` | `feature/phase-20-dynamic-web-modules` | `742914b097aa8407bbd93a0ddcd0b40bfe7f46d9` | 2 |
| `phase20-production-validation` | detached | `2cfb8e6f8756b6ee934143e5393bea6e1adf1cc3` | 0 |
| `phase21-security-hardening` | `feature/phase-21-security-operations-hardening` | `c0f931b9ac57624173c3a96aa94843c753a0f461` | 0 |
| `phase22-retention-reconciliation-capacity` | `feature/phase-22-retention-reconciliation-capacity` | `468f1ab87e242ed241d88fa8963aabace5b4ca52` | 0 |
| `phase23-e2e-compatibility-upgrades` | `feature/phase-23-e2e-compatibility-upgrades` | `4faf71323cc9ec121743e85b48c354bbaa784b6c` | 1 |
| `phase24-dev-launcher-proposal` | `codex/authorized-short-soak-launcher` | `7c0cb4a532d1ecc6c553ab749900db4842a50bf5` | 0 |
| `phase24-operational-resilience` | `feature/phase-24-operational-resilience` | `f2c5798b2c3c973deacf2cad2e7cc6d89720c247` | 3 |
| `phase25-final-integration-release-readiness` | `fix/phase25-powershell-install-home` | `ce17c2e0601345fbd38018527cbce65b6e98e83a` | 0 |
| `release-0.7.0-rc.1-preparation` | `audit/modularity-0.7.0` | `b9ea7f3c9efd16290d4ecd82854d3059bb9db709` | 0 |
| `step1-work` | `codex/hispania-0.7.0-step2-platform-only` | `17eaee7df6a51068e895c335340fe109ac75766d` | 29 |
| `step2-1-production-readiness` | `codex/hispania-0.7.0-step2-1-production-readiness` | `1463ca5ce47866e8813a3499a34609c723808b2f` | 0 |
| `phase-13-1-identity-foundation` | `feature/phase-13-1-identity-foundation` | `d3b680cf9555b90107fb4ea819b2b92655695648` | 0 |
| `phase-13-2-authorization-web-auth` | `feature/phase-13-3-domain-authorization` | `3ebbb5bcc144316d9e2d2f9e1b0fa281b8222044` | 0 |

The 14 saved records match their preflight branch, HEAD and status count. The additional detached worktree is present and clean. Status counts describe preserved pre-existing local modifications; they are not instructions to clean those trees.

The five source-preservation roots and their 49 backed-up source files remain governed by the Step 0.2 preservation manifest. At this preflight all 49 live-file hashes and all 49 backup hashes match that manifest. This inventory does not authorize edits to any original worktree.
