[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $ArtifactRoot,

    [Parameter(Mandatory)]
    [string] $BaselinePath,

    [switch] $CreateBaseline,

    [switch] $SelfTest
)

$ErrorActionPreference = 'Stop'

function Get-NormalizedRoot([string] $Path) {
    $resolved = [IO.Path]::GetFullPath($Path)
    return [IO.Path]::TrimEndingDirectorySeparator($resolved)
}

function Test-PathWithin([string] $Path, [string] $Root) {
    $prefix = $Root + [IO.Path]::DirectorySeparatorChar
    return $Path.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)
}

function Get-RelativeArtifactPath([string] $Root, [string] $Path) {
    $relative = [IO.Path]::GetRelativePath($Root, $Path).Replace('\', '/')
    if ([IO.Path]::IsPathRooted($relative) -or $relative -eq '..' -or $relative.StartsWith('../', [StringComparison]::Ordinal)) {
        throw 'Artifact inventory contains a path outside its root.'
    }
    return $relative
}

function Get-ArtifactSnapshot([string] $Root) {
    $rootPath = Get-NormalizedRoot $Root
    if (!(Test-Path -LiteralPath $rootPath -PathType Container)) { throw 'Artifact root is unavailable.' }
    if (([IO.File]::GetAttributes($rootPath) -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw 'Artifact root is a reparse point.'
    }
    $manifestPath = Join-Path $rootPath 'core-artifact-manifest.json'
    if (!(Test-Path -LiteralPath $manifestPath -PathType Leaf)) { throw 'Artifact manifest is missing.' }
    if (([IO.File]::GetAttributes($manifestPath) -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw 'Artifact manifest is a reparse point.'
    }

    $manifestBytes = [IO.File]::ReadAllBytes($manifestPath)
    $manifestHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($manifestBytes))
    $manifest = [Text.Encoding]::UTF8.GetString($manifestBytes) | ConvertFrom-Json
    $expected = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($entry in @($manifest.files)) {
        $relative = ([string]$entry.path).Replace('\', '/')
        if ([string]::IsNullOrWhiteSpace($relative) -or $relative.StartsWith('/') -or $relative.Contains(':') -or
            @($relative.Split('/') | Where-Object { $_ -in @('', '.', '..') }).Count -gt 0 -or
            $expected.ContainsKey($relative)) {
            throw 'Artifact manifest contains an invalid or duplicate path.'
        }
        if ([string]$entry.sha256 -notmatch '^[A-Fa-f0-9]{64}$' -or [long]$entry.size -lt 0) {
            throw 'Artifact manifest contains an invalid size or digest.'
        }
        $expected.Add($relative, [pscustomobject]@{ path = $relative; size = [long]$entry.size; sha256 = ([string]$entry.sha256).ToUpperInvariant() })
    }

    $manifestRelative = 'core-artifact-manifest.json'
    if ($expected.ContainsKey($manifestRelative)) { throw 'Artifact manifest path collides with a listed file.' }
    $actual = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($item in Get-ChildItem -LiteralPath $rootPath -Force -Recurse) {
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw 'Artifact inventory contains a reparse point.'
        }
        if ($item.PSIsContainer) { continue }
        $relative = Get-RelativeArtifactPath $rootPath $item.FullName
        if ($relative -eq $manifestRelative) { continue }
        if ($actual.ContainsKey($relative)) { throw 'Artifact inventory contains duplicate paths.' }
        $actual.Add($relative, [pscustomobject]@{
            path = $relative
            size = [long]$item.Length
            sha256 = (Get-FileHash -LiteralPath $item.FullName -Algorithm SHA256).Hash.ToUpperInvariant()
        })
    }

    $manifestInfo = Get-Item -LiteralPath $manifestPath
    $actual.Add($manifestRelative, [pscustomobject]@{
        path = $manifestRelative
        size = [long]$manifestInfo.Length
        sha256 = $manifestHash
    })

    $added = @($actual.Keys | Where-Object { !$expected.ContainsKey($_) -and $_ -ne $manifestRelative } | Sort-Object)
    $missing = @($expected.Keys | Where-Object { !$actual.ContainsKey($_) } | Sort-Object)
    $modified = [Collections.Generic.List[object]]::new()
    foreach ($path in $expected.Keys) {
        if (!$actual.ContainsKey($path)) { continue }
        $want = $expected[$path]
        $got = $actual[$path]
        if ($want.size -ne $got.size -or $want.sha256 -ne $got.sha256) {
            $modified.Add([pscustomobject]@{ path = $path; expectedSize = $want.size; actualSize = $got.size; expectedSha256 = $want.sha256; actualSha256 = $got.sha256 })
        }
    }
    $unlisted = @($actual.Keys | Where-Object { $_ -ne $manifestRelative -and !$expected.ContainsKey($_) } | Sort-Object)
    return [pscustomobject]@{
        root = $rootPath
        manifest = $manifest
        manifestSha256 = $manifestHash
        files = @($actual.Values | Sort-Object path)
        listedFiles = $expected.Count
        added = $added
        missing = $missing
        modified = @($modified)
        unlisted = $unlisted
    }
}

function Compare-ArtifactToBaseline([string] $Root, [object] $Baseline) {
    $snapshot = Get-ArtifactSnapshot $Root
    $expected = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($entry in @($Baseline.files)) { $expected.Add([string]$entry.path, $entry) }
    $actual = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($entry in @($snapshot.files)) { $actual.Add([string]$entry.path, $entry) }

    $added = @($actual.Keys | Where-Object { !$expected.ContainsKey($_) } | Sort-Object)
    $missing = @($expected.Keys | Where-Object { !$actual.ContainsKey($_) } | Sort-Object)
    $modified = [Collections.Generic.List[object]]::new()
    foreach ($path in $expected.Keys) {
        if (!$actual.ContainsKey($path)) { continue }
        $want = $expected[$path]
        $got = $actual[$path]
        if ([long]$want.size -ne [long]$got.size -or ([string]$want.sha256).ToUpperInvariant() -ne ([string]$got.sha256).ToUpperInvariant()) {
            $modified.Add([pscustomobject]@{ path = $path; expectedSize = [long]$want.size; actualSize = [long]$got.size; expectedSha256 = $want.sha256; actualSha256 = $got.sha256 })
        }
    }
    $identityMatches = $snapshot.manifest.contentId -eq $Baseline.contentId -and
        $snapshot.manifest.sourceCommit -eq $Baseline.sourceCommit -and
        $snapshot.manifest.artifactId -eq $Baseline.artifactId -and
        $snapshot.manifest.targetFramework -eq $Baseline.targetFramework -and
        $snapshot.manifest.runtimeIdentifier -eq $Baseline.runtimeIdentifier -and
        $snapshot.manifest.publishMode -eq $Baseline.publishMode -and
        $snapshot.manifestSha256 -eq $Baseline.manifestSha256
    return [pscustomobject]@{
        passed = ($added.Count -eq 0 -and $missing.Count -eq 0 -and $modified.Count -eq 0 -and $identityMatches)
        contentId = $snapshot.manifest.contentId
        manifestSha256 = $snapshot.manifestSha256
        identityMatches = $identityMatches
        added = $added
        missing = $missing
        modified = @($modified)
    }
}

function Assert-ExpectedDifference([string] $Name, [object] $Result, [string] $Field, [string] $ExpectedPath) {
    if ($Result.passed -or @($Result.$Field | Where-Object { $_ -eq $ExpectedPath }).Count -ne 1) {
        throw "Self-test failed to detect the expected $Name change."
    }
    return [pscustomobject]@{ scenario = $Name; detected = $true }
}

$artifact = Get-NormalizedRoot $ArtifactRoot
$baseline = [IO.Path]::GetFullPath($BaselinePath)
if (Test-PathWithin $baseline $artifact) { throw 'Baseline output must be outside the artifact root.' }
if ($CreateBaseline -and $SelfTest) { throw 'CreateBaseline and SelfTest cannot be combined.' }

if ($CreateBaseline) {
    $snapshot = Get-ArtifactSnapshot $artifact
    if ($snapshot.added.Count -ne 0 -or $snapshot.missing.Count -ne 0 -or $snapshot.modified.Count -ne 0 -or $snapshot.unlisted.Count -ne 0) {
        throw 'Publish inventory does not agree with its manifest; baseline was not written.'
    }
    $byPath = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($file in @($snapshot.files)) { $byPath.Add([string]$file.path, $file) }
    $baselineObject = [ordered]@{
        schemaVersion = 1
        evidenceType = 'HISPANIA-SLICE8-CORE-ARTIFACT-BASELINE'
        capturedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
        artifactLocation = '%TEMP%\hispania-slice7-accepted-20261018-publish'
        artifactId = $snapshot.manifest.artifactId
        sourceCommit = $snapshot.manifest.sourceCommit
        targetFramework = $snapshot.manifest.targetFramework
        runtimeIdentifier = $snapshot.manifest.runtimeIdentifier
        publishMode = $snapshot.manifest.publishMode
        contentId = $snapshot.manifest.contentId
        manifestSha256 = $snapshot.manifestSha256
        manifestListedFileCount = $snapshot.listedFiles
        totalFileCountIncludingManifest = $snapshot.files.Count
        coreSha256 = $byPath['Hispania.Core.exe'].sha256
        coreAssemblySha256 = $byPath['Hispania.Core.dll'].sha256
        moduleHostSha256 = $byPath['module-host/Hispania.ModuleHost.dll'].sha256
        files = @($snapshot.files | ForEach-Object { [pscustomobject]@{ path = $_.path; size = $_.size; sha256 = $_.sha256 } })
    }
    $parent = Split-Path -Parent $baseline
    if (!(Test-Path -LiteralPath $parent -PathType Container)) { [void][IO.Directory]::CreateDirectory($parent) }
    $json = $baselineObject | ConvertTo-Json -Depth 8
    [IO.File]::WriteAllText($baseline, $json + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))
    [pscustomobject]@{ passed = $true; operation = 'baseline-created'; contentId = $baselineObject.contentId; manifestSha256 = $baselineObject.manifestSha256; manifestListedFileCount = $baselineObject.manifestListedFileCount; totalFileCountIncludingManifest = $baselineObject.totalFileCountIncludingManifest; baselinePath = $baseline } | ConvertTo-Json -Depth 5
    exit 0
}

if (!(Test-Path -LiteralPath $baseline -PathType Leaf)) { throw 'Baseline file is unavailable.' }
$baselineObject = Get-Content -Raw -LiteralPath $baseline | ConvertFrom-Json
if ($baselineObject.schemaVersion -ne 1 -or $baselineObject.evidenceType -ne 'HISPANIA-SLICE8-CORE-ARTIFACT-BASELINE') { throw 'Unsupported baseline format.' }
$comparison = Compare-ArtifactToBaseline $artifact $baselineObject
$results = [Collections.Generic.List[object]]::new()
if ($SelfTest) {
    $testRoot = Join-Path ([IO.Path]::GetTempPath()) ('hispania-slice8-baseline-selftest-' + [guid]::NewGuid().ToString('N'))
    try {
        [void][IO.Directory]::CreateDirectory($testRoot)
        $addedCopy = Join-Path $testRoot 'added'
        Copy-Item -LiteralPath $artifact -Destination $addedCopy -Recurse
        [IO.File]::WriteAllText((Join-Path $addedCopy 'probe-added.txt'), 'temporary detector probe')
        $results.Add((Assert-ExpectedDifference 'added-file' (Compare-ArtifactToBaseline $addedCopy $baselineObject) 'added' 'probe-added.txt'))

        $deletedCopy = Join-Path $testRoot 'deleted'
        Copy-Item -LiteralPath $artifact -Destination $deletedCopy -Recurse
        [IO.File]::Delete((Join-Path $deletedCopy 'Hispania.Core.exe'))
        $results.Add((Assert-ExpectedDifference 'deleted-file' (Compare-ArtifactToBaseline $deletedCopy $baselineObject) 'missing' 'Hispania.Core.exe'))

        $modifiedCopy = Join-Path $testRoot 'modified'
        Copy-Item -LiteralPath $artifact -Destination $modifiedCopy -Recurse
        [IO.File]::AppendAllText((Join-Path $modifiedCopy 'Hispania.Core.exe'), 'temporary detector probe')
        $modifiedResult = Compare-ArtifactToBaseline $modifiedCopy $baselineObject
        if ($modifiedResult.passed -or @($modifiedResult.modified | Where-Object path -eq 'Hispania.Core.exe').Count -ne 1) {
            throw 'Self-test failed to detect the expected modified-file change.'
        }
        $results.Add([pscustomobject]@{ scenario = 'modified-file'; detected = $true })
    }
    finally {
        $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
        $resolvedTestRoot = [IO.Path]::GetFullPath($testRoot)
        if (!$resolvedTestRoot.StartsWith($tempRoot.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or
            [IO.Path]::GetFileName($resolvedTestRoot) -notlike 'hispania-slice8-baseline-selftest-*') {
            throw 'Refusing to remove a self-test directory outside its unique temporary root.'
        }
        if (Test-Path -LiteralPath $resolvedTestRoot -PathType Container) { Remove-Item -LiteralPath $resolvedTestRoot -Recurse -Force }
    }
    $results.Add([pscustomobject]@{ scenario = 'self-test-temporary-cleanup'; detected = !(Test-Path -LiteralPath $testRoot) })
}

$result = [pscustomobject]@{
    passed = $comparison.passed
    operation = if ($SelfTest) { 'self-test' } else { 'compare' }
    contentId = $comparison.contentId
    manifestSha256 = $comparison.manifestSha256
    added = $comparison.added
    missing = $comparison.missing
    modified = $comparison.modified
    selfTest = @($results)
}
$result | ConvertTo-Json -Depth 8
if (!$comparison.passed -and !$SelfTest) { exit 1 }
if ($SelfTest -and @($results | Where-Object { !$_.detected }).Count -gt 0) { exit 1 }
exit 0
