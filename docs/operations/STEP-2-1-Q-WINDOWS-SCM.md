# Step 2.1-Q — Windows privileged runner and SCM approval

This runbook closes the Windows SCM gate without granting the candidate checkout administrator rights. It is for an administrator reviewing a specific candidate SHA on a disposable Windows runner. It does not authorize changes to the current host, protected snapshots, or existing services.

## Read-only administrative audit

Use a private console on the runner host. Do not run the audit as a GitHub Actions step because its textual diff can expose protected source. The audit never elevates itself and never writes to the protected root.

```powershell
$repo = 'C:\Users\joaqu\Documents\home_server_project_docs\step2-1-production-readiness'
Set-Location $repo
git status --short
git rev-parse HEAD
pwsh -NoProfile -File .\tools\ci\audit-windows-privileged-runner.ps1 `
  -ProtectedRoot 'C:\ProgramData\HispaniaPhase21CI' `
  -AuthorizedProtectedRead -ShowDiff
$LASTEXITCODE
```

Run this only from an elevated PowerShell after the administrator has authorized reading those files. The script compares candidate and discovered snapshots by source signature and behavior, reports raw and workflow-normalized SHA-256, reports snapshot and parent ACLs/owners and potential write grants, rejects reparse points, and reports runner/service/process/data-path metadata without opening database contents. `-ShowDiff` must only be used in a private local console.

Exit codes are `0` for matching normalized source, `10` for a content discrepancy, `20` for an access-denied or unauthorized read, and `30` for missing/ambiguous snapshots, unsafe path state, or another audit error. A matching hash is evidence of identity; it is not approval by itself. The ACL listing is an audit aid and does not replace Windows effective-access review.

If access is denied, close the current console and start PowerShell using **Run as administrator** only after authorization. Then run the command above. Do not use `Start-Process -Verb RunAs`, change ACLs, take ownership, or copy candidate files as a workaround.

## Host isolation decision

The audit reports runner service accounts and matching HISPANIA services/processes, the configured fixture and helper roots, the workflow concurrency group, and metadata for known HISPANIA data roots and SQLite file paths. It does not inspect database contents. A clean inventory alone does not prove a host is disposable or free of production dependencies.

Do not run SCM on `JOAQUIM` unless an administrator verifies there are no production services, production data, credentials, shared workloads, or concurrent administrative jobs on that host and confirms the runner identity cannot alter protected snapshots. The GitHub workflow's `phase21-self-hosted` concurrency group serializes this workflow group, but does not serialize unrelated local jobs or administrator activity.

If that cannot be proven, use a disposable Windows 11/Server VM with:

- No production files, databases, credentials, certificates, or network shares; use only generated fixture secrets and certificates.
- A dedicated, non-administrator runner account with no interactive logon and no access to production resources. The runner account may invoke only the fixed named-pipe protocol.
- A separate administrator-owned broker using the reviewed helper generation. The broker accepts only the fixed operation allowlist, verifies fixture GUID, service name, executable, home, role, service account, and active PID before each operation, and logs operation metadata.
- Outbound networking limited to GitHub Actions requirements and inbound networking disabled except local fixture ports. Use a one-job/ephemeral runner registration and destroy or revert the VM after evidence is collected.
- A unique service root such as `C:\ProgramData\HispaniaPhase18CI`, unique per-run service names, a single-job concurrency policy, and no production services or data mounted into the VM.
- A pinned .NET 10 SDK/runtime and the workflow's required tooling. Do not install a hypervisor or create/configure a VM on the current host under this runbook.

## Independent helper approval

1. Select the exact candidate commit. Require a clean worktree and record the full Git SHA.
2. Run the read-only audit above in a private elevated console. Review the complete behavior diff for both helpers, their candidate and protected hashes, service actions, path rules, caller identity, ACL changes, recovery settings, and audit behavior. Do not approve based solely on matching the Git HEAD.
3. Have a second administrator record the repository, candidate SHA, raw and normalized SHA-256 for both files, approver identity, UTC timestamp, review reference, decision, and the audit output's secure local location. Keep this record outside public job logs.
4. After approval, stage the pair in a new versioned directory named for the full candidate SHA. Never edit a generation in place. Set owner to Administrators and protected ACLs granting write only to Administrators and SYSTEM; grant the runner/broker caller read-and-execute only. Verify file and directory ACLs, owner, hashes, and absence of reparse points after staging.
5. Start the approved broker from that exact immutable generation during a maintenance window with no candidate jobs running. Confirm its process identity, command path, pipe ACL, generation SHA, and fixed action allowlist. Keep the generation unchanged for the job. The workflow locks the approval manifest and both helper files against write/delete opens, then verifies their hashes, the running broker startup record, and `github.sha` before setting SCM enabled.
6. Revoke/retire the generation only after the run ends and its evidence is preserved. Do not replace the currently protected files or restart the existing runner/broker without the separate administrative change approval.

The workflow now requires `C:\ProgramData\HispaniaPhase21CI\active-approval.json` and the immutable `approved\<full-candidate-sha>` generation. It checks the exact repository and SHA, both workflow-normalized helper hashes, the host-isolation attestation, the runner host, a GitHub approver different from the dispatch actor, protected owner/DACLs, absence of reparse points, and an elevated broker process whose startup log records the same approval ID, SHA, identity, and hashes. It locks the manifest and helpers for the job to prevent replacement after verification. The approval manifest is protected by Windows ACLs and references a separate review record; it is not a cryptographic signature. Do not treat the manifest alone as an approval without reviewing that record and the private audit evidence.

The manifest schema required by the current workflow is:

```json
{
  "schemaVersion": 1,
  "repository": "joaquimcastilla/hispania",
  "sourceSha": "<40 lowercase hex characters>",
  "approvalId": "<review or ticket ID>",
  "approvalReference": "<review or ticket reference>",
  "approvedByWindows": "<Windows administrator identity>",
  "approvedByGitHub": "<GitHub reviewer, different from the dispatch actor>",
  "approvedAtUtc": "<ISO 8601 UTC timestamp>",
  "hostIsolationVerified": true,
  "hostIsolationEvidence": "<private audit evidence reference>",
  "runnerHost": "<isolated runner computer name>",
  "brokerOwnerSid": "<administrator SID>",
  "nativeHelperSha256": "<workflow-normalized SHA-256>",
  "windowsServiceSha256": "<workflow-normalized SHA-256>"
}
```

Before launching the broker, the administrator must set `HISPANIA_PHASE21_APPROVED_GIT_SHA`, `HISPANIA_PHASE21_APPROVAL_ID`, `HISPANIA_PHASE21_APPROVED_NATIVE_SHA256`, and `HISPANIA_PHASE21_APPROVED_SERVICE_SHA256` from this reviewed record. The helper independently requires an elevated token and verifies those values against the immutable generation. The workflow's `hispania-windows-scm-isolated` runner label is intentionally separate from the existing `hispania-windows` label; provision it only on the disposable isolated runner.

## Controlled SCM recovery test

`NativeServiceTests` now requests `crash-test` only through the restricted helper on Windows. The broker accepts it only for the run-GUID service under the exact fixture root, confirms the SCM `PathName`, `LocalService` account, running PID, and executable image twice, and terminates the held process handle only if the image still matches. It returns the PID and logs a fixed operation record. It does not accept a PID or command from the workflow request.

The test requires SCM to restart the service with a new PID, checks readiness/health (or the worker's next authenticated handshake), checks retained fixture state, and requires the SCM failure event 7031. The server path bootstraps a fixture-only administrator and verifies login again after recovery. Cleanup continues through the existing fixture ownership checks. This remains **unverified on a real runner** until the administrator approves the protected helper generation and the isolated runner.

## Validation commands

```powershell
$scripts = @(
  'tools/ci/audit-windows-privileged-runner.ps1',
  'tools/ci/windows-native-service-helper.ps1',
  'deployment/windows-service.ps1',
  'tools/ci/test-windows-native-helper.ps1',
  'tools/ci/test-windows-service-preflight.ps1'
)
foreach ($script in $scripts) {
  $tokens = $null; $errors = $null
  [System.Management.Automation.Language.Parser]::ParseFile((Resolve-Path $script), [ref]$tokens, [ref]$errors) | Out-Null
  if ($errors.Count) { throw "$script : $($errors -join '; ')" }
}
./tools/ci/test-windows-service-preflight.ps1
dotnet build tests/deployment-process/Hispania.Deployment.ProcessTests.csproj -c Release --no-restore
actionlint .github/workflows/*.yml
git diff --check
```

The native helper integration test and `NativeServiceTests` require the approved elevated broker and isolated runner; do not substitute the local production host. A build or a successful workflow that omits SCM is not SCM evidence.
