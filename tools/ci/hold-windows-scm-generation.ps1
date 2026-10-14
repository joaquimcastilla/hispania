[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ReadyFile,
    [Parameter(Mandatory)][string[]]$Path
)

$ErrorActionPreference = 'Stop'
$handles = [System.Collections.Generic.List[IO.FileStream]]::new()
try {
    $paths = @($Path | ForEach-Object { [IO.Path]::GetFullPath($_) } | Sort-Object -Unique)
    if ($paths.Count -lt 3) { throw 'The manifest and both approved helper files must be locked.' }
    foreach ($itemPath in $paths) {
        $item = Get-Item -LiteralPath $itemPath -Force
        if ($item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Only regular, non-reparse files can be locked.' }
        # FileShare.Read keeps the generation readable while denying write/delete opens
        # until the workflow always-cleanup step ends this process.
        $handles.Add([IO.File]::Open($itemPath, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read))
    }
    [pscustomobject]@{
        processId = $PID
        runId = $env:GITHUB_RUN_ID
        runAttempt = $env:GITHUB_RUN_ATTEMPT
        acquiredUtc = [DateTime]::UtcNow.ToString('O')
        lockedPaths = $paths
    } | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath $ReadyFile -Encoding utf8NoBOM
    while ($true) { Start-Sleep -Seconds 2 }
} finally {
    foreach ($handle in $handles) { $handle.Dispose() }
    if (Test-Path -LiteralPath $ReadyFile) { Remove-Item -LiteralPath $ReadyFile -Force -ErrorAction SilentlyContinue }
}
