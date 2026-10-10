$ErrorActionPreference='Stop'
$repo = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$probe = Join-Path $env:RUNNER_TEMP ('phase21-synthetic-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $probe | Out-Null
try {
    # Assemble the fixture rather than echoing it into captured evidence.
    [IO.File]::WriteAllText((Join-Path $probe 'probe.txt'), ('PHASE21_' + 'SESSION_CANARY'))
    $caught=$null
    try { & (Join-Path $repo 'tools/test-security-canaries.ps1') -EvidenceDirectory $probe } catch { $caught=$_.Exception.Message }
    if ($caught -cne 'Fake secret canary leaked into output (value and content withheld).') { throw 'Synthetic leak probe must detect the deliberate secret using the value-withheld failure.' }
    Write-Output 'Synthetic leak correctly rejected; fake value withheld; temporary evidence removed.'
} finally {
    if ([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($probe)) -ne [IO.Path]::GetFullPath($env:RUNNER_TEMP).TrimEnd([IO.Path]::DirectorySeparatorChar)) { throw 'Probe cleanup escaped runner temp.' }
    Remove-Item -LiteralPath $probe -Recurse -Force
}
