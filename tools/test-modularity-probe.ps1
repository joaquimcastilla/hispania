param([string]$ResultsRoot = '.artifacts/modularity-probe')

$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$resultsBase = [IO.Path]::GetFullPath((Join-Path $repo $ResultsRoot))
$run = Join-Path $resultsBase ('run-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $run -Force | Out-Null
$privateKeyPath = Join-Path $run 'ephemeral-publisher-private.pem'
$publicKeyPath = Join-Path $run 'ephemeral-publisher-public.pem'
$publisherId = 'audit.ephemeral.publisher'
$rsa = [System.Security.Cryptography.RSA]::Create(2048)
[IO.File]::WriteAllText($privateKeyPath, $rsa.ExportPkcs8PrivateKeyPem())
[IO.File]::WriteAllText($publicKeyPath, $rsa.ExportSubjectPublicKeyInfoPem())
$rsa.Dispose()

$previous = @{
    V1 = $env:HISPANIA_MODULARITY_PROBE_V1_PACKAGE
    V2 = $env:HISPANIA_MODULARITY_PROBE_V2_PACKAGE
    PublicKey = $env:HISPANIA_MODULARITY_PROBE_PUBLIC_KEY
}
try {
    foreach ($version in @('1.0.0', '2.0.0')) {
        $publishDirectory = Join-Path $run "publish-v$($version.Substring(0, 1))"
        $manifest = if ($version -eq '1.0.0') { 'manifest.template.json' } else { 'manifest-v2.template.json' }
        $packagePath = Join-Path $run "greeting-$version.hispania"

        & dotnet publish (Join-Path $repo 'modules/extension-demo/Hispania.ExtensionDemo.csproj') `
            --configuration Release --output $publishDirectory
        if ($LASTEXITCODE -ne 0) { throw "Independent module publish failed for $version." }

        & (Join-Path $repo 'tools/package-extension.ps1') `
            -PublishDirectory $publishDirectory `
            -ManifestTemplate (Join-Path $repo "extensions/experimental-greeting/$manifest") `
            -OutputPath $packagePath `
            -PublisherId $publisherId `
            -SigningKeyPath $privateKeyPath
        if ($LASTEXITCODE -ne 0) { throw "Official package creation failed for $version." }

        $archive = [IO.Compression.ZipFile]::OpenRead($packagePath)
        try {
            $reader = [IO.StreamReader]::new($archive.GetEntry('module.json').Open())
            try { $metadata = $reader.ReadToEnd() | ConvertFrom-Json }
            finally { $reader.Dispose() }
            if ($metadata.publisherId -ne $publisherId -or -not $metadata.signature -or -not $metadata.files.'Hispania.ExtensionDemo.dll') {
                throw "The signed package manifest is incomplete for $version."
            }
        }
        finally { $archive.Dispose() }
    }

    $env:HISPANIA_MODULARITY_PROBE_V1_PACKAGE = Join-Path $run 'greeting-1.0.0.hispania'
    $env:HISPANIA_MODULARITY_PROBE_V2_PACKAGE = Join-Path $run 'greeting-2.0.0.hispania'
    $env:HISPANIA_MODULARITY_PROBE_PUBLIC_KEY = $publicKeyPath
    & dotnet test (Join-Path $repo 'tests/playback/Hispania.Playback.Tests.csproj') `
        --filter FullyQualifiedName~ExtensionAdminHttpIntegrationTests
    if ($LASTEXITCODE -ne 0) { throw 'Strict signed external-module HTTP lifecycle probe failed.' }

    foreach ($package in @('greeting-1.0.0.hispania', 'greeting-2.0.0.hispania')) {
        Get-FileHash -LiteralPath (Join-Path $run $package) -Algorithm SHA256 |
            Select-Object @{Name='Package';Expression={ Split-Path $_.Path -Leaf }}, @{Name='SHA256';Expression={ $_.Hash.ToLowerInvariant() }}
    }
    Write-Output "Probe packages and public test key retained under $run; ephemeral private key is removed."
}
finally {
    $env:HISPANIA_MODULARITY_PROBE_V1_PACKAGE = $previous.V1
    $env:HISPANIA_MODULARITY_PROBE_V2_PACKAGE = $previous.V2
    $env:HISPANIA_MODULARITY_PROBE_PUBLIC_KEY = $previous.PublicKey
    if (Test-Path -LiteralPath $privateKeyPath) { Remove-Item -LiteralPath $privateKeyPath -Force }
}
