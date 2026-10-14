[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$FirstDirectory,
    [Parameter(Mandatory)][string]$SecondDirectory,
    [string]$FirstPackage,
    [string]$SecondPackage
)

$ErrorActionPreference = 'Stop'
$firstRoot = (Resolve-Path -LiteralPath $FirstDirectory).Path
$secondRoot = (Resolve-Path -LiteralPath $SecondDirectory).Path
if ($firstRoot -eq $secondRoot) { throw 'Reproducibility comparison requires two separate fresh output directories.' }

function Get-ArtifactFileHashes([string]$Root) {
    $hashes = [ordered]@{}
    foreach ($file in (Get-ChildItem -LiteralPath $Root -File -Recurse -Force | Sort-Object FullName)) {
        $relative = [IO.Path]::GetRelativePath($Root, $file.FullName).Replace('\', '/')
        $hashes[$relative] = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    }
    return $hashes
}

$firstManifestPath = Join-Path $firstRoot 'artifact-manifest.json'
$secondManifestPath = Join-Path $secondRoot 'artifact-manifest.json'
$firstManifest = Get-Content -LiteralPath $firstManifestPath -Raw | ConvertFrom-Json
$secondManifest = Get-Content -LiteralPath $secondManifestPath -Raw | ConvertFrom-Json
if ($firstManifest.SourceDirty -ne $false -or $secondManifest.SourceDirty -ne $false) { throw 'Both artifacts must come from clean source trees.' }
if ($firstManifest.GitSha -cne $secondManifest.GitSha -or $firstManifest.TargetRid -cne $secondManifest.TargetRid) { throw 'Repeated publications must have identical source SHA and target RID.' }

$firstFiles = Get-ArtifactFileHashes $firstRoot
$secondFiles = Get-ArtifactFileHashes $secondRoot
$allPaths = @($firstFiles.Keys + $secondFiles.Keys | Sort-Object -Unique)
$different = @($allPaths | Where-Object { $firstFiles[$_] -cne $secondFiles[$_] })
if ($different.Count -gt 0) { throw "Repeated publication differs for: $($different -join ', ')" }

$firstPackagePath = if ($FirstPackage) { (Resolve-Path -LiteralPath $FirstPackage).Path } else { $firstRoot.TrimEnd([IO.Path]::DirectorySeparatorChar) + '.zip' }
$secondPackagePath = if ($SecondPackage) { (Resolve-Path -LiteralPath $SecondPackage).Path } else { $secondRoot.TrimEnd([IO.Path]::DirectorySeparatorChar) + '.zip' }
foreach ($package in @($firstPackagePath, $secondPackagePath)) {
    if (-not (Test-Path -LiteralPath $package -PathType Leaf)) { throw "Package ZIP is missing: $package" }
    $sidecar = (Get-Content -LiteralPath ($package + '.sha256') -Raw).Trim()
    $actual = (Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($sidecar -cne $actual) { throw "Package checksum sidecar is invalid: $package" }
}
$firstZipHash = (Get-FileHash -LiteralPath $firstPackagePath -Algorithm SHA256).Hash.ToLowerInvariant()
$secondZipHash = (Get-FileHash -LiteralPath $secondPackagePath -Algorithm SHA256).Hash.ToLowerInvariant()
if ($firstZipHash -cne $secondZipHash) { throw "Repeated ZIP archives differ: $firstZipHash != $secondZipHash" }

Add-Type -AssemblyName System.IO.Compression
$firstArchive = [IO.Compression.ZipFile]::OpenRead($firstPackagePath)
$secondArchive = [IO.Compression.ZipFile]::OpenRead($secondPackagePath)
try {
    $firstEntries = @($firstArchive.Entries | ForEach-Object { '{0}|{1}|{2}|{3}' -f $_.FullName, $_.Length, $_.LastWriteTime.ToUniversalTime().ToString('O'), $_.ExternalAttributes })
    $secondEntries = @($secondArchive.Entries | ForEach-Object { '{0}|{1}|{2}|{3}' -f $_.FullName, $_.Length, $_.LastWriteTime.ToUniversalTime().ToString('O'), $_.ExternalAttributes })
    if (($firstEntries -join "`n") -cne ($secondEntries -join "`n")) { throw 'ZIP entry paths, ordering, timestamps, lengths, or attributes differ.' }
    if (@($firstArchive.Entries | Where-Object { $_.FullName.Contains('..') -or $_.FullName.StartsWith('/') }).Count -gt 0) { throw 'ZIP contains an unsafe entry path.' }
} finally { $firstArchive.Dispose(); $secondArchive.Dispose() }

$manifestHash = (Get-FileHash -LiteralPath $firstManifestPath -Algorithm SHA256).Hash.ToLowerInvariant()
foreach ($root in @($firstRoot, $secondRoot)) {
    $sidecarPath = $root + '.manifest.sha256'
    $sidecarHash = (Get-Content -LiteralPath $sidecarPath -Raw).Trim()
    $actualHash = (Get-FileHash -LiteralPath (Join-Path $root 'artifact-manifest.json') -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($sidecarHash -cne $actualHash) { throw "Manifest checksum sidecar is invalid: $sidecarPath" }
    $buildEvidence = Get-Content -LiteralPath ($root + '.build-evidence.json') -Raw | ConvertFrom-Json
    if ($buildEvidence.SourceDirty -ne $false -or $buildEvidence.SourceSha -cne $firstManifest.GitSha) { throw "Build evidence does not match clean manifest provenance: $root" }
}

Write-Output "$($firstManifest.TargetRid): $($firstFiles.Count) files are byte-identical at $($firstManifest.GitSha); manifest SHA-256 $manifestHash; ZIP SHA-256 $firstZipHash; $($firstEntries.Count) canonical entries."
