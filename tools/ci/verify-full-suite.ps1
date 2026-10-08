$ErrorActionPreference='Stop'
$reports=@(Get-ChildItem -LiteralPath .artifacts/full-suite -Filter '*.trx' -Recurse)
if ($reports.Count -eq 0) { throw 'Full-suite evidence is mandatory.' }
$tests=@(foreach ($report in $reports) { [xml]$trx=Get-Content -LiteralPath $report.FullName -Raw; $trx.TestRun.Results.UnitTestResult })
if ($tests.Count -eq 0 -or @($tests | Where-Object outcome -notin 'Passed','NotExecuted').Count -ne 0) { throw 'Full suite must contain only passed tests and the explicit historical Windows PostgreSQL omissions.' }
$skipped=@($tests | Where-Object outcome -eq 'NotExecuted')
$allowed=@(
    'Hispania.Library.Persistence.Sqlite.Tests.PostgreSqlProviderTests.PostgreSqlMigrationAndConnectionFailureRecoveryUseAnIsolatedSchema',
    'Hispania.Inventory.Tests.Phase17PostgreSqlTests.MixedSqlitePostgreSqlFilesystemDisasterRestoresWithReprovisionedCredentialsAndCurrentReader'
)
if ($IsLinux -and $skipped.Count -ne 0) { throw 'Linux acceptance forbids skips.' }
if ($IsWindows -and ($skipped.Count -ne 2 -or @($skipped | Where-Object testName -notin $allowed).Count -ne 0)) { throw 'Windows permits only its two exact historical PostgreSQL omissions.' }
[ordered]@{ reports=$reports.Count; total=$tests.Count; passed=@($tests|Where-Object outcome -eq 'Passed').Count; failed=0; skipped=$skipped.Count; skipNames=@($skipped.testName); sha=$env:GITHUB_SHA } |
    ConvertTo-Json -Depth 4 | Tee-Object -FilePath .artifacts/full-suite/summary.json
