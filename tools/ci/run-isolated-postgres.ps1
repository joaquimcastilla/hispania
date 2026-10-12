[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateScript({ Test-Path -LiteralPath $_ -PathType Container })][string]$BinDirectory
)
$ErrorActionPreference = 'Stop'
if (-not $IsWindows) { throw 'This helper requires the Windows executables from a verified PostgreSQL 16 installation.' }
$required = @('initdb.exe','pg_ctl.exe','postgres.exe')
foreach ($name in $required) {
    if (-not (Test-Path -LiteralPath (Join-Path $BinDirectory $name) -PathType Leaf)) { throw "PostgreSQL executable missing from verified bin directory: $name" }
}
$initdb = Join-Path $BinDirectory 'initdb.exe'
$pgctl = Join-Path $BinDirectory 'pg_ctl.exe'
$postgres = Join-Path $BinDirectory 'postgres.exe'
$version = (& $postgres --version 2>&1 | Out-String).Trim()
if ($version -notmatch '^postgres \(PostgreSQL\) 16(?:\.|$)') { throw "PostgreSQL 16 is required to match the repository's live integration gate; found: $version" }
$repo = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$runRoot = Join-Path ([IO.Path]::GetTempPath()) ('hispania-postgres-ci-' + [Guid]::NewGuid().ToString('N'))
if (Test-Path -LiteralPath $runRoot) { throw 'Refusing to reuse an existing temporary PostgreSQL directory.' }
$data = Join-Path $runRoot 'data'
$log = Join-Path $runRoot 'postgres.log'
$passwordFile = Join-Path $runRoot 'initdb-password.txt'
$password = [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(32)).ToLowerInvariant()
$listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
$listener.Start(); $port = ([Net.IPEndPoint]$listener.LocalEndpoint).Port; $listener.Stop()
$started = $false
New-Item -ItemType Directory -Path $runRoot | Out-Null
try {
    $acl = [Security.AccessControl.DirectorySecurity]::new()
    $acl.SetAccessRuleProtection($true, $false)
    $current = [Security.Principal.WindowsIdentity]::GetCurrent().User
    $acl.SetOwner($current)
    $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($current, 'FullControl', 'ContainerInherit,ObjectInherit', 'None', 'Allow'))
    Set-Acl -LiteralPath $runRoot -AclObject $acl
    [IO.File]::WriteAllText($passwordFile, $password + "`n", [Text.Encoding]::ASCII)
    & $initdb -D $data -U postgres --auth-local=scram-sha-256 --auth-host=scram-sha-256 --pwfile=$passwordFile --encoding=UTF8
    if ($LASTEXITCODE -ne 0) { throw 'Isolated PostgreSQL initialization failed.' }
    Remove-Item -LiteralPath $passwordFile -Force
    $options = "-h 127.0.0.1 -p $port -c listen_addresses=127.0.0.1 -c password_encryption=scram-sha-256"
    & $pgctl -D $data -l $log -o $options -w start
    if ($LASTEXITCODE -ne 0) { throw 'Isolated PostgreSQL did not start.' }
    $started = $true
    $env:HISPANIA_POSTGRES_TEST_CONNECTION = "Host=127.0.0.1;Port=$port;Database=postgres;Username=postgres;Password=$password;Pooling=false;Timeout=5;Command Timeout=30"
    Push-Location $repo
    try {
        & dotnet test tests/library-persistence/Hispania.Library.Persistence.Sqlite.Tests.csproj --filter 'FullyQualifiedName~PostgreSqlProviderTests'
        if ($LASTEXITCODE -ne 0) { throw 'PostgreSQL library persistence tests failed.' }
        & dotnet build apps/server/Hispania.Server.csproj --configuration Debug
        if ($LASTEXITCODE -ne 0) { throw 'Full Server build required by the Phase 17 PostgreSQL test failed.' }
        & dotnet test tests/inventory/Hispania.Inventory.Tests.csproj --filter 'FullyQualifiedName~Phase17PostgreSqlTests'
        if ($LASTEXITCODE -ne 0) { throw 'Phase 17 PostgreSQL integration test failed.' }
    } finally { Pop-Location }
} finally {
    Remove-Item Env:HISPANIA_POSTGRES_TEST_CONNECTION -ErrorAction SilentlyContinue
    if ($started) { & $pgctl -D $data -m fast -w stop; if ($LASTEXITCODE -ne 0) { throw 'Could not stop isolated PostgreSQL cleanly; preserve its temporary directory for inspection.' } }
    if (Test-Path -LiteralPath $runRoot) {
        if ([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($runRoot)) -cne [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')) { throw 'Temporary directory ownership check failed; refusing cleanup.' }
        Remove-Item -LiteralPath $runRoot -Recurse -Force
    }
}
