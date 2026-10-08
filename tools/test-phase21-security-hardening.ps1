param([string]$ResultsDirectory='.artifacts/phase21-security', [switch]$AuditDependencies)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$results = [IO.Path]::GetFullPath((Join-Path $repo $ResultsDirectory))
New-Item -ItemType Directory -Path $results -Force | Out-Null
Push-Location $repo
$previousEvidenceDirectory = $env:HISPANIA_SECURITY_EVIDENCE_DIRECTORY
$env:HISPANIA_SECURITY_EVIDENCE_DIRECTORY = $results
try {
    $suites = @(
        @{ Project='tests/unit/Hispania.Runtime.Tests.csproj'; Filter='FullyQualifiedName~Phase21SecurityHardening'; Name='runtime'; Minimum=32 },
        @{ Project='tests/inventory/Hispania.Inventory.Tests.csproj'; Filter='FullyQualifiedName~Phase21SecurityHardening'; Name='http-identity'; Minimum=7 },
        @{ Project='tests/inventory/Hispania.Inventory.Tests.csproj'; Filter='FullyQualifiedName~PlatformAuthorizationTests'; Name='bootstrap-permission-races'; Minimum=2 },
        @{ Project='tests/deployment/Hispania.Deployment.Tests.csproj'; Filter='FullyQualifiedName~Phase21CertificateSecurity'; Name='certificates'; Minimum=4 },
        @{ Project='tests/playback/Hispania.Playback.Tests.csproj'; Filter='FullyQualifiedName~Phase21EndpointPolicy'; Name='endpoint-policies'; Minimum=3 },
        @{ Project='tests/playback/Hispania.Playback.Tests.csproj'; Filter='FullyQualifiedName~Phase21MtlsAbuse'; Name='mtls-abuse'; Minimum=1 },
        @{ Project='tests/playback/Hispania.Playback.Tests.csproj'; Filter='FullyQualifiedName~Phase21DeterministicRanges'; Name='ranges-opaque-ids'; Minimum=2 },
        @{ Project='tests/deployment/Hispania.Deployment.Tests.csproj'; Filter='FullyQualifiedName~Phase21SecretCanary'; Name='secret-canaries'; Minimum=1 }
    )
    foreach ($suite in $suites) {
        dotnet test $suite.Project --filter $suite.Filter --results-directory $results --logger "trx;LogFileName=$($suite.Name).trx" --verbosity quiet 2>&1 | Tee-Object -FilePath (Join-Path $results "$($suite.Name).log")
        if ($LASTEXITCODE -ne 0) { throw "Security suite $($suite.Name) failed." }
        [xml]$report = Get-Content -LiteralPath (Join-Path $results "$($suite.Name).trx")
        $tests = @($report.TestRun.Results.UnitTestResult)
        if ($tests.Count -lt $suite.Minimum -or @($tests | Where-Object outcome -ne Passed).Count -ne 0) { throw 'Security gate rejects missing, failed or skipped tests.' }
    }
    $canaries = @('canary-password-never-log','phase21 password canary','PHASE21_PASSWORD_CANARY','PHASE21_BEARER_CANARY','PHASE21_SESSION_CANARY','PHASE21_POSTGRES_CANARY','PHASE21_MTLS_PRIVATE_KEY_CANARY','PHASE21_BOOTSTRAP_CANARY','PHASE21_SIGNING_PRIVATE_KEY_CANARY')
    foreach ($file in Get-ChildItem -LiteralPath $results -Recurse -File | Where-Object Extension -in '.log','.trx','.txt','.json') {
        $content = [IO.File]::ReadAllText($file.FullName)
        foreach ($canary in $canaries) { if ($content.Contains($canary, [StringComparison]::Ordinal)) { throw 'Secret canary leaked into security output (value withheld).' } }
    }
    if ($AuditDependencies) {
        $auditSolution = if ($IsWindows) { 'Hispania.sln' } else { 'Hispania.CrossPlatform.slnf' }
        dotnet list $auditSolution package --vulnerable --include-transitive --format json --no-restore > (Join-Path $results 'nuget-audit.json')
        if ($LASTEXITCODE -ne 0) { throw 'NuGet audit unavailable or failed.' }
        $nuget = Get-Content -LiteralPath (Join-Path $results 'nuget-audit.json') -Raw | ConvertFrom-Json
        foreach ($project in $nuget.projects) { foreach ($framework in $project.frameworks) { foreach ($package in @($framework.topLevelPackages) + @($framework.transitivePackages)) { if (@($package.vulnerabilities).Count -gt 0 -and $package.vulnerabilities) { throw 'NuGet advisory requires explicit reachability assessment.' } } } }
        npm audit --omit=dev --json --prefix apps/web > (Join-Path $results 'npm-runtime-audit.json')
        if ($LASTEXITCODE -ne 0) { throw 'Runtime npm vulnerability requires assessment before acceptance.' }
        npm audit --json --prefix apps/web > (Join-Path $results 'npm-all-audit.json')
        $auditExit = $LASTEXITCODE
        $audit = Get-Content -LiteralPath (Join-Path $results 'npm-all-audit.json') -Raw | ConvertFrom-Json
        if ($auditExit -notin 0,1) { throw 'npm audit failed to produce evidence.' }
        foreach ($finding in $audit.vulnerabilities.PSObject.Properties) {
            if ($finding.Name -ne 'source-map-js' -or $finding.Value.nodes.Count -ne 1 -or $finding.Value.via.Count -ne 1 -or $finding.Value.severity -ne 'high' -or $finding.Value.via[0].url -ne 'https://github.com/advisories/GHSA-68fv-2mgg-jv7q') { throw 'New build advisory requires an explicit scope review.' }
        }
        # Exact build-only advisory assessed in docs/security/SUPPLY-CHAIN.md; never waive runtime findings.
    }
    git rev-parse HEAD > (Join-Path $results 'git-sha.txt')
    Write-Output 'Phase21 bounded security gate passed; complete phase acceptance also requires existing regression and production gates.'
} finally { $env:HISPANIA_SECURITY_EVIDENCE_DIRECTORY = $previousEvidenceDirectory; Pop-Location }
