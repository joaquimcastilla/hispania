[CmdletBinding()]
param(
    [ValidateSet('Smoke', 'Short', 'Soak', 'Extended')][string]$Profile = 'Smoke',
    [int]$Seed = 24001,
    [string]$OutputDirectory = '.artifacts/phase24',
    [string]$Campaign,
    [string]$ManifestPath,
    [int]$MaxMemoryMb,
    [int]$MaxCpuPercent,
    [int]$MaxConcurrency,
    [ValidateRange(100, 60000)][int]$WorkloadIntervalMilliseconds = 1000,
    [switch]$ConfirmExtended
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$pwshVersion = $PSVersionTable.PSVersion
if ($pwshVersion.Major -lt 7) { throw 'Phase 24 requires PowerShell 7 or later.' }
if ($Profile -eq 'Extended' -and -not $ConfirmExtended) { throw 'Extended requires the explicit -ConfirmExtended switch.' }
if ($Seed -lt 0) { throw 'Seed must be nonnegative.' }
$outputFull = if ([IO.Path]::IsPathRooted($OutputDirectory)) { [IO.Path]::GetFullPath($OutputDirectory) } else { [IO.Path]::GetFullPath((Join-Path $repo $OutputDirectory)) }
$repoArtifacts = [IO.Path]::GetFullPath((Join-Path $repo '.artifacts')).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
$tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
if (-not $outputFull.StartsWith($repoArtifacts, [StringComparison]::OrdinalIgnoreCase) -and -not $outputFull.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'OutputDirectory must be inside repository .artifacts or the operating-system temporary directory.' }
New-Item -ItemType Directory -Path $outputFull -Force | Out-Null
Push-Location $repo
try {
    $project = Join-Path $repo 'tools/phase24/Hispania.ResilienceHarness.csproj'
    $server = Join-Path $repo 'apps/server/Hispania.Server.csproj'
    dotnet build $server --verbosity quiet
    if ($LASTEXITCODE -ne 0) { throw 'HISPANIA Server build failed.' }
    dotnet build $project --verbosity quiet
    if ($LASTEXITCODE -ne 0) { throw 'Phase 24 harness build failed.' }
    $arguments = @('--profile', $Profile, '--seed', $Seed.ToString([Globalization.CultureInfo]::InvariantCulture), '--output-directory', $outputFull)
    if ($Campaign) { $arguments += @('--campaign', $Campaign) }
    if ($ManifestPath) { $arguments += @('--manifest-path', [IO.Path]::GetFullPath($ManifestPath)) }
    if ($MaxMemoryMb -gt 0) { $arguments += @('--max-memory-mb', $MaxMemoryMb.ToString([Globalization.CultureInfo]::InvariantCulture)) }
    if ($MaxCpuPercent -gt 0) { $arguments += @('--max-cpu-percent', $MaxCpuPercent.ToString([Globalization.CultureInfo]::InvariantCulture)) }
    if ($MaxConcurrency -gt 0) { $arguments += @('--max-concurrency', $MaxConcurrency.ToString([Globalization.CultureInfo]::InvariantCulture)) }
    $arguments += @('--workload-interval-milliseconds', $WorkloadIntervalMilliseconds.ToString([Globalization.CultureInfo]::InvariantCulture))
    if ($ConfirmExtended) { $arguments += '--confirm-extended' }
    dotnet run --no-build --project $project -- @arguments
    if ($LASTEXITCODE -ne 0) { throw "Phase 24 campaign failed with exit code $LASTEXITCODE." }
} finally { Pop-Location }
