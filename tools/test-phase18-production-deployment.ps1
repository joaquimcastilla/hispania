[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ArtifactDirectory,
    [Parameter(Mandatory)][string]$PredecessorDirectory,
    [string]$ResultsDirectory='.artifacts/phase18-gate',
    [switch]$RequirePostgreSql,
    [switch]$RequireNativeService
)
$ErrorActionPreference='Stop'
Push-Location (Split-Path $PSScriptRoot -Parent)
try {
    $env:HISPANIA_PHASE18_ARTIFACT=[IO.Path]::GetFullPath($ArtifactDirectory)
    $env:HISPANIA_PHASE18_PREDECESSOR=[IO.Path]::GetFullPath($PredecessorDirectory)
    $results=[IO.Path]::GetFullPath($ResultsDirectory)
    function Test-Suite([string]$Project,[string]$Filter,[string]$Name,[int]$Expected) {
        & dotnet test $Project --filter $Filter --results-directory $results --logger "trx;LogFileName=$Name.trx" --nologo -v quiet
        if ($LASTEXITCODE -ne 0) { throw "Phase 18 $Name failed." }
        $trx=[xml](Get-Content -LiteralPath (Join-Path $results "$Name.trx") -Raw)
        $cases=@($trx.TestRun.Results.UnitTestResult)
        if ($cases.Count -ne $Expected -or @($cases | Where-Object outcome -ne Passed).Count -ne 0) { throw "$Name must discover and pass all $Expected cases without skips." }
    }
    Test-Suite 'tests/deployment/Hispania.Deployment.Tests.csproj' 'Category=Phase18ProductionDeployment' 'configuration-secrets' 28
    Test-Suite 'tests/deployment-process/Hispania.Deployment.ProcessTests.csproj' 'Category=Phase18ProductionDeployment' 'published-processes' 10
    if ($RequirePostgreSql) {
        if (-not $env:HISPANIA_PHASE18_POSTGRES_CONNECTION) { throw 'A password-auth PostgreSQL fixture is mandatory; trust auth cannot prove credential rotation.' }
        foreach ($tool in @('pg_dump','pg_restore')) { Get-Command $tool -ErrorAction Stop | Out-Null }
        Test-Suite 'tests/deployment-process/Hispania.Deployment.ProcessTests.csproj' 'Category=Phase18PostgreSql' 'postgres-rotation-disaster' 1
    }
    if ($RequireNativeService) {
        $env:HISPANIA_PHASE18_REQUIRE_SERVICE='1'
        Test-Suite 'tests/deployment-process/Hispania.Deployment.ProcessTests.csproj' 'Category=Phase18NativeService' 'native-services' 2
    }
    [ordered]@{ configurationCases=28;publishedProcessCases=10;nativeServices=if ($RequireNativeService) {'actually_installed_started_restarted_stopped_uninstalled'} else {'not_executed_requires_privileged_runner'};postgreSql=if ($RequirePostgreSql) {'password_rotation_and_disaster_live'} else {'not_executed_requires_password_auth_server'} } |
        ConvertTo-Json | Set-Content -LiteralPath (Join-Path $results 'evidence.json') -Encoding utf8NoBOM
} finally { Pop-Location }
