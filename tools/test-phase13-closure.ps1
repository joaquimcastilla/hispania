param([string]$ResultsDirectory = (Join-Path ([System.IO.Path]::GetTempPath()) ("hispania-phase13-closure-" + [guid]::NewGuid().ToString('N'))))
$ErrorActionPreference = 'Stop'
Push-Location (Split-Path $PSScriptRoot -Parent)
try {
    dotnet test tests/inventory/Hispania.Inventory.Tests.csproj --no-build --filter 'FullyQualifiedName~RealServerRestartsKeepInventoryAssetsAuditAndPlayableVideo|FullyQualifiedName~RealCrashRecoversRunningJobAndProcessingEventTogether|FullyQualifiedName~EveryPlatformProjectReferenceClosure|FullyQualifiedName~ConcurrentPublishAndDispatchKeepOneAcquisition' --logger 'trx;LogFileName=required-phase13-closure.trx' --results-directory $ResultsDirectory
    if ($LASTEXITCODE -ne 0) { throw 'Phase 13 process/architecture closure failed.' }
    $trx = [xml](Get-Content -LiteralPath (Join-Path $ResultsDirectory 'required-phase13-closure.trx') -Raw)
    $results = @($trx.TestRun.Results.UnitTestResult)
    if ($results.Count -ne 4 -or @($results | Where-Object { $_.outcome -ne 'Passed' }).Count -ne 0) { throw 'All four required closure tests must be discovered and pass without skips.' }
    dotnet test tests/playback/Hispania.Playback.Tests.csproj --no-build --filter 'FullyQualifiedName~ProviderFailuresNeverExposeExceptionMaterialInResponsesOrLogs' --logger 'trx;LogFileName=required-phase13-errors.trx' --results-directory $ResultsDirectory
    if ($LASTEXITCODE -ne 0) { throw 'Phase 13 error sanitization failed.' }
    $trx = [xml](Get-Content -LiteralPath (Join-Path $ResultsDirectory 'required-phase13-errors.trx') -Raw)
    $results = @($trx.TestRun.Results.UnitTestResult)
    if ($results.Count -ne 6 -or @($results | Where-Object { $_.outcome -ne 'Passed' }).Count -ne 0) { throw 'All six error boundary cases must be discovered and pass without skips.' }
    & "$PSScriptRoot/test-phase13-concurrency.ps1" -Repetitions 20 -ResultsDirectory (Join-Path $ResultsDirectory 'stress')
} finally { Pop-Location }
