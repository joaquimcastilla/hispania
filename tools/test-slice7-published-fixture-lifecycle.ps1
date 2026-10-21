[CmdletBinding()]
param(
    [string]$ArtifactDirectory = (Join-Path $env:TEMP 'hispania-slice7-accepted-20261018-publish'),
    [string]$RepositoryRoot = (Split-Path -Parent $PSScriptRoot),
    [int]$Port = 5070,
    [switch]$RestartOnly
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

if ($PSVersionTable.PSVersion.Major -lt 7) { throw 'PowerShell 7 or later is required.' }
if (-not $IsWindows) { throw 'This acceptance script is limited to Windows x64.' }
if ([Environment]::Is64BitOperatingSystem -eq $false) { throw 'Windows x64 is required.' }

$artifact = [IO.Path]::GetFullPath((Resolve-Path -LiteralPath $ArtifactDirectory).Path)
$repo = [IO.Path]::GetFullPath((Resolve-Path -LiteralPath $RepositoryRoot).Path)
$coreExe = Join-Path $artifact 'Hispania.Core.exe'
$contractsDll = Join-Path $artifact 'Hispania.ModuleSystem.Contracts.dll'
$moduleHostDll = Join-Path $artifact 'module-host/Hispania.ModuleHost.dll'
$fixtureProject = Join-Path $repo 'tests/fixtures/slice7-fixture/Hispania.Slice7.Fixture.csproj'
$fixtureAssembly = $null
$fixtureId = 'hispania.slice7.fixture'
$publisherId = 'hispania.slice7.test'
$baseUri = [Uri]::new("https://127.0.0.1:$Port")
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('hispania-slice7-published-lifecycle-' + [Guid]::NewGuid().ToString('N'))
$envNames = @(
    'HISPANIA_CORE_DATA_DIRECTORY', 'HISPANIA_CORE_LISTEN_ADDRESS', 'HISPANIA_CORE_PORT',
    'HISPANIA_CORE_HTTPS_CERTIFICATE_PATH', 'HISPANIA_CORE_HTTPS_CERTIFICATE_PASSWORD',
    'HISPANIA_CORE_MODULES__TRUSTEDPUBLISHERKEYS__hispania.slice7.test'
)
$savedEnvironment = @{}
foreach ($name in $envNames) { $savedEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, 'Process') }
$results = [Collections.Generic.List[object]]::new()
$coreProcessId = $null
$lastCoreProcessId = $null
$coreHasStarted = $false
$testRootCreated = $false
$baselineModuleHostIds = @()
$client = $null
$rsa = $null
$privatePem = $null
$certificate = $null
$packageBytes = [Collections.Generic.List[byte[]]]::new()
$overallFailure = $null
$failureDetailCode = $null
$stage = 'preflight'
$evidence = [ordered]@{}

function Add-Result([string]$Name, [bool]$Passed) {
    $results.Add([pscustomobject]@{ check = $Name; result = $(if ($Passed) { 'PASS' } else { 'FAIL' }) })
    if (-not $Passed) { throw "Acceptance check failed: $Name" }
}

function Invoke-CoreRequest([string]$Method, [string]$Path, [object]$JsonBody = $null,
    [byte[]]$Bytes = $null, [switch]$Mutation) {
    $request = [Net.Http.HttpRequestMessage]::new([Net.Http.HttpMethod]::new($Method), [Uri]::new($baseUri, $Path))
    try {
        [void]$request.Headers.TryAddWithoutValidation('Origin', $baseUri.GetLeftPart([UriPartial]::Authority))
        if ($Mutation) {
            if ([string]::IsNullOrWhiteSpace($script:csrfToken)) { throw 'Missing in-memory antiforgery token.' }
            [void]$request.Headers.TryAddWithoutValidation('X-Hispania-CSRF', $script:csrfToken)
        }
        if ($null -ne $Bytes) {
            $request.Content = [Net.Http.ByteArrayContent]::new($Bytes)
            $request.Content.Headers.ContentType = [Net.Http.Headers.MediaTypeHeaderValue]::new('application/zip')
        } elseif ($null -ne $JsonBody) {
            $json = ConvertTo-Json -InputObject $JsonBody -Depth 8 -Compress
            $request.Content = [Net.Http.StringContent]::new($json, [Text.Encoding]::UTF8, 'application/json')
        }
        $response = $client.Send($request)
        try {
            $body = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
            return [pscustomobject]@{ Status = [int]$response.StatusCode; Body = $body }
        } finally { $response.Dispose() }
    } finally { $request.Dispose() }
}

function Get-SafeHttpFailureCode([int]$Status, [string]$Path, [switch]$Mutation, [string]$Body) {
    $bodyCode = $null
    try { $bodyCode = [string](($Body | ConvertFrom-Json -ErrorAction Stop).error) } catch { }
    if ($Status -eq 400) {
        if ($Path -eq '/api/v1/auth/login') {
            if ($bodyCode -eq 'invalid_request') { return 'LOGIN_VALIDATION_REJECTED' }
            return 'LOGIN_BAD_REQUEST'
        }
        if ($Mutation) {
            switch ($bodyCode) {
                'application_zip_required' { return 'MODULE_CONTENT_TYPE_REJECTED' }
                'module_package_rejected' { return 'MODULE_PACKAGE_REJECTED' }
                default { return 'ANTIFORGERY_REJECTED' }
            }
        }
        return 'HTTP_400_REJECTED'
    }
    if ($Status -eq 401) { return 'AUTHENTICATION_REJECTED' }
    if ($Status -eq 403) {
        if ($Path -eq '/api/v1/auth/login') { return 'LOGIN_ORIGIN_REJECTED' }
        if ($Path -eq '/api/v1/auth/modules') { return 'MODULE_READ_PERMISSION_REJECTED' }
        if ($Mutation) { return 'MODULE_ORIGIN_REJECTED_AFTER_PERMISSION_CHECK' }
        return 'AUTHORIZATION_REJECTED'
    }
    if ($Status -eq 429) { return 'LOGIN_RATE_LIMITED' }
    return 'UNEXPECTED_HTTP_STATUS'
}

function Get-FixtureRow {
    $reply = Invoke-CoreRequest 'GET' '/api/v1/auth/modules'
    if ($reply.Status -ne 200) {
        $script:failureDetailCode = Get-SafeHttpFailureCode $reply.Status '/api/v1/auth/modules' -Body $reply.Body
        throw 'Authenticated module listing did not return HTTP 200.'
    }
    $rows = @($reply.Body | ConvertFrom-Json -AsHashtable)
    return @($rows | Where-Object { $_.packageId -eq $fixtureId })
}

function Wait-CoreHealthy {
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds(45)
    while ([DateTimeOffset]::UtcNow -lt $deadline) {
        if ($coreProcessId) {
            $script:stage = 'published-core-process-monitor'
            $proc = Get-Process -Id $coreProcessId -ErrorAction SilentlyContinue
            if (-not $proc) {
                $script:stage = 'published-core-exited-before-health'
                throw 'Published Core exited before health became available.'
            }
        }
        $script:stage = 'published-core-health-https-request'
        try {
            $reply = Invoke-CoreRequest 'GET' '/health/live'
            $script:failureDetailCode = $null
        } catch {
            $script:failureDetailCode = if ($_.Exception.GetType().Name -eq 'HttpRequestException') {
                'HTTPS_REQUEST_EXCEPTION'
            } else { 'HEALTH_REQUEST_EXCEPTION' }
            Start-Sleep -Milliseconds 400
            continue
        }
        if ($reply.Status -ne 200) {
            $script:stage = 'published-core-health-http-status-rejected'
            $script:failureDetailCode = 'HEALTH_HTTP_STATUS_REJECTED'
            Start-Sleep -Milliseconds 400
            continue
        }
        $script:stage = 'published-core-health-json'
        try { $health = $reply.Body | ConvertFrom-Json -ErrorAction Stop }
        catch {
            $script:stage = 'published-core-health-json-parse'
            $script:failureDetailCode = 'HEALTH_JSON_INVALID'
            Start-Sleep -Milliseconds 400
            continue
        }
        if ($health.status -eq 'healthy') { return }
        $script:stage = 'published-core-health-body-status-unexpected'
        $script:failureDetailCode = 'HEALTH_STATUS_UNEXPECTED'
        Start-Sleep -Milliseconds 400
    }
    $script:stage = 'published-core-health-timeout'
    throw 'Published Core did not become healthy within 45 seconds.'
}

function Start-PublishedCore {
    $script:stage = 'published-core-process-start'
    $pidValue = [Slice7ProcessControl]::StartNewGroup($coreExe, $artifact)
    if ($pidValue -le 0) { throw 'Could not start the accepted published Core.' }
    $script:coreProcessId = [int]$pidValue
    $script:lastCoreProcessId = [int]$pidValue
    $script:coreHasStarted = $true
    $script:stage = 'published-core-health-check'
    Wait-CoreHealthy
}

function Stop-PublishedCore([bool]$MustBeGraceful = $true) {
    if (-not $script:coreProcessId) { return }
    $proc = Get-Process -Id $script:coreProcessId -ErrorAction SilentlyContinue
    if (-not $proc) { $script:coreProcessId = $null; return }
    $script:stage = 'published-core-stop-send-control-event'
    $script:failureDetailCode = 'CORE_CONTROL_EVENT_SEND_FAILED'
    if (-not [Slice7ProcessControl]::SendCtrlBreak([uint32]$script:coreProcessId)) {
        if ($MustBeGraceful) { throw 'Could not send CTRL+BREAK to the isolated Core process group.' }
    }
    $script:stage = 'published-core-stop-wait-for-exit'
    $script:failureDetailCode = 'CORE_GRACEFUL_SHUTDOWN_TIMEOUT'
    try { $proc.WaitForExit(15000) | Out-Null } catch { }
    $proc.Refresh()
    if (-not $proc.HasExited) {
        if ($MustBeGraceful) { throw 'Core did not stop gracefully after CTRL+BREAK.' }
        $proc.Kill($true)
        $proc.WaitForExit()
    }
    $script:stage = 'published-core-stop-exit-code'
    $script:failureDetailCode = 'CORE_EXIT_CODE_UNAVAILABLE'
    $exitCode = [int]$proc.ExitCode
    $script:evidence.coreExitCode = $exitCode
    if ($MustBeGraceful -and $exitCode -ne [int]0) {
        $script:failureDetailCode = 'CORE_GRACEFUL_SHUTDOWN_NONZERO_EXIT'
        throw 'Published Core returned a nonzero exit code after graceful shutdown.'
    }
    $script:failureDetailCode = $null
    $script:coreProcessId = $null
    $script:stage = 'published-core-stop-modulehost-check'
    $script:failureDetailCode = 'MODULEHOST_REMAINS_AFTER_CORE_STOP'
    $moduleHostPattern = [regex]::Escape($moduleHostDll)
    $moduleHosts = @(Get-CimInstance -ClassName Win32_Process -Filter "Name = 'dotnet.exe'" -ErrorAction Stop |
        Where-Object { $_.CommandLine -and $_.CommandLine -match $moduleHostPattern -and $script:baselineModuleHostIds -notcontains [int]$_.ProcessId })
    if ($moduleHosts.Count -gt 0) { throw 'A ModuleHost process for this accepted artifact remains after Core shutdown.' }
    $script:failureDetailCode = $null
}

function New-SignedFixturePackage([string]$AssemblyPath, [string]$EntryType, [string]$PackageVersion,
    [string]$ModuleVersion, [string]$OutputPath, [string]$FixtureKind = 'normal') {
    $script:stage = "$FixtureKind-fixture-payload-hash"
    $payloadName = [IO.Path]::GetFileName($AssemblyPath)
    $digest = (Get-FileHash -LiteralPath $AssemblyPath -Algorithm SHA256).Hash.ToLowerInvariant()
    $manifestMap = [ordered]@{
        packageFormatVersion = 2; packageVersion = $PackageVersion; packageDependencies = @()
        requiredModuleHostProtocol = 1; id = $fixtureId; name = 'HISPANIA Slice 7 Fixture'
        version = $ModuleVersion; implements = @(); provides = @(); requires = @(); optionalDependencies = @()
        dependencies = @(); dependencyMinimumVersions = @{}; permissions = @('core.modules.manage')
        minimumHostVersion = '1.0.0'; maximumHostVersion = '1.0.0'; runtime = 'dotnet-outofprocess'
        restartRequiredForUpdate = $true; entryAssembly = $payloadName; entryType = $EntryType
        sharedAssemblies = @('Hispania.ModuleSystem.Contracts'); contractTypes = @{}
        files = [ordered]@{ $payloadName = $digest }
    }
    $options = [Text.Json.JsonSerializerOptions]::new()
    $options.PropertyNameCaseInsensitive = $true
    $options.PropertyNamingPolicy = [Text.Json.JsonNamingPolicy]::CamelCase
    $script:stage = "$FixtureKind-fixture-manifest-construction"
    $typed = [Text.Json.JsonSerializer]::Deserialize(
        (ConvertTo-Json -InputObject $manifestMap -Depth 24 -Compress),
        [Hispania.ModuleSystem.Contracts.ModuleManifest], $options)
    if ($null -eq $typed) { throw 'Could not construct the fixture manifest using the accepted package contract.' }
    $script:stage = "$FixtureKind-fixture-rsa-pss-signing"
    $signed = [Hispania.ModuleSystem.Contracts.ModulePackageSignature]::Sign($typed, $publisherId, $privatePem)

    $script:stage = "$FixtureKind-fixture-package-staging"
    $staging = Join-Path $testRoot ('package-stage-' + [Guid]::NewGuid().ToString('N'))
    [void][IO.Directory]::CreateDirectory($staging)
    try {
        [IO.File]::Copy($AssemblyPath, (Join-Path $staging $payloadName), $false)
        $manifestJson = [Text.Json.JsonSerializer]::Serialize($signed, $options)
        [IO.File]::WriteAllText((Join-Path $staging 'module.json'), $manifestJson, [Text.UTF8Encoding]::new($false))
        $script:stage = "$FixtureKind-fixture-zip-packaging"
        $zipStream = [IO.File]::Open($OutputPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
        try {
            $zip = [IO.Compression.ZipArchive]::new($zipStream, [IO.Compression.ZipArchiveMode]::Create, $false)
            try {
                foreach ($file in @('module.json', $payloadName)) {
                    $entry = $zip.CreateEntry($file, [IO.Compression.CompressionLevel]::Optimal)
                    $input = [IO.File]::OpenRead((Join-Path $staging $file))
                    try { $output = $entry.Open(); try { $input.CopyTo($output) } finally { $output.Dispose() } }
                    finally { $input.Dispose() }
                }
            } finally { $zip.Dispose() }
        } finally { $zipStream.Dispose() }
    } finally {
        if ([IO.Directory]::Exists($staging)) { [IO.Directory]::Delete($staging, $true) }
    }
    return [IO.File]::ReadAllBytes($OutputPath)
}

function Assert-AcceptedArtifactIntegrity {
    $manifestPath = Join-Path $artifact 'core-artifact-manifest.json'
    $manifest = Get-Content -Raw -LiteralPath $manifestPath | ConvertFrom-Json
    if ($manifest.contentId -ne '44B43088658F2F70FE8A0745EA5DFC3B1519CBF10B9347F3F4B88835EEDB3B12' -or
        $manifest.sourceCommit -ne '591dc17db24f0afd3ad066c96b0b1db1494d0150') {
        throw 'Accepted artifact identity does not match the recorded Slice 7 publish.'
    }
    $rootPrefix = $artifact.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    $expectedPaths = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($entry in $manifest.files) {
        $relative = ([string]$entry.path).Replace('/', [IO.Path]::DirectorySeparatorChar)
        if ([IO.Path]::IsPathRooted($relative) -or $relative.Split([IO.Path]::DirectorySeparatorChar) -contains '..') {
            throw 'Artifact manifest contains an unsafe relative path.'
        }
        $full = [IO.Path]::GetFullPath((Join-Path $artifact $relative))
        if (-not $full.StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase) -or -not [IO.File]::Exists($full)) {
            throw 'An accepted artifact manifest file is missing or outside the artifact root.'
        }
        $info = [IO.FileInfo]::new($full)
        $hash = (Get-FileHash -LiteralPath $full -Algorithm SHA256).Hash
        if ($info.Length -ne [long]$entry.size -or $hash -ne [string]$entry.sha256) {
            throw 'An accepted artifact file differs from its recorded size or SHA-256.'
        }
        [void]$expectedPaths.Add($relative)
    }
    $actualPaths = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($file in Get-ChildItem -LiteralPath $artifact -File -Recurse -Force) {
        $relative = [IO.Path]::GetRelativePath($artifact, $file.FullName)
        if ($relative -ne 'core-artifact-manifest.json') { [void]$actualPaths.Add($relative) }
    }
    if (-not $expectedPaths.SetEquals($actualPaths)) { throw 'Accepted artifact file inventory differs from its manifest.' }
    $script:evidence.coreContentId = $manifest.contentId
    $script:evidence.coreExeSha256 = (Get-FileHash -LiteralPath $coreExe -Algorithm SHA256).Hash
    $script:evidence.moduleHostSha256 = (Get-FileHash -LiteralPath $moduleHostDll -Algorithm SHA256).Hash
}

try {
    $stage = 'required-paths'
    foreach ($required in @($coreExe, $contractsDll, $moduleHostDll, $fixtureProject)) {
        if (-not [IO.File]::Exists($required)) { throw "Required file is missing: $required" }
    }
    $stage = 'artifact-identity'
    if ((Split-Path -Leaf $artifact) -ne 'hispania-slice7-accepted-20261018-publish') {
        throw 'Refusing a different artifact; this procedure is pinned to the accepted Slice 7 publish directory.'
    }
    $stage = 'port-preflight'
    if ($Port -lt 1024 -or $Port -gt 65535) { throw 'Port must be an unprivileged TCP port.' }
    $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, $Port)
    try { $listener.Start(); $listener.Stop() } catch { throw "Port $Port is already in use or unavailable." }

    $stage = 'temporary-root-create'
    if ([IO.Directory]::Exists($testRoot) -or [IO.File]::Exists($testRoot)) { throw 'Unique test root already exists; refusing reuse.' }
    [void][IO.Directory]::CreateDirectory($testRoot)
    $testRootCreated = $true
    $stage = 'accepted-artifact-validation'
    Assert-AcceptedArtifactIntegrity
    Add-Result 'accepted artifact source/content ID, inventory and manifest SHA-256 verified' $true

    $stage = 'modulehost-process-inventory'
    if (-not (Get-Command Get-CimInstance -ErrorAction SilentlyContinue)) {
        throw 'Required Win32 process inventory command is unavailable.'
    }
    $moduleHostPattern = [regex]::Escape($moduleHostDll)
    $baselineModuleHostIds = @(Get-CimInstance -ClassName Win32_Process -Filter "Name = 'dotnet.exe'" -ErrorAction Stop |
        Where-Object { $_.CommandLine -and $_.CommandLine -match $moduleHostPattern } |
        ForEach-Object { [int]$_.ProcessId })

    # Load the exact contract assembly shipped beside the accepted Core executable.
    $stage = 'published-contract-assembly-load'
    Add-Type -LiteralPath $contractsDll
    $stage = 'process-and-tls-helper-compile'
    Add-Type -TypeDefinition @'
#nullable enable
using System;
using System.Runtime.InteropServices;
using System.Text;
public static class Slice7ProcessControl {
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO { public int cb; public string? lpReserved; public string? lpDesktop; public string? lpTitle; public int dwX; public int dwY; public int dwXSize; public int dwYSize; public int dwXCountChars; public int dwYCountChars; public int dwFillAttribute; public int dwFlags; public short wShowWindow; public short cbReserved2; public IntPtr lpReserved2; public IntPtr hStdInput; public IntPtr hStdOutput; public IntPtr hStdError; }
    [StructLayout(LayoutKind.Sequential)] private struct PROCESS_INFORMATION { public IntPtr hProcess; public IntPtr hThread; public int dwProcessId; public int dwThreadId; }
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)] private static extern bool CreateProcess(string app, StringBuilder command, IntPtr processAttrs, IntPtr threadAttrs, bool inherit, uint flags, IntPtr environment, string cwd, ref STARTUPINFO startup, out PROCESS_INFORMATION info);
    [DllImport("kernel32.dll", SetLastError=true)] private static extern bool GenerateConsoleCtrlEvent(uint eventType, uint processGroupId);
    [DllImport("kernel32.dll", SetLastError=true)] private static extern bool CloseHandle(IntPtr handle);
    public static int StartNewGroup(string exe, string cwd) {
        var si = new STARTUPINFO(); si.cb = Marshal.SizeOf<STARTUPINFO>();
        PROCESS_INFORMATION pi;
        var command = new StringBuilder("\"" + exe + "\"");
        if (!CreateProcess(exe, command, IntPtr.Zero, IntPtr.Zero, false, 0x00000200, IntPtr.Zero, cwd, ref si, out pi))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "CreateProcess failed.");
        CloseHandle(pi.hThread); CloseHandle(pi.hProcess);
        return pi.dwProcessId;
    }
    public static bool SendCtrlBreak(uint processGroupId) => GenerateConsoleCtrlEvent(1, processGroupId);
}
public static class Slice7TlsValidation {
    public static System.Func<System.Net.Http.HttpRequestMessage, System.Security.Cryptography.X509Certificates.X509Certificate2?, System.Security.Cryptography.X509Certificates.X509Chain?, System.Net.Security.SslPolicyErrors, bool> CreateHttpClientCallback(string expectedThumbprint) =>
        (_, cert, _, errors) => Validate(cert, errors, expectedThumbprint);
    public static bool Validate(System.Security.Cryptography.X509Certificates.X509Certificate2? cert, System.Net.Security.SslPolicyErrors errors, string expectedThumbprint) {
        if (cert == null || errors.HasFlag(System.Net.Security.SslPolicyErrors.RemoteCertificateNameMismatch) || errors.HasFlag(System.Net.Security.SslPolicyErrors.RemoteCertificateNotAvailable)) return false;
        if (!String.Equals(cert.Thumbprint, expectedThumbprint, StringComparison.OrdinalIgnoreCase) || DateTime.UtcNow < cert.NotBefore.ToUniversalTime() || DateTime.UtcNow >= cert.NotAfter.ToUniversalTime()) return false;
        using var chain = new System.Security.Cryptography.X509Certificates.X509Chain();
        chain.ChainPolicy.TrustMode = System.Security.Cryptography.X509Certificates.X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(cert);
        return chain.Build(cert);
    }
}
'@

    $stage = 'publisher-test-key-generation'
    $rsa = [Security.Cryptography.RSA]::Create(2048)
    $privatePem = $rsa.ExportPkcs8PrivateKeyPem()
    $publicPem = $rsa.ExportSubjectPublicKeyInfoPem()

    $stage = 'https-test-certificate-generation'
    $certRsa = [Security.Cryptography.RSA]::Create(2048)
    try {
        $request = [Security.Cryptography.X509Certificates.CertificateRequest]::new(
            'CN=HISPANIA Slice 7 published smoke', $certRsa,
            [Security.Cryptography.HashAlgorithmName]::SHA256, [Security.Cryptography.RSASignaturePadding]::Pkcs1)
        $san = [Security.Cryptography.X509Certificates.SubjectAlternativeNameBuilder]::new()
        $san.AddIpAddress([Net.IPAddress]::Loopback)
        $request.CertificateExtensions.Add($san.Build())
        $request.CertificateExtensions.Add([Security.Cryptography.X509Certificates.X509BasicConstraintsExtension]::new($false, $false, 0, $false))
        $request.CertificateExtensions.Add([Security.Cryptography.X509Certificates.X509KeyUsageExtension]::new([Security.Cryptography.X509Certificates.X509KeyUsageFlags]::DigitalSignature, $false))
        $eku = [Security.Cryptography.OidCollection]::new(); [void]$eku.Add([Security.Cryptography.Oid]::new('1.3.6.1.5.5.7.3.1'))
        $request.CertificateExtensions.Add([Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension]::new($eku, $false))
        $certificate = $request.CreateSelfSigned([DateTimeOffset]::UtcNow.AddMinutes(-2), [DateTimeOffset]::UtcNow.AddHours(2))
        $pfxPassword = [Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
        $pfxPath = Join-Path $testRoot 'https-test-certificate.pfx'
        $stage = 'https-test-certificate-temporary-write'
        [IO.File]::WriteAllBytes($pfxPath, $certificate.Export([Security.Cryptography.X509Certificates.X509ContentType]::Pfx, $pfxPassword))
    } finally { $certRsa.Dispose() }

    $envMap = @{
        HISPANIA_CORE_DATA_DIRECTORY = Join-Path $testRoot 'core-data'
        HISPANIA_CORE_LISTEN_ADDRESS = '127.0.0.1'
        HISPANIA_CORE_PORT = [string]$Port
        HISPANIA_CORE_HTTPS_CERTIFICATE_PATH = $pfxPath
        HISPANIA_CORE_HTTPS_CERTIFICATE_PASSWORD = $pfxPassword
        'HISPANIA_CORE_MODULES__TRUSTEDPUBLISHERKEYS__hispania.slice7.test' = $publicPem
    }
    $stage = 'isolated-core-environment-configuration'
    foreach ($entry in $envMap.GetEnumerator()) { [Environment]::SetEnvironmentVariable($entry.Key, $entry.Value, 'Process') }

    # Trust is supplied through existing protected host settings, never a SQLite edit.
    if ([Environment]::GetEnvironmentVariable('HISPANIA_CORE_MODULES__TRUSTEDPUBLISHERKEYS__hispania.slice7.test', 'Process') -ne $publicPem) {
        throw 'Test publisher key is not present in the Core process configuration.'
    }
    Add-Result 'publisher trust configured through existing protected host settings' $true

    # The Core's documented one-use bootstrap path creates a synthetic local admin.
    $adminName = 'slice7-' + [Guid]::NewGuid().ToString('N').Substring(0, 12)
    $adminPassword = [Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
    $stage = 'synthetic-admin-bootstrap'
    if ([IO.Directory]::Exists($envMap.HISPANIA_CORE_DATA_DIRECTORY)) { throw 'Fresh isolated Core data directory already exists.' }
    $bootstrapInfo = [Diagnostics.ProcessStartInfo]::new($coreExe, 'bootstrap-admin')
    $bootstrapInfo.WorkingDirectory = $artifact
    $bootstrapInfo.UseShellExecute = $false
    $bootstrapInfo.RedirectStandardInput = $true
    $bootstrapInfo.RedirectStandardOutput = $true
    $bootstrapInfo.RedirectStandardError = $true
    $bootstrap = [Diagnostics.Process]::Start($bootstrapInfo)
    try {
        $bootstrap.StandardInput.WriteLine($adminName)
        $bootstrap.StandardInput.WriteLine($adminPassword)
        $bootstrap.StandardInput.WriteLine($adminPassword)
        $bootstrap.StandardInput.Close()
        $bootstrap.WaitForExit(30000) | Out-Null
        if (-not $bootstrap.HasExited -or $bootstrap.ExitCode -ne 0) { throw 'Published Core local administrator bootstrap failed.' }
        $bootstrapOutput = $bootstrap.StandardOutput.ReadToEnd()
        if ($bootstrapOutput -notmatch 'Initial administrator created\.') { throw 'Bootstrap did not report successful creation.' }
        [void]$bootstrap.StandardError.ReadToEnd()
        if (-not [IO.File]::Exists((Join-Path $envMap.HISPANIA_CORE_DATA_DIRECTORY 'core.db'))) {
            throw 'Bootstrap did not create the isolated Core SQLite database.'
        }
        Add-Result 'published Core fresh SQLite database and one-time local bootstrap' $true
    } finally { $bootstrap.Dispose() }

    $stage = 'https-client-configuration'
    $handler = [Net.Http.HttpClientHandler]::new()
    $thumbprint = $certificate.Thumbprint
    $handler.ServerCertificateCustomValidationCallback = [Slice7TlsValidation]::CreateHttpClientCallback($thumbprint)
    $client = [Net.Http.HttpClient]::new($handler)
    $client.Timeout = [TimeSpan]::FromSeconds(15)

    Start-PublishedCore
    $stage = 'published-core-health-check'
    Add-Result 'published Core HTTPS liveness with SAN-validated test certificate' $true

    $stage = 'admin-authentication-and-module-api-preflight'
    $login = Invoke-CoreRequest 'POST' '/api/v1/auth/login' @{ username = $adminName; password = $adminPassword }
    if ($login.Status -ne 200) {
        $failureDetailCode = Get-SafeHttpFailureCode $login.Status '/api/v1/auth/login' -Body $login.Body
        throw 'Synthetic administrator login failed.'
    }
    $loginJson = $login.Body | ConvertFrom-Json
    $script:csrfToken = [string]$loginJson.csrfToken
    if ([string]::IsNullOrWhiteSpace($csrfToken)) { throw 'Login did not return the expected antiforgery request token.' }
    $me = Invoke-CoreRequest 'GET' '/api/v1/auth/me'
    if ($me.Status -ne 200) {
        $failureDetailCode = Get-SafeHttpFailureCode $me.Status '/api/v1/auth/me' -Body $me.Body
        throw 'Authenticated /me request failed.'
    }
    $meJson = $me.Body | ConvertFrom-Json
    if (@($meJson.permissions) -notcontains 'core.modules.manage' -or @($meJson.permissions) -notcontains 'core.modules.read') {
        $failureDetailCode = 'MODULE_PERMISSIONS_MISSING'
        throw 'Bootstrapped administrator lacks the expected module permissions.'
    }
    $initial = Get-FixtureRow
    if ($initial.Count -ne 0) { throw 'Fresh Core module registry was not empty.' }
    Add-Result 'authenticated admin has module read/manage permissions; fresh registry empty' $true

    $stage = 'normal-fixture-build'
    $buildArtifacts = Join-Path $testRoot 'fixture-build-artifacts'
    & dotnet build $fixtureProject --configuration Release --artifacts-path $buildArtifacts --verbosity quiet
    $buildExitCode = $LASTEXITCODE
    if ($buildExitCode -ne 0) {
        $failureDetailCode = 'NORMAL_FIXTURE_BUILD_FAILED'
        throw 'Could not build the approved test fixture payload in the isolated temporary root.'
    }
    $stage = 'normal-fixture-output-selection'
    $fixtureBinRoot = Join-Path $buildArtifacts 'bin'
    $fixtureOutputs = @(Get-ChildItem -LiteralPath $fixtureBinRoot -File -Filter 'Hispania.Slice7.Fixture.dll' -Recurse -ErrorAction SilentlyContinue)
    if ($fixtureOutputs.Count -ne 1) {
        $failureDetailCode = 'NORMAL_FIXTURE_BIN_OUTPUT_AMBIGUOUS'
        throw 'Could not identify exactly one executable fixture assembly under the isolated bin output.'
    }
    $fixtureAssembly = $fixtureOutputs[0].FullName

    $stage = 'failure-fixture-source-preparation'
    $failureSourceRoot = Join-Path $testRoot 'failure-fixture-source'
    $failureOut = Join-Path $testRoot 'failure-fixture-output'
    [void][IO.Directory]::CreateDirectory($failureSourceRoot)
    [void][IO.Directory]::CreateDirectory($failureOut)
    $contractRef = [Security.SecurityElement]::Escape($contractsDll)
    $failureProject = @"
<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework><AssemblyName>Hispania.Slice7.FailureFixture</AssemblyName><Nullable>enable</Nullable></PropertyGroup><ItemGroup><Reference Include="Hispania.ModuleSystem.Contracts"><HintPath>$contractRef</HintPath></Reference></ItemGroup></Project>
"@
    $failureCode = @'
using System;
using System.Threading;
using System.Threading.Tasks;
using Hispania.ModuleSystem.Contracts;
namespace Hispania.Slice7.FailureFixture;
public sealed class FailingFixtureModule : IModule {
    public ModuleDescriptor Descriptor { get; } = new("hispania.slice7.fixture", "HISPANIA Slice 7 Fixture", new Version(1, 0, 1));
    public ValueTask StartAsync(CancellationToken cancellationToken) => ValueTask.FromException(new InvalidOperationException("Expected controlled Slice 7 fixture failure."));
    public ValueTask StopAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
}
'@
    [IO.File]::WriteAllText((Join-Path $failureSourceRoot 'FailureFixture.csproj'), $failureProject.Trim(), [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText((Join-Path $failureSourceRoot 'FailingFixtureModule.cs'), $failureCode, [Text.UTF8Encoding]::new($false))
    $stage = 'failure-fixture-build'
    & dotnet build (Join-Path $failureSourceRoot 'FailureFixture.csproj') --configuration Release --output $failureOut --verbosity quiet
    $failureBuildExitCode = $LASTEXITCODE
    $failureAssembly = Join-Path $failureOut 'Hispania.Slice7.FailureFixture.dll'
    if ($failureBuildExitCode -ne 0 -or -not [IO.File]::Exists($failureAssembly)) {
        $failureDetailCode = 'CONTROLLED_FAILURE_FIXTURE_BUILD_FAILED'
        throw 'Could not build the isolated controlled-failure fixture.'
    }

    $stage = 'normal-fixture-package-preparation'
    $normalPackagePath = Join-Path $testRoot 'fixture-normal.hispania'
    $normalPackage = New-SignedFixturePackage $fixtureAssembly 'Hispania.Slice7.Fixture.Slice7FixtureModule' '1.0.0' '1.0.0' $normalPackagePath
    $packageBytes.Add($normalPackage)
    $evidence.fixtureAssemblySha256 = (Get-FileHash -LiteralPath $fixtureAssembly -Algorithm SHA256).Hash
    $evidence.signedFixturePackageSha256 = (Get-FileHash -LiteralPath $normalPackagePath -Algorithm SHA256).Hash
    $stage = 'normal-fixture-package-install'
    $install = Invoke-CoreRequest 'POST' '/api/v1/auth/modules/install' -Bytes $normalPackage -Mutation
    if ($install.Status -ne 201) {
        $failureDetailCode = Get-SafeHttpFailureCode $install.Status '/api/v1/auth/modules/install' -Mutation -Body $install.Body
        throw 'Published Core rejected the trusted signed fixture package.'
    }
    $row = @(Get-FixtureRow)
    if ($row.Count -ne 1 -or -not $row[0].trusted -or -not $row[0].authorized -or -not $row[0].installed -or $row[0].enabled) {
        throw 'Installed fixture listing did not show trusted, authorized, installed and disabled state.'
    }
    Add-Result 'signed fixture installed through published Core and listed disabled/trusted/authorized' $true

    $enable = Invoke-CoreRequest 'POST' ("/api/v1/auth/modules/$fixtureId/enable") -Mutation
    if ($enable.Status -ne 200) {
        $failureDetailCode = Get-SafeHttpFailureCode $enable.Status "/api/v1/auth/modules/$fixtureId/enable" -Mutation -Body $enable.Body
        throw 'Published Core could not activate the trusted fixture.'
    }
    $row = @(Get-FixtureRow)
    if ($row.Count -ne 1 -or -not $row[0].enabled -or -not $row[0].healthy) { throw 'Fixture did not become enabled and healthy.' }
    Add-Result 'fixture activation and healthy state' $true

    $dataRootBeforeRestart = [IO.Path]::GetFullPath($envMap.HISPANIA_CORE_DATA_DIRECTORY)
    $databasePath = Join-Path $dataRootBeforeRestart 'core.db'
    $moduleRoot = Join-Path $dataRootBeforeRestart 'modules'
    $packageCatalogPath = Join-Path $moduleRoot '.package-state/catalog.json'
    $installedFixturePath = Join-Path $moduleRoot "$fixtureId/1.0.0"
    $stage = 'published-core-restart-stop'
    if (-not [IO.File]::Exists($databasePath) -or -not [IO.File]::Exists($packageCatalogPath) -or -not [IO.Directory]::Exists($installedFixturePath)) {
        $failureDetailCode = 'PERSISTED_STORAGE_NOT_PRESENT_BEFORE_RESTART'
        throw 'Expected SQLite, package catalog and installed fixture were not present before restart.'
    }
    Stop-PublishedCore
    $failureDetailCode = $null
    $stage = 'published-core-restart-storage-path-check'
    if ([IO.Path]::GetFullPath([Environment]::GetEnvironmentVariable('HISPANIA_CORE_DATA_DIRECTORY', 'Process')) -cne $dataRootBeforeRestart) {
        $failureDetailCode = 'PERSISTENCE_ROOT_CHANGED_BETWEEN_PROCESSES'
        throw 'Core data directory changed between the published Core processes.'
    }
    $stage = 'published-core-restart-start'
    $failureDetailCode = 'CORE_RESTART_OR_HEALTH_FAILED'
    Start-PublishedCore
    $failureDetailCode = $null
    $stage = 'published-core-restart-storage-path-check'
    if ([IO.Path]::GetFullPath([Environment]::GetEnvironmentVariable('HISPANIA_CORE_DATA_DIRECTORY', 'Process')) -cne $dataRootBeforeRestart -or
        -not [IO.File]::Exists($databasePath) -or -not [IO.File]::Exists($packageCatalogPath) -or -not [IO.Directory]::Exists($installedFixturePath)) {
        $failureDetailCode = 'PERSISTED_STORAGE_PATH_OR_FILES_CHANGED'
        throw 'The restarted Core did not retain the expected isolated persistence paths.'
    }
    $stage = 'published-core-restart-module-query'
    $failureDetailCode = 'PERSISTED_MODULE_QUERY_FAILED'
    $row = @(Get-FixtureRow)
    $failureDetailCode = $null
    $stage = 'published-core-restart-module-state-count'
    if ($row.Count -ne 1) { $failureDetailCode = 'PERSISTED_MODULE_ROW_COUNT_UNEXPECTED'; throw 'Persisted module listing did not contain exactly one fixture row.' }
    $stage = 'published-core-restart-module-enabled-state'
    if (-not $row[0].enabled) { $failureDetailCode = 'PERSISTED_MODULE_NOT_ENABLED'; throw 'Persisted fixture was not enabled after restart.' }
    $stage = 'published-core-restart-module-health-state'
    if (-not $row[0].healthy) { $failureDetailCode = 'PERSISTED_MODULE_NOT_HEALTHY'; throw 'Persisted enabled fixture was not healthy after restart.' }
    Add-Result 'published Core restart preserves enabled fixture state and health' $true

    if (-not $RestartOnly) {
    $disable = Invoke-CoreRequest 'POST' ("/api/v1/auth/modules/$fixtureId/disable") -Mutation
    if ($disable.Status -ne 200) {
        $failureDetailCode = Get-SafeHttpFailureCode $disable.Status "/api/v1/auth/modules/$fixtureId/disable" -Mutation -Body $disable.Body
        throw 'Published Core could not disable the fixture.'
    }
    $row = @(Get-FixtureRow)
    if ($row.Count -ne 1 -or $row[0].enabled) { throw 'Fixture remained enabled after disable.' }
    $remove = Invoke-CoreRequest 'DELETE' ("/api/v1/auth/modules/$fixtureId") -Mutation
    if ($remove.Status -ne 204) {
        $failureDetailCode = Get-SafeHttpFailureCode $remove.Status "/api/v1/auth/modules/$fixtureId" -Mutation -Body $remove.Body
        throw 'Published Core did not remove the fixture.'
    }
    if (@(Get-FixtureRow).Count -ne 0) { throw 'Published Core did not remove the fixture.' }
    Add-Result 'fixture disabled and removed through published Core endpoints' $true

    $stage = 'controlled-failure-fixture-package-preparation'
    $failurePackagePath = Join-Path $testRoot 'fixture-controlled-failure.hispania'
    $failurePackage = New-SignedFixturePackage $failureAssembly 'Hispania.Slice7.FailureFixture.FailingFixtureModule' '1.0.1' '1.0.1' $failurePackagePath 'controlled-failure'
    $packageBytes.Add($failurePackage)
    $evidence.failureFixturePackageSha256 = (Get-FileHash -LiteralPath $failurePackagePath -Algorithm SHA256).Hash
    $stage = 'controlled-failure-fixture-install'
    $failureInstall = Invoke-CoreRequest 'POST' '/api/v1/auth/modules/install' -Bytes $failurePackage -Mutation
    if ($failureInstall.Status -ne 201) {
        $failureDetailCode = Get-SafeHttpFailureCode $failureInstall.Status '/api/v1/auth/modules/install' -Mutation -Body $failureInstall.Body
        throw 'Published Core rejected the signed controlled-failure fixture.'
    }
    $stage = 'controlled-fixture-failure-isolation'
    $failureEnable = Invoke-CoreRequest 'POST' ("/api/v1/auth/modules/$fixtureId/enable") -Mutation
    if ($failureEnable.Status -ne 409) { throw 'Controlled fixture failure did not return the expected isolated HTTP 409.' }
    Wait-CoreHealthy
    $failureRow = @(Get-FixtureRow)
    if ($failureRow.Count -ne 1 -or $failureRow[0].enabled -or $failureRow[0].healthy) { throw 'Failure did not leave fixture installed but disabled/unhealthy as expected.' }
    $failureRemove = Invoke-CoreRequest 'DELETE' ("/api/v1/auth/modules/$fixtureId") -Mutation
    if ($failureRemove.Status -ne 204) {
        $failureDetailCode = Get-SafeHttpFailureCode $failureRemove.Status "/api/v1/auth/modules/$fixtureId" -Mutation -Body $failureRemove.Body
        throw 'Could not remove controlled-failure fixture.'
    }
    if (@(Get-FixtureRow).Count -ne 0) { throw 'Could not remove controlled-failure fixture.' }
    Add-Result 'controlled fixture start failure leaves published Core healthy and fixture removable' $true
    }

    $stage = 'published-core-final-stop'
    Stop-PublishedCore
    $coreHasStarted = $false
    Add-Result 'published Core stopped cleanly after CTRL+BREAK' $true
    Assert-AcceptedArtifactIntegrity
    Add-Result 'accepted publish remained byte-for-byte unchanged' $true
    $script:csrfToken = $null
    $adminPassword = $null
    $overallFailure = $null
} catch {
    $errorCode = 'S7-' + ($stage -replace '[^A-Za-z0-9-]', '-').ToUpperInvariant() + '-FAILED'
    if (-not $failureDetailCode) {
        $failureDetailCode = switch -Wildcard ($stage) {
            '*fixture-payload-hash' { 'FIXTURE_PAYLOAD_HASH_FAILED'; break }
            '*fixture-manifest-construction' { 'FIXTURE_MANIFEST_CONSTRUCTION_FAILED'; break }
            '*fixture-rsa-pss-signing' { 'FIXTURE_SIGNATURE_CREATION_FAILED'; break }
            '*fixture-package-staging' { 'FIXTURE_PACKAGE_STAGING_FAILED'; break }
            '*fixture-zip-packaging' { 'FIXTURE_ZIP_PACKAGING_FAILED'; break }
            'failure-fixture-source-preparation' { 'CONTROLLED_FAILURE_SOURCE_PREPARATION_FAILED'; break }
            'failure-fixture-build' { 'CONTROLLED_FAILURE_FIXTURE_BUILD_FAILED'; break }
            'normal-fixture-build' { 'NORMAL_FIXTURE_BUILD_FAILED'; break }
            default { $null }
        }
    }
    $exceptionType = $_.Exception.GetType().Name
    if ($exceptionType -notmatch '^[A-Za-z][A-Za-z0-9]*$') { $exceptionType = 'Exception' }
    $overallFailure = "Stage '$stage' failed ($exceptionType); exception text and sensitive values are omitted."
} finally {
    if ($coreProcessId) {
        try { Stop-PublishedCore -MustBeGraceful:$false } catch { }
    }
    if ($client) { $client.Dispose() }
    if ($rsa) { $rsa.Dispose() }
    if ($certificate) { $certificate.Dispose() }
    $privatePem = $null
    $publicPem = $null
    $adminPassword = $null
    $pfxPassword = $null
    $script:csrfToken = $null
    $login = $null
    $loginJson = $null
    foreach ($name in $envNames) { [Environment]::SetEnvironmentVariable($name, $savedEnvironment[$name], 'Process') }
    foreach ($bytes in $packageBytes) { [Security.Cryptography.CryptographicOperations]::ZeroMemory($bytes) }
    if ($lastCoreProcessId) {
        $residualCore = Get-Process -Id $lastCoreProcessId -ErrorAction SilentlyContinue
        $moduleHostPattern = [regex]::Escape($moduleHostDll)
        $residualModuleHosts = @(Get-CimInstance -ClassName Win32_Process -Filter "Name = 'dotnet.exe'" -ErrorAction SilentlyContinue |
            Where-Object { $_.CommandLine -and $_.CommandLine -match $moduleHostPattern -and $baselineModuleHostIds -notcontains [int]$_.ProcessId })
        if ($residualCore -or $residualModuleHosts.Count -gt 0) {
            $results.Add([pscustomobject]@{ check = 'no residual Core or owned ModuleHost processes'; result = 'FAIL' })
            $overallFailure = 'A process from this test remains; retained temporary root for safe owner cleanup: ' + $testRoot
            $testRootCreated = $false
        } elseif ($lastCoreProcessId) {
            $results.Add([pscustomobject]@{ check = 'no residual Core or owned ModuleHost processes'; result = 'PASS' })
        }
    }
    if ($testRootCreated -and [IO.Directory]::Exists($testRoot)) {
        try {
            $resolvedTemp = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
            $resolvedRoot = [IO.Path]::GetFullPath($testRoot)
            if (-not $resolvedRoot.StartsWith($resolvedTemp, [StringComparison]::OrdinalIgnoreCase) -or
                [IO.Path]::GetDirectoryName($resolvedRoot) -ine [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar)) {
                throw 'Owned test-root safety check failed; refusing recursive cleanup.'
            }
            [IO.Directory]::Delete($resolvedRoot, $true)
            if ([IO.Directory]::Exists($resolvedRoot)) { throw 'Owned test-root cleanup verification failed.' }
            $results.Add([pscustomobject]@{ check = 'exclusive temporary directory cleanup'; result = 'PASS' })
        } catch {
            $results.Add([pscustomobject]@{ check = 'exclusive temporary directory cleanup'; result = 'FAIL' })
            $overallFailure = 'Temporary cleanup failed; inspect only this exact path: ' + $testRoot
        }
    }
}

$summary = [ordered]@{
    overall = $(if ($null -eq $overallFailure -and @($results | Where-Object result -eq 'FAIL').Count -eq 0) { 'PASS' } else { 'FAIL' })
    artifact = $artifact
    evidence = $evidence
    checks = @($results)
    failureStage = $(if ($overallFailure) { $stage } else { $null })
    errorCode = $(if ($overallFailure) { $errorCode } else { $null })
    failureDetailCode = $(if ($overallFailure) { $failureDetailCode } else { $null })
    failure = $(if ($overallFailure) { $overallFailure } else { $null })
    secretsPrinted = $false
}
$summary | ConvertTo-Json -Depth 8
if ($summary.overall -ne 'PASS') { exit 1 }
