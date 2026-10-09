[CmdletBinding()]
param([string]$ResultsDirectory = '.artifacts/phase23')
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$results = if ([IO.Path]::IsPathRooted($ResultsDirectory)) { [IO.Path]::GetFullPath($ResultsDirectory) } else { [IO.Path]::GetFullPath((Join-Path $repo $ResultsDirectory)) }
New-Item -ItemType Directory -Path $results -Force | Out-Null
Push-Location $repo
try {
    $base = '468f1ab87e242ed241d88fa8963aabace5b4ca52'
    $head = (& git rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0) { throw 'Cannot resolve current Phase23 worktree HEAD.' }
    & git cat-file -e "$base`^{commit}" 2>$null
    if ($LASTEXITCODE -ne 0) { throw 'Accepted Phase22 base commit is absent.' }
    $workingTree = @(& git status --porcelain)
    if ($head -eq $base -and $workingTree.Count -eq 0) { throw 'Phase23 gate requires implementation commits or working changes beyond the Phase22 base.' }

    $inventoryPath = Join-Path $results 'fixture-inventory.json'
    $inventoryOutput = & python tools/phase23-compatibility-inventory.py $inventoryPath
    if ($LASTEXITCODE -ne 0) { throw 'Immutable Phase15/16 historical corpus verification failed.' }
    $inventory = Get-Content -LiteralPath $inventoryPath -Raw | ConvertFrom-Json
    if ($inventory.phase15CompatibilityArtifactsVerified -ne 27 -or $inventory.phase16HistoricalDataArtifactsVerified -ne 15 -or $inventory.errors.Count -ne 0) { throw 'Historical fixture membership or hashes do not match the pinned manifests.' }

    function Invoke-Gate([string]$Name, [string]$Script, [string[]]$Arguments) {
        $log = Join-Path $results "$Name.log"
        & pwsh -NoProfile -NonInteractive -File (Join-Path $repo $Script) @Arguments 2>&1 | Tee-Object -FilePath $log
        if ($LASTEXITCODE -ne 0) { throw "Integrated compatibility gate '$Name' failed; see $log." }
    }

    Invoke-Gate 'phase15' 'tools/test-phase15-compatibility.ps1' @('-ResultsDirectory', '.artifacts/phase23/phase15')
    Invoke-Gate 'phase16' 'tools/test-phase16-historical-upgrades.ps1' @('-ResultsDirectory', '.artifacts/phase23/phase16')
    Invoke-Gate 'phase17' 'tools/test-phase17-backup-restore.ps1' @('-ResultsDirectory', '.artifacts/phase23/phase17')
    Invoke-Gate 'phase19' 'tools/test-phase19-package-lifecycle.ps1' @('-ResultsDirectory', '.artifacts/phase23/phase19')
    Invoke-Gate 'phase20' 'tools/test-phase20-dynamic-web.ps1' @('-ResultsDirectory', '.artifacts/phase23/phase20')
    Invoke-Gate 'phase21' 'tools/test-phase21-security-hardening.ps1' @('-ResultsDirectory', '.artifacts/phase23/phase21')
    & dotnet restore Hispania.Platform.slnf --verbosity quiet
    if ($LASTEXITCODE -ne 0) { throw 'Platform solution restore failed before the Phase22 no-restore build.' }
    Invoke-Gate 'phase22' 'tools/test-phase22-retention-reconciliation-capacity.ps1' @('-ResultsDirectory', '.artifacts/phase23/phase22')

    $summary = [ordered]@{
        gate = 'Phase23EndToEndCompatibilityUpgrade'
        baseSha = $base
        phase23WorktreeHead = $head
        historicalSourceSha = '78f291fdacf9442c6e2d8e23094dc0e309828693'
        compatibilityArtifactsVerified = $inventory.phase15CompatibilityArtifactsVerified
        populatedDataArtifactsVerified = $inventory.phase16HistoricalDataArtifactsVerified
        completeHistoricalReleaseSnapshots = 0
        executedGates = @('Phase15Compatibility', 'Phase16HistoricalDataUpgrade', 'Phase17BackupRestore', 'Phase19PackageLifecycle', 'Phase20DynamicWeb', 'Phase21SecurityHardening', 'Phase22Maintenance')
        PostgreSqlLive = if ($env:HISPANIA_POSTGRES_TEST_CONNECTION) { 'Phase16 available; Phase17/18/22 live scopes depend on gate invocation' } else { 'not run: no PostgreSQL test connection configured' }
        NativeServices = 'not run: privileged Windows SCM/Linux systemd installation is platform-runner coverage'
        powerLoss = 'not claimed: tests inject process termination, not physical power loss'
        secretsScan = 'run by Phase21 gate over its own captured artifacts'
        status = 'passed'
    }
    $summary | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $results 'phase23-summary.json') -Encoding utf8NoBOM
    Set-Content -LiteralPath (Join-Path $results 'phase23-target-sha.txt') -Value $head -Encoding ascii
    Write-Output "Phase23 integrated compatibility gate passed at $head; historical artifacts 27/27, populated owner data artifacts 15/15."
} finally { Pop-Location }
