param([string]$ResultsDirectory = '.artifacts/phase15-gate')
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$results = if ([IO.Path]::IsPathRooted($ResultsDirectory)) { [IO.Path]::GetFullPath($ResultsDirectory) } else { [IO.Path]::GetFullPath((Join-Path $root $ResultsDirectory)) }
New-Item -ItemType Directory -Force $results | Out-Null
$suites = @(
    @{ Project = 'tests/compatibility/Hispania.Compatibility.Tests.csproj'; Count = 13; Log = 'corpus.trx' },
    @{ Project = 'tests/playback/Hispania.Playback.Tests.csproj'; Count = 3; Log = 'http.trx' }
)
foreach ($suite in $suites) {
    & dotnet test (Join-Path $root $suite.Project) --filter 'Category=Phase15Compatibility' --results-directory $results --logger "trx;LogFileName=$($suite.Log)"
    if ($LASTEXITCODE -ne 0) { throw "Phase 15 failed: $($suite.Project)" }
    $trx = [xml](Get-Content -LiteralPath (Join-Path $results $suite.Log) -Raw)
    $tests = @($trx.TestRun.Results.UnitTestResult)
    if ($tests.Count -ne $suite.Count -or @($tests | Where-Object { $_.outcome -ne 'Passed' }).Count -ne 0) {
        throw "All $($suite.Count) Phase 15 tests must be discovered and pass without skips."
    }
}
Write-Output 'Phase15Compatibility: 16/16 passed; immutable historical corpus, source/binary consumers, API, serialization, HTTP, fencing, signed package and protocols.'
