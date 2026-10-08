param([switch]$Server, [string]$Action, [string]$InstallationHome, [string]$Role)
$ErrorActionPreference = 'Stop'
$pipeName = 'HispaniaPhase21NativeServices'
if (-not $Server) {
    $pipe = [IO.Pipes.NamedPipeClientStream]::new('.', $pipeName, [IO.Pipes.PipeDirection]::InOut)
    try {
        $pipe.Connect(15000)
        $writer = [IO.StreamWriter]::new($pipe); $writer.AutoFlush = $true
        $reader = [IO.StreamReader]::new($pipe)
        $writer.WriteLine((@{action=$Action;home=$InstallationHome;role=$Role} | ConvertTo-Json -Compress))
        $response = $reader.ReadLine() | ConvertFrom-Json
        if (-not $response.ok) { throw 'Restricted SCM helper rejected or failed the operation; see administrative helper log.' }
    } finally { $pipe.Dispose() }
    exit 0
}
$root = 'C:\ProgramData\HispaniaPhase18CI'
$acl = [IO.Pipes.PipeSecurity]::new()
foreach ($sid in @('S-1-5-20','S-1-5-18','S-1-5-32-544')) {
    $acl.AddAccessRule([IO.Pipes.PipeAccessRule]::new([Security.Principal.SecurityIdentifier]::new($sid), [IO.Pipes.PipeAccessRights]::FullControl, 'Allow'))
}
# Interactive elevated broker expires after eight hours. Runner identity stays NetworkService.
$deadline = [DateTime]::UtcNow.AddHours(8)
while ([DateTime]::UtcNow -lt $deadline) {
    $pipe = [IO.Pipes.NamedPipeServerStreamAcl]::Create($pipeName, [IO.Pipes.PipeDirection]::InOut, 1, [IO.Pipes.PipeTransmissionMode]::Byte, [IO.Pipes.PipeOptions]::Asynchronous, 4096, 4096, $acl)
    try {
        $cancel = [Threading.CancellationTokenSource]::new([TimeSpan]::FromMinutes(5))
        try { $pipe.WaitForConnectionAsync($cancel.Token).GetAwaiter().GetResult() } catch { continue } finally { $cancel.Dispose() }
        $reader = [IO.StreamReader]::new($pipe)
        $writer = [IO.StreamWriter]::new($pipe); $writer.AutoFlush=$true
        $ok=$false
        try {
            $line = $reader.ReadLine()
            if (-not $line -or $line.Length -gt 2048) { throw 'Request bound' }
            $request = $line | ConvertFrom-Json
            if ($request.action -notin @('install','start','stop','restart','status','uninstall','reclaim') -or $request.role -notin @('server','worker')) { throw 'Operation rejected' }
            $home = [IO.Path]::GetFullPath([string]$request.home)
            if ([IO.Path]::GetDirectoryName($home) -cne $root -or [IO.Path]::GetFileName($home) -cnotmatch '^hispania-phase18 isolated ñ [0-9a-f]{32}$') { throw 'Fixture root rejected' }
            foreach ($item in @((Get-Item -LiteralPath $root),(Get-Item -LiteralPath $home)) + @(Get-ChildItem -LiteralPath $home -Recurse -Force)) {
                if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Reparse point rejected' }
            }
            if ($request.action -eq 'reclaim') {
                & icacls.exe (Join-Path $home 'secrets') /reset /T /Q | Out-Null
                if ($LASTEXITCODE -ne 0) { throw 'ACL reclaim failed' }
            } else {
                & (Join-Path $PSScriptRoot 'windows-service.ps1') -Action $request.action -InstallationHome $home -Role $request.role | Out-Null
            }
            $ok=$true
        } catch {
            ('{0:o} operation failed: {1}' -f [DateTime]::UtcNow,$_.Exception.Message) | Add-Content (Join-Path $PSScriptRoot 'broker.log')
        }
        $writer.WriteLine((@{ok=$ok} | ConvertTo-Json -Compress))
    } finally { $pipe.Dispose() }
}
