using Hispania.Platform.Contracts.Maintenance;

namespace Hispania.Server;

/// <summary>Owns only marked backup-create staging under the explicit archive root.</summary>
internal sealed class BackupStagingRetentionOwnerV1 : IRetentionOwnerV1
{
    private readonly string? root;
    public BackupStagingRetentionOwnerV1(string? archiveRoot)
        => root = string.IsNullOrWhiteSpace(archiveRoot) ? null : Path.GetFullPath(archiveRoot);
    public MaintenanceOwnerIdV1 OwnerId => new("platform.backup-staging");
    public RetentionCapabilitiesV1 Capabilities => new(true, false, false, 100, root is not null);

    public ValueTask<RetentionPlanV1> PlanAsync(RetentionPolicyV1 policy,
        DateTimeOffset evaluatedAtUtc, DateTimeOffset expiresAtUtc, CancellationToken cancellationToken = default)
    {
        policy.Validate(Capabilities);
        if (root is null)
            return ValueTask.FromResult(new RetentionPlanV1(Guid.NewGuid(), OwnerId, policy, evaluatedAtUtc,
                expiresAtUtc, 0, 0, null, null, null, "Backup:ArchiveRoot is not configured.", []));
        using var lease = BackupInstallation.LockArchive(root);
        var survey = Survey(policy, evaluatedAtUtc, cancellationToken);
        return ValueTask.FromResult(new RetentionPlanV1(Guid.NewGuid(), OwnerId, policy, evaluatedAtUtc,
            expiresAtUtc, survey.Count, survey.Protected, null,
            survey.Oldest, survey.Newest,
            "Only marked abandoned backup-create staging; restore staging and incomplete restore markers are excluded.",
            survey.Candidates.Take(policy.BatchSize).Select(item => "stage:" + item.Name).ToArray()));
    }

    public ValueTask<RetentionBatchResultV1> ExecuteBatchAsync(RetentionPlanV1 plan,
        CancellationToken cancellationToken = default)
    {
        if (root is null || plan.Owner != OwnerId || !plan.Policy.Enabled)
            throw new InvalidOperationException("Backup staging retention is disabled or plan is invalid.");
        plan.Policy.Validate(Capabilities);
        var now = DateTimeOffset.UtcNow;
        if (now < plan.EvaluatedAtUtc || now > plan.ExpiresAtUtc)
            throw new InvalidOperationException("Backup staging plan expired or clock moved backwards.");
        using var lease = BackupInstallation.LockArchive(root); // Active create holds this lock.
        var selected = plan.Warnings.Where(value => value.StartsWith("stage:", StringComparison.Ordinal))
            .Take(plan.Policy.BatchSize).Select(value => value["stage:".Length..]).ToArray();
        var affected = 0;
        foreach (var name in selected)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!ValidStageName(name)) continue;
            var current = Survey(plan.Policy, DateTimeOffset.UtcNow, cancellationToken);
            if (!current.Candidates.Any(candidate => candidate.Name == name)) continue;
            var path = Path.Combine(root, name);
            var examined = 0;
            foreach (var _ in BackupInstallation.EnumerateSafe(path))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (++examined > 65536) throw new InvalidDataException("Backup staging exceeds the deletion safety bound.");
            }
            Directory.Delete(path, recursive: true);
            File.Delete(path + ".owned");
            affected++;
        }
        return ValueTask.FromResult(new RetentionBatchResultV1(selected.Length, affected, null,
            false, selected.Length - affected));
    }

    private SurveyResult Survey(RetentionPolicyV1 policy, DateTimeOffset at, CancellationToken cancellationToken)
    {
        if (root is null || !Directory.Exists(root)) return new(0, [], 0, null, null);
        if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Backup archive root cannot be a link.");
        var candidates = new List<StageCandidate>();
        long candidateCount = 0, protectedCount = 0;
        DateTimeOffset? oldest = null, newest = null;
        var age = policy.MinimumAge ?? TimeSpan.FromDays(1);
        foreach (var path in Directory.EnumerateDirectories(root))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = Path.GetFileName(path);
            if (!ValidStageName(name)) continue;
            var marker = path + ".owned";
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0 ||
                !File.Exists(marker) || (File.GetAttributes(marker) & FileAttributes.ReparsePoint) != 0 ||
                new FileInfo(marker).Length != "HISPANIA_BACKUP_STAGING_V1".Length ||
                File.ReadAllText(marker) != "HISPANIA_BACKUP_STAGING_V1")
            { protectedCount++; continue; }
            var stamp = new DateTimeOffset(Directory.GetLastWriteTimeUtc(path));
            if (at < stamp || at - stamp < age) { protectedCount++; continue; }
            candidateCount++;
            oldest = oldest is null || stamp < oldest ? stamp : oldest;
            newest = newest is null || stamp > newest ? stamp : newest;
            if (candidates.Count < 101) candidates.Add(new(name, stamp));
            else
            {
                var latest = candidates.MaxBy(item => item.Stamp)!;
                if (stamp < latest.Stamp) { candidates.Remove(latest); candidates.Add(new(name, stamp)); }
            }
        }
        return new(candidateCount, candidates.OrderBy(item => item.Stamp).ThenBy(item => item.Name, StringComparer.Ordinal).ToArray(),
            protectedCount, oldest, newest);
    }

    private static bool ValidStageName(string name)
    {
        var index = name.LastIndexOf(".tmp-", StringComparison.Ordinal);
        if (index is < 1 or > 128 || !Guid.TryParseExact(name[(index + 5)..], "N", out _)) return false;
        return name[..index].All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');
    }
    private sealed record StageCandidate(string Name, DateTimeOffset Stamp);
    private sealed record SurveyResult(long Count, IReadOnlyList<StageCandidate> Candidates, long Protected,
        DateTimeOffset? Oldest, DateTimeOffset? Newest);
}
