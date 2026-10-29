param([switch]$PostgreSqlOnly)
$ErrorActionPreference='Stop'
$trustedFeatureBranches=@('refs/heads/feature/phase-21-security-operations-hardening','refs/heads/feature/phase-22-retention-reconciliation-capacity','refs/heads/feature/phase-23-e2e-compatibility-upgrades','refs/heads/feature/slice10-core-state-backup')
$candidateBranch='refs/heads/codex/hispania-0.7.0-step2-1-production-readiness'
$slice10CandidateBranch='refs/heads/feature/slice10-core-state-backup'
$slice10CandidateBase='fcfaee1a05726ae3702f2b815c86fad7c36e1ec7'
$candidateDispatch=$env:GITHUB_REF -ceq $candidateBranch
if ($env:GITHUB_ACTIONS -cne 'true' -or $env:GITHUB_REPOSITORY -cne 'joaquimcastilla/hispania' -or $env:GITHUB_EVENT_NAME -cne 'workflow_dispatch') { throw 'Only manual dispatch from the official repository is supported.' }
if ($candidateDispatch) {
    if ($env:GITHUB_ACTOR -cne 'joaquimcastilla' -or $env:HISPANIA_TRIGGERING_ACTOR -cne 'joaquimcastilla' -or $env:GITHUB_SHA -notmatch '^[0-9a-f]{40}$' -or $env:HISPANIA_CANDIDATE_SHA -cne $env:GITHUB_SHA) { throw 'Candidate validation requires the authorized actor and exact full commit SHA.' }
} elseif ($env:GITHUB_REF -ceq $slice10CandidateBranch) {
    if ($env:GITHUB_ACTOR -cne 'joaquimcastilla' -or $env:HISPANIA_TRIGGERING_ACTOR -cne 'joaquimcastilla' -or $env:GITHUB_SHA -notmatch '^[0-9a-f]{40}$' -or $env:HISPANIA_CANDIDATE_SHA -notmatch '^[0-9a-f]{40}$' -or $env:HISPANIA_CANDIDATE_SHA -cne $env:GITHUB_SHA) { throw 'Slice 10 candidate validation requires the authorized actor and exact full commit SHA.' }
    $checkoutSha = (& git rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0 -or $checkoutSha -cne $env:HISPANIA_CANDIDATE_SHA) { throw 'Slice 10 checkout does not match the dispatched candidate SHA.' }
    $remoteOutput = @(& git ls-remote 'https://github.com/joaquimcastilla/hispania.git' $slice10CandidateBranch)
    if ($LASTEXITCODE -ne 0 -or $remoteOutput.Count -ne 1) { throw 'Slice 10 candidate branch HEAD could not be verified.' }
    $remoteFields = ([string]$remoteOutput[0] -split '\s+')
    if ($remoteFields.Count -ne 2 -or $remoteFields[1] -cne $slice10CandidateBranch -or $remoteFields[0] -cne $env:HISPANIA_CANDIDATE_SHA) { throw 'Slice 10 candidate SHA is not the current authorized branch HEAD.' }
    & git merge-base --is-ancestor $slice10CandidateBase $env:HISPANIA_CANDIDATE_SHA
    if ($LASTEXITCODE -ne 0) { throw 'Slice 10 candidate does not descend from the accredited source commit.' }
} elseif ($env:GITHUB_REF -cnotin $trustedFeatureBranches) { throw 'Only the explicitly allowlisted trusted branches may use this preflight.' }
if ($IsLinux) {
    # Existing shared /tmp fixture parents can belong to other operators. Do not
    # change their permissions; use the job's own runner-managed temporary root.
    $env:TMPDIR=$env:RUNNER_TEMP
    "TMPDIR=$env:RUNNER_TEMP" | Add-Content -LiteralPath $env:GITHUB_ENV
    $configuration = Get-Content -LiteralPath /etc/hispania-phase21/postgres.json -Raw | ConvertFrom-Json
    foreach ($name in @('HISPANIA_POSTGRES_TEST_CONNECTION','HISPANIA_PHASE18_POSTGRES_CONNECTION')) {
        $value = [string]$configuration.$name
        if (-not $value -or $value.Contains("`n") -or $value.Contains("`r")) { throw 'Missing or malformed authenticated CI PostgreSQL configuration.' }
        $password = [regex]::Match($value,'(?:^|;)Password=([^;]+)').Groups[1].Value
        if (-not $password) { throw 'CI PostgreSQL must use password authentication.' }
        Write-Output "::add-mask::$password"
        Write-Output "::add-mask::$value"
        [Environment]::SetEnvironmentVariable($name,$value)
        "$name=$value" | Add-Content -LiteralPath $env:GITHUB_ENV
    }
    $env:PGHOST='127.0.0.1'; $env:PGPORT='55421'; $env:PGUSER='hispania_ci'; $env:PGDATABASE='hispania_tests'; $env:PGPASSWORD=$password
    try {
        & psql --no-psqlrc --tuples-only --no-align --command='SHOW server_version'
        if ($LASTEXITCODE -ne 0) { throw 'Authenticated PostgreSQL connection failed.' }
    } finally { Remove-Item Env:PGPASSWORD }
}
if ($PostgreSqlOnly) { return }
foreach ($tool in @('git','pwsh','dotnet','node','npm','ffmpeg','ffprobe','python')) { Get-Command $tool -ErrorAction Stop | Select-Object Name,Source }
foreach ($tool in @('dotnet','node','npm','python')) { & $tool --version; if ($LASTEXITCODE -ne 0) { throw "$tool unavailable." } }
foreach ($tool in @('ffmpeg','ffprobe')) { & $tool -version | Select-Object -First 1; if ($LASTEXITCODE -ne 0) { throw "$tool unavailable." } }
if ($IsLinux) {
    if ((& id -un).Trim() -ne 'gha-hispania') { throw 'Linux runner must retain its non-root identity.' }
    foreach ($tool in @('curl','jq','tar','unzip','psql','pg_dump','pg_restore','prlimit')) { Get-Command $tool -ErrorAction Stop | Out-Null }
    & pg_dump --version; & pg_restore --version
} elseif ($IsWindows) {
    if ([Security.Principal.WindowsIdentity]::GetCurrent().User.Value -ne 'S-1-5-20') { throw 'Windows runner must retain NetworkService identity.' }
}
if ($env:HISPANIA_PHASE21_NATIVE_HELPER) {
    if ($IsWindows) {
        # Protected broker snapshots must implement this exact checkout, irrespective of CRLF.
        foreach ($pair in @(@{source='./tools/ci/windows-native-service-helper.ps1';installed=$env:HISPANIA_PHASE21_NATIVE_HELPER},@{source='./deployment/windows-service.ps1';installed=(Join-Path (Split-Path $env:HISPANIA_PHASE21_NATIVE_HELPER) 'windows-service.ps1')})) {
            $expected=(Get-Content -LiteralPath $pair.source -Raw).Replace("`r`n","`n")
            $actual=(Get-Content -LiteralPath $pair.installed -Raw).Replace("`r`n","`n")
            if ($actual -cne $expected) { throw 'Protected Windows helper does not match the exact source checkout.' }
        }
        Write-Output 'Protected Windows broker and SCM helper match the exact source checkout.'
    }
    $probe=[Diagnostics.ProcessStartInfo]::new()
    $probe.UseShellExecute=$false; $probe.RedirectStandardOutput=$true; $probe.RedirectStandardError=$true
    $probe.FileName=if ($IsWindows) { 'pwsh' } else { 'sudo' }
    $arguments=if ($IsWindows) { @('-NoProfile','-File',$env:HISPANIA_PHASE21_NATIVE_HELPER,'-Action','status','-InstallationHome','C:\Windows','-Role','server') } else { @('-n',$env:HISPANIA_PHASE21_NATIVE_HELPER,'status','/tmp','server') }
    foreach($argument in $arguments) { $probe.ArgumentList.Add($argument) }
    $process=[Diagnostics.Process]::Start($probe)
    try {
        $stdout=$process.StandardOutput.ReadToEndAsync(); $stderr=$process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit(20000)) { $process.Kill($true); throw 'Native helper transport deadline exceeded.' }
        $output=$stdout.GetAwaiter().GetResult()+$stderr.GetAwaiter().GetResult()
        $expected=if ($IsWindows) { 'Restricted SCM helper rejected or failed the operation' } else { 'Restricted native CI service operation failed' }
        if ($process.ExitCode -eq 0 -or -not $output.Contains($expected,[StringComparison]::Ordinal)) { throw 'Native helper must be reachable by the runner and reject outside-root requests.' }
        Write-Output 'Native helper outside-root negative passed under the actual runner identity.'
    } finally { $process.Dispose() }
}
