param([string]$ResultsDirectory = '.artifacts/phase17-gate', [switch]$RequirePostgreSql)
$ErrorActionPreference = 'Stop'
Push-Location (Split-Path -Parent $PSScriptRoot)
try {
    dotnet test tests/inventory/Hispania.Inventory.Tests.csproj --filter 'Category=Phase17BackupRestore' --results-directory $ResultsDirectory --logger 'trx;LogFileName=backup-restore.trx'
    if ($LASTEXITCODE -ne 0) { throw 'Phase 17 backup/restore failed.' }
    $trx = [xml](Get-Content -LiteralPath (Join-Path $ResultsDirectory 'backup-restore.trx') -Raw)
    $cases = @($trx.TestRun.Results.UnitTestResult)
    if ($cases.Count -ne 46 -or @($cases | Where-Object { $_.outcome -ne 'Passed' }).Count -ne 0) { throw 'All 46 portable Phase 17 cases must execute and pass without skips.' }
    if ($RequirePostgreSql) {
        if ([string]::IsNullOrWhiteSpace($env:HISPANIA_POSTGRES_TEST_CONNECTION)) { throw 'Mixed-provider disaster configuration is mandatory.' }
        foreach ($tool in @('pg_dump', 'pg_restore', 'psql')) { Get-Command $tool -ErrorAction Stop | Out-Null }
        & pg_dump --version
        & pg_restore --version
        dotnet test tests/inventory/Hispania.Inventory.Tests.csproj --no-build --filter 'Category=Phase17PostgreSql' --results-directory $ResultsDirectory --logger 'trx;LogFileName=mixed-provider.trx'
        if ($LASTEXITCODE -ne 0) { throw 'Mixed-provider disaster restore failed.' }
        $pg = [xml](Get-Content -LiteralPath (Join-Path $ResultsDirectory 'mixed-provider.trx') -Raw)
        $pgCases = @($pg.TestRun.Results.UnitTestResult)
        if ($pgCases.Count -ne 1 -or $pgCases[0].outcome -ne 'Passed') { throw 'Mixed-provider disaster must execute and pass; no skips.' }
    }
} finally { Pop-Location }
