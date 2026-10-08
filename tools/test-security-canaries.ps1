param([Parameter(Mandatory)][string]$EvidenceDirectory, [string]$ArtifactDirectory)
$ErrorActionPreference = 'Stop'
$canaries = @('PHASE21_PASSWORD_CANARY','PHASE21_BEARER_CANARY','PHASE21_SESSION_CANARY','PHASE21_POSTGRES_CANARY','PHASE21_MTLS_PRIVATE_KEY_CANARY','PHASE21_BOOTSTRAP_CANARY','PHASE21_SIGNING_PRIVATE_KEY_CANARY','canary-password-never-log','phase21 password canary')
$count = 0
foreach ($directory in @($EvidenceDirectory, $ArtifactDirectory) | Where-Object { $_ }) {
    if (-not (Test-Path -LiteralPath $directory -PathType Container)) { throw 'Canary scan input is missing; evidence is not verified.' }
    foreach ($file in Get-ChildItem -LiteralPath $directory -File -Recurse) {
        if ($directory -eq $EvidenceDirectory -and $file.Extension -notin '.json','.jsonl','.log','.txt','.trx','.xml','.out','.err','.html','.js','.css') { continue }
        $content = [Text.Encoding]::UTF8.GetString([IO.File]::ReadAllBytes($file.FullName))
        foreach ($canary in $canaries) { if ($content.Contains($canary, [StringComparison]::Ordinal)) { throw 'Fake secret canary leaked into output (value and content withheld).' } }
        $count++
    }
}
if ($count -eq 0) { throw 'Canary scan found no evidence files.' }
Write-Output "Security canary scan passed for $count local output files. Remote CI console logs are outside this scan."
