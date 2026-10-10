[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$FirstDirectory,
    [Parameter(Mandatory)][string]$SecondDirectory
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

$manifestHash = (Get-FileHash -LiteralPath $firstManifestPath -Algorithm SHA256).Hash.ToLowerInvariant()
foreach ($root in @($firstRoot, $secondRoot)) {
    $sidecarPath = $root + '.manifest.sha256'
    $sidecarHash = (Get-Content -LiteralPath $sidecarPath -Raw).Trim()
    $actualHash = (Get-FileHash -LiteralPath (Join-Path $root 'artifact-manifest.json') -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($sidecarHash -cne $actualHash) { throw "Manifest checksum sidecar is invalid: $sidecarPath" }
    $buildEvidence = Get-Content -LiteralPath ($root + '.build-evidence.json') -Raw | ConvertFrom-Json
    if ($buildEvidence.SourceDirty -ne $false -or $buildEvidence.SourceSha -cne $firstManifest.GitSha) { throw "Build evidence does not match clean manifest provenance: $root" }
}

Write-Output "$($firstManifest.TargetRid): $($firstFiles.Count) files are byte-identical at $($firstManifest.GitSha); manifest SHA-256 $manifestHash."
