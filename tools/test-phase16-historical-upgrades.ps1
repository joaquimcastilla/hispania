param([string]$ResultsDirectory = '.artifacts/phase16-gate', [switch]$RequirePostgreSql)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$results = if ([IO.Path]::IsPathRooted($ResultsDirectory)) { [IO.Path]::GetFullPath($ResultsDirectory) } else { [IO.Path]::GetFullPath((Join-Path $root $ResultsDirectory)) }
New-Item -ItemType Directory -Force $results | Out-Null
Push-Location $root
try {
    $first = python tools/data-compatibility-manifest.py
    if ($LASTEXITCODE -ne 0) { throw 'Historical data manifest failed.' }
    $second = python tools/data-compatibility-manifest.py
    if ($LASTEXITCODE -ne 0 -or ($first -join "`n") -cne ($second -join "`n")) { throw 'Historical data analysis must be deterministic.' }
    $first
    python tools/data-compatibility-manifest.py --self-test
    if ($LASTEXITCODE -ne 0) { throw 'Data corpus negative cases failed.' }
    dotnet test tests/compatibility/Hispania.Compatibility.Tests.csproj --filter 'Category=Phase16HistoricalDataUpgrade' --results-directory $results --logger 'trx;LogFileName=historical-data.trx'
    if ($LASTEXITCODE -ne 0) { throw 'Historical data upgrade suite failed.' }
    $trx = [xml](Get-Content -LiteralPath (Join-Path $results 'historical-data.trx') -Raw)
    $tests = @($trx.TestRun.Results.UnitTestResult)
    if ($tests.Count -ne 37 -or @($tests | Where-Object { $_.outcome -ne 'Passed' }).Count -ne 0) { throw 'All 37 portable Phase 16 cases must be discovered and pass without skips.' }
    if ($RequirePostgreSql) {
        if ([string]::IsNullOrWhiteSpace($env:HISPANIA_POSTGRES_TEST_CONNECTION)) { throw 'Historical PostgreSQL-live configuration is mandatory on Linux.' }
        dotnet test tests/library-persistence/Hispania.Library.Persistence.Sqlite.Tests.csproj --filter 'Category=Phase16PostgreSqlHistoricalUpgrade' --results-directory $results --logger 'trx;LogFileName=historical-postgresql.trx'
        if ($LASTEXITCODE -ne 0) { throw 'Historical PostgreSQL upgrade failed.' }
        $pg = [xml](Get-Content -LiteralPath (Join-Path $results 'historical-postgresql.trx') -Raw)
        $pgTests = @($pg.TestRun.Results.UnitTestResult)
        if ($pgTests.Count -ne 1 -or $pgTests[0].outcome -ne 'Passed') { throw 'Historical PostgreSQL-live must execute and pass; a skip is forbidden.' }
    }
    Write-Output 'Phase16HistoricalDataUpgrade: 37/37 portable cases; 15 immutable data artifacts, real owner upgrades, data invariants and schema safety.'
} finally { Pop-Location }
