param([switch]$PostgreSqlOnly)
$ErrorActionPreference='Stop'
if ($env:GITHUB_ACTIONS -ne 'true' -or $env:GITHUB_REPOSITORY -ne 'joaquimcastilla/hispania' -or $env:GITHUB_REF -ne 'refs/heads/feature/phase-21-security-operations-hardening') { throw 'Only explicitly dispatched trusted Phase21 CI is supported.' }
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
