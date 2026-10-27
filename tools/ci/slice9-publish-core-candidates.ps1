$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$expectedRef = 'feature/slice9-core-native-smoke'
$sourceSha = $env:GITHUB_SHA
if ($env:GITHUB_REF_NAME -cne $expectedRef -or $sourceSha -notmatch '^[0-9a-f]{40}$') { throw 'Unexpected source identity.' }
if ((& git -C $repoRoot rev-parse HEAD).Trim() -cne $sourceSha) { throw 'Checked-out source SHA differs from the workflow SHA.' }
if (@(& git -C $repoRoot status --porcelain --untracked-files=normal).Count -ne 0) { throw 'The source checkout is not clean.' }

$outputRoot = Join-Path $env:RUNNER_TEMP ("hispania-slice9-candidates-{0}-{1}" -f $env:GITHUB_RUN_ID, $env:GITHUB_RUN_ATTEMPT)
if (Test-Path -LiteralPath $outputRoot) { throw 'Run output path already exists.' }
$tempRoot = [IO.Path]::GetFullPath($env:RUNNER_TEMP).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
if (-not [IO.Path]::GetFullPath($outputRoot).StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'Output path is outside runner temporary storage.' }
New-Item -ItemType Directory -Path $outputRoot | Out-Null
"output_root=$outputRoot" | Add-Content -LiteralPath $env:GITHUB_OUTPUT -Encoding utf8

function Invoke-Dotnet([string[]]$Arguments) {
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet failed with exit code $LASTEXITCODE." }
}

$policy = Join-Path $repoRoot 'docs/architecture/HISPANIA-V1-CORE-ARTIFACT-ALLOWLIST-v1.json'
$validatorProject = Join-Path $repoRoot 'tools/core-artifact-validator/Hispania.CoreArtifactValidator.csproj'
$validatorDll = Join-Path $repoRoot 'tools/core-artifact-validator/bin/Release/net10.0/Hispania.CoreArtifactValidator.dll'
$fixtureProject = Join-Path $repoRoot 'tests/fixtures/slice7-fixture/Hispania.Slice7.Fixture.csproj'
$fixtureAssembly = Join-Path $repoRoot 'tests/fixtures/slice7-fixture/bin/Release/net10.0/Hispania.Slice7.Fixture.dll'
$packagerProject = Join-Path $repoRoot 'tools/ci/slice9-fixture-packager/Slice9.FixturePackager.csproj'
$packagerDll = Join-Path $repoRoot 'tools/ci/slice9-fixture-packager/bin/Release/net10.0/Slice9.FixturePackager.dll'

Invoke-Dotnet @('restore', $validatorProject, '--verbosity', 'quiet', '-p:NuGetAudit=false')
Invoke-Dotnet @('build', $validatorProject, '--configuration', 'Release', '--no-restore', '--verbosity', 'quiet')
Invoke-Dotnet @('restore', $fixtureProject, '--verbosity', 'quiet', '-p:NuGetAudit=false')
Invoke-Dotnet @('build', $fixtureProject, '--configuration', 'Release', '--no-restore', '--verbosity', 'quiet')
Invoke-Dotnet @('restore', $packagerProject, '--verbosity', 'quiet', '-p:NuGetAudit=false')
Invoke-Dotnet @('build', $packagerProject, '--configuration', 'Release', '--no-restore', '--verbosity', 'quiet')

$rids = @('linux-x64', 'linux-arm64')
foreach ($rid in $rids) {
    $bundle = Join-Path $outputRoot $rid
    $package = Join-Path $bundle 'package'
    New-Item -ItemType Directory -Path $package -Force | Out-Null

    Invoke-Dotnet @('restore', (Join-Path $repoRoot 'apps/core/Hispania.Core.csproj'), '--runtime', $rid, '--verbosity', 'quiet', '-p:NuGetAudit=false')
    Invoke-Dotnet @('publish', (Join-Path $repoRoot 'apps/core/Hispania.Core.csproj'), '--configuration', 'Release', '--runtime', $rid,
        '--self-contained', 'false', '--no-restore', '--output', $package,
        '-p:UseAppHost=true', '-p:Deterministic=true', '-p:ContinuousIntegrationBuild=true', '-p:DebugSymbols=false', '-p:DebugType=none',
        '-p:UseSharedCompilation=false', '-p:BuildInParallel=false', '-m:1', '-nr:false', '--nologo', '--verbosity', 'quiet')

    $descriptor = [ordered]@{
        schemaVersion = 1; artifactId = 'hispania.core'; sourceCommit = $sourceSha; targetFramework = 'net10.0'; runtimeIdentifier = $rid
        publishMode = 'framework-dependent'
        dependencies = @(
            @{ kind='project'; id='core/runtime/Hispania.Runtime.csproj'; version='source'; phase='build'; direct=$true },
            @{ kind='project'; id='apps/module-host/Hispania.ModuleHost.csproj'; version='source'; phase='build'; direct=$true },
            @{ kind='project'; id='contracts/module-system/Hispania.ModuleSystem.Contracts.csproj'; version='source'; phase='build'; direct=$true },
            @{ kind='project'; id='contracts/platform/Hispania.Platform.Contracts.csproj'; version='source'; phase='build'; direct=$true },
            @{ kind='package'; id='Microsoft.Data.Sqlite'; version='10.0.12'; phase='build'; direct=$true },
            @{ kind='package'; id='NuGet.Versioning'; version='7.9.0'; phase='build'; direct=$true }
        )
    }
    $descriptorPath = Join-Path $outputRoot "$rid-descriptor.json"
    $descriptor | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $descriptorPath -Encoding utf8NoBOM
    $createOutput = & dotnet $validatorDll create --root $package --descriptor $descriptorPath --policy $policy
    if ($LASTEXITCODE -ne 0) { throw "Official Core artifact validator rejected $rid candidate." }
    $report = $createOutput | ConvertFrom-Json
    if (-not $report.passed -or $report.issues.Count -ne 0) { throw "Official validator did not return a clean PASS for $rid." }
    $validateOutput = & dotnet $validatorDll validate --root $package --policy $policy
    if ($LASTEXITCODE -ne 0 -or -not ($validateOutput | ConvertFrom-Json).passed) { throw "Official manifest validation failed for $rid." }

    $manifestPath = Join-Path $package 'core-artifact-manifest.json'
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    if ($manifest.sourceCommit -cne $sourceSha -or $manifest.runtimeIdentifier -cne $rid -or $manifest.targetFramework -cne 'net10.0' -or $manifest.publishMode -cne 'framework-dependent') { throw "Manifest provenance mismatch for $rid." }
    $actualFiles = @(Get-ChildItem -LiteralPath $package -File -Recurse | ForEach-Object { [IO.Path]::GetRelativePath($package, $_.FullName).Replace('\','/') } | Sort-Object)
    $manifestFiles = @($manifest.files | ForEach-Object { $_.path } | Sort-Object)
    if (($actualFiles -join "`n") -cne (($manifestFiles + 'core-artifact-manifest.json' | Sort-Object) -join "`n")) { throw "Published file inventory differs from manifest for $rid." }
    $hashes = [ordered]@{}
    foreach ($file in Get-ChildItem -LiteralPath $package -File -Recurse | Sort-Object FullName) {
        $relative = [IO.Path]::GetRelativePath($package, $file.FullName).Replace('\','/')
        $hashes[$relative] = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    }
    $manifestSha = (Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash.ToLowerInvariant()
    $integrity = [ordered]@{
        sourceCommit = $sourceSha; runtimeIdentifier = $rid; architecture = $(if ($rid -eq 'linux-arm64') {'aarch64'} else {'x86_64'})
        targetFramework = 'net10.0'; publishMode = 'framework-dependent'; contentId = $manifest.contentId
        manifestSha256 = $manifestSha; fileCountIncludingManifest = $hashes.Count; files = $hashes
    }
    $integrity | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $bundle 'integrity.json') -Encoding utf8NoBOM
    $integritySha = (Get-FileHash -LiteralPath (Join-Path $bundle 'integrity.json') -Algorithm SHA256).Hash.ToLowerInvariant()
    $prefix = $rid.Replace('-', '_')
    "${prefix}_artifact=hispania-core-$rid-$sourceSha" | Add-Content -LiteralPath $env:GITHUB_OUTPUT -Encoding utf8
    "${prefix}_content_id=$($manifest.contentId)" | Add-Content -LiteralPath $env:GITHUB_OUTPUT -Encoding utf8
    "${prefix}_manifest_sha256=$manifestSha" | Add-Content -LiteralPath $env:GITHUB_OUTPUT -Encoding utf8
    "${prefix}_integrity_sha256=$integritySha" | Add-Content -LiteralPath $env:GITHUB_OUTPUT -Encoding utf8
    Write-Output "Validated $rid candidate: contentId=$($manifest.contentId); manifestSha256=$manifestSha; files=$($hashes.Count)"
}

$fixtureRoot = Join-Path $outputRoot 'fixture'
New-Item -ItemType Directory -Path $fixtureRoot | Out-Null
$contractAssembly = Join-Path $outputRoot 'linux-x64/package/Hispania.ModuleSystem.Contracts.dll'
Invoke-Dotnet @($packagerDll, $fixtureAssembly, $contractAssembly, (Join-Path $fixtureRoot 'fixture.hispania'), (Join-Path $fixtureRoot 'publisher-public.pem'))
$fixtureHashes = [ordered]@{}
foreach ($name in @('fixture.hispania','publisher-public.pem')) { $fixtureHashes[$name] = (Get-FileHash -LiteralPath (Join-Path $fixtureRoot $name) -Algorithm SHA256).Hash.ToLowerInvariant() }
([ordered]@{ sourceCommit=$sourceSha; publisherId='hispania.slice7.test'; files=$fixtureHashes } | ConvertTo-Json -Depth 4) |
    Set-Content -LiteralPath (Join-Path $fixtureRoot 'integrity.json') -Encoding utf8NoBOM
$fixtureIntegritySha = (Get-FileHash -LiteralPath (Join-Path $fixtureRoot 'integrity.json') -Algorithm SHA256).Hash.ToLowerInvariant()
"fixture_artifact=hispania-slice9-fixture-$sourceSha" | Add-Content -LiteralPath $env:GITHUB_OUTPUT -Encoding utf8
"fixture_integrity_sha256=$fixtureIntegritySha" | Add-Content -LiteralPath $env:GITHUB_OUTPUT -Encoding utf8
Write-Output "Prepared signed OOP fixture bundle; sourceCommit=$sourceSha"
