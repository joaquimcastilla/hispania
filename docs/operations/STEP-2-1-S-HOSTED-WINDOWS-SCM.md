# Step 2.1-S — GitHub-hosted Windows SCM acceptance

The dedicated `windows-scm-hosted-acceptance` job runs on the ephemeral `windows-2025` GitHub-hosted VM. Dispatch it only from the candidate branch with the exact candidate SHA and `hosted_scm_acceptance=true`. That mode skips both existing production jobs, so it does not schedule any self-hosted runner.

The job requires the official repository, the authorized repository owner as both actor and triggering actor, the candidate branch, and `inputs.candidate_sha == github.sha`. It grants `contents: read`, checks out with `persist-credentials: false`, has no production secrets, uses a dedicated concurrency group, and has a 60-minute timeout. Standard GitHub-hosted runners are free for this public repository under GitHub's current Actions billing rules; the job uses no larger runner.

## Privileged test boundary

This workflow deliberately does not copy or approve the protected `ProgramData\HispaniaPhase21CI` helper generation from JOAQUIM. Instead, it checks at runtime that the runner reports `github-hosted`, Windows x64, the expected OS image, the exact dispatch identity/SHA, Administrators membership, and a high-integrity token. A failure stops the gate before service creation.

`tools/ci/windows-hosted-scm-helper.ps1` is a test-only direct SCM adapter for that fresh VM. It accepts a fixed action allowlist, requires the exact per-run root under the VM's `ProgramData\HispaniaPhase18CI` (with LocalService read/traverse access), binds the service name to the fixture GUID and role, checks fixture files for reparse points/hardlinks, verifies the published artifact and packaged SCM script against the candidate checkout, and validates the service registration, account, path, PID and executable image before destructive operations. The recovery test keeps a process handle open while terminating only the verified fixture executable. No command string or arbitrary executable is accepted from the test request. The helper is not an independent administrative approval and must not be installed on a persistent self-hosted runner.

## Run

```powershell
gh workflow run production-deployment.yml `
  --ref codex/hispania-0.7.0-step2-1-production-readiness `
  -f candidate_sha=<full-40-character-commit-sha> `
  -f hosted_scm_acceptance=true
gh run list --workflow production-deployment.yml --branch codex/hispania-0.7.0-step2-1-production-readiness --limit 5
```

The test publishes the exact `win-x64` candidate artifact, runs both `Phase18NativeService` cases, requires two passed/no skipped tests, preserves TRX and artifact provenance, and verifies cleanup. Each case performs native SCM install, start, readiness/health, stop, restart, persistence, real process failure and SCM recovery, uninstall, and fixture cleanup. A successful preflight or workflow that does not execute both native-service cases is not acceptance.
