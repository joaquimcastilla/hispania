param([switch]$Server, [string]$Action, [string]$InstallationHome, [string]$Role)
$ErrorActionPreference = 'Stop'
$pipeName = 'HispaniaPhase21NativeServices'
if (-not $Server) {
    $pipe = [IO.Pipes.NamedPipeClientStream]::new('.', $pipeName, [IO.Pipes.PipeDirection]::InOut, [IO.Pipes.PipeOptions]::None, [Security.Principal.TokenImpersonationLevel]::Impersonation)
    try {
        $pipe.Connect(15000)
        $writer = [IO.StreamWriter]::new($pipe); $writer.AutoFlush = $true
        $reader = [IO.StreamReader]::new($pipe)
        $writer.WriteLine((@{action=$Action;home=$InstallationHome;role=$Role} | ConvertTo-Json -Compress))
        $response = $reader.ReadLine() | ConvertFrom-Json
        if (-not $response.ok) { throw ('Restricted SCM helper rejected or failed the operation; operation={0}; stage={1}; win32={2}; hresult={3}.' -f $response.operation,$response.stage,$response.win32,$response.hresult) }
    } finally { $pipe.Dispose() }
    exit 0
}
$root = 'C:\ProgramData\HispaniaPhase18CI'
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.IO.Pipes;
using Microsoft.Win32.SafeHandles;
public static class HispaniaCiFileLinks {
    [StructLayout(LayoutKind.Sequential)]
    public struct Information {
        public uint Attributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME Created, Accessed, Written;
        public uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }
    [DllImport("kernel32.dll", SetLastError=true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetFileInformationByHandle(SafeFileHandle handle, out Information information);
    // A zero desired-access handle queries metadata without reading private secrets.
    [DllImport("kernel32.dll", EntryPoint="CreateFileW", CharSet=CharSet.Unicode, SetLastError=true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    public static SafeFileHandle OpenMetadata(string path) {
        return CreateFile(path, 0, 7, IntPtr.Zero, 3, 0x00200000, IntPtr.Zero);
    }
    public static SafeFileHandle OpenMetadataForCaller(NamedPipeServerStream pipe, string path) {
        SafeFileHandle handle = null;
        pipe.RunAsClient(() => {
            handle = OpenMetadata(path);
            if (handle.IsInvalid) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        });
        return handle;
    }
}
'@
$acl = [IO.Pipes.PipeSecurity]::new()
foreach ($sid in @('S-1-5-20','S-1-5-18','S-1-5-32-544')) {
    $rights = if ($sid -eq 'S-1-5-20') { [IO.Pipes.PipeAccessRights]::ReadWrite } else { [IO.Pipes.PipeAccessRights]::FullControl }
    $acl.AddAccessRule([IO.Pipes.PipeAccessRule]::new([Security.Principal.SecurityIdentifier]::new($sid), $rights, 'Allow'))
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
        $operation='validation'; $stage='request'; $win32=0; $hresult=0
        try {
            $line = $reader.ReadLine()
            if (-not $line -or $line.Length -gt 2048) { throw 'Request bound' }
            $request = $line | ConvertFrom-Json
            if ($request.action -notin @('install','start','stop','restart','status','uninstall','reclaim','retained-secret') -or $request.role -notin @('server','worker')) { throw 'Operation rejected' }
            $operation=$request.action; $stage='fixture-validation'
            $fixtureHome = [IO.Path]::GetFullPath([string]$request.home)
            if ([IO.Path]::GetDirectoryName($fixtureHome) -cne $root -or [IO.Path]::GetFileName($fixtureHome) -cnotmatch '^hispania-phase18 isolated ñ [0-9a-f]{32}$') { throw 'Fixture root rejected' }
            foreach ($item in @((Get-Item -LiteralPath $root),(Get-Item -LiteralPath $fixtureHome)) + @(Get-ChildItem -LiteralPath $fixtureHome -Recurse -Force)) {
                if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Reparse point rejected' }
                if (-not $item.PSIsContainer) {
                    $stage='metadata-open'
                    $handle = [HispaniaCiFileLinks]::OpenMetadata($item.FullName)
                    if ($handle.IsInvalid -and [Runtime.InteropServices.Marshal]::GetLastWin32Error() -eq 5) {
                        $handle.Dispose()
                        # Before install, owner-only secrets belong to the non-admin caller.
                        # Impersonate only for the metadata open, never for SCM operations.
                        $handle = [HispaniaCiFileLinks]::OpenMetadataForCaller($pipe,$item.FullName)
                    }
                    try {
                        if ($handle.IsInvalid) { throw [ComponentModel.Win32Exception]::new([Runtime.InteropServices.Marshal]::GetLastWin32Error()) }
                        $stage='metadata-query'
                        $information = [HispaniaCiFileLinks+Information]::new()
                        if (-not [HispaniaCiFileLinks]::GetFileInformationByHandle($handle,[ref]$information)) { throw [ComponentModel.Win32Exception]::new([Runtime.InteropServices.Marshal]::GetLastWin32Error()) }
                        if ($information.Links -ne 1 -or ($information.Attributes -band 0x400)) { throw 'Hardlink or reparse point rejected' }
                    } finally { $handle.Dispose() }
                }
            }
            if ($request.action -eq 'retained-secret') {
                $stage='retained-secret'
                $retained=Join-Path $fixtureHome 'secrets/NATIVE_CANARY.secret'
                if (-not [IO.File]::Exists($retained)) { throw 'Retained secret missing' }
                $privateAcl=Get-Acl -LiteralPath $retained
                if (-not $privateAcl.AreAccessRulesProtected -or $privateAcl.GetOwner([Security.Principal.SecurityIdentifier]).Value -ne 'S-1-5-32-544') { throw 'Private ownership policy failed' }
                $rules=@($privateAcl.Access)
                if ($rules.Count -ne 3) { throw 'Private access policy failed' }
                foreach ($rule in $rules) {
                    $sid=$rule.IdentityReference.Translate([Security.Principal.SecurityIdentifier]).Value
                    $expectedRights=if ($sid -eq 'S-1-5-19') { [Security.AccessControl.FileSystemRights]::ReadAndExecute } elseif ($sid -in @('S-1-5-18','S-1-5-32-544')) { [Security.AccessControl.FileSystemRights]::FullControl } else { throw 'Unexpected private principal' }
                    if ($rule.AccessControlType -ne 'Allow' -or ($rule.FileSystemRights -band (-bnot [Security.AccessControl.FileSystemRights]::Synchronize)) -ne ($expectedRights -band (-bnot [Security.AccessControl.FileSystemRights]::Synchronize))) { throw 'Private rights policy failed' }
                }
            } elseif ($request.action -eq 'reclaim') {
                $stage='acl-reclaim'
                & icacls.exe (Join-Path $fixtureHome 'secrets') /reset /T /Q | Out-Null
                if ($LASTEXITCODE -ne 0) { throw 'ACL reclaim failed' }
            } else {
                $stage='scm-helper'
                & (Join-Path $PSScriptRoot 'windows-service.ps1') -Action $request.action -InstallationHome $fixtureHome -Role $request.role | Out-Null
            }
            $ok=$true
        } catch {
            $errorException=$_.Exception; $hresult=$errorException.HResult
            while ($errorException.InnerException) { $errorException=$errorException.InnerException }
            if ($errorException -is [ComponentModel.Win32Exception]) { $win32=$errorException.NativeErrorCode }
            # Fixed categories and numeric codes only; never serialize exception text or stderr.
            ('{0:o} operation={1}; stage={2}; win32={3}; hresult={4}' -f [DateTime]::UtcNow,$operation,$stage,$win32,$hresult) | Add-Content (Join-Path $PSScriptRoot 'broker.log')
        }
        $writer.WriteLine((@{ok=$ok;operation=$operation;stage=$stage;win32=$win32;hresult=$hresult} | ConvertTo-Json -Compress))
    } catch [IO.IOException] {
        # A client disconnect must not terminate the privileged broker listener.
    } finally { $pipe.Dispose() }
}
