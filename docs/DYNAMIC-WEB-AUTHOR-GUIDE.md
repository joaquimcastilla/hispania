# Authoring Experimental Dynamic Web packages

Use Package Format 2 and the Phase 19 signing/install/dependency workflow. This is trusted CLR extensibility with a declarative Web descriptor, not a JavaScript plugin SDK.

1. Choose a stable package ID and contribution ID. The route must equal `/extensions/<package-id>/<id>` exactly. Never claim `/admin`, `/login` or a built-in route.
2. Add `web/contributions.json` using Schema Generation 1. Include its exact SHA-256 in `ModuleManifest.Files` before signing; include any referenced PNG in the same signed inventory.
3. For required UI, declare `hispania.web.contributions@1.0` in manifest and CLR `Requires`. For optional UI, omit that requirement and keep the backend independently useful. Do not claim required UI while omitting the CLR requirement: an older host relies on CLR dependency negotiation.
4. Declare a permission such as `extensions.test.package.web.view` in the manifest. Installation never assigns it to a user. Alternatively use the supported Platform observability/jobs permissions. Health data/action additionally require `platform.observability.read` on the backend.
5. `tools/package-extension.ps1` runs offline Web preflight when a descriptor or required Web capability is present. You can also run `dotnet run --project tools/package-preflight/Hispania.PackagePreflight.csproj -- <archive.hispania>`. Programmatic validation uses `WebContributionValidator.Parse(manifest, descriptorBytes)` and `PackageArchiveInspector.InspectAsync(archive)`. Run the normal package-set preflight on the target host before commit. Parsing performs no network access or module execution. Signed package trust/host policy remain separate from syntactic validation.

Minimal example (for package `test.package.web`):

```json
{
  "schemaGeneration": 1,
  "contributions": [{
    "id": "dashboard",
    "route": "/extensions/test.package.web/dashboard",
    "title": "Package status",
    "permission": "extensions.test.package.web.view",
    "navigation": { "icon": "status", "section": "PrimaryNavigation", "order": 10 },
    "components": [
      { "type": "text", "text": "Plain display text" },
      { "type": "table", "text": "Health", "operation": "platform.health" },
      { "type": "button", "text": "Check health", "operation": "platform.health.refresh" }
    ]
  }]
}
```

All strings are literal text. There is no raw HTML, CSS, script, remote URL, component import, iframe, form or arbitrary API route. Unknown properties and generations fail closed. Initial buttons accept no arguments. PNG decoration may use `navigation.asset: "web/assets/icon.png"`; keep it nonsensitive and within documented byte/dimension bounds. The host derives the hash URL and MIME, never the author.

Test both Shell bundles, keyboard navigation/focus, direct route access, partial permissions, expired sessions and lifecycle removal. Current lifecycle updates require a page reload for navigation; operation calls already revalidate current eligibility. Package update failures must preserve the prior page/backend. Use the isolated TEST ONLY fixture project for tests; never distribute its private signing material or demo DLL as a production host dependency.

The Schema 1 corpus is frozen independently of Phase 15/16 history. Schema evolution needs a new generation/corpus and required-feature negotiation. No Stable API or independent npm/package release version is promised.
