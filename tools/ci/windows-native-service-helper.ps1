param([switch]$Server, [string]$Action, [string]$InstallationHome, [string]$Role, [string]$ServiceName)
$ErrorActionPreference = 'Stop'
$pipeName = 'HispaniaPhase21NativeServices'
if (-not $Server) {
    $pipe = [IO.Pipes.NamedPipeClientStream]::new('.', $pipeName, [IO.Pipes.PipeDirection]::InOut, [IO.Pipes.PipeOptions]::None, [Security.Principal.TokenImpersonationLevel]::Impersonation)
    try {
        $pipe.Connect(15000)
        $writer = [IO.StreamWriter]::new($pipe); $writer.AutoFlush = $true
        $reader = [IO.StreamReader]::new($pipe)
        $writer.WriteLine((@{action=$Action;home=$InstallationHome;role=$Role;serviceName=$ServiceName} | ConvertTo-Json -Compress))
        $response = $reader.ReadLine() | ConvertFrom-Json
        if (-not $response.ok) { throw ('Restricted SCM helper rejected or failed the operation; operation={0}; stage={1}; win32={2}; hresult={3}.' -f $response.operation,$response.stage,$response.win32,$response.hresult) }
        if ($response.operation -eq 'crash-test') { Write-Output ('Controlled fixture process termination requested at {0:o}; pid={1}.' -f [DateTime]::UtcNow,[uint32]$response.processId) }
    } finally { $pipe.Dispose() }
    exit 0
}
$root = 'C:\ProgramData\HispaniaPhase18CI'
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.IO.Pipes;
using System.Text;
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
public static class HispaniaCiProcess {
    [DllImport("kernel32.dll", SetLastError=true)]
    private static extern SafeFileHandle OpenProcess(uint access, bool inherit, uint processId);
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(SafeFileHandle process, uint flags, StringBuilder imageName, ref uint size);
    [DllImport("kernel32.dll", SetLastError=true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(SafeFileHandle process, uint exitCode);
    public static bool TerminateIfImageMatches(uint processId, string expectedImage, out int error) {
        // Keep this verified process handle open through termination so a PID cannot
        // be recycled between checking its image and applying the test-only failure.
        using (SafeFileHandle process = OpenProcess(0x0001 | 0x1000, false, processId)) {
            if (process.IsInvalid) { error = Marshal.GetLastWin32Error(); return false; }
            StringBuilder actualImage = new StringBuilder(32768);
            uint size = (uint)actualImage.Capacity;
            if (!QueryFullProcessImageName(process, 0, actualImage, ref size)) { error = Marshal.GetLastWin32Error(); return false; }
            if (!String.Equals(System.IO.Path.GetFullPath(actualImage.ToString()), System.IO.Path.GetFullPath(expectedImage), StringComparison.OrdinalIgnoreCase)) { error = 87; return false; }
            if (!TerminateProcess(process, 0xE2)) { error = Marshal.GetLastWin32Error(); return false; }
            error = 0;
            return true;
        }
    }
}
'@
if ($Server) {
    $brokerIdentity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $brokerPrincipal = [Security.Principal.WindowsPrincipal]::new($brokerIdentity)
    if (-not $brokerPrincipal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Privileged helper server must run in an elevated administrator token.' }
    $approvedSha = [string]$env:HISPANIA_PHASE21_APPROVED_GIT_SHA
    $approvalId = [string]$env:HISPANIA_PHASE21_APPROVAL_ID
    $approvedNativeHash = [string]$env:HISPANIA_PHASE21_APPROVED_NATIVE_SHA256
    $approvedServiceHash = [string]$env:HISPANIA_PHASE21_APPROVED_SERVICE_SHA256
    if ($approvedSha -cnotmatch '^[0-9a-f]{40}$' -or $approvalId -cnotmatch '^[A-Za-z0-9._/-]{1,80}$' -or $approvedNativeHash -cnotmatch '^[0-9a-f]{64}$' -or $approvedServiceHash -cnotmatch '^[0-9a-f]{64}$') { throw 'Privileged helper startup requires complete SHA-bound independent approval metadata.' }
    $nativeText = (Get-Content -LiteralPath $PSCommandPath -Raw).Replace("`r`n", "`n")
    $servicePath = Join-Path $PSScriptRoot 'windows-service.ps1'
    $serviceText = (Get-Content -LiteralPath $servicePath -Raw).Replace("`r`n", "`n")
    $encoding = [Text.UTF8Encoding]::new($false)
    $nativeHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($encoding.GetBytes($nativeText))).ToLowerInvariant()
    $serviceHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($encoding.GetBytes($serviceText))).ToLowerInvariant()
    if ($nativeHash -cne $approvedNativeHash -or $serviceHash -cne $approvedServiceHash) { throw 'Privileged helper generation does not match the approved source hashes.' }
    ('{0:o} kind=broker-start; pid={1}; user_sid={2}; elevated_admin=true; source_sha={3}; approval_id={4}; native_sha256={5}; service_sha256={6}' -f [DateTime]::UtcNow,$PID,$brokerIdentity.User.Value,$approvedSha,$approvalId,$nativeHash,$serviceHash) | Add-Content -LiteralPath (Join-Path $PSScriptRoot 'broker.log')
}
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
        $processId=0
        try {
            $line = $reader.ReadLine()
            if (-not $line -or $line.Length -gt 2048) { throw 'Request bound' }
            $request = $line | ConvertFrom-Json
            if ($request.action -notin @('install','start','stop','restart','status','uninstall','reclaim','retained-secret','crash-test') -or $request.role -notin @('server','worker') -or [string]$request.serviceName -cnotmatch '^Hispania-Test-[0-9a-f]{32}-(Server|Worker)$') { throw 'Operation rejected' }
            if (($request.role -eq 'server' -and -not [string]$request.serviceName.EndsWith('-Server')) -or ($request.role -eq 'worker' -and -not [string]$request.serviceName.EndsWith('-Worker'))) { throw 'Service role mismatch' }
            $operation=$request.action; $stage='fixture-validation'
            $fixtureHome = [IO.Path]::GetFullPath([string]$request.home)
            if ([IO.Path]::GetDirectoryName($fixtureHome) -cne $root -or [IO.Path]::GetFileName($fixtureHome) -cnotmatch '^hispania-phase18 isolated ñ [0-9a-f]{32}$') { throw 'Fixture root rejected' }
            $fixtureRunId=[regex]::Match([IO.Path]::GetFileName($fixtureHome),'^hispania-phase18 isolated ñ ([0-9a-f]{32})$').Groups[1].Value
            if ($request.serviceName -cne ('Hispania-Test-'+$fixtureRunId+$(if($request.role -eq 'server'){'-Server'}else{'-Worker'}))) { throw 'Service name does not belong to fixture run' }
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
            } elseif ($request.action -eq 'crash-test') {
                $stage='crash-service-ownership'
                $directory=if ($request.role -eq 'server') { 'server' } else { 'worker' }
                $assembly=if ($request.role -eq 'server') { 'Hispania.Server' } else { 'Hispania.WorkerAgent' }
                $expectedExecutable=[IO.Path]::GetFullPath((Join-Path $fixtureHome "app/$directory/$assembly.exe"))
                $expectedServicePath='"'+$expectedExecutable+'" --home "'+$fixtureHome+'"'
                $registered=Get-CimInstance Win32_Service -Filter "Name='$($request.serviceName)'" -ErrorAction SilentlyContinue
                if (-not $registered -or $registered.State -ne 'Running' -or [uint32]$registered.ProcessId -eq 0 -or $registered.PathName -cne $expectedServicePath -or $registered.StartName -cne 'NT AUTHORITY\LocalService') { throw 'Crash test requires the running LocalService owned by this exact fixture.' }
                $ownedProcessId=[uint32]$registered.ProcessId
                $process=Get-CimInstance Win32_Process -Filter "ProcessId=$ownedProcessId" -ErrorAction SilentlyContinue
                if (-not $process -or [IO.Path]::GetFullPath([string]$process.ExecutablePath) -cne $expectedExecutable) { throw 'Crash test process image does not match the owned fixture executable.' }
                # Re-read SCM registration and process identity immediately before opening
                # the process handle; abort if either changed during validation.
                $registeredAgain=Get-CimInstance Win32_Service -Filter "Name='$($request.serviceName)'" -ErrorAction SilentlyContinue
                $processAgain=Get-CimInstance Win32_Process -Filter "ProcessId=$ownedProcessId" -ErrorAction SilentlyContinue
                if (-not $registeredAgain -or $registeredAgain.State -ne 'Running' -or [uint32]$registeredAgain.ProcessId -ne $ownedProcessId -or $registeredAgain.PathName -cne $expectedServicePath -or $registeredAgain.StartName -cne 'NT AUTHORITY\LocalService' -or -not $processAgain -or [IO.Path]::GetFullPath([string]$processAgain.ExecutablePath) -cne $expectedExecutable) { throw 'Service or process identity changed; controlled crash aborted.' }
                $stage='controlled-process-termination'
                $nativeError=0
                if (-not [HispaniaCiProcess]::TerminateIfImageMatches($ownedProcessId,$expectedExecutable,[ref]$nativeError)) { throw [ComponentModel.Win32Exception]::new($nativeError,'Could not terminate the verified fixture service process.') }
                $processId=$ownedProcessId
                $stage='crash-audit-record'
                ('{0:o} operation=crash-test; run={1}; service={2}; pid={3}; result=terminated-verified-fixture-image' -f [DateTime]::UtcNow,$fixtureRunId,$request.serviceName,$processId) | Add-Content (Join-Path $PSScriptRoot 'broker.log')
            } else {
                $stage='scm-helper'
                & (Join-Path $PSScriptRoot 'windows-service.ps1') -Action $request.action -InstallationHome $fixtureHome -Role $request.role -ServiceName $request.serviceName | Out-Null
            }
            $ok=$true
        } catch {
            $errorException=$_.Exception; $hresult=$errorException.HResult
            while ($errorException.InnerException) { $errorException=$errorException.InnerException }
            if ($errorException -is [ComponentModel.Win32Exception]) { $win32=$errorException.NativeErrorCode }
            # Fixed categories and numeric codes only; never serialize exception text or stderr.
            ('{0:o} operation={1}; stage={2}; win32={3}; hresult={4}' -f [DateTime]::UtcNow,$operation,$stage,$win32,$hresult) | Add-Content (Join-Path $PSScriptRoot 'broker.log')
        }
        $writer.WriteLine((@{ok=$ok;operation=$operation;stage=$stage;win32=$win32;hresult=$hresult;processId=$processId} | ConvertTo-Json -Compress))
    } catch [IO.IOException] {
        # A client disconnect must not terminate the privileged broker listener.
    } finally { $pipe.Dispose() }
}
