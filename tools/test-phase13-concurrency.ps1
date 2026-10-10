param(
    [ValidateRange(1, 100)][int]$Repetitions = 20,
    [string]$ResultsDirectory = (Join-Path ([System.IO.Path]::GetTempPath()) ("hispania-phase13-stress-" + [guid]::NewGuid().ToString('N')))
)
$ErrorActionPreference = 'Stop'
$repository = Split-Path $PSScriptRoot -Parent
$groups = @(
    @{ Name = 'platform'; Project = 'tests/inventory/Hispania.Inventory.Tests.csproj'; Count = 6; Filter = 'FullyQualifiedName~AdministratorBootstrapRequiresConfiguredCorrectToken|FullyQualifiedName~UsernameIsCaseInsensitiveAndDatabasePreventsConcurrentDuplicates|FullyQualifiedName~RolesPermissionsAssignmentsAndEffectiveAccessPersist|FullyQualifiedName~CompetingSqliteExecutorsAcquireAJobOnlyOnce|FullyQualifiedName~ConcurrentPublishAndDispatchKeepOneAcquisition|FullyQualifiedName~FilesystemProviderRejectsMalformedAndTraversingHandles' },
    @{ Name = 'metadata'; Project = 'tests/library-persistence/Hispania.Library.Persistence.Sqlite.Tests.csproj'; Count = 1; Filter = 'FullyQualifiedName~ConcurrentConfirmationAttemptsKeepTheUniqueIndexAuthoritative' },
    @{ Name = 'fencing'; Project = 'tests/unit/Hispania.Runtime.Tests.csproj'; Count = 3; Filter = 'FullyQualifiedName~LeaseExpiryReassignsOnlyRetrySafeWorkAndFencesOldGeneration|FullyQualifiedName~TwoAuthorizedWorkersCannotLeaseSameGenerationAndFailoverFencesLateResult|FullyQualifiedName~CancellingQueuedOrLeasedWorkFencesLateCompletion' }
)
Push-Location $repository
try {
    for ($iteration = 1; $iteration -le $Repetitions; $iteration++) {
        foreach ($group in $groups) {
            $name = "$($group.Name)-$iteration.trx"
            dotnet test $group.Project --no-build --filter $group.Filter --logger "trx;LogFileName=$name" --results-directory $ResultsDirectory
            if ($LASTEXITCODE -ne 0) { throw "Concurrency failure: $($group.Name), iteration $iteration." }
            $trx = [xml](Get-Content -LiteralPath (Join-Path $ResultsDirectory $name) -Raw)
            $results = @($trx.TestRun.Results.UnitTestResult)
            if ($results.Count -ne $group.Count -or @($results | Where-Object { $_.outcome -ne 'Passed' }).Count -ne 0) {
                throw "Concurrency tests must all be discovered and pass without skips: $($group.Name), iteration $iteration."
            }
        }
        Write-Host "Phase 13 concurrency iteration $iteration/$Repetitions passed."
    }
} finally { Pop-Location }
