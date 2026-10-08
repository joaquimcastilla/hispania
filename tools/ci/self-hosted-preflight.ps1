param([switch]$PostgreSqlOnly)
$ErrorActionPreference='Stop'
if ($env:GITHUB_ACTIONS -ne 'true' -or $env:GITHUB_REPOSITORY -ne 'joaquimcastilla/hispania' -or $env:GITHUB_REF -ne 'refs/heads/feature/phase-21-security-operations-hardening') { throw 'Only explicitly dispatched trusted Phase21 CI is supported.' }
if ($IsLinux) {
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
