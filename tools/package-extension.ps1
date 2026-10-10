param(
    [Parameter(Mandatory = $true)][string]$PublishDirectory,
    [Parameter(Mandatory = $true)][string]$ManifestTemplate,
    [Parameter(Mandatory = $true)][string]$OutputPath,
    [string]$PublisherId,
    [string]$SigningKeyPath
)

$ErrorActionPreference = 'Stop'
$publishRoot = (Resolve-Path -LiteralPath $PublishDirectory).Path
$templatePath = (Resolve-Path -LiteralPath $ManifestTemplate).Path
$outputFullPath = [System.IO.Path]::GetFullPath($OutputPath)
if ($outputFullPath.StartsWith($publishRoot.TrimEnd([System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase)) { throw 'Output package must be outside the publish directory.' }
if (Test-Path -LiteralPath $outputFullPath) { throw "Refusing to overwrite existing package: $outputFullPath" }
$manifest = Get-Content -Raw -LiteralPath $templatePath | ConvertFrom-Json -AsHashtable
$manifest.files = [ordered]@{}
$files = Get-ChildItem -LiteralPath $publishRoot -File -Recurse | Sort-Object FullName
if ($files.Count -gt 511) { throw 'Extension package exceeds the 511 payload file limit.' }
foreach ($file in $files) {
    $relative = [System.IO.Path]::GetRelativePath($publishRoot, $file.FullName).Replace('\', '/')
    if ($relative.Split('/') -contains '..' -or $relative.StartsWith('/')) { throw "Unsafe package path: $relative" }
    $manifest.files[$relative] = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
}
if (-not $manifest.files.Contains($manifest.entryAssembly)) { throw 'The entry assembly is missing from the publish directory.' }
if (($PublisherId -and -not $SigningKeyPath) -or ($SigningKeyPath -and -not $PublisherId)) { throw 'PublisherId and SigningKeyPath must be supplied together.' }
if ($SigningKeyPath) {
    $keyPath = (Resolve-Path -LiteralPath $SigningKeyPath).Path
    $repoRoot = Split-Path -Parent $PSScriptRoot
    $contractAssembly = Join-Path $repoRoot 'contracts/module-system/bin/Release/net10.0/Hispania.ModuleSystem.Contracts.dll'
    if (-not (Test-Path -LiteralPath $contractAssembly)) {
        & dotnet build (Join-Path $repoRoot 'contracts/module-system/Hispania.ModuleSystem.Contracts.csproj') --configuration Release
        if ($LASTEXITCODE -ne 0) { throw 'Could not build package-signature support.' }
    }
    Add-Type -Path $contractAssembly
    $options = [System.Text.Json.JsonSerializerOptions]::new()
    $options.PropertyNameCaseInsensitive = $true
    $options.PropertyNamingPolicy = [System.Text.Json.JsonNamingPolicy]::CamelCase
    $manifestJson = $manifest | ConvertTo-Json -Depth 64
    $typedManifest = [System.Text.Json.JsonSerializer]::Deserialize($manifestJson, [Hispania.ModuleSystem.Contracts.ModuleManifest], $options)
    $privateKey = Get-Content -Raw -LiteralPath $keyPath
    $typedManifest = [Hispania.ModuleSystem.Contracts.ModulePackageSignature]::Sign($typedManifest, $PublisherId, $privateKey)
    $manifest = [System.Text.Json.JsonSerializer]::Serialize($typedManifest, $options) | ConvertFrom-Json -AsHashtable
}
$parent = Split-Path -Parent $outputFullPath
New-Item -ItemType Directory -Path $parent -Force | Out-Null
$staging = Join-Path $parent ('.package-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $staging | Out-Null
try {
    Copy-Item -Path (Join-Path $publishRoot '*') -Destination $staging -Recurse
    $json = $manifest | ConvertTo-Json -Depth 32
    [System.IO.File]::WriteAllText((Join-Path $staging 'module.json'), $json, [System.Text.UTF8Encoding]::new($false))
    Compress-Archive -Path (Join-Path $staging '*') -DestinationPath $outputFullPath -CompressionLevel Optimal
    if ($manifest.files.Contains('web/contributions.json') -or @($manifest.requires) -contains 'hispania.web.contributions@1.0') {
        $preflightProject = Join-Path $PSScriptRoot 'package-preflight/Hispania.PackagePreflight.csproj'
        & dotnet run --project $preflightProject --configuration Release -- $outputFullPath
        if ($LASTEXITCODE -ne 0) { throw 'Offline Web contribution validation failed; do not distribute the rejected archive.' }
    }
}
finally {
    if (Test-Path -LiteralPath $staging) { Remove-Item -LiteralPath $staging -Recurse -Force }
}
Write-Output $outputFullPath
