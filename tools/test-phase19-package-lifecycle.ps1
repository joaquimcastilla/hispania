param([string]$ResultsDirectory='.artifacts/phase19-gate')
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$results = [IO.Path]::GetFullPath((Join-Path $repo $ResultsDirectory))
New-Item -ItemType Directory -Path $results -Force | Out-Null
$historical = Join-Path $results 'historical-source'
$archive = Join-Path $results 'phase18-source.zip'
$sha = '86b43bc414edd53d8f7312a0d4045dc2818a39f6'
git -C $repo cat-file -e "$sha`^{commit}"
if ($LASTEXITCODE -ne 0) { throw 'The exact validated Phase 18 commit must be available locally.' }
git -C $repo archive --format=zip "--output=$archive" $sha core/runtime contracts/module-system contracts/platform Directory.Build.props
if ($LASTEXITCODE -ne 0) { throw 'Cannot archive the historical reader source.' }
Expand-Archive -LiteralPath $archive -DestinationPath $historical -Force
dotnet build (Join-Path $historical 'core/runtime/Hispania.Runtime.csproj') --configuration Release --verbosity quiet
if ($LASTEXITCODE -ne 0) { throw 'Historical runtime compilation failed.' }
$historicalRuntime = Join-Path $historical 'core/runtime/bin/Release/net10.0'
$reader = Join-Path $results 'historical-reader'
dotnet build (Join-Path $repo 'tests/historical-package-reader/Hispania.HistoricalPackage.Reader.csproj') "-p:HistoricalRuntimeRoot=$historicalRuntime" --output $reader --verbosity quiet
if ($LASTEXITCODE -ne 0) { throw 'Historical reader compilation failed.' }
$previousReader = $env:HISPANIA_PHASE19_OLD_READER
try {
    $env:HISPANIA_PHASE19_OLD_READER = Join-Path $reader 'Hispania.HistoricalPackage.Reader.dll'
    dotnet test (Join-Path $repo 'tests/unit/Hispania.Runtime.Tests.csproj') --filter FullyQualifiedName~PackageLifecycle --results-directory $results --logger 'trx;LogFileName=phase19-package-lifecycle.trx'
    if ($LASTEXITCODE -ne 0) { throw 'Phase 19 package lifecycle tests failed.' }
    $trx = [xml](Get-Content -LiteralPath (Join-Path $results 'phase19-package-lifecycle.trx') -Raw)
    $tests = @($trx.TestRun.Results.UnitTestResult)
    if ($tests.Count -lt 71 -or @($tests | Where-Object outcome -ne 'Passed').Count -ne 0) { throw 'Phase 19 requires all resolver, lifecycle, historical reader and real process tests without skips.' }
    dotnet test (Join-Path $repo 'tests/inventory/Hispania.Inventory.Tests.csproj') --filter FullyQualifiedName~Phase19PackageBackupTests --results-directory $results --logger 'trx;LogFileName=phase19-package-backup.trx'
    if ($LASTEXITCODE -ne 0) { throw 'Phase 19 real installation backup/restore failed.' }
    $backup = [xml](Get-Content -LiteralPath (Join-Path $results 'phase19-package-backup.trx') -Raw)
    $backupTests = @($backup.TestRun.Results.UnitTestResult)
    if ($backupTests.Count -ne 2 -or @($backupTests | Where-Object outcome -ne 'Passed').Count -ne 0) { throw 'Both Phase 19 backup/restore cases must pass without skips.' }
    Write-Output "Phase19PackageLifecycle: $($tests.Count)/$($tests.Count) passed; historical reader SHA $sha; no network package resolution."
} finally { $env:HISPANIA_PHASE19_OLD_READER = $previousReader }
