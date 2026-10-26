# Limitations and residual risks — Step 7.4 accepted scope

Step 7.4 was accepted as **PASS within the ratified scope** on 2026-10-26. This register preserves the accepted scope limits and unresolved risks; it does not mark unverified criteria as passed.

## Explicit NOT VERIFIED, non-blocking only under owner-ratified scope

- PI-31 — No run under a different ordinary Windows identity. Do not claim multi-identity support.
- PI-32 — No clean Windows host with runtime-only installation. Do not claim clean-machine support.
- PI-34 — No byte-identical rebuild from declared clean inputs. Do not claim byte reproducibility.

## PI-35 evidence limits

- The successful run used elevated PowerShell (`elevated=true`); it demonstrates direct symlink creation and StoragePathGuard behavior in that elevated test context, not non-elevated creation.
- The test verifies the external sentinel remains present; it does not compare the sentinel content hash.
- The summary JSON does not embed the runner hash. Runner identity is linked by the recorded invocation and preparation package evidence, rather than a runner digest field inside the run summary.
- The PI-35 run remains owner-accepted PASS at its demonstrated scope; these limits are disclosed, not silently promoted to broader claims.

## Other residual risks

- A historical Runtime child-memory probe returned `0xC00000FD`; ten later focused repetitions and a later full Runtime run passed, but the cause was not demonstrated. Keep as unexplained historical intermittent risk, not corrected.
- OOP-only is process fault isolation and resource supervision, not a security sandbox against malicious native code.
- Candidate is framework-dependent and requires Windows x64 plus .NET 10 / ASP.NET Core 10 shared runtimes. PI-32 remains unverified.
- The PI-35 transfer runner hash is externally verified but is not embedded in the run JSON.
- PI-36 is not applicable by owner decision; no public generic HTTP capability endpoint is claimed.
