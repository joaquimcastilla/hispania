# Production operator quick start

Use a trusted `win-x64` or `linux-x64` distribution with its independently obtained
manifest SHA-256. Install the x64 .NET 10 ASP.NET Core runtime. Install FFmpeg and
FFprobe for Media operations that use analysis or transcoding. The application
package is framework-dependent. Copy the package without source/Git/SDK/Node.
Choose an absolute installation home; spaces and Unicode are supported. Delivery
and installation paths must be distinct and nonoverlapping.

```sh
dotnet /delivery/deployment/Hispania.Deployment.dll verify /delivery
dotnet /delivery/deployment/Hispania.Deployment.dll install /delivery /srv/hispania server
dotnet /srv/hispania/app/server/Hispania.Server.dll --home /srv/hispania --check
```

Review and edit `/srv/hispania/config/production.json`. The default endpoint is
`http://127.0.0.1:5080` and the default Web build is Modern. HTTP binding is
restricted to loopback; configure HTTPS before remote access. Use the
[configuration reference](CONFIGURATION-REFERENCE.md) and [secret provisioning
guide](SECRET-PROVISIONING.md). Configure `bootstrapUsername`, `bootstrapSecret`
and `bootstrapPasswordSecret` references, then provision the first operator
locally:

```sh
dotnet /srv/hispania/app/server/Hispania.Server.dll --home /srv/hispania --bootstrap
```

After the bootstrap token is consumed, remove the bootstrap references and
username from public configuration and remove transient secret material. Start
the server in the foreground with:

```sh
dotnet /srv/hispania/app/server/Hispania.Server.dll --home /srv/hispania
```

Open `http://127.0.0.1:5080/` on the local host to reach the configured Web Shell;
confirm `/livez` and `/readyz`. Use HTTPS and the configured endpoint for remote
access. These commands install and run the Server directly. The native service
templates below are available, but Phase 25 acceptance did not install systemd or
Windows SCM services.

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
