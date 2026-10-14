[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('install','start','stop','restart','status','uninstall','retained-secret','reclaim','crash-test')][string]$Action,
    [Parameter(Mandatory)][string]$InstallationHome,
    [Parameter(Mandatory)][ValidateSet('server','worker')][string]$Role,
    [string]$ServiceName
)

# Test-only direct SCM adapter for a fresh GitHub-hosted VM. This does not use,
# copy, or claim approval of the protected JOAQUIM helper generation.
$ErrorActionPreference = 'Stop'
$stage = 'hosted-runner-context'

Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
public static class HispaniaHostedScmNative {
    [StructLayout(LayoutKind.Sequential)]
    public struct FileInformation {
        public uint Attributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME Created, Accessed, Written;
        public uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }
    [DllImport("kernel32.dll", EntryPoint="CreateFileW", CharSet=CharSet.Unicode, SetLastError=true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError=true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation information);
    public static SafeFileHandle OpenMetadata(string path) { return CreateFile(path, 0, 7, IntPtr.Zero, 3, 0x00200000, IntPtr.Zero); }

    [DllImport("kernel32.dll", SetLastError=true)]
    private static extern SafeFileHandle OpenProcess(uint access, bool inherit, uint processId);
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(SafeFileHandle process, uint flags, StringBuilder imageName, ref uint size);
    [DllImport("kernel32.dll", SetLastError=true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(SafeFileHandle process, uint exitCode);
    public static bool TerminateIfImageMatches(uint processId, string expectedImage, out int error) {
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

try {
    if ($env:GITHUB_ACTIONS -cne 'true' -or $env:HISPANIA_RUNNER_ENVIRONMENT -cne 'github-hosted' -or $env:RUNNER_OS -cne 'Windows' -or $env:RUNNER_ARCH -cne 'X64') { throw 'Runner is not the isolated GitHub-hosted Windows x64 environment.' }
    if ($env:GITHUB_REPOSITORY -cne 'joaquimcastilla/hispania' -or $env:GITHUB_EVENT_NAME -cne 'workflow_dispatch' -or $env:GITHUB_REF -cne 'refs/heads/codex/hispania-0.7.0-step2-1-production-readiness' -or $env:GITHUB_ACTOR -cne 'joaquimcastilla' -or $env:HISPANIA_TRIGGERING_ACTOR -cne 'joaquimcastilla' -or $env:HISPANIA_CANDIDATE_SHA -cne $env:GITHUB_SHA -or $env:GITHUB_SHA -cnotmatch '^[0-9a-f]{40}$') { throw 'Repository, actor, branch, or candidate SHA is not authorized.' }
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Runner token is not a member of Administrators.' }
    $integrity = @(whoami.exe /groups /fo csv | ConvertFrom-Csv | Where-Object { $_.SID -eq 'S-1-16-12288' -or $_.'Nombre conocido' -match 'High Mandatory|Nivel obligatorio alto' })
    if ($integrity.Count -eq 0) { throw 'Runner process does not have a high-integrity token.' }

    $stage = 'fixture-path-validation'
    $root = [IO.Path]::GetFullPath($env:HISPANIA_PHASE18_SERVICE_ROOT)
    $baseRoot = [IO.Path]::GetFullPath((Join-Path $env:ProgramData 'HispaniaPhase18CI'))
    if ((Get-Item -LiteralPath $baseRoot -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Fixture base is a reparse point.' }
    $expectedRoot = [IO.Path]::GetFullPath((Join-Path $baseRoot "$env:GITHUB_RUN_ID-$env:GITHUB_RUN_ATTEMPT"))
    if ($root -ine $expectedRoot) { throw 'Configured service root is not the exact per-job temporary fixture root.' }
    $programDataPrefix = [IO.Path]::GetFullPath($env:ProgramData).TrimEnd('\') + '\'
    if (-not $root.StartsWith($programDataPrefix,[StringComparison]::OrdinalIgnoreCase)) { throw 'Service root escapes the ephemeral VM ProgramData fixture boundary.' }
    if (-not [IO.Path]::IsPathFullyQualified($InstallationHome) -or $InstallationHome.Contains('"') -or [regex]::IsMatch($InstallationHome,'[\x00-\x1f\x7f]')) { throw 'Fixture home must be a clean absolute path.' }
    $home = [IO.Path]::GetFullPath($InstallationHome)
    if ([IO.Path]::GetDirectoryName($home) -ine $root -or [IO.Path]::GetFileName($home) -cnotmatch '^hispania-phase18 isolated ñ ([0-9a-f]{32})$') { throw 'Fixture home is not a unique direct child of the job-scoped root.' }
    $fixtureId = [regex]::Match([IO.Path]::GetFileName($home),'^hispania-phase18 isolated ñ ([0-9a-f]{32})$').Groups[1].Value
    $expectedSuffix = if ($Role -eq 'server') { '-Server' } else { '-Worker' }
    $expectedService = 'Hispania-Test-' + $fixtureId + $expectedSuffix
    if ($ServiceName -cne $expectedService) { throw 'Service name is not bound to this fixture GUID and role.' }
    if (-not (Test-Path -LiteralPath $home -PathType Container) -or -not (Test-Path -LiteralPath $root -PathType Container)) { throw 'Fixture home or root does not exist.' }
    foreach ($path in @($root,$home)) {
        if ((Get-Item -LiteralPath $path -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Fixture root/home is a reparse point.' }
    }

    $stage = 'fixture-file-integrity'
    foreach ($item in Get-ChildItem -LiteralPath $home -Force -Recurse -ErrorAction SilentlyContinue) {
        if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Fixture contains a reparse point.' }
        if (-not $item.PSIsContainer) {
            $handle = [HispaniaHostedScmNative]::OpenMetadata($item.FullName)
            try {
                if ($handle.IsInvalid) { throw 'Could not safely inspect a fixture file.' }
                $info = [HispaniaHostedScmNative+FileInformation]::new()
                if (-not [HispaniaHostedScmNative]::GetFileInformationByHandle($handle,[ref]$info) -or $info.Links -ne 1 -or ($info.Attributes -band 0x400)) { throw 'Fixture hardlink/reparse validation failed.' }
            } finally { $handle.Dispose() }
        }
    }

    $stage = 'artifact-and-configuration-validation'
    $artifact = [IO.Path]::GetFullPath($env:HISPANIA_PHASE18_ARTIFACT)
    $expectedArtifact = [IO.Path]::GetFullPath((Join-Path $env:GITHUB_WORKSPACE '.artifacts/phase18/win-x64'))
    if ($artifact -ine $expectedArtifact -or -not (Test-Path -LiteralPath (Join-Path $artifact 'artifact-manifest.json') -PathType Leaf)) { throw 'SCM helper requires the exact published win-x64 artifact.' }
    $manifest = Get-Content -LiteralPath (Join-Path $artifact 'artifact-manifest.json') -Raw | ConvertFrom-Json
    if ($manifest.gitSha -cne $env:GITHUB_SHA -or $manifest.sourceDirty -ne $false -or $manifest.targetRid -cne 'win-x64') { throw 'Published artifact provenance does not match the exact clean candidate SHA.' }
    $operations = Join-Path $artifact 'operations/windows-service.ps1'
    $sourceOperations = Join-Path $env:GITHUB_WORKSPACE 'deployment/windows-service.ps1'
    $operationText = (Get-Content -LiteralPath $operations -Raw).Replace("`r`n","`n")
    $sourceText = (Get-Content -LiteralPath $sourceOperations -Raw).Replace("`r`n","`n")
    if ($operationText -cne $sourceText) { throw 'Packaged SCM operations do not match the reviewed checkout script.' }
    $configurationPath = Join-Path $home 'config/production.json'
    if (-not (Test-Path -LiteralPath $configurationPath -PathType Leaf)) { throw 'Fixture production configuration is missing.' }
    $configuration = Get-Content -LiteralPath $configurationPath -Raw | ConvertFrom-Json
    if ($configuration.role -cne $Role -or $configuration.windowsServiceName -cne $expectedService) { throw 'Fixture configuration does not match the requested role/service.' }

    $service = Get-CimInstance Win32_Service -Filter "Name='$expectedService'" -ErrorAction SilentlyContinue
    $serviceDirectory = if ($Role -eq 'server') { 'server' } else { 'worker' }
    $assembly = if ($Role -eq 'server') { 'Hispania.Server' } else { 'Hispania.WorkerAgent' }
    $executable = [IO.Path]::GetFullPath((Join-Path $home "app/$serviceDirectory/$assembly.exe"))
    $expectedPath = '"' + $executable + '" --home "' + $home + '"'
    $processId = 0

    if ($Action -eq 'install') {
        $stage = 'install-ownership-check'
        if ($service) { throw 'Unique fixture service already exists; install refused.' }
        if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) { throw 'Published fixture apphost is missing.' }
    } elseif ($Action -notin @('reclaim') -and $Action -ne 'retained-secret') {
        $stage = 'service-ownership-check'
        if (-not $service -or $service.PathName -cne $expectedPath -or $service.StartName -cne 'NT AUTHORITY\LocalService') { throw 'SCM registration does not belong to this exact fixture; operation refused.' }
        if ($Action -in @('start','stop','restart','uninstall','crash-test') -and $service.State -eq 'Running') {
            if ([uint32]$service.ProcessId -eq 0) { throw 'Running fixture service has no valid process ID.' }
            $process = Get-CimInstance Win32_Process -Filter "ProcessId=$([uint32]$service.ProcessId)" -ErrorAction SilentlyContinue
            if (-not $process -or [IO.Path]::GetFullPath([string]$process.ExecutablePath) -cne $executable) { throw 'Running service process image does not match the fixture apphost.' }
        }
    }

    if ($Action -eq 'crash-test') {
        $stage = 'controlled-crash-ownership-check'
        if (-not $service -or $service.State -ne 'Running' -or [uint32]$service.ProcessId -eq 0) { throw 'Controlled failure requires the running owned fixture service.' }
        $processId = [uint32]$service.ProcessId
        $again = Get-CimInstance Win32_Service -Filter "Name='$expectedService'" -ErrorAction SilentlyContinue
        $againProcess = Get-CimInstance Win32_Process -Filter "ProcessId=$processId" -ErrorAction SilentlyContinue
        if (-not $again -or $again.State -ne 'Running' -or [uint32]$again.ProcessId -ne $processId -or $again.PathName -cne $expectedPath -or $again.StartName -cne 'NT AUTHORITY\LocalService' -or -not $againProcess -or [IO.Path]::GetFullPath([string]$againProcess.ExecutablePath) -cne $executable) { throw 'SCM or process ownership changed; controlled failure aborted.' }
        $stage = 'controlled-process-termination'
        $nativeError = 0
        if (-not [HispaniaHostedScmNative]::TerminateIfImageMatches($processId,$executable,[ref]$nativeError)) { throw [ComponentModel.Win32Exception]::new($nativeError,'Verified fixture termination failed.') }
        Write-Output ('Controlled fixture process termination requested at {0:o}; pid={1}.' -f [DateTimeOffset]::UtcNow,$processId)
    } elseif ($Action -eq 'retained-secret') {
        $stage = 'retained-secret-check'
        $secret = Join-Path $home 'secrets/NATIVE_CANARY.secret'
        if (-not (Test-Path -LiteralPath $secret -PathType Leaf)) { throw 'Fixture secret was not retained.' }
        $acl = Get-Acl -LiteralPath $secret
        if (-not $acl.AreAccessRulesProtected -or $acl.GetOwner([Security.Principal.SecurityIdentifier]).Value -ne 'S-1-5-32-544') { throw 'Retained fixture secret ownership/ACL is unexpected.' }
    } elseif ($Action -eq 'reclaim') {
        $stage = 'fixture-acl-reclaim'
        if (-not (Test-Path -LiteralPath (Join-Path $home 'secrets') -PathType Container)) { throw 'Fixture secret directory is missing.' }
        & icacls.exe (Join-Path $home 'secrets') /reset /T /Q | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'Fixture-only secret ACL reclaim failed.' }
    } else {
        $stage = 'fixed-scm-operation'
        & $operations -Action $Action -InstallationHome $home -Role $Role -ServiceName $expectedService
    }
    exit 0
} catch {
    Write-Error ('Restricted SCM helper rejected or failed the operation; operation={0}; stage={1}.' -f $Action,$stage) -ErrorAction Continue
    exit 1
}
