[CmdletBinding()]
param(
    [string]$ProtectedRoot = 'C:\ProgramData\HispaniaPhase21CI',
    [string]$GenerationPath,
    [string]$CandidateNativeHelper = (Join-Path $PSScriptRoot 'windows-native-service-helper.ps1'),
    [string]$CandidateServiceScript = (Join-Path $PSScriptRoot '../../deployment/windows-service.ps1'),
    [switch]$AuthorizedProtectedRead,
    [switch]$ShowDiff
)

# Read-only audit. This script never elevates, changes ACLs, writes snapshots,
# starts/stops services, or copies candidate code into a privileged directory.
$ErrorActionPreference = 'Stop'
$exitMatch = 0
$exitMismatch = 10
$exitDenied = 20
$exitError = 30

function Write-Section([string]$Title) { Write-Output "`n=== $Title ===" }

function Get-FileHashes([string]$Path) {
    $rawBytes = [IO.File]::ReadAllBytes($Path)
    $rawHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($rawBytes)).ToLowerInvariant()
    # Match the production workflow exactly: Get-Content -Raw, CRLF -> LF,
    # then UTF-8 without BOM before calculating SHA-256.
    $normalizedText = (Get-Content -LiteralPath $Path -Raw).Replace("`r`n", "`n")
    $normalizedBytes = [Text.UTF8Encoding]::new($false).GetBytes($normalizedText)
    $normalizedHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($normalizedBytes)).ToLowerInvariant()
    [pscustomobject]@{ Raw = $rawHash; Normalized = $normalizedHash; Text = $normalizedText }
}

function Get-BehaviorSummary([string]$Text) {
    $summary = [System.Collections.Generic.List[string]]::new()
    $patterns = [ordered]@{
        'Named pipe' = 'NamedPipe|HispaniaPhase21NativeServices'
        'Allowlisted actions' = "action.{0,80}(install|start|stop|restart|status|uninstall)"
        'Service-name restriction' = 'Hispania-Test-|HispaniaServer|HispaniaWorker|ValidateSet'
        'Fixture/path restriction' = 'fixture|InstallationHome|GetFullPath|ProgramData'
        'Service ownership check' = 'Assert-OwnedService|PathName|StartName|Win32_Service'
        'SCM operations' = 'sc\.exe|Start-Service|Stop-Service|Restart-Service|New-Service'
        'ACL/secret handling' = 'icacls|takeown|Get-Acl|Set-Acl|secret'
        'Recovery configuration' = 'failure|restart/'
        'Reparse/hardlink protection' = 'ReparsePoint|Hardlink|Links'
        'Logging' = 'broker\.log|GITHUB_STEP_SUMMARY|Write-EventLog'
    }
    foreach ($entry in $patterns.GetEnumerator()) {
        if ([regex]::IsMatch($Text, $entry.Value, [Text.RegularExpressions.RegexOptions]::IgnoreCase)) { $summary.Add($entry.Key) }
    }
    return @($summary)
}

function Get-CandidateKind([string]$Path, [string]$Text) {
    $leaf = [IO.Path]::GetFileName($Path)
    if ($leaf -match '(?i)native.*service.*helper|windows-native-service-helper') { return 'native' }
    if ($leaf -match '(?i)windows-service') { return 'service' }
    if ($Text -match 'HispaniaPhase21NativeServices' -and $Text -match 'NamedPipeServerStream') { return 'native' }
    if ($Text -match 'HispaniaServer' -and $Text -match 'HispaniaWorker' -and $Text -match 'ValidateSet') { return 'service' }
    return $null
}

function Get-AclReport([string]$Path) {
    try {
        $acl = Get-Acl -LiteralPath $Path
        $rules = @($acl.Access | ForEach-Object {
            '{0} {1} {2} Inherited={3}' -f $_.IdentityReference, $_.AccessControlType, $_.FileSystemRights, $_.IsInherited
        })
        $writeRules = @($acl.Access | Where-Object {
            $_.AccessControlType -eq [Security.AccessControl.AccessControlType]::Allow -and
            [string]$_.FileSystemRights -match '(?i)Write|Modify|FullControl|Delete|ChangePermissions|TakeOwnership|CreateFiles|CreateDirectories|AppendData|WriteData'
        } | ForEach-Object { '{0}: {1} (Inherited={2})' -f $_.IdentityReference, $_.FileSystemRights, $_.IsInherited })
        [pscustomobject]@{
            Path = $Path
            Owner = $acl.Owner
            ProtectedDacl = $acl.AreAccessRulesProtected
            Rules = $rules
            PotentialWriteGrants = $writeRules
            Status = 'read'
        }
    } catch [UnauthorizedAccessException] {
        [pscustomobject]@{ Path = $Path; Owner = '<inaccessible>'; ProtectedDacl = $null; Rules = @(); PotentialWriteGrants = @(); Status = 'access-denied' }
    }
}

function Test-ReparsePath([string]$Path, [string]$Boundary) {
    $current = [IO.Path]::GetFullPath($Path)
    while ($current) {
        if (Test-Path -LiteralPath $current) {
            $item = Get-Item -LiteralPath $current -Force
            if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { return $current }
        }
        $current = [IO.Path]::GetDirectoryName($current)
    }
    return $null
}

try {
    if (-not $IsWindows) { throw 'This audit requires Windows PowerShell semantics.' }
    if ($ShowDiff -and $env:GITHUB_ACTIONS -eq 'true') { throw 'Text diffs are prohibited in GitHub Actions logs; run the audit locally in an authorized administrative session.' }

    Write-Section 'Process identity and integrity'
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    $elevated = $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    [pscustomobject]@{
        Identity = $identity.Name
        UserSid = $identity.User.Value
        IsAdministrator = $elevated
        ProcessId = $PID
        ProcessPath = (Get-Process -Id $PID).Path
        RunnerName = if ($env:RUNNER_NAME) { $env:RUNNER_NAME } else { '<not running inside GitHub Actions>' }
        RunnerUser = if ($env:RUNNER_NAME) { $env:USERNAME } else { '<local session>' }
    } | Format-List | Out-String | Write-Output
    $whoamiGroups = & whoami.exe /groups /fo csv 2>$null
    $integrityLines = @($whoamiGroups | Where-Object { $_ -match 'Mandatory Label|S-1-16-' })
    Write-Output 'Integrity-label groups:'
    if ($integrityLines.Count) { $integrityLines | ForEach-Object { Write-Output $_ } } else { Write-Output '<not reported>' }
    Write-Output 'Process privileges (whoami /priv):'
    & whoami.exe /priv 2>$null | ForEach-Object { Write-Output $_ }
    $repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path

    Write-Section 'Runner process and service inventory (metadata only)'
    $runnerServices = @()
    $hispaniaServices = @()
    try {
        $allServices = @(Get-CimInstance Win32_Service -ErrorAction Stop)
        $runnerServices = @($allServices | Where-Object { $_.Name -match '(?i)actions\.runner|hispania.*runner' -or $_.PathName -match '(?i)actions-runner|actions\.runner' })
        $hispaniaServices = @($allServices | Where-Object { $_.Name -match '(?i)hispania' -or $_.DisplayName -match '(?i)hispania' -or $_.PathName -match '(?i)\\Hispania|Hispania\.(Server|Worker)' })
        Write-Output ('Runner services found: {0}' -f $runnerServices.Count)
        $runnerServices | Select-Object Name, State, StartName, ProcessId | Format-List | Out-String | Write-Output
        foreach ($runnerService in $runnerServices) {
            if ($runnerService.ProcessId -gt 0) {
                $runnerProcess = Get-CimInstance Win32_Process -Filter "ProcessId=$($runnerService.ProcessId)" -ErrorAction SilentlyContinue
                if ($runnerProcess) {
                    [pscustomobject]@{ RunnerService = $runnerService.Name; ProcessId = $runnerProcess.ProcessId; ParentProcessId = $runnerProcess.ParentProcessId; SessionId = $runnerProcess.SessionId; ProcessName = $runnerProcess.Name; Executable = $runnerProcess.ExecutablePath; Mode = if ($runnerProcess.SessionId -eq 0) { 'service-session' } else { 'interactive-session' } } | Format-List | Out-String | Write-Output
                }
            }
        }
        $networkServiceSid = 'S-1-5-20'
        $networkServiceAccount = ([Security.Principal.SecurityIdentifier]::new($networkServiceSid)).Translate([Security.Principal.NTAccount]).Value
        if (@($runnerServices | Where-Object {
            $serviceSid = $null
            try { $serviceSid = ([Security.Principal.NTAccount]::new($_.StartName)).Translate([Security.Principal.SecurityIdentifier]).Value } catch { }
            $serviceSid -eq $networkServiceSid -or $_.StartName -match '(?i)Network Service|NetworkService' -or $_.StartName -ieq $networkServiceAccount
        }).Count -gt 0) {
            Write-Output 'Runner SCM capability: runner service account is NetworkService and is not a local administrator; privileged SCM actions require the separately audited broker. No SCM capability probe was attempted.'
        } elseif ($runnerServices.Count -eq 0) {
            Write-Output 'Runner SCM capability: runner service identity not discovered; determine the actual runner process identity before approval.'
        } else {
            Write-Output 'Runner SCM capability: service account needs administrator review; no SCM capability probe was attempted.'
        }
        Write-Output ('Hispania-related services found: {0}' -f $hispaniaServices.Count)
        $hispaniaServices | Select-Object Name, DisplayName, State, StartName, ProcessId, PathName | Format-List | Out-String | Write-Output
    } catch {
        Write-Output ('Service inventory unavailable: {0}' -f $_.Exception.Message)
    }
    try {
        $allProcesses = @(Get-CimInstance Win32_Process -ErrorAction Stop)
        $hispaniaProcesses = @($allProcesses | Where-Object { $_.Name -match '(?i)hispania' -or $_.ExecutablePath -match '(?i)\\Hispania|Hispania\.(Server|Worker)' })
        Write-Output ('Hispania-related active processes found: {0}' -f $hispaniaProcesses.Count)
        $hispaniaProcesses | Select-Object ProcessId, ParentProcessId, Name, ExecutablePath | Format-List | Out-String | Write-Output
        $brokers = @($allProcesses | Where-Object { $_.Name -match '^(?i:powershell|pwsh)\.exe$' -and $_.CommandLine -match '(?i)windows-native-service-helper\.ps1' -and $_.CommandLine -match '(?i)(-|/)Server\b' })
        Write-Output ('Privileged helper listener processes found: {0}' -f $brokers.Count)
        foreach ($broker in $brokers) {
            $owner = '<unavailable>'
            try { $ownerInfo = Invoke-CimMethod -InputObject $broker -MethodName GetOwner -ErrorAction Stop; if ($ownerInfo.ReturnValue -eq 0) { $owner = $ownerInfo.Domain + '\' + $ownerInfo.User } } catch { }
            $scriptPath = '<unparsed>'
            if ($broker.CommandLine -match '(?i)(?:-File|/File)\s+"([^"]+windows-native-service-helper\.ps1)"') { $scriptPath = $Matches[1] }
            elseif ($broker.CommandLine -match '(?i)(?:-File|/File)\s+([^\s]+windows-native-service-helper\.ps1)') { $scriptPath = $Matches[1].Trim('"') }
            [pscustomobject]@{ ProcessId = $broker.ProcessId; ParentProcessId = $broker.ParentProcessId; SessionId = $broker.SessionId; Owner = $owner; Executable = $broker.ExecutablePath; HelperPath = $scriptPath; CreationDate = $broker.CreationDate } | Format-List | Out-String | Write-Output
        }
    } catch {
        Write-Output ('Process inventory unavailable: {0}' -f $_.Exception.Message)
    }
    $dataRoots = @(
        (Join-Path $env:ProgramData 'Hispania'),
        (Join-Path $env:ProgramData 'HispaniaPhase18CI'),
        (Join-Path $env:ProgramData 'HispaniaPhase21CI'),
        (Join-Path $env:ProgramFiles 'Hispania'),
        (Join-Path $env:LOCALAPPDATA 'Hispania'),
        (Join-Path $env:APPDATA 'Hispania')
    ) | Select-Object -Unique
    foreach ($dataRoot in $dataRoots) {
        if (-not (Test-Path -LiteralPath $dataRoot)) { Write-Output "Hispania data path absent: $dataRoot"; continue }
        Write-Output "Hispania data path present: $dataRoot"
        try {
            # List paths and metadata only; never open a database or configuration file.
            $dbFiles = @(Get-ChildItem -LiteralPath $dataRoot -File -Recurse -Force -ErrorAction SilentlyContinue | Where-Object { $_.Extension -match '(?i)^\.(db|sqlite|sqlite3)$' } | Select-Object -First 100)
            if ($env:GITHUB_ACTIONS -eq 'true') {
                Write-Output ('  database_file_count_at_most_100={0}; file paths suppressed in CI logs; content not inspected.' -f $dbFiles.Count)
            } else {
                foreach ($db in $dbFiles) { Write-Output ('  database_path={0}; length={1}; last_write_utc={2:o}' -f $db.FullName, $db.Length, $db.LastWriteTimeUtc) }
            }
            if ($dbFiles.Count -eq 0) { Write-Output '  no SQLite database files found in this root (content not inspected).' }
        } catch { Write-Output ('  metadata scan unavailable: {0}' -f $_.Exception.Message) }
    }
    $workflowPath = Join-Path $repoRoot '.github/workflows/production-deployment.yml'
    if (Test-Path -LiteralPath $workflowPath) {
        $workflowText = Get-Content -LiteralPath $workflowPath -Raw
        Write-Output ('Workflow concurrency declarations: {0}' -f ((@($workflowText -split "`n" | Where-Object { $_ -match '^\s*(concurrency:|group:|cancel-in-progress:)' }) -join ' | ')))
        Write-Output ('Workflow fixture/helper roots: {0}' -f ((@($workflowText -split "`n" | Where-Object { $_ -match 'HISPANIA_PHASE18_SERVICE_ROOT|HISPANIA_PHASE21_NATIVE_HELPER' }) -join ' | ')))
    }
    Write-Output 'Isolation assessment: inventory is advisory; absence of matching paths/services does not prove this host is disposable or production-free. Do not run SCM unless an administrator verifies isolation independently.'
    Write-Output 'Exclusive service creation capability: NOT TESTED (audit is read-only and does not call SCM).'

    Write-Section 'Candidate Git provenance'
    $gitSha = (& git -C $repoRoot rev-parse HEAD 2>$null | Select-Object -First 1)
    $gitStatus = (& git -C $repoRoot status --porcelain 2>$null)
    [pscustomobject]@{ Repository = $repoRoot; GitSha = $gitSha; WorktreeClean = (-not $gitStatus) } | Format-List | Out-String | Write-Output

    $candidates = @(
        [pscustomobject]@{ Kind = 'native'; Path = (Resolve-Path -LiteralPath $CandidateNativeHelper).Path },
        [pscustomobject]@{ Kind = 'service'; Path = (Resolve-Path -LiteralPath $CandidateServiceScript).Path }
    )
    foreach ($candidate in $candidates) {
        $hashes = Get-FileHashes $candidate.Path
        $relativePath = [IO.Path]::GetRelativePath($repoRoot, $candidate.Path).Replace('\', '/')
        $workingBlob = (& git -C $repoRoot hash-object "--path=$relativePath" $candidate.Path 2>$null | Select-Object -First 1)
        $headBlob = (& git -C $repoRoot rev-parse "$($gitSha):$relativePath" 2>$null | Select-Object -First 1)
        $blobMatchesHead = $workingBlob -and $headBlob -and $workingBlob -ceq $headBlob
        $candidate | Add-Member NoteProperty Hashes $hashes
        $candidate | Add-Member NoteProperty Behavior (Get-BehaviorSummary $hashes.Text)
        $candidate | Add-Member NoteProperty BlobMatchesHead $blobMatchesHead
        Write-Output ('Candidate {0}: {1}' -f $candidate.Kind, $candidate.Path)
        Write-Output ('  raw_sha256={0}; normalized_sha256={1}; bytes={2}; last_write_utc={3:o}' -f $hashes.Raw, $hashes.Normalized, (Get-Item -LiteralPath $candidate.Path).Length, (Get-Item -LiteralPath $candidate.Path).LastWriteTimeUtc)
        Write-Output ('  git_blob_matches_head={0}; working_blob={1}; head_blob={2}; worktree_clean={3}' -f $blobMatchesHead, $workingBlob, $headBlob, (-not $gitStatus))
        try {
            $signature = Get-AuthenticodeSignature -LiteralPath $candidate.Path
            Write-Output ('  authenticode_status={0}; signer_thumbprint={1}' -f $signature.Status, $(if ($signature.SignerCertificate) { $signature.SignerCertificate.Thumbprint } else { '<none>' }))
        } catch { Write-Output ('  authenticode_status=unavailable; reason={0}' -f $_.Exception.Message) }
        Write-Output ('  behavior={0}' -f ($candidate.Behavior -join ', '))
    }

    Write-Section 'Protected directory metadata'
    $rootItem = Get-Item -LiteralPath $ProtectedRoot -Force -ErrorAction Stop
    $rootAcl = Get-AclReport $rootItem.FullName
    $rootAcl | Format-List | Out-String | Write-Output
    $rootAcl.Rules | ForEach-Object { Write-Output "  $_" }
    $rootAcl.PotentialWriteGrants | ForEach-Object { Write-Output "  potential_write_grant: $_" }
    $rootReparse = Test-ReparsePath $rootItem.FullName $rootItem.FullName
    if ($rootReparse) { Write-Output "ERROR: Protected root is a reparse point: $rootReparse"; exit $exitError }

    if (-not $AuthorizedProtectedRead) {
        Write-Output 'Protected contents were not read. Rerun only after an administrator authorizes the read and starts PowerShell elevated:'
        Write-Output ('  pwsh -NoProfile -File "{0}" -ProtectedRoot "{1}" -AuthorizedProtectedRead' -f (Join-Path $PSScriptRoot 'audit-windows-privileged-runner.ps1'), $ProtectedRoot)
        exit $exitDenied
    }
    if (-not $elevated) {
        Write-Output 'Protected contents were not read: this PowerShell process is not elevated as an administrator. No automatic elevation was attempted.'
        Write-Output ('Open elevated PowerShell, verify the checkout is at the approved SHA, then run: pwsh -NoProfile -File "{0}" -ProtectedRoot "{1}" -AuthorizedProtectedRead' -f (Join-Path $PSScriptRoot 'audit-windows-privileged-runner.ps1'), $ProtectedRoot)
        exit $exitDenied
    }

    Write-Section 'Snapshot discovery, ACL, and comparison'
    $scanRoot = $rootItem.FullName
    if ($GenerationPath) {
        $scanRoot = [IO.Path]::GetFullPath($GenerationPath)
    } else {
        $approvalPath = Join-Path $rootItem.FullName 'active-approval.json'
        if (Test-Path -LiteralPath $approvalPath -PathType Leaf) {
            $approval = Get-Content -LiteralPath $approvalPath -Raw | ConvertFrom-Json
            if ([string]$approval.sourceSha -cnotmatch '^[0-9a-f]{40}$') { throw 'Protected approval manifest contains an invalid source SHA.' }
            $scanRoot = Join-Path (Join-Path $rootItem.FullName 'approved') ([string]$approval.sourceSha)
        }
    }
    $scanRoot = [IO.Path]::GetFullPath($scanRoot)
    if (-not $scanRoot.StartsWith($rootItem.FullName.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Generation path escapes the protected root.' }
    if (Test-ReparsePath $scanRoot $rootItem.FullName) { Write-Output 'ERROR: Generation path or one of its ancestors is a reparse point.'; exit $exitError }
    Write-Output "Snapshot discovery root: $scanRoot"
    # Walk only within the configured root and never descend through reparse points.
    $files = [System.Collections.Generic.List[IO.FileInfo]]::new()
    $directories = [System.Collections.Generic.Stack[string]]::new()
    $directories.Push($scanRoot)
    while ($directories.Count -gt 0) {
        $directory = $directories.Pop()
        foreach ($entry in Get-ChildItem -LiteralPath $directory -Force -ErrorAction Stop) {
            if ($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) {
                Write-Output "ERROR: Reparse point inside protected root; not followed: $($entry.FullName)"
                exit $exitError
            }
            if ($entry.PSIsContainer) { $directories.Push($entry.FullName) }
            elseif ($entry.Extension -ieq '.ps1') { $files.Add([IO.FileInfo]$entry) }
        }
    }
    $matches = [System.Collections.Generic.List[object]]::new()
    foreach ($file in $files) {
        $text = Get-Content -LiteralPath $file.FullName -Raw -ErrorAction Stop
        $kind = Get-CandidateKind $file.FullName $text
        if ($kind) { $matches.Add([pscustomobject]@{ Kind = $kind; Path = $file.FullName; Text = $text }) }
    }

    $different = $false
    foreach ($candidate in $candidates) {
        $found = @($matches | Where-Object Kind -eq $candidate.Kind)
        if ($found.Count -ne 1) {
            Write-Output ('ERROR: Expected exactly one {0} snapshot by filename/content signature; found {1}. No snapshot was selected.' -f $candidate.Kind, $found.Count)
            exit $exitError
        }
        $snapshot = $found[0]
        $snapshotHashes = Get-FileHashes $snapshot.Path
        $candidateAcl = Get-AclReport $snapshot.Path
        $parentAcl = Get-AclReport (Split-Path -Parent $snapshot.Path)
        $reparse = Test-ReparsePath $snapshot.Path $rootItem.FullName
        Write-Output ('Snapshot {0}: {1}' -f $snapshot.Kind, $snapshot.Path)
        Write-Output ('  raw_sha256={0}; normalized_sha256={1}; bytes={2}; last_write_utc={3:o}' -f $snapshotHashes.Raw, $snapshotHashes.Normalized, (Get-Item -LiteralPath $snapshot.Path).Length, (Get-Item -LiteralPath $snapshot.Path).LastWriteTimeUtc)
        Write-Output ('  behavior={0}' -f ((Get-BehaviorSummary $snapshotHashes.Text) -join ', '))
        try {
            $signature = Get-AuthenticodeSignature -LiteralPath $snapshot.Path
            Write-Output ('  authenticode_status={0}; signer_thumbprint={1}' -f $signature.Status, $(if ($signature.SignerCertificate) { $signature.SignerCertificate.Thumbprint } else { '<none>' }))
        } catch { Write-Output ('  authenticode_status=unavailable; reason={0}' -f $_.Exception.Message) }
        Write-Output '  file ACL:'
        Write-Output ('    owner={0}; protected_dacl={1}; status={2}' -f $candidateAcl.Owner, $candidateAcl.ProtectedDacl, $candidateAcl.Status)
        $candidateAcl.Rules | ForEach-Object { Write-Output "    $_" }
        $candidateAcl.PotentialWriteGrants | ForEach-Object { Write-Output "    potential_write_grant: $_" }
        Write-Output ('  parent ACL: owner={0}; protected_dacl={1}; status={2}' -f $parentAcl.Owner, $parentAcl.ProtectedDacl, $parentAcl.Status)
        $parentAcl.Rules | ForEach-Object { Write-Output "    $_" }
        $parentAcl.PotentialWriteGrants | ForEach-Object { Write-Output "    potential_write_grant: $_" }
        $directory = Split-Path -Parent $snapshot.Path
        while ($directory -and $directory -ne $rootItem.FullName) {
            $directoryAcl = Get-AclReport $directory
            Write-Output ('  ancestor ACL: path={0}; owner={1}; protected_dacl={2}; status={3}' -f $directoryAcl.Path, $directoryAcl.Owner, $directoryAcl.ProtectedDacl, $directoryAcl.Status)
            $directoryAcl.PotentialWriteGrants | ForEach-Object { Write-Output "    potential_write_grant: $_" }
            $directory = Split-Path -Parent $directory
        }
        $parentAcl.PotentialWriteGrants | ForEach-Object { Write-Output "    potential_write_grant: $_" }
        if ($reparse) { Write-Output "ERROR: Snapshot resolves through a reparse point: $reparse"; exit $exitError }

        if ($snapshotHashes.Normalized -cne $candidate.Hashes.Normalized) {
            $different = $true
            Write-Output ('  RESULT=DIFFERENT candidate_normalized_sha256={0}; protected_normalized_sha256={1}' -f $candidate.Hashes.Normalized, $snapshotHashes.Normalized)
            $candidateLines = @($candidate.Hashes.Text -split "`n")
            $snapshotLines = @($snapshotHashes.Text -split "`n")
            $diff = @(Compare-Object -ReferenceObject $candidateLines -DifferenceObject $snapshotLines -IncludeEqual:$false | Select-Object -First 80)
            Write-Output ('  textual_diff_lines_shown={0} (<= candidate, => protected)' -f $diff.Count)
            if ($ShowDiff) {
                foreach ($change in $diff) {
                    $line = [string]$change.InputObject
                    if ($line -match '(?i)(password|secret|token|private.?key|credential|connectionstring)') { $line = '<redacted sensitive-looking line>' }
                    Write-Output ('    {0} {1}' -f $change.SideIndicator, $line)
                }
            } else { Write-Output '    <diff suppressed; rerun locally with -ShowDiff after confirming the console is private>' }
        } else {
            Write-Output '  RESULT=MATCH'
        }
        if (-not $candidate.BlobMatchesHead -or $gitStatus) {
            $different = $true
            Write-Output '  RESULT=ERROR: candidate files or worktree do not represent a clean exact Git HEAD.'
        }
    }

    if ($different) { exit $exitMismatch }
    exit $exitMatch
} catch [UnauthorizedAccessException] {
    Write-Error 'ACCESS DENIED: Protected snapshot metadata/content could not be read. No elevation was attempted.'
    Write-Output ('Run this script from elevated PowerShell only after administrator authorization: pwsh -NoProfile -File "{0}" -ProtectedRoot "{1}" -AuthorizedProtectedRead' -f (Join-Path $PSScriptRoot 'audit-windows-privileged-runner.ps1'), $ProtectedRoot)
    exit $exitDenied
} catch {
    Write-Error ('AUDIT ERROR: {0}' -f $_.Exception.Message)
    exit $exitError
}
