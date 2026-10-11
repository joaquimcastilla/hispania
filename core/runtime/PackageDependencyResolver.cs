using Hispania.ModuleSystem.Contracts;
using NuGet.Versioning;

namespace Hispania.Runtime;

public sealed record PackageCandidate(ModuleManifest Manifest, string ArtifactHash, string Location, bool Eligible = true)
{
    public string Id => Manifest.Id;
    public string Version => Manifest.PackageVersion ?? Manifest.Version;
}

public sealed record PackageFinding(string Code, string PackageId, string? DependencyId, string Detail);
public sealed record PackageChange(string PackageId, string? PreviousVersion, string Version, string Action);
public sealed record PackageOperationPlan(IReadOnlyList<PackageCandidate> Current, IReadOnlyList<PackageCandidate> Selected,
    IReadOnlyList<PackageChange> Changes, IReadOnlyList<string> ActivationOrder, IReadOnlyList<string> DeactivationOrder,
    IReadOnlyList<PackageFinding> Findings)
{
    public bool Resolved => Findings.Count == 0;
}

/// <summary>Local, deterministic, bounded required-dependency planning. Never writes files or executes code.</summary>
public static class PackageDependencyResolver
{
    public const int MaximumPackages = 128;
    public const int MaximumDependencies = 32;
    public const int MaximumDepth = 32;
    public const int MaximumSearchSteps = 8192;

    public static void ValidateId(string id)
    {
        if (string.IsNullOrEmpty(id) || id.Length > 128 || id[0] is not (>= 'a' and <= 'z' or >= '0' and <= '9') ||
            id[^1] is '.' || id.Any(c => c is not (>= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '-' or '_')))
            throw new InvalidDataException("Package ID must be canonical lowercase ASCII.");
        var stem = id.Split('.')[0];
        if (new[] { "con", "prn", "aux", "nul", "com1", "com2", "com3", "com4", "com5", "com6", "com7", "com8", "com9", "lpt1", "lpt2", "lpt3", "lpt4", "lpt5", "lpt6", "lpt7", "lpt8", "lpt9" }.Contains(stem, StringComparer.Ordinal))
            throw new InvalidDataException("Package ID uses a reserved filesystem name.");
    }

    public static SemanticVersion ParseVersion(string text)
    {
        if (text is null || text.Length > 128 || !SemanticVersion.TryParse(text, out var version))
            throw new InvalidDataException("Invalid strict SemVer 2 package version.");
        return version;
    }

    public static VersionRange ParseRange(string text)
    {
        if (string.IsNullOrEmpty(text) || text.Length > 260 || text.Any(char.IsWhiteSpace) || text.Contains('*') ||
            text[0] is not ('[' or '(') || text[^1] is not (']' or ')'))
            throw new InvalidDataException("Dependency range must use bounded NuGet interval notation.");
        var bounds = text[1..^1].Split(',');
        if (bounds.Length is < 1 or > 2 || bounds.All(string.IsNullOrEmpty)) throw new InvalidDataException("Invalid dependency interval.");
        foreach (var bound in bounds.Where(bound => bound.Length > 0)) _ = ParseVersion(bound);
        if (!VersionRange.TryParse(text, out var range) || range.Float is not null ||
            (bounds.Length == 1 && (text[0] != '[' || text[^1] != ']')) ||
            (range.MinVersion is not null && range.MaxVersion is not null &&
             (VersionComparer.VersionRelease.Compare(range.MinVersion, range.MaxVersion) > 0 ||
              (VersionComparer.VersionRelease.Compare(range.MinVersion, range.MaxVersion) == 0 && (!range.IsMinInclusive || !range.IsMaxInclusive)))))
            throw new InvalidDataException("Invalid dependency interval.");
        return range;
    }

    public static void ValidateManifest(ModuleManifest manifest)
    {
        if (manifest.Implements is null || manifest.Provides is null || manifest.Requires is null || manifest.OptionalDependencies is null || manifest.Dependencies is null || manifest.JobContributions is { Length: > 128 } ||
            manifest.Permissions is null || manifest.SharedAssemblies is null || manifest.Files is null || manifest.ContractTypes is null || manifest.DependencyMinimumVersions is null ||
            new[] { manifest.Implements, manifest.Provides, manifest.Requires, manifest.OptionalDependencies, manifest.Dependencies, manifest.Permissions, manifest.SharedAssemblies }
                .Any(values => values.Length > 256 || values.Any(value => value is null || value.Length > 1024 || value.Any(char.IsControl))) ||
            manifest.Files.Count > 512 || manifest.ContractTypes.Count > 256 || manifest.DependencyMinimumVersions.Count > 256)
            throw new InvalidDataException("Manifest collections are null or exceed security bounds.");
        if (manifest.PackageFormatVersion is not (1 or 2)) throw new InvalidDataException("Unsupported package format generation.");
        if (manifest.JobContributions is { } contributions)
        {
            var jobTypes = new HashSet<string>(StringComparer.Ordinal);
            foreach (var contribution in contributions)
            {
                if (contribution is null || !Hispania.Platform.Contracts.Jobs.PlatformJobHandlerRegistryV1.IsValidJobType(contribution.JobType) ||
                    string.IsNullOrWhiteSpace(contribution.CapabilityId) || contribution.CapabilityId.Length > 256 ||
                    !System.Version.TryParse(contribution.CapabilityVersion, out var contributionVersion) || contributionVersion.Major != 1 ||
                    !manifest.Provides.Contains($"{contribution.CapabilityId}@{contributionVersion}", StringComparer.Ordinal) || !jobTypes.Add(contribution.JobType))
                    throw new InvalidDataException("Module Job contribution declarations are invalid, duplicated, or not provided by the manifest.");
            }
        }
        if (manifest.PackageFormatVersion == 1)
        {
            if (manifest.PackageVersion is not null || manifest.PackageDependencies is not null || manifest.RequiredModuleHostProtocol is not null)
                throw new InvalidDataException("Package dependency metadata requires format 2.");
            if (!System.Version.TryParse(manifest.Version, out _)) throw new InvalidDataException("Invalid historical CLR module version.");
            return;
        }
        ValidateId(manifest.Id);
        if (manifest.RequiredModuleHostProtocol is < 1) throw new InvalidDataException("Invalid ModuleHost protocol generation requirement.");
        _ = ParseVersion(manifest.PackageVersion!);
        if (manifest.PackageDependencies is null || manifest.PackageDependencies.Length > MaximumDependencies)
            throw new InvalidDataException("Format 2 requires a bounded package dependency list.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var dependency in manifest.PackageDependencies)
        {
            if (dependency is null) throw new InvalidDataException("Null package dependency.");
            ValidateId(dependency.PackageId);
            if (dependency.PackageId == manifest.Id || !ids.Add(dependency.PackageId)) throw new InvalidDataException("Self or duplicate package dependency.");
            _ = ParseRange(dependency.VersionRange);
        }
    }

    public static IReadOnlyList<PackageDependency> Dependencies(ModuleManifest manifest)
    {
        if (manifest.PackageFormatVersion == 2) return manifest.PackageDependencies!;
        return manifest.Dependencies.Select(id => new PackageDependency(id,
            manifest.DependencyMinimumVersions.TryGetValue(id, out var minimum) ? $"[{LegacyVersion(minimum)},)" : "[0.0.0,)")).ToArray();
    }

    private static string LegacyVersion(string text)
    {
        var version = System.Version.Parse(text);
        if (version.Revision > 0) throw new InvalidDataException("Historical four-part versions require explicit migration before package graph operations.");
        return $"{version.Major}.{version.Minor}.{Math.Max(0, version.Build)}";
    }

    public static SemanticVersion CandidateVersion(PackageCandidate candidate) => ParseVersion(candidate.Manifest.PackageFormatVersion == 1 ? LegacyVersion(candidate.Version) : candidate.Version);

    private static bool Satisfies(PackageCandidate candidate, string text)
    {
        var range = ParseRange(text);
        var version = CandidateVersion(candidate);
        if (version.IsPrerelease && range.MinVersion?.IsPrerelease != true && range.MaxVersion?.IsPrerelease != true) return false;
        return range.Satisfies(NuGetVersion.Parse(version.ToFullString()));
    }

    public static PackageOperationPlan Plan(IReadOnlyList<PackageCandidate> current, IReadOnlyList<PackageCandidate> supplied,
        IReadOnlyDictionary<string, string>? targets = null, IReadOnlySet<string>? remove = null)
    {
        var findings = new List<PackageFinding>();
        var empty = Array.Empty<PackageCandidate>();
        PackageOperationPlan Failed() => new(current, empty, [], [], [], findings.ToArray());
        if (current.Count + supplied.Count > MaximumPackages * 4 || current.Select(candidate => candidate.Id).Distinct(StringComparer.Ordinal).Count() != current.Count)
        { findings.Add(new("GraphLimit", "", null, "Package candidate count or installed identity uniqueness exceeded.")); return Failed(); }
        var candidates = current.Concat(supplied).Where(candidate => candidate.Eligible).ToArray();
        try
        {
            foreach (var candidate in candidates) { ValidateManifest(candidate.Manifest); _ = CandidateVersion(candidate); }
            foreach (var id in (targets?.Keys ?? []).Concat(remove ?? new HashSet<string>())) ValidateId(id);
            foreach (var range in targets?.Values ?? []) _ = ParseRange(range);
        }
        catch (InvalidDataException) { findings.Add(new("InvalidManifest", "", null, "Invalid package identity, version, format or range.")); return Failed(); }
        foreach (var group in candidates.GroupBy(candidate => (candidate.Id, Version: CandidateVersion(candidate).ToFullString())))
            if (group.Select(candidate => candidate.ArtifactHash).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1)
                findings.Add(new("AmbiguousArtifact", group.Key.Id, null, "Same package identity/version has different bytes."));
        if (findings.Count > 0) return Failed();
        var pool = candidates.GroupBy(candidate => candidate.Id, StringComparer.Ordinal).ToDictionary(group => group.Key,
            group => group.DistinctBy(candidate => candidate.ArtifactHash).OrderByDescending(CandidateVersion, VersionComparer.VersionRelease)
                .ThenBy(candidate => candidate.Version, StringComparer.Ordinal).ThenBy(candidate => candidate.ArtifactHash, StringComparer.Ordinal).ToArray(), StringComparer.Ordinal);
        var roots = current.Select(candidate => candidate.Id).Concat(targets?.Keys ?? []).Distinct(StringComparer.Ordinal)
            .Where(id => remove?.Contains(id) != true).Order(StringComparer.Ordinal).ToArray();
        var constraints = roots.ToDictionary(id => id, id => new List<string> { targets?.GetValueOrDefault(id) ??
            $"[{CandidateVersion(current.Single(candidate => candidate.Id == id)).ToFullString()}]" }, StringComparer.Ordinal);
        var steps = 0;
        PackageFinding? lastFailure = null;
        Dictionary<string, PackageCandidate>? Search(Dictionary<string, PackageCandidate> selected, Dictionary<string, List<string>> required)
        {
            if (++steps > MaximumSearchSteps || required.Count > MaximumPackages) { lastFailure = new("GraphLimit", "", null, "Bounded resolver search exceeded."); return null; }
            foreach (var pair in selected)
                if (required[pair.Key].Any(range => !Satisfies(pair.Value, range))) return null;
            var next = required.Keys.Where(id => !selected.ContainsKey(id)).Order(StringComparer.Ordinal).FirstOrDefault();
            if (next is null)
            {
                try { _ = TopologicalOrder(selected.Values.ToArray()); return selected; }
                catch (InvalidDataException) { lastFailure = new("CycleOrDepth", "", null, "Dependency cycle or graph depth limit exceeded."); return null; }
            }
            if (remove?.Contains(next) == true || !pool.TryGetValue(next, out var available))
            { lastFailure = new("MissingDependency", next, next, "Unresolved dependency."); return null; }
            var matching = available.Where(candidate => required[next].All(range => Satisfies(candidate, range))).ToArray();
            if (matching.Length == 0) lastFailure = new("VersionConflict", next, next, "No local candidate satisfies all required ranges.");
            foreach (var candidate in matching)
            {
                var copy = new Dictionary<string, PackageCandidate>(selected, StringComparer.Ordinal) { [next] = candidate };
                var needs = required.ToDictionary(pair => pair.Key, pair => new List<string>(pair.Value), StringComparer.Ordinal);
                foreach (var dependency in Dependencies(candidate.Manifest))
                {
                    if (!needs.TryGetValue(dependency.PackageId, out var ranges)) needs[dependency.PackageId] = ranges = [];
                    ranges.Add(dependency.VersionRange);
                }
                var result = Search(copy, needs);
                if (result is not null) return result;
                if (steps > MaximumSearchSteps) break;
            }
            return null;
        }
        var resolved = Search(new(StringComparer.Ordinal), constraints);
        if (resolved is null) { findings.Add(lastFailure ?? new("VersionConflict", "", null, "Conflicting dependency requirements.")); return Failed(); }
        var selectedSet = resolved.Values.OrderBy(candidate => candidate.Id, StringComparer.Ordinal).ToArray();
        var changes = selectedSet.Select(candidate =>
        {
            var old = current.FirstOrDefault(item => item.Id == candidate.Id);
            var action = old is null ? "install" : old.ArtifactHash == candidate.ArtifactHash ? "unchanged" : CandidateVersion(candidate).CompareTo(CandidateVersion(old)) < 0 ? "downgrade" : "update";
            return new PackageChange(candidate.Id, old?.Version, candidate.Version, action);
        }).Concat(current.Where(candidate => !resolved.ContainsKey(candidate.Id)).Select(candidate => new PackageChange(candidate.Id, candidate.Version, candidate.Version, "uninstall"))).ToArray();
        return new(current, selectedSet, changes, TopologicalOrder(selectedSet), TopologicalOrder(current).Reverse().ToArray(), []);
    }

    public static IReadOnlyList<string> TopologicalOrder(IReadOnlyList<PackageCandidate> packages)
    {
        var byId = packages.ToDictionary(candidate => candidate.Id, StringComparer.Ordinal);
        var state = new Dictionary<string, int>(StringComparer.Ordinal);
        var order = new List<string>();
        void Visit(string id, int depth)
        {
            if (depth > MaximumDepth) throw new InvalidDataException("Package dependency graph depth exceeded.");
            if (state.TryGetValue(id, out var value)) { if (value == 1) throw new InvalidDataException("Package dependency cycle."); return; }
            if (!byId.TryGetValue(id, out var package)) throw new InvalidDataException("Unresolved dependency.");
            state[id] = 1;
            foreach (var dependency in Dependencies(package.Manifest).OrderBy(item => item.PackageId, StringComparer.Ordinal)) Visit(dependency.PackageId, depth + 1);
            state[id] = 2; order.Add(id);
        }
        foreach (var id in byId.Keys.Order(StringComparer.Ordinal)) Visit(id, 0);
        return order;
    }
}
