# Phase21 self-hosted acceptance hosts

Validation and Production Deployment are manual-only on the trusted Phase21 branch.
Jobs select `[self-hosted, hispania, hispania-windows]` or
`[self-hosted, hispania, hispania-linux]`. Both workflows share a concurrency group;
no push/PR trigger, fork execution, hosted runner or runner reinstallation is used.
The historical billing failure remains in the operator CI evidence ledger.

Windows runner 21, `hispania-win-ci` (2.338.0), retains NetworkService. Shared
PowerShell and FFmpeg binaries live in administrator-owned
`C:\ProgramData\HispaniaPhase21CI`; NetworkService has read/execute access.
The existing service is restarted to load the shared tool PATH. Native Python
3.14.7 is copied as a read-only shared tool. .NET 10 and Node 24 are selected by
setup actions in runner-writable tool directories. Linux uses Ubuntu Python 3.12.
The Python setup action attempted to invoke an installer under Windows Restricted
policy; preprovisioned Python avoids granting installer elevation or relaxing policy.

An interactively UAC-elevated, eight-hour broker runs the protected copy of
`tools/ci/windows-native-service-helper.ps1`. Its named pipe grants NetworkService
read/write and SYSTEM/Administrators full control. It accepts only install/start/stop/restart/status/
uninstall/reclaim for server/worker, under `C:\ProgramData\HispaniaPhase18CI`,
with exact `hispania-phase18 isolated ñ <32 lowercase hex>` fixture basenames.
It rejects reparse points and hardlinked files and invokes a protected deployment script; no submitted
PowerShell command or script path is accepted. Server and Worker stay LocalService.
The broker is a temporary administrative process, not an elevated CI runner.

Linux runner 22, `hispania-linux-ci` (2.338.0), retains `gha-hispania` on native
Ubuntu 24.04. Dependencies are installed administratively: PostgreSQL 16 server/
client, unzip, Python alias, Chromium shared libraries/fonts, and Microsoft Ubuntu
24.04 packages for PowerShell and .NET 10. FFmpeg/FFprobe already existed. Setup
actions select exact requested major versions; Chromium comes from the locked
Playwright dependency without runtime sudo installation.

Root-owned `/usr/local/sbin/hispania-phase21-service` is an installed copy of
`tools/ci/native-service-helper.py`. Root-owned deployment script/template snapshots
are in `/usr/local/lib/hispania-phase21`. The only runner sudo grant is:

```sudoers
gha-hispania ALL=(root) NOPASSWD: /usr/local/sbin/hispania-phase21-service
```

`visudo -cf /etc/sudoers.d/hispania-actions` validates this file. The helper validates
actions, role and exact fixture basenames under `/opt/hispania-phase18-ci`, rejects
symlinks/hardlinks, uses fixed commands, a clean environment and a fixed root-owned
script/template. It provides bounded journal queries, retained-secret existence and
ownership reclamation solely for these fixtures. After a verified inactive stop,
the helper hands non-secret fixture files back to the CI operator for the real
offline lock and retained-state checks; secrets remain owned/private to the
service account until the final uninstall/reclaim. No runner sudo shell, generic
chown, useradd, env, wildcard commands or unrestricted systemctl is granted.
Native tests still execute all original lifecycle, identity, recovery configuration,
readiness, stop, retained-state and secret-log assertions. The Linux service account
is `hispania` with nonzero UID; native test fixture data is reclaimed and removed.
The dedicated empty service root and non-login service account remain reusable.

PostgreSQL has an isolated `16/hispania_ci` cluster on loopback port 55421 with
SCRAM host authentication. Existing databases on other clusters are not modified.
`hispania_ci` is a non-superuser CI login with CREATEDB/CREATEROLE and
`createrole_self_grant=set,inherit`, allowing cleanup of only its generated roles/
databases. `hispania_tests` is its test database. Random credentials are stored in
`/etc/hispania-phase21/postgres.json`, root:gha-hispania 0640, parent 0750;
they are never committed or printed. Preflight masks credentials before exporting
connections through GitHub's job environment. No GitHub secret is required.
Phase18 creates independent random A/B passwords and proves old rejection, new
acceptance and secret reprovision against this real authenticated instance.

Only administrator-owned helper snapshots may be updated administratively after
review. The runner cannot replace them. These controls assume trusted branch code
and private fixture roots; they do not establish a sandbox against hostile code
racing filesystem changes under the same OS identity.

Acceptance run IDs, exact SHA, tool versions, test totals, artifact checksums,
canary traversal counts and any failures are recorded separately in
`.artifacts/phase21-self-hosted/PHASE-21-SELF-HOSTED-CI.md`. No green result is
inferred from provisioning alone.
