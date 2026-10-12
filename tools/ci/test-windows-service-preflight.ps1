[CmdletBinding()]
param([string]$ServiceScript = (Join-Path $PSScriptRoot '../../deployment/windows-service.ps1'))
$ErrorActionPreference = 'Stop'
if (-not $IsWindows) { throw 'This preflight regression requires Windows PowerShell semantics.' }
$serviceScriptPath = (Resolve-Path -LiteralPath $ServiceScript).Path
$tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')
$runId = [Guid]::NewGuid().ToString('N')
$fixtureHome = Join-Path $tempRoot ('hispania-phase18 isolated ñ ' + $runId)
New-Item -ItemType Directory -Path (Join-Path $fixtureHome 'config') -Force | Out-Null
function Assert-RejectedBeforeScm([string]$Name, [string]$ExpectedMessage) {
    $message = $null
    try { & $serviceScriptPath -Action status -Home $fixtureHome -Role server -ServiceName $Name } catch { $message = $_.Exception.Message }
    if (-not $message -or -not $message.Contains($ExpectedMessage, [StringComparison]::Ordinal)) { throw 'Unsafe service request was not rejected by the expected pre-SCM guard.' }
}
try {
    [IO.File]::WriteAllText((Join-Path $fixtureHome 'config/production.json'), '{"role":"server"}')
    Assert-RejectedBeforeScm 'Hispania/Test' 'Invalid Windows SCM service name.'
    $otherRun = [Guid]::NewGuid().ToString('N')
    Assert-RejectedBeforeScm ('Hispania-Test-' + $otherRun + '-Server') 'does not belong to this role and fixture run.'
    "PASS: malformed and cross-run service names rejected before SCM; run=$runId"
} finally {
    if ([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($fixtureHome)) -cne $tempRoot -or [IO.Path]::GetFileName($fixtureHome) -cne ('hispania-phase18 isolated ñ ' + $runId)) { throw 'Temporary fixture ownership check failed; refusing cleanup.' }
    Remove-Item -LiteralPath $fixtureHome -Recurse -Force
}
