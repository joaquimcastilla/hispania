using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Hispania.ModuleSystem.Contracts;

namespace Hispania.Runtime;

public enum PackageOperationStage { Planned, Staged, Draining, Committed, Activating, Validated, Completed, RollingBack, Failed }
public sealed record PackageSetEntry(ModuleManifest Manifest, string ArtifactHash, string RelativeDirectory, bool Enabled, bool Authorized);
public sealed record PackageSetState(int Generation, PackageSetEntry[] Packages);
public sealed record PackageOperationJournal(int Generation, string OperationId, PackageOperationStage Stage, PackageSetState Previous, PackageSetState Next);
public sealed record PackageGraphDiagnostic(string PackageId, string Version, string Status, string[] Dependencies, string[] Dependents)
{
    public bool Installed { get; init; }
    public bool Trusted { get; init; }
    public bool Authorized { get; init; }
    public bool Enabled { get; init; }
    public bool Healthy { get; init; }
}

public sealed partial class ModuleExtensionCatalog
{
    private static readonly JsonSerializerOptions StateJson = new(JsonSerializerDefaults.Web)
    { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, Converters = { new JsonStringEnumConverter() } };
    private static readonly Meter PackageMeter = new("Hispania.Runtime.Packages", "1.0");
    private static readonly Counter<long> PackageSuccess = PackageMeter.CreateCounter<long>("hispania.packages.operations.success");
    private static readonly Counter<long> PackageFailure = PackageMeter.CreateCounter<long>("hispania.packages.operations.failure");
    private static readonly Counter<long> ResolutionFailure = PackageMeter.CreateCounter<long>("hispania.packages.resolution.failure");
    private static readonly Counter<long> Rollbacks = PackageMeter.CreateCounter<long>("hispania.packages.rollback");
    private static readonly Histogram<double> PackageDuration = PackageMeter.CreateHistogram<double>("hispania.packages.operation.duration", "ms");
    private string PackageStateRoot => Path.Combine(directory, ".package-state");
    private string PackageCatalogPath => Path.Combine(PackageStateRoot, "catalog.json");
    private string PackageJournalPath => Path.Combine(PackageStateRoot, "journal.json");

    /// <summary>Fault checkpoints for deterministic tests, including real process termination. Never receives secrets.</summary>
    public Action<PackageOperationStage>? PackageOperationCheckpoint { get; set; }
    /// <summary>TEST ONLY storage failures: stage-write, catalog-write or journal-write.</summary>
    public Action<string>? PackageStorageCheckpoint { get; set; }

    public async Task<PackageOperationPlan> PlanPackageSetAsync(IReadOnlyList<string> localArchives,
        IReadOnlyDictionary<string, string> targets, IReadOnlySet<string>? remove = null, CancellationToken cancellationToken = default)
    {
        var current = ReadPackageState();
        foreach (var entry in current.Packages)
        {
            var installed = ReadStoredManifest(ResolveStoredDirectory(entry.RelativeDirectory));
            if (!SameManifest(installed, entry.Manifest)) throw new InvalidDataException("Installed manifest differs from authoritative package state.");
            ValidateBackupPackage(installed, ResolveStoredDirectory(entry.RelativeDirectory));
        }
        var supplied = new List<PackageCandidate>();
        var rejected = new List<PackageFinding>();
        foreach (var path in localArchives)
        {
            var candidate = await PackageArchiveInspector.InspectAsync(path, cancellationToken).ConfigureAwait(false);
            var error = ValidateArchiveHost(candidate.Manifest);
            var status = GetPublisherStatus(candidate.Manifest);
            var eligible = error is null && status is not (PublisherSignatureStatus.Invalid or PublisherSignatureStatus.RevokedPublisher) &&
                (!requirePublisherSignature || status == PublisherSignatureStatus.Trusted);
            if (!eligible) rejected.Add(new(error?.Contains("ModuleHost", StringComparison.Ordinal) == true ? "ProtocolIncompatible" : error is not null ? "HostIncompatible" : "UntrustedDependency",
                candidate.Id, null, error ?? "Package publisher signature/trust policy rejected this candidate."));
            supplied.Add(candidate with { Eligible = eligible });
        }
        var installedCandidates = current.Packages.Select(entry =>
        {
            var candidate = ToCandidate(entry);
            var status = GetPublisherStatus(entry.Manifest);
            return candidate with { Eligible = ValidateArchiveHost(entry.Manifest) is null && status is not (PublisherSignatureStatus.Invalid or PublisherSignatureStatus.RevokedPublisher) &&
                (!requirePublisherSignature || status == PublisherSignatureStatus.Trusted) };
        }).ToArray();
        var plan = PackageDependencyResolver.Plan(installedCandidates, supplied, targets, remove);
        if (!plan.Resolved) { ResolutionFailure.Add(1); if (rejected.Count > 0) plan = plan with { Findings = plan.Findings.Concat(rejected).ToArray() }; }
        if (plan.Resolved)
        {
            var ownership = plan.Selected.Where(candidate => current.Packages.Any(old => old.Manifest.Id == candidate.Id && old.Manifest.PublisherId != candidate.Manifest.PublisherId)).ToArray();
            if (ownership.Length > 0) return plan with { Findings = ownership.Select(candidate => new PackageFinding("PublisherOwnership", candidate.Id, null, "Publisher ownership transfer requires an explicit policy; this host has none.")).ToArray() };
        }
        return plan;
    }

    private string? ValidateArchiveHost(ModuleManifest manifest)
    {
        if (!Version.TryParse(manifest.Version, out _) || !Version.TryParse(manifest.MinimumHostVersion, out var minimum) || hostVersion < minimum ||
            manifest.MaximumHostVersion is not null && (!Version.TryParse(manifest.MaximumHostVersion, out var maximum) || hostVersion > maximum)) return "Host compatibility bounds rejected.";
        if (manifest.Runtime is not ("dotnet-inprocess-trusted" or "dotnet-outofprocess")) return "Unsupported extension runtime.";
        if (manifest.RequiredModuleHostProtocol is not null && manifest.RequiredModuleHostProtocol != ModuleWorkerProtocolV1.Version) return "Unsupported ModuleHost protocol generation requirement.";
        if (manifest.Requires.Any(requirement => requirement.StartsWith(WebContributionSchema.Capability + "@", StringComparison.Ordinal) && requirement != WebContributionSchema.Requirement))
            return "Unsupported required Dynamic Web capability generation.";
        if (RequiresWeb(manifest) && !SupportsWeb) return "Required Dynamic Web host capability is unavailable.";
        return null;
    }

    public Task ApplyPackageSetAsync(PackageOperationPlan plan, IReadOnlySet<string>? enabledIds = null,
        bool allowDowngrade = false, CancellationToken cancellationToken = default)
        => ApplyPackageSetCoreAsync(plan, enabledIds, allowDowngrade, cancellationToken);

    /// <summary>Apply one operator enable policy change against the authoritative state under the mutation gate.</summary>
    public async Task SetPackageEnabledAsync(string id, bool enabled, bool uninstall = false, CancellationToken cancellationToken = default)
    {
        PackageDependencyResolver.ValidateId(id);
        var plan = await PlanPackageSetAsync([], new Dictionary<string, string>(), uninstall ? new HashSet<string>(StringComparer.Ordinal) { id } : null, cancellationToken).ConfigureAwait(false);
        await ApplyPackageSetCoreAsync(plan, null, false, cancellationToken, (id, enabled && !uninstall)).ConfigureAwait(false);
    }

    private async Task ApplyPackageSetCoreAsync(PackageOperationPlan plan, IReadOnlySet<string>? enabledIds,
        bool allowDowngrade, CancellationToken cancellationToken, (string Id, bool Enabled)? enabledChange = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        await activationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var timer = Stopwatch.StartNew();
        PackageOperationJournal? journal = null;
        try
        {
            using var mutationLock = await AcquirePackageMutationLockAsync(cancellationToken).ConfigureAwait(false);
            EnsureNoIncompleteOperation();
            if (!plan.Resolved) throw new InvalidOperationException("Unresolved package operation plan.");
            if (!allowDowngrade && plan.Changes.Any(change => change.Action == "downgrade")) throw new InvalidOperationException("Deliberate downgrade requires explicit operator consent and module data compatibility.");
            var previous = ReadPackageState();
            if (!SameSet(previous.Packages.Select(ToCandidate), plan.Current)) throw new InvalidOperationException("Stale package operation plan.");
            if (plan.Selected.Any(candidate => loaded.ContainsKey(candidate.Id) && !previous.Packages.Any(entry => entry.Manifest.Id == candidate.Id)))
                throw new InvalidOperationException("Unload the legacy generation before explicitly adopting its original archive into the managed graph.");
            await AuditPackageOperationAsync("apply", "attempt", "package-set").ConfigureAwait(false);
            var operationId = Guid.NewGuid().ToString("N");
            var desiredEnabled = enabledIds ?? previous.Packages.Where(entry => entry.Enabled).Select(entry => entry.Manifest.Id).ToHashSet(StringComparer.Ordinal);
            if (enabledChange is { } policyChange)
            {
                if (!previous.Packages.Any(entry => entry.Manifest.Id == policyChange.Id)) throw new InvalidOperationException("Package policy target is not installed.");
                var changed = desiredEnabled.ToHashSet(StringComparer.Ordinal);
                if (policyChange.Enabled) changed.Add(policyChange.Id); else changed.Remove(policyChange.Id);
                desiredEnabled = changed;
            }
            if (desiredEnabled.Any(id => !plan.Selected.Any(candidate => candidate.Id == id))) throw new InvalidOperationException("Enabled ID is not in the selected set.");
            var next = new PackageSetState(1, plan.Selected.Select(candidate => new PackageSetEntry(candidate.Manifest, candidate.ArtifactHash,
                Path.Combine(candidate.Id, candidate.Version).Replace('\\', '/'), desiredEnabled.Contains(candidate.Id), allowlist.Contains(candidate.Id))).ToArray());
            ValidateStoredGraph(next);
            if (next.Packages.Any(entry => previous.Packages.Any(old => old.Manifest.Id == entry.Manifest.Id && old.Manifest.PublisherId != entry.Manifest.PublisherId)))
                throw new InvalidOperationException("Publisher ownership transfer is not supported.");
            journal = new(1, operationId, PackageOperationStage.Planned, previous, next);
            WriteJournal(journal);
            var stageRoot = Path.Combine(PackageStateRoot, "staging", operationId);
            foreach (var candidate in plan.Selected)
            {
                if (previous.Packages.Any(entry => entry.ArtifactHash == candidate.ArtifactHash && entry.Manifest.Id == candidate.Id)) continue;
                var inspected = await PackageArchiveInspector.InspectAsync(candidate.Location, cancellationToken).ConfigureAwait(false);
                if (inspected.ArtifactHash != candidate.ArtifactHash || !SameManifest(inspected.Manifest, candidate.Manifest)) throw new InvalidDataException("Package changed since planning.");
                if (ValidateArchiveHost(inspected.Manifest) is not null) throw new InvalidDataException("Package host compatibility rejected.");
                PackageStorageCheckpoint?.Invoke("stage-write");
                var stagedInstaller = new ExtensionPackageInstaller(stageRoot, hostVersion, [], _ => null);
                await using var archive = File.OpenRead(candidate.Location);
                var staged = await stagedInstaller.InstallAsync(archive, cancellationToken).ConfigureAwait(false);
                ValidateBackupPackage(staged.Manifest, staged.Directory);
                ValidateRequiredWeb(staged.Manifest, staged.Directory);
                var artifact = Path.Combine(stageRoot, candidate.ArtifactHash + ".hispania");
                await CopyDurableAsync(candidate.Location, artifact, cancellationToken).ConfigureAwait(false);
                await using var retainedBytes = File.OpenRead(artifact);
                if (Convert.ToHexString(await SHA256.HashDataAsync(retainedBytes, cancellationToken).ConfigureAwait(false)) != candidate.ArtifactHash)
                    throw new InvalidDataException("Candidate changed during staging.");
            }
            journal = journal with { Stage = PackageOperationStage.Staged }; WriteJournal(journal);
            // No runtime or catalog changes before every candidate has passed staging.
            journal = journal with { Stage = PackageOperationStage.Draining }; WriteJournal(journal);
            await StopPackageSetAsync(previous, cancellationToken).ConfigureAwait(false);
            foreach (var entry in next.Packages)
            {
                var destination = ResolveStoredDirectory(entry.RelativeDirectory);
                var staged = Path.Combine(stageRoot, entry.RelativeDirectory.Replace('/', Path.DirectorySeparatorChar));
                if (!Directory.Exists(staged)) continue;
                if (Directory.Exists(destination))
                {
                    var existing = ReadStoredManifest(destination);
                    if (!SameManifest(existing, entry.Manifest)) throw new InvalidDataException("Existing package directory has conflicting bytes.");
                    ValidateBackupPackage(existing, destination);
                }
                else { Directory.CreateDirectory(Path.GetDirectoryName(destination)!); Directory.Move(staged, destination); }
                var artifacts = Path.Combine(PackageStateRoot, "artifacts"); Directory.CreateDirectory(artifacts);
                var retained = Path.Combine(artifacts, entry.ArtifactHash + ".hispania");
                if (!File.Exists(retained)) File.Move(Path.Combine(stageRoot, entry.ArtifactHash + ".hispania"), retained);
            }
            WriteState(PackageCatalogPath, next);
            journal = journal with { Stage = PackageOperationStage.Committed }; WriteJournal(journal);
            journal = journal with { Stage = PackageOperationStage.Activating }; WriteJournal(journal);
            await StartPackageSetAsync(next, cancellationToken).ConfigureAwait(false);
            journal = journal with { Stage = PackageOperationStage.Validated }; WriteJournal(journal);
            foreach (var change in plan.Changes.Where(change => change.Action != "unchanged"))
                await AuditPackageOperationAsync(change.Action, "success", change.PackageId).ConfigureAwait(false);
            foreach (var entry in next.Packages.Where(entry => previous.Packages.FirstOrDefault(old => old.Manifest.Id == entry.Manifest.Id)?.Enabled != entry.Enabled))
                await AuditPackageOperationAsync(entry.Enabled ? "enable" : "disable", "success", entry.Manifest.Id).ConfigureAwait(false);
            WriteState(Path.Combine(PackageStateRoot, "previous.json"), previous);
            journal = journal with { Stage = PackageOperationStage.Completed }; WriteJournal(journal);
            if (Directory.Exists(stageRoot)) Directory.Delete(stageRoot, recursive: true);
            PackageSuccess.Add(1);
        }
        catch
        {
            PackageFailure.Add(1);
            if (journal is not null)
            {
                try { await RestorePackageSetAsync(journal, CancellationToken.None).ConfigureAwait(false); }
                catch { WriteJournal(journal with { Stage = PackageOperationStage.Failed }); }
            }
            await AuditPackageOperationAsync("apply", "failure", "package-set").ConfigureAwait(false);
            throw;
        }
        finally { PackageDuration.Record(timer.Elapsed.TotalMilliseconds); activationGate.Release(); }
    }

    public async Task RecoverPackageSetAsync(CancellationToken cancellationToken = default)
    {
        await activationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var mutationLock = await AcquirePackageMutationLockAsync(cancellationToken).ConfigureAwait(false);
            var journal = ReadState<PackageOperationJournal>(PackageJournalPath);
            if (journal is not null) ValidateJournal(journal);
            if (journal is not null && journal.Stage != PackageOperationStage.Completed)
                await RestorePackageSetAsync(journal, cancellationToken).ConfigureAwait(false);
            else if (File.Exists(PackageCatalogPath)) await StartPackageSetAsync(ReadPackageState(), cancellationToken).ConfigureAwait(false);
        }
        finally { activationGate.Release(); }
    }

    public async Task RollbackPackageSetAsync(CancellationToken cancellationToken = default)
    {
        await activationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var mutationLock = await AcquirePackageMutationLockAsync(cancellationToken).ConfigureAwait(false);
            EnsureNoIncompleteOperation();
            var previous = ReadState<PackageSetState>(Path.Combine(PackageStateRoot, "previous.json")) ?? throw new InvalidOperationException("No previous known good package set.");
            var current = ReadPackageState();
            await RestorePackageSetAsync(new(1, Guid.NewGuid().ToString("N"), PackageOperationStage.RollingBack, previous, current), cancellationToken).ConfigureAwait(false);
        }
        finally { activationGate.Release(); }
    }

    private async Task RestorePackageSetAsync(PackageOperationJournal journal, CancellationToken cancellationToken)
    {
        WriteJournal(journal with { Stage = PackageOperationStage.RollingBack });
        await StopPackageSetAsync(journal.Next, cancellationToken).ConfigureAwait(false);
        ValidateStoredGraph(journal.Previous);
        foreach (var entry in journal.Previous.Packages) ValidateBackupPackage(ReadStoredManifest(ResolveStoredDirectory(entry.RelativeDirectory)), ResolveStoredDirectory(entry.RelativeDirectory));
        WriteState(PackageCatalogPath, journal.Previous);
        await StartPackageSetAsync(journal.Previous, cancellationToken).ConfigureAwait(false);
        WriteJournal(journal with { Stage = PackageOperationStage.Completed });
        Rollbacks.Add(1);
        await AuditPackageOperationAsync("rollback", "success", "package-set").ConfigureAwait(false);
    }

    private async Task StopPackageSetAsync(PackageSetState state, CancellationToken cancellationToken)
    {
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        bounded.CancelAfter(resourceLimits.MaximumRequestDuration);
        foreach (var id in PackageDependencyResolver.TopologicalOrder(state.Packages.Select(ToCandidate).ToArray()).Reverse())
        {
            if (!loaded.TryGetValue(id, out var extension)) continue;
            extension.StopAccepting();
            try { await extension.DrainAsync(bounded.Token).ConfigureAwait(false); }
            catch { extension.ResumeAccepting(); throw; }
            var health = runtime.GetHealth(id);
            if (health.State is ModuleLifecycleState.Running or ModuleLifecycleState.Degraded) await runtime.StopModuleAsync(id, bounded.Token).ConfigureAwait(false);
            runtime.UnregisterStoppedModule(id);
            lock (syncRoot) loaded.Remove(id);
            var weak = extension.Unload(); extension = null;
            if (!await WaitForUnloadAsync(weak, bounded.Token).ConfigureAwait(false)) throw new InvalidOperationException("Package load context did not unload.");
        }
    }

    private async Task StartPackageSetAsync(PackageSetState state, CancellationToken cancellationToken)
    {
        ValidateStoredGraph(state);
        foreach (var id in PackageDependencyResolver.TopologicalOrder(state.Packages.Select(ToCandidate).ToArray()))
        {
            var entry = state.Packages.Single(entry => entry.Manifest.Id == id);
            if (!entry.Enabled) continue;
            var manifest = ReadStoredManifest(ResolveStoredDirectory(entry.RelativeDirectory));
            if (!SameManifest(manifest, entry.Manifest)) throw new InvalidDataException("Installed manifest differs from committed package state.");
            if (ValidateArchiveHost(manifest) is not null) throw new InvalidOperationException("Installed package is incompatible with this host.");
            if (!allowlist.Contains(id)) throw new InvalidOperationException("Package is not authorized.");
            ValidatePublisherTrust(manifest);
            ValidateActivePackageDependencies(manifest);
            ValidateRequiredWeb(manifest, ResolveStoredDirectory(entry.RelativeDirectory));
            if (loaded.TryGetValue(id, out var existing))
            {
                if (!SameManifest(existing.Manifest, manifest) || !runtime.GetHealth(id).IsHealthy) throw new InvalidOperationException("Loaded package does not match healthy committed state.");
                continue;
            }
            var extension = new DiscoveredExtension(manifest, Path.Combine(ResolveStoredDirectory(entry.RelativeDirectory), "module.json"), true, null, true, null);
            lock (syncRoot) discovered[id] = extension;
            await LoadAndStartAsync(extension, cancellationToken).ConfigureAwait(false);
            if (!runtime.GetHealth(id).IsHealthy) throw new InvalidOperationException("Package activation health gate failed.");
            ValidatePublisherTrust(manifest);
            ValidateActivePackageDependencies(manifest);
        }
    }

    private void ValidateActivePackageDependencies(ModuleManifest manifest, HashSet<string>? checkedIds = null)
    {
        checkedIds ??= new HashSet<string>(StringComparer.Ordinal) { manifest.Id };
        foreach (var dependency in PackageDependencyResolver.Dependencies(manifest))
        {
            if (!loaded.TryGetValue(dependency.PackageId, out var provider) || !provider.Running || !runtime.GetHealth(dependency.PackageId).IsHealthy)
                throw new InvalidOperationException("DependencyUnhealthy: required package is not active and healthy.");
            if (!allowlist.Contains(dependency.PackageId)) throw new InvalidOperationException("UnauthorizedDependency.");
            ValidatePublisherTrust(provider.Manifest);
            if (checkedIds.Add(provider.Manifest.Id)) ValidateActivePackageDependencies(provider.Manifest, checkedIds);
            // Validate just the version here; the committed graph has already validated transitive edges.
            var range = PackageDependencyResolver.ParseRange(dependency.VersionRange);
            if (!range.Satisfies(NuGet.Versioning.NuGetVersion.Parse(PackageDependencyResolver.CandidateVersion(new(provider.Manifest, "active", "")).ToFullString())))
                throw new InvalidOperationException("VersionConflict: active dependency does not satisfy the required range.");
        }
    }

    public IReadOnlyList<PackageGraphDiagnostic> InspectPackageGraph()
    {
        var state = ReadPackageState();
        HashSet<string> loadedIds;
        lock (syncRoot) loadedIds = loaded.Keys.ToHashSet(StringComparer.Ordinal);
        var healthy = runtime.ListHealth().Where(health => health.IsHealthy).Select(health => health.Descriptor.Id).ToHashSet(StringComparer.Ordinal);
        var journal = ReadState<PackageOperationJournal>(PackageJournalPath);
        if (journal is not null) ValidateJournal(journal);
        var result = state.Packages.Select(entry =>
        {
            var status = "Resolved";
            if (!allowlist.Contains(entry.Manifest.Id)) status = "UnauthorizedDependency";
            else if (GetPublisherStatus(entry.Manifest) != PublisherSignatureStatus.Trusted && (entry.Manifest.PackageFormatVersion == 2 || requirePublisherSignature || GetPublisherStatus(entry.Manifest) != PublisherSignatureStatus.NoSignature)) status = "UntrustedDependency";
            else if (ValidateArchiveHost(entry.Manifest) is not null) status = "HostIncompatible";
            else if (entry.Enabled && (!loadedIds.Contains(entry.Manifest.Id) || !healthy.Contains(entry.Manifest.Id))) status = "DependencyUnhealthy";
            foreach (var dependency in PackageDependencyResolver.Dependencies(entry.Manifest))
            {
                var provider = state.Packages.Single(other => other.Manifest.Id == dependency.PackageId);
                if (!allowlist.Contains(dependency.PackageId)) { status = "UnauthorizedDependency"; break; }
                if (GetPublisherStatus(provider.Manifest) != PublisherSignatureStatus.Trusted && (provider.Manifest.PackageFormatVersion == 2 || requirePublisherSignature || GetPublisherStatus(provider.Manifest) != PublisherSignatureStatus.NoSignature)) { status = "UntrustedDependency"; break; }
                if (entry.Enabled && (!provider.Enabled || !loadedIds.Contains(dependency.PackageId) || !healthy.Contains(dependency.PackageId))) status = "DependencyUnhealthy";
            }
            if (journal is not null && journal.Stage != PackageOperationStage.Completed) status = "RecoveryRequired";
            return new PackageGraphDiagnostic(entry.Manifest.Id, entry.Manifest.PackageVersion ?? entry.Manifest.Version, status,
                PackageDependencyResolver.Dependencies(entry.Manifest).Select(dependency => dependency.PackageId + " " + dependency.VersionRange).ToArray(),
                state.Packages.Where(other => PackageDependencyResolver.Dependencies(other.Manifest).Any(dependency => dependency.PackageId == entry.Manifest.Id)).Select(other => other.Manifest.Id).ToArray())
            {
                Installed = Directory.Exists(ResolveStoredDirectory(entry.RelativeDirectory)),
                Trusted = GetPublisherStatus(entry.Manifest) == PublisherSignatureStatus.Trusted,
                Authorized = allowlist.Contains(entry.Manifest.Id), Enabled = entry.Enabled,
                Healthy = loadedIds.Contains(entry.Manifest.Id) && healthy.Contains(entry.Manifest.Id)
            };
        }).ToArray();
        // Propagate existing Phase19 decisions through the same dependency graph, not a Web graph.
        foreach (var id in PackageDependencyResolver.TopologicalOrder(state.Packages.Select(ToCandidate).ToArray()))
        {
            var index = Array.FindIndex(result, row => row.PackageId == id);
            if (result[index].Status != "Resolved") continue;
            var entry = state.Packages.Single(entry => entry.Manifest.Id == id);
            var blocked = PackageDependencyResolver.Dependencies(entry.Manifest).Select(dependency => result.Single(row => row.PackageId == dependency.PackageId)).FirstOrDefault(row => row.Status != "Resolved");
            if (blocked is not null) result[index] = result[index] with { Status = blocked.Status };
        }
        return result;
    }

    private PackageSetState ReadPackageState()
    {
        var state = ReadState<PackageSetState>(PackageCatalogPath);
        if (state is null) return new(1, []);
        ValidateStoredGraph(state);
        return state;
    }

    private PackageCandidate ToCandidate(PackageSetEntry entry) => new(entry.Manifest, entry.ArtifactHash, ResolveStoredDirectory(entry.RelativeDirectory));
    private static bool SameSet(IEnumerable<PackageCandidate> left, IEnumerable<PackageCandidate> right) =>
        left.OrderBy(candidate => candidate.Id, StringComparer.Ordinal).Select(candidate => (candidate.Id, candidate.ArtifactHash))
            .SequenceEqual(right.OrderBy(candidate => candidate.Id, StringComparer.Ordinal).Select(candidate => (candidate.Id, candidate.ArtifactHash)));
    private static bool SameManifest(ModuleManifest left, ModuleManifest right) => ModulePackageSignature.GetSigningBytes(left).AsSpan().SequenceEqual(ModulePackageSignature.GetSigningBytes(right));

    private static void ValidateStoredGraph(PackageSetState state)
    {
        if (state.Generation != 1 || state.Packages is null || state.Packages.Length > PackageDependencyResolver.MaximumPackages)
            throw new InvalidDataException("Unsupported or invalid package catalog generation.");
        foreach (var entry in state.Packages)
        {
            PackageDependencyResolver.ValidateManifest(entry.Manifest);
            if (entry.ArtifactHash.Length != 64 || !entry.ArtifactHash.All(Uri.IsHexDigit) ||
                entry.RelativeDirectory != entry.Manifest.Id + "/" + (entry.Manifest.PackageVersion ?? Version.Parse(entry.Manifest.Version).ToString()))
                throw new InvalidDataException("Invalid package catalog identity or digest.");
        }
        var candidates = state.Packages.Select(entry => new PackageCandidate(entry.Manifest, entry.ArtifactHash, entry.RelativeDirectory)).ToArray();
        var plan = PackageDependencyResolver.Plan(candidates, []);
        if (!plan.Resolved) throw new InvalidDataException("Persisted package graph is unresolved.");
        foreach (var entry in state.Packages.Where(entry => entry.Enabled))
            if (PackageDependencyResolver.Dependencies(entry.Manifest).Any(dependency => !state.Packages.Single(other => other.Manifest.Id == dependency.PackageId).Enabled))
                throw new InvalidOperationException("Enabled package requires a disabled dependency.");
    }

    private string ResolveStoredDirectory(string relative)
    {
        var parts = relative.Split('/');
        if (parts.Length != 2) throw new InvalidDataException("Invalid package catalog path.");
        PackageDependencyResolver.ValidateId(parts[0]);
        if (!Version.TryParse(parts[1], out _)) _ = PackageDependencyResolver.ParseVersion(parts[1]);
        var path = Path.GetFullPath(Path.Combine(directory, parts[0], parts[1]));
        for (var cursor = new DirectoryInfo(path); cursor is not null && cursor.FullName.StartsWith(directory, StringComparison.OrdinalIgnoreCase); cursor = cursor.Parent)
            if (cursor.Exists && (cursor.Attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Package state path contains a link.");
        return path;
    }

    private static ModuleManifest ReadStoredManifest(string path)
    {
        var file = Path.Combine(path, "module.json");
        if (new FileInfo(file).Length > 1024 * 1024) throw new InvalidDataException("Oversized installed manifest.");
        return JsonSerializer.Deserialize<ModuleManifest>(File.ReadAllText(file), JsonOptions) ?? throw new InvalidDataException("Invalid installed manifest.");
    }

    private static T? ReadState<T>(string path) where T : class
    {
        if (!File.Exists(path)) return null;
        if (new FileInfo(path).Length > 4 * 1024 * 1024) throw new InvalidDataException("Package state exceeds the size limit.");
        try
        {
            using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var document = JsonDocument.Parse(input);
            PackageArchiveInspector.RejectDuplicates(document.RootElement);
            return document.RootElement.Deserialize<T>(StateJson) ?? throw new InvalidDataException("Empty package state.");
        }
        catch (JsonException exception) { throw new InvalidDataException("Corrupt or future package state; operator recovery required.", exception); }
    }

    private void WriteState<T>(string path, T state)
    {
        PackageStorageCheckpoint?.Invoke(path == PackageCatalogPath ? "catalog-write" : "journal-write");
        Directory.CreateDirectory(PackageStateRoot);
        var temporary = path + ".new";
        var bytes = JsonSerializer.SerializeToUtf8Bytes(state, StateJson);
        if (bytes.Length > 4 * 1024 * 1024) throw new InvalidDataException("Package catalog/journal exceeds its persistence limit.");
        using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 81920, FileOptions.WriteThrough))
        { file.Write(bytes); file.Flush(flushToDisk: true); }
        File.Move(temporary, path, overwrite: true);
    }

    private void WriteJournal(PackageOperationJournal journal)
    {
        ValidateJournal(journal);
        WriteState(PackageJournalPath, journal);
        PackageOperationCheckpoint?.Invoke(journal.Stage);
    }

    private void EnsureNoIncompleteOperation()
    {
        var journal = ReadState<PackageOperationJournal>(PackageJournalPath);
        if (journal is not null) ValidateJournal(journal);
        if (journal is not null && (journal.Generation != 1 || journal.Stage != PackageOperationStage.Completed)) throw new InvalidOperationException("Package operation recovery required before mutation.");
    }

    private static void ValidateJournal(PackageOperationJournal journal)
    {
        if (journal.Generation != 1 || journal.OperationId is null || journal.OperationId.Length != 32 || !journal.OperationId.All(Uri.IsHexDigit) || !Enum.IsDefined(journal.Stage))
            throw new InvalidDataException("Unsupported or invalid package journal identity/stage.");
        ValidateStoredGraph(journal.Previous); ValidateStoredGraph(journal.Next);
    }

    private async Task<FileStream> AcquirePackageMutationLockAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(PackageStateRoot);
        if ((File.GetAttributes(PackageStateRoot) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Package state directory cannot be a link.");
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        bounded.CancelAfter(resourceLimits.MaximumRequestDuration);
        while (true)
        {
            bounded.Token.ThrowIfCancellationRequested();
            try { return new FileStream(Path.Combine(PackageStateRoot, "mutation.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException exception) when ((exception.HResult & 0xffff) is 11 or 32 or 33)
            {
                // A terminated owner's handle can still be closing. Never steal/delete the lock.
                await Task.Delay(25, bounded.Token).ConfigureAwait(false);
            }
        }
    }

    public async Task StopManagedPackagesAsync(CancellationToken cancellationToken = default)
    {
        await activationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await StopPackageSetAsync(ReadPackageState(), cancellationToken).ConfigureAwait(false); }
        finally { activationGate.Release(); }
    }

    /// <summary>Explicit cleanup retains the current and previous known-good sets; never deletes unrelated drop-in files.</summary>
    public async Task CleanupPackageHistoryAsync(CancellationToken cancellationToken = default)
    {
        await activationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var mutationLock = await AcquirePackageMutationLockAsync(cancellationToken).ConfigureAwait(false);
            EnsureNoIncompleteOperation();
            var current = ReadPackageState();
            var previous = ReadState<PackageSetState>(Path.Combine(PackageStateRoot, "previous.json")) ?? new(1, []);
            ValidateStoredGraph(previous);
            var retained = current.Packages.Concat(previous.Packages).ToArray();
            var hashes = retained.Select(entry => entry.ArtifactHash).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var paths = retained.Select(entry => entry.RelativeDirectory).ToHashSet(StringComparer.Ordinal);
            var artifacts = Path.Combine(PackageStateRoot, "artifacts");
            if (Directory.Exists(artifacts))
            {
                if ((File.GetAttributes(artifacts) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Artifact store cannot be a link.");
                foreach (var file in Directory.EnumerateFiles(artifacts, "*.hispania").Order(StringComparer.Ordinal))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var hash = Path.GetFileNameWithoutExtension(file);
                    if (hash.Length != 64 || !hash.All(Uri.IsHexDigit) || hashes.Contains(hash)) continue;
                    if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Retained artifact cannot be a link.");
                    var candidate = await PackageArchiveInspector.InspectAsync(file, cancellationToken).ConfigureAwait(false);
                    if (!hash.Equals(candidate.ArtifactHash, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Retained artifact hash disagrees with its name.");
                    var relative = candidate.Id + "/" + candidate.Version;
                    if (!paths.Contains(relative))
                    {
                        var package = ResolveStoredDirectory(relative);
                        if (loaded.Values.Any(extension => Path.GetFullPath(extension.PackageDirectory) == package)) throw new InvalidOperationException("An active generation blocks package cleanup.");
                        if (Directory.Exists(package)) Directory.Delete(package, recursive: true);
                    }
                    File.Delete(file);
                }
            }
            var staging = Path.Combine(PackageStateRoot, "staging");
            if (Directory.Exists(staging))
            {
                if ((File.GetAttributes(staging) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Staging root cannot be a link.");
                foreach (var path in Directory.EnumerateDirectories(staging))
                    if (Guid.TryParseExact(Path.GetFileName(path), "N", out _) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0)
                        Directory.Delete(path, recursive: true);
            }
        }
        finally { activationGate.Release(); }
    }

    public static void ValidateBackupPackageSet(string extensionRoot)
    {
        foreach (var name in new[] { "catalog.json", "previous.json" })
        {
            var state = ReadState<PackageSetState>(Path.Combine(extensionRoot, ".package-state", name));
            if (state is null) continue;
            ValidateStoredGraph(state);
            foreach (var entry in state.Packages)
            {
                var path = Path.Combine(extensionRoot, entry.RelativeDirectory.Replace('/', Path.DirectorySeparatorChar));
                var manifest = ReadStoredManifest(path);
                if (!SameManifest(manifest, entry.Manifest)) throw new InvalidDataException("Backup package graph disagrees with installed manifest.");
                ValidateBackupPackage(manifest, path);
                var artifact = Path.Combine(extensionRoot, ".package-state", "artifacts", entry.ArtifactHash + ".hispania");
                using var bytes = File.OpenRead(artifact);
                if (Convert.ToHexString(SHA256.HashData(bytes)) != entry.ArtifactHash) throw new InvalidDataException("Backup package artifact digest mismatch.");
            }
        }
        var journal = ReadState<PackageOperationJournal>(Path.Combine(extensionRoot, ".package-state", "journal.json"));
        if (journal is not null) { ValidateJournal(journal); if (journal.Stage != PackageOperationStage.Completed) throw new InvalidDataException("Package operation must be recovered before offline backup."); }
    }

    private void RejectManagedMutation(string id)
    {
        EnsureNoIncompleteOperation();
        if (ReadPackageState().Packages.Any(entry => entry.Manifest.Id == id))
            throw new InvalidOperationException("This package belongs to a managed dependency graph; use a package-set plan.");
    }

    private void ValidatePackageExecutionJournal()
    {
        var journal = ReadState<PackageOperationJournal>(PackageJournalPath);
        if (journal is null) return;
        ValidateJournal(journal);
        if (journal.Stage is not (PackageOperationStage.Planned or PackageOperationStage.Staged or PackageOperationStage.Completed))
            throw new InvalidOperationException("Package graph cutover/recovery blocks new operations.");
    }

    private static async Task CopyDurableAsync(string source, string destination, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        await using var input = File.OpenRead(source);
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.WriteThrough);
        await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
        await output.FlushAsync(cancellationToken).ConfigureAwait(false); output.Flush(flushToDisk: true);
    }

    private ValueTask AuditPackageOperationAsync(string action, string result, string id) => packageAuditWriter?.WriteAsync(new(
        DateTimeOffset.UtcNow, null, Hispania.Platform.Contracts.Observability.PlatformCorrelationV1.Current?.CorrelationId,
        "platform.packages", action, "package", id, result, new Dictionary<string, string>())) ?? ValueTask.CompletedTask;
}
