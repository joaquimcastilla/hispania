param(
    [string]$OutputRoot = (Join-Path (Split-Path -Parent $PSScriptRoot) '.artifacts/platform-only-validation')
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$serverProject = Join-Path $repoRoot 'apps/server/Hispania.Server.csproj'
$runOutputRoot = Join-Path $OutputRoot ('run-' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss-fff'))
$winOutput = Join-Path $runOutputRoot 'win-x64'
$armOutput = Join-Path $runOutputRoot 'linux-arm64'
$forbidden = 'Hispania\.(Media|Inventory|Library|Playback|Transcoding|Metadata|FFmpeg)|WindowsServices|ServiceController'

function Invoke-Dotnet([string[]]$Arguments) {
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet $($Arguments -join ' ') failed with exit code $LASTEXITCODE" }
}

function Assert-PlatformArtifact([string]$Path, [switch]$Arm64) {
    $files = @(Get-ChildItem -LiteralPath $Path -Recurse -File)
    $badFiles = @($files | Where-Object Name -Match $forbidden)
    if ($badFiles.Count -gt 0) { throw "Domain/Windows-only artifact files found: $($badFiles.Name -join ', ')" }
    foreach ($required in @('Hispania.Platform.Server.dll', 'Hispania.Platform.Server.deps.json', 'module-host/Hispania.ModuleHost.dll')) {
        if (-not (Test-Path -LiteralPath (Join-Path $Path $required) -PathType Leaf)) { throw "Required Platform-only artifact file missing: $required" }
    }
    $deps = Join-Path $Path 'Hispania.Platform.Server.deps.json'
    if (-not (Test-Path -LiteralPath $deps)) { throw "Missing dependency manifest: $deps" }
    if (Select-String -LiteralPath $deps -Pattern $forbidden -Quiet) { throw 'Domain/Windows-only dependencies found in the dependency manifest.' }
    if ($Arm64) {
        $native = Join-Path $Path 'libe_sqlite3.so'
        if (-not (Test-Path -LiteralPath $native)) { throw 'ARM64 SQLite native provider is missing.' }
        $bytes = [IO.File]::ReadAllBytes($native)
        if ($bytes.Length -lt 20 -or $bytes[0] -ne 0x7f -or $bytes[1] -ne 0x45 -or $bytes[2] -ne 0x4c -or $bytes[3] -ne 0x46 -or [BitConverter]::ToUInt16($bytes, 18) -ne 183) {
            throw 'libe_sqlite3.so is not an ELF AArch64 binary.'
        }
    }
    [pscustomobject]@{ Path = $Path; Files = $files.Count; Bytes = ($files | Measure-Object Length -Sum).Sum }
}

function New-JsonContent($Value) {
    $json = ConvertTo-Json -InputObject $Value -Depth 10 -Compress
    return [Net.Http.StringContent]::new($json, [Text.Encoding]::UTF8, 'application/json')
}

New-Item -ItemType Directory -Force -Path $runOutputRoot | Out-Null
Invoke-Dotnet @('publish', $serverProject, '-c', 'Release', '-p:PlatformOnly=true', '-p:ProductionPublication=true', '-p:UseAppHost=false', '-o', $winOutput)
Invoke-Dotnet @('publish', $serverProject, '-c', 'Release', '-p:PlatformOnly=true', '-p:ProductionPublication=true', '-p:UseAppHost=false', '-r', 'linux-arm64', '--self-contained', 'false', '-o', $armOutput)
$winInventory = Assert-PlatformArtifact $winOutput
$armInventory = Assert-PlatformArtifact $armOutput -Arm64

# Preserve a separate full distribution build as the compatibility compile gate.
Invoke-Dotnet @('build', $serverProject, '-c', 'Release', '-p:ProductionPublication=true', '-p:UseAppHost=false')
Invoke-Dotnet @('build', $serverProject, '-c', 'Debug', '-p:PlatformOnly=true', '-p:UseAppHost=false')

# Run the development Platform-only composition to exercise its test handler and graceful test shutdown hook.
$listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
$listener.Start(); $port = $listener.LocalEndpoint.Port; $listener.Stop()
$dataRoot = Join-Path ([IO.Path]::GetTempPath()) ('hispania-platform-only-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $dataRoot | Out-Null
$adminToken = [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
$bootstrapToken = [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
$shutdownToken = [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
$process = $null
$client = [Net.Http.HttpClient]::new()
$client.Timeout = [TimeSpan]::FromSeconds(5)

try {
    $start = [Diagnostics.ProcessStartInfo]::new('dotnet')
    $debugOutput = Join-Path $repoRoot 'apps/server/bin/Debug/net10.0'
    $start.WorkingDirectory = $debugOutput
    $start.UseShellExecute = $false
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.ArgumentList.Add((Join-Path $debugOutput 'Hispania.Platform.Server.dll'))
    $start.Environment['ASPNETCORE_URLS'] = "http://127.0.0.1:$port"
    $start.Environment['Platform__DataPath'] = Join-Path $dataRoot 'platform'
    $start.Environment['Identity__Bootstrap__Token'] = $bootstrapToken
    $start.Environment['Modules__AdminToken'] = $adminToken
    $start.Environment['Testing__ShutdownToken'] = $shutdownToken
    $startupWatch = [Diagnostics.Stopwatch]::StartNew()
    $process = [Diagnostics.Process]::Start($start)
    $client.BaseAddress = [Uri]"http://127.0.0.1:$port/"

    $ready = $false
    for ($attempt = 0; $attempt -lt 60 -and -not $ready; $attempt++) {
        if ($process.HasExited) { throw "Platform-only process exited early: $($process.StandardError.ReadToEnd())" }
        try { $response = $client.GetAsync('/readyz').GetAwaiter().GetResult(); $ready = $response.StatusCode -eq [Net.HttpStatusCode]::OK; $response.Dispose() }
        catch { Start-Sleep -Milliseconds 500 }
    }
    if (-not $ready) { throw 'Platform-only process did not become ready within 30 seconds.' }
    $startupWatch.Stop()

    foreach ($route in @('/healthz', '/api/health/live', '/api/health/ready')) {
        $response = $client.GetAsync($route).GetAwaiter().GetResult()
        if ($response.StatusCode -ne [Net.HttpStatusCode]::OK) { throw "$route returned HTTP $([int]$response.StatusCode)." }
        $response.Dispose()
    }
    foreach ($route in @('/api/inventory/items', '/api/v1/media')) {
        $response = $client.GetAsync($route).GetAwaiter().GetResult()
        if ($response.StatusCode -ne [Net.HttpStatusCode]::NotFound) { throw "$route should be absent (404), got HTTP $([int]$response.StatusCode)." }
        $response.Dispose()
    }

    $create = [Net.Http.HttpRequestMessage]::new([Net.Http.HttpMethod]::Post, '/api/identity/users')
    $create.Headers.Authorization = [Net.Http.Headers.AuthenticationHeaderValue]::new('Bearer', $adminToken)
    $create.Content = New-JsonContent @{ username = 'platform-probe'; displayName = 'Platform probe'; password = 'ProbePass!23456789' }
    $created = $client.SendAsync($create).GetAwaiter().GetResult()
    if ($created.StatusCode -ne [Net.HttpStatusCode]::Created) { throw "Identity user creation returned HTTP $([int]$created.StatusCode)." }
    $user = $created.Content.ReadAsStringAsync().GetAwaiter().GetResult() | ConvertFrom-Json
    $userId = $user.id.value
    $created.Dispose(); $create.Dispose()

    $bootstrap = [Net.Http.HttpRequestMessage]::new([Net.Http.HttpMethod]::Post, '/api/authorization/bootstrap-admin')
    $bootstrap.Headers.Authorization = [Net.Http.Headers.AuthenticationHeaderValue]::new('Bootstrap', $bootstrapToken)
    $bootstrap.Content = New-JsonContent @{ userId = $userId }
    $bootstrapped = $client.SendAsync($bootstrap).GetAwaiter().GetResult()
    if ($bootstrapped.StatusCode -ne [Net.HttpStatusCode]::OK) { throw "Administrator bootstrap returned HTTP $([int]$bootstrapped.StatusCode)." }
    $bootstrapped.Dispose(); $bootstrap.Dispose()

    $login = [Net.Http.HttpRequestMessage]::new([Net.Http.HttpMethod]::Post, '/api/identity/login')
    $login.Content = New-JsonContent @{ username = 'platform-probe'; password = 'ProbePass!23456789' }
    $loggedIn = $client.SendAsync($login).GetAwaiter().GetResult()
    if ($loggedIn.StatusCode -ne [Net.HttpStatusCode]::OK) { throw "Identity login returned HTTP $([int]$loggedIn.StatusCode)." }
    $loginJson = $loggedIn.Content.ReadAsStringAsync().GetAwaiter().GetResult() | ConvertFrom-Json
    $sessionToken = $loginJson.sessionToken
    $loggedIn.Dispose(); $login.Dispose()

    foreach ($route in @('/api/platform/modules', '/api/platform/jobs/health', '/api/health/details', '/api/platform/observability', '/api/platform/admin/security/endpoints')) {
        $request = [Net.Http.HttpRequestMessage]::new([Net.Http.HttpMethod]::Get, $route)
        $request.Headers.Authorization = [Net.Http.Headers.AuthenticationHeaderValue]::new('Session', $sessionToken)
        $response = $client.SendAsync($request).GetAwaiter().GetResult()
        if ($response.StatusCode -ne [Net.HttpStatusCode]::OK) { throw "$route returned HTTP $([int]$response.StatusCode)." }
        if ($route -eq '/api/platform/admin/security/endpoints') {
            $inventory = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult() | ConvertFrom-Json
            if (-not ($inventory | Where-Object route -Like '/internal/worker/v1/*/connect')) { throw 'Worker connect route is missing from endpoint inventory.' }
            if (-not ($inventory | Where-Object route -Like '/internal/worker/v1/*/jobs/lease')) { throw 'Worker job lease route is missing from endpoint inventory.' }
        }
        $response.Dispose(); $request.Dispose()
    }

    $createJob = [Net.Http.HttpRequestMessage]::new([Net.Http.HttpMethod]::Post, '/api/platform/jobs')
    $createJob.Headers.Authorization = [Net.Http.Headers.AuthenticationHeaderValue]::new('Session', $sessionToken)
    $createJob.Content = New-JsonContent @{ jobType = 'platform.test.hash'; payload = @{ text = 'platform-only-smoke'; algorithm = 'sha256' }; executionPolicy = 'LocalOnly' }
    $acceptedJob = $client.SendAsync($createJob).GetAwaiter().GetResult()
    if ($acceptedJob.StatusCode -ne [Net.HttpStatusCode]::Accepted) { throw "Generic Platform Job creation returned HTTP $([int]$acceptedJob.StatusCode)." }
    $job = $acceptedJob.Content.ReadAsStringAsync().GetAwaiter().GetResult() | ConvertFrom-Json
    $jobId = $job.jobId
    $acceptedJob.Dispose(); $createJob.Dispose()
    $jobSucceeded = $false
    for ($attempt = 0; $attempt -lt 40 -and -not $jobSucceeded; $attempt++) {
        $jobRequest = [Net.Http.HttpRequestMessage]::new([Net.Http.HttpMethod]::Get, "/api/platform/jobs/$jobId")
        $jobRequest.Headers.Authorization = [Net.Http.Headers.AuthenticationHeaderValue]::new('Session', $sessionToken)
        $jobResponse = $client.SendAsync($jobRequest).GetAwaiter().GetResult()
        if ($jobResponse.StatusCode -ne [Net.HttpStatusCode]::OK) { throw "Generic Platform Job lookup returned HTTP $([int]$jobResponse.StatusCode)." }
        $jobState = $jobResponse.Content.ReadAsStringAsync().GetAwaiter().GetResult() | ConvertFrom-Json
        $jobSucceeded = $jobState.status -eq 'Succeeded'
        $jobResponse.Dispose(); $jobRequest.Dispose()
        if (-not $jobSucceeded) { Start-Sleep -Milliseconds 250 }
    }
    if (-not $jobSucceeded) { throw 'The supported platform.test.hash job did not complete successfully.' }
    $process.Refresh()
    $runtimeSample = [pscustomobject]@{
        StartupToReadyMilliseconds = $startupWatch.ElapsedMilliseconds
        WorkingSetBytes = $process.WorkingSet64
        ManagedHeapBytes = 'Not sampled'
        ThreadCount = $process.Threads.Count
        CpuIdle = 'Not sampled'
        Environment = 'Windows x64; one local process; after authenticated endpoint smoke checks'
    }

    $stop = [Net.Http.HttpRequestMessage]::new([Net.Http.HttpMethod]::Post, '/_test/shutdown')
    $stop.Headers.Authorization = [Net.Http.Headers.AuthenticationHeaderValue]::new('Bearer', $shutdownToken)
    $stopped = $client.SendAsync($stop).GetAwaiter().GetResult()
    if ($stopped.StatusCode -ne [Net.HttpStatusCode]::Accepted) { throw "Shutdown request returned HTTP $([int]$stopped.StatusCode)." }
    $stopped.Dispose(); $stop.Dispose()
    if (-not $process.WaitForExit(10000)) { throw 'Platform-only process did not stop cleanly within 10 seconds.' }

    [pscustomobject]@{
        Result = 'PASS'
        WindowsArtifact = $winInventory
        LinuxArm64Artifact = $armInventory
        Runtime = 'Ready; health, auth/bootstrap, module, generic job execution, worker route inventory and observability passed; domain routes returned 404; graceful shutdown observed.'
        ResourceSample = $runtimeSample
        FullBuild = 'PASS'
        NativeArm64Execution = 'Not run; requires Linux ARM64 hardware/runner.'
    } | Format-List
}
finally {
    $client.Dispose()
    if ($process -and -not $process.HasExited) { $process.Kill($true); $process.WaitForExit() }
    if (Test-Path -LiteralPath $dataRoot) { Remove-Item -LiteralPath $dataRoot -Recurse -Force }
}
