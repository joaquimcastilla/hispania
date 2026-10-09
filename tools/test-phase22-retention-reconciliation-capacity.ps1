param([string]$ResultsDirectory = '.artifacts/phase22')
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$results = [IO.Path]::GetFullPath((Join-Path $repo $ResultsDirectory))
New-Item -ItemType Directory -Path $results -Force | Out-Null
Push-Location $repo
try {
    $reportPath = Join-Path $results 'phase22.trx'
    dotnet test tests/inventory/Hispania.Inventory.Tests.csproj --filter 'FullyQualifiedName~Phase22MaintenanceTests|FullyQualifiedName~Phase22BackupRetentionTests' --results-directory $results --logger 'trx;LogFileName=phase22.trx' --verbosity quiet 2>&1 | Tee-Object -FilePath (Join-Path $results 'phase22.log')
    if ($LASTEXITCODE -ne 0) { throw 'Phase22 maintenance tests failed.' }
    if (-not (Test-Path -LiteralPath $reportPath)) { throw 'Phase22 test report is missing.' }
    [xml]$report = Get-Content -LiteralPath $reportPath
    $tests = @($report.TestRun.Results.UnitTestResult)
    if ($tests.Count -lt 21 -or @($tests | Where-Object outcome -ne 'Passed').Count -ne 0) {
        throw 'Phase22 gate rejects missing, failed or skipped tests.'
    }
    $additional = @(
        @{ Project='tests/unit/Hispania.Runtime.Tests.csproj'; Filter='FullyQualifiedName~RetentionPlanPrunesV1ButPreservesV2KnownGoodForRollback'; Name='package-known-good' },
        @{ Project='tests/compatibility/Hispania.Compatibility.Tests.csproj'; Filter='FullyQualifiedName~Phase22MaintenanceNeverReducesCoordinatorGenerationTenHighWater'; Name='coordinator-high-water' },
        @{ Project='tests/inventory/Hispania.Inventory.Tests.csproj'; Filter='FullyQualifiedName~Phase17RealHostDestroyRestoreRetainsLoginBootstrapAssetsInventoryAndScheduledJob'; Name='backup-restore-regression' }
    )
    foreach ($suite in $additional) {
        dotnet test $suite.Project --filter $suite.Filter --results-directory $results --logger "trx;LogFileName=$($suite.Name).trx" --verbosity quiet 2>&1 | Tee-Object -FilePath (Join-Path $results "$($suite.Name).log")
        if ($LASTEXITCODE -ne 0) { throw "Phase22 related suite $($suite.Name) failed." }
        [xml]$extraReport = Get-Content -LiteralPath (Join-Path $results "$($suite.Name).trx")
        $extraTests = @($extraReport.TestRun.Results.UnitTestResult)
        if ($extraTests.Count -ne 1 -or $extraTests[0].outcome -ne 'Passed') { throw "Phase22 related suite $($suite.Name) has a missing or skipped test." }
    }
    dotnet build Hispania.Platform.slnf --no-restore --verbosity quiet 2>&1 | Tee-Object -FilePath (Join-Path $results 'platform-build.log')
    if ($LASTEXITCODE -ne 0) { throw 'Platform solution filter failed.' }
    dotnet build apps/server/Hispania.Server.csproj --no-restore --verbosity quiet 2>&1 | Tee-Object -FilePath (Join-Path $results 'server-build.log')
    if ($LASTEXITCODE -ne 0) { throw 'Server build failed.' }
    Push-Location apps/web
    try {
        npm test -- --run src/AdminShell.test.tsx 2>&1 | Tee-Object -FilePath (Join-Path $results 'web-admin.log')
        if ($LASTEXITCODE -ne 0) { throw 'Web admin tests failed.' }
        npm run build 2>&1 | Tee-Object -FilePath (Join-Path $results 'web-build.log')
        if ($LASTEXITCODE -ne 0) { throw 'Modern/Classic build failed.' }
    } finally { Pop-Location }
    git rev-parse HEAD > (Join-Path $results 'git-sha.txt')
    Write-Output "Phase22 dedicated gate passed: $($tests.Count) owner tests plus package, coordinator and backup/restore regressions; full acceptance still requires the other local gates."
} finally { Pop-Location }
