[CmdletBinding()]
param(
    [ValidateSet('win-x64','linux-x64')][string]$Rid,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [switch]$SkipWebBuild
)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$outputRoot = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $outputRoot) { throw 'Output must be a fresh directory; previous artifacts are never overwritten.' }
if (-not $Rid) { $Rid = if ($IsWindows) { 'win-x64' } else { 'linux-x64' } }
Push-Location $repoRoot
try {
    if (-not $SkipWebBuild) {
        Push-Location (Join-Path $repoRoot 'apps/web')
        try { & npm run build; if ($LASTEXITCODE -ne 0) { throw 'Web build failed.' } }
        finally { Pop-Location }
    }
    if (-not (Test-Path -LiteralPath 'apps/web/dist/modern/index.html') -or -not (Test-Path -LiteralPath 'apps/web/dist/classic/index.classic.html')) { throw 'Both Modern and Classic Web builds are required.' }
    $publications = @(
        @{ Project='apps/server/Hispania.Server.csproj'; Path='server' },
        @{ Project='apps/worker-agent/Hispania.WorkerAgent.csproj'; Path='worker' },
        @{ Project='apps/module-host/Hispania.ModuleHost.csproj'; Path='server/module-host' },
        @{ Project='apps/module-host/Hispania.ModuleHost.csproj'; Path='worker/module-host' },
        @{ Project='apps/ffmpeg-worker/Hispania.FFmpeg.Worker.csproj'; Path='server/ffmpeg-worker' },
        @{ Project='apps/deployment/Hispania.Deployment.csproj'; Path='deployment' }
    )
    foreach ($publication in $publications) {
        & dotnet publish $publication.Project -c Release -r $Rid --self-contained false -p:UseAppHost=true -p:ProductionPublication=true ('-p:PathMap='+$repoRoot+'=/_/hispania') --nologo -v quiet -o (Join-Path $outputRoot $publication.Path)
        if ($LASTEXITCODE -ne 0) { throw "Publication failed: $($publication.Project)" }
    }
    Copy-Item -LiteralPath (Join-Path $outputRoot 'server/wwwroot/classic/index.classic.html') -Destination (Join-Path $outputRoot 'server/wwwroot/classic/index.html')
    # Server's development content is deliberately excluded from production delivery.
    foreach ($developmentContent in @('server/extensions','server/demo-assets','server/appsettings.Development.json')) {
        $candidate = [IO.Path]::GetFullPath((Join-Path $outputRoot $developmentContent))
        if (-not $candidate.StartsWith($outputRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Content path escaped the publication.' }
        if (Test-Path -LiteralPath $candidate) { Remove-Item -LiteralPath $candidate -Recurse -Force }
    }
    New-Item -ItemType Directory -Path (Join-Path $outputRoot 'operations') | Out-Null
    Copy-Item -LiteralPath 'deployment/windows-service.ps1','deployment/hispania.service.in','deployment/install-systemd.sh' -Destination (Join-Path $outputRoot 'operations')
    $sourceSha = (& git rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0) { throw 'Source SHA unavailable.' }
    $sourceDirty = @(& git status --porcelain --untracked-files=normal).Count -ne 0
    $files = [ordered]@{}
    foreach ($file in (Get-ChildItem -LiteralPath $outputRoot -File -Recurse -Force | Sort-Object FullName)) {
        $relative = [IO.Path]::GetRelativePath($outputRoot,$file.FullName).Replace('\','/')
        $files[$relative] = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    }
    $manifest = [ordered]@{
        manifestGeneration=1; distributionVersion=(Get-Content -Raw version.json | ConvertFrom-Json).distributionVersion
        gitSha=$sourceSha; sourceDirty=$sourceDirty; targetRid=$Rid; buildConfiguration='Release'; dotnetSdkVersion=(& dotnet --version).Trim()
        runtimePrerequisite='ASP.NET Core Runtime 10.0 (x64)'; buildUtc=[DateTimeOffset]::UtcNow.ToString('O'); files=$files
    }
    $manifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $outputRoot 'artifact-manifest.json') -Encoding utf8NoBOM
    # The dirty flag is evidence alongside the artifact, never silently claimed as exact-commit output.
    [ordered]@{ sourceDirty=$sourceDirty; sourceSha=$sourceSha; note='A dirty artifact is local verification evidence, not a reproducible release.' } | ConvertTo-Json | Set-Content -LiteralPath ($outputRoot + '.build-evidence.json') -Encoding utf8NoBOM
    (Get-FileHash -LiteralPath (Join-Path $outputRoot 'artifact-manifest.json') -Algorithm SHA256).Hash.ToLowerInvariant() | Set-Content -LiteralPath ($outputRoot + '.manifest.sha256') -Encoding ascii
    & dotnet (Join-Path $outputRoot 'deployment/Hispania.Deployment.dll') verify $outputRoot
    if ($LASTEXITCODE -ne 0) { throw 'Published artifact verification failed.' }
    Write-Host "Published $Rid artifact: $outputRoot"
}
finally { Pop-Location }
