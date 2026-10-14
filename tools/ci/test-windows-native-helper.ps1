param([Parameter(Mandatory)][string]$Helper)
$ErrorActionPreference='Stop'
if([Security.Principal.WindowsIdentity]::GetCurrent().User.Value -ne 'S-1-5-20') {throw 'Regression must use the actual NetworkService caller identity'}
$root='C:\ProgramData\HispaniaPhase18CI'
$fixture=Join-Path $root ('hispania-phase18 isolated ñ '+[Guid]::NewGuid().ToString('N'))
$runId=[IO.Path]::GetFileName($fixture).Substring('hispania-phase18 isolated ñ '.Length)
$serviceName='Hispania-Test-'+$runId+'-Server'
function Require-Rejection([string]$Action,[string]$FixtureHome,[string]$Stage) {
    $message=$null
    try { & $Helper -Action $Action -InstallationHome $FixtureHome -Role server -ServiceName $serviceName } catch {$message=$_.Exception.Message}
    if(-not $message -or -not $message.Contains("stage=$Stage;",[StringComparison]::Ordinal)) {throw "Expected restricted rejection at $Stage"}
}
try {
    # Disconnect before sending a frame, then prove that the same listener survives.
    $pipe=[IO.Pipes.NamedPipeClientStream]::new('.','HispaniaPhase21NativeServices',[IO.Pipes.PipeDirection]::InOut)
    try {$pipe.Connect(5000)} finally {$pipe.Dispose()}
    Require-Rejection status 'C:\Windows' 'fixture-validation'
    New-Item -ItemType Directory -Path (Join-Path $fixture 'secrets') | Out-Null
    $secret=Join-Path $fixture 'secrets\metadata-only.txt'
    [IO.File]::WriteAllText($secret,'Non-secret metadata regression fixture')
    $acl=[Security.AccessControl.FileSecurity]::new()
    $acl.SetAccessRuleProtection($true,$false)
    $acl.SetOwner([Security.Principal.SecurityIdentifier]::new('S-1-5-20'))
    $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new([Security.Principal.SecurityIdentifier]::new('S-1-5-20'),'FullControl','Allow'))
    Set-Acl -LiteralPath $secret -AclObject $acl
    $before=(Get-Acl $secret).Sddl
    $actual=Get-Acl -LiteralPath $secret
    if(-not $actual.AreAccessRulesProtected -or @($actual.Access).Count -ne 1 -or $actual.Access[0].IdentityReference.Translate([Security.Principal.SecurityIdentifier]).Value -ne 'S-1-5-20') {throw 'Private-file regression requires NetworkService-only access'}
    # No test service is installed yet: reaching SCM proves metadata validation passed.
    Require-Rejection status $fixture 'scm-helper'
    Require-Rejection 'crash-test' $fixture 'crash-service-ownership'
    if((Get-Acl $secret).Sddl -cne $before) {throw 'Metadata query changed private ACL'}
    New-Item -ItemType HardLink -Path (Join-Path $fixture 'linked.txt') -Value $secret | Out-Null
    Require-Rejection reclaim $fixture 'metadata-query'
    if((Get-Acl $secret).Sddl -cne $before) {throw 'Rejected hardlink changed private ACL'}
    'PASS: disconnected client survived; outside root rejected; owner-only metadata traversed without content access or ACL change; hardlink rejected.'
} finally {
    if(Test-Path $fixture) {
        if([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($fixture)) -cne $root) {throw 'Cleanup outside dedicated root'}
        Remove-Item -LiteralPath $fixture -Recurse -Force
    }
}
