[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ArtifactDirectory,
    [Parameter(Mandatory)][string]$PredecessorDirectory,
    [string]$ResultsDirectory='.artifacts/phase18-gate',
    [string]$WebPredecessorDirectory,
    [string]$PredecessorSha,
    [switch]$RequirePostgreSql,
    [switch]$RequireNativeService
)
$ErrorActionPreference='Stop'
Push-Location (Split-Path $PSScriptRoot -Parent)
try {
    $env:HISPANIA_PHASE18_ARTIFACT=[IO.Path]::GetFullPath($ArtifactDirectory)
    $env:HISPANIA_PHASE18_PREDECESSOR=[IO.Path]::GetFullPath($PredecessorDirectory)
    $expectedPredecessorSha = if ($PredecessorSha) { $PredecessorSha } else { 'c3a827a4b828cff37785884e889689af7cc5fce2' }
    if ($expectedPredecessorSha -notmatch '^[0-9a-fA-F]{40}$') { throw 'Predecessor SHA must be a full Git commit hash.' }
    $predecessorManifest = Get-Content -LiteralPath (Join-Path $env:HISPANIA_PHASE18_PREDECESSOR 'artifact-manifest.json') -Raw | ConvertFrom-Json
    if ($predecessorManifest.gitSha -cne $expectedPredecessorSha) { throw 'Predecessor artifact does not match the expected SHA.' }
    $env:HISPANIA_PHASE18_PREDECESSOR_SHA = $expectedPredecessorSha
    $results=[IO.Path]::GetFullPath($ResultsDirectory)
    if ($env:GITHUB_ACTIONS -eq 'true') {
        $manifest = Get-Content -LiteralPath (Join-Path $env:HISPANIA_PHASE18_ARTIFACT 'artifact-manifest.json') -Raw | ConvertFrom-Json
        if ($manifest.sourceDirty -isnot [bool] -or $manifest.sourceDirty -ne $false) { throw 'CI artifact must record sourceDirty=false; investigate checkout and generated source changes.' }
        if (-not $env:GITHUB_SHA -or $manifest.gitSha -cne $env:GITHUB_SHA) { throw 'CI artifact GitSha must match the exact published workflow SHA.' }
    }
    function Test-Suite([string]$Project,[string]$Filter,[string]$Name,[int]$Expected) {
        & dotnet test $Project --filter $Filter --results-directory $results --logger "trx;LogFileName=$Name.trx" --nologo -v quiet
        if ($LASTEXITCODE -ne 0) { throw "Phase 18 $Name failed." }
        $trx=[xml](Get-Content -LiteralPath (Join-Path $results "$Name.trx") -Raw)
        $cases=@($trx.TestRun.Results.UnitTestResult)
        if ($cases.Count -ne $Expected -or @($cases | Where-Object outcome -ne Passed).Count -ne 0) { throw "$Name must discover and pass all $Expected cases without skips." }
    }
    Test-Suite 'tests/deployment/Hispania.Deployment.Tests.csproj' 'Category=Phase18ProductionDeployment' 'configuration-secrets' 30
    Test-Suite 'tests/deployment-process/Hispania.Deployment.ProcessTests.csproj' 'Category=Phase18ProductionDeployment' 'published-processes' 10
    Test-Suite 'tests/deployment-process/Hispania.Deployment.ProcessTests.csproj' 'Category=Phase19PackageDistribution' 'package-distribution-upgrade' 2
    if ($WebPredecessorDirectory) {
        $env:HISPANIA_PHASE20_PREDECESSOR=[IO.Path]::GetFullPath($WebPredecessorDirectory)
        Test-Suite 'tests/deployment-process/Hispania.Deployment.ProcessTests.csproj' 'Category=Phase20WebDistribution' 'web-distribution-upgrade' 2
    }
    if ($RequirePostgreSql) {
        if (-not $env:HISPANIA_PHASE18_POSTGRES_CONNECTION) { throw 'A password-auth PostgreSQL fixture is mandatory; trust auth cannot prove credential rotation.' }
        foreach ($tool in @('pg_dump','pg_restore')) { Get-Command $tool -ErrorAction Stop | Out-Null }
        Test-Suite 'tests/deployment-process/Hispania.Deployment.ProcessTests.csproj' 'Category=Phase18PostgreSql' 'postgres-rotation-disaster' 1
    }
    if ($RequireNativeService) {
        $env:HISPANIA_PHASE18_REQUIRE_SERVICE='1'
        Test-Suite 'tests/deployment-process/Hispania.Deployment.ProcessTests.csproj' 'Category=Phase18NativeService' 'native-services' 2
    }
    [ordered]@{ configurationCases=30;publishedProcessCases=10;packageDistributionCases=2;nativeServices=if ($RequireNativeService) {'actually_installed_started_restarted_stopped_uninstalled'} else {'not_executed_requires_privileged_runner'};postgreSql=if ($RequirePostgreSql) {'password_rotation_and_disaster_live'} else {'not_executed_requires_password_auth_server'} } |
        ConvertTo-Json | Set-Content -LiteralPath (Join-Path $results 'evidence.json') -Encoding utf8NoBOM
} finally { Pop-Location }
