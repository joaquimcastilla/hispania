[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Artifact,
    [string]$ValidationRoot = (Join-Path (Split-Path -Parent $PSScriptRoot) '.artifacts/platform-only-production-validation')
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$artifactRoot = [IO.Path]::GetFullPath($Artifact)
$validationRoot = [IO.Path]::GetFullPath($ValidationRoot)
if (-not (Test-Path -LiteralPath $artifactRoot -PathType Container)) { throw 'Artifact directory does not exist.' }
if (Test-Path -LiteralPath $validationRoot) { throw 'Validation root must be fresh; previous test data is never overwritten.' }
New-Item -ItemType Directory -Path $validationRoot | Out-Null
$installationHome = Join-Path $validationRoot 'home'
$portListener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
$portListener.Start(); $port = $portListener.LocalEndpoint.Port; $portListener.Stop()
$endpoint = "http://127.0.0.1:$port"
$adminSecret = [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
$password = 'Platform-Production-Probe!234'
$session = $null
$process = $null
$client = [Net.Http.HttpClient]::new()
$client.Timeout = [TimeSpan]::FromSeconds(5)
$client.BaseAddress = [Uri]$endpoint

function Invoke-Deployment([string[]]$Arguments) {
    & dotnet (Join-Path $artifactRoot 'deployment/Hispania.Deployment.dll') @Arguments
    if ($LASTEXITCODE -ne 0) { throw "Deployment command failed: $($Arguments -join ' ') (exit $LASTEXITCODE)." }
}
function Invoke-Host([string[]]$Arguments) {
    & dotnet (Join-Path $installationHome 'app/server/Hispania.Platform.Server.dll') @Arguments
    if ($LASTEXITCODE -ne 0) { throw "Platform host command failed: $($Arguments -join ' ') (exit $LASTEXITCODE)." }
}
function Protect-TestSecret([string]$Path) {
    $acl = Get-Acl -LiteralPath $Path
    $acl.SetAccessRuleProtection($true, $false)
    foreach ($rule in @($acl.Access)) { [void]$acl.RemoveAccessRuleSpecific($rule) }
    $principals = @(
        [Security.Principal.WindowsIdentity]::GetCurrent().User,
        [Security.Principal.SecurityIdentifier]::new([Security.Principal.WellKnownSidType]::LocalSystemSid, $null),
        [Security.Principal.SecurityIdentifier]::new([Security.Principal.WellKnownSidType]::BuiltinAdministratorsSid, $null)
    )
    foreach ($principal in $principals) {
        $rule = [Security.AccessControl.FileSystemAccessRule]::new($principal, [Security.AccessControl.FileSystemRights]::FullControl, [Security.AccessControl.AccessControlType]::Allow)
        [void]$acl.AddAccessRule($rule)
    }
    Set-Acl -LiteralPath $Path -AclObject $acl
}
function Start-InstalledHost {
    $start = [Diagnostics.ProcessStartInfo]::new('dotnet')
    $start.WorkingDirectory = Join-Path $installationHome 'app/server'
    $start.UseShellExecute = $false; $start.RedirectStandardOutput = $true; $start.RedirectStandardError = $true
    $start.ArgumentList.Add((Join-Path $start.WorkingDirectory 'Hispania.Platform.Server.dll'))
    $start.ArgumentList.Add('--home'); $start.ArgumentList.Add($installationHome)
    $script:process = [Diagnostics.Process]::Start($start)
    for ($i = 0; $i -lt 60; $i++) {
        if ($script:process.HasExited) { throw "Production host exited: $($script:process.StandardError.ReadToEnd())" }
        try {
            $response = $client.GetAsync('/readyz').GetAwaiter().GetResult()
            $ready = $response.StatusCode -eq [Net.HttpStatusCode]::OK; $response.Dispose()
            if ($ready) { return }
        } catch { }
        Start-Sleep -Milliseconds 500
    }
    throw 'Installed production host did not become ready within 30 seconds.'
}
function Stop-InstalledHost {
    if ($script:process -and -not $script:process.HasExited) {
        # This validator exercises process restart; graceful shutdown is covered by the local runtime smoke.
        $script:process.Kill($true); [void]$script:process.WaitForExit(10000)
    }
    $script:process = $null
}
function Assert-Login {
    $body = [Net.Http.StringContent]::new((@{ username = 'production-probe'; password = $password } | ConvertTo-Json -Compress), [Text.Encoding]::UTF8, 'application/json')
    $response = $client.PostAsync('/api/identity/login', $body).GetAwaiter().GetResult()
    if ($response.StatusCode -ne [Net.HttpStatusCode]::OK) { throw "Persisted production user could not log in (HTTP $([int]$response.StatusCode))." }
    $script:session = ($response.Content.ReadAsStringAsync().GetAwaiter().GetResult() | ConvertFrom-Json).sessionToken
    if (-not $script:session) { throw 'Login did not return a session token.' }
    $response.Dispose(); $body.Dispose()
}

try {
    Invoke-Deployment @('install', $artifactRoot, $installationHome, 'server')
    $configPath = Join-Path $installationHome 'config/production.json'
    $config = Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json
    $config.endpoint = $endpoint
    $config.bootstrapSecret = 'file:BOOTSTRAP_TOKEN'
    $config.bootstrapUsername = 'production-probe'
    $config.bootstrapPasswordSecret = 'file:BOOTSTRAP_PASSWORD'
    $config | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $configPath -Encoding utf8NoBOM
    Set-Content -LiteralPath (Join-Path $installationHome 'secrets/BOOTSTRAP_TOKEN.secret') -Value $adminSecret -NoNewline
    Set-Content -LiteralPath (Join-Path $installationHome 'secrets/BOOTSTRAP_PASSWORD.secret') -Value $password -NoNewline
    Protect-TestSecret (Join-Path $installationHome 'secrets/BOOTSTRAP_TOKEN.secret')
    Protect-TestSecret (Join-Path $installationHome 'secrets/BOOTSTRAP_PASSWORD.secret')

    Invoke-Host @('--home', $installationHome, '--check')
    Invoke-Host @('--home', $installationHome, '--bootstrap')
    $config = Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json
    $config.bootstrapSecret = $null; $config.bootstrapPasswordSecret = $null; $config.bootstrapUsername = $null
    $config | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $configPath -Encoding utf8NoBOM
    Remove-Item -LiteralPath (Join-Path $installationHome 'secrets/BOOTSTRAP_TOKEN.secret'), (Join-Path $installationHome 'secrets/BOOTSTRAP_PASSWORD.secret') -Force
    Start-InstalledHost
    Assert-Login
    foreach ($route in @('/healthz', '/api/health/live', '/api/health/ready', '/api/platform/jobs/health', '/api/platform/modules')) {
        $request = [Net.Http.HttpRequestMessage]::new([Net.Http.HttpMethod]::Get, $route)
        $request.Headers.Authorization = [Net.Http.Headers.AuthenticationHeaderValue]::new('Session', $session)
        $response = $client.SendAsync($request).GetAwaiter().GetResult()
        if ($route -in @('/healthz', '/api/health/live', '/api/health/ready')) {
            if ($response.StatusCode -ne [Net.HttpStatusCode]::OK) { throw "$route returned HTTP $([int]$response.StatusCode)." }
        } elseif ($response.StatusCode -ne [Net.HttpStatusCode]::OK) {
            throw "$route returned unexpected HTTP $([int]$response.StatusCode)."
        }
        $response.Dispose(); $request.Dispose()
    }
    $unsupportedJob = [Net.Http.HttpRequestMessage]::new([Net.Http.HttpMethod]::Post, '/api/platform/jobs')
    $unsupportedJob.Headers.Authorization = [Net.Http.Headers.AuthenticationHeaderValue]::new('Session', $session)
    $unsupportedJob.Content = [Net.Http.StringContent]::new('{"jobType":"platform.test.hash","payload":{"text":"not registered in production","algorithm":"sha256"}}', [Text.Encoding]::UTF8, 'application/json')
    $unsupportedResponse = $client.SendAsync($unsupportedJob).GetAwaiter().GetResult()
    if ($unsupportedResponse.StatusCode -ne [Net.HttpStatusCode]::BadRequest) { throw "Production Core unexpectedly accepted an unprovided Job handler (HTTP $([int]$unsupportedResponse.StatusCode))." }
    $unsupportedResponse.Dispose(); $unsupportedJob.Dispose()
    Stop-InstalledHost

    $bundle = Invoke-Deployment @('upgrade', $artifactRoot, $installationHome)
    $bundlePath = ($bundle | Select-String -Pattern '^[A-Za-z]:\\').ToString().Trim()
    if (-not $bundlePath -or -not (Test-Path -LiteralPath $bundlePath)) { throw 'Upgrade did not return a verifiable backup bundle path.' }
    Invoke-Deployment @('rollback-restore', $installationHome, $bundlePath)
    Start-InstalledHost
    Assert-Login
    Stop-InstalledHost

    [pscustomobject]@{
        Result = 'PASS'
        Installation = 'Production Deployment CLI install and --check passed on a fresh isolated HOME.'
        Bootstrap = 'Production-only bootstrap created an administrator from protected file secrets.'
        Persistence = 'Authenticated login succeeded before and after backup-backed upgrade/rollback restart.'
        Upgrade = 'Same-RID Platform-only artifact staged; Phase 17 SQLite backup created and verified.'
        Rollback = 'Old artifact and restored clean state were switched back; persisted identity remained usable.'
        Endpoint = $endpoint
        Home = $installationHome
        BackupBundle = $bundlePath
        Shutdown = 'Process termination used for this production lifecycle pass; graceful stop is verified by validate-platform-only.ps1.'
    } | Format-List
}
finally {
    Stop-InstalledHost
    $client.Dispose()
}
