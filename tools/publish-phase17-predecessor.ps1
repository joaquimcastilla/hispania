[CmdletBinding()]
param([Parameter(Mandatory)][string]$OutputDirectory,[ValidateSet('win-x64','linux-x64')][string]$Rid)
$ErrorActionPreference='Stop'
$repoRoot=Split-Path $PSScriptRoot -Parent
$outputRoot=[IO.Path]::GetFullPath($OutputDirectory)
$sourceRoot=$outputRoot+'.source'
$checkpoint='c3a827a4b828cff37785884e889689af7cc5fce2'
if (-not $Rid) { $Rid=if ($IsWindows) { 'win-x64' } else { 'linux-x64' } }
foreach ($path in @($outputRoot,$sourceRoot,$outputRoot+'.tar')) { if (Test-Path -LiteralPath $path) { throw 'Predecessor output/archive must be fresh; no checkout is overwritten.' } }
Push-Location $repoRoot
try {
    & git cat-file -e ($checkpoint+'^{commit}'); if ($LASTEXITCODE -ne 0) { throw 'Exact Phase 17 checkpoint missing.' }
    New-Item -ItemType Directory -Path $sourceRoot | Out-Null
    & git archive --format=tar ('--output='+$outputRoot+'.tar') $checkpoint
    if ($LASTEXITCODE -ne 0) { throw 'Checkpoint archive failed.' }
    & tar -xf ($outputRoot+'.tar') -C $sourceRoot
    if ($LASTEXITCODE -ne 0) { throw 'Checkpoint extraction failed.' }
    Push-Location (Join-Path $sourceRoot 'apps/web')
    try {
        & npm ci; if ($LASTEXITCODE -ne 0) { throw 'Predecessor Web dependencies failed.' }
        & npm run build; if ($LASTEXITCODE -ne 0) { throw 'Predecessor Web build failed.' }
    } finally { Pop-Location }
    foreach ($entry in @(
        @('apps/server/Hispania.Server.csproj','server'),
        @('apps/worker-agent/Hispania.WorkerAgent.csproj','worker'),
        @('apps/module-host/Hispania.ModuleHost.csproj','server/module-host'),
        @('apps/module-host/Hispania.ModuleHost.csproj','worker/module-host'),
        @('apps/ffmpeg-worker/Hispania.FFmpeg.Worker.csproj','server/ffmpeg-worker')
    )) {
        & dotnet publish (Join-Path $sourceRoot $entry[0]) -c Release -r $Rid --self-contained false -p:UseAppHost=true ('-p:PathMap='+$sourceRoot+'=/_/phase17') --nologo -v quiet -o (Join-Path $outputRoot $entry[1])
        if ($LASTEXITCODE -ne 0) { throw 'Archived predecessor publication failed.' }
    }
    Copy-Item -LiteralPath (Join-Path $outputRoot 'server/wwwroot/classic/index.classic.html') -Destination (Join-Path $outputRoot 'server/wwwroot/classic/index.html')
    $files=[ordered]@{}
    foreach ($file in (Get-ChildItem -LiteralPath $outputRoot -File -Recurse | Sort-Object FullName)) {
        $files[[IO.Path]::GetRelativePath($outputRoot,$file.FullName).Replace('\','/')]=(Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    }
    [ordered]@{ manifestGeneration=1;distributionVersion='0.7.0';gitSha=$checkpoint;targetRid=$Rid;buildConfiguration='Release';dotnetSdkVersion=(& dotnet --version).Trim();runtimePrerequisite='ASP.NET Core Runtime 10.0 (x64)';buildUtc=[DateTimeOffset]::UtcNow.ToString('O');files=$files } |
        ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $outputRoot 'artifact-manifest.json') -Encoding utf8NoBOM
    Write-Host "Archived exact Phase 17 artifact prepared: $outputRoot. This is a compatibility predecessor, not a new supported release."
} finally { Pop-Location }
