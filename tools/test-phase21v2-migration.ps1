[CmdletBinding()]
param([Parameter(Mandatory)][string]$ResultsDirectory)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$results = [IO.Path]::GetFullPath($ResultsDirectory)
New-Item -ItemType Directory -Path $results -Force | Out-Null
$assets = Join-Path $results 'official-rc1'
New-Item -ItemType Directory -Path $assets -Force | Out-Null

$releases = @(
    @{ Rid='win-x64'; Zip='HISPANIA-0.7.0-rc.1-win-x64.zip'; Manifest='HISPANIA-0.7.0-rc.1-win-x64.artifact-manifest.json'; ZipSha='5a83527edc208552372f8452bfbf1037e0fa761b7e13b6c6ae9a13512e2a7eaf'; ManifestSha='61b820965c39aa62e3f835d4f1137956744d576f3911a52979056d67348aa9c5' },
    @{ Rid='linux-x64'; Zip='HISPANIA-0.7.0-rc.1-linux-x64.zip'; Manifest='HISPANIA-0.7.0-rc.1-linux-x64.artifact-manifest.json'; ZipSha='5ab9db0683fe5edd9eb9e5ddb274155758512ce7c0f29d01bff32984b601005d'; ManifestSha='65611934dc81ec38aec97ca8f99155277f98e56fcd6caa7332d6c1834a981335' }
)
$releaseBase = 'https://github.com/joaquimcastilla/hispania/releases/download/v0.7.0-rc.1'
foreach ($release in $releases) {
    foreach ($name in @($release.Zip, $release.Manifest)) {
        $path = Join-Path $assets $name
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
            Invoke-WebRequest -Uri "$releaseBase/$name" -OutFile $path
        }
    }
    $actualZip = (Get-FileHash -LiteralPath (Join-Path $assets $release.Zip) -Algorithm SHA256).Hash.ToLowerInvariant()
    $actualManifest = (Get-FileHash -LiteralPath (Join-Path $assets $release.Manifest) -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actualZip -cne $release.ZipSha -or $actualManifest -cne $release.ManifestSha) { throw "Official $($release.Rid) RC1 ZIP/manifest digest mismatch." }
    "{0} ZIP SHA-256 {1}; manifest SHA-256 {2}" -f $release.Rid, $actualZip, $actualManifest | Tee-Object -FilePath (Join-Path $results 'verified-hashes.txt') -Append
}

$hostRid = if ($IsWindows -or $env:OS -eq 'Windows_NT') { 'win-x64' } else { 'linux-x64' }
$selected = $releases | Where-Object Rid -eq $hostRid
$sourceZip = Join-Path $assets $selected.Zip
$sourceDirectory = Join-Path $results 'official-rc1-extracted'
if (Test-Path -LiteralPath $sourceDirectory) { Remove-Item -LiteralPath $sourceDirectory -Recurse -Force }
New-Item -ItemType Directory -Path $sourceDirectory | Out-Null
Expand-Archive -LiteralPath $sourceZip -DestinationPath $sourceDirectory
$externalManifest = Get-Content -LiteralPath (Join-Path $assets $selected.Manifest) -Raw | ConvertFrom-Json
$embeddedManifest = Get-Content -LiteralPath (Join-Path $sourceDirectory 'artifact-manifest.json') -Raw | ConvertFrom-Json
if ($embeddedManifest.gitSha -cne '9b5abefe4c6b631360d505587748aedcb6c522d0' -or $embeddedManifest.distributionVersion -cne '0.7.0-rc.1' -or ($embeddedManifest.distributionType -and $embeddedManifest.distributionType -cne 'complete') -or $embeddedManifest.sourceDirty -ne $false) { throw 'Extracted RC1 manifest identity does not match the pinned official Complete RC1 publication.' }
if ($externalManifest.gitSha -cne $embeddedManifest.gitSha -or $externalManifest.files.Count -ne $embeddedManifest.files.Count) { throw 'External and embedded RC1 manifests do not describe the same artifact.' }

$platformDirectory = Join-Path $results "platform-$hostRid"
if (Test-Path -LiteralPath $platformDirectory) { Remove-Item -LiteralPath $platformDirectory -Recurse -Force }
$platformPackage = $platformDirectory + '.zip'
foreach ($stale in @($platformPackage, $platformPackage + '.sha256', $platformDirectory + '.manifest.sha256', $platformDirectory + '.build-evidence.json')) {
    if (Test-Path -LiteralPath $stale -PathType Leaf) { Remove-Item -LiteralPath $stale -Force }
}
& (Join-Path $repoRoot 'tools/publish-platform-only.ps1') -Rid $hostRid -OutputDirectory $platformDirectory 2>&1 | Tee-Object -FilePath (Join-Path $results 'platform-publish.log')
if ($LASTEXITCODE -ne 0) { throw "Platform-only $hostRid publication failed with exit code $LASTEXITCODE." }

$env:HISPANIA_PHASE21V_RC1_ARTIFACT = $sourceDirectory
$env:HISPANIA_PHASE21V_PLATFORM_ARTIFACT = $platformDirectory
Push-Location $repoRoot
try {
    & dotnet test tests/deployment-process/Hispania.Deployment.ProcessTests.csproj -c Release --filter 'FullyQualifiedName~OfficialCompleteRc1MigratesToPlatformOnlyAndCanRestoreTheCompleteFixture' --results-directory $results --logger 'trx;LogFileName=phase21v2-migration.trx' 2>&1 | Tee-Object -FilePath (Join-Path $results 'migration-test.log')
    if ($LASTEXITCODE -ne 0) { throw "Migration acceptance test failed with exit code $LASTEXITCODE." }
    $trxPath = Join-Path $results 'phase21v2-migration.trx'
    [xml]$trx = Get-Content -LiteralPath $trxPath -Raw
    $unitResults = @($trx.TestRun.Results.UnitTestResult)
    if ($unitResults.Count -ne 1 -or @($unitResults | Where-Object { $_.outcome -ne 'Passed' }).Count -ne 0) {
        throw "Expected exactly one required migration test to pass with no skips; found $($unitResults.Count) result(s)."
    }
    "Required migration gate: $($unitResults[0].outcome); count=$($unitResults.Count); skipped=0" | Tee-Object -FilePath (Join-Path $results 'required-test-summary.txt')
} finally { Pop-Location }
