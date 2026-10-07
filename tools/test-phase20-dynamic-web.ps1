param([string]$ResultsDirectory='.artifacts/phase20-gate')
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$results = [IO.Path]::GetFullPath((Join-Path $repo $ResultsDirectory))
New-Item -ItemType Directory -Path $results -Force | Out-Null
$sha = 'da57b39149b5d297f3970c17899a4da9eb366eb7'
git -C $repo cat-file -e "$sha`^{commit}"
if ($LASTEXITCODE -ne 0) { throw 'Exact validated Phase19 predecessor is required.' }
$archive = Join-Path $results 'phase19-source.zip'
$historical = Join-Path $results 'historical-source'
git -C $repo archive --format=zip "--output=$archive" $sha core/runtime contracts/module-system contracts/platform Directory.Build.props
if ($LASTEXITCODE -ne 0) { throw 'Cannot archive predecessor.' }
Expand-Archive -LiteralPath $archive -DestinationPath $historical -Force
dotnet build (Join-Path $historical 'core/runtime/Hispania.Runtime.csproj') --configuration Release --verbosity quiet
if ($LASTEXITCODE -ne 0) { throw 'Predecessor build failed.' }
$oldRuntime = Join-Path $historical 'core/runtime/bin/Release/net10.0'
$reader = Join-Path $results 'historical-reader'
dotnet build (Join-Path $repo 'tests/historical-web-reader/Hispania.HistoricalWeb.Reader.csproj') "-p:HistoricalRuntimeRoot=$oldRuntime" --output $reader --verbosity quiet
if ($LASTEXITCODE -ne 0) { throw 'Historical reader build failed.' }
dotnet build (Join-Path $repo 'tests/web-contribution-fixtures/Hispania.Web.Fixtures.csproj') --verbosity quiet
if ($LASTEXITCODE -ne 0) { throw 'Fixture build failed.' }
$fixtures = Join-Path $results 'fixtures'
dotnet (Join-Path $repo 'tests/web-contribution-fixtures/bin/Debug/net10.0/Hispania.Web.Fixtures.dll') $fixtures
if ($LASTEXITCODE -ne 0) { throw 'Fixture generation failed.' }
dotnet run --project (Join-Path $repo 'tools/package-preflight/Hispania.PackagePreflight.csproj') --configuration Release -- (Join-Path $fixtures '1.0.0.hispania')
if ($LASTEXITCODE -ne 0) { throw 'Offline package author tooling failed.' }
dotnet (Join-Path $reader 'Hispania.HistoricalWeb.Reader.dll') (Join-Path $fixtures '1.0.0.hispania') (Join-Path $results ('old-installed-' + [Guid]::NewGuid().ToString('N'))) (Join-Path $fixtures 'keys.json')
if ($LASTEXITCODE -ne 0) { throw 'Required Web predecessor safety failed.' }
dotnet test (Join-Path $repo 'tests/unit/Hispania.Runtime.Tests.csproj') --filter FullyQualifiedName~WebContribution --results-directory $results --logger 'trx;LogFileName=phase20-web.trx'
if ($LASTEXITCODE -ne 0) { throw 'Web contribution runtime tests failed.' }
$trx = [xml](Get-Content -LiteralPath (Join-Path $results 'phase20-web.trx') -Raw)
$tests = @($trx.TestRun.Results.UnitTestResult)
if ($tests.Count -lt 35 -or @($tests | Where-Object outcome -ne 'Passed').Count -ne 0) { throw 'Phase20 runtime gate forbids missing/skipped tests.' }
dotnet test (Join-Path $repo 'tests/inventory/Hispania.Inventory.Tests.csproj') --filter FullyQualifiedName~Phase20WebBackup --results-directory $results --logger 'trx;LogFileName=phase20-backup.trx'
if ($LASTEXITCODE -ne 0) { throw 'Phase20 actual backup/restore and future-schema isolation failed.' }
$backup = [xml](Get-Content -LiteralPath (Join-Path $results 'phase20-backup.trx') -Raw)
$backupTests = @($backup.TestRun.Results.UnitTestResult)
if ($backupTests.Count -ne 2 -or @($backupTests | Where-Object outcome -ne 'Passed').Count -ne 0) { throw 'Both Phase20 backup tests must execute without skips.' }
dotnet build (Join-Path $repo 'apps/server/Hispania.Server.csproj') --verbosity quiet
if ($LASTEXITCODE -ne 0) { throw 'Real server build failed.' }
Push-Location (Join-Path $repo 'apps/web')
try {
    npm run build
    if ($LASTEXITCODE -ne 0) { throw 'Modern/Classic build failed.' }
    npx playwright test
    if ($LASTEXITCODE -ne 0) { throw 'Modern/Classic real browser gate failed.' }
    $browser = Get-Content -LiteralPath (Join-Path $repo '.artifacts/phase20-browser-results.json') -Raw | ConvertFrom-Json
    if ($browser.stats.expected -ne 2 -or $browser.stats.unexpected -ne 0 -or $browser.stats.skipped -ne 0 -or $browser.stats.flaky -ne 0) { throw 'Both actual browser bundles must pass without retries or skips.' }
} finally { Pop-Location }
