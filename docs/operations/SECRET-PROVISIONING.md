# Private provisioning and rotation

Public configuration contains references `env:NAME` or `file:NAME`, with bounded
uppercase letters/digits/underscore. `env:MEDIA_PASSWORD` reads
`HISPANIA_SECRET_MEDIA_PASSWORD` for the service identity. `file:MEDIA_PASSWORD`
reads `HOME/secrets/MEDIA_PASSWORD.secret`. The host abstraction is internal host
infrastructure, not a new independently released module SDK. Missing references
are reported by opaque name; values are never enumerated in diagnostics.

Protected local files are plaintext. Permissions are not encryption, a vault,
hardware protection or protection against system administrators. Linux x64 checks
effective-user ownership, file mode 0600/0400 and enclosing directory mode 0700.
Windows checks file owner and every allow ACL: only effective service identity,
SYSTEM and Administrators may read/write/control material. Reparse points reject.
Use the native service installer to grant the dedicated account and reprovision
private files with matching ownership. Do not store secret values in shell history,
command-line arguments, source or public config; enter them through protected
provisioning tooling/environment injection. Environment provisioning is ephemeral;
SCM/systemd must receive it explicitly if selected, rather than assuming your shell
environment reaches a service. Protected files are the portable default procedure.

Private classification: PostgreSQL password; bootstrap token; initial operator
password; TLS PFX/private key and its password; Worker mTLS PFX/private key and its
password; future provider credentials. Public certificates, certificate pins and
publisher verification keys are public trust material. Do not use private publisher
keys in trustedPublisherKeys. No test release signing keys are production material.

Bootstrap is a local, offline owner-contract operation:

```sh
dotnet HOME/app/server/Hispania.Server.dll --home HOME --bootstrap
```

Configure explicit bootstrapUsername and secret references beforehand. Consumption
is persisted by existing Authorization; remove both bootstrap references, username
and transient secret material afterward. Normal startup and restored Identity do
not need the consumed token or initial password provisioning secret. The persisted
password hash is owner state and is distinct from plaintext credentials.

Rotation is offline: stop the service, rotate the external credential/private file,
update only its provider material/reference if necessary, run `--check`, start and
verify Ready/negotiation. Never rewrite owner data for a password change. The Linux
CI fixture requires SCRAM password authentication, verifies old password rejection,
then starts with the replacement and restores into a fresh database using freshly
provisioned material. Trust-auth PostgreSQL cannot establish that evidence.

After disaster, restore public owner data with Phase 17, reapply public installation
config, reprovision the private files/environment and original media mounts, then
start. Secrets/certificates/config paths outside state are excluded from backups.
Logs redact resolved secret values and suppress exception details. Health/admin
projections use existing safe DTOs; no configuration/secret inventory API is added.

Windows Schannel uses the service identity’s native protected key container when loading a client PFX; Linux production uses ephemeral key material. Windows service profile/key-container access must be provisioned, and native containers remain outside managed backup state. No cross-platform encrypted-vault claim is made.
