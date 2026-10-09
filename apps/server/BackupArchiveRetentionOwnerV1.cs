using Hispania.Platform.Contracts.Maintenance;

namespace Hispania.Server;

/// <summary>Only immediate, fully verified bundles in an explicitly configured archive are eligible.</summary>
internal sealed class BackupArchiveRetentionOwnerV1 : IRetentionOwnerV1, IDisposable
{
    private readonly string? root;
    private readonly SemaphoreSlim gate = new(1, 1);
    public BackupArchiveRetentionOwnerV1(string? archiveRoot)
    {
        root = string.IsNullOrWhiteSpace(archiveRoot) ? null : Path.GetFullPath(archiveRoot);
    }
    public MaintenanceOwnerIdV1 OwnerId => new("platform.backups");
    public RetentionCapabilitiesV1 Capabilities => new(true, true, false, 100, root is not null);

    public async ValueTask<RetentionPlanV1> PlanAsync(RetentionPolicyV1 policy,
        DateTimeOffset evaluatedAtUtc, DateTimeOffset expiresAtUtc, CancellationToken cancellationToken = default)
    {
        policy.Validate(Capabilities);
        if (root is null)
            return new(Guid.NewGuid(), OwnerId, policy, evaluatedAtUtc, expiresAtUtc, 0, 0, null,
                null, null, "Backup:ArchiveRoot is not configured; managed archive retention is unavailable.", []);
        await gate.WaitAsync(cancellationToken);
        try
        {
            using var archiveLease = BackupInstallation.LockArchive(root);
            var survey = await SurveyAsync(policy, evaluatedAtUtc, cancellationToken);
            var warnings = survey.Warnings.Concat(survey.Candidates.Take(policy.BatchSize)
                .SelectMany(candidate => new[] {
                    "candidate:" + candidate.Name + ":" + candidate.Manifest.BackupId.ToString("D"),
                    "backup:" + candidate.Name + ":" + candidate.Manifest.CreatedUtc.ToUniversalTime().ToString("O")
                        + ":" + candidate.Size + ":verified:candidate:eligible-by-age"
                })).ToArray();
            return new(Guid.NewGuid(), OwnerId, policy, evaluatedAtUtc, expiresAtUtc,
                survey.Candidates.Count, survey.Protected, survey.Candidates.Sum(candidate => candidate.Size),
                survey.Candidates.Count == 0 ? null : survey.Candidates.Min(candidate => candidate.Manifest.CreatedUtc),
                survey.Candidates.Count == 0 ? null : survey.Candidates.Max(candidate => candidate.Manifest.CreatedUtc),
                "Verified archive bundles only; pinned, recent, newest verified and latest pre-upgrade copies remain protected.", warnings);
        }
        finally { gate.Release(); }
    }

    public async ValueTask<RetentionBatchResultV1> ExecuteBatchAsync(RetentionPlanV1 plan,
        CancellationToken cancellationToken = default)
    {
        if (root is null || plan.Owner != OwnerId || !plan.Policy.Enabled)
            throw new InvalidOperationException("Backup archive retention is disabled or plan owner is invalid.");
        plan.Policy.Validate(Capabilities);
        var now = DateTimeOffset.UtcNow;
        if (now < plan.EvaluatedAtUtc || now > plan.ExpiresAtUtc)
            throw new InvalidOperationException("Backup archive retention plan expired or clock moved backwards.");
        await gate.WaitAsync(cancellationToken);
        try
        {
            using var archiveLease = BackupInstallation.LockArchive(root);
            var selected = plan.Warnings.Where(warning => warning.StartsWith("candidate:", StringComparison.Ordinal))
                .Take(plan.Policy.BatchSize).Select(warning => warning.Split(':', 3)).Where(parts => parts.Length == 3).ToArray();
            var affected = 0;
            long bytes = 0;
            foreach (var parts in selected)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var name = parts[1];
                if (!Guid.TryParse(parts[2], out var backupId) || !SafeName(name)) continue;
                var path = Path.Combine(root, name);
                // Re-evaluate the entire archive before every deletion: a new pin or failed new backup
                // must never turn the last verified recovery point into a candidate.
                var survey = await SurveyAsync(plan.Policy, DateTimeOffset.UtcNow, cancellationToken);
                var candidate = survey.Candidates.SingleOrDefault(item => item.Name == name && item.Manifest.BackupId == backupId);
                if (candidate is null || !Directory.Exists(path)) continue;
                using var lease = BackupInstallation.Lock(path);
                var manifest = await BackupInstallation.VerifyAsync(path, cancellationToken);
                if (manifest.BackupId != backupId || File.Exists(path + ".pin")) continue;
                Directory.Delete(path, recursive: true);
                affected++;
                bytes = checked(bytes + candidate.Size);
            }
            // Plans are intentionally limited to one bounded batch; the operator re-plans to continue.
            return new(selected.Length, affected, bytes, false, selected.Length - affected);
        }
        finally { gate.Release(); }
    }

    private async Task<ArchiveSurvey> SurveyAsync(RetentionPolicyV1 policy, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (root is null || !Directory.Exists(root)) return new([], 0, []);
        if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Backup archive root cannot be a link.");
        var verified = new List<ArchiveBundle>();
        var warnings = new List<string>();
        long protectedCount = 0;
        foreach (var path in Directory.EnumerateDirectories(root).Order(StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = Path.GetFileName(path);
            if (name.Contains(".tmp-", StringComparison.Ordinal) && File.Exists(path + ".owned")) continue;
            if (!SafeName(name) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            { protectedCount++; continue; }
            if (verified.Count >= 10000)
                throw new InvalidDataException("Backup archive exceeds the bounded retention inventory.");
            try
            {
                var manifest = await BackupInstallation.VerifyAsync(path, cancellationToken);
                var size = checked(manifest.Files.Sum(file => file.Size));
                verified.Add(new(name, path, manifest, size));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
            {
                protectedCount++;
                if (warnings.Count < 20) warnings.Add("backup:" + name + ":unknown:unknown:verification-failed:protected:invalid-bundle");
            }
        }
        var ordered = verified.OrderByDescending(item => item.Manifest.CreatedUtc)
            .ThenBy(item => item.Name, StringComparer.Ordinal).ToArray();
        var retain = Math.Max(1, policy.MaximumRetainedCount ?? 2);
        var protectedIds = ordered.Take(retain).Select(item => item.Manifest.BackupId).ToHashSet();
        var currentVersion = Version.Parse(BackupInstallation.DistributionVersion());
        var preUpgrade = ordered.FirstOrDefault(item => Version.TryParse(item.Manifest.DistributionVersion, out var version)
            && version < currentVersion);
        if (preUpgrade is not null) protectedIds.Add(preUpgrade.Manifest.BackupId);
        var age = policy.MinimumAge ?? TimeSpan.FromDays(30);
        var candidates = new List<ArchiveBundle>();
        foreach (var item in ordered)
        {
            if (protectedIds.Contains(item.Manifest.BackupId) || File.Exists(item.Path + ".pin") ||
                now < item.Manifest.CreatedUtc || now - item.Manifest.CreatedUtc < age)
            {
                protectedCount++;
                if (warnings.Count < 20)
                {
                    var reason = File.Exists(item.Path + ".pin") ? "pinned"
                        : protectedIds.Contains(item.Manifest.BackupId) ? "newest-or-pre-upgrade"
                        : "too-recent-or-future";
                    warnings.Add("backup:" + item.Name + ":" + item.Manifest.CreatedUtc.ToUniversalTime().ToString("O")
                        + ":" + item.Size + ":verified:protected:" + reason);
                }
                continue;
            }
            candidates.Add(item);
        }
        return new(candidates.OrderBy(item => item.Manifest.CreatedUtc).ThenBy(item => item.Name, StringComparer.Ordinal).ToArray(),
            protectedCount, warnings);
    }

    private static bool SafeName(string name) => name is not ("." or "..") && name.Length is > 0 and <= 128 &&
        name.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.');
    private sealed record ArchiveBundle(string Name, string Path, BackupInstallation.Manifest Manifest, long Size);
    private sealed record ArchiveSurvey(IReadOnlyList<ArchiveBundle> Candidates, long Protected, IReadOnlyList<string> Warnings);
    public void Dispose() => gate.Dispose();
}
