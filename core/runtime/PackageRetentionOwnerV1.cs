using Hispania.Platform.Contracts.Maintenance;

namespace Hispania.Runtime;

public sealed partial class ModuleExtensionCatalog : IRetentionOwnerV1
{
    public MaintenanceOwnerIdV1 OwnerId => new("platform.packages");
    public RetentionCapabilitiesV1 Capabilities => new(true, false, false, 100, true);

    public async ValueTask<RetentionPlanV1> PlanAsync(RetentionPolicyV1 policy,
        DateTimeOffset evaluatedAtUtc, DateTimeOffset expiresAtUtc, CancellationToken cancellationToken = default)
    {
        policy.Validate(Capabilities);
        await activationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var mutationLock = await AcquirePackageMutationLockAsync(cancellationToken).ConfigureAwait(false);
            EnsureNoIncompleteOperation();
            var candidates = EnumeratePackageRetentionCandidates(policy, evaluatedAtUtc, cancellationToken);
            return new(Guid.NewGuid(), OwnerId, policy, evaluatedAtUtc, expiresAtUtc,
                candidates.Count, candidates.Protected, candidates.Bytes, candidates.Oldest,
                candidates.Newest, "Obsolete owned package artifacts and abandoned staging; current and KnownGood are protected.", []);
        }
        finally { activationGate.Release(); }
    }

    public async ValueTask<RetentionBatchResultV1> ExecuteBatchAsync(RetentionPlanV1 plan,
        CancellationToken cancellationToken = default)
    {
        if (plan.Owner != OwnerId || !plan.Policy.Enabled) throw new InvalidOperationException("Package retention plan is invalid.");
        plan.Policy.Validate(Capabilities);
        var now = DateTimeOffset.UtcNow;
        if (now < plan.EvaluatedAtUtc || now > plan.ExpiresAtUtc)
            throw new InvalidOperationException("Package retention plan expired or clock moved backwards.");
        await activationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var mutationLock = await AcquirePackageMutationLockAsync(cancellationToken).ConfigureAwait(false);
            EnsureNoIncompleteOperation();
            var selected = EnumeratePackageRetentionCandidates(plan.Policy, now, cancellationToken)
                .Items.Take(plan.Policy.BatchSize + 1).ToArray();
            var affected = 0;
            long bytes = 0;
            foreach (var candidate in selected.Take(plan.Policy.BatchSize))
            {
                cancellationToken.ThrowIfCancellationRequested();
                // Re-read the graph before each deletion, including after any previous batch mutation.
                var current = ReadPackageState();
                var previous = ReadState<PackageSetState>(Path.Combine(PackageStateRoot, "previous.json")) ?? new(1, []);
                ValidateStoredGraph(previous);
                var retained = current.Packages.Concat(previous.Packages).ToArray();
                if (candidate.IsStaging)
                {
                    if (!Directory.Exists(candidate.Path) ||
                        (File.GetAttributes(candidate.Path) & FileAttributes.ReparsePoint) != 0 ||
                        !OwnedPackageStaging(candidate.Path) ||
                        DateTimeOffset.UtcNow - Directory.GetLastWriteTimeUtc(candidate.Path) < (plan.Policy.MinimumAge ?? TimeSpan.FromDays(30))) continue;
                    StoragePathGuard.EnsureTreeHasNoReparsePoints(candidate.Path);
                    Directory.Delete(candidate.Path, recursive: true);
                }
                else
                {
                    if (!File.Exists(candidate.Path) ||
                        (File.GetAttributes(candidate.Path) & FileAttributes.ReparsePoint) != 0 ||
                        retained.Any(entry => entry.ArtifactHash.Equals(candidate.Hash, StringComparison.OrdinalIgnoreCase)) ||
                        DateTimeOffset.UtcNow - File.GetLastWriteTimeUtc(candidate.Path) < (plan.Policy.MinimumAge ?? TimeSpan.FromDays(30))) continue;
                    var inspected = await PackageArchiveInspector.InspectAsync(candidate.Path, cancellationToken).ConfigureAwait(false);
                    if (!candidate.Hash.Equals(inspected.ArtifactHash, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("Package artifact digest disagrees with its name.");
                    var relative = inspected.Id + "/" + inspected.Version;
                    if (!retained.Any(entry => entry.RelativeDirectory == relative))
                    {
                        var package = ResolveStoredDirectory(relative);
                        if (loaded.Values.Any(extension => Path.GetFullPath(extension.PackageDirectory) == package))
                            throw new InvalidOperationException("An active generation blocks package cleanup.");
                        if (Directory.Exists(package))
                        {
                            StoragePathGuard.EnsureTreeHasNoReparsePoints(package);
                            var stored = ReadStoredManifest(package);
                            if (!SameManifest(stored, inspected.Manifest))
                                throw new InvalidDataException("Stored package manifest disagrees with its retained artifact.");
                            ValidateBackupPackage(stored, package);
                            EnsurePackageFilesAreOwned(stored, package);
                            Directory.Delete(package, recursive: true);
                        }
                    }
                    File.Delete(candidate.Path);
                }
                affected++;
                bytes = checked(bytes + candidate.Bytes);
            }
            var selectedCount = Math.Min(selected.Length, plan.Policy.BatchSize);
            return new(selectedCount, affected, bytes, selected.Length > plan.Policy.BatchSize,
                selectedCount - affected);
        }
        finally { activationGate.Release(); }
    }

    private PackageRetentionEnumeration EnumeratePackageRetentionCandidates(RetentionPolicyV1 policy,
        DateTimeOffset now, CancellationToken cancellationToken)
    {
        var current = ReadPackageState();
        var previous = ReadState<PackageSetState>(Path.Combine(PackageStateRoot, "previous.json")) ?? new(1, []);
        ValidateStoredGraph(previous);
        var protectedHashes = current.Packages.Concat(previous.Packages)
            .Select(entry => entry.ArtifactHash).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var age = policy.MinimumAge ?? TimeSpan.FromDays(30);
        var items = new List<PackageRetentionCandidate>();
        long candidateCount = 0, protectedCount = 0, bytes = 0;
        DateTimeOffset? oldest = null, newest = null;
        var artifacts = Path.Combine(PackageStateRoot, "artifacts");
        if (Directory.Exists(artifacts))
        {
            if ((File.GetAttributes(artifacts) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Artifact root cannot be a link.");
            foreach (var path in Directory.EnumerateFiles(artifacts, "*.hispania"))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var hash = Path.GetFileNameWithoutExtension(path);
                if (hash.Length != 64 || !hash.All(Uri.IsHexDigit)) continue;
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Package artifact cannot be a link.");
                var stamp = new DateTimeOffset(File.GetLastWriteTimeUtc(path));
                if (protectedHashes.Contains(hash) || now < stamp || now - stamp < age) { protectedCount++; continue; }
                var length = new FileInfo(path).Length;
                AddCandidate(new(path, hash, false, length, stamp));
                bytes = checked(bytes + length);
                oldest = oldest is null || stamp < oldest ? stamp : oldest;
                newest = newest is null || stamp > newest ? stamp : newest;
            }
        }
        var staging = Path.Combine(PackageStateRoot, "staging");
        if (Directory.Exists(staging))
        {
            if ((File.GetAttributes(staging) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Staging root cannot be a link.");
            foreach (var path in Directory.EnumerateDirectories(staging))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Package staging directory cannot be a link.");
                if (!OwnedPackageStaging(path)) continue;
                var stamp = new DateTimeOffset(Directory.GetLastWriteTimeUtc(path));
                if (now < stamp || now - stamp < age) { protectedCount++; continue; }
                AddCandidate(new(path, "", true, 0, stamp));
                oldest = oldest is null || stamp < oldest ? stamp : oldest;
                newest = newest is null || stamp > newest ? stamp : newest;
            }
        }
        return new(candidateCount, protectedCount, bytes, oldest, newest,
            items.OrderBy(item => item.Stamp).ThenBy(item => item.Path, StringComparer.Ordinal).ToArray());

        void AddCandidate(PackageRetentionCandidate candidate)
        {
            candidateCount++;
            if (items.Count < 101) { items.Add(candidate); return; }
            var latest = items.MaxBy(item => item.Stamp)!;
            if (candidate.Stamp < latest.Stamp)
            {
                items.Remove(latest);
                items.Add(candidate);
            }
        }
    }

    private sealed record PackageRetentionCandidate(string Path, string Hash, bool IsStaging, long Bytes, DateTimeOffset Stamp);
    private sealed record PackageRetentionEnumeration(long Count, long Protected, long Bytes,
        DateTimeOffset? Oldest, DateTimeOffset? Newest, IReadOnlyList<PackageRetentionCandidate> Items);

    private static bool OwnedPackageStaging(string path)
    {
        var name = Path.GetFileName(path);
        if (!Guid.TryParseExact(name, "N", out _)) return false;
        var marker = Path.Combine(path, "ownership.marker");
        return File.Exists(marker) && new FileInfo(marker).Length == 32 &&
            (File.GetAttributes(marker) & FileAttributes.ReparsePoint) == 0 &&
            File.ReadAllText(marker) == name;
    }
}
