[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('win-x64','linux-x64','linux-arm64')][string]$Rid,
    [Parameter(Mandatory)][string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$outputRoot = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $outputRoot) { throw 'Output must be a fresh directory; previous artifacts are never overwritten.' }

Push-Location $repoRoot
try {
    $sourceSha = (& git rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0 -or $sourceSha -notmatch '^[0-9a-fA-F]{40}$') { throw 'Source commit SHA unavailable.' }
    $branch = (& git branch --show-current).Trim()
    if (-not $branch -and $env:GITHUB_REF_NAME) { $branch = $env:GITHUB_REF_NAME }
    if (-not $branch) { throw 'Source branch/tag is unavailable; set GITHUB_REF_NAME for detached builds.' }
    $sourceDirty = @(& git status --porcelain --untracked-files=normal).Count -ne 0
    $version = (Get-Content -LiteralPath 'version.json' -Raw | ConvertFrom-Json).distributionVersion
    $buildUtc = if ($sourceDirty) { [DateTimeOffset]::UtcNow } else {
        $epoch = (& git show -s --format=%ct $sourceSha).Trim()
        if ($LASTEXITCODE -ne 0 -or $epoch -notmatch '^\d+$') { throw 'Clean commit timestamp unavailable.' }
        [DateTimeOffset]::FromUnixTimeSeconds([long]$epoch)
    }
    $architecture = if ($Rid -eq 'linux-arm64') { 'aarch64' } else { 'x86_64' }
    $runtime = if ($Rid -eq 'linux-arm64') { 'Microsoft.AspNetCore.App 10.0 (ARM64)' } else { 'Microsoft.AspNetCore.App 10.0 (x64)' }

    New-Item -ItemType Directory -Path $outputRoot | Out-Null
    & dotnet publish 'apps/server/Hispania.Server.csproj' -c Release -r $Rid --self-contained false -p:PlatformOnly=true -p:ProductionPublication=true -p:UseAppHost=false ('-p:PathMap='+$repoRoot+'=/_/hispania') --nologo -v quiet -o (Join-Path $outputRoot 'server')
    if ($LASTEXITCODE -ne 0) { throw 'Platform-only server publication failed.' }
    & dotnet publish 'apps/deployment/Hispania.Deployment.csproj' -c Release -r $Rid --self-contained false -p:PlatformOnly=true -p:UseAppHost=false ('-p:PathMap='+$repoRoot+'=/_/hispania') --nologo -v quiet -o (Join-Path $outputRoot 'deployment')
    if ($LASTEXITCODE -ne 0) { throw 'Deployment CLI publication failed.' }

    $files = [ordered]@{}
    foreach ($file in (Get-ChildItem -LiteralPath $outputRoot -File -Recurse -Force | Sort-Object FullName)) {
        $relative = [IO.Path]::GetRelativePath($outputRoot, $file.FullName).Replace('\','/')
        $files[$relative] = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    }
    $manifest = [ordered]@{
        manifestGeneration=1; distributionVersion=$version; distributionType='platform-only'
        gitSha=$sourceSha; sourceBranch=$branch; sourceDirty=$sourceDirty; targetRid=$Rid; architecture=$architecture
        buildConfiguration='Release'; publishProfile='platform-only-production'; dotnetSdkVersion=(& dotnet --version).Trim()
        runtimePrerequisite=$runtime; buildUtc=$buildUtc.ToUniversalTime().ToString('O'); files=$files
    }
    $manifestPath = Join-Path $outputRoot 'artifact-manifest.json'
    $manifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $manifestPath -Encoding utf8NoBOM
    $manifestHash = (Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash.ToLowerInvariant()
    Set-Content -LiteralPath ($outputRoot + '.manifest.sha256') -Value $manifestHash -Encoding ascii
    [ordered]@{ sourceDirty=$sourceDirty; sourceSha=$sourceSha; sourceBranch=$branch; distributionType='platform-only'; targetRid=$Rid; buildUtc=$buildUtc.ToUniversalTime().ToString('O') } |
        ConvertTo-Json | Set-Content -LiteralPath ($outputRoot + '.build-evidence.json') -Encoding utf8NoBOM

    $verifier = Join-Path $outputRoot 'deployment/Hispania.Deployment.dll'
    if ($Rid -eq 'linux-arm64') {
        & dotnet build 'apps/deployment/Hispania.Deployment.csproj' -c Release -p:PlatformOnly=true -p:UseAppHost=false --nologo -v quiet
        if ($LASTEXITCODE -ne 0) { throw 'Host-native Deployment verifier build failed.' }
        $verifier = Join-Path $repoRoot 'apps/deployment/bin/Release/net10.0/Hispania.Deployment.dll'
    }
    & dotnet $verifier verify $outputRoot
    if ($LASTEXITCODE -ne 0) { throw 'Published Platform-only artifact verification failed.' }

    $packagePath = $outputRoot.TrimEnd([IO.Path]::DirectorySeparatorChar) + '.zip'
    Compress-Archive -Path (Join-Path $outputRoot '*') -DestinationPath $packagePath -CompressionLevel Optimal
    $packageHash = (Get-FileHash -LiteralPath $packagePath -Algorithm SHA256).Hash.ToLowerInvariant()
    Set-Content -LiteralPath ($packagePath + '.sha256') -Value $packageHash -Encoding ascii
    Write-Output "Platform-only artifact: $outputRoot"
    Write-Output "Artifact manifest SHA-256: $manifestHash"
    Write-Output "Package: $packagePath"
    Write-Output "Package SHA-256: $packageHash"
    Write-Output "Traceability: $sourceSha ($branch), dirty=$sourceDirty, $Rid, $runtime, platform-only-production"
}
finally { Pop-Location }
