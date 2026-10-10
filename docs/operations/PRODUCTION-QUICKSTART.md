# Production operator quick start

Receive a trusted win-x64 or linux-x64 artifact and its independently obtained
manifest checksum. Install the x64 .NET 10 ASP.NET Core runtime and FFmpeg/FFprobe.
Copy the artifact without source/Git/SDK/Node. Pick an absolute HOME; spaces and
Unicode are supported. Artifact and HOME must be distinct, nonoverlapping paths.

```sh
dotnet /delivery/deployment/Hispania.Deployment.dll verify /delivery
dotnet /delivery/deployment/Hispania.Deployment.dll install /delivery /srv/hispania server
dotnet /srv/hispania/app/server/Hispania.Server.dll --home /srv/hispania --check
```

Edit `HOME/config/production.json`, provision private references (see the secret
guide), and provision the first operator with the local `--bootstrap` command.
Supply `bootstrapUsername`, `bootstrapSecret` and `bootstrapPasswordSecret` refs;
remove them and delete transient provisioning secrets after successful consumption.

Linux, as a privileged operator:

```sh
sh /delivery/operations/install-systemd.sh install /srv/hispania server hispania
sh /delivery/operations/install-systemd.sh start /srv/hispania server hispania
systemctl status hispania-server
journalctl -u hispania-server
curl --fail http://127.0.0.1:5080/livez
curl --fail http://127.0.0.1:5080/readyz
```

Windows, from an administrator PowerShell 7 terminal, replacing HOME with an
absolute Windows path:

```powershell
& 'C:\delivery\operations\windows-service.ps1' -Action install -Home 'C:\Hispania' -Role server
& 'C:\delivery\operations\windows-service.ps1' -Action start -Home 'C:\Hispania' -Role server
Invoke-WebRequest 'http://127.0.0.1:5080/readyz'
```

Never equate process/service existence with Ready. Check HTTP health and the Web
entry page. Stop/restart/status/uninstall use the same OS script and role. Stop
must finish within 40s before backup/upgrade. Service uninstall preserves all data.

For standalone Worker, use `install ... worker`, configure pinned coordinator
HTTPS/node identity and private client certificate, then `--check` using
`HOME/app/worker/Hispania.WorkerAgent.dll`; use service role `worker`. Verify actual
coordinator negotiation/worker inventory, not an HTTP Server readiness endpoint.
The Worker needs .NET but not Server-side FFmpeg unless a provisioned capability
provider explicitly requires it. ModuleHost is included in each host publication.
