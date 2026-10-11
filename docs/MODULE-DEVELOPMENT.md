# External module development

This guide describes the current experimental `.hispania` extension path in HISPANIA 0.7.0 RC1. Contracts and package format are pre-1.0 and may change. Native extensions are trusted code: in-process modules receive Server process privileges. Do not install code from a publisher you do not trust.

## 1. Implement a public contract

Create a separate .NET 10 project and reference the contract assembly that defines the capability plus `Hispania.ModuleSystem.Contracts`. Implement `IModule` and the capability contract. Do not reference the Server project or another extension's implementation assembly. Use `modules/extension-demo` as the small greeting example.

The Server does not offer a universal dependency injection or permissions sandbox to extensions. Module permissions in a manifest are declarations. The operator separately controls the allowed extension ID and publisher trust. For out-of-process modules, use a public versioned contract that fits ModuleHost protocol V1's bounded JSON request/response model.

## 2. Publish and package

Publish into a clean output directory, then pass the output and manifest template to the official packager:

```powershell
dotnet publish .\MyExtension.csproj -c Release -o .artifacts\my-extension
./tools/package-extension.ps1 `
  -PublishDirectory .artifacts\my-extension `
  -ManifestTemplate .\module.template.json `
  -OutputPath .artifacts\my-extension-1.0.0.hispania `
  -PublisherId 'example.publisher' `
  -SigningKeyPath $env:HISPANIA_PUBLISHER_PRIVATE_KEY
```

Use `packageFormatVersion: 2` for package SemVer and dependency ranges; format 2 requires `packageVersion` and `packageDependencies`. Declare exact capability IDs/versions, entry assembly/type, runtime, supported host range and shared contract assemblies. The packager records SHA-256 for every payload file and refuses to overwrite an existing archive. A digest detects changed bytes; the publisher signature authenticates a configured publisher. Keep the private key outside the repository and configure its public key in operator-controlled `Modules:TrustedPublisherKeys`. Format 2 graph changes currently use the Runtime package-set application service; they do not have a general public upload/plan/apply CLI or REST flow.

## 3. Validate and install locally

The server's authenticated local admin endpoint accepts the package bytes; it does not fetch a remote URL. Configure `Modules:AdminToken` and TLS/network exposure safely before use:

```powershell
curl.exe --fail-with-body -X POST `
  -H "Authorization: Bearer $env:HISPANIA_MODULE_ADMIN_TOKEN" `
  --data-binary "@.artifacts/my-extension-1.0.0.hispania" `
  http://localhost:5080/internal/admin/modules/extensions/packages
```

Installation validates archive limits, paths, manifest/host compatibility, contracts, payload list and hashes, and stores the bytes inertly. It does not authorize or run the module. Configure the exact ID in `Modules:TrustedExtensionIds`; trust signature, publisher revocation and module ID authorization are separate checks. Format 2 always requires a trusted publisher signature. Keep keys and grants in protected deployment configuration.

## 4. Discover, activate and exercise

Use the authenticated admin API to list extensions, then activate the authorized ID:

```powershell
curl.exe -H "Authorization: Bearer $env:HISPANIA_MODULE_ADMIN_TOKEN" `
  http://localhost:5080/internal/admin/modules/extensions
curl.exe -X POST -H "Authorization: Bearer $env:HISPANIA_MODULE_ADMIN_TOKEN" `
  http://localhost:5080/internal/admin/modules/extensions/example.extension/activate
```

Consume the extension's declared public capability from an application using the typed catalog/lease contract; do not add a privileged host-only shortcut. Confirm runtime health and test invalid signature, unknown publisher, unauthorized ID, incompatible contract and dependency failures. The greeting demo's Server HTTP projection is specific to that sample contract and is not a universal API for all modules.

## 5. Update, rollback, deactivate and remove

Install a new version alongside the current one, explicitly update to it, verify health and behavior, and keep the prior package for rollback. Candidate startup or health failure should leave the old generation active. Deactivation drains admitted calls; unload releases the stopped module's load context; physical uninstall is a separate operation and requires the module to be unloaded. Format 2 graph changes use `ModuleExtensionCatalog` planning/application operations; package dependencies do not grant trust, authorization or implementation access. Current trust and authorization still apply to rollback.

No package registry or automatic download exists. Activation after Server restart is operator initiated. Database migrations are not automatically rolled back with package rollback. Review [Module System](MODULE_SYSTEM.md), [Phase 19 lifecycle](PHASE-19-PACKAGE-LIFECYCLE.md), [Security Model](SECURITY_MODEL.md) and the [0.7.0 modularity audit](MODULARITY-AUDIT-0.7.0.md) before deployment.
