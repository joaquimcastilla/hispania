[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('install','start','stop','restart','status','uninstall')][string]$Action,
    [Parameter(Mandatory)][Alias('Home')][string]$InstallationHome,
    [ValidateSet('server','worker')][string]$Role='server',
    [string]$Account='NT AUTHORITY\LocalService'
)
$ErrorActionPreference='Stop'
if (-not $IsWindows) { throw 'Windows SCM operation requires Windows.' }
if (-not [IO.Path]::IsPathFullyQualified($InstallationHome) -or $InstallationHome.Contains('"') -or [regex]::IsMatch($InstallationHome, '[\x00-\x1F\x7F]')) { throw 'Provide an absolute installation root without quote or control characters.' }
$InstallationHome=[IO.Path]::GetFullPath($InstallationHome)
$name=if ($Role -eq 'server') { 'HispaniaServer' } else { 'HispaniaWorker' }
$directory=if ($Role -eq 'server') { 'server' } else { 'worker' }
$assembly=if ($Role -eq 'server') { 'Hispania.Server' } else { 'Hispania.WorkerAgent' }
function Invoke-Sc([Parameter(ValueFromRemainingArguments=$true)][string[]]$Arguments) { & sc.exe @Arguments; if ($LASTEXITCODE -ne 0) { throw 'SCM operation failed; inspect service identity, privileges and existing registration.' } }
switch ($Action) {
    install {
        if ($Account -ne 'NT AUTHORITY\LocalService') { throw 'This script supports LocalService; provision custom non-admin accounts and their logon credentials through SCM separately.' }
        if (Get-Service -Name $name -ErrorAction SilentlyContinue) { throw 'Service already registered; existing installation was preserved.' }
        $executable=Join-Path $InstallationHome "app/$directory/$assembly.exe"
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
        New-ItemProperty -Path ("HKLM:\SYSTEM\CurrentControlSet\Services\"+$name) -Name Environment -PropertyType MultiString -Value @("DOTNET_ROOT_X64=$runtimeRoot","DOTNET_ENVIRONMENT=Production") -Force | Out-Null
        Invoke-Sc @('failure',$name,'reset=','86400','actions=','restart/10000/restart/30000/none/0')
    }
    start { Start-Service -Name $name }
    stop { Stop-Service -Name $name; (Get-Service $name).WaitForStatus('Stopped',[TimeSpan]::FromSeconds(40)) }
    restart { Restart-Service -Name $name }
    status { Get-Service -Name $name }
    uninstall { Stop-Service -Name $name; (Get-Service $name).WaitForStatus('Stopped',[TimeSpan]::FromSeconds(40)); Invoke-Sc @('delete',$name) }
}
