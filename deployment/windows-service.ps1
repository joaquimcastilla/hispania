[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('install','start','stop','restart','status','uninstall')][string]$Action,
    [Parameter(Mandatory)][Alias('Home')][string]$InstallationHome,
    [ValidateSet('server','worker')][string]$Role='server',
    [string]$ServiceName,
    [string]$Account='NT AUTHORITY\LocalService'
)
$ErrorActionPreference='Stop'
if (-not $IsWindows) { throw 'Windows SCM operation requires Windows.' }
if (-not [IO.Path]::IsPathFullyQualified($InstallationHome) -or $InstallationHome.Contains('"') -or [regex]::IsMatch($InstallationHome, '[\x00-\x1F\x7F]')) { throw 'Provide an absolute installation root without quote or control characters.' }
$InstallationHome=[IO.Path]::GetFullPath($InstallationHome)
$configPath=Join-Path $InstallationHome 'config/production.json'
if (-not (Test-Path -LiteralPath $configPath -PathType Leaf)) { throw 'Production configuration is missing.' }
try { $configuration=Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json } catch { throw 'Production configuration is invalid.' }
if ($configuration.role -cne $Role) { throw 'Configured role does not match the requested service role.' }
$configuredName=$configuration.windowsServiceName
$defaultName=if ($Role -eq 'server') { 'HispaniaServer' } else { 'HispaniaWorker' }
$name=if ($ServiceName) { $ServiceName } elseif ($configuredName) { [string]$configuredName } else { $defaultName }
if ($configuredName -and $name -cne $configuredName) { throw 'Requested service name does not match production configuration.' }
if ($name -cnotmatch '^[A-Za-z0-9][A-Za-z0-9_.-]{0,79}$') { throw 'Invalid Windows SCM service name.' }
if ($name -match '^Hispania-Test-([0-9a-f]{32})-(Server|Worker)$') {
    $fixtureLeaf='hispania-phase18 isolated ñ '+$Matches[1]
    if ([IO.Path]::GetFileName($InstallationHome) -cne $fixtureLeaf -or $Matches[2] -cne $(if($Role -eq 'server'){'Server'}else{'Worker'})) { throw 'Test service name does not belong to this role and fixture run.' }
}
$directory=if ($Role -eq 'server') { 'server' } else { 'worker' }
$assembly=if ($Role -eq 'server') { 'Hispania.Server' } else { 'Hispania.WorkerAgent' }
$executable=Join-Path $InstallationHome "app/$directory/$assembly.exe"
$expectedPath='"'+[IO.Path]::GetFullPath($executable)+'" --home "'+$InstallationHome+'"'
function Assert-OwnedService {
    $registered=Get-CimInstance Win32_Service -Filter "Name='$name'" -ErrorAction SilentlyContinue
    if (-not $registered) { throw 'Service is not registered; no lifecycle operation was performed.' }
    if ($registered.PathName -cne $expectedPath -or $registered.StartName -cne $Account) { throw 'Registered service identity does not match this installation; refusing lifecycle operation.' }
}
function Invoke-Sc([Parameter(ValueFromRemainingArguments=$true)][string[]]$Arguments) { & sc.exe @Arguments; if ($LASTEXITCODE -ne 0) { throw 'SCM operation failed; inspect service identity, privileges and existing registration.' } }
switch ($Action) {
    install {
        if ($Account -ne 'NT AUTHORITY\LocalService') { throw 'This script supports LocalService; provision custom non-admin accounts and their logon credentials through SCM separately.' }
        if (Get-Service -Name $name -ErrorAction SilentlyContinue) { throw 'Service already registered; existing installation was preserved.' }
        if (-not (Test-Path -LiteralPath $executable)) { throw 'Missing native apphost; publish win-x64 first.' }
        & icacls.exe $InstallationHome /grant '*S-1-5-19:RX' /Q | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'Installation root read/traverse permissions could not be applied.' }
        foreach ($readOnly in @('app','config')) {
            & icacls.exe (Join-Path $InstallationHome $readOnly) /grant '*S-1-5-19:(OI)(CI)RX' /T /Q | Out-Null
            if ($LASTEXITCODE -ne 0) { throw 'Read permissions could not be applied.' }
        }
        foreach ($mutable in @('state','logs','backups','run')) {
            & icacls.exe (Join-Path $InstallationHome $mutable) /grant '*S-1-5-19:(OI)(CI)M' /T /Q | Out-Null
            if ($LASTEXITCODE -ne 0) { throw 'Mutable permissions could not be applied.' }
        }
        $secrets=Join-Path $InstallationHome 'secrets'
        foreach ($privateItem in @(Get-Item -LiteralPath $secrets)+@(Get-ChildItem -LiteralPath $secrets -Recurse)) {
            # Runner-created secrets have a protected owner-only ACL. Transfer ownership
            # before reading/changing that ACL; no secret contents need to be opened.
            & takeown.exe /F $privateItem.FullName /A | Out-Null
            if ($LASTEXITCODE -ne 0) { throw 'Private secret ownership could not be transferred to Administrators.' }
            $acl=Get-Acl -LiteralPath $privateItem.FullName
            $acl.SetAccessRuleProtection($true,$false)
            foreach ($rule in @($acl.Access)) { $acl.RemoveAccessRuleSpecific($rule) }
            $acl.SetOwner([Security.Principal.SecurityIdentifier]::new('S-1-5-32-544'))
            foreach ($sid in @('S-1-5-19','S-1-5-18','S-1-5-32-544')) {
                $rights=if ($sid -eq 'S-1-5-19') { 'ReadAndExecute' } else { 'FullControl' }
                $rule=[Security.AccessControl.FileSystemAccessRule]::new([Security.Principal.SecurityIdentifier]::new($sid),$rights,'Allow')
                $acl.AddAccessRule($rule)
            }
            Set-Acl -LiteralPath $privateItem.FullName -AclObject $acl
        }
        $lock=Join-Path $InstallationHome 'state.maintenance.lock'
        if (-not (Test-Path -LiteralPath $lock)) { New-Item -ItemType File -Path $lock | Out-Null }
        & icacls.exe $lock /grant '*S-1-5-19:M' /Q | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'Maintenance lock permissions could not be applied.' }
        Invoke-Sc @('create',$name,'binPath=',('"'+$executable+'" --home "'+$InstallationHome+'"'),'start=','auto','obj=',$Account)
        $runtimeRoot=Split-Path (Get-Command dotnet -ErrorAction Stop).Source -Parent
        # SCM may retain the PATH from boot, before native prerequisites were installed.
        # Use only the current machine PATH, never the administrative user's PATH.
        $machinePath=[Environment]::GetEnvironmentVariable('Path','Machine')
        New-ItemProperty -Path ("HKLM:\SYSTEM\CurrentControlSet\Services\"+$name) -Name Environment -PropertyType MultiString -Value @("DOTNET_ROOT_X64=$runtimeRoot","DOTNET_ENVIRONMENT=Production","PATH=$machinePath") -Force | Out-Null
        Invoke-Sc @('failure',$name,'reset=','86400','actions=','restart/10000/restart/30000/none/0')
    }
    start { Assert-OwnedService; Start-Service -Name $name }
    stop { Assert-OwnedService; Stop-Service -Name $name; (Get-Service $name).WaitForStatus('Stopped',[TimeSpan]::FromSeconds(40)) }
    restart { Assert-OwnedService; Restart-Service -Name $name }
    status { Assert-OwnedService; Get-Service -Name $name }
    uninstall { Assert-OwnedService; Stop-Service -Name $name; (Get-Service $name).WaitForStatus('Stopped',[TimeSpan]::FromSeconds(40)); Assert-OwnedService; Invoke-Sc @('delete',$name) }
}
